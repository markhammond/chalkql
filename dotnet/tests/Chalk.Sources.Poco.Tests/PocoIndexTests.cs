using Chalk.Catalog;
using IndexKind = Chalk.Ir.IndexKind;
using SortDirection = Chalk.Ir.SortDirection;
using StatisticsLevel = Chalk.Ir.StatisticsLevel;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// The built-in index (D35): what it builds, what it refuses, and what it hands back. The bar for
/// every lookup here is the full scan it replaces — a range's answer is the rows a filter would keep.
/// </summary>
public sealed class PocoIndexTests
{
    private sealed record Point(string Group, int? Rank, double Value);

    private static readonly Point[] Points =
    [
        new("a", 1, 1.5),
        new("a", 3, 2.5),
        new("b", 2, double.NaN),
        new("b", 5, 4.5),
        new("c", null, 5.5),
        new("a", 2, 6.5),
        new("c", 4, 7.5),
    ];

    private static PocoSource Source(Action<PocoTableBuilder<Point>> configure) =>
        new PocoSourceBuilder("mem").AddTable("points", Points, configure).Build();

    private static IndexDescriptor Descriptor(PocoSource source, string index) =>
        PocoTestSupport.Describe(source, "points").Indexes.Single(
            i => string.Equals(i.Name, index, StringComparison.Ordinal));

    [Fact]
    public void Index_is_named_after_its_table_and_columns()
    {
        var source = Source(t => t.Index(p => p.Group, p => p.Rank));
        var index = Descriptor(source, "ix_points_Group_Rank");

        Assert.Equal(IndexKind.Ordered, index.Kind);
        Assert.Equal([0, 1], index.Columns);
        Assert.False(index.Unique);
        Assert.Empty(index.Directions);
        Assert.Equal(SortDirection.AscNullsLast, index.DirectionAt(0));
    }

