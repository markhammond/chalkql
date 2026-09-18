using System.Numerics;
using System.Runtime.CompilerServices;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using Chalk.Sources.Keys;
using Array = System.Array;

namespace Chalk.Execution.Operators;

/// <summary>Which of the sort's three paths an ordering takes (<c>docs/design/41-blocking-sort.md</c> §2).</summary>
internal enum SortKeyPath : byte
{
    /// <summary>Nothing about the ordering could be encoded: the multi-key comparer, as before.</summary>
    Comparer,

    /// <summary>The whole composite fits one word and sorts as <c>Array.Sort(ulong[], int[])</c>.</summary>
    Word64,

    /// <summary>The composite needs two words.</summary>
    Word128,
}

/// <summary>How a two-word composite is sorted; measured on the hop benchmark (ADR 0051 §4).</summary>
internal enum SortWide128 : byte
{
    /// <summary>Sort by the high word, then each run of equal high words by the low word.</summary>
    RunsByLowWord,

    /// <summary>An LSD radix sort over both words.</summary>
    Radix,
}

/// <summary>
/// The concatenated input as the sort addresses it: one view per column per chunk, with a row's
/// chunk and offset a shift and a subtraction away (D267 c). One chunk is the whole table, which is
/// what every sort small enough to fit one buffer gets.
/// </summary>
internal readonly struct SortTable(ColumnView[] views, int columns, int chunkRows, int chunkShift, int rows)
{
    /// <summary>
    /// The concatenation as it came out: one chunk is addressed as chunk zero and costs no shifting
    /// at all, several are a power of two rows apart.
    /// </summary>
    public static SortTable Of(
        ColumnView[] views, int columns, int chunkShift, int chunks, int rows) =>
        chunks == 1
            ? new(views, columns, int.MaxValue, 31, rows)
            : new(views, columns, 1 << chunkShift, chunkShift, rows);

    /// <summary>Views by <c>chunk * Columns + column</c>.</summary>
    public ColumnView[] Views { get; } = views;

    public int Columns { get; } = columns;

    /// <summary>Rows in every chunk but the last, always <c>1 &lt;&lt; ChunkShift</c> where there is more than one.</summary>
    public int ChunkRows { get; } = chunkRows;

    public int ChunkShift { get; } = chunkShift;

    /// <summary>Rows in the whole table.</summary>
    public int Rows { get; } = rows;

    public int ChunkOf(int row) => row >> ChunkShift;

    public int OffsetOf(int row) => row - (ChunkOf(row) << ChunkShift);

    /// <summary>One column of one chunk.</summary>
    public ref readonly ColumnView Column(int chunk, int column) =>
        ref Views[(chunk * Columns) + column];

    /// <summary>The columns of the chunk a row falls in, for the comparer's own addressing.</summary>
    public ReadOnlySpan<ColumnView> ChunkOfRow(int row) =>
        Views.AsSpan(ChunkOf(row) * Columns, Columns);
}

/// <summary>
/// The blocking sort's key, encoded rather than compared (D267 b): one fixed-width composite per row
/// with each ordering key packed most-significant first, so that the rows sort as numbers with no
/// comparer call at all. The encoders are the POCO index's, shared through
/// <c>Chalk.Sources.Keys</c> (D267 a).
/// </summary>
/// <remarks>
/// <para>
/// A key contributes a <em>field</em> of as many bits as its values actually need. The sort is
/// blocking, so the whole column is in hand before a key is encoded and its range is known: a key's
/// code is narrowed to <c>code - min</c>, which is what lets the benchmark's four-key hop — a symbol,
/// two timestamps and a volume — fit 128 bits with nothing truncated. A column of one distinct value
/// contributes no bits at all.
/// </para>
/// <para>
/// A descending key complements its code within its own span, and a NULL takes the code below or
/// above every value as the ordering says — the permutation index's symbol rule, applied to a
/// narrowed integer as well as to a rank. A STRING or BINARY key takes the rank of its value among
/// the column's distinct values where there are few enough short ones, and otherwise the first eight
/// bytes of the value, which order values without identifying them.
/// </para>
/// <para>
/// A field that does not identify its values ends the composite: an eight-byte prefix, a code too
/// wide to leave room for a NULL's own, or a field the 128 bits could only take part of. What is
/// packed is then a correct <em>prefix</em> of the ordering — rows it separates are ordered, rows it
/// leaves equal are of unknown order — and the comparer settles those runs afterwards
/// (<see cref="SortKeyLayout.NeedsTieBreak"/>). A key below such a field is not packed at all: it
/// would order rows the field above it had already left undecided.
/// </para>
/// </remarks>
internal sealed class SortKeyComposite
{
    /// <summary>The widest composite there is: two words.</summary>
    public const int WidestBits = 128;

