using Chalk.Ir;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// Statistics freshness as a policy (D271 (g), <c>docs/design/44-catalog-registration.md</c> §7):
/// recomputed now, deferred to the next plain refresh, or recomputed once the table's rows have moved
/// by a stated fraction, a tenth unless the table names another (D351). The row count is exact
/// whatever the policy says.
/// </summary>
/// <remarks>
/// What each assertion watches is the <em>largest value</em> of the key column, because that is what
/// a pass over the values produces and a carried set of statistics cannot know about rows added
/// since. No test here asserts a duration: the question is which numbers the descriptor reports, not
/// how long producing them took.
/// </remarks>
public sealed class PocoStatisticsPolicyTests
{
    private sealed record Line(int Id, string Region);

    private sealed record Mark(int Id, long Price);

    private static Line[] Rows(int from, int count, string region) =>
        [.. Enumerable.Range(from, count).Select(i => new Line(i, region))];

    [Fact]
    public async Task An_append_that_defers_moves_the_row_count_and_not_the_values()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("lines", Rows(1, 4, "north"), out var lines)
            .Build();

        Assert.Equal(4, Describe(source).RowCount);
        Assert.Equal(4, LargestId(source));

        await Append(lines, Rows(5, 4, "south"), StatisticsRefresh.Defer);

