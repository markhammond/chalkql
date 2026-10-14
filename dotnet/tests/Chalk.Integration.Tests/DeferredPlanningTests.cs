using Chalk.Catalog;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A refresh that leaves its statistics and plans to the background (design 69 §4): nothing reaches
/// the planner under the refresh lock, one run at a time computes the statistics and plans again the
/// statements whose tables moved twofold, and all of it takes effect at the next refresh, never
/// underneath one.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class DeferredPlanningTests(SharedSidecar sidecar)
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

    /// <summary>
    /// The planner's statistics messages are held at a gate throughout, and a deferred refresh still
    /// completes: it sent none. Its rows are published with the table's statistics as they were, the
    /// row count exact; the statistics computed in the background enter the catalog at the next
    /// refresh, and not before.
    /// </summary>
    [Fact]
    public async Task A_deferred_refresh_sends_nothing_and_computes_no_statistics()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var planner = new GatedPlanner(sidecar.CreatePlanner());
        await using var desk = await DeskAsync(planner, Fills(100, books: 10), Books(10));
        Assert.Equal(7L, QtyMax(desk.Engine));

        planner.Hold();
        var plans = planner.Plans;
        await desk.Engine
            .RefreshAsync(r => r.Append(desk.Source, "fills", Fills(50, books: 10, qty: 99_999)).DeferPlanning(), Ct)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(150, RowCount(desk.Engine, "fills"));
        Assert.Equal(7L, QtyMax(desk.Engine));
        Assert.Equal(plans, planner.Plans);

        planner.Release();
        await desk.Engine.BackgroundPlanningSettledAsync();
        Assert.True(desk.Engine.BackgroundPlanningStaged);
        Assert.Equal(7L, QtyMax(desk.Engine));

        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        Assert.Equal(99_999L, QtyMax(desk.Engine));
        Assert.False(desk.Engine.BackgroundPlanningStaged);
    }

    /// <summary>
    /// A statement prepared on empty tables runs the plan they chose until the refresh after the
    /// background run, and then the plan a prepare makes now: another plan digest, the same logical
    /// digest, current again, and the same rows before and after.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_plan_swaps_in_at_the_next_refresh(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync(new GatedPlanner(sidecar.CreatePlanner()), [], [], executor);
        var statement = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        var planned = statement.PlanDigest;

        await desk.LoadAsync(Fills(5_000, books: 50), Books(50), deferred: true);
        await desk.Engine.BackgroundPlanningSettledAsync();
        Assert.Equal(planned, statement.PlanDigest);
        var rowsBefore = await RowsAsync(desk.Engine, statement);

        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);

        var prepared = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        Assert.NotEqual(planned, statement.PlanDigest);
        Assert.Equal(prepared.PlanDigest, statement.PlanDigest);
        Assert.Equal(prepared.LogicalDigest, statement.LogicalDigest);
        Assert.False(statement.IsStale);
        Assert.Equal(rowsBefore, await RowsAsync(desk.Engine, statement));
        Assert.Equal(await RowsAsync(desk.Engine, prepared), await RowsAsync(desk.Engine, statement));
    }

    /// <summary>An execution running when the plan swaps in finishes on the plan it began with.</summary>
    [Fact]
    public async Task An_execution_running_at_the_swap_keeps_its_plan()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync(new GatedPlanner(sidecar.CreatePlanner()), [], [], batchSize: 1);
        var statement = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        var planned = statement.PlanDigest;
        await desk.LoadAsync(Fills(5_000, books: 50), Books(50), deferred: true);
        await desk.Engine.BackgroundPlanningSettledAsync();

        await using var execution = await desk.Engine.ExecuteAsync(statement, (IReadOnlyList<object?>?)null, ct: Ct);
        await using var batches = execution.Batches.GetAsyncEnumerator(Ct);
        Assert.True(await batches.MoveNextAsync());
        var rows = batches.Current.Length;
        batches.Current.Dispose();

        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        Assert.NotEqual(planned, statement.PlanDigest);

        while (await batches.MoveNextAsync())
        {
            rows += batches.Current.Length;
            batches.Current.Dispose();
        }

        Assert.Equal(planned, execution.Plan.PlanDigest);
        Assert.Equal(3, rows);
    }

    /// <summary>
    /// One run at a time: refreshes that defer while one is running ask for one more, which starts
    /// when it ends, over what is published then.
    /// </summary>
    [Fact]
    public async Task Refreshes_during_a_run_ask_for_one_more()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var planner = new GatedPlanner(sidecar.CreatePlanner());
        await using var desk = await DeskAsync(planner, Fills(100, books: 10), Books(10));
        planner.Hold();

        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", Fills(10, books: 10, qty: 100)).DeferPlanning(), Ct);
        await planner.Arrived.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", Fills(10, books: 10, qty: 200)).DeferPlanning(), Ct);
        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", Fills(10, books: 10, qty: 300)).DeferPlanning(), Ct);
        Assert.Equal(1, desk.Engine.BackgroundPlanningRuns);

        planner.Release();
        await desk.Engine.BackgroundPlanningSettledAsync();
        Assert.Equal(2, desk.Engine.BackgroundPlanningRuns);

        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        Assert.Equal(300L, QtyMax(desk.Engine));
    }

    /// <summary>
    /// A statement an extension decorated without a check on later plans keeps the plan it was
    /// handed: the background run would hand it one nothing checked. Its twin takes the new plan. An
    /// extension that checks later plans takes part, as entitlements do (<c>EntitledPlansTests</c>).
    /// </summary>
    [Fact]
    public async Task A_statement_decorated_without_a_plan_check_keeps_its_plan()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync(new GatedPlanner(sidecar.CreatePlanner()), [], []);
        var checkedOne = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        checkedOne.Decorate(schema => schema, (_, _) => { });
        var plain = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        var planned = plain.PlanDigest;

        await desk.LoadAsync(Fills(5_000, books: 50), Books(50), deferred: true);
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);

        Assert.Equal(planned, checkedOne.PlanDigest);
        Assert.NotEqual(planned, plain.PlanDigest);
        Assert.True(checkedOne.IsStale);
    }

    /// <summary>
    /// A run that fails leaves the plans as they were and nothing to take in; the next refresh that
    /// defers runs again, and that run's work takes effect.
    /// </summary>
    [Fact]
    public async Task A_failed_run_leaves_the_plans_as_they_were()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var planner = new GatedPlanner(sidecar.CreatePlanner());
        await using var desk = await DeskAsync(planner, [], []);
        var statement = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        var planned = statement.PlanDigest;

        planner.FailNext(new IOException("the planner went away"));
        await desk.LoadAsync(Fills(5_000, books: 50), Books(50), deferred: true);
        await desk.Engine.BackgroundPlanningSettledAsync();
        Assert.False(desk.Engine.BackgroundPlanningStaged);
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        Assert.Equal(planned, statement.PlanDigest);

        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        Assert.NotEqual(planned, statement.PlanDigest);
    }

    /// <summary>
    /// A re-read describes its tables with their statistics now, the opposite of deferring them, so
    /// the two in one refresh are refused before anything is applied.
    /// </summary>
    [Fact]
    public async Task A_refresh_that_defers_and_re_reads_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync(new GatedPlanner(sidecar.CreatePlanner()), Fills(10, books: 2), Books(2));
        var epoch = desk.Engine.Catalog.Epoch;

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            async () => await desk.Engine.RefreshAsync(
                r => r.Refresh(Chalk.Sources.RefreshTarget.Source("trading"))
                    .Append(desk.Source, "fills", Fills(1, books: 2))
                    .DeferPlanning(),
                Ct));

        Assert.Contains("defers its planning and re-reads source 'trading'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(epoch, desk.Engine.Catalog.Epoch);
        Assert.Equal(10, RowCount(desk.Engine, "fills"));
    }

    /// <summary>
    /// A standing view takes its statement's new plan at its first update after the refresh that
    /// swapped it in, and keeps its readers: the result's columns and key are the ones it had.
    /// </summary>
    [Fact]
    public async Task A_standing_view_takes_its_new_plan_and_keeps_its_readers()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync(new GatedPlanner(sidecar.CreatePlanner()), [], []);
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, DeskOptions(), Ct);
        var sink = new Recorder();
        var cursor = ViewCursor.Start;
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        sink.Calls.Clear();
        var statement = view.Statement;
        var planned = statement.PlanDigest;

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3), deferred: true);
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await view.UpdateAsync(Ct);

        Assert.Same(statement, view.Statement);
        Assert.NotEqual(planned, view.Statement.PlanDigest);
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        Assert.DoesNotContain("reset", sink.Calls);
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), sink.Rows);
    }

    /// <summary>
    /// A view kept by incremental maintenance takes the new plan too, at the same update: it is
    /// maintained from the new plan from the start, and its readers keep their place, sent only the
    /// keys whose rows differ from what they hold.
    /// </summary>
    [Fact]
    public async Task A_maintained_view_takes_its_new_plan_and_keeps_its_readers()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync(new GatedPlanner(sidecar.CreatePlanner()), [], []);
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, DeskOptions(ViewMaintenance.Incremental), Ct);
        var sink = new Recorder();
        var cursor = ViewCursor.Start;
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        sink.Calls.Clear();
        var statement = view.Statement;
        var planned = statement.PlanDigest;
        Assert.True(view.MaintainsCurrentPlan);

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3), deferred: true);
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        Assert.False(view.MaintainsCurrentPlan);
        await view.UpdateAsync(Ct);

        Assert.True(view.MaintainsCurrentPlan);
        Assert.Same(statement, view.Statement);
        Assert.NotEqual(planned, view.Statement.PlanDigest);
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        Assert.DoesNotContain("reset", sink.Calls);
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), sink.Rows);
    }

    /// <summary>
    /// A statement whose tables moved twofold is planned again in the background, naming the plan it
    /// runs; when the planner comes to the same plan it says so and sends none. The statement keeps the
    /// plan it has, judges drift from the numbers it was planned on again, and a view maintained from
    /// it keeps on with no new start.
    /// </summary>
    [Fact]
    public async Task A_plan_the_background_comes_to_again_is_kept()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        const string ByBook = "SELECT f.book AS desk, SUM(f.qty) AS qty FROM fills AS f GROUP BY f.book";
        var planner = new GatedPlanner(sidecar.CreatePlanner());
        await using var desk = await DeskAsync(planner, Fills(100, books: 10), Books(10));
        await using var view = await desk.Engine.MaterializeAsync(ByBook, DeskOptions(ViewMaintenance.Incremental), Ct);
        var sink = new Recorder();
        var cursor = ViewCursor.Start;
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        sink.Calls.Clear();
        var statement = view.Statement;
        var plans = statement.Plans;
        var planned = statement.PlanDigest;

        await desk.LoadAsync(Fills(5_000, books: 10), Books(10), deferred: true);
        Assert.True(statement.DriftedFrom(desk.Engine.Catalog));
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);

        Assert.True(planner.Unchanged >= 1, $"the planner answered {planner.Unchanged} plans unchanged");
        Assert.Same(plans, statement.Plans);
        Assert.Equal(planned, statement.PlanDigest);
        Assert.False(statement.DriftedFrom(desk.Engine.Catalog));
        Assert.True(view.MaintainsCurrentPlan);
        await view.UpdateAsync(Ct);
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        Assert.DoesNotContain("reset", sink.Calls);
        Assert.Equal(await RowsAsync(desk.Engine, statement), sink.Rows);
    }

    /// <summary>
    /// A deferred refresh that leaves every table within a tenth of the numbers the planner holds asks
    /// for no background run (D351): the catalog's row count is exact and its statistics stand. The
    /// refresh that takes a table past the tenth asks for one, and its statistics follow.
    /// </summary>
    [Fact]
    public async Task A_deferred_refresh_within_a_tenth_asks_for_no_run()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var planner = new GatedPlanner(sidecar.CreatePlanner());
        await using var desk = await DeskAsync(planner, Fills(100, books: 10), Books(10));

        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", Fills(9, books: 10, qty: 50_000)).DeferPlanning(), Ct);
        await desk.Engine.BackgroundPlanningSettledAsync();
        Assert.Equal(0, desk.Engine.BackgroundPlanningRuns);
        Assert.Equal(109, RowCount(desk.Engine, "fills"));
        Assert.Equal(7L, QtyMax(desk.Engine));

        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", Fills(1, books: 10, qty: 99_999)).DeferPlanning(), Ct);
        await desk.Engine.BackgroundPlanningSettledAsync();
        Assert.Equal(1, desk.Engine.BackgroundPlanningRuns);

        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await desk.Engine.BackgroundPlanningSettledAsync();
        Assert.Equal(99_999L, QtyMax(desk.Engine));
        Assert.Equal(1, desk.Engine.BackgroundPlanningRuns);
    }

    /// <summary>
    /// Numbers a refresh that does not defer sent are new to the held statements: the next deferred
    /// refresh asks for a run, though it moved nothing itself, and the statements whose tables moved
    /// are planned again on them.
    /// </summary>
    [Fact]
    public async Task Numbers_sent_by_a_refresh_that_did_not_defer_are_judged_at_the_next_run()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync(new GatedPlanner(sidecar.CreatePlanner()), [], []);
        var statement = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        var planned = statement.PlanDigest;

        await desk.LoadAsync(Fills(5_000, books: 50), Books(50), deferred: false);
        await desk.Engine.BackgroundPlanningSettledAsync();
        Assert.Equal(0, desk.Engine.BackgroundPlanningRuns);

        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await desk.Engine.BackgroundPlanningSettledAsync();
        Assert.Equal(1, desk.Engine.BackgroundPlanningRuns);

        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await desk.Engine.BackgroundPlanningSettledAsync();
        Assert.NotEqual(planned, statement.PlanDigest);
        Assert.Equal(1, desk.Engine.BackgroundPlanningRuns);
    }

    // ---- the parts ----

    private static IReadOnlyList<Fill> Fills(int count, int books, long? qty = null) =>
        [.. Enumerable.Range(1, count).Select(i => new Fill(i, i % books, qty ?? (i % 7) + 1))];

    private static IReadOnlyList<Book> Books(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Book(i, i % 3))];

    private static long RowCount(ChalkEngine engine, string table) =>
        engine.Catalog.Schemas.Single(s => s.SourceId == "trading").FindTable(table)!.RowCount;

    /// <summary>The largest <c>qty</c> the catalog's statistics know of.</summary>
    private static long QtyMax(ChalkEngine engine) =>
        Convert.ToInt64(
            engine.Catalog.Schemas.Single(s => s.SourceId == "trading").FindTable("fills")!.Columns
                .Single(c => c.Name == "qty").Statistics.Max,
            System.Globalization.CultureInfo.InvariantCulture);

    private static ViewOptions<One, One> DeskOptions(ViewMaintenance maintenance = ViewMaintenance.Recompute) => new()
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
    };

    /// <summary>A statement's rows, the first column to the second, as numbers.</summary>
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

        /// <summary>Replaces both tables in one refresh, as a host's load does.</summary>
        public async Task LoadAsync(IReadOnlyList<Fill> fills, IReadOnlyList<Book> books, bool deferred)
        {
            await Engine.RefreshAsync(
                refresh =>
                {
                    refresh.Replace(Source, "fills", fills);
                    refresh.Replace(Source, "books", books);
                    if (deferred)
                    {
                        refresh.DeferPlanning();
                    }
                },
                Ct);
        }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private static async Task<Desk> DeskAsync(
        IQueryPlanner planner,
        IReadOnlyList<Fill> fills,
        IReadOnlyList<Book> books,
        ExecutionEngine executor = ExecutionEngine.Vectorised,
        int batchSize = 1024)
    {
        var source = new PocoSourceBuilder("trading")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("fills", fills)
            .AddTable("books", books)
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions
            {
                Sources = [source],
                Planner = planner,
                Execution = new ExecutionOptions { Engine = executor, BatchSize = batchSize },
            },
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

    /// <summary>
    /// The sidecar, with a gate its statistics messages can be held at and a failure the next one
    /// can be given, counting the plans it is asked for.
    /// </summary>
    private sealed class GatedPlanner(IQueryPlanner inner) : IQueryPlanner
    {
        private TaskCompletionSource? _held;
        private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _failNext;
        private int _plans;
        private int _unchanged;

        public int Plans => Volatile.Read(ref _plans);

        /// <summary>Plans the planner answered unchanged, as the caller held them already.</summary>
        public int Unchanged => Volatile.Read(ref _unchanged);

        /// <summary>Completes when a statistics message reaches the gate while it is held.</summary>
        public Task Arrived => Volatile.Read(ref _arrived).Task;

        public void Hold()
        {
            Volatile.Write(ref _arrived, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref _held, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public void Release() => Interlocked.Exchange(ref _held, null)?.TrySetResult();

        public void FailNext(Exception failure) => Volatile.Write(ref _failNext, failure);

        public bool AcceptsCatalogDeltas => inner.AcceptsCatalogDeltas;

        public ValueTask<PlannerInfo> GetInfoAsync(CancellationToken ct = default) => inner.GetInfoAsync(ct);

        public ValueTask RegisterCatalogAsync(CatalogRegistration registration, CancellationToken ct = default) =>
            inner.RegisterCatalogAsync(registration, ct);

        public async ValueTask RegisterStatisticsAsync(StatisticsRegistration statistics, CancellationToken ct = default)
        {
            if (Volatile.Read(ref _held) is { } held)
            {
                Volatile.Read(ref _arrived).TrySetResult();
                await held.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            if (Interlocked.Exchange(ref _failNext, null) is { } failure)
            {
                throw failure;
            }

            await inner.RegisterStatisticsAsync(statistics, ct).ConfigureAwait(false);
        }

        public async ValueTask<PlanResult> PlanAsync(PlanRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _plans);
            var result = await inner.PlanAsync(request, ct).ConfigureAwait(false);
            if (result.Unchanged)
            {
                Interlocked.Increment(ref _unchanged);
            }

            return result;
        }

        public ValueTask<RedactedSql> RedactSqlAsync(RedactSqlRequest request, CancellationToken ct = default) =>
            inner.RedactSqlAsync(request, ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
