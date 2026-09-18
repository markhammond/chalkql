using System.Buffers;
using System.Data.Common;
using System.Globalization;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Catalog;
using Chalk.Ir;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Sources.Ado;

/// <summary>
/// A <see cref="DbDataReader"/> turned into Arrow batches, column by column, through the execution's
/// arena (§3).
/// </summary>
/// <remarks>
/// <para>
/// The shape is the POCO source's: staging rented once for the batch and reused for every batch,
/// validity collected a byte per row and packed once, and the Arrow buffers built through
/// <see cref="ExecutionArena.CreateBuffer"/> so they are the execution's memory rather than the
/// heap's.
/// </para>
/// <para>
/// Two paths read a value. The <b>typed</b> path asks the provider for the CLR type of the column
/// once, at the first non-NULL value, and from then on reads every cell through the typed getter
/// that type names — <c>GetInt64</c>, <c>GetDouble</c>, <c>GetDecimal</c>, <c>GetDateTime</c> —
/// straight into arena-rented staging of the Chalk column's width, so a numeric, temporal or UUID
/// cell allocates nothing. A string cell still costs the provider's string; nothing here can
/// change that. The <b>boxed</b> path is the original one: <c>GetValue</c>, a contract check on
/// the first value, and a conversion table at build time. It serves a provider whose values are
/// typed per row rather than per column (<see cref="DialectProfileDescriptor.DynamicallyTyped"/>,
/// SQLite), any CLR type the typed table does not name, and any provider type the catalog's
/// declared type cannot hold — which is where the contract error of the original design is raised,
/// unchanged.
/// </para>
/// <para>
/// Every value is checked against the column type Chalk promised, once per column per scan: the
/// provider's reported field type decides on the typed path, the first non-NULL value on the boxed
/// one, because a provider that returns one wrong type returns it for the whole column. A mismatch
/// is a <see cref="SourceContractException"/> naming the column, the type the catalog declared and
/// the type the provider produced — which is the message a host needs when SQLite's dynamic typing
/// has let a string into an integer column.
/// </para>
/// </remarks>
internal sealed class AdoBatchReader : IDisposable
{
    private readonly string _sourceId;
    private readonly string _subject;
    private readonly ArrowSchema _schema;
    private readonly ChalkType[] _types;
    private readonly ExecutionArena _arena;
    private readonly int _capacity;
    private readonly bool _dynamic;
    private readonly ColumnStaging[] _columns;
    private bool _disposed;
    private bool _namesChecked;
    private bool _nullCheckIsSynchronous;

    public AdoBatchReader(
        string sourceId,
        string subject,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        ExecutionArena arena,
        int capacity,
        DialectProfileDescriptor? profile = null,
        AdoProviderTraits? traits = null)
    {
        _sourceId = sourceId;
        _subject = subject;
        _schema = schema;
        _types = [.. types];
        _arena = arena;
        _capacity = capacity;
        _dynamic = profile?.DynamicallyTyped ?? false;
        _columns = new ColumnStaging[_types.Length];
        for (var i = 0; i < _types.Length; i++)
        {
            _columns[i] = new ColumnStaging(
                arena, capacity, _types[i], schema.FieldsList[i].DataType, schema.FieldsList[i].Name,
                traits?.Text);
        }
    }

    /// <summary>
    /// Reads up to <see cref="_capacity"/> rows into one Arrow batch, or null at end of stream. The
    /// batch owns its buffers and the caller disposes it.
    /// </summary>
    public async Task<RecordBatch?> ReadBatchAsync(DbDataReader reader, CancellationToken ct)
    {
        if (!_namesChecked)
        {
            _namesChecked = true;
            CheckColumnNames(reader);
            _nullCheckIsSynchronous = NullCheckIsSynchronous(reader.GetType());
        }

        foreach (var column in _columns)
        {
            column.BeginBatch();
        }

        var rows = 0;
        while (rows < _capacity && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            for (var c = 0; c < _types.Length; c++)
            {
                var column = _columns[c];
                var isNull = _nullCheckIsSynchronous
                    ? reader.IsDBNull(c)
                    : await reader.IsDBNullAsync(c, ct).ConfigureAwait(false);
                if (isNull)
                {
                    column.SetNull(rows);
                    continue;
                }

                if (column.Path == ReadPath.Undecided)
                {
                    column.Path = TypedReads.Decide(reader, c, _types[c].Kind, _dynamic);
                }
                else if (_dynamic && column.Path != ReadPath.Boxed)
                {
                    // V42: this provider's reported field type is the *current row's* storage class,
                    // not the column's, so the check that the boxed path did per value is done here
                    // per value too. A row whose storage class the declared type cannot hold is the
                    // same contract error, with the same message.
                    var decided = TypedReads.Decide(reader, c, _types[c].Kind, _dynamic);
                    if (decided == ReadPath.Boxed)
                    {
                        throw Mismatch(reader, c, reader.GetValue(c));
                    }

                    column.Path = decided;
                }

                if (column.Path == ReadPath.Boxed)
                {
                    var value = reader.GetValue(c);
                    column.SetBoxed(rows, Check(reader, c, value));
                }
                else
                {
                    column.ReadTyped(reader, c, rows);
                }
            }

            rows++;
        }

        if (rows == 0)
        {
            return null;
        }

        var arrays = new IArrowArray[_types.Length];
        for (var c = 0; c < _types.Length; c++)
        {
            arrays[c] = _columns[c].Build(_arena, rows);
        }

        return new RecordBatch(_schema, arrays, rows);
    }

