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
/// The host's say on the plans a view takes, and the tracker of its updates (design 70 §1, §3). A
/// host's RefreshQueryPlan that says no leaves the statement on its plan, with nothing requested of the
/// planner, until a later refresh finds it saying yes; one that throws says no; a drift that makes the
/// statement stale is planned again in the background, which asks it too, and a change of shape does
/// not ask. The planner holds back a plan that saves
/// less than the view's PlanImprovementMargin, sends one that saves more, and sends any it cannot price
/// against a plan it no longer keeps. A statement carries one view's say; and the tracker is told of
/// every update that moves the view.
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
        Assert.Equal(Counts(drifted: 1, vetoed: 1), desk.Engine.LastBackgroundPlans);
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
        Assert.Equal(Counts(drifted: 1, planned: 1), desk.Engine.LastBackgroundPlans);
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
    /// Planning left in the refresh, a tenfold drift makes the statement stale, and the view has it
    /// planned again in the background, in place (design 70 §2): that run asks the host, as any run
    /// does. No keeps the statement and its plan, which still answer correctly; yes, at a later update,
    /// plans it again, and the statement is no longer stale. The view never prepares it anew.
    /// </summary>
    [Fact]
    public async Task A_drift_that_makes_the_statement_stale_is_planned_again_after_asking_the_host()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var host = new Host { Willing = false };
        await using var desk = await DeskAsync();
        await desk.Engine.RefreshAsync(
            r => r.Replace(desk.Source, "fills", Fills(30, books: 3)).Replace(desk.Source, "books", Books(3)), Ct);
        await using var view = await desk.Engine.MaterializeAsync(
            ByDesk, Options(ViewMaintenance.Incremental, refresh: host.Refresh), Ct);
        var before = view.Statement;
        var planned = before.PlanDigest;

        await desk.Engine.RefreshAsync(
            r => r.Replace(desk.Source, "fills", Fills(3_000, books: 3)).Replace(desk.Source, "books", Books(3)), Ct);
        Assert.True(before.IsStale);
        Assert.False(before.ChangedShape);
        await view.UpdateAsync(Ct);
        await desk.Engine.BackgroundPlanningSettledAsync();

        Assert.Same(before, view.Statement);
        Assert.Equal(planned, before.PlanDigest);
        Assert.True(before.IsStale);
        Assert.Equal(Counts(drifted: 1, vetoed: 1), desk.Engine.LastBackgroundPlans);
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), await ViewRowsAsync(view));

        host.Willing = true;
        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", [new Fill(3_001, 1, 5)]), Ct);
        await view.UpdateAsync(Ct);
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", [new Fill(3_002, 2, 7)]), Ct);
        await view.UpdateAsync(Ct);

        var counts = desk.Engine.LastBackgroundPlans!;
        Assert.Equal(1, counts.New + counts.Unchanged);
        Assert.Same(before, view.Statement);
        Assert.False(before.IsStale);
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), await ViewRowsAsync(view));
    }

    /// <summary>
    /// A statement the background cannot plan again — one an extension decorated without a check on
    /// later plans — is prepared anew for a tenfold drift on the update's thread, as before, and asks
    /// the host first there: no keeps it, yes prepares it again.
    /// </summary>
    [Fact]
    public async Task A_statement_the_background_cannot_plan_again_is_prepared_anew_after_asking()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var host = new Host { Willing = false };
        await using var desk = await DeskAsync();
        await desk.Engine.RefreshAsync(
            r => r.Replace(desk.Source, "fills", Fills(30, books: 3)).Replace(desk.Source, "books", Books(3)), Ct);
        var before = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        before.Decorate(schema => schema, (_, _) => { });
        await using var view = await desk.Engine.MaterializeAsync(
            before, Options(ViewMaintenance.Recompute, refresh: host.Refresh), Ct);

        await desk.Engine.RefreshAsync(
            r => r.Replace(desk.Source, "fills", Fills(3_000, books: 3)).Replace(desk.Source, "books", Books(3)), Ct);
        Assert.True(before.IsStale);
        Assert.False(before.MayPlanAgain);
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
            "SELECT id, name FROM db.widget", WidgetOptions(refresh: host.Refresh), Ct);
        var before = view.Statement;

        Execute(connection, "ALTER TABLE widget ADD COLUMN price REAL");
        await engine.RefreshAsync(Ct);
        await view.UpdateAsync(Ct);

        Assert.NotSame(before, view.Statement);
        Assert.Equal(0, host.Asked);
    }

    /// <summary>
    /// A statement planned again to the plan it runs is counted, as unchanged, beside the ones planned
    /// anew: a run that took no new plan still says whether anything drifted.
    /// </summary>
    [Fact]
    public async Task A_statement_planned_again_to_its_own_plan_is_counted_unchanged()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(ViewMaintenance.Incremental), Ct);
        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await view.UpdateAsync(Ct);
        var planned = view.Statement.PlanDigest;

        await desk.LoadAsync(Fills(6_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();

        Assert.Equal(Counts(drifted: 1, unchanged: 1), desk.Engine.LastBackgroundPlans);
        Assert.Equal(planned, view.Statement.PlanDigest);
        Assert.False(view.Statement.DriftedFrom(desk.Engine.Catalog));
    }

    /// <summary>
    /// A plan that does not save the margin is held back by the planner: nothing is sent or compiled,
    /// the view keeps its plan, and drift is judged from the new numbers.
    /// </summary>
    [Theory]
    [InlineData(ViewMaintenance.Incremental)]
    [InlineData(ViewMaintenance.Recompute)]
    public async Task A_plan_that_saves_less_than_the_margin_is_held_back(ViewMaintenance maintenance)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var watch = new PlannerWatch(sidecar.CreatePlanner());
        await using var desk = await DeskAsync(watch);
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(maintenance, margin: 0.95), Ct);
        var planned = view.Statement.PlanDigest;

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();
        Assert.Equal(Counts(drifted: 1, held: 1), desk.Engine.LastBackgroundPlans);
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await view.UpdateAsync(Ct);

        Assert.Equal(planned, view.Statement.PlanDigest);
        Assert.True(view.MaintainsCurrentPlan);
        var held = Assert.Single(watch.Answers, a => a.Held);
        Assert.Equal(0UL, held.Plan.PlanDigest);
        Assert.True(
            held.PlanCost < held.KnownPlanCost && held.PlanCost >= held.KnownPlanCost * 0.05,
            $"the new plan costs {held.PlanCost}, the held one {held.KnownPlanCost}");
        Assert.False(view.Statement.DriftedFrom(desk.Engine.Catalog));
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), await ViewRowsAsync(view));
    }

    /// <summary>A plan that saves the margin is sent and taken, priced beside the plan it replaces.</summary>
    [Fact]
    public async Task A_plan_that_saves_the_margin_is_taken()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var watch = new PlannerWatch(sidecar.CreatePlanner());
        await using var desk = await DeskAsync(watch);
        await using var view = await desk.Engine.MaterializeAsync(
            ByDesk, Options(ViewMaintenance.Incremental, margin: 0.05), Ct);
        var planned = view.Statement.PlanDigest;

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();
        Assert.Equal(Counts(drifted: 1, planned: 1), desk.Engine.LastBackgroundPlans);
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await view.UpdateAsync(Ct);

        Assert.NotEqual(planned, view.Statement.PlanDigest);
        Assert.True(view.MaintainsCurrentPlan);
        var sent = Assert.Single(watch.Answers, a => a.Plan.PlanDigest == view.Statement.PlanDigest);
        Assert.True(sent.PlanCost < sent.KnownPlanCost * 0.95, $"the new plan costs {sent.PlanCost}, the held one {sent.KnownPlanCost}");
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), await ViewRowsAsync(view));
    }

    /// <summary>
    /// A planner that does not keep the plan held, as one that has restarted since, cannot price it: no
    /// margin holds the new plan back, and it is taken. The tables are a source no other test reads: a
    /// plan's digest names no engine, so the sidecar these tests share would otherwise keep the same
    /// plan for another test's view.
    /// </summary>
    [Fact]
    public async Task A_margin_against_a_plan_the_planner_does_not_keep_takes_the_new_plan()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var watch = new PlannerWatch(sidecar.CreatePlanner()) { Forgetting = true };
        await using var desk = await DeskAsync(watch, name: "unkept");
        await using var view = await desk.Engine.MaterializeAsync(
            ByDesk, Options(ViewMaintenance.Incremental, margin: 0.95), Ct);
        watch.Forgetting = false;
        var planned = view.Statement.PlanDigest;

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await view.UpdateAsync(Ct);

        Assert.NotEqual(planned, view.Statement.PlanDigest);
        Assert.DoesNotContain(watch.Answers, a => a.Held);
        var sent = Assert.Single(watch.Answers, a => a.Plan.PlanDigest == view.Statement.PlanDigest);
        Assert.NotNull(sent.PlanCost);
        Assert.Null(sent.KnownPlanCost);
    }

    /// <summary>A host's veto comes before the margin: a plan it vetoes is not requested, so not priced.</summary>
    [Fact]
    public async Task A_veto_comes_before_the_margin()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var host = new Host { Willing = false };
        var watch = new PlannerWatch(sidecar.CreatePlanner());
        await using var desk = await DeskAsync(watch);
        await using var view = await desk.Engine.MaterializeAsync(
            ByDesk, Options(ViewMaintenance.Incremental, refresh: host.Refresh, margin: 0.05), Ct);
        var planned = view.Statement.PlanDigest;

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await view.UpdateAsync(Ct);

        Assert.Equal(planned, view.Statement.PlanDigest);
        Assert.True(host.Asked > 0);
        Assert.Equal(0, watch.Replans);
    }

    /// <summary>A margin is a fraction of the held plan's cost, from none up to all of it; anything else is refused when the view is made.</summary>
    [Theory]
    [InlineData(1d)]
    [InlineData(1.5d)]
    [InlineData(-0.01d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task A_margin_that_is_not_a_fraction_below_one_is_refused(double margin)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();

        var refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await desk.Engine.MaterializeAsync(ByDesk, Options(ViewMaintenance.Incremental, margin: margin), Ct));
        Assert.Contains("PlanImprovementMargin", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host that made a view of its own statement follows the statement the view runs, rather than
    /// prepare its own: its own is the view's until a table it reads changes shape, and then the view's
    /// is the one it prepared in its place, which is current and gives the view's rows.
    /// </summary>
    [Fact]
    public async Task A_host_follows_the_statement_the_view_runs_after_a_change_of_shape()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (source, connection) = Database();
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner() }, Ct);
        var held = await engine.PrepareAsync(
            "SELECT id, name FROM db.widget",
            new PrepareOptions
            {
                JoinPolicy = new CrossSourceJoinPolicy { LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows },
            },
            Ct);
        await using var view = await engine.MaterializeAsync(held, WidgetOptions(), Ct);
        Assert.Same(held, view.Statement);

        Execute(connection, "ALTER TABLE widget ADD COLUMN price REAL");
        Execute(connection, "INSERT INTO widget (id, name) VALUES (4, 'four')");
        await engine.RefreshAsync(Ct);
        await view.UpdateAsync(Ct);

        Assert.True(held.IsStale);
        Assert.True(held.ChangedShape);
        var follows = view.Statement;
        Assert.NotSame(held, follows);
        Assert.False(follows.IsStale);
        Assert.Equal(await IdsAsync(engine, follows), [.. (await ViewRowsAsync(view)).Keys]);
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

        refused = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await desk.Engine.MaterializeAsync(statement, Options(ViewMaintenance.Recompute, margin: 0.1), Ct));
        Assert.Contains("carries another view's RefreshQueryPlan or PlanImprovementMargin", refused.Message, StringComparison.Ordinal);

        await using (var without = await desk.Engine.MaterializeAsync(statement, Options(ViewMaintenance.Recompute), Ct))
        {
        }

        await first.DisposeAsync();
        await using var second = await desk.Engine.MaterializeAsync(
            statement, Options(ViewMaintenance.Recompute, refresh: new Host().Refresh), Ct);
    }

    // ---- the parts ----

    /// <summary>What a background run should have done with the statements it found drifted.</summary>
    private static ChalkEngine.BackgroundPlans Counts(
        int drifted, int planned = 0, int unchanged = 0, int held = 0, int vetoed = 0) =>
        new(drifted, planned, unchanged, held, vetoed, NotTheSame: 0, Failed: 0);

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

    /// <summary>
    /// The sidecar's planner, watched: every answer it gives, and the requests that plan a statement
    /// again, naming the plan it runs, apart from a view's request to keep that plan. While
    /// <see cref="Forgetting"/>, a request that asks for prices is answered as though the plan it names
    /// were the one the planner came to, and never reaches the planner, which so keeps nothing: a
    /// planner that has restarted since.
    /// </summary>
    private sealed class PlannerWatch(IQueryPlanner inner) : IQueryPlanner
    {
        private readonly ConcurrentQueue<PlanResult> _answers = new();
        private int _replans;
        private volatile bool _forgetting;

        public bool Forgetting
        {
            get => _forgetting;
            set => _forgetting = value;
        }

        public IReadOnlyList<PlanResult> Answers => [.. _answers];

        public int Replans => Volatile.Read(ref _replans);

        public bool AcceptsCatalogDeltas => inner.AcceptsCatalogDeltas;

        public ValueTask<PlannerInfo> GetInfoAsync(CancellationToken ct = default) => inner.GetInfoAsync(ct);

        public ValueTask RegisterCatalogAsync(CatalogRegistration registration, CancellationToken ct = default) =>
            inner.RegisterCatalogAsync(registration, ct);

        public ValueTask RegisterStatisticsAsync(StatisticsRegistration statistics, CancellationToken ct = default) =>
            inner.RegisterStatisticsAsync(statistics, ct);

        public async ValueTask<PlanResult> PlanAsync(PlanRequest request, CancellationToken ct = default)
        {
            var keep = request.PricePlans && request.CheaperBy is null;
            if (request.KnownPlanDigest != 0 && !keep)
            {
                Interlocked.Increment(ref _replans);
            }

            if (Forgetting && request.PricePlans)
            {
                return new PlanResult { Plan = new Chalk.Ir.Plan(), Unchanged = true };
            }

            var result = await inner.PlanAsync(request, ct).ConfigureAwait(false);
            _answers.Enqueue(result);
            return result;
        }

        public ValueTask<RedactedSql> RedactSqlAsync(RedactSqlRequest request, CancellationToken ct = default) =>
            inner.RedactSqlAsync(request, ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static ViewOptions<One, One> Options(
        ViewMaintenance maintenance, Func<bool>? refresh = null, IViewCostTracker? tracker = null, double? margin = null) => new()
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
        PlanImprovementMargin = margin,
    };

    /// <summary>A view of the widget table's ids, read from its database when the engine refreshes.</summary>
    private static ViewOptions<One, One> WidgetOptions(Func<bool>? refresh = null) => new()
    {
        Key = ["id"],
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        DatabaseReads = ViewDatabaseReads.WhenRefreshed,
        Map = static (in ViewRow row, out One key, out One value) =>
        {
            key = new One { Value = row.GetInt64(0) };
            value = key;
        },
        RefreshQueryPlan = refresh,
    };

    private static async Task<HashSet<long>> IdsAsync(ChalkEngine engine, PreparedQuery query)
    {
        var ids = new HashSet<long>();
        await using var execution = await engine.ExecuteAsync(query, (IReadOnlyList<object?>?)null, ct: Ct);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    ids.Add(Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(0), i)));
                }
            }
        }

        return ids;
    }

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

    private async Task<Desk> DeskAsync(IQueryPlanner? planner = null, string name = "trading")
    {
        var source = new PocoSourceBuilder(name)
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
