using Chalk.Sources.Keys;

namespace Chalk.Execution.Tests;

/// <summary>
/// The key encoders shared by the POCO permutation index and the blocking sort (D267 a,
/// <c>docs/design/41-blocking-sort.md</c> §2). One property carries all of them: the code order is
/// the value order of <c>02-ir.md</c> §3, so a key encoded this way may be sorted as a number.
/// </summary>
public sealed class SortKeyEncodingTests
{
    private static readonly sbyte[] Int8Values = [sbyte.MinValue, -3, -1, 0, 1, 3, sbyte.MaxValue];

    private static readonly short[] Int16Values = [short.MinValue, -300, -1, 0, 1, 300, short.MaxValue];

    private static readonly int[] Int32Values = [int.MinValue, -70_000, -1, 0, 1, 70_000, int.MaxValue];

    private static readonly long[] Int64Values =
        [long.MinValue, -5_000_000_000L, -1, 0, 1, 5_000_000_000L, long.MaxValue];

    private static readonly float[] FloatValues =
    [
        float.NegativeInfinity, -3.5f, -float.Epsilon, -0f, 0f, float.Epsilon, 3.5f,
        float.PositiveInfinity, float.NaN,
    ];

    private static readonly double[] DoubleValues =
    [
        double.NegativeInfinity, -3.5d, -double.Epsilon, -0d, 0d, double.Epsilon, 3.5d,
        double.PositiveInfinity, double.NaN,
    ];

    [Fact]
    public void Signed_integers_encode_in_value_order()
    {
        AssertAscending(Int8Values, SByteEncoding.EncodeAscending);
        AssertAscending(Int16Values, Int16Encoding.EncodeAscending);
        AssertAscending(Int32Values, Int32Encoding.EncodeAscending);
        AssertAscending(Int64Values, Int64Encoding.EncodeAscending);
    }

    [Fact]
    public void Unsigned_integers_encode_as_themselves()
    {
        Assert.Equal(byte.MaxValue, ByteEncoding.EncodeAscending(byte.MaxValue));
        Assert.Equal(ushort.MaxValue, UInt16Encoding.EncodeAscending(ushort.MaxValue));
        Assert.Equal(uint.MaxValue, UInt32Encoding.EncodeAscending(uint.MaxValue));
        Assert.Equal(ulong.MaxValue, UInt64Encoding.EncodeAscending(ulong.MaxValue));
        AssertAscending<byte, byte>([0, 1, 200, byte.MaxValue], ByteEncoding.EncodeAscending);
        AssertAscending<ulong, ulong>([0, 1, long.MaxValue, ulong.MaxValue], UInt64Encoding.EncodeAscending);
    }

    [Fact]
    public void Floating_point_encodes_in_value_order_with_NaN_last()
    {
        AssertAscending(FloatValues, SingleEncoding.EncodeAscending, allowEqual: true);
        AssertAscending(DoubleValues, DoubleEncoding.EncodeAscending, allowEqual: true);

        // The two departures from IEEE that 02-ir.md §3 asks for.
        Assert.Equal(uint.MaxValue, SingleEncoding.EncodeAscending(float.NaN));
        Assert.Equal(ulong.MaxValue, DoubleEncoding.EncodeAscending(double.NaN));
        Assert.Equal(SingleEncoding.EncodeAscending(0f), SingleEncoding.EncodeAscending(-0f));
        Assert.Equal(DoubleEncoding.EncodeAscending(0d), DoubleEncoding.EncodeAscending(-0d));
    }

    [Fact]
    public void Instants_encode_in_instant_order()
    {
        DateTime[] times =
        [
            new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc),
            new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ];
        AssertAscending(times, DateTimeEncoding.EncodeAscending);