    /// <summary>
    /// The columns the driver reports, against the columns the plan expects, once per result set
    /// (F35). Every read here is by position, so a result set whose columns are not the ones the
    /// plan named would be mapped silently onto the wrong fields — which is exactly what a schema
    /// that drifted after registration, or a hand-written descriptor, produces. Naming the mismatch
    /// is the cheapest way to make that a failure instead of wrong numbers.
    /// </summary>
    /// <remarks>
    /// The comparison is case-insensitive: a source that folds unquoted identifiers, or reports them
    /// in its own case, has not swapped anything. A driver that reports no name for a column — some
    /// do for a computed one — is not evidence either way and is skipped.
    /// </remarks>
    private void CheckColumnNames(DbDataReader reader)
    {
        if (reader.FieldCount != _types.Length)
        {
            throw new SourceContractException(
                _sourceId,
                _subject,
                $"the result set has {reader.FieldCount} columns and the plan expects "
                + $"{_types.Length} ({string.Join(", ", _schema.FieldsList.Select(f => f.Name))})");
        }

        for (var c = 0; c < _types.Length; c++)
        {
            var reported = reader.GetName(c);
            if (string.IsNullOrEmpty(reported))
            {
                continue;
            }

            var expected = _schema.FieldsList[c].Name;
            if (!string.Equals(reported, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new SourceContractException(
                    _sourceId,
                    _subject,
                    $"the result set names column {c} '{reported}' and the plan expects "
                    + $"'{expected}'; reading it by position would map the source's columns onto the "
                    + "wrong fields");
            }
        }
    }

    /// <summary>
    /// The value, having confirmed it is one this column can hold. A provider type that does not
    /// map to the declared one is a contract error naming both (§3) — never a silent conversion,
    /// because a silent conversion is how a string ends up compared as a number.
    /// </summary>
    private object Check(DbDataReader reader, int column, object value)
    {
        var type = _types[column];
        if (_columns[column].Checked || AdoTypeMapping.IsCompatible(value, type))
        {
            _columns[column].Checked = true;
            return value;
        }

        throw Mismatch(reader, column, value);
    }

    /// <summary>
    /// The contract error, in one place so the boxed path and the per-row typed check of V42 raise
    /// exactly the same message.
    /// </summary>
    private SourceContractException Mismatch(DbDataReader reader, int column, object value) =>
        new(
            _sourceId,
            _subject,
            $"column '{_schema.FieldsList[column].Name}' is declared {_types[column]} in the catalog, "
            + $"but the provider produced a {value.GetType().Name} (its reported field type is "
            + $"{AdoTypeMapping.Describe(reader, column)}). Correct the declared type, or cast the "
            + "column in the source.");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var column in _columns)
        {
            column.Release(_arena);
        }
    }

    /// <summary>
    /// Whether this reader's type may be asked how long a TEXT value is at all (V55, ADR 0024).
    /// One may not, on either accessor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Microsoft.Data.Sqlite</c>'s streaming accessors <b>crash the process</b> — SIGSEGV inside
    /// <c>libe_sqlite3</c>, not an exception — whenever the result set holds two or more
    /// <em>computed</em> TEXT columns. Measured on the repository's own pin,
    /// Microsoft.Data.Sqlite 10.0.1 over SQLitePCLRaw.lib.e_sqlite3 3.53.3, and reproduced on
    /// 3.49.1 and on 10.0.12 over 3.53.4 (ADR 0023's addendum), so it is the driver's and not the
    /// SQLite release's: over
    /// <c>SELECT a || '-' || b, a || '!' FROM t</c>, the first length query dies — through
    /// <c>GetBytes</c> and through <c>GetChars</c> alike, whichever of the two columns is asked,
    /// under either <c>CommandBehavior</c>. One computed column is fine, and so is one computed
    /// beside one plain; it takes two. A view over two expressions is enough to reach it — no
    /// pushdown required — and it is how the §5 D pushdown query found it.
    /// </para>
    /// <para>
    /// V44 made the strategy a measurement rather than a declaration on purpose, and it was right
    /// to: a provider's cost is not something a manual can be trusted for. But a measurement
    /// presumes the instrument survives the reading, and here it does not, so the one provider
    /// that cannot be measured is named and takes <see cref="TextStrategy.String"/> unasked.
    /// Everything else keeps the measured path, another SQLite driver included, and naming this one
    /// costs nothing: it materialises the value to answer a length query (V40, V44), so it was
    /// never going to earn a streaming path anyway.
    /// </para>
    /// <para>
    /// D263 (ADR 0046 §2) removed the reader's other provider table — the one naming which
    /// <em>strategy</em> a known provider costs least, now a declared <see cref="AdoProviderTraits"/>
    /// or a per-process measurement — but left this one alone. The two answer different questions:
    /// that table says what a provider costs, which a host or a vendor package may reasonably know
    /// and declare; this one says whether the provider survives being asked at all, which is not a
    /// cost a declaration could stand in for and not a fact reflection over the type can derive —
    /// nothing about <c>SqliteDataReader</c>'s shape says it crashes, only V55's measurement does.
    /// A trait a host declares wrongly costs a wrong number; being wrong here costs the process, so
    /// it stays a name, and it is the only one <c>Chalk.Sources.Ado</c> keeps.
    /// </para>
    /// </remarks>
    internal static bool CanBeProbed(System.Type reader) =>
        reader.FullName != "Microsoft.Data.Sqlite.SqliteDataReader";

    /// <summary>
    /// One reflection answer per reader type for the whole process (D263, ADR 0046 §1): the type
    /// itself, not its name, is now the only thing this asks.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Type, bool>
        NullCheckIsSynchronousByType = new();

    /// <summary>
    /// Whether a provider answers <c>IsDBNull</c> without I/O once <c>ReadAsync</c> has returned the
    /// row, so the synchronous call is the right one — decided once per reader type by reflection
    /// (D263, ADR 0046 §1) rather than by a name table: true when
    /// <c>IsDBNullAsync(int, CancellationToken)</c> resolves to <see cref="DbDataReader"/>'s own
    /// declaration, which merely wraps <c>IsDBNull</c> in a completed task, so the row is already in
    /// memory by the time it is asked and the synchronous call costs nothing extra; false when the
    /// provider overrides it, which is the provider saying it has I/O of its own to do there — a
    /// networked provider under <c>SequentialAccess</c> may still have the column on the wire, and
    /// the asynchronous call is what keeps a thread from blocking on a socket. The asynchronous call
    /// costs a state machine per cell, which is why this is asked once per reader rather than
    /// assumed, and memoised so the reflection itself is paid once per reader <em>type</em> for the
    /// life of the process.
    /// </summary>
    internal static bool NullCheckIsSynchronous(System.Type reader) =>
        NullCheckIsSynchronousByType.GetOrAdd(reader, static type =>
        {
            var method = type.GetMethod(
                nameof(DbDataReader.IsDBNullAsync), [typeof(int), typeof(CancellationToken)]);
            return method?.DeclaringType == typeof(DbDataReader);
        });

    /// <summary>
    /// One measured answer per provider type and data type name for the whole process: the first
    /// scan of a provider pays the probe, every later column of that provider and type reads it.
    /// Nothing is added here for a column whose trait was declared (D263, ADR 0046 §2): a declared
    /// answer is used, never measured, so it never occupies this cache.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(System.Type Reader, string DataType), TextStrategy>
        MeasuredStrategies = new();

    /// <summary>
    /// How many times <see cref="Measured"/> actually ran a length query, for the whole process
    /// (D263). Zero for a reader whose provider declared its <see cref="AdoProviderTraits.Text"/> or
    /// whose type <see cref="CanBeProbed"/> refuses outright — both skip this entirely — which is
    /// what lets a test prove a declaration was honoured rather than merely agreed with by chance.
    /// </summary>
    private static long _measurementRuns;

    /// <summary>See <see cref="_measurementRuns"/>.</summary>
    internal static long MeasurementRuns => System.Threading.Volatile.Read(ref _measurementRuns);

    /// <summary>Drops every measured answer, for a test whose provider double plays more than one part.</summary>
    internal static void ForgetMeasuredStrategies()
    {
        MeasuredStrategies.Clear();
        System.Threading.Volatile.Write(ref _measurementRuns, 0);
    }

    /// <summary>
    /// What <see cref="ColumnStaging"/> would decide for one column right now, a declared trait
    /// aside — the exact probe a live scan runs, over the exact cache a live scan shares — for the
    /// conformance kit's report (D263, ADR 0046 §3): "the probe already runs when a reader is
    /// opened" is <see cref="MeasuredStrategies"/> already holding the answer from an earlier scan,
    /// and this is "or runs the probe itself" for a column no scan has reached yet. Safe to call
    /// against a reader mid-result-set; it is not a special path, it is the path.
    /// </summary>
    internal static TextStrategy ProbeTextForReport(DbDataReader reader, int column) =>
        ColumnStaging.ProbeText(reader, column);

    /// <summary>
    /// One column's staging: a byte of validity per row, and the values either in arena-rented
    /// storage of the column's own width (the typed path), as strings, or boxed (the original path).
    /// </summary>

    private sealed class ColumnStaging
    {
        private readonly ExecutionArena _arena;
        private readonly ChalkType _type;
        private readonly TypeKind _kind;
        private readonly IArrowType _arrowType;
        private readonly string _name;
        private readonly int _capacity;
        private readonly int _width;
        private readonly long _unitMultiplier;
        private readonly long _unitDivisor;
        private byte[] _valid;
        private byte[] _fixed;
        private string?[]? _strings;
        private object?[]? _boxed;
        private byte[] _text = [];
        private int[] _textOffsets = [];
        private char[]? _chars;
        private int _textUsed;
        private readonly TextStrategy? _declaredText;

        public ColumnStaging(
            ExecutionArena arena, int capacity, ChalkType type, IArrowType arrowType, string name,
            TextStrategy? declaredText = null)
        {
            _arena = arena;
            _type = type;
            _kind = type.Kind;
            _arrowType = arrowType;
            _name = name;
            _capacity = capacity;
            _width = TypedReads.WidthOf(type.Kind);
            _valid = arena.Rent<byte>(capacity);
            _fixed = _width == 0 ? [] : arena.Rent<byte>(capacity * _width);
            (_unitMultiplier, _unitDivisor) = TypedReads.UnitsOf(type.Kind, arrowType);
            _declaredText = declaredText;
        }

        /// <summary>How this column is read, decided at its first non-NULL value.</summary>
        public ReadPath Path { get; set; } = ReadPath.Undecided;

        /// <summary>
        /// How a TEXT value reaches the arena (D149, §7; D263, ADR 0046 §2), decided at its first
        /// non-NULL value from <see cref="_declaredText"/> when the source declared one, or else
        /// probed. <see cref="TextStrategy.Undecided"/> until then.
        /// </summary>
        public TextStrategy Text { get; private set; } = TextStrategy.Undecided;

        /// <summary>Resets the per-batch text staging. Rows arrive in order, so offsets append.</summary>
        public void BeginBatch()
        {
            _textUsed = 0;
            if (_textOffsets.Length > 0)
            {
                _textOffsets[0] = 0;
            }
        }

        /// <summary>Whether this column's provider type has already been confirmed for this scan.</summary>
        public bool Checked { get; set; }

        public void SetBoxed(int row, object value)
        {
            (_boxed ??= ArrayPool<object?>.Shared.Rent(_capacity))[row] = value;
            _valid[row] = 1;
        }

        public void SetNull(int row)
        {
            _valid[row] = 0;
            if (_width != 0)
            {
                _fixed.AsSpan(row * _width, _width).Clear();
            }
            else if (_strings is { } strings)
            {
                strings[row] = null;
            }

            if (_textOffsets.Length > row + 1)
            {
                // A NULL row still needs an offset, and it is the one the previous row left.
                _textOffsets[row + 1] = _textUsed;
            }

            if (_boxed is { } boxed)
            {
                boxed[row] = null;
            }
        }

        /// <summary>
        /// Reads one non-NULL cell through the typed getter <see cref="Path"/> names, into staging of
        /// the Chalk column's width. Nothing here allocates except a string cell's string.
        /// </summary>
        public void ReadTyped(DbDataReader reader, int c, int row)
        {
            switch (_kind)
            {
                case TypeKind.I64:
                    Fixed<long>()[row] = ReadInt64(reader, c);
                    break;
                case TypeKind.I32:
                    Fixed<int>()[row] = checked((int)ReadInt64(reader, c));
                    break;
                case TypeKind.I16:
                    Fixed<short>()[row] = checked((short)ReadInt64(reader, c));
                    break;
                case TypeKind.I8:
                    Fixed<sbyte>()[row] = checked((sbyte)ReadInt64(reader, c));
                    break;
                case TypeKind.Fp64:
                    Fixed<double>()[row] = ReadDouble(reader, c);
                    break;
                case TypeKind.Fp32:
                    Fixed<float>()[row] = ReadSingle(reader, c);
                    break;
                case TypeKind.Decimal:
                    Fixed<decimal>()[row] = ReadDecimal(reader, c);
                    break;
                case TypeKind.Bool:
                    _fixed[row] = ReadBool(reader, c) ? (byte)1 : (byte)0;
                    break;
                case TypeKind.Date:
                    Fixed<int>()[row] = ReadDays(reader, c);
                    break;
                case TypeKind.Timestamp or TypeKind.TimestampTz:
                    Fixed<long>()[row] = Units(ReadTicksSinceEpoch(reader, c));
                    break;
                case TypeKind.Time:
                    Fixed<long>()[row] = Units(ReadTimeTicks(reader, c));
                    break;
                case TypeKind.Uuid:
                    Fixed<Guid>()[row] = reader.GetGuid(c);
                    break;
                case TypeKind.String:
                    ReadText(reader, c, row);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"the typed read path has no case for {_kind}; TypedReads.Decide should not "
                        + "have chosen it.");
            }

            _valid[row] = 1;
        }

        public void Release(ExecutionArena arena)
        {
            arena.Return(_valid);
            _valid = [];
            arena.Return(_fixed);
            _fixed = [];
            if (_strings is { } strings)
            {
                ArrayPool<string?>.Shared.Return(strings, clearArray: true);
                _strings = null;
            }

            arena.Return(_text);
            _text = [];
            arena.Return(_textOffsets);
            _textOffsets = [];
            if (_chars is { } chars)
            {
                ArrayPool<char>.Shared.Return(chars);
                _chars = null;
            }

            if (_boxed is { } boxed)
            {
                ArrayPool<object?>.Shared.Return(boxed, clearArray: true);
                _boxed = null;
            }
        }

        /// <summary>The Arrow array for the first <paramref name="rows"/> staged values.</summary>
        public IArrowArray Build(ExecutionArena arena, int rows)
        {
            var (validity, nulls) = BuildValidity(arena, rows);
            switch (Path)
            {
                case ReadPath.Undecided:
                case ReadPath.Boxed:
                {
                    // Undecided with rows means every value so far was NULL: the boxed builder reads
                    // an all-null staging array and produces the right all-null column.
                    var boxed = _boxed ??= ArrayPool<object?>.Shared.Rent(_capacity);
                    var array = AdoArrays.Build(arena, _type, _arrowType, boxed, rows, validity, nulls, _name);
                    System.Array.Clear(boxed, 0, rows);
                    return array;
                }

                case ReadPath.String when Text is TextStrategy.Bytes or TextStrategy.Chars:
                {
                    // The bytes are already in the arena, with their offsets beside them: the Arrow
                    // array is those two buffers and nothing else (D149).
                    var offsets = arena.CreateBuffer(
                        MemoryMarshal.AsBytes(_textOffsets.AsSpan(0, rows + 1)));
                    return new StringArray(
                        rows, offsets, arena.CreateBuffer(_text.AsSpan(0, _textUsed)),
                        validity, nulls, 0);
                }

                case ReadPath.String:
                {
                    var strings = _strings ??= ArrayPool<string?>.Shared.Rent(_capacity);
                    var array = AdoArrays.String(arena, strings, rows, validity, nulls);
                    System.Array.Clear(strings, 0, rows);
                    return array;
                }

                default:
                    return TypedArray(arena, rows, validity, nulls);
            }
        }

        private IArrowArray TypedArray(ExecutionArena arena, int rows, ArrowBuffer validity, int nulls)
        {
            switch (_kind)
            {
                case TypeKind.Bool:
                {
                    var bytes = (rows + 7) / 8;
                    var packed = arena.Rent<byte>(bytes);
                    try
                    {
                        var span = packed.AsSpan(0, bytes);
                        span.Clear();
                        for (var i = 0; i < rows; i++)
                        {
                            if (_valid[i] != 0 && _fixed[i] != 0)
                            {
                                BitUtility.SetBit(span, i);
                            }
                        }

                        return new BooleanArray(arena.CreateBuffer(span), validity, rows, nulls, 0);
                    }
                    finally
                    {
                        arena.Return(packed);
                    }
                }

                case TypeKind.Decimal:
                {
                    const int Width = 16;
                    var type = (Decimal128Type)_arrowType;
                    var lanes = arena.Rent<byte>(rows * Width);
                    try
                    {
                        var span = lanes.AsSpan(0, rows * Width);
                        span.Clear();
                        var values = Fixed<decimal>();
                        for (var i = 0; i < rows; i++)
                        {
                            if (_valid[i] != 0)
                            {
                                SourceEncoding.WriteDecimal(
                                    values[i], type.Precision, type.Scale, span.Slice(i * Width, Width),
                                    "ado", "a pushed query", _name);
                            }
                        }

                        return new Decimal128Array(
                            new ArrayData(type, rows, nulls, 0, [validity, arena.CreateBuffer(span)]));
                    }
                    finally
                    {
                        arena.Return(lanes);
                    }
                }

                case TypeKind.Uuid:
                {
                    var type = (FixedSizeBinaryType)_arrowType;
                    var width = type.ByteWidth;
                    var lanes = arena.Rent<byte>(rows * width);
                    try
                    {
                        var span = lanes.AsSpan(0, rows * width);
                        span.Clear();
                        var values = Fixed<Guid>();
                        for (var i = 0; i < rows; i++)
                        {
                            if (_valid[i] != 0)
                            {
                                SourceEncoding.WriteUuid(values[i], span.Slice(i * width, width));
                            }
                        }

                        return new Apache.Arrow.Arrays.FixedSizeBinaryArray(
                            new ArrayData(type, rows, nulls, 0, [validity, arena.CreateBuffer(span)]));
                    }
                    finally
                    {
                        arena.Return(lanes);
                    }
                }

                default:
                {
                    var buffer = arena.CreateBuffer(_fixed.AsSpan(0, rows * _width));
                    return _kind switch
                    {
                        TypeKind.I8 => new Int8Array(buffer, validity, rows, nulls, 0),
                        TypeKind.I16 => new Int16Array(buffer, validity, rows, nulls, 0),
                        TypeKind.I32 => new Int32Array(buffer, validity, rows, nulls, 0),
                        TypeKind.I64 => new Int64Array(buffer, validity, rows, nulls, 0),
                        TypeKind.Fp32 => new FloatArray(buffer, validity, rows, nulls, 0),
                        TypeKind.Fp64 => new DoubleArray(buffer, validity, rows, nulls, 0),
                        TypeKind.Date => new Date32Array(buffer, validity, rows, nulls, 0),
                        TypeKind.Time => new Time64Array(
                            new ArrayData((Time64Type)_arrowType, rows, nulls, 0, [validity, buffer])),
                        TypeKind.Timestamp or TypeKind.TimestampTz => new TimestampArray(
                            new ArrayData((TimestampType)_arrowType, rows, nulls, 0, [validity, buffer])),
                        _ => throw new InvalidOperationException($"no typed array for {_kind}"),
                    };
                }
            }
        }

        /// <summary>
        /// One TEXT value into the arena, by the strategy this column chose at its first non-NULL
        /// value (D149, §7).
        /// </summary>
        /// <remarks>
        /// <b>Bytes</b> is the one that costs nothing: a provider that hands a TEXT value's UTF-8
        /// over through <c>GetBytes</c> — <c>Microsoft.Data.Sqlite</c> and Npgsql both do (V40, V41)
        /// — fills the arena buffer directly. <b>Chars</b> streams characters into a pooled buffer
        /// and transcodes, for a provider that has <c>GetChars</c> and not <c>GetBytes</c>.
        /// <b>String</b> is the original path, and the only one that makes a .NET string.
        /// </remarks>
        private void ReadText(DbDataReader reader, int c, int row)
        {
            if (Text == TextStrategy.Undecided)
            {
                // D263 (ADR 0046 §2): a declared trait is used without a probe; the lookup is the
                // constructor argument this instance already carries, so nothing is asked here at
                // all. An undeclared column falls to the same per-process probe as before.
                Text = _declaredText ?? ProbeText(reader, c);
            }

            if (Text == TextStrategy.String)
            {
                (_strings ??= ArrayPool<string?>.Shared.Rent(_capacity))[row] = reader.GetString(c);
                return;
            }

            EnsureTextStaging(row);
            if (Text == TextStrategy.Bytes)
            {
                var length = (int)reader.GetBytes(c, 0, null, 0, 0);
                GrowText(length);
                var got = length == 0
                    ? 0
                    : (int)reader.GetBytes(c, 0, _text, _textUsed, length);
                _textUsed += got;
            }
            else
            {
                var length = (int)reader.GetChars(c, 0, null, 0, 0);
                var chars = _chars;
                if (chars is null || chars.Length < length)
                {
                    if (chars is not null)
                    {
                        ArrayPool<char>.Shared.Return(chars);
                    }

                    chars = _chars = ArrayPool<char>.Shared.Rent(Math.Max(64, length));
                }

                var got = length == 0 ? 0 : (int)reader.GetChars(c, 0, chars, 0, length);
                GrowText(System.Text.Encoding.UTF8.GetMaxByteCount(got));
                _textUsed += System.Text.Encoding.UTF8.GetBytes(
                    chars.AsSpan(0, got), _text.AsSpan(_textUsed));
            }

            _textOffsets[row + 1] = _textUsed;
        }

        /// <summary>
        /// Which strategy this provider serves this column best, asked once with a length query so
        /// that nothing is consumed when the answer is no.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two questions, and the second is the one the design did not think to ask. <b>Can</b> the
        /// provider hand a TEXT value's UTF-8 over through <c>GetBytes</c>? Both providers Chalk
        /// ships tests against can (V40, V41). <b>Does it cost anything?</b> Npgsql answers out of
        /// the buffer it already holds; <c>Microsoft.Data.Sqlite</c> materialises the whole value to
        /// answer <em>how long it is</em>, so taking that path there costs about thirty times what
        /// <c>GetString</c> costs (V44). Capability is not the question — cost is.
        /// </para>
        /// <para>
        /// So the probe measures. One length query, with the thread's allocation counter either
        /// side: a provider that answers for nothing is handing over bytes it already has, and one
        /// that allocates to answer will allocate to give them to you. The measurement is one
        /// thread-local read per column per scan, and being wrong costs speed rather than
        /// correctness — <em>on a provider that survives being asked</em>. See
        /// <see cref="CanBeProbed"/> for the one that does not.
        /// </para>
        /// <para>
        /// Called only for a column whose source declared nothing (D263, ADR 0046 §2): a declared
        /// <see cref="AdoProviderTraits.Text"/> is used by <see cref="ReadText"/> directly and never
        /// reaches here, so <see cref="MeasuredStrategies"/> holds no entry for it either.
        /// </para>
        /// </remarks>
        internal static TextStrategy ProbeText(DbDataReader reader, int c)
        {
            var type = reader.GetType();
            if (!CanBeProbed(type))
            {
                return TextStrategy.String;
            }

            // Cached per provider and data type: a provider answers the same way for every column
            // of one type, so the measurement — and any exception it costs — happens once per
            // process rather than once per column per scan.
            return MeasuredStrategies.GetOrAdd((type, reader.GetDataTypeName(c)), _ => Measured(reader, c));
        }

        private static TextStrategy Measured(DbDataReader reader, int c)
        {
            System.Threading.Interlocked.Increment(ref _measurementRuns);
            if (Measure(() => reader.GetBytes(c, 0, null, 0, 0)) is { } bytes)
            {
                return bytes <= ProbeBudget ? TextStrategy.Bytes : TextStrategy.String;
            }

            if (Measure(() => reader.GetChars(c, 0, null, 0, 0)) is { } chars)
            {
                return chars <= ProbeBudget ? TextStrategy.Chars : TextStrategy.String;
            }

            return TextStrategy.String;
        }
        /// <summary>
        /// What one length query allocated, or null when the provider refused it. A provider
        /// answering out of its own buffer lands at <see cref="ProbeBudget"/> — zero — and one that
        /// materialises the value to answer does not.
        /// </summary>
        private static long? Measure(Func<long> query)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                _ = query();
            }
            catch (Exception failure) when (Refused(failure))
            {
                return null;
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        /// <summary>
        /// What a length query may allocate and still count as free — which is nothing at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// V44 measured the two providers at zero (Npgsql, answering out of the buffer it already
        /// holds) and about 1 400 bytes (<c>Microsoft.Data.Sqlite</c>, materialising the value to
        /// answer how long it is) and set the budget generously between them, at 128. <b>V55</b> is
        /// why "generous" was the wrong shape: what a SQLite length query allocates depends on the
        /// <em>value</em>, and for a computed column — <c>a || '-' || b</c>, or any expression in a
        /// view — it lands on exactly 128, inside the budget. One column of a scan then took the
        /// <c>Bytes</c> path while its neighbours took <c>String</c>, which is not a decision
        /// anything could have predicted from the provider.
        /// </para>
        /// <para>
        /// And on that provider the <c>Bytes</c> path is not merely slow. Two computed TEXT columns
        /// read with a length query and a fill, row by row, <b>segfault</b> the process in
        /// <c>libe_sqlite3</c> (measured on Microsoft.Data.Sqlite 10.0.1 over
        /// SQLitePCLRaw.lib.e_sqlite3 3.53.3, under either <c>CommandBehavior</c>; two plain columns
        /// are fine, and so
        /// is one plain beside one computed). Chalk cannot fix the provider, but it can stop
        /// choosing the path that reaches it — and the measurement V44 actually wanted was
        /// "answered for nothing", which is zero and not a threshold. A provider that allocates to
        /// answer will allocate to give you the bytes, so this loses no case worth having.
        /// </para>
        /// </remarks>
        private const long ProbeBudget = 0;

        /// <summary>
        /// The shapes "this provider does not do that" arrives in. Anything else is a real failure
        /// and travels out to be attributed.
        /// </summary>
        private static bool Refused(Exception failure) =>
            failure is InvalidCastException
                or NotSupportedException
                or NotImplementedException
                or InvalidOperationException;

        private void EnsureTextStaging(int row)
        {
            if (_textOffsets.Length < _capacity + 1)
            {
                var offsets = _arena.Rent<int>(_capacity + 1);
                _arena.Return(_textOffsets);
                _textOffsets = offsets;

                // Every row before this one was NULL, and its offset is where this one starts.
                _textOffsets.AsSpan(0, row + 1).Clear();
            }

            if (_text.Length == 0)
            {
                _text = _arena.Rent<byte>(Math.Max(64, _capacity * 8));
            }
        }

        private void GrowText(int extra)
        {
            var needed = _textUsed + extra;
            if (_text.Length >= needed)
            {
                return;
            }

            var grown = _arena.Rent<byte>(Math.Max(needed, _text.Length * 2));
            _text.AsSpan(0, _textUsed).CopyTo(grown);
            _arena.Return(_text);
            _text = grown;
        }

        private Span<T> Fixed<T>()
            where T : struct
            => MemoryMarshal.Cast<byte, T>(_fixed.AsSpan(0, _capacity * _width));

        private long Units(long ticks) => ticks * _unitMultiplier / _unitDivisor;

        private long ReadInt64(DbDataReader reader, int c) => Path switch
        {
            ReadPath.Int64 => reader.GetInt64(c),
            ReadPath.Int32 => reader.GetInt32(c),
            ReadPath.Int16 => reader.GetInt16(c),
            ReadPath.Byte => reader.GetByte(c),
            _ => throw Unexpected(),
        };

        private double ReadDouble(DbDataReader reader, int c) => Path switch
        {
            ReadPath.Double => reader.GetDouble(c),
            ReadPath.Single => reader.GetFloat(c),
            ReadPath.Decimal => (double)reader.GetDecimal(c),
            ReadPath.Int64 or ReadPath.Int32 or ReadPath.Int16 or ReadPath.Byte =>
                ReadInt64(reader, c),
            _ => throw Unexpected(),
        };

        private float ReadSingle(DbDataReader reader, int c) => Path switch
        {
            ReadPath.Single => reader.GetFloat(c),
            ReadPath.Double => (float)reader.GetDouble(c),
            ReadPath.Decimal => (float)reader.GetDecimal(c),
            ReadPath.Int64 or ReadPath.Int32 or ReadPath.Int16 or ReadPath.Byte =>
                ReadInt64(reader, c),
            _ => throw Unexpected(),
        };

        private decimal ReadDecimal(DbDataReader reader, int c) => Path switch
        {
            ReadPath.Decimal => reader.GetDecimal(c),
            ReadPath.Double => (decimal)reader.GetDouble(c),
            ReadPath.Single => (decimal)reader.GetFloat(c),
            ReadPath.Int64 or ReadPath.Int32 or ReadPath.Int16 or ReadPath.Byte =>
                ReadInt64(reader, c),
            _ => throw Unexpected(),
        };

        private bool ReadBool(DbDataReader reader, int c) => Path switch
        {
            ReadPath.Boolean => reader.GetBoolean(c),
            ReadPath.Byte => reader.GetByte(c) != 0,
            ReadPath.Int16 => reader.GetInt16(c) != 0,
            ReadPath.Int32 => reader.GetInt32(c) != 0,
            ReadPath.Int64 => reader.GetInt64(c) != 0,
            _ => throw Unexpected(),
        };

        /// <summary>Days since the epoch — Arrow's DATE32, and what the executor's kernels read.</summary>
        private int ReadDays(DbDataReader reader, int c) => Path switch
        {
            ReadPath.DateTime => TypedReads.Days(reader.GetDateTime(c)),
            ReadPath.DateOnly => reader.GetFieldValue<DateOnly>(c).DayNumber - TypedReads.EpochDayNumber,
            ReadPath.DateTimeOffset => TypedReads.Days(reader.GetFieldValue<DateTimeOffset>(c).UtcDateTime),
            _ => throw Unexpected(),
        };

        private long ReadTicksSinceEpoch(DbDataReader reader, int c)
        {
            var utc = Path switch
            {
                ReadPath.DateTime => TypedReads.Utc(reader.GetDateTime(c)),
                ReadPath.DateTimeOffset => reader.GetFieldValue<DateTimeOffset>(c).UtcDateTime,
                ReadPath.DateOnly => reader.GetFieldValue<DateOnly>(c).ToDateTime(TimeOnly.MinValue),
                _ => throw Unexpected(),
            };
            return utc.Ticks - DateTime.UnixEpoch.Ticks;
        }

        private long ReadTimeTicks(DbDataReader reader, int c) => Path switch
        {
            ReadPath.TimeSpan => reader.GetFieldValue<TimeSpan>(c).Ticks,
            ReadPath.TimeOnly => reader.GetFieldValue<TimeOnly>(c).Ticks,
            ReadPath.DateTime => reader.GetDateTime(c).TimeOfDay.Ticks,
            _ => throw Unexpected(),
        };

        private InvalidOperationException Unexpected() =>
            new($"column '{_name}' is on the {Path} read path, which cannot produce a {_kind}.");

        private (ArrowBuffer Buffer, int NullCount) BuildValidity(ExecutionArena arena, int rows)
        {
            var bytes = (rows + 7) / 8;
            var bits = arena.Rent<byte>(bytes);
            try
            {
                var span = bits.AsSpan(0, bytes);
                span.Clear();
                var nulls = 0;
                for (var i = 0; i < rows; i++)
                {
                    if (_valid[i] != 0)
                    {
                        BitUtility.SetBit(span, i);
                    }
                    else
                    {
                        nulls++;
                    }
                }

                return nulls == 0 ? (ArrowBuffer.Empty, 0) : (arena.CreateBuffer(span), nulls);
            }
            finally
            {
                arena.Return(bits);
            }
        }
    }
}