    [Fact]
    public void Unique_index_names_the_two_rows_that_collide()
    {
        var error = Assert.Throws<CatalogVerificationException>(
            () => Source(t => t.UniqueIndex(p => p.Group)));

        Assert.Contains("unique index", error.Message, StringComparison.Ordinal);
        Assert.Contains("share the key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unique_index_over_a_key_that_holds_builds()
    {
        var source = Source(t => t.UniqueIndex(p => p.Group, p => p.Value));
        Assert.True(Descriptor(source, "ix_points_Group_Value").Unique);
    }

    [Fact]
    public void Equality_lookup_returns_the_rows_a_filter_would_keep()
    {
        var index = Build(t => t.Index(p => p.Group));
        Assert.Equal(
            Points.Where(p => p.Group == "a").ToArray(),
            index.Lookup(IndexKeyRange.Equality("a")).ToArray());
    }

    [Fact]
    public void Ordered_lookup_yields_rows_in_key_order()
    {
        var index = Build(t => t.Index(p => p.Value));
        var values = index.Lookup(IndexKeyRange.All).Select(p => p.Value).ToArray();

        // NaN sorts last, as 02-ir.md §3 requires; everything else ascends.
        Assert.Equal([1.5, 2.5, 4.5, 5.5, 6.5, 7.5, double.NaN], values);
    }

    [Fact]
    public void Range_lookup_honours_inclusivity()
    {
        var index = Build(t => t.Index(p => p.Value));

        var inclusive = new IndexKeyRange
        {
            Lower = [2.5], LowerInclusive = true, Upper = [5.5], UpperInclusive = true,
        };
        var exclusive = new IndexKeyRange
        {
            Lower = [2.5], LowerInclusive = false, Upper = [5.5], UpperInclusive = false,
        };

        Assert.Equal([2.5, 4.5, 5.5], index.Lookup(inclusive).Select(p => p.Value));
        Assert.Equal([4.5], index.Lookup(exclusive).Select(p => p.Value));
    }

    [Fact]
    public void Open_range_stops_before_the_nulls()
    {
        // `rank >= 2` must not match the NULL rank: a comparison with NULL is unknown, and the
        // range stands for that comparison (§3). NULLs sort last, so a naive scan to the end would.
        var index = Build(t => t.Index(p => p.Rank));
        var range = new IndexKeyRange { Lower = [2], LowerInclusive = true, Upper = [] };

        Assert.Equal([2, 2, 3, 4, 5], index.Lookup(range).Select(p => p.Rank!.Value));
        Assert.Equal(Points.Count(p => p.Rank >= 2), index.Lookup(range).Count());
    }

    [Fact]
    public void Open_range_under_nulls_first_stops_after_the_nulls()
    {
        var index = Build(t => t.Index(
            "ix_desc", IndexKind.Ordered, unique: false, [SortDirection.AscNullsFirst], p => p.Rank));
        var range = new IndexKeyRange { Lower = [], Upper = [3], UpperInclusive = true };

        Assert.Equal([1, 2, 2, 3], index.Lookup(range).Select(p => p.Rank!.Value));
    }

    [Fact]
    public void Unbounded_range_returns_every_row_including_nulls()
    {
        var index = Build(t => t.Index(p => p.Rank));
        Assert.Equal(Points.Length, index.Lookup(IndexKeyRange.All).Count());
    }

    [Fact]
    public void Prefix_equality_with_a_range_on_the_next_column()
    {
        var index = Build(t => t.Index(p => p.Group, p => p.Value));
        var range = new IndexKeyRange
        {
            Lower = ["a", 2.0], LowerInclusive = true, Upper = ["a"], UpperInclusive = true,
        };

        Assert.Equal([2.5, 6.5], index.Lookup(range).Select(p => p.Value));
    }

    [Fact]
    public void Collation_backed_index_builds_no_permutation()
    {
        // Sorted the way Chalk orders values, NaN last — which is not what OrderBy does, so the NaN
        // row is left out rather than quietly failing the collation check.
        var ordered = Points.Where(p => !double.IsNaN(p.Value)).OrderBy(p => p.Value).ToArray();
        var source = new PocoSourceBuilder("mem")
            .AddTable("points", ordered, t => t.OrderedBy(p => p.Value).Index(p => p.Value))
            .Build();

        var index = (PermutationIndex<Point>)Index(source, "points", "ix_points_Value");
        Assert.True(index.IsCollationBacked);
        Assert.Equal(0, index.BytesPerRow);
        Assert.Equal([4.5], index.Lookup(IndexKeyRange.Equality(4.5)).Select(p => p.Value));
    }

    [Fact]
    public void Permutation_index_costs_one_int_per_row()
    {
        var index = Build(t => t.Index(p => p.Value));
        Assert.Equal(sizeof(int), index.BytesPerRow);
        Assert.False(index.IsCollationBacked);
    }

    [Fact]
    public void Distinct_counts_come_off_the_sorted_order()
    {
        var index = Build(t => t.Index(p => p.Group, p => p.Rank));
        Assert.Equal(3, index.DistinctCount(1));
        Assert.Equal(Points.Length, index.DistinctCount(2));
        Assert.Null(index.DistinctCount(3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Lookup_gathers_through_the_chunk_writers_at_any_batch_size(int batchSize)
    {
        var source = Source(t => t.Index(p => p.Group));
        var stats = new ExecutionStats();
        var rows = await LookupAsync(
            source, "ix_points_Group", [IndexKeyRange.Equality("a")], batchSize, stats);

        Assert.Equal(["a", "a", "a"], rows.Select(r => (string)r[0]!));

        // Equal keys keep their position order: the sort breaks ties by position, so a permutation
        // is the same on every run (and the rows here are the list's 1st, 2nd and 6th).
        Assert.Equal([1, 3, 2], rows.Select(r => (int)r[1]!));

        // A pure lookup reads exactly what it produces — the M2 instrumentation criterion.
        Assert.Equal(3, stats.RowsScanned);
    }

    [Fact]
    public async Task Lookup_of_several_ranges_de_duplicates()
    {
        var source = Source(t => t.Index(p => p.Group));
        var rows = await LookupAsync(
            source,
            "ix_points_Group",
            [IndexKeyRange.Equality("a"), IndexKeyRange.Equality("a"), IndexKeyRange.Equality("c")],
            batchSize: 2);

        Assert.Equal(5, rows.Count);
        Assert.Equal(["a", "a", "a", "c", "c"], rows.Select(r => (string)r[0]!));
    }

    [Fact]
    public async Task Lookup_with_no_ranges_returns_nothing()
    {
        var source = Source(t => t.Index(p => p.Group));
        Assert.Empty(await LookupAsync(source, "ix_points_Group", [], batchSize: 8));
    }

    [Fact]
    public async Task Lookup_names_an_index_the_table_does_not_have()
    {
        var source = Source(t => t.Index(p => p.Group));
        var error = await Assert.ThrowsAsync<SourceContractException>(
            () => LookupAsync(source, "ix_nope", [IndexKeyRange.All], batchSize: 8));

        Assert.Contains("ix_points_Group", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hash_index_refuses_a_range()
    {
        var source = Source(t => t.Index("ix_hash", IndexKind.Hash, unique: false, p => p.Group));
        var range = new IndexKeyRange { Lower = ["a"], Upper = [] };

        var error = await Assert.ThrowsAsync<SourceContractException>(
            () => LookupAsync(source, "ix_hash", [range], batchSize: 8));

        Assert.Contains("equality only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Source_without_an_index_throws_the_unsupported_default()
    {
        var source = new PocoSourceBuilder("mem").AddTable("points", Points).Build();
        await Assert.ThrowsAsync<SourceContractException>(
            () => LookupAsync(source, "ix_points_Group", [IndexKeyRange.All], batchSize: 8));
    }

    [Fact]
    public void Basic_statistics_are_computed_for_free()
    {
        var source = Source(t => t.Index(p => p.Group));
        var table = PocoTestSupport.Describe(source, "points");

        Assert.Equal(Chalk.Ir.RowCountKind.Exact, table.RowCountKind);

        var group = table.Columns[0].Statistics;
        Assert.Equal(StatisticsLevel.Basic, group.Level);
        Assert.Equal(3, group.DistinctCount);
        Assert.Equal(0, group.NullCount);
        Assert.Equal("a", group.Min);
        Assert.Equal("c", group.Max);

        var rank = table.Columns[1].Statistics;
        Assert.Equal(1, rank.NullCount);
        Assert.Equal(1, rank.Min);
        Assert.Equal(5, rank.Max);

        // Only index and collation key columns get an exact distinct count (§2).
        Assert.Equal(-1, rank.DistinctCount);
    }

    [Fact]
    public void Statistics_can_be_turned_off_entirely()
    {
        var source = Source(t => t.Index(p => p.Group).Statistics(StatisticsLevel.Unknown));
        var statistics = PocoTestSupport.Describe(source, "points").Columns[0].Statistics;

        Assert.Equal(StatisticsLevel.Unknown, statistics.Level);
        Assert.Equal(-1, statistics.DistinctCount);
        Assert.Null(statistics.Min);
    }

    [Fact]
    public void Histogram_statistics_cover_every_row()
    {
        var source = Source(t => t.Statistics(StatisticsLevel.Histogram, buckets: 3));
        var value = PocoTestSupport.Describe(source, "points").Columns[2].Statistics;

        Assert.Equal(StatisticsLevel.Histogram, value.Level);
        Assert.NotEmpty(value.Histogram);
        Assert.Equal(Points.Length, value.Histogram.Sum(b => b.Count));
        for (var b = 1; b < value.Histogram.Count; b++)
        {
            Assert.True(
                SourceValueOrder.CompareValues(value.Histogram[b - 1].Upper!, value.Histogram[b].Upper!) < 0,
                "histogram bucket bounds must ascend strictly");
        }
        Assert.NotEmpty(value.FrequentValues);
    }

    [Fact]
    public void Host_supplied_statistics_win_over_the_computed_ones()
    {
        var source = Source(t => t.Statistics(
            p => p.Group,
            new ColumnStatistics { Level = StatisticsLevel.Basic, DistinctCount = 42 }));

        Assert.Equal(42, PocoTestSupport.Describe(source, "points").Columns[0].Statistics.DistinctCount);
    }

    [Fact]
    public void A_collection_that_only_enumerates_scans_and_declares_no_collation()
    {
        var set = new HashSet<Point>(Points);
        var source = new PocoSourceBuilder("mem").AddTable("points", (IReadOnlyCollection<Point>)set).Build();
        var table = PocoTestSupport.Describe(source, "points");

        Assert.Equal(Points.Length, table.RowCount);
        Assert.Empty(table.Collations);

        var error = Assert.Throws<CatalogValidationException>(() =>
            new PocoSourceBuilder("mem")
                .AddTable("points", (IReadOnlyCollection<Point>)set, t => t.OrderedBy(p => p.Value))
                .Build());
        Assert.Contains("IReadOnlyCollection", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_collection_that_only_enumerates_produces_every_row()
    {
        var set = new HashSet<Point>(Points);
        var source = new PocoSourceBuilder("mem").AddTable("points", (IReadOnlyCollection<Point>)set).Build();
        var values = await PocoTestSupport.ColumnAsync(source, "points", column: 2, batchSize: 3);

        Assert.Equal(
            Points.Select(p => p.Value).OrderBy(v => v).ToArray(),
            values.Select(v => (double)v!).OrderBy(v => v).ToArray());
    }

    [Fact]
    public void A_built_in_index_needs_a_list()
    {
        var set = new HashSet<Point>(Points);
        var error = Assert.Throws<CatalogValidationException>(() =>
            new PocoSourceBuilder("mem")
                .AddTable("points", (IReadOnlyCollection<Point>)set, t => t.Index(p => p.Group))
                .Build());

        Assert.Contains("IReadOnlyList", error.Message, StringComparison.Ordinal);
    }

    private static PermutationIndex<Point> Build(Action<PocoTableBuilder<Point>> configure) =>
        (PermutationIndex<Point>)IndexOf(Source(configure));

    private static IPocoIndex<Point> IndexOf(PocoSource source) =>
        Index(source, "points", PocoTestSupport.Describe(source, "points").Indexes[0].Name);

    private static IPocoIndex<Point> Index(PocoSource source, string table, string name) =>
        source.FindIndex<Point>(table, name)
        ?? throw new InvalidOperationException($"no index '{name}' on '{table}'");

    private static async Task<List<object?[]>> LookupAsync(
        PocoSource source,
        string index,
        IReadOnlyList<IndexKeyRange> ranges,
        int batchSize,
        ExecutionStats? stats = null)
    {
        var descriptor = PocoTestSupport.Describe(source, "points");
        var projection = Enumerable.Range(0, descriptor.Columns.Count).ToArray();
        var request = new IndexLookupRequest
        {
            Table = "points",
            Index = index,
            Ranges = ranges,
            Projection = projection,
            OutputSchema = ArrowTypeMapping.ToArrowSchema(descriptor, projection),
            BatchSize = batchSize,
        };

        var rows = new List<object?[]>();
        await foreach (var batch in source.IndexLookupAsync(
            request, PocoTestSupport.Context(stats), TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    rows.Add([.. Enumerable.Range(0, batch.ColumnCount)
                        .Select(c => PocoTestSupport.Read(batch.Column(c), i))]);
                }
            }
        }

        return rows;
    }
}
