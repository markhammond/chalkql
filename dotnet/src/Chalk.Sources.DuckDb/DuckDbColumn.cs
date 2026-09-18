using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Catalog;
using DuckDB.NET.Native;
using ArrowArray = Apache.Arrow.IArrowArray;
using TypeKind = Chalk.Ir.TypeKind;

namespace Chalk.Sources.DuckDb;

/// <summary>
/// One output column's staging, filled straight out of DuckDB's vectors (D148,
/// <c>docs/design/24-zero-gc.md</c> §6).
/// </summary>
/// <remarks>
/// <para>
/// The shape is <c>AdoBatchReader</c>'s, one level lower down: a byte of validity per row, values in
/// arena-rented storage of the Chalk column's own width, and the Arrow buffers built through
/// <see cref="ExecutionArena.CreateBuffer"/>. What is different is where the values come from — a
/// pointer into DuckDB's own vector rather than a getter per cell — so a fetched row costs nothing
/// on the managed heap, strings included.
/// </para>
/// <para>
/// A DuckDB chunk holds up to 2 048 rows (<c>duckdb_vector_size</c>) and a Chalk batch holds as many
/// as the execution asked for, so a chunk is appended in slices and a batch may span several.
/// </para>
/// </remarks>
internal sealed unsafe class DuckDbColumn
{
    private readonly ExecutionArena _arena;
    private readonly ChalkType _type;
    private readonly TypeKind _kind;
    private readonly IArrowType _arrowType;
    private readonly string _name;
    private readonly string _sourceId;
    private readonly string _subject;
    private readonly int _capacity;
    private readonly int _width;
    private readonly DuckDBType _duckType;
    private readonly DuckDBType _decimalStorage;
    private readonly int _sourceWidth;
    private readonly long _unitMultiplier;
    private readonly long _unitDivisor;

    private byte[] _valid;
    private byte[] _fixed;
    private byte[] _text = [];
    private int[] _offsets = [];
    private int _used;

    private DuckDbColumn(
        ExecutionArena arena,
        int capacity,
        ChalkType type,
        IArrowType arrowType,
        string name,
        string sourceId,
        string subject,
        DuckDBType duckType,
        DuckDBType decimalStorage,
        long unitMultiplier,
        long unitDivisor)
    {
        _arena = arena;
        _capacity = capacity;
        _type = type;
        _kind = type.Kind;
        _arrowType = arrowType;
        _name = name;
        _sourceId = sourceId;
        _subject = subject;
        _duckType = duckType;
        _decimalStorage = decimalStorage;
        _unitMultiplier = unitMultiplier;
        _unitDivisor = unitDivisor;
        _width = DuckDbTypes.StagingWidth(type.Kind);
        _sourceWidth = DuckDbTypes.PhysicalWidth(duckType, decimalStorage);
        _valid = arena.Rent<byte>(capacity);
        _fixed = _width == 0 ? [] : arena.Rent<byte>(capacity * _width);
        if (_width == 0)
        {
            _offsets = arena.Rent<int>(capacity + 1);
            _text = arena.Rent<byte>(Math.Max(64, capacity * 8));
        }
    }

    /// <summary>
    /// A column reader for this pair of types, or null when the pair is outside §6's table — which
    /// is what sends the whole query to the <c>DbDataReader</c> path.
    /// </summary>
    public static DuckDbColumn? TryCreate(
        ExecutionArena arena,
        int capacity,
        ChalkType type,
        IArrowType arrowType,
        string name,
        string sourceId,
        string subject,
        DuckDBLogicalType logicalType)
    {
        var duckType = NativeMethods.LogicalType.DuckDBGetTypeId(logicalType);
        var storage = DuckDBType.Invalid;
        if (duckType == DuckDBType.Decimal)
        {
            storage = NativeMethods.LogicalType.DuckDBDecimalInternalType(logicalType);
            if (NativeMethods.LogicalType.DuckDBDecimalScale(logicalType) != type.Scale)
            {
                // A scale the column was not declared at would have to be rescaled, which is the
                // DbDataReader path's business: it converts through decimal.
                return null;
            }
        }

        if (!DuckDbTypes.Handles(duckType, type.Kind))
        {
            return null;
        }

        var (multiplier, divisor) = DuckDbTypes.Units(duckType, type.Kind, arrowType);
        return new DuckDbColumn(
            arena, capacity, type, arrowType, name, sourceId, subject,
            duckType, storage, multiplier, divisor);
    }

    /// <summary>Resets the per-batch staging. Rows arrive in order, so offsets append.</summary>
    public void BeginBatch()
    {
        _used = 0;
        if (_offsets.Length > 0)
        {
            _offsets[0] = 0;
        }
    }