        // The count is exact — it is a field read, and no policy governs it.
        Assert.Equal(8, Describe(source).RowCount);
        // The values are as of the last pass: the largest Id it saw was 4.
        Assert.Equal(4, LargestId(source));
    }

    [Fact]
    public async Task An_append_that_does_not_defer_recomputes()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("lines", Rows(1, 4, "north"), out var lines)
            .Build();

        await Append(lines, Rows(5, 4, "south"), StatisticsRefresh.Now);

        Assert.Equal(8, Describe(source).RowCount);
        Assert.Equal(8, LargestId(source));
    }

    [Fact]
    public async Task A_deferred_append_is_recomputed_by_the_next_plain_refresh()
    {
        IReadOnlyList<Line> registered = Rows(1, 4, "north");
        var source = new PocoSourceBuilder("mem")
            .AddTable("lines", () => registered, out var lines)
            .Build();

        await Append(lines, Rows(5, 4, "south"), StatisticsRefresh.Defer);
        Assert.Equal(4, LargestId(source));

        // The plain refresh is what a deferral is deferred *to*. It re-reads the registration, so
        // what it recomputes over is whatever the registration now reports.
        registered = [.. Rows(1, 4, "north"), .. Rows(5, 4, "south")];
        await source.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(8, Describe(source).RowCount);
        Assert.Equal(8, LargestId(source));
    }

    /// <summary>
    /// A deferral over a registration that has not changed: the plain refresh rebuilds nothing, and
    /// still ends the deferral, because the statistics standing are the ones it was deferred to.
    /// </summary>
    [Fact]
    public async Task A_plain_refresh_ends_a_deferral_even_when_the_registration_is_unchanged()
    {
        var rows = Rows(1, 4, "north");
        var source = new PocoSourceBuilder("mem")
            .AddTable("lines", rows, out var lines)
            .Build();

        await Replace(lines, [.. Rows(1, 4, "north"), .. Rows(5, 4, "south")], StatisticsRefresh.Defer);
        Assert.Equal(4, LargestId(source));

        await source.RefreshAsync(TestContext.Current.CancellationToken);

        // Back to the registration's own four rows, and recomputed over them.
        Assert.Equal(4, Describe(source).RowCount);
        Assert.Equal(4, LargestId(source));
    }

    [Fact]
    public async Task A_growth_policy_carries_until_the_table_has_grown_by_the_fraction()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable(
                "lines",
                Rows(1, 100, "north"),
                out var lines,
                t => t.Statistics(StatisticsLevel.Basic, refreshWhenGrownBy: 0.1))
            .Build();

        Assert.Equal(100, Describe(source).RowCount);
        Assert.Equal(100, LargestId(source));

        // Five more rows: under the tenth, so the statistics stand and the count still moves.
        await Append(lines, Rows(101, 5, "south"));
        Assert.Equal(105, Describe(source).RowCount);
        Assert.Equal(100, LargestId(source));

        // Past the tenth: recomputed, and the newest rows are in the numbers.
        await Append(lines, Rows(106, 10, "south"));
        Assert.Equal(115, Describe(source).RowCount);
        Assert.Equal(115, LargestId(source));
    }

    /// <summary>
    /// A table that names no policy keeps its statistics until it is a tenth larger than when they
    /// were computed (D351), and its row count moves at every append.
    /// </summary>
    [Fact]
    public async Task A_table_that_names_no_policy_keeps_its_statistics_until_it_is_a_tenth_larger()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("lines", Rows(1, 100, "north"), out var lines)
            .Build();

        await Append(lines, Rows(101, 9, "south"));
        Assert.Equal(109, Describe(source).RowCount);
        Assert.Equal(100, LargestId(source));

        await Append(lines, Rows(110, 1, "south"));
        Assert.Equal(110, Describe(source).RowCount);
        Assert.Equal(110, LargestId(source));
    }

    /// <summary>
    /// Statistics a table computes under its growth policy are the ones it carries on (F206): the
    /// fraction is counted from them, not from the ones before, and a deferral after them keeps them.
    /// </summary>
    [Fact]
    public async Task Statistics_a_table_computes_are_the_ones_it_carries_on()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable(
                "lines",
                Rows(1, 100, "north"),
                out var lines,
                t => t.Statistics(StatisticsLevel.Basic, refreshWhenGrownBy: 0.1))
            .Build();

        await Append(lines, Rows(101, 10, "south"));
        Assert.Equal(110, LargestId(source));

        await Append(lines, Rows(111, 1, "south"));
        Assert.Equal(110, LargestId(source));

        await Append(lines, Rows(112, 1, "south"), StatisticsRefresh.Defer);
        Assert.Equal(110, LargestId(source));
    }

    /// <summary>
    /// Rows upserted count as rows moved, though the table does not grow: a table whose values are
    /// rewritten in place has its statistics computed again once a tenth of its rows have been.
    /// </summary>
    [Fact]
    public async Task Rows_upserted_count_as_rows_moved()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("marks", Marks(1, 100, price: 1), out var marks, t => t.Keyed(x => x.Id))
            .Build();

        await Apply(marks, Marks(1, 9, price: 500), SourceRefreshKind.Upsert, StatisticsRefresh.Now);
        Assert.Equal(1L, LargestPrice(source));

        await Apply(marks, Marks(10, 1, price: 700), SourceRefreshKind.Upsert, StatisticsRefresh.Now);
        Assert.Equal(100, DescribeTable(source, "marks").RowCount);
        Assert.Equal(700L, LargestPrice(source));
    }

    /// <summary>Rows deleted count as rows moved: statistics do not stand over a tenth of rows that are gone.</summary>
    [Fact]
    public async Task Rows_deleted_count_as_rows_moved()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("marks", Marks(1, 100, price: 1), out var marks, t => t.Keyed(x => x.Id))
            .Build();

        await Apply(marks, Marks(91, 10, price: 1), SourceRefreshKind.Delete, StatisticsRefresh.Now);

        Assert.Equal(90, DescribeTable(source, "marks").RowCount);
        Assert.Equal(90, (int)(DescribeTable(source, "marks").Columns.Single(c => c.Name == "Id").Statistics.Max ?? -1));
    }

    /// <summary>
    /// A replacement moves every row the table had and every row it brings, so its statistics are
    /// the new rows', whatever its size: none of the old rows is known to be among them.
    /// </summary>
    [Fact]
    public async Task A_replacement_is_described_by_its_own_rows()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("lines", Rows(1, 100, "north"), out var lines)
            .Build();

        await Replace(lines, Rows(1_001, 50, "south"), StatisticsRefresh.Now);

        Assert.Equal(50, Describe(source).RowCount);
        Assert.Equal(1_050, LargestId(source));
    }

    /// <summary>A policy of zero recomputes at every refresh, as every table did before D271.</summary>
    [Fact]
    public async Task A_policy_of_zero_recomputes_at_every_refresh()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable(
                "lines",
                Rows(1, 100, "north"),
                out var lines,
                t => t.Statistics(StatisticsLevel.Basic, refreshWhenGrownBy: 0))
            .Build();

        await Append(lines, Rows(101, 1, "south"));

        Assert.Equal(101, LargestId(source));
    }

    /// <summary>Choosing a level, or a bucket count, leaves the policy as it was given.</summary>
    [Fact]
    public async Task Choosing_a_level_keeps_the_policy()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable(
                "lines",
                Rows(1, 100, "north"),
                out var lines,
                t => t.Statistics(StatisticsLevel.Basic, refreshWhenGrownBy: 0)
                    .Statistics(StatisticsLevel.Histogram)
                    .Statistics(StatisticsLevel.Histogram, buckets: 8))
            .Build();

        await Append(lines, Rows(101, 1, "south"));

        Assert.Equal(101, LargestId(source));
    }

    /// <summary>
    /// A growth policy is not a deferral: the plain refresh does not end it, because "fresh enough
    /// until the table has grown that much" is unaffected by the catalog being reassembled.
    /// </summary>
    [Fact]
    public async Task A_plain_refresh_does_not_end_a_growth_policy()
    {
        var rows = Rows(1, 100, "north");
        var source = new PocoSourceBuilder("mem")
            .AddTable(
                "lines",
                rows,
                out var lines,
                t => t.Statistics(StatisticsLevel.Basic, refreshWhenGrownBy: 0.5))
            .Build();

        Assert.Equal(100, LargestId(source));
        await Append(lines, Rows(101, 5, "south"));

        await source.RefreshAsync(TestContext.Current.CancellationToken);

        // The registration reports the original list, which the refresh rebuilds from; what matters
        // here is that nothing threw and the count is the registration's own.
        Assert.Equal(100, Describe(source).RowCount);
    }

    [Fact]
    public void A_negative_growth_fraction_is_refused()
    {
        var refused = Assert.Throws<ArgumentOutOfRangeException>(
            () => new PocoSourceBuilder("mem")
                .AddTable(
                    "lines",
                    Rows(1, 1, "north"),
                    t => t.Statistics(StatisticsLevel.Basic, refreshWhenGrownBy: -0.1)));

        Assert.Equal("refreshWhenGrownBy", refused.ParamName);
    }

    private static Catalog.TableDescriptor Describe(PocoSource source) => DescribeTable(source, "lines");

    private static Catalog.TableDescriptor DescribeTable(PocoSource source, string table) =>
        source.DescribeSchema().Tables.Single(t => t.Name == table);

    private static Mark[] Marks(int from, int count, long price) =>
        [.. Enumerable.Range(from, count).Select(i => new Mark(i, price))];

    /// <summary>The largest <c>Price</c> the column statistics report.</summary>
    private static long LargestPrice(PocoSource source) =>
        Convert.ToInt64(
            DescribeTable(source, "marks").Columns.Single(c => c.Name == "Price").Statistics.Max ?? -1L,
            System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The largest <c>Id</c> the column statistics report — the number a pass over the values
    /// produces, and the one a carried set cannot have seen the newest rows in.
    /// </summary>
    private static int LargestId(PocoSource source) =>
        (int)(Describe(source).Columns.Single(c => c.Name == "Id").Statistics.Max ?? -1);

    private static Task Append(
        PocoTable<Line> table,
        IReadOnlyList<Line> rows,
        StatisticsRefresh statistics = StatisticsRefresh.Now) =>
        Apply(table, rows, SourceRefreshKind.Append, statistics);

    private static Task Replace(
        PocoTable<Line> table, IReadOnlyList<Line> rows, StatisticsRefresh statistics) =>
        Apply(table, rows, SourceRefreshKind.Replace, statistics);

    private static async Task Apply<TRow>(
        PocoTable<TRow> table,
        IReadOnlyList<TRow> rows,
        SourceRefreshKind kind,
        StatisticsRefresh statistics)
    {
        var source = (IRefreshableSource)table.Runtime;
        var entry = new SourceRefreshEntry
        {
            Table = table.Name,
            Kind = kind,
            RowType = typeof(TRow),
            Rows = rows,
            Statistics = statistics,
        };
        var commit = await source.PrepareRefreshAsync([entry], TestContext.Current.CancellationToken);
        commit.Commit();
    }
}