/// <summary>
/// How a TEXT value reaches the arena (D149, <c>docs/design/24-zero-gc.md</c> §7). Decided once per
/// column, at its first non-NULL value: from <see cref="AdoProviderTraits.Text"/> when the source
/// declared one, or else by asking the provider (D263, ADR 0046 §2). Public so a host or a vendor
/// package can name the value it declares; <see cref="Undecided"/> is this type's own internal
/// starting state and is not a declaration <see cref="AdoSourceBuilder.Provider"/> accepts.
/// </summary>
public enum TextStrategy
{
    Undecided,

    /// <summary>The provider hands the value's UTF-8 over through <c>GetBytes</c> (V40, V41).</summary>
    Bytes,

    /// <summary>It streams characters through <c>GetChars</c>, and Chalk transcodes.</summary>
    Chars,

    /// <summary>It does neither, so the value arrives as a .NET string. The original path.</summary>
    String,
}

/// <summary>How a column's cells are read: not yet decided, boxed, or one typed getter.</summary>
internal enum ReadPath
{
    Undecided,
    Boxed,
    Int64,
    Int32,
    Int16,
    Byte,
    Double,
    Single,
    Decimal,
    Boolean,
    DateTime,
    DateOnly,
    DateTimeOffset,
    TimeOnly,
    TimeSpan,
    Guid,
    String,
}