    /// <summary>
    /// Copies <paramref name="count"/> rows of one chunk's vector, starting at
    /// <paramref name="from"/>, into staging at <paramref name="at"/>.
    /// </summary>
    public void Append(nint vector, int from, int count, int at)
    {
        var data = (byte*)NativeMethods.Vectors.DuckDBVectorGetData(vector);
        var validity = NativeMethods.Vectors.DuckDBVectorGetValidity(vector);

        for (var i = 0; i < count; i++)
        {
            var source = from + i;
            var row = at + i;
            var valid = validity is null || (validity[source >> 6] & (1UL << (source & 63))) != 0;
            _valid[row] = valid ? (byte)1 : (byte)0;
            if (!valid)
            {
                if (_width != 0)
                {
                    _fixed.AsSpan(row * _width, _width).Clear();
                }
                else
                {
                    _offsets[row + 1] = _used;
                }

                continue;
            }

            Copy(data, source, row);
        }
    }

    /// <summary>The Arrow array for the first <paramref name="rows"/> staged values.</summary>
    public ArrowArray Build(int rows)
    {
        var (validity, nulls) = BuildValidity(rows);
        if (_width == 0)
        {
            var offsets = _arena.CreateBuffer(
                MemoryMarshal.AsBytes(_offsets.AsSpan(0, rows + 1)));
            var values = _arena.CreateBuffer(_text.AsSpan(0, _used));
            return _kind == TypeKind.String
                ? new StringArray(rows, offsets, values, validity, nulls, 0)
                : new BinaryArray(BinaryType.Default, rows, offsets, values, validity, nulls, 0);
        }

        if (_kind == TypeKind.Bool)
        {
            var bytes = (rows + 7) / 8;
            var packed = _arena.Rent<byte>(bytes);
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

                return new BooleanArray(_arena.CreateBuffer(span), validity, rows, nulls, 0);
            }
            finally
            {
                _arena.Return(packed);
            }
        }

