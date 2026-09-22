using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using IndexReversal = Chalk.Ir.IndexReversal;

namespace Chalk.Integration.Tests;

/// <summary>
/// D283 — the built-in indexes read backwards, through the conformance kit that an adapter author
/// runs: every range shape, answered forwards and backwards and compared with a full scan.
/// </summary>
/// <remarks>
/// A permutation index walks a contiguous slice, so it can be read from either end and declares
/// <see cref="IndexReversal.Any"/>; a clustered index is a permutation with a copy beside it and
/// declares the same. The kit checks both halves of the claim: the rows a reversed lookup returns
/// are the rows the forward one returns, and they arrive in the opposite order.
/// </remarks>
public sealed class ReversedIndexTests
{
    private sealed record Point(string Group, int Rank, double Value);

    private static readonly Point[] Points =
    [
        new("a", 1, 1.5),
        new("a", 3, 2.5),
        new("b", 2, 3.5),
        new("b", 5, 4.5),
        new("c", 4, 5.5),
        new("a", 2, 6.5),
        new("c", 6, 7.5),
        new("b", 1, 8.5),
    ];

    private static PocoSource Source() =>
        new PocoSourceBuilder("mem")
            .AddTable("points", Points, t => t
                .Index("ix_points_group_rank", Chalk.Ir.IndexKind.Ordered, unique: false,
                    p => p.Group, p => p.Rank)
                .ClusteredIndex("ix_points_rank", unique: false, directions: [], p => p.Rank))
            .Build();

    private static IPocoIndex<Point> Index(string name) =>
        Source().FindIndex<Point>("points", name)
        ?? throw new InvalidOperationException($"no index '{name}'");

    [Fact]
    public void A_permutation_index_declares_that_any_range_reads_backwards()
    {
        var index = Index("ix_points_group_rank");

        Assert.Equal(IndexReversal.Any, index.Descriptor.Reversal);
        Assert.IsAssignableFrom<IReversiblePocoIndex<Point>>(index);
    }

    [Fact]
    public void A_clustered_index_declares_the_same()
    {
        Assert.Equal(IndexReversal.Any, Index("ix_points_rank").Descriptor.Reversal);
    }

    [Fact]
    public void Every_range_shape_agrees_with_a_full_scan_read_backwards()
    {
        var report = PocoIndexConformance.Verify(
            Index("ix_points_group_rank"),
            Points,
            [p => p.Group, p => p.Rank]);

        Assert.True(report.Ranges >= 20, report.ToString());
    }

    [Fact]
    public void The_clustered_index_agrees_too()
    {
        var report = PocoIndexConformance.Verify(Index("ix_points_rank"), Points, [p => p.Rank]);

        Assert.True(report.Ranges >= 20, report.ToString());
    }

    /// <summary>
    /// The shape the whole decision is for: the last row of a range, without reading the ones before
    /// it. Stated as rows rather than as time, so there is no timing assertion in it.
    /// </summary>
    [Fact]
    public void A_reversed_lookup_starts_at_the_last_row()
    {
        var index = (IReversiblePocoIndex<Point>)Index("ix_points_group_rank");

        var last = index.LookupReversed(IndexKeyRange.All).First();
        var first = index.Lookup(IndexKeyRange.All).First();

        Assert.Equal(("c", 6), (last.Group, last.Rank));
        Assert.Equal(("a", 1), (first.Group, first.Rank));
    }
}
