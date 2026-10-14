using System.Data.Common;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using Microsoft.Data.Sqlite;

namespace Chalk.Integration.Tests;

/// <summary>
/// The host's say on the plans a view takes, and the tracker of its updates (design 70 §1). A host's
/// RefreshQueryPlan that says no leaves the statement on its plan, with nothing requested of the
/// planner, until a later refresh finds it saying yes; one that throws says no; a drift that makes the
/// statement stale asks it too, and a change of shape does not; a statement carries one view's; and the
/// tracker is told of every update that moves the view.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class ViewPlanTests(SharedSidecar sidecar) : IDisposable
{
    private sealed record Fill(int Seq, int Book, long Qty);

    private sealed record Book(int Id, int Desk);

    private struct One
    {
        public long Value;
    }

    private const string ByDesk =
        "SELECT b.desk, SUM(f.qty) AS qty FROM fills AS f JOIN books AS b ON b.id = f.book GROUP BY b.desk";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<DbConnection> _open = [];

    public void Dispose()
    {
        foreach (var connection in _open)
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// A host that says no keeps the statement on its plan, and the planner is asked for nothing; the
    /// refresh the host orchestrates once it says yes asks again, and the plan is requested and taken.
    /// </summary>
    [Theory]
    [InlineData(ViewMaintenance.Incremental)]
    [InlineData(ViewMaintenance.Recompute)]
    public async Task A_veto_keeps_the_plan_until_a_later_refresh_finds_the_host_willing(ViewMaintenance maintenance)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var host = new Host { Willing = false };
        var watch = new PlannerWatch(sidecar.CreatePlanner());
        await using var desk = await DeskAsync(watch);
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(maintenance, refresh: host.Refresh), Ct);
        var planned = view.Statement.PlanDigest;

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await view.UpdateAsync(Ct);

        Assert.Equal(planned, view.Statement.PlanDigest);
        Assert.Equal(0, watch.Replans);
        var asked = host.Asked;
        Assert.True(asked > 0);
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), await ViewRowsAsync(view));

        // Willing now, the host refreshes: that refresh's run asks again, and plans the statement.
        host.Willing = true;
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await view.UpdateAsync(Ct);

        Assert.True(host.Asked > asked);
        Assert.Equal(1, watch.Replans);
        Assert.NotEqual(planned, view.Statement.PlanDigest);
        Assert.True(view.MaintainsCurrentPlan);
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), await ViewRowsAsync(view));
    }

    [Fact]
    public async Task A_veto_that_throws_says_not_now()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var host = new Host { Throws = true };
        var watch = new PlannerWatch(sidecar.CreatePlanner());
        await using var desk = await DeskAsync(watch);
        await using var view = await desk.Engine.MaterializeAsync(
            ByDesk, Options(ViewMaintenance.Incremental, refresh: host.Refresh), Ct);
        var planned = view.Statement.PlanDigest;

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await desk.Engine.BackgroundPlanningSettledAsync();
        await view.UpdateAsync(Ct);

        Assert.Equal(planned, view.Statement.PlanDigest);
        Assert.Equal(0, watch.Replans);
        Assert.True(host.Asked >= 2, $"asked {host.Asked} times: the refresh after the throw asks again");
    }

    /// <summary>
    /// Planning left in the refresh, a tenfold drift makes the statement stale, and the view asks the
    /// host before it prepares the statement again: no keeps the statement it runs, which still answers
    /// correctly, and yes, at a later update, prepares it again.
    /// </summary>
    [Fact]
    public async Task A_drift_that_makes_the_statement_stale_asks_the_host_first()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var host = new Host { Willing = false };
        await using var desk = await DeskAsync();
        await desk.Engine.RefreshAsync(
            r => r.Replace(desk.Source, "fills", Fills(30, books: 3)).Replace(desk.Source, "books", Books(3)), Ct);
        await using var view = await desk.Engine.MaterializeAsync(
            ByDesk, Options(ViewMaintenance.Incremental, refresh: host.Refresh), Ct);
        var before = view.Statement;

        await desk.Engine.RefreshAsync(
            r => r.Replace(desk.Source, "fills", Fills(3_000, books: 3)).Replace(desk.Source, "books", Books(3)), Ct);
        Assert.True(before.IsStale);
        await view.UpdateAsync(Ct);

        Assert.Same(before, view.Statement);
        Assert.True(host.Asked > 0);
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), await ViewRowsAsync(view));

        host.Willing = true;
        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", [new Fill(3_001, 1, 5)]), Ct);
        await view.UpdateAsync(Ct);

        Assert.NotSame(before, view.Statement);
        Assert.False(view.Statement.IsStale);
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), await ViewRowsAsync(view));
    }

    /// <summary>A table that changed shape requests a plan without asking: the plan no longer fits it.</summary>
    [Fact]
    public async Task A_change_of_shape_prepares_the_statement_again_without_asking()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var host = new Host { Willing = false };
        var (source, connection) = Database();
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner() }, Ct);
        await using var view = await engine.MaterializeAsync(
            "SELECT id, name FROM db.widget",
            new ViewOptions<One, One>
            {
                Key = ["id"],
                LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
                DatabaseReads = ViewDatabaseReads.WhenRefreshed,
                Map = static (in ViewRow row, out One key, out One value) =>
                {
                    key = new One { Value = row.GetInt64(0) };
                    value = key;
                },
                RefreshQueryPlan = host.Refresh,
            },
            Ct);
        var before = view.Statement;

        Execute(connection, "ALTER TABLE widget ADD COLUMN price REAL");
        await engine.RefreshAsync(Ct);
        await view.UpdateAsync(Ct);

        Assert.NotSame(before, view.Statement);
        Assert.Equal(0, host.Asked);
    }

    /// <summary>
    /// The tracker hears of every update that moves the view, and of none that does not: the version it
    /// brought, whether it was whole — every update of a recomputing view, an incremental view's first —
    /// the keys it changed, and the plan it ran.
    /// </summary>
    [Theory]
    [InlineData(ViewMaintenance.Incremental)]
    [InlineData(ViewMaintenance.Recompute)]
    public async Task The_tracker_hears_of_each_update(ViewMaintenance maintenance)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var tracker = new Tracker();
        await using var desk = await DeskAsync();
        await desk.LoadAsync(Fills(300, books: 3), Books(3));
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(maintenance, tracker: tracker), Ct);

        await view.UpdateAsync(Ct);
        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", [new Fill(301, 1, 5)]), Ct);
        await view.UpdateAsync(Ct);

        Assert.Equal(2, tracker.Updates.Count);
        var (first, second) = (tracker.Updates[0], tracker.Updates[1]);
        Assert.True(first.Whole);
        Assert.Equal(maintenance == ViewMaintenance.Recompute, second.Whole);
        Assert.Equal(3, first.Changes);
        Assert.Equal(1, second.Changes);
        Assert.True(second.Version > first.Version);
        Assert.Equal(view.Version, second.Version);
        Assert.Equal(view.Statement.PlanDigest, second.PlanDigest);
        Assert.True(second.Elapsed > TimeSpan.Zero);
    }

    /// <summary>
    /// A statement carries one view's RefreshQueryPlan; a second view with one over it is refused until
    /// the first goes, and one without is made.
    /// </summary>
    [Fact]
    public async Task A_statement_carries_one_views_RefreshQueryPlan()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        var statement = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        var first = await desk.Engine.MaterializeAsync(
            statement, Options(ViewMaintenance.Recompute, refresh: new Host().Refresh), Ct);

        var refused = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await desk.Engine.MaterializeAsync(statement, Options(ViewMaintenance.Recompute, refresh: new Host().Refresh), Ct));
        Assert.Contains("carries another view's RefreshQueryPlan", refused.Message, StringComparison.Ordinal);

        await using (var without = await desk.Engine.MaterializeAsync(statement, Options(ViewMaintenance.Recompute), Ct))
        {
        }

        await first.DisposeAsync();
        await using var second = await desk.Engine.MaterializeAsync(
            statement, Options(ViewMaintenance.Recompute, refresh: new Host().Refresh), Ct);
    }

    // ---- the parts ----

    /// <summary>A host's RefreshQueryPlan: whether it is willing, how often it was asked, and whether it throws.</summary>
    private sealed class Host
    {
        private int _asked;
        private volatile bool _willing = true;

        public bool Willing
        {
            get => _willing;
            set => _willing = value;
        }

        public bool Throws { get; init; }

        public int Asked => Volatile.Read(ref _asked);

        public bool Refresh()
        {
            Interlocked.Increment(ref _asked);
            return Throws ? throw new InvalidOperationException("the host's veto failed") : Willing;
        }
    }

    /// <summary>A tracker that keeps every update it is told of.</summary>
    private sealed class Tracker : IViewCostTracker
    {
        public List<ViewUpdate> Updates { get; } = [];

        public void Updated(in ViewUpdate update) => Updates.Add(update);
    }

    /// <summary>The sidecar's planner, counting the requests that plan a statement again: those naming the plan it runs.</summary>
    private sealed class PlannerWatch(IQueryPlanner inner) : IQueryPlanner
    {
        private int _replans;

        public int Replans => Volatile.Read(ref _replans);

        public bool AcceptsCatalogDeltas => inner.AcceptsCatalogDeltas;

        public ValueTask<PlannerInfo> GetInfoAsync(CancellationToken ct = default) => inner.GetInfoAsync(ct);

        public ValueTask RegisterCatalogAsync(CatalogRegistration registration, CancellationToken ct = default) =>
            inner.RegisterCatalogAsync(registration, ct);

        public ValueTask RegisterStatisticsAsync(StatisticsRegistration statistics, CancellationToken ct = default) =>
            inner.RegisterStatisticsAsync(statistics, ct);

        public ValueTask<PlanResult> PlanAsync(PlanRequest request, CancellationToken ct = default)
        {
            if (request.KnownPlanDigest != 0)
            {
                Interlocked.Increment(ref _replans);
            }

            return inner.PlanAsync(request, ct);
        }

        public ValueTask<RedactedSql> RedactSqlAsync(RedactSqlRequest request, CancellationToken ct = default) =>
            inner.RedactSqlAsync(request, ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static ViewOptions<One, One> Options(
        ViewMaintenance maintenance, Func<bool>? refresh = null, IViewCostTracker? tracker = null) => new()
    {
        Key = ["desk"],
        Maintenance = maintenance,
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Map = static (in ViewRow row, out One key, out One value) =>
        {
            key = new One { Value = row.GetInt64(0) };
            value = new One { Value = row.GetInt64(1) };
        },
        ResetWhen = double.PositiveInfinity,
        RefreshQueryPlan = refresh,
        Tracker = tracker,
    };

    private static IReadOnlyList<Fill> Fills(int count, int books) =>
        [.. Enumerable.Range(1, count).Select(i => new Fill(i, i % books, (i % 7) + 1))];

    private static IReadOnlyList<Book> Books(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Book(i, i % 3))];

    /// <summary>The view's rows, as a reader starting now is sent them.</summary>
    private static Task<Dictionary<long, long>> ViewRowsAsync(StandingView<One, One> view)
    {
        var sink = new Recorder();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, sink);
        return Task.FromResult(sink.Rows);
    }

    private static async Task<Dictionary<long, long>> RowsAsync(ChalkEngine engine, PreparedQuery query)
    {
        var rows = new Dictionary<long, long>();
        await using var execution = await engine.ExecuteAsync(query, (IReadOnlyList<object?>?)null, ct: Ct);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    rows[Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(0), i))] =
                        Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(1), i));
                }
            }
        }

        return rows;
    }

    private sealed class Desk : IAsyncDisposable
    {
        public required ChalkEngine Engine { get; init; }

        public required PocoSource Source { get; init; }

        /// <summary>Replaces both tables in one refresh that defers its planning, as a host's load might.</summary>
        public async Task LoadAsync(IReadOnlyList<Fill> fills, IReadOnlyList<Book> books) =>
            await Engine.RefreshAsync(
                refresh => refresh.Replace(Source, "fills", fills).Replace(Source, "books", books).DeferPlanning(),
                Ct);

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private async Task<Desk> DeskAsync(IQueryPlanner? planner = null)
    {
        var source = new PocoSourceBuilder("trading")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("fills", (IReadOnlyList<Fill>)[])
            .AddTable("books", (IReadOnlyList<Book>)[])
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = planner ?? sidecar.CreatePlanner() },
            Ct);
        return new Desk { Engine = engine, Source = source };
    }

    /// <summary>A private in-memory SQLite with one small table, discovered rather than declared.</summary>
    private (AdoSource Source, DbConnection KeepAlive) Database()
    {
        var connectionString = $"Data Source=chalk-view-plans-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        DbConnection Connect() => new SqliteConnection(connectionString);
        var keepAlive = Connect();
        keepAlive.Open();
        _open.Add(keepAlive);
        Execute(keepAlive, "CREATE TABLE widget (id INTEGER NOT NULL, name TEXT)");
        Execute(keepAlive, "INSERT INTO widget VALUES (1, 'one'), (2, 'two'), (3, 'three')");
        var source = new AdoSourceBuilder("db", Connect, "db")
            .Dialect(DialectProfiles.Sqlite)
            .Capabilities(AdoCapabilities.For(DialectProfiles.Sqlite))
            .DiscoverTables()
            .Build();
        return (source, keepAlive);
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>A sink that keeps the rows it was sent.</summary>
    private sealed class Recorder : IViewSink<One, One>
    {
        public Dictionary<long, long> Rows { get; } = [];

        public void Reset() => Rows.Clear();

        public void Upsert(in One key, in One row) => Rows[key.Value] = row.Value;

        public void Delete(in One key) => Rows.Remove(key.Value);

        public void Commit(long version, uint checksum)
        {
        }
    }
}
