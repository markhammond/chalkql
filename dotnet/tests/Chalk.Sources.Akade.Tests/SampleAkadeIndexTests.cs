using Akade.IndexedSet;
using Chalk.Catalog;
using Chalk.Sample.AkadeIndexedSet;
using IndexKind = Chalk.Ir.IndexKind;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// The sample adapter's ordered path: yielded as Akade produces it, in key order, a consumer that
/// stops early paying only for what it read, and an out-of-order row refused by name.
/// </summary>
public sealed class SampleAkadeIndexTests
{
    private sealed record Row(int Id, int Key);

    // Static, so Akade records the same accessor name at registration and at query time.
    private static readonly Func<Row, int> KeyOf = row => row.Key;

    private static int _keyCalls;
    private static readonly Func<Row, int> CountingKeyOf = row =>
    {
        _keyCalls++;
        return row.Key;
    };

    private static IndexDescriptor Descriptor => new()
    {
        Name = "ix_rows_key",
        Kind = IndexKind.Ordered,
        Columns = [1],
    };

    private static readonly AkadeKey<Row, int> Bounds = new((bounds, _) => (int)bounds[0]!);

    private static IReadOnlyList<Row> Shuffled(int count)
    {
        var random = new Random(7);
        return Enumerable.Range(0, count).Select(i => new Row(i, i)).OrderBy(_ => random.Next()).ToArray();
    }

    private static IndexKeyRange From(int lower) => new() { Lower = [lower], Upper = [] };

    [Fact]
    public void A_bounded_lookup_arrives_in_key_order()
    {
        var set = Shuffled(1_000).ToIndexedSet().WithRangeIndex(KeyOf).Build();
        var index = new AkadeIndex<Row, int>(Descriptor, set, KeyOf, "KeyOf", Bounds, Comparer<int>.Default);

        var keys = index.Lookup(From(500)).Select(r => r.Key).ToList();

        Assert.Equal(Enumerable.Range(500, 500), keys);
    }

    [Fact]
    public void An_unbounded_lookup_is_the_whole_table_in_key_order()
    {
        var set = Shuffled(1_000).ToIndexedSet().WithRangeIndex(KeyOf).Build();
        var index = new AkadeIndex<Row, int>(Descriptor, set, KeyOf, "KeyOf", Bounds, Comparer<int>.Default);

        var keys = index.Lookup(IndexKeyRange.All).Select(r => r.Key).ToList();

        Assert.Equal(Enumerable.Range(0, 1_000), keys);
    }

    [Fact]
    public void A_consumer_that_stops_after_one_row_pays_for_one_row()
    {
        var set = Shuffled(100_000).ToIndexedSet().WithRangeIndex(CountingKeyOf).Build();
        var index = new AkadeIndex<Row, int>(
            Descriptor, set, CountingKeyOf, "CountingKeyOf", Bounds, Comparer<int>.Default);
        _keyCalls = 0;

        var first = index.Lookup(From(0)).First();

        Assert.Equal(0, first.Key);
        // The guard reads the first row's key, and Akade may read it once more; a sort of the range
        // would have read all hundred thousand before yielding anything.
        Assert.InRange(_keyCalls, 1, 2);
    }

    [Fact]
    public void An_out_of_order_enumeration_is_refused_by_name()
    {
        // Akade orders the index ascending; the adapter is told the key order is descending, so the
        // second row Akade yields is out of order as far as Chalk's contract is concerned. The
        // unbounded shape is the one that reaches the guard whatever the two orders are: a bounded
        // one is a Range between the index's own extremes, and those come back in Akade's order.
        var descending = Comparer<int>.Create((a, b) => b.CompareTo(a));
        var set = Shuffled(10).ToIndexedSet().WithRangeIndex(KeyOf).Build();
        var index = new AkadeIndex<Row, int>(Descriptor, set, KeyOf, "KeyOf", Bounds, descending);

        var refusal = Assert.Throws<SourceContractException>(
            () => index.Lookup(IndexKeyRange.All).ToList());

        Assert.Contains("KeyOf", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("ix_rows_key", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("after", refusal.Message, StringComparison.Ordinal);
    }
}
