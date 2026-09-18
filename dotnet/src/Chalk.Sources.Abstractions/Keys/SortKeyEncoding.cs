using System.Numerics;
using System.Runtime.CompilerServices;

namespace Chalk.Sources.Keys;

/// <summary>
/// An order-preserving map from a value to an unsigned code: <c>a &lt; b</c> exactly when
/// <c>EncodeAscending(a) &lt; EncodeAscending(b)</c> under the ordering rules of
/// <c>docs/design/02-ir.md</c> §3. A key encoded this way is sorted as a number, with no comparer
/// and no virtual call (<c>docs/design/41-blocking-sort.md</c> §2, D267 b).
/// </summary>
/// <remarks>
/// These are the POCO permutation index's encoders, lifted here unchanged so that the index and the
/// executor's blocking sort share one implementation rather than each keeping its own (D267 a). The
/// index materialises one code per row per key; the sort packs several of them into one fixed-width
/// composite word. Both need the same map, and an encoding that ordered differently in one of them
/// would be a defect in the other.
/// </remarks>
internal interface ISortEncoding<TValue, TCode>
{
    static abstract TCode EncodeAscending(TValue value);
}

internal readonly struct SByteEncoding : ISortEncoding<sbyte, byte>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte EncodeAscending(sbyte value) => unchecked((byte)(value ^ sbyte.MinValue));
}

internal readonly struct ByteEncoding : ISortEncoding<byte, byte>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte EncodeAscending(byte value) => value;
}

internal readonly struct Int16Encoding : ISortEncoding<short, ushort>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort EncodeAscending(short value) => unchecked((ushort)(value ^ short.MinValue));
}

internal readonly struct UInt16Encoding : ISortEncoding<ushort, ushort>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort EncodeAscending(ushort value) => value;
}

internal readonly struct Int32Encoding : ISortEncoding<int, uint>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint EncodeAscending(int value) => unchecked((uint)(value ^ int.MinValue));
}

internal readonly struct UInt32Encoding : ISortEncoding<uint, uint>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint EncodeAscending(uint value) => value;
}

internal readonly struct Int64Encoding : ISortEncoding<long, ulong>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong EncodeAscending(long value) => unchecked((ulong)(value ^ long.MinValue));
}

internal readonly struct UInt64Encoding : ISortEncoding<ulong, ulong>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong EncodeAscending(ulong value) => value;
}

/// <summary>
/// The IEEE trick, with SQL's two departures from it: NaN is the largest value rather than
/// unordered, and -0.0 encodes as +0.0 because <c>CompareReal</c> treats them as equal.
/// </summary>
internal readonly struct SingleEncoding : ISortEncoding<float, uint>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint EncodeAscending(float value)
    {
        if (float.IsNaN(value))
            return uint.MaxValue;

        // CompareReal treats -0 and +0 as equal.
        if (value == 0f)
            return 0x8000_0000u;

        var bits = BitConverter.SingleToUInt32Bits(value);

        return (bits & 0x8000_0000u) != 0
            ? ~bits
            : bits ^ 0x8000_0000u;
    }
}

internal readonly struct DoubleEncoding : ISortEncoding<double, ulong>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong EncodeAscending(double value)
    {
        if (double.IsNaN(value))
            return ulong.MaxValue;

        if (value == 0d)
            return 0x8000_0000_0000_0000UL;

        var bits = BitConverter.DoubleToUInt64Bits(value);

        return (bits & 0x8000_0000_0000_0000UL) != 0
            ? ~bits
            : bits ^ 0x8000_0000_0000_0000UL;
    }
}

internal readonly struct DateTimeEncoding : ISortEncoding<DateTime, ulong>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong EncodeAscending(DateTime value) => (ulong)value.Ticks;
}

internal readonly struct DateTimeOffsetEncoding : ISortEncoding<DateTimeOffset, ulong>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong EncodeAscending(DateTimeOffset value) => (ulong)value.UtcDateTime.Ticks;
}