/// <summary>
/// The typed read table: which provider CLR types have a typed getter, which Chalk kinds each may
/// feed (the same table as <see cref="AdoTypeMapping.IsCompatible"/>, restricted to those getters),
/// and the widths and unit conversions of the typed staging.
/// </summary>
internal static class TypedReads
{
    public static readonly int EpochDayNumber = DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;

    /// <summary>
    /// The read path for a column, from the CLR type the provider reports for it and the kind the
    /// catalog declared. Anything the typed table cannot serve, including a type the declared kind
    /// cannot hold, goes to the boxed path, which either converts it as before or raises the
    /// contract error.
    /// </summary>
    public static ReadPath Decide(DbDataReader reader, int column, TypeKind kind, bool dynamic)
    {
        if (dynamic && kind is TypeKind.Date or TypeKind.Time or TypeKind.Timestamp
            or TypeKind.TimestampTz or TypeKind.Uuid)
        {
            // V42's one exception. A dynamically-typed provider's temporal and UUID columns are the
            // ones whose storage class genuinely varies row to row — SQLite stores a timestamp as
            // text, as an integer or as a real, and the boxed builder is what parses each — so those
            // kinds stay boxed and every other kind leaves the boxed path.
            return ReadPath.Boxed;
        }

        System.Type field;
        try
        {
            field = reader.GetFieldType(column);
        }
        catch (NotSupportedException)
        {
            return ReadPath.Boxed;
        }

        var path =
            field == typeof(long) ? ReadPath.Int64
            : field == typeof(int) ? ReadPath.Int32
            : field == typeof(short) ? ReadPath.Int16
            : field == typeof(byte) ? ReadPath.Byte
            : field == typeof(double) ? ReadPath.Double
            : field == typeof(float) ? ReadPath.Single
            : field == typeof(decimal) ? ReadPath.Decimal
            : field == typeof(bool) ? ReadPath.Boolean
            : field == typeof(DateTime) ? ReadPath.DateTime
            : field == typeof(DateOnly) ? ReadPath.DateOnly
            : field == typeof(DateTimeOffset) ? ReadPath.DateTimeOffset
            : field == typeof(TimeOnly) ? ReadPath.TimeOnly
            : field == typeof(TimeSpan) ? ReadPath.TimeSpan
            : field == typeof(Guid) ? ReadPath.Guid
            : field == typeof(string) ? ReadPath.String
            : ReadPath.Boxed;
        return Accepts(kind, path) ? path : ReadPath.Boxed;
    }

