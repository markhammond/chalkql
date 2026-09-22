using Akade.IndexedSet;
using Chalk.Catalog;
using Chalk.Sources.Poco;
using Chalk.Tests;
using IndexKind = Chalk.Ir.IndexKind;
using IndexReversal = Chalk.Ir.IndexReversal;
using SortDirection = Chalk.Ir.SortDirection;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// D283 — the Akade ordered index read from its last row to its first.
/// </summary>
/// <remarks>
/// A range with no upper bound is what Akade's public surface serves backwards without a copy:
/// <c>OrderByDescending</c> starts at the top and every row down to the lower bound is wanted, so
/// nothing is skipped and nothing is buffered. A range with an upper bound is refused, because
/// reaching it backwards would mean skipping every row above it or buffering the matched range —
/// the copy D277 removed.
/// </remarks>
public sealed class AkadeReversedLookupTests
{
    private sealed record Row(int Key, string Tag);

    private const string AkadeIndexName = "x => x.Key";

    private static readonly IndexDescriptor Descriptor = new()
    {
        Name = "ix_rows_key",
        Kind = IndexKind.Ordered,
        Columns = [0],
        Directions = [SortDirection.AscNullsLast],
        Reversal = IndexReversal.OpenAbove,
    };

    private static IndexedSet<Row> Build(IEnumerable<int> keys)
    {
        var rows = new List<Row>();
        foreach (var key in keys)
        {
            rows.Add(new Row(key, $"r{key}"));
        }

        return rows.ToIndexedSet().WithRangeIndex(x => x.Key).Build();
    }

    /// <summary>Ten rows, added in an order that is not their key order.</summary>
    private static IndexedSet<Row> Shuffled() => Build([7, 1, 9, 3, 5, 2, 8, 4, 6, 0]);

    private static AkadeScalarIndex<Row, int> Index(
        Func<Row, int>? key = null, IndexedSet<Row>? set = null) =>
        new(Descriptor, set ?? Shuffled(), key ?? (x => x.Key), AkadeIndexName, "akade", "rows");

    private static IndexKeyRange From(int? lower, bool inclusive = true) => new()
    {
        Lower = lower is null ? [] : [lower.Value],
        LowerInclusive = inclusive,
        Upper = [],
    };

    private static int[] Keys(IEnumerable<Row> rows) => [.. rows.Select(r => r.Key)];

    [Fact]
    public void An_ordered_index_declares_that_an_open_range_reads_backwards()
    {
        Assert.Equal(IndexReversal.OpenAbove, Index().Reversal);
    }

    [Fact]
    public void The_whole_index_backwards_is_the_whole_index_in_reverse_key_order()
    {
        Assert.Equal(
            [9, 8, 7, 6, 5, 4, 3, 2, 1, 0],
            Keys(Index().LookupReversed(IndexKeyRange.All)));
    }

    [Fact]
    public void A_lower_bounded_range_backwards_stops_at_the_bound()
    {
        Assert.Equal([9, 8, 7, 6], Keys(Index().LookupReversed(From(6))));
        Assert.Equal([9, 8, 7], Keys(Index().LookupReversed(From(6, inclusive: false))));
    }

    [Fact]
    public void A_reversed_lookup_is_the_forward_one_in_reverse()
    {
        var index = Index();

        Assert.Equal(
            Keys(index.Lookup(From(3))).Reverse(),
            Keys(index.LookupReversed(From(3))));
    }

    /// <summary>
    /// The point of the decision: a consumer that stops after one row reads one row. Counted through
    /// the key accessor, which the guard calls once per row, so there is no timing assertion in it.
    /// </summary>
    [Fact]
    public void A_consumer_that_stops_after_one_row_pulls_exactly_one()
    {
        var pulled = 0;
        var index = Index(row =>
        {
            pulled++;
            return row.Key;
        });

        var last = index.LookupReversed(From(0)).First();

        Assert.Equal(9, last.Key);
        Assert.Equal(1, pulled);
    }

    [Fact]
    public void An_upper_bounded_range_is_refused_by_name()
    {
        var range = new IndexKeyRange { Lower = [2], Upper = [6] };

        var failure = Assert.Throws<SourceContractException>(
            () => Index().LookupReversed(range).ToList());

        Assert.Equal("akade", failure.SourceId);
        Assert.Equal("rows", failure.Table);
        Assert.Contains(AkadeIndexName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("no upper bound", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard, reversed: a row that arrives out of the order a reversed lookup claims fails by
    /// name rather than being sorted around, exactly as the forward one does.
    /// </summary>
    [Fact]
    public void The_reversed_guard_names_the_akade_index_when_a_row_arrives_out_of_order()
    {
        var misordered = new[] { new Row(9, "a"), new Row(3, "b"), new Row(5, "c") };

        var failure = Assert.Throws<SourceContractException>(
            () => Keys(Index().InReverseKeyOrder(misordered)));

        Assert.Contains(AkadeIndexName, failure.Message, StringComparison.Ordinal);
        Assert.Contains(Descriptor.Name, failure.Message, StringComparison.Ordinal);
        Assert.Contains("'5' after '3'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("read backwards", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the reversed path allocates for the enumeration and not for the rows, which is the same
    /// gate the forward one has.
    /// </summary>
    [Fact]
    public async Task The_reversed_enumeration_allocates_nothing_per_row()
    {
        var set = Build(Enumerable.Range(0, 1024));
        var index = Index(set: set);

        var (whole, wholeRows) = await AllocationProbe.SteadyStateAsync(
            () => Task.FromResult(Count(index.LookupReversed(From(0)))));
        var (few, fewRows) = await AllocationProbe.SteadyStateAsync(
            () => Task.FromResult(Count(index.LookupReversed(From(1016)))));

        Assert.Equal(1024, wholeRows);
        Assert.Equal(8, fewRows);
        Assert.True(
            whole <= few + 256,
            $"{whole} bytes for {wholeRows} rows against {few} for {fewRows}: the difference is "
            + "per-row allocation.");
    }

    private static int Count(IEnumerable<Row> rows)
    {
        var seen = 0;
        foreach (var row in rows)
        {
            seen += row.Key == int.MinValue ? 0 : 1;
        }

        return seen;
    }

    private sealed record Trade(int Id, long Ts, decimal Price);

    /// <summary>
    /// The source declares what the adapter serves, so a host gets the reversal without saying so.
    /// </summary>
    [Fact]
    public void The_catalog_carries_the_reversal_the_adapter_serves()
    {
        var rows = new[] { new Trade(1, 100, 1m), new Trade(2, 200, 2m) };
        var set = rows.ToIndexedSet(x => x.Id)
            .WithRangeIndex(x => x.Ts)
            .WithIndex(x => x.Price)
            .Build();

        var indexes = AkadeSource
            .From("trades", set)
            .TableName("trades")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .Build()
            .DescribeSchema()
            .Tables
            .Single()
            .Indexes
            .ToDictionary(i => i.Name, StringComparer.Ordinal);

        Assert.Equal(IndexReversal.OpenAbove, indexes["x => x.Ts"].Reversal);

        // A hash index has no order to reverse.
        Assert.Equal(IndexReversal.Unspecified, indexes["x => x.Price"].Reversal);
        Assert.Equal(IndexReversal.Unspecified, indexes["x => x.Id"].Reversal);
    }
}
