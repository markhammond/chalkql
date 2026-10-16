using System.Collections.Concurrent;
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
/// The host's observer of the engine's planning: told of each prepare and why, of each drifted
/// statement a background run judges and what came of it — planned anew, unchanged, held back by a
/// margin, vetoed — of each run's totals, and of each refresh that takes a run's work in. One that
/// throws changes nothing the engine does.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class PlanningObserverTests(SharedSidecar sidecar) : IDisposable
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

    [Fact]
    public async Task A_host_prepare_is_observed()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Recorder();
        await using var desk = await DeskAsync(observer);

        var statement = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);

        var prepared = Assert.Single(observer.Prepares);
        Assert.Equal(PrepareReason.Host, prepared.Reason);
        Assert.Equal(statement.LogicalDigest, prepared.LogicalDigest);
        Assert.Equal(statement.PlanDigest, prepared.PlanDigest);
        Assert.True(prepared.Elapsed > TimeSpan.Zero);
    }

    /// <summary>
    /// A background run tells of the drifted statement it planned anew — the plan it ran, the plan it
    /// came to, and that a tenfold drift had made it stale — and of its totals; the refresh that takes
    /// the plan in tells of it, with the epoch and the version it published.
    /// </summary>
    [Fact]
    public async Task A_run_that_plans_a_statement_anew_and_the_refresh_that_takes_it_in_are_observed()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Recorder();
        await using var desk = await DeskAsync(observer);
        var statement = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        var planned = statement.PlanDigest;

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();

        var judged = Assert.Single(observer.Judgements);
        Assert.Equal(statement.LogicalDigest, judged.LogicalDigest);
        Assert.Equal(PlanVerdict.PlannedAnew, judged.Verdict);
        Assert.Equal(planned, judged.RunningPlan);
        Assert.NotEqual(0UL, judged.NewPlan);
        Assert.NotEqual(planned, judged.NewPlan);
        Assert.True(judged.Stale);
        Assert.Null(judged.NewCost);
        var run = observer.Runs[^1];
        Assert.Equal(1, run.Drifted);
        Assert.Equal(1, run.PlannedAnew);
        Assert.True(run.TablesSent > 0);
        Assert.True(run.Elapsed > TimeSpan.Zero);
        Assert.Empty(observer.TakenIn);

        var epoch = await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);

        var takenIn = Assert.Single(observer.TakenIn);
        Assert.Equal(epoch, takenIn.Epoch);
        Assert.Equal(desk.Engine.Version, takenIn.Version);
        Assert.Equal(1, takenIn.PlansSwappedIn);
        Assert.Equal(0, takenIn.PlansUnchanged);
        Assert.Equal(judged.NewPlan, statement.PlanDigest);
    }

    /// <summary>
    /// A refresh that takes a view's new plan in tells of the version it published, and the view's
    /// update that runs the plan reports the same: here the host's own, which the epoch is not.
    /// </summary>
    [Fact]
    public async Task A_take_in_matches_the_view_update_that_runs_its_plan_by_version()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Recorder();
        var tracker = new Tracker();
        await using var desk = await DeskAsync(observer);
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(tracker: tracker), Ct);
        var planned = view.Statement.PlanDigest;

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();
        var epoch = await desk.Engine.RefreshAsync(r => r.Version(1_000).DeferPlanning(), Ct);
        await view.UpdateAsync(Ct);

        var takenIn = Assert.Single(observer.TakenIn);
        Assert.Equal(1, takenIn.PlansSwappedIn);
        Assert.Equal(epoch, takenIn.Epoch);
        Assert.Equal(1_000, takenIn.Version);
        Assert.NotEqual(takenIn.Epoch, takenIn.Version);
        Assert.Equal(2, tracker.Updates.Count);
        var update = tracker.Updates[^1];
        Assert.Equal(takenIn.Version, update.Version);
        Assert.True(update.Whole);
        Assert.NotEqual(planned, update.PlanDigest);
        Assert.Equal(view.Statement.PlanDigest, update.PlanDigest);
    }

    /// <summary>
    /// <c>PlanAsync</c> tells of the plans it takes in at the epoch and version current, which it does
    /// not move, and of no statistics: those enter with the next refresh, which tells of them.
    /// </summary>
    [Fact]
    public async Task PlanAsync_tells_of_the_plans_it_takes_in_at_the_current_version()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Recorder();
        await using var desk = await DeskAsync(observer);
        var statement = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        var planned = statement.PlanDigest;
        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        var epoch = desk.Engine.Catalog.Epoch;
        var version = desk.Engine.Version;

        Assert.True(await desk.Engine.PlanAsync(Ct));

        var takenIn = Assert.Single(observer.TakenIn);
        Assert.Equal(epoch, takenIn.Epoch);
        Assert.Equal(version, takenIn.Version);
        Assert.Equal(1, takenIn.PlansSwappedIn);
        Assert.Equal(0, takenIn.StatisticsTables);
        Assert.NotEqual(planned, statement.PlanDigest);
        Assert.Equal(epoch, desk.Engine.Catalog.Epoch);

        var next = await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);

        Assert.Equal(2, observer.TakenIn.Count);
        var refresh = observer.TakenIn[^1];
        Assert.Equal(next, refresh.Epoch);
        Assert.Equal(0, refresh.PlansSwappedIn);
        Assert.True(refresh.StatisticsTables > 0);
    }

    [Fact]
    public async Task A_statement_planned_again_to_its_own_plan_is_observed_unchanged()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Recorder();
        await using var desk = await DeskAsync(observer);
        var statement = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        observer.Clear();

        await desk.LoadAsync(Fills(6_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();

        var judged = Assert.Single(observer.Judgements);
        Assert.Equal(PlanVerdict.Unchanged, judged.Verdict);
        Assert.Equal(judged.RunningPlan, judged.NewPlan);
        Assert.Equal(statement.PlanDigest, judged.RunningPlan);
        Assert.False(judged.Stale);
        Assert.Equal(1, observer.Runs[^1].Unchanged);
    }

    /// <summary>A plan the planner holds back under a view's margin is observed with both its costs, and no plan.</summary>
    [Fact]
    public async Task A_plan_held_back_by_a_margin_is_observed_with_both_costs()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Recorder();
        await using var desk = await DeskAsync(observer);
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(margin: 0.95), Ct);

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();

        var judged = Assert.Single(observer.Judgements);
        Assert.Equal(PlanVerdict.HeldByMargin, judged.Verdict);
        Assert.Equal(0UL, judged.NewPlan);
        Assert.Equal(view.Statement.PlanDigest, judged.RunningPlan);
        Assert.NotNull(judged.NewCost);
        Assert.NotNull(judged.RunningCost);
        Assert.True(judged.NewCost >= judged.RunningCost * 0.05, $"the new plan costs {judged.NewCost}, the held one {judged.RunningCost}");
        Assert.Equal(1, observer.Runs[^1].HeldByMargin);
    }

    [Fact]
    public async Task A_veto_is_observed()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Recorder();
        await using var desk = await DeskAsync(observer);
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(refresh: static () => false), Ct);

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();

        var judged = Assert.Single(observer.Judgements);
        Assert.Equal(PlanVerdict.Vetoed, judged.Verdict);
        Assert.Equal(0UL, judged.NewPlan);
        Assert.Equal(1, observer.Runs[^1].Vetoed);
    }

    /// <summary>A view that prepares its statement again after a change of shape is observed preparing it, and why.</summary>
    [Fact]
    public async Task A_view_preparing_again_for_a_change_of_shape_is_observed()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Recorder();
        var (source, connection) = Database();
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner(), PlanningObserver = observer }, Ct);
        await using var view = await engine.MaterializeAsync("SELECT id, name FROM db.widget", WidgetOptions(), Ct);
        Assert.Equal([PrepareReason.Host], observer.Prepares.Select(p => p.Reason));

        Execute(connection, "ALTER TABLE widget ADD COLUMN price REAL");
        await engine.RefreshAsync(Ct);
        await view.UpdateAsync(Ct);

        Assert.Equal([PrepareReason.Host, PrepareReason.ChangedShape], observer.Prepares.Select(p => p.Reason));
        Assert.Equal(view.Statement.PlanDigest, observer.Prepares[^1].PlanDigest);
    }

    /// <summary>
    /// A view whose statement a tenfold drift made stale, and which the background cannot plan again,
    /// prepares it again, and is observed doing so for the drift.
    /// </summary>
    [Fact]
    public async Task A_view_preparing_again_for_a_drift_is_observed()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Recorder();
        await using var desk = await DeskAsync(observer);
        await desk.Engine.RefreshAsync(
            r => r.Replace(desk.Source, "fills", Fills(30, books: 3)).Replace(desk.Source, "books", Books(3)), Ct);
        var held = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        held.Decorate(schema => schema, _ => { });
        await using var view = await desk.Engine.MaterializeAsync(held, Options(maintenance: ViewMaintenance.Recompute), Ct);

        await desk.Engine.RefreshAsync(
            r => r.Replace(desk.Source, "fills", Fills(3_000, books: 3)).Replace(desk.Source, "books", Books(3)), Ct);
        await view.UpdateAsync(Ct);

        Assert.Equal([PrepareReason.Host, PrepareReason.Drift], observer.Prepares.Select(p => p.Reason));
    }

    /// <summary>An observer that throws changes nothing the engine does: the plan is still made and swapped in.</summary>
    [Fact]
    public async Task An_observer_that_throws_changes_nothing()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Recorder { Throws = true };
        await using var desk = await DeskAsync(observer);
        var statement = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        var planned = statement.PlanDigest;

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);

        Assert.NotEqual(planned, statement.PlanDigest);
        Assert.NotEmpty(observer.Judgements);
        Assert.NotEmpty(observer.TakenIn);
    }

    // ---- the parts ----

    /// <summary>An observer that keeps everything it is told, on whatever thread tells it.</summary>
    private sealed class Recorder : IPlanningObserver
    {
        private readonly ConcurrentQueue<StatementJudged> _judgements = new();
        private readonly ConcurrentQueue<PlanningRun> _runs = new();
        private readonly ConcurrentQueue<PlansTakenIn> _takenIn = new();
        private readonly ConcurrentQueue<StatementPrepared> _prepares = new();

        public bool Throws { get; init; }

        public IReadOnlyList<StatementJudged> Judgements => [.. _judgements];

        public IReadOnlyList<PlanningRun> Runs => [.. _runs];

        public IReadOnlyList<PlansTakenIn> TakenIn => [.. _takenIn];

        public IReadOnlyList<StatementPrepared> Prepares => [.. _prepares];

        public void Clear()
        {
            _judgements.Clear();
            _runs.Clear();
            _takenIn.Clear();
            _prepares.Clear();
        }

        public void Judged(in StatementJudged judged)
        {
            _judgements.Enqueue(judged);
            Throw();
        }

        public void Ran(in PlanningRun run)
        {
            _runs.Enqueue(run);
            Throw();
        }

        public void TookIn(in PlansTakenIn takenIn)
        {
            _takenIn.Enqueue(takenIn);
            Throw();
        }

        public void Prepared(in StatementPrepared prepared)
        {
            _prepares.Enqueue(prepared);
            Throw();
        }

        private void Throw()
        {
            if (Throws)
            {
                throw new InvalidOperationException("the host's observer failed");
            }
        }
    }

    /// <summary>A tracker that keeps every update it is told of.</summary>
    private sealed class Tracker : IViewCostTracker
    {
        public List<ViewUpdate> Updates { get; } = [];

        public void Updated(in ViewUpdate update) => Updates.Add(update);
    }

    private static ViewOptions<One, One> Options(
        ViewMaintenance maintenance = ViewMaintenance.Incremental,
        Func<bool>? refresh = null,
        double? margin = null,
        IViewCostTracker? tracker = null) => new()
    {
        Key = ["desk"],
        Maintenance = maintenance,
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Map = static (in ViewRow row, out One key, out One value) =>
        {
            key = new One { Value = row.GetInt64(0) };
            value = new One { Value = row.GetInt64(1) };
        },
        RefreshQueryPlan = refresh,
        PlanImprovementMargin = margin,
        Tracker = tracker,
    };

    private static ViewOptions<One, One> WidgetOptions() => new()
    {
        Key = ["id"],
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        DatabaseReads = ViewDatabaseReads.WhenRefreshed,
        Map = static (in ViewRow row, out One key, out One value) =>
        {
            key = new One { Value = row.GetInt64(0) };
            value = key;
        },
    };

    private static IReadOnlyList<Fill> Fills(int count, int books) =>
        [.. Enumerable.Range(1, count).Select(i => new Fill(i, i % books, (i % 7) + 1))];

    private static IReadOnlyList<Book> Books(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Book(i, i % 3))];

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

    private async Task<Desk> DeskAsync(IPlanningObserver observer)
    {
        var source = new PocoSourceBuilder("trading")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("fills", (IReadOnlyList<Fill>)[])
            .AddTable("books", (IReadOnlyList<Book>)[])
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner(), PlanningObserver = observer },
            Ct);
        return new Desk { Engine = engine, Source = source };
    }

    /// <summary>A private in-memory SQLite with one small table, discovered rather than declared.</summary>
    private (AdoSource Source, DbConnection KeepAlive) Database()
    {
        var connectionString = $"Data Source=chalk-observer-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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
}