        var buffer = _arena.CreateBuffer(_fixed.AsSpan(0, rows * _width));
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
            TypeKind.Decimal => new Decimal128Array(
                new ArrayData((Decimal128Type)_arrowType, rows, nulls, 0, [validity, buffer])),
            TypeKind.Uuid => new Apache.Arrow.Arrays.FixedSizeBinaryArray(
                new ArrayData((FixedSizeBinaryType)_arrowType, rows, nulls, 0, [validity, buffer])),
            _ => throw new InvalidOperationException($"no native DuckDB array for {_kind}"),
        };
    }

    public void Release()
    {
        _arena.Return(_valid);
        _valid = [];
        _arena.Return(_fixed);
        _fixed = [];
        _arena.Return(_text);
        _text = [];
        _arena.Return(_offsets);
        _offsets = [];
    }

    /// <summary>One non-NULL value, from DuckDB's own memory into the staging.</summary>
    private void Copy(byte* data, int source, int row)
    {
        switch (_kind)
        {
            case TypeKind.Bool:
                _fixed[row] = data[source] != 0 ? (byte)1 : (byte)0;
                break;
            case TypeKind.I8:
                Fixed<sbyte>()[row] = checked((sbyte)Integer(data, source));
                break;
            case TypeKind.I16:
                Fixed<short>()[row] = checked((short)Integer(data, source));
                break;
            case TypeKind.I32:
                Fixed<int>()[row] = checked((int)Integer(data, source));
                break;
            case TypeKind.I64:
                Fixed<long>()[row] = Integer(data, source);
                break;
            case TypeKind.Fp32:
                Fixed<float>()[row] = _duckType == DuckDBType.Float
                    ? ((float*)data)[source]
                    : (float)((double*)data)[source];
                break;
            case TypeKind.Fp64:
                Fixed<double>()[row] = _duckType == DuckDBType.Double
                    ? ((double*)data)[source]
                    : ((float*)data)[source];
                break;
            case TypeKind.Date:
                Fixed<int>()[row] = ((int*)data)[source];
                break;
            case TypeKind.Time or TypeKind.Timestamp or TypeKind.TimestampTz:
                Fixed<long>()[row] = Units(((long*)data)[source]);
                break;
            case TypeKind.Decimal:
                CopyDecimal(data, source, row);
                break;
            case TypeKind.Uuid:
                CopyUuid(data, source, row);
                break;
            case TypeKind.String or TypeKind.Binary:
                CopyText(data, source, row);
                break;
            default:
                throw new InvalidOperationException(
                    $"the native DuckDB reader has no case for {_kind}; DuckDbColumn.TryCreate "
                    + "should not have accepted this column.");
        }
    }

    /// <summary>
    /// A DECIMAL's unscaled value, sign-extended into the sixteen little-endian two's-complement
    /// bytes Arrow's DECIMAL128 wants. DuckDB stores it in INT16, INT32, INT64 or HUGEINT by the
    /// declared width; a HUGEINT already <em>is</em> those sixteen bytes.
    /// </summary>
    private void CopyDecimal(byte* data, int source, int row)
    {
        const int Width = 16;
        var target = _fixed.AsSpan(row * Width, Width);
        if (_decimalStorage == DuckDBType.HugeInt)
        {
            new ReadOnlySpan<byte>(data + (source * Width), Width).CopyTo(target);
            return;
        }

        var unscaled = _decimalStorage switch
        {
            DuckDBType.SmallInt => ((short*)data)[source],
            DuckDBType.Integer => ((int*)data)[source],
            _ => ((long*)data)[source],
        };

        MemoryMarshal.Write(target, in unscaled);
        target[8..].Fill(unscaled < 0 ? (byte)0xFF : (byte)0);
    }

    /// <summary>
    /// A UUID's sixteen bytes, big-endian, which is what Chalk's FIXED_SIZE_BINARY(16) holds.
    /// DuckDB keeps one as a HUGEINT with the sign bit flipped — little-endian, high bit inverted —
    /// so recovering the UUID is a reverse and one XOR (ADR 0023).
    /// </summary>
    private void CopyUuid(byte* data, int source, int row)
    {
        const int Width = 16;
        var stored = new ReadOnlySpan<byte>(data + (source * Width), Width);
        var target = _fixed.AsSpan(row * Width, Width);
        for (var i = 0; i < Width; i++)
        {
            target[i] = stored[Width - 1 - i];
        }

        target[0] ^= 0x80;
    }

    /// <summary>
    /// A <c>duckdb_string_t</c>'s bytes: <c>{ int32 length; }</c> then either twelve inlined bytes or
    /// a four-byte prefix and an eight-byte pointer. Copied into the arena, never made into a string.
    /// </summary>
    private void CopyText(byte* data, int source, int row)
    {
        var entry = data + (source * 16);
        var length = *(int*)entry;
        var bytes = length <= 12 ? entry + 4 : *(byte**)(entry + 8);

        Grow(length);
        new ReadOnlySpan<byte>(bytes, length).CopyTo(_text.AsSpan(_used));
        _used += length;
        _offsets[row + 1] = _used;
    }

    /// <summary>One integer value widened to 64 bits, whatever DuckDB stored it in.</summary>
    private long Integer(byte* data, int source) => _duckType switch
    {
        DuckDBType.TinyInt => ((sbyte*)data)[source],
        DuckDBType.SmallInt => ((short*)data)[source],
        DuckDBType.Integer => ((int*)data)[source],
        DuckDBType.BigInt => ((long*)data)[source],
        DuckDBType.UnsignedTinyInt => data[source],
        DuckDBType.UnsignedSmallInt => ((ushort*)data)[source],
        DuckDBType.UnsignedInteger => ((uint*)data)[source],
        DuckDBType.UnsignedBigInt => checked((long)((ulong*)data)[source]),
        DuckDBType.Boolean => data[source] != 0 ? 1 : 0,
        _ => throw new InvalidOperationException(
            $"column '{_name}' is {_duckType}, which is not an integer DuckDB stores by width."),
    };

    private long Units(long value) => value * _unitMultiplier / _unitDivisor;

    private Span<T> Fixed<T>()
        where T : struct
        => MemoryMarshal.Cast<byte, T>(_fixed.AsSpan(0, _capacity * _width));

    private void Grow(int extra)
    {
        var needed = _used + extra;
        if (_text.Length >= needed)
        {
            return;
        }

        var grown = _arena.Rent<byte>(Math.Max(needed, _text.Length * 2));
        _text.AsSpan(0, _used).CopyTo(grown);
        _arena.Return(_text);
        _text = grown;
    }

    private (ArrowBuffer Buffer, int NullCount) BuildValidity(int rows)
    {
        var bytes = (rows + 7) / 8;
        var bits = _arena.Rent<byte>(bytes);
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

            if (nulls > 0 && !_type.Nullable)
            {
                throw new SourceContractException(
                    _sourceId,
                    _subject,
                    $"column '{_name}' is declared NOT NULL in the catalog, but the query produced "
                    + $"{nulls} NULL value(s). Correct the declared nullability.");
            }

            return nulls == 0 ? (ArrowBuffer.Empty, 0) : (_arena.CreateBuffer(span), nulls);
        }
        finally
        {
            _arena.Return(bits);
        }
    }

    /// <summary>Bytes one row of the source vector occupies. Only the text kinds ignore it.</summary>
    public int SourceWidth => _sourceWidth;
}