    private static bool Accepts(TypeKind kind, ReadPath path) => kind switch
    {
        TypeKind.Bool => path is ReadPath.Boolean or ReadPath.Byte or ReadPath.Int16 or ReadPath.Int32 or ReadPath.Int64,
        TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.I64 =>
            path is ReadPath.Int64 or ReadPath.Int32 or ReadPath.Int16 or ReadPath.Byte,
        // The integer paths are here because a dynamically-typed provider may report one storage
        // class for one row of a column and another for the next (V42), and because a driver that
        // reports an integer field type for a numeric column should not fall back to boxing over it.
        // Every conversion is widening and exact.
        TypeKind.Fp32 or TypeKind.Fp64 =>
            path is ReadPath.Double or ReadPath.Single or ReadPath.Decimal
                or ReadPath.Int64 or ReadPath.Int32 or ReadPath.Int16 or ReadPath.Byte,
        TypeKind.Decimal =>
            path is ReadPath.Decimal or ReadPath.Double or ReadPath.Single
                or ReadPath.Int64 or ReadPath.Int32 or ReadPath.Int16 or ReadPath.Byte,
        TypeKind.String => path is ReadPath.String,
        TypeKind.Date => path is ReadPath.DateTime or ReadPath.DateOnly or ReadPath.DateTimeOffset,
        TypeKind.Timestamp => path is ReadPath.DateTime or ReadPath.DateTimeOffset or ReadPath.DateOnly,
        TypeKind.TimestampTz => path is ReadPath.DateTimeOffset or ReadPath.DateTime,
        TypeKind.Time => path is ReadPath.TimeSpan or ReadPath.TimeOnly or ReadPath.DateTime,
        TypeKind.Uuid => path is ReadPath.Guid,
        _ => false,
    };