    /// <summary>
    /// A rank must identify its value exactly, and the image that makes a probe cheap is exact only
    /// up to sixteen bytes, so a column with a longer value takes the byte prefix instead.
    /// </summary>
    private const int LongestRankedValue = KeyImage.MaxInline;

    /// <summary>
    /// The largest distinct count worth ranking, measured on the benchmark (ADR 0051 §5). The
    /// permutation index's own limit (<see cref="SortSymbolRanks.Limit"/>) caps it on a small input,
    /// where a dictionary of distinct values would cost more than the comparisons it saves.
    /// </summary>
    private const int RankCardinalityCap = 1024;

    /// <summary>Bytes of a value the prefix path packs into one word, big-endian so they order.</summary>
    private const int PrefixBytes = 8;

    private static readonly AsyncLocal<int?> BitsOverride = new();

    private static readonly AsyncLocal<SortWide128?> WideOverride = new();

    private readonly SortOrdering _ordering;
    private readonly ColumnKind[] _kinds;
    private readonly Field[] _fields;
    private readonly SymbolTable[] _symbols;

    public SortKeyComposite(SortOrdering ordering, ChalkType[] columnTypes)
    {
        _ordering = ordering;
        _kinds = new ColumnKind[ordering.KeyCount];
        _fields = new Field[ordering.KeyCount];
        _symbols = new SymbolTable[ordering.KeyCount];
        for (var k = 0; k < ordering.KeyCount; k++)
        {
            _kinds[k] = ColumnKinds.Of(columnTypes[ordering.ColumnOf(k)]);
            _symbols[k] = new SymbolTable();
        }
    }

    /// <summary>
    /// A test hook: the widest composite this sort will build. Production is
    /// <see cref="WidestBits"/>; a test lowers it to force the 64-bit, the tie-break and the comparer
    /// paths over data that would otherwise take a wider one.
    /// </summary>
    /// <remarks>
    /// <see cref="AsyncLocal{T}"/> rather than a plain static: a test that lowers it must not change
    /// what another test's sort does on another thread, and an execution is a chain of awaits from
    /// the caller that set it.
    /// </remarks>
    internal static int MaxBits
    {
        get => BitsOverride.Value ?? WidestBits;
        set => BitsOverride.Value = value;
    }

    /// <summary>A test hook and the benchmark's switch: how a two-word composite is sorted.</summary>
    internal static SortWide128 Wide128
    {
        get => WideOverride.Value ?? SortWide128.RunsByLowWord;
        set => WideOverride.Value = value;
    }

    /// <summary>
    /// Plans the composite over the concatenated input: one pass per key to learn its range, its
    /// NULLs and — for a STRING key — its distinct values, then the packing itself.
    /// </summary>
    public SortKeyLayout Plan(in SortTable table, ExecutionArena arena)
    {
        var budget = Math.Clamp(MaxBits, 0, WidestBits);
        var used = 0;
        var lossy = false;

        for (var k = 0; k < _fields.Length; k++)
        {
            ref var field = ref _fields[k];
            field = default;
            field.Column = _ordering.ColumnOf(k);
            field.Descending = _ordering.IsDescending(k);
            field.NullsFirst = _ordering.NullsFirst(k);

            if (used >= budget || !Describe(k, ref field, in table, arena))
            {
                // Neither this key nor any below it reaches the composite; the comparer settles them.
                lossy = true;
                Abandon(k);
                break;
            }

            var take = Math.Min(field.CodeBits, budget - used);
            field.Drop = field.CodeBits - take;
            field.Shift = WidestBits - used - take;
            field.Width = take;
            used += take;

            // A field that does not identify its values — a truncated one, an eight-byte prefix —
            // may be the last thing in the composite and nothing more. Two rows it leaves equal are
            // of unknown order, and a key packed below it would order them by the wrong key.
            if (field.Drop > 0 || !field.Exact)
            {
                lossy = true;
                Abandon(k + 1);
                break;
            }
        }

        if (used == 0 && lossy)
        {
            return new SortKeyLayout(SortKeyPath.Comparer, needsTieBreak: true, bits: 0);
        }

        return new SortKeyLayout(
            used <= 64 ? SortKeyPath.Word64 : SortKeyPath.Word128, lossy, used);
    }

