using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Low-jitter plan swaps (design 70 §2): a view whose statement takes a new plan builds it beside the
/// one it runs from the moment the plan swaps in, keeps updating the one it runs meanwhile, and swaps
/// the new one in at the first update after the build, which catches it up rather than computing it
/// from nothing, readers keeping their place. A refresh that plans now moves the view so. A newer plan
/// abandons the build for one on that plan; a build that fails keeps the plan the view runs; a tenfold
/// drift is planned again in place, and moves the view as any new plan does, or rebuilds nothing when
/// the plan comes back the same; disposing the view stops the build; and a view that recomputes
/// refuses the option.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class ViewLowJitterTests(SharedSidecar sidecar)
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

    [Fact]
    public async Task A_view_builds_its_new_plan_beside_the_one_it_runs_and_swaps_it_in()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var tracker = new Tracker();
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(tracker), Ct);
        var sink = new Recorder();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, sink);
        var planned = view.Statement.PlanDigest;

        // Running the plan its statement runs, the view builds nothing beside it.
        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "books", [new Book(0, 0)]), Ct);
        await view.UpdateAsync(Ct);
        Assert.Null(view.GreenBuild);

        // The statement takes a new plan. The view keeps updating the plan it runs, incrementally,
        // while the new one is built beside it: here, held until after the update.
        var built = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.HoldGreenBuild = stop => built.Task.WaitAsync(stop);
        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        var swapped = view.Statement.PlanDigest;
        Assert.NotEqual(planned, swapped);

        await view.UpdateAsync(Ct);
        Assert.False(view.MaintainsCurrentPlan);
        var during = tracker.Updates[^1];
        Assert.False(during.Whole);
        Assert.Equal(planned, during.PlanDigest);
        Assert.NotNull(view.GreenBuild);
        built.SetResult();
        await view.GreenBuild!;
        Assert.True(view.StateBytes > 0);
        view.Drain(ref cursor, sink);
        sink.Calls.Clear();

        // The first update after the build runs the new plan, caught up rather than computed from
        // nothing, and readers are sent the one key whose row moved.
        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", [new Fill(3_001, 1, 5)]), Ct);
        await view.UpdateAsync(Ct);

        Assert.True(view.MaintainsCurrentPlan);
        Assert.Null(view.GreenBuild);
        var swap = tracker.Updates[^1];
        Assert.False(swap.Whole);
        Assert.Equal(swapped, swap.PlanDigest);
        Assert.Equal(1, swap.Changes);
        view.Drain(ref cursor, sink);
        Assert.Equal(["upsert 1", $"commit {view.Version}"], sink.Calls);
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), sink.Rows);

        // And it goes on from the new plan.
        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", [new Fill(3_002, 2, 7)]), Ct);
        await view.UpdateAsync(Ct);
        Assert.False(tracker.Updates[^1].Whole);
        Assert.Equal(swapped, tracker.Updates[^1].PlanDigest);
        view.Drain(ref cursor, sink);
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), sink.Rows);
    }

    /// <summary>
    /// Without a low-jitter swap, the update after the statement takes a new plan computes it from
    /// nothing: the stall a low-jitter swap takes off the update.
    /// </summary>
    [Fact]
    public async Task Without_it_the_update_after_a_new_plan_computes_it_from_nothing()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var tracker = new Tracker();
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(
            ByDesk, Options(tracker, ViewPlanSwap.AtNextUpdate), Ct);

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await view.UpdateAsync(Ct);

        Assert.True(view.MaintainsCurrentPlan);
        Assert.Null(view.GreenBuild);
        Assert.True(tracker.Updates[^1].Whole);
        Assert.Equal(view.Statement.PlanDigest, tracker.Updates[^1].PlanDigest);
    }

    [Fact]
    public async Task A_newer_plan_abandons_the_build_for_one_on_that_plan()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var tracker = new Tracker();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(tracker), Ct);
        view.HoldGreenBuild = async stop =>
        {
            Interlocked.Increment(ref builds);
            await release.Task.WaitAsync(stop);
        };

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        var first = view.Statement.PlanDigest;
        await view.UpdateAsync(Ct);
        var abandoned = view.GreenBuild;
        Assert.NotNull(abandoned);

        // The tables trade sizes, and the statement takes another plan while the first is held.
        await desk.LoadAsync(Fills(3, books: 3), Books(30_000));
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        var newest = view.Statement.PlanDigest;
        Assert.NotEqual(first, newest);

        await view.UpdateAsync(Ct);
        Assert.NotSame(abandoned, view.GreenBuild);
        await abandoned!.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        Assert.True(abandoned.IsCanceled, $"the abandoned build ended {abandoned.Status}");

        release.SetResult();
        await view.GreenBuild!;
        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", [new Fill(4, 1, 5)]), Ct);
        await view.UpdateAsync(Ct);

        Assert.Equal(2, builds);
        Assert.True(view.MaintainsCurrentPlan);
        Assert.False(tracker.Updates[^1].Whole);
        Assert.Equal(newest, tracker.Updates[^1].PlanDigest);
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), await ViewRowsAsync(view));
    }

    [Fact]
    public async Task A_build_that_fails_keeps_the_plan_the_view_runs()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var tracker = new Tracker();
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(tracker), Ct);
        view.HoldGreenBuild = static _ => Task.FromException(new InvalidOperationException("the build failed"));
        var planned = view.Statement.PlanDigest;

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await view.BuildingAtSwap;
        await view.GreenBuild!.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);

        // The failure is found at the next update, which keeps the plan the view runs, and asks for no
        // other build on the plan the statement runs.
        await view.UpdateAsync(Ct);
        Assert.Null(view.GreenBuild);
        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", [new Fill(3_001, 1, 5)]), Ct);
        await view.UpdateAsync(Ct);
        Assert.Null(view.GreenBuild);
        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", [new Fill(3_002, 2, 7)]), Ct);
        await view.UpdateAsync(Ct);

        Assert.Null(view.GreenBuild);
        Assert.NotEqual(planned, view.Statement.PlanDigest);
        Assert.All(tracker.Updates.Skip(1), update => Assert.Equal(planned, update.PlanDigest));
        Assert.All(tracker.Updates.Skip(1), update => Assert.False(update.Whole));
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), await ViewRowsAsync(view));
    }

    /// <summary>
    /// A tenfold drift, planning left in the refresh, makes the statement stale, and the view plans it
    /// again in place rather than prepare it anew on its update's thread: it keeps its statement and its
    /// plan, the background run plans it anew, a refresh swaps the plan in, and the view moves to it as
    /// to any — built beside from the swap, or whole at its next update.
    /// </summary>
    [Theory]
    [InlineData(ViewPlanSwap.LowJitter)]
    [InlineData(ViewPlanSwap.AtNextUpdate)]
    public async Task A_tenfold_drift_is_planned_again_in_place(ViewPlanSwap swap)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var tracker = new Tracker();
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(tracker, swap), Ct);
        var statement = view.Statement;
        var planned = statement.PlanDigest;

        await desk.Engine.RefreshAsync(
            r => r.Replace(desk.Source, "fills", Fills(3_000, books: 3)).Replace(desk.Source, "books", Books(3)), Ct);
        Assert.True(statement.IsStale);
        await view.UpdateAsync(Ct);

        Assert.Same(statement, view.Statement);
        Assert.False(tracker.Updates[^1].Whole);
        Assert.Equal(planned, tracker.Updates[^1].PlanDigest);

        await desk.Engine.BackgroundPlanningSettledAsync();
        Assert.Equal(1, desk.Engine.LastBackgroundPlans!.New);
        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", [new Fill(3_001, 1, 5)]), Ct);
        Assert.Same(statement, view.Statement);
        Assert.NotEqual(planned, statement.PlanDigest);
        Assert.False(statement.IsStale);
        if (swap == ViewPlanSwap.LowJitter)
        {
            // Built beside the plan the view runs from the swap, so the update after the refresh takes it.
            await view.BuildingAtSwap;
            await view.GreenBuild!;
        }

        await view.UpdateAsync(Ct);

        Assert.Equal(swap == ViewPlanSwap.AtNextUpdate, tracker.Updates[^1].Whole);
        Assert.Equal(statement.PlanDigest, tracker.Updates[^1].PlanDigest);
        Assert.True(view.MaintainsCurrentPlan);
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), await ViewRowsAsync(view));
    }

    /// <summary>
    /// A tenfold drift that the planner meets with the plan the statement runs rebuilds nothing: the
    /// statement is judged from the new numbers, and no update after it computes from nothing.
    /// </summary>
    [Theory]
    [InlineData(ViewPlanSwap.LowJitter)]
    [InlineData(ViewPlanSwap.AtNextUpdate)]
    public async Task A_tenfold_drift_to_the_same_plan_rebuilds_nothing(ViewPlanSwap swap)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var tracker = new Tracker();
        await using var desk = await DeskAsync();
        await desk.Engine.RefreshAsync(
            r => r.Replace(desk.Source, "fills", Fills(3_000, books: 3)).Replace(desk.Source, "books", Books(3)), Ct);
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(tracker, swap), Ct);
        var statement = view.Statement;
        var planned = statement.PlanDigest;

        await desk.Engine.RefreshAsync(r => r.Replace(desk.Source, "fills", Fills(30_000, books: 3)), Ct);
        Assert.True(statement.IsStale);
        await view.UpdateAsync(Ct);
        await desk.Engine.BackgroundPlanningSettledAsync();

        Assert.Equal(1, desk.Engine.LastBackgroundPlans!.Unchanged);
        Assert.False(statement.IsStale);
        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", [new Fill(30_001, 1, 5)]), Ct);
        await view.UpdateAsync(Ct);

        Assert.Same(statement, view.Statement);
        Assert.Equal(planned, statement.PlanDigest);
        Assert.Null(view.GreenBuild);
        Assert.All(tracker.Updates.Skip(1), update => Assert.False(update.Whole));
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), await ViewRowsAsync(view));
    }

    /// <summary>
    /// The view starts building its statement's new plan as the plan swaps in, before any update, so
    /// that the first update after the refresh can take it: the view runs the new plan an update
    /// sooner than if the build began at an update's end.
    /// </summary>
    [Fact]
    public async Task A_view_starts_building_a_new_plan_as_it_swaps_in()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var tracker = new Tracker();
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(tracker), Ct);
        var sink = new Recorder();
        var cursor = ViewCursor.Start;
        var planned = view.Statement.PlanDigest;
        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, sink);
        sink.Calls.Clear();
        await desk.Engine.BackgroundPlanningSettledAsync();

        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", [new Fill(3_001, 1, 5)]), Ct);
        var swapped = view.Statement.PlanDigest;
        Assert.NotEqual(planned, swapped);
        await view.BuildingAtSwap;
        Assert.NotNull(view.GreenBuild);
        await view.GreenBuild!;
        Assert.Equal(planned, tracker.Updates[^1].PlanDigest);

        await view.UpdateAsync(Ct);

        Assert.True(view.MaintainsCurrentPlan);
        Assert.Null(view.GreenBuild);
        var update = tracker.Updates[^1];
        Assert.False(update.Whole);
        Assert.Equal(swapped, update.PlanDigest);
        view.Drain(ref cursor, sink);
        Assert.Equal(["upsert 1", $"commit {view.Version}"], sink.Calls);
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), sink.Rows);
    }

    /// <summary>
    /// The cutover a host makes to change what a view reads: one refresh replaces the tables and plans
    /// now. The view's next plan is built beside the one it runs as the run's plan swaps in, and the
    /// view's next update runs it, caught up, without computing it from nothing; readers keep their
    /// place.
    /// </summary>
    [Fact]
    public async Task A_refresh_that_plans_now_moves_the_view_without_a_whole_update()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var tracker = new Tracker();
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(tracker), Ct);
        var sink = new Recorder();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, sink);
        sink.Calls.Clear();
        var planned = view.Statement.PlanDigest;

        await desk.Engine.RefreshAsync(
            r => r.Replace(desk.Source, "fills", Fills(3_000, books: 3)).Replace(desk.Source, "books", Books(3)).PlanNow(),
            Ct);
        await desk.Engine.PlannedNow;
        var swapped = view.Statement.PlanDigest;
        Assert.NotEqual(planned, swapped);
        await view.BuildingAtSwap;
        await view.GreenBuild!;

        await view.UpdateAsync(Ct);

        Assert.True(view.MaintainsCurrentPlan);
        var update = tracker.Updates[^1];
        Assert.False(update.Whole);
        Assert.Equal(swapped, update.PlanDigest);
        view.Drain(ref cursor, sink);
        Assert.DoesNotContain("reset", sink.Calls);
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), sink.Rows);
    }

    [Fact]
    public async Task Disposing_the_view_stops_its_build()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        var view = await desk.Engine.MaterializeAsync(ByDesk, Options(new Tracker()), Ct);
        view.HoldGreenBuild = static stop => Task.Delay(Timeout.Infinite, stop);

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await view.UpdateAsync(Ct);
        var build = view.GreenBuild;
        Assert.NotNull(build);

        await view.DisposeAsync();

        Assert.True(build!.IsCanceled, $"the build ended {build.Status}");
    }

    [Fact]
    public async Task A_view_that_recomputes_refuses_low_jitter()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();

        var refused = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await desk.Engine.MaterializeAsync(
                ByDesk, Options(null, ViewPlanSwap.LowJitter, ViewMaintenance.Recompute), Ct));
        Assert.Contains("PlanSwap = LowJitter would do nothing", refused.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await desk.Engine.MaterializeAsync(ByDesk, Options(null, (ViewPlanSwap)7), Ct));
    }

    // ---- the parts ----

    /// <summary>A tracker that keeps every update it is told of.</summary>
    private sealed class Tracker : IViewCostTracker
    {
        public List<ViewUpdate> Updates { get; } = [];

        public void Updated(in ViewUpdate update) => Updates.Add(update);
    }

    private static ViewOptions<One, One> Options(
        Tracker? tracker,
        ViewPlanSwap swap = ViewPlanSwap.LowJitter,
        ViewMaintenance maintenance = ViewMaintenance.Incremental) => new()
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
        Tracker = tracker,
        PlanSwap = swap,
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

    private async Task<Desk> DeskAsync()
    {
        var source = new PocoSourceBuilder("trading")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("fills", (IReadOnlyList<Fill>)[])
            .AddTable("books", (IReadOnlyList<Book>)[])
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner() },
            Ct);
        return new Desk { Engine = engine, Source = source };
    }

    /// <summary>A sink that records each call by key, and keeps the rows it was sent.</summary>
    private sealed class Recorder : IViewSink<One, One>
    {
        public List<string> Calls { get; } = [];

        public Dictionary<long, long> Rows { get; } = [];

        public void Reset()
        {
            Calls.Add("reset");
            Rows.Clear();
        }

        public void Upsert(in One key, in One row)
        {
            Calls.Add($"upsert {key.Value}");
            Rows[key.Value] = row.Value;
        }

        public void Delete(in One key)
        {
            Calls.Add($"delete {key.Value}");
            Rows.Remove(key.Value);
        }

        public void Commit(long version, uint checksum) => Calls.Add($"commit {version}");
    }
}