    /// <summary>Bytes per staged value on the typed path; 0 for a kind staged as objects or strings.</summary>
    public static int WidthOf(TypeKind kind) => kind switch
    {
        TypeKind.Bool or TypeKind.I8 => 1,
        TypeKind.I16 => 2,
        TypeKind.I32 or TypeKind.Fp32 or TypeKind.Date => 4,
        TypeKind.I64 or TypeKind.Fp64 or TypeKind.Time or TypeKind.Timestamp or TypeKind.TimestampTz => 8,
        TypeKind.Decimal or TypeKind.Uuid => 16,
        _ => 0,
    };

    /// <summary>Ticks to the Arrow unit of a temporal column: multiply, then divide.</summary>
    public static (long Multiplier, long Divisor) UnitsOf(TypeKind kind, IArrowType arrowType)
    {
        var unit = kind switch
        {
            TypeKind.Time => ((Time64Type)arrowType).Unit,
            TypeKind.Timestamp or TypeKind.TimestampTz => ((TimestampType)arrowType).Unit,
            _ => (TimeUnit?)null,
        };
        return unit switch
        {
            null => (1, 1),
            TimeUnit.Nanosecond => (100, 1),
            TimeUnit.Microsecond => (1, 10),
            TimeUnit.Millisecond => (1, TimeSpan.TicksPerMillisecond),
            _ => (1, TimeSpan.TicksPerSecond),
        };
    }

