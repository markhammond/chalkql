namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// Statistics a refresh deferred, computed in the background and taken in at a later refresh
/// (design 69 §4): computed only for a table that awaits them, taken in by whatever snapshot the table
/// serves then, and never in place of newer ones.
/// </summary>
public sealed class PocoDeferredStatisticsTests
{
    private sealed record Tick(int Seq, long Qty);

    private static IReadOnlyList<Tick> Ticks(int from, int count, long qty) =>
        [.. Enumerable.Range(from, count).Select(i => new Tick(i, qty))];

    /// <summary>
    /// A deferred append leaves the table awaiting statistics: the background computes them over its
    /// rows, and once they are taken in the table reports them and awaits nothing more.
    /// </summary>
    [Fact]
    public async Task A_deferred_append_awaits_statistics_until_they_are_taken_in()
    {
        var source = Source(Ticks(1, 10, qty: 5));
        Assert.Equal(5L, QtyMax(source));
        Assert.Empty(Background(source).ComputeDeferredStatistics(TestContext.Current.CancellationToken));

        await AppendAsync(source, Ticks(11, 5, qty: 50), StatisticsRefresh.Defer);
        Assert.Equal(5L, QtyMax(source));

        var computed = Background(source).ComputeDeferredStatistics(TestContext.Current.CancellationToken);
        var ticks = Assert.Single(computed);
        Assert.Equal("ticks", ticks.Table);
        Assert.Equal(15, ticks.RowCount);
        Assert.Equal(50L, Convert.ToInt64(ticks.Descriptor.Columns.Single(c => c.Name == "Qty").Statistics.Max));
        Assert.Equal(5L, QtyMax(source));

        Background(source).AdmitStatistics(computed);
        Assert.Equal(50L, QtyMax(source));
        Assert.Empty(Background(source).ComputeDeferredStatistics(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Statistics computed over one snapshot are taken in by the next, deferred again since, which
    /// carries them on and still awaits statistics over its own rows.
    /// </summary>
    [Fact]
    public async Task A_later_deferred_snapshot_takes_them_in_and_awaits_its_own()
    {
        var source = Source(Ticks(1, 10, qty: 5));
        await AppendAsync(source, Ticks(11, 5, qty: 50), StatisticsRefresh.Defer);
        var computed = Background(source).ComputeDeferredStatistics(TestContext.Current.CancellationToken);

        await AppendAsync(source, Ticks(16, 5, qty: 500), StatisticsRefresh.Defer);
        Background(source).AdmitStatistics(computed);

        Assert.Equal(50L, QtyMax(source));
        var next = Assert.Single(Background(source).ComputeDeferredStatistics(TestContext.Current.CancellationToken));
        Assert.Equal(20, next.RowCount);
        Assert.Equal(500L, Convert.ToInt64(next.Descriptor.Columns.Single(c => c.Name == "Qty").Statistics.Max));
    }

    /// <summary>
    /// Statistics computed before a refresh that computed its own are older than the ones the table
    /// carries since, and are not taken in.
    /// </summary>
    [Fact]
    public async Task Statistics_older_than_the_table_s_own_are_not_taken_in()
    {
        var source = Source(Ticks(1, 10, qty: 5));
        await AppendAsync(source, Ticks(11, 5, qty: 50), StatisticsRefresh.Defer);
        var computed = Background(source).ComputeDeferredStatistics(TestContext.Current.CancellationToken);

        await RefreshAsync(source, SourceRefreshKind.Replace, Ticks(1, 10, qty: 7), StatisticsRefresh.Now);
        Assert.Equal(7L, QtyMax(source));
        await AppendAsync(source, Ticks(11, 5, qty: 70), StatisticsRefresh.Defer);
        Background(source).AdmitStatistics(computed);

        Assert.Equal(7L, QtyMax(source));
    }

    // ---- the scaffolding ----

    private static PocoSource Source(IReadOnlyList<Tick> rows) =>
        new PocoSourceBuilder("mem").AddTable("ticks", rows).Build();

    private static IDeferredStatisticsSource Background(PocoSource source) => source;

    private static long QtyMax(PocoSource source) =>
        Convert.ToInt64(PocoTestSupport.Describe(source, "ticks").Columns.Single(c => c.Name == "Qty").Statistics.Max);

    private static Task AppendAsync(PocoSource source, IReadOnlyList<Tick> rows, StatisticsRefresh statistics) =>
        RefreshAsync(source, SourceRefreshKind.Append, rows, statistics);

    private static async Task RefreshAsync(
        PocoSource source, SourceRefreshKind kind, IReadOnlyList<Tick> rows, StatisticsRefresh statistics)
    {
        var commit = await source.PrepareRefreshAsync(
            [
                new SourceRefreshEntry
                {
                    Table = "ticks",
                    Kind = kind,
                    RowType = typeof(Tick),
                    Rows = rows,
                    Statistics = statistics,
                },
            ],
            TestContext.Current.CancellationToken);
        commit.Commit();
    }
}