    /// <summary>Marks every key from <paramref name="key"/> down as one the composite does not carry.</summary>
    private void Abandon(int key)
    {
        for (var rest = key; rest < _fields.Length; rest++)
        {
            _fields[rest].Source = KeySource.None;
        }
    }

    /// <summary>Writes one composite per row into <paramref name="high"/> and <paramref name="low"/>.</summary>
    /// <remarks>
    /// One pass per key rather than one pass per row: each pass is a tight loop over one column's
    /// lanes with the field's shape hoisted out of it, and dispatched on the kind once per chunk.
    /// </remarks>
    public void Encode(in SortTable table, in SortKeyLayout layout, ulong[] high, ulong[] low)
    {
        Array.Clear(high, 0, table.Rows);
        if (layout.Path == SortKeyPath.Word128)
        {
            Array.Clear(low, 0, table.Rows);
        }

        for (var k = 0; k < _fields.Length; k++)
        {
            ref readonly var field = ref _fields[k];
            if (field.Source == KeySource.None || field.Width == 0)
            {
                continue;
            }

            for (var start = 0; start < table.Rows; start += table.ChunkRows)
            {
                EncodeColumn(
                    in table.Column(table.ChunkOf(start), field.Column),
                    in field,
                    _symbols[k],
                    start,
                    Math.Min(table.ChunkRows, table.Rows - start),
                    high,
                    low);
            }
        }
    }

    /// <summary>
    /// Sorts <paramref name="permutation"/> by the composite, with no comparer. The high word comes
    /// back in the permutation's own order; the return says whether the low word did too, which is
    /// what a tie-break needs to know to find its runs.
    /// </summary>
    public bool Sort(
        in SortKeyLayout layout,
        ulong[] high,
        ulong[] low,
        int[] permutation,
        int rows,
        ExecutionArena arena)
    {
        if (layout.Path == SortKeyPath.Word64)
        {
            Array.Sort(high, permutation, 0, rows);
            return true;
        }

        if (Wide128 == SortWide128.Radix)
        {
            SortWideByRadix(high, low, permutation, rows, arena);
            return true;
        }

        SortWideByRuns(high, low, permutation, rows);
        return false;
    }

    /// <summary>Gives back everything <see cref="Plan"/> rented. Safe to call twice.</summary>
    public void Release(ExecutionArena arena)
    {
        foreach (var table in _symbols)
        {
            table.Release(arena);
        }
    }

    /// <summary>
    /// The high word, then each run of equal high words by its low word. The low words stay where
    /// they are and the run sorts through a struct comparer over them: a run is a handful of rows,
    /// and a whole second array of low words would cost more than the indirection does.
    /// </summary>
    private static void SortWideByRuns(ulong[] high, ulong[] low, int[] permutation, int rows)
    {
        Array.Sort(high, permutation, 0, rows);

        var order = new ByLowWord(low);
        var start = 0;
        while (start < rows)
        {
            var end = start + 1;
            while (end < rows && high[end] == high[start])
            {
                end++;
            }

            if (end - start > 1)
            {
                RowSort.Sort(permutation.AsSpan(start, end - start), order);
            }

            start = end;
        }
    }