/// <summary>
/// A key whose values have no arithmetic order — a string, a byte array — encoded as its value's
/// <em>rank</em> among the distinct values of the column: a small integer that sorts the way the
/// values themselves do. This is the permutation index's symbol encoding, shared with the sort
/// (D267 b), and it is worth its dictionary only while the distinct count stays small, which
/// <see cref="Limit"/> decides.
/// </summary>
internal static class SortSymbolRanks
{
    /// <summary>The smallest distinct count worth encoding whatever the row count.</summary>
    private const int MinimumSymbolBudget = 256;

    /// <summary>The largest rank there is, so a rank always fits sixteen bits.</summary>
    private const int MaximumSymbolCardinality = ushort.MaxValue;

    /// <summary>
    /// Rows a distinct value must average before ranking pays: below this the dictionary costs more
    /// than the comparisons it saves.
    /// </summary>
    private const int RowsPerSymbolThreshold = 8;

    /// <summary>The largest distinct count that is still worth ranking over <paramref name="rows"/> rows.</summary>
    public static int Limit(int rows) =>
        Math.Min(MaximumSymbolCardinality, Math.Max(MinimumSymbolBudget, rows / RowsPerSymbolThreshold));

    /// <summary>
    /// Turns first-seen symbol ids into ranks: <paramref name="order"/> is filled with the ids in
    /// value order by <paramref name="comparer"/>, and <paramref name="rankById"/> then maps each id
    /// to where it landed. Both spans are the cardinality long.
    /// </summary>
    public static void RankBySortedValues<TComparer>(
        Span<int> order, Span<int> rankById, TComparer comparer)
        where TComparer : IComparer<int>
    {
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        order.Sort(comparer);

        for (var rank = 0; rank < order.Length; rank++)
        {
            rankById[order[rank]] = rank;
        }
    }

    /// <summary>
    /// One row's code: the rank complemented within the cardinality for a descending key, and
    /// shifted up by one where a NULL takes the bottom code. Ascending, nulls last, this is the rank
    /// itself.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Code(int rank, int cardinality, bool descending, bool nullsFirst) =>
        (uint)Code((uint)rank, (uint)(cardinality - 1), descending, nullsFirst);

    /// <summary>
    /// The same rule over a code that is not a rank: a value narrowed to <c>0..span</c> — which is
    /// what the sort's own keys are once their range is known — complemented within that span for a
    /// descending key and shifted up by one where a NULL takes the bottom code.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Code(ulong code, ulong span, bool descending, bool nullsFirst)
    {
        if (descending)
        {
            code = span - code;
        }

        return nullsFirst ? code + 1 : code;
    }

    /// <summary>
    /// The code a NULL takes: below every rank when nulls come first, above every rank when they
    /// come last. Either way it is one more code than the ranks themselves need.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint NullCode(int cardinality, bool nullsFirst) =>
        nullsFirst ? 0u : (uint)cardinality;

    /// <summary>The same rule for a code narrowed to <c>0..span</c> rather than a rank.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong NullCode(ulong span, bool nullsFirst) => nullsFirst ? 0UL : span + 1;

    /// <summary>The narrowest unsigned field that holds every code up to <paramref name="largest"/>.</summary>
    public static int BitsFor(ulong largest) => 64 - BitOperations.LeadingZeroCount(largest);

    /// <summary>
    /// How many distinct codes <see cref="Code"/> and <see cref="NullCode"/> can produce for a
    /// column of this cardinality — the cardinality, and one more where the column has NULLs.
    /// </summary>
    public static int CodeCount(int cardinality, bool hasNulls) => cardinality + (hasNulls ? 1 : 0);

    /// <summary>The narrowest unsigned field that holds <paramref name="codeCount"/> distinct codes.</summary>
    public static int BitsForCodes(int codeCount) =>
        codeCount <= 1 ? 0 : BitsFor((ulong)(codeCount - 1));
}