    public static int Days(DateTime value) => (int)(value.Date - DateTime.UnixEpoch.Date).TotalDays;

    public static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
}

/// <summary>
/// Building one Arrow array from staged, boxed provider values. Split out so the conversions read as
/// a table: one branch per Chalk type kind, and every branch says how it reads what a driver might
/// have handed it.
/// </summary>
internal static class AdoArrays
{
    public static IArrowArray Build(
        ExecutionArena arena,
        ChalkType type,
        IArrowType arrowType,
        object?[] values,
        int rows,
        ArrowBuffer validity,
        int nullCount,
        string column)
    {
        return type.Kind switch
        {
            TypeKind.Bool => Boolean(arena, values, rows, validity, nullCount),
            TypeKind.I8 => Fixed<sbyte>(arena, values, rows, validity, nullCount, ToInt8,
                (v, n, c, k) => new Int8Array(v, n, c, k, 0)),
            TypeKind.I16 => Fixed<short>(arena, values, rows, validity, nullCount, ToInt16,
                (v, n, c, k) => new Int16Array(v, n, c, k, 0)),
            TypeKind.I32 => Fixed<int>(arena, values, rows, validity, nullCount, ToInt32,
                (v, n, c, k) => new Int32Array(v, n, c, k, 0)),
            TypeKind.I64 => Fixed<long>(arena, values, rows, validity, nullCount, ToInt64,
                (v, n, c, k) => new Int64Array(v, n, c, k, 0)),
            TypeKind.Fp32 => Fixed<float>(arena, values, rows, validity, nullCount, ToSingle,
                (v, n, c, k) => new FloatArray(v, n, c, k, 0)),
            TypeKind.Fp64 => Fixed<double>(arena, values, rows, validity, nullCount, ToDouble,
                (v, n, c, k) => new DoubleArray(v, n, c, k, 0)),
            TypeKind.Date => Fixed<int>(arena, values, rows, validity, nullCount, ToDays,
                (v, n, c, k) => new Date32Array(v, n, c, k, 0)),
            TypeKind.Time => Fixed<long>(
                arena, values, rows, validity, nullCount,
                value => ToTimeUnits(value, (Time64Type)arrowType),
                (v, n, c, k) => new Time64Array(
                    new ArrayData((Time64Type)arrowType, c, k, 0, [n, v]))),
            TypeKind.Timestamp or TypeKind.TimestampTz => Fixed<long>(
                arena, values, rows, validity, nullCount,
                value => ToTimestampUnits(value, (TimestampType)arrowType),
                (v, n, c, k) => new TimestampArray(
                    new ArrayData((TimestampType)arrowType, c, k, 0, [n, v]))),
            TypeKind.Decimal => Decimal(arena, values, rows, validity, nullCount, (Decimal128Type)arrowType, column),
            TypeKind.Uuid => Uuid(arena, values, rows, validity, nullCount, (FixedSizeBinaryType)arrowType),
            TypeKind.String => String(arena, values, rows, validity, nullCount),
            TypeKind.Binary => Binary(arena, values, rows, validity, nullCount),
            _ => throw new UnsupportedFeatureException(
                $"reading a {type.Kind} column from an ADO.NET provider",
                "docs/design/18-m4-capabilities-and-pushdown.md §3 lists the types the in-box "
                + "ADO.NET source reads; a LIST or a struct column is not one of them."),
        };
    }

    private delegate IArrowArray FixedFactory(ArrowBuffer values, ArrowBuffer nulls, int length, int nullCount);