    /// <summary>
    /// An LSD radix sort over the two words, a byte at a time from the low word up, skipping any
    /// digit every row shares — which is every digit above the bits the composite actually uses.
    /// </summary>
    private static void SortWideByRadix(
        ulong[] high, ulong[] low, int[] permutation, int rows, ExecutionArena arena)
    {
        const int Digits = 256;
        var highDestination = arena.Rent<ulong>(rows);
        var lowDestination = arena.Rent<ulong>(rows);
        var permutationDestination = arena.Rent<int>(rows);
        var counts = arena.Rent<int>(Digits);
        try
        {
            var sourceHigh = high;
            var sourceLow = low;
            var sourcePermutation = permutation;
            var spareHigh = highDestination;
            var spareLow = lowDestination;
            var sparePermutation = permutationDestination;

            for (var pass = 0; pass < 16; pass++)
            {
                var wide = pass >= 8;
                var shift = (pass & 7) * 8;
                var words = wide ? sourceHigh : sourceLow;
                counts.AsSpan(0, Digits).Clear();
                for (var i = 0; i < rows; i++)
                {
                    counts[(int)((words[i] >> shift) & 0xFF)]++;
                }

                if (counts[(int)((words[0] >> shift) & 0xFF)] == rows)
                {
                    continue;
                }

                var running = 0;
                for (var digit = 0; digit < Digits; digit++)
                {
                    var here = counts[digit];
                    counts[digit] = running;
                    running += here;
                }

                for (var i = 0; i < rows; i++)
                {
                    var at = counts[(int)((words[i] >> shift) & 0xFF)]++;
                    spareHigh[at] = sourceHigh[i];
                    spareLow[at] = sourceLow[i];
                    sparePermutation[at] = sourcePermutation[i];
                }

                (sourceHigh, spareHigh) = (spareHigh, sourceHigh);
                (sourceLow, spareLow) = (spareLow, sourceLow);
                (sourcePermutation, sparePermutation) = (sparePermutation, sourcePermutation);
            }

            // An odd number of scatters leaves the answer in the rented arrays; copy it home.
            if (!ReferenceEquals(sourcePermutation, permutation))
            {
                sourcePermutation.AsSpan(0, rows).CopyTo(permutation);
                sourceHigh.AsSpan(0, rows).CopyTo(high);
                sourceLow.AsSpan(0, rows).CopyTo(low);
            }
        }
        finally
        {
            arena.Return(counts);
            arena.Return(permutationDestination);
            arena.Return(lowDestination);
            arena.Return(highDestination);
        }
    }

    /// <summary>
    /// What one key contributes: its source, how wide its code is once narrowed, and whether it
    /// needs a NULL flag. False when the key cannot be encoded at all.
    /// </summary>
    private bool Describe(int key, ref Field field, in SortTable table, ExecutionArena arena)
    {
        var hasNulls = false;
        for (var start = 0; start < table.Rows; start += table.ChunkRows)
        {
            hasNulls |= table.Column(table.ChunkOf(start), field.Column).NullCount != 0;
        }

        field.HasNulls = hasNulls;
        field.Exact = true;

        switch (_kinds[key])
        {
            case ColumnKind.Boolean:
                field.Source = KeySource.Bool;
                Narrow(ref field, (0, 1));
                break;
            case ColumnKind.Int8:
                field.Source = KeySource.Int8;
                Narrow(ref field, Range<sbyte, byte, SByteEncoding>(in table, in field));
                break;
            case ColumnKind.Int16:
                field.Source = KeySource.Int16;
                Narrow(ref field, Range<short, ushort, Int16Encoding>(in table, in field));
                break;
            case ColumnKind.Int32:
                field.Source = KeySource.Int32;
                Narrow(ref field, Range<int, uint, Int32Encoding>(in table, in field));
                break;
            case ColumnKind.Int64:
                field.Source = KeySource.Int64;
                Narrow(ref field, Range<long, ulong, Int64Encoding>(in table, in field));
                break;
            case ColumnKind.Float:
                field.Source = KeySource.Float;
                Narrow(ref field, Range<float, uint, SingleEncoding>(in table, in field));
                break;
            case ColumnKind.Double:
                field.Source = KeySource.Double;
                Narrow(ref field, Range<double, ulong, DoubleEncoding>(in table, in field));
                break;
            case ColumnKind.Utf8 or ColumnKind.Binary or ColumnKind.StringView:
                DescribeBytes(key, ref field, in table, arena);
                break;
            default:
                // DECIMAL, UUID and LIST: no order-preserving code, so the comparer keeps them (§2).
                field.Source = KeySource.None;
                return false;
        }

        return true;
    }