        // Two spellings of one instant encode to one code, which is what makes the offset ignorable.
        var utc = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        var plusTwo = new DateTimeOffset(2026, 9, 16, 14, 0, 0, TimeSpan.FromHours(2));
        Assert.Equal(
            DateTimeOffsetEncoding.EncodeAscending(utc),
            DateTimeOffsetEncoding.EncodeAscending(plusTwo));
    }

    [Fact]
    public void The_symbol_limit_grows_with_the_rows_between_a_floor_and_a_ceiling()
    {
        Assert.Equal(256, SortSymbolRanks.Limit(0));
        Assert.Equal(256, SortSymbolRanks.Limit(2_000));
        Assert.Equal(1_250, SortSymbolRanks.Limit(10_000));
        Assert.Equal(ushort.MaxValue, SortSymbolRanks.Limit(int.MaxValue));
    }

    [Fact]
    public void Ranks_follow_the_value_order_whatever_order_the_ids_were_seen_in()
    {
        // Ids in first-seen order; the values they stand for are out of order on purpose.
        string[] symbols = ["delta", "alpha", "charlie", "bravo"];
        var order = new int[symbols.Length];
        var ranks = new int[symbols.Length];

        SortSymbolRanks.RankBySortedValues(order, ranks, new OrdinalById(symbols));

        Assert.Equal([3, 0, 2, 1], ranks);
        Assert.Equal([1, 3, 2, 0], order);
    }

    [Theory]
    [InlineData(false, false, 0u, 1u, 2u, 3u)]  // asc, nulls last: the ranks, NULL above them
    [InlineData(false, true, 1u, 2u, 3u, 0u)]   // asc, nulls first: the ranks shifted up by one
    [InlineData(true, false, 2u, 1u, 0u, 3u)]   // desc, nulls last: the ranks complemented
    [InlineData(true, true, 3u, 2u, 1u, 0u)]    // desc, nulls first
    public void A_symbol_code_places_the_direction_and_the_NULL(
        bool descending, bool nullsFirst, uint first, uint middle, uint last, uint whenNull)
    {
        const int Cardinality = 3;
        Assert.Equal(first, SortSymbolRanks.Code(0, Cardinality, descending, nullsFirst));
        Assert.Equal(middle, SortSymbolRanks.Code(1, Cardinality, descending, nullsFirst));
        Assert.Equal(last, SortSymbolRanks.Code(2, Cardinality, descending, nullsFirst));
        Assert.Equal(whenNull, SortSymbolRanks.NullCode(Cardinality, nullsFirst));
    }

    [Fact]
    public void A_codes_field_is_as_wide_as_the_codes_need_and_no_wider()
    {
        Assert.Equal(4, SortSymbolRanks.CodeCount(3, hasNulls: true));
        Assert.Equal(3, SortSymbolRanks.CodeCount(3, hasNulls: false));
        Assert.Equal(0, SortSymbolRanks.BitsForCodes(1));
        Assert.Equal(1, SortSymbolRanks.BitsForCodes(2));
        Assert.Equal(2, SortSymbolRanks.BitsForCodes(3));
        Assert.Equal(2, SortSymbolRanks.BitsForCodes(4));
        Assert.Equal(3, SortSymbolRanks.BitsForCodes(5));
        Assert.Equal(16, SortSymbolRanks.BitsForCodes(ushort.MaxValue + 1));
    }

    private static void AssertAscending<TValue, TCode>(
        TValue[] values, Func<TValue, TCode> encode, bool allowEqual = false)
        where TCode : IComparable<TCode>
    {
        for (var i = 1; i < values.Length; i++)
        {
            var comparison = encode(values[i - 1]).CompareTo(encode(values[i]));
            Assert.True(
                allowEqual ? comparison <= 0 : comparison < 0,
                $"{values[i - 1]} encodes at or above {values[i]}");
        }
    }

    /// <summary>Orders symbol ids by the string each one stands for, as the index's comparer does.</summary>
    private readonly struct OrdinalById(string[] symbols) : IComparer<int>
    {
        public int Compare(int left, int right) =>
            string.CompareOrdinal(symbols[left], symbols[right]);
    }
}