    private static IArrowArray Fixed<T>(
        ExecutionArena arena,
        object?[] values,
        int rows,
        ArrowBuffer validity,
        int nullCount,
        Func<object, T> convert,
        FixedFactory create)
        where T : struct
    {
        var bytes = arena.Rent<byte>(rows * System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
        try
        {
            var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(
                bytes.AsSpan(0, rows * System.Runtime.CompilerServices.Unsafe.SizeOf<T>()));
            for (var i = 0; i < rows; i++)
            {
                span[i] = values[i] is { } value ? convert(value) : default;
            }

            var buffer = arena.CreateBuffer(
                bytes.AsSpan(0, rows * System.Runtime.CompilerServices.Unsafe.SizeOf<T>()));
            return create(buffer, validity, rows, nullCount);
        }
        finally
        {
            arena.Return(bytes);
        }
    }

    private static IArrowArray Boolean(
        ExecutionArena arena, object?[] values, int rows, ArrowBuffer validity, int nullCount)
    {
        var bytes = (rows + 7) / 8;
        var packed = arena.Rent<byte>(bytes);
        try
        {
            var span = packed.AsSpan(0, bytes);
            span.Clear();
            for (var i = 0; i < rows; i++)
            {
                if (values[i] is { } value && ToBool(value))
                {
                    BitUtility.SetBit(span, i);
                }
            }

            return new BooleanArray(arena.CreateBuffer(span), validity, rows, nullCount, 0);
        }
        finally
        {
            arena.Return(packed);
        }
    }

    internal static IArrowArray String(
        ExecutionArena arena, object?[] values, int rows, ArrowBuffer validity, int nullCount)
    {
        var offsets = arena.Rent<int>(rows + 1);
        var data = arena.Rent<byte>(Math.Max(64, rows * 8));
        try
        {
            offsets[0] = 0;
            var used = 0;
            for (var i = 0; i < rows; i++)
            {
                if (values[i] is string text && text.Length > 0)
                {
                    var needed = used + System.Text.Encoding.UTF8.GetMaxByteCount(text.Length);
                    if (data.Length < needed)
                    {
                        var grown = arena.Rent<byte>(Math.Max(needed, data.Length * 2));
                        data.AsSpan(0, used).CopyTo(grown);
                        arena.Return(data);
                        data = grown;
                    }

                    used += System.Text.Encoding.UTF8.GetBytes(text.AsSpan(), data.AsSpan(used));
                }

                offsets[i + 1] = used;
            }

            var offsetBuffer = arena.CreateBuffer(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(offsets.AsSpan(0, rows + 1)));
            return new StringArray(rows, offsetBuffer, arena.CreateBuffer(data.AsSpan(0, used)),
                validity, nullCount, 0);
        }
        finally
        {
            arena.Return(offsets);
            arena.Return(data);
        }
    }

    private static IArrowArray Binary(
        ExecutionArena arena, object?[] values, int rows, ArrowBuffer validity, int nullCount)
    {
        var offsets = arena.Rent<int>(rows + 1);
        var total = 0;
        for (var i = 0; i < rows; i++)
        {
            total += Bytes(values[i]).Length;
        }

        var data = arena.Rent<byte>(Math.Max(1, total));
        try
        {
            offsets[0] = 0;
            var used = 0;
            for (var i = 0; i < rows; i++)
            {
                var span = Bytes(values[i]).Span;
                span.CopyTo(data.AsSpan(used));
                used += span.Length;
                offsets[i + 1] = used;
            }

            var offsetBuffer = arena.CreateBuffer(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(offsets.AsSpan(0, rows + 1)));
            return new BinaryArray(
                new ArrayData(
                    BinaryType.Default,
                    rows,
                    nullCount,
                    0,
                    [validity, offsetBuffer, arena.CreateBuffer(data.AsSpan(0, used))]));
        }
        finally
        {
            arena.Return(offsets);
            arena.Return(data);
        }
    }

    private static IArrowArray Decimal(
        ExecutionArena arena,
        object?[] values,
        int rows,
        ArrowBuffer validity,
        int nullCount,
        Decimal128Type type,
        string column)
    {
        const int Width = 16;
        var bytes = arena.Rent<byte>(rows * Width);
        try
        {
            var span = bytes.AsSpan(0, rows * Width);
            span.Clear();
            for (var i = 0; i < rows; i++)
            {
                if (values[i] is { } value)
                {
                    SourceEncoding.WriteDecimal(
                        ToDecimal(value),
                        type.Precision,
                        type.Scale,
                        span.Slice(i * Width, Width),
                        "ado",
                        "a pushed query",
                        column);
                }
            }

            return new Decimal128Array(
                new ArrayData(type, rows, nullCount, 0, [validity, arena.CreateBuffer(span)]));
        }
        finally
        {
            arena.Return(bytes);
        }
    }

    private static IArrowArray Uuid(
        ExecutionArena arena,
        object?[] values,
        int rows,
        ArrowBuffer validity,
        int nullCount,
        FixedSizeBinaryType type)
    {
        var width = type.ByteWidth;
        var bytes = arena.Rent<byte>(rows * width);
        try
        {
            var span = bytes.AsSpan(0, rows * width);
            span.Clear();
            for (var i = 0; i < rows; i++)
            {
                if (values[i] is { } value)
                {
                    SourceEncoding.WriteUuid(ToGuid(value), span.Slice(i * width, width));
                }
            }

            return new Apache.Arrow.Arrays.FixedSizeBinaryArray(
                new ArrayData(type, rows, nullCount, 0, [validity, arena.CreateBuffer(span)]));
        }
        finally
        {
            arena.Return(bytes);
        }
    }

    // ---- the conversions a driver's boxed value may need -------------------------------------
    //
    // Every one of these is "the value the provider gave, read as the type the catalog declared".
    // They are deliberately narrow: a widening conversion (int → long) is fine, and a lossy one
    // (double → int) is not, because the value would silently change. Anything that does not fit is
    // an InvalidCastException, which the source turns into a SourceExecutionException naming the
    // query.

    private static bool ToBool(object value) => value switch
    {
        bool b => b,
        byte b => b != 0,
        short s => s != 0,
        int i => i != 0,
        long l => l != 0,
        _ => throw Cast(value, "BOOL"),
    };

    private static sbyte ToInt8(object value) => Convert.ToSByte(value, CultureInfo.InvariantCulture);

    private static short ToInt16(object value) => Convert.ToInt16(value, CultureInfo.InvariantCulture);

    private static int ToInt32(object value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static long ToInt64(object value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static float ToSingle(object value) => Convert.ToSingle(value, CultureInfo.InvariantCulture);

    private static double ToDouble(object value) => Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static decimal ToDecimal(object value) => value switch
    {
        decimal d => d,
        double d => (decimal)d,
        float f => (decimal)f,
        long l => l,
        int i => i,
        string s => decimal.Parse(s, CultureInfo.InvariantCulture),
        _ => throw Cast(value, "DECIMAL"),
    };

    private static Guid ToGuid(object value) => value switch
    {
        Guid g => g,
        string s => Guid.Parse(s),
        byte[] b when b.Length == 16 => new Guid(b),
        _ => throw Cast(value, "UUID"),
    };

    private static ReadOnlyMemory<byte> Bytes(object? value) => value switch
    {
        null => ReadOnlyMemory<byte>.Empty,
        byte[] b => b,
        ReadOnlyMemory<byte> m => m,
        _ => throw Cast(value, "BINARY"),
    };

    /// <summary>Days since the epoch — Arrow's DATE32, and what the executor's kernels read.</summary>
    private static int ToDays(object value) => value switch
    {
        DateTime d => (int)(d.Date - DateTime.UnixEpoch.Date).TotalDays,
        DateOnly d => d.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber,
        DateTimeOffset o => (int)(o.UtcDateTime.Date - DateTime.UnixEpoch.Date).TotalDays,
        string s => ToDays(DateTime.Parse(s, CultureInfo.InvariantCulture)),
        _ => throw Cast(value, "DATE"),
    };

    private static long ToTimeUnits(object value, Time64Type type)
    {
        var ticks = value switch
        {
            TimeSpan t => t.Ticks,
            TimeOnly t => t.Ticks,
            DateTime d => d.TimeOfDay.Ticks,
            string s => TimeSpan.Parse(s, CultureInfo.InvariantCulture).Ticks,
            _ => throw Cast(value, "TIME"),
        };

        return type.Unit switch
        {
            TimeUnit.Nanosecond => ticks * 100,
            TimeUnit.Microsecond => ticks / 10,
            TimeUnit.Millisecond => ticks / TimeSpan.TicksPerMillisecond,
            _ => ticks / TimeSpan.TicksPerSecond,
        };
    }

    private static long ToTimestampUnits(object value, TimestampType type)
    {
        var utc = value switch
        {
            DateTimeOffset o => o.UtcDateTime,
            DateTime d => d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d,
            DateOnly d => d.ToDateTime(TimeOnly.MinValue),
            string s => DateTime.Parse(
                s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            _ => throw Cast(value, "TIMESTAMP"),
        };

        var ticks = utc.Ticks - DateTime.UnixEpoch.Ticks;
        return type.Unit switch
        {
            TimeUnit.Nanosecond => ticks * 100,
            TimeUnit.Microsecond => ticks / 10,
            TimeUnit.Millisecond => ticks / TimeSpan.TicksPerMillisecond,
            _ => ticks / TimeSpan.TicksPerSecond,
        };
    }

    private static InvalidCastException Cast(object value, string kind) =>
        new($"a {value.GetType().Name} cannot be read as {kind}.");
}