    /// <summary>
    /// A STRING or BINARY key: the rank of its value where the column has few enough short distinct
    /// values, and the first eight bytes of the value otherwise.
    /// </summary>
    private void DescribeBytes(int key, ref Field field, in SortTable table, ExecutionArena arena)
    {
        var symbols = _symbols[key];
        var cap = Math.Min(RankCardinalityCap, SortSymbolRanks.Limit(table.Rows));
        if (symbols.Build(in table, field.Column, cap, arena))
        {
            symbols.Rank(in table, field.Column, arena);
            field.Source = KeySource.SymbolRank;
            field.Cardinality = symbols.Count;

            // The shared symbol code carries the direction and the NULL, so the rank is the code.
            field.Bump = field.HasNulls && field.NullsFirst ? 1UL : 0UL;
            field.CodeBits = SortSymbolRanks.BitsForCodes(
                SortSymbolRanks.CodeCount(symbols.Count, field.HasNulls));
            return;
        }

        // Eight bytes order a value without identifying it, so this field ends the composite.
        field.Source = KeySource.BytePrefix;
        field.Exact = false;
        Narrow(ref field, (0, ulong.MaxValue));
    }

    /// <summary>
    /// Turns a code range into a field: <c>min</c> is subtracted from every code, a NULL takes the
    /// code below or above the rest — the symbol encoding's rule, at the width of a narrowed integer
    /// — and the field is as wide as the largest code it can hold.
    /// </summary>
    private static void Narrow(ref Field field, (ulong Min, ulong Max) range)
    {
        if (range.Min > range.Max)
        {
            // Every row is NULL, so every row's code is the same one and no bit tells them apart.
            field.Min = 0;
            field.CodeBits = 0;
            return;
        }

        field.Min = range.Min;
        var span = range.Max - range.Min;

        // The one span with no room for a NULL's own code: give up the lowest bit of the value, and
        // say so, because two codes that differ only in that bit are then indistinguishable.
        if (field.HasNulls && span == ulong.MaxValue)
        {
            field.CodeDrop = 1;
            field.Exact = false;
            span >>= 1;
        }

        field.Span = span;
        if (field.HasNulls)
        {
            field.Bump = field.NullsFirst ? 1UL : 0UL;
            field.NullCode = SortSymbolRanks.NullCode(span, field.NullsFirst);
        }

        field.CodeBits = SortSymbolRanks.BitsFor(field.HasNulls ? span + 1 : span);
    }

    private static (ulong Min, ulong Max) Range<TValue, TCode, TEncoding>(
        in SortTable table, in Field field)
        where TValue : unmanaged
        where TCode : unmanaged, IBinaryInteger<TCode>, IUnsignedNumber<TCode>
        where TEncoding : struct, ISortEncoding<TValue, TCode>
    {
        var min = ulong.MaxValue;
        var max = ulong.MinValue;
        for (var start = 0; start < table.Rows; start += table.ChunkRows)
        {
            ref readonly var column = ref table.Column(table.ChunkOf(start), field.Column);
            var count = Math.Min(table.ChunkRows, table.Rows - start);
            var lanes = column.Lanes<TValue>();
            var validity = column.NullCount == 0 ? default : column.ValidityBits();
            var offset = column.Offset;
            for (var i = 0; i < count; i++)
            {
                if (!validity.IsEmpty && !BitUtility.GetBit(validity, offset + i))
                {
                    continue;
                }

                var code = ulong.CreateTruncating(TEncoding.EncodeAscending(lanes[i]));
                if (code < min)
                {
                    min = code;
                }

                if (code > max)
                {
                    max = code;
                }
            }
        }

        return (min, max);
    }

    private static void EncodeColumn(
        in ColumnView column,
        in Field field,
        SymbolTable symbols,
        int rowBase,
        int count,
        ulong[] high,
        ulong[] low)
    {
        switch (field.Source)
        {
            case KeySource.Bool:
                EncodeBool(in column, in field, rowBase, count, high, low);
                break;
            case KeySource.Int8:
                EncodeLanes<sbyte, byte, SByteEncoding>(in column, in field, rowBase, count, high, low);
                break;
            case KeySource.Int16:
                EncodeLanes<short, ushort, Int16Encoding>(in column, in field, rowBase, count, high, low);
                break;
            case KeySource.Int32:
                EncodeLanes<int, uint, Int32Encoding>(in column, in field, rowBase, count, high, low);
                break;
            case KeySource.Int64:
                EncodeLanes<long, ulong, Int64Encoding>(in column, in field, rowBase, count, high, low);
                break;
            case KeySource.Float:
                EncodeLanes<float, uint, SingleEncoding>(in column, in field, rowBase, count, high, low);
                break;
            case KeySource.Double:
                EncodeLanes<double, ulong, DoubleEncoding>(in column, in field, rowBase, count, high, low);
                break;
            case KeySource.SymbolRank:
                EncodeRanks(in column, in field, symbols, rowBase, count, high, low);
                break;
            default:
                EncodePrefix(in column, in field, rowBase, count, high, low);
                break;
        }
    }

    private static void EncodeLanes<TValue, TCode, TEncoding>(
        in ColumnView column, in Field field, int rowBase, int count, ulong[] high, ulong[] low)
        where TValue : unmanaged
        where TCode : unmanaged, IBinaryInteger<TCode>, IUnsignedNumber<TCode>
        where TEncoding : struct, ISortEncoding<TValue, TCode>
    {
        var lanes = column.Lanes<TValue>();
        var validity = Validity(in column, in field);
        var offset = column.Offset;
        for (var i = 0; i < count; i++)
        {
            var valid = validity.IsEmpty || BitUtility.GetBit(validity, offset + i);
            var code = valid
                ? ulong.CreateTruncating(TEncoding.EncodeAscending(lanes[i])) - field.Min
                : 0UL;
            Place(in field, rowBase + i, valid, code, high, low);
        }
    }

    private static void EncodeBool(
        in ColumnView column, in Field field, int rowBase, int count, ulong[] high, ulong[] low)
    {
        var validity = Validity(in column, in field);
        var offset = column.Offset;
        for (var i = 0; i < count; i++)
        {
            var valid = validity.IsEmpty || BitUtility.GetBit(validity, offset + i);
            Place(in field, rowBase + i, valid, valid && column.BoolAt(i) ? 1UL : 0UL, high, low);
        }
    }

    private static void EncodeRanks(
        in ColumnView column,
        in Field field,
        SymbolTable symbols,
        int rowBase,
        int count,
        ulong[] high,
        ulong[] low)
    {
        var validity = Validity(in column, in field);
        var offset = column.Offset;
        for (var i = 0; i < count; i++)
        {
            var code = validity.IsEmpty || BitUtility.GetBit(validity, offset + i)
                ? SortSymbolRanks.Code(
                    symbols.RankOf(in column, i), field.Cardinality, field.Descending, field.Bump != 0)
                : SortSymbolRanks.NullCode(field.Cardinality, field.NullsFirst);

            // The symbol code is already in the ordering's direction, so it is placed as it stands.
            PlaceCode(in field, rowBase + i, code, high, low);
        }
    }

    private static void EncodePrefix(
        in ColumnView column, in Field field, int rowBase, int count, ulong[] high, ulong[] low)
    {
        var validity = Validity(in column, in field);
        var offset = column.Offset;
        for (var i = 0; i < count; i++)
        {
            var valid = validity.IsEmpty || BitUtility.GetBit(validity, offset + i);
            Place(in field, rowBase + i, valid, valid ? Prefix(column.VarValue(i)) : 0UL, high, low);
        }
    }

    /// <summary>A chunk with no NULL of its own needs no per-row validity test, whatever the key has.</summary>
    private static ReadOnlySpan<byte> Validity(in ColumnView column, in Field field) =>
        field.HasNulls && column.NullCount != 0 ? column.ValidityBits() : default;

    /// <summary>The first eight bytes of a value, big-endian and zero-padded, so they order as bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Prefix(ReadOnlySpan<byte> value)
    {
        ulong word = 0;
        var taken = Math.Min(PrefixBytes, value.Length);
        for (var i = 0; i < taken; i++)
        {
            word |= (ulong)value[i] << ((PrefixBytes - 1 - i) * 8);
        }

        return word;
    }

    /// <summary>
    /// Assembles one row's field from its narrowed code — complemented for a descending key, moved
    /// aside for a NULL's own code where the column has NULLs — and writes it into the composite.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Place(
        in Field field, int row, bool valid, ulong code, ulong[] high, ulong[] low)
    {
        code = valid
            ? SortSymbolRanks.Code(code >> field.CodeDrop, field.Span, field.Descending, field.Bump != 0)
            : field.NullCode;

        PlaceCode(in field, row, code, high, low);
    }

    /// <summary>Writes an assembled field into the 128-bit composite at its own offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PlaceCode(in Field field, int row, ulong code, ulong[] high, ulong[] low)
    {
        code >>= field.Drop;
        var shift = field.Shift;
        if (shift >= 64)
        {
            high[row] |= code << (shift - 64);
            return;
        }

        if (shift + field.Width <= 64)
        {
            low[row] |= code << shift;
            return;
        }

        high[row] |= code >> (64 - shift);
        low[row] |= code << shift;
    }

    /// <summary>Orders rows by their low word, for the runs a high-word sort leaves equal.</summary>
    private readonly struct ByLowWord(ulong[] low) : IComparer<int>
    {
        public int Compare(int left, int right) => low[left].CompareTo(low[right]);
    }

    private enum KeySource : byte
    {
        None,
        Bool,
        Int8,
        Int16,
        Int32,
        Int64,
        Float,
        Double,
        SymbolRank,
        BytePrefix,
    }

    private struct Field
    {
        public KeySource Source;
        public int Column;
        public bool Descending;
        public bool NullsFirst;
        public bool HasNulls;

        /// <summary>Subtracted from every code, so a narrow range costs few bits (§2).</summary>
        public ulong Min;

        /// <summary>The largest narrowed code, which is what a descending key complements within.</summary>
        public ulong Span;

        /// <summary>One where a NULL takes the code below every value, zero where it takes the one above.</summary>
        public ulong Bump;

        /// <summary>The code a NULL row takes.</summary>
        public ulong NullCode;

        /// <summary>Bits the code needs, NULL's own included.</summary>
        public int CodeBits;

        /// <summary>Low code bits given up before the code is narrowed; only the widest span needs any.</summary>
        public int CodeDrop;

        /// <summary>The column's distinct count, for a ranked key.</summary>
        public int Cardinality;

        /// <summary>Low field bits the composite's budget could not hold.</summary>
        public int Drop;

        /// <summary>Bits this field occupies in the composite.</summary>
        public int Width;

        /// <summary>Where this field's lowest bit sits in the 128-bit composite.</summary>
        public int Shift;

        /// <summary>
        /// Whether the field identifies its values, so that equal fields mean equal keys. A field
        /// that only orders them — a byte prefix, a code with its lowest bit given up — is the last
        /// one the composite carries.
        /// </summary>
        public bool Exact;
    }

    /// <summary>
    /// The distinct values of one STRING or BINARY column, open-addressed by the sixteen-byte image
    /// the hash aggregate uses (D255), and the rank of each once they are in value order.
    /// </summary>
    private sealed class SymbolTable
    {
        private int[] _slots = [];
        private KeyImage[] _images = [];
        private int[] _rows = [];
        private int[] _ranks = [];
        private int[] _order = [];
        private int _mask;
        private KeyImage _lastImage;
        private int _lastId = -1;

        public int Count { get; private set; }

        /// <summary>
        /// Walks the column and collects its distinct values, up to <paramref name="cap"/> of them.
        /// False — too many values, or one too long to image exactly — leaves the key to the prefix.
        /// </summary>
        public bool Build(in SortTable table, int column, int cap, ExecutionArena arena)
        {
            Release(arena);
            var capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(cap, 4) * 2);
            _slots = arena.Rent<int>(capacity);
            _images = arena.Rent<KeyImage>(cap);
            _rows = arena.Rent<int>(cap);
            _slots.AsSpan(0, capacity).Clear();
            _mask = capacity - 1;
            Count = 0;
            _lastId = -1;

            for (var start = 0; start < table.Rows; start += table.ChunkRows)
            {
                ref readonly var view = ref table.Column(table.ChunkOf(start), column);
                var count = Math.Min(table.ChunkRows, table.Rows - start);
                var validity = view.NullCount == 0 ? default : view.ValidityBits();
                var offset = view.Offset;
                for (var i = 0; i < count; i++)
                {
                    if (!validity.IsEmpty && !BitUtility.GetBit(validity, offset + i))
                    {
                        continue;
                    }

                    var value = view.VarValue(i);
                    if (value.Length > LongestRankedValue || !Add(value, start + i, cap))
                    {
                        Release(arena);
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>Puts the distinct values in value order and records where each one landed.</summary>
        public void Rank(in SortTable table, int column, ExecutionArena arena)
        {
            _ranks = arena.Rent<int>(Math.Max(Count, 1));
            _order = arena.Rent<int>(Math.Max(Count, 1));
            SortSymbolRanks.RankBySortedValues(
                _order.AsSpan(0, Count),
                _ranks.AsSpan(0, Count),
                new ByValue(table.Views, table.Columns, table.ChunkShift, column, _rows));
            _lastId = -1;
        }

        /// <summary>The rank of the value in <paramref name="row"/> of <paramref name="view"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int RankOf(in ColumnView view, int row)
        {
            var image = KeyImage.OfBytes(view.VarValue(row));

            // Values run in blocks far more often than they alternate, so the row before is the
            // cheapest guess there is (D255.3).
            if (_lastId >= 0 && Same(in _lastImage, in image))
            {
                return _ranks[_lastId];
            }

            var slot = (int)(image.Hash() & (ulong)(uint)_mask);
            while (true)
            {
                var entry = _slots[slot];
                if (entry != 0 && Same(in _images[entry - 1], in image))
                {
                    _lastImage = image;
                    _lastId = entry - 1;
                    return _ranks[_lastId];
                }

                slot = (slot + 1) & _mask;
            }
        }

        public void Release(ExecutionArena arena)
        {
            if (_slots.Length != 0)
            {
                arena.Return(_slots);
                arena.Return(_images);
                arena.Return(_rows);
            }

            if (_ranks.Length != 0)
            {
                arena.Return(_ranks);
                arena.Return(_order);
            }

            _slots = [];
            _images = [];
            _rows = [];
            _ranks = [];
            _order = [];
            Count = 0;
            _lastId = -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Same(in KeyImage left, in KeyImage right) =>
            left.Length == right.Length && left.Low == right.Low && left.High == right.High;

        private bool Add(ReadOnlySpan<byte> value, int row, int cap)
        {
            var image = KeyImage.OfBytes(value);
            if (_lastId >= 0 && Same(in _lastImage, in image))
            {
                return true;
            }

            var slot = (int)(image.Hash() & (ulong)(uint)_mask);
            while (true)
            {
                var entry = _slots[slot];
                if (entry == 0)
                {
                    if (Count == cap)
                    {
                        return false;
                    }

                    _images[Count] = image;
                    _rows[Count] = row;
                    _slots[slot] = ++Count;
                    _lastImage = image;
                    _lastId = Count - 1;
                    return true;
                }

                if (Same(in _images[entry - 1], in image))
                {
                    _lastImage = image;
                    _lastId = entry - 1;
                    return true;
                }

                slot = (slot + 1) & _mask;
            }
        }

        /// <summary>Orders symbol ids by the bytes of the row each one was first seen in.</summary>
        private readonly struct ByValue(
            ColumnView[] views, int columns, int chunkShift, int column, int[] rows)
            : IComparer<int>
        {
            public int Compare(int left, int right) => Bytes(left).SequenceCompareTo(Bytes(right));

            private ReadOnlySpan<byte> Bytes(int id)
            {
                var row = rows[id];
                var chunk = row >> chunkShift;
                return views[(chunk * columns) + column].VarValue(row - (chunk << chunkShift));
            }
        }
    }
}

/// <summary>What <see cref="SortKeyComposite.Plan"/> decided, and what the sort must do about it.</summary>
internal readonly struct SortKeyLayout(SortKeyPath path, bool needsTieBreak, int bits)
{
    public SortKeyPath Path { get; } = path;

    /// <summary>
    /// Whether the composite is a prefix of the ordering rather than the whole of it, so rows it
    /// leaves equal still have to be settled by the comparer.
    /// </summary>
    public bool NeedsTieBreak { get; } = needsTieBreak;

    /// <summary>Bits the composite actually uses, for the operator's own reporting.</summary>
    public int Bits { get; } = bits;
}
