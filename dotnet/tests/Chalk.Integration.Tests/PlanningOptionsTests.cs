using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Planning options end to end against a real sidecar (D234–D238,
/// <c>docs/design/30-planning-options.md</c>): the state on a prepared query, a cooperative stop, a
/// cancelled prepare, the cache key, and a narrowing governed exactly as a prepare is.
/// </summary>
/// <remarks>
/// <para><b>No test here asserts a duration.</b> What is asserted is the termination reason, the
/// presence of a state, and evaluation counts, which are counts of rule matches and not of time.</para>
/// <para>
/// The statement is a four-join federation query — the most joins Volcano enumerates before the
/// heuristic pass takes over, across two sources so every side has a remote alternative as well as a
/// local one. It is the longest search this repository has, which is what makes a stop landing
/// mid-search something a test can rely on.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class PlanningOptionsTests(SharedSidecar sidecar)
{
    private const string LongStatement =
        "SELECT n.n_name, o.o_orderdate, SUM(l.l_extendedprice * (1 - l.l_discount)) AS revenue"
        + " FROM customer c, duck.orders o, lineitem l, sqlite.supplier s, nation n"
        + " WHERE c.c_custkey = o.o_custkey AND l.l_orderkey = o.o_orderkey"
        + " AND l.l_suppkey = s.s_suppkey AND s.s_nationkey = n.n_nationkey"
        + " AND o.o_orderdate >= DATE '1994-01-01'"
        + " GROUP BY n.n_name, o.o_orderdate ORDER BY revenue DESC, n.n_name";

    private Task<ChalkEngine> CreateEngineAsync() =>
        FederationCorpusTests.CreateEngineAsync(sidecar, ExecutionEngine.Vectorised);

    /// <summary>
    /// A prepare that sets no option still reports a full state (D240): the sidecar's bounded
    /// worker pool gives every planning a governor now, whatever its options, so the evaluation
    /// count and the slice count are real. The default cadence (20 ms) is far longer than this
    /// search usually takes, so a look is rare — deliberately not asserted here, along with
    /// whether a cost was ever sampled, because both are wall-clock: on a slower run a look can
    /// land early (nothing sampled yet) or late (the root already complete), and either is a
    /// correct report of what actually happened, not a fact this test has any business pinning
    /// down. What is invariant is the mode (period, not count) and that it converged.
    /// </summary>
    [Fact]
    public async Task A_default_prepare_reports_a_full_state_with_nothing_sampled()
    {
        await using var engine = await CreateEngineAsync();

        var prepared = await engine.PrepareAsync(
            LongStatement,
            new PrepareOptions { IncludePlanText = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(PlanningTerminationReason.Converged, prepared.PlanningState.TerminationReason);
        Assert.True(prepared.PlanningState.EvaluationCount > 0);
        Assert.Null(prepared.PlanningState.EvaluationInterval);

        // D240, D242: at least the one slice every planning runs in, and that time counted as
        // running rather than queued, because the fixture's own worker was free to grant it at
        // once. Not asserted as exactly one, for the same reason above.
        Assert.True(prepared.PlanningState.Slices >= 1);
        Assert.True(prepared.PlanningState.RunningElapsed > TimeSpan.Zero);

        // The plan text now says how the search ended for every prepare, not only a governed one —
        // there is no "ungoverned" any more at this level (D240).
        Assert.NotNull(prepared.PlanText);
        Assert.Contains("-- planning: converged after", prepared.PlanText, StringComparison.Ordinal);
    }

    /// <summary>The fixture's one worker (D243) is what GetInfo reports back, over the real wire.</summary>
    [Fact]
    public async Task The_fixtures_one_worker_is_in_force()
    {
        await using var planner = sidecar.CreatePlanner();

        var info = await planner.GetInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1u, info.PlanningWorkers);
    }

    /// <summary>
    /// Two prepares at once, from one engine, over the fixture's single worker (D240, D241, D242):
    /// both complete, each with its own plan and its own state, and the sidecar does not confuse one
    /// planning's counters for the other's — the same statement twice is not one governor shared.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_prepares_from_one_engine_both_complete_with_their_own_states()
    {
        await using var engine = await CreateEngineAsync();
        var options = new PrepareOptions
        {
            IncludePlanText = true,
            Planning = new PlanningOptions { ConvergenceEvaluationInterval = 3 },
        };

        var first = engine.PrepareAsync(LongStatement, options, TestContext.Current.CancellationToken).AsTask();
        var second = engine.PrepareAsync(LongStatement, options, TestContext.Current.CancellationToken).AsTask();
        await Task.WhenAll(first, second);

        var a = await first;
        var b = await second;

        // Both are complete, correct plans of the same statement under the same options.
        Assert.Equal(a.PlanDigest, b.PlanDigest);
        Assert.Equal(PlanningTerminationReason.Converged, a.PlanningState.TerminationReason);
        Assert.Equal(PlanningTerminationReason.Converged, b.PlanningState.TerminationReason);
        Assert.True(a.PlanningState.EvaluationCount > 0);
        Assert.True(b.PlanningState.EvaluationCount > 0);
        // One worker: one of the two necessarily spent some time queued behind the other, whichever
        // it was — a structural consequence of contention, not a timing bound.
        Assert.True(a.PlanningState.QueuedElapsed > TimeSpan.Zero || b.PlanningState.QueuedElapsed > TimeSpan.Zero);
    }

    /// <summary>
    /// A named session round-trips over the real wire (D245, D247): the sidecar reports the name
    /// back on the planning state exactly as this prepare named it.
    /// </summary>
    [Fact]
    public async Task A_planning_session_is_named_on_the_wire_and_read_back()
    {
        await using var engine = await CreateEngineAsync();

        var prepared = await engine.PrepareAsync(
            LongStatement,
            new PrepareOptions
            {
                Planning = new PlanningOptions
                {
                    Session = new PlanningSession { Name = "report-export", MaxConcurrency = 1 },
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("report-export", prepared.PlanningState.Session);

        // A prepare that names no session reads back null, not an empty name.
        var unsessioned = await engine.PrepareAsync(
            LongStatement, new PrepareOptions(), TestContext.Current.CancellationToken);
        Assert.Null(unsessioned.PlanningState.Session);
    }

    /// <summary>
    /// A governed prepare counts its evaluations, samples the root's cost, and says so both on the
    /// prepared query and in the plan text.
    /// </summary>
    [Fact]
    public async Task A_governed_prepare_reports_its_counts_and_its_costs()
    {
        await using var engine = await CreateEngineAsync();

        var prepared = await engine.PrepareAsync(
            LongStatement,
            new PrepareOptions
            {
                IncludePlanText = true,
                Planning = new PlanningOptions
                {
                    ConvergencePatience = 3,
                    ConvergenceEvaluationInterval = 5,
                },
            },
            TestContext.Current.CancellationToken);

        var state = prepared.PlanningState;
        Assert.Equal(PlanningTerminationReason.Converged, state.TerminationReason);
        Assert.True(state.EvaluationCount > 0);
        Assert.NotNull(state.FirstCost);
        Assert.NotNull(state.BestCost);
        // The search bought something after its first complete answer: a ratio below 1.
        Assert.NotNull(state.CostRatio);
        Assert.True(state.CostRatio < 1.0);
        Assert.Contains("-- planning: converged after", prepared.PlanText, StringComparison.Ordinal);
        Assert.Contains("ratio ", prepared.PlanText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stop asked for before the prepare begins returns the first complete plan the optimiser
    /// finds — a stop never leaves a caller without a plan (D236) — and gets there in fewer
    /// evaluations than the unstopped search.
    /// </summary>
    [Fact]
    public async Task A_stop_before_any_plan_returns_the_first_plan()
    {
        await using var engine = await CreateEngineAsync();

        using var alreadyStopped = new CancellationTokenSource();
        await alreadyStopped.CancelAsync();

        var stopped = await engine.PrepareAsync(
            LongStatement,
            new PrepareOptions
            {
                Planning = new PlanningOptions
                {
                    StopToken = alreadyStopped.Token,
                    ConvergenceEvaluationInterval = 1,
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(PlanningTerminationReason.StoppedByHost, stopped.PlanningState.TerminationReason);
        Assert.NotEqual(0UL, stopped.PlanDigest);

        // The unstopped search over the same statement, governed only so that it counts.
        var full = await engine.PrepareAsync(
            LongStatement,
            new PrepareOptions
            {
                Planning = new PlanningOptions { ConvergenceEvaluationInterval = 1_000_000, TimeBudget = TimeSpan.FromMinutes(5) },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(PlanningTerminationReason.Converged, full.PlanningState.TerminationReason);
        Assert.True(
            stopped.PlanningState.EvaluationCount < full.PlanningState.EvaluationCount,
            $"stopped at {stopped.PlanningState.EvaluationCount} evaluations, unstopped ran to "
            + $"{full.PlanningState.EvaluationCount}");
    }

    /// <summary>
    /// A stop delivered while a search is in flight ends it with the best complete plan it has
    /// (D236). The stop is polled until the sidecar says it found the planning, so the test never
    /// races the call it means to shorten.
    /// </summary>
    [Fact]
    public async Task A_stop_delivered_mid_search_returns_a_plan()
    {
        await using var engine = await CreateEngineAsync();
        await using var stopper = sidecar.CreatePlanner();

        var requestId = Guid.NewGuid().ToString("n");
        var request = new PlanRequest
        {
            Sql = LongStatement,
            ContextId = engine.Catalog.ContextId,
            CatalogEpoch = engine.Catalog.Epoch,
            PlanningRequestId = requestId,
            Planning = new PlanningOptions { ConvergenceEvaluationInterval = 1 },
        };

        await using var planner = sidecar.CreatePlanner();
        await planner.RegisterCatalogAsync(engine.Catalog, TestContext.Current.CancellationToken);

        var planning = Task.Run(
            async () => await planner.PlanAsync(request, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        var found = false;
        while (!found && !planning.IsCompleted)
        {
            found = await stopper.StopPlanningAsync(requestId, TestContext.Current.CancellationToken);
        }

        var result = await planning;
        Assert.True(found, "the planning finished before any stop could reach it");
        Assert.Equal(
            PlanningTerminationReason.StoppedByHost, result.PlanningState.TerminationReason);
        Assert.NotEqual(0UL, result.PlanDigest);

        // And an id nothing is in flight under is not found, which a client reads as "already
        // finished" rather than as an error.
        Assert.False(
            await stopper.StopPlanningAsync(requestId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A cancelled prepare throws <see cref="OperationCanceledException"/> carrying
    /// <c>CancelledByHost</c> and no plan, and the sidecar stops planning for that id: it is no
    /// longer in flight, and the evaluation count it ended on does not move again.
    /// </summary>
    [Fact]
    public async Task A_cancelled_prepare_throws_and_the_sidecar_stops_counting()
    {
        await using var engine = await CreateEngineAsync();
        await using var observer = sidecar.CreatePlanner();

        var requestId = Guid.NewGuid().ToString("n");
        var request = new PlanRequest
        {
            Sql = LongStatement,
            ContextId = engine.Catalog.ContextId,
            CatalogEpoch = engine.Catalog.Epoch,
            PlanningRequestId = requestId,
            Planning = new PlanningOptions { ConvergenceEvaluationInterval = 1 },
        };

        await using var planner = sidecar.CreatePlanner();
        await planner.RegisterCatalogAsync(engine.Catalog, TestContext.Current.CancellationToken);

        using var cancel = new CancellationTokenSource();
        var planning = Task.Run(
            async () => await planner.PlanAsync(request, cancel.Token),
            TestContext.Current.CancellationToken);

        // Cancel only once the sidecar has the planning, so the call is cancelled in flight rather
        // than before it was sent.
        while (!await observer.StopPlanningAsync(requestId, TestContext.Current.CancellationToken)
            && !planning.IsCompleted)
        {
            // Poll. The stop this issues is harmless: the cancel below is what the test is about,
            // and a stopped planning is still a planning that has to stop when its call goes away.
        }

        await cancel.CancelAsync();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await planning);
        var cancelled = Assert.IsType<PlanningCancelledException>(thrown);
        Assert.Equal(
            PlanningTerminationReason.CancelledByHost, cancelled.PlanningState.TerminationReason);

        // The diagnostic: the sidecar unwinds the cancelled planning and takes it out of flight.
        // Polled rather than timed — the deadline is a backstop that fails the test, not an
        // assertion about how long anything took.
        using var backstop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        backstop.CancelAfter(TimeSpan.FromSeconds(30));
        while (await observer.StopPlanningAsync(requestId, backstop.Token))
        {
            // Still in flight; the cancellation has not reached the next rule boundary yet.
        }

        // And the count it ended on is settled: two reads of the same number.
        var grpc = Assert.IsType<GrpcQueryPlanner>(observer);
        var first = await ReadEvaluationsAsync(grpc, requestId);
        var second = await ReadEvaluationsAsync(grpc, requestId);
        Assert.Equal(first, second);
    }

    /// <summary>Two prepares that differ only in their planning options are two cache entries.</summary>
    [Fact]
    public void The_plan_cache_key_distinguishes_planning_options()
    {
        var plain = RecordedPlanner.KeyFor(
            LongStatement, PushdownLevel.Full, SqlConformance.Default, [], "corpus");
        var defaulted = RecordedPlanner.KeyFor(
            LongStatement, PushdownLevel.Full, SqlConformance.Default, [], "corpus", null,
            PlanningOptions.None);
        var budgeted = RecordedPlanner.KeyFor(
            LongStatement, PushdownLevel.Full, SqlConformance.Default, [], "corpus", null,
            new PlanningOptions { TimeBudget = TimeSpan.FromMilliseconds(250) });
        var converging = RecordedPlanner.KeyFor(
            LongStatement, PushdownLevel.Full, SqlConformance.Default, [], "corpus", null,
            new PlanningOptions { ConvergencePatience = 3 });

        // The run-to-completion default keeps the key every recorded plan already has.
        Assert.Equal(plain, defaulted);
        Assert.NotEqual(plain, budgeted);
        Assert.NotEqual(plain, converging);
        Assert.NotEqual(budgeted, converging);

        // A stop token is an event during one search, not a property of the statement.
        using var stop = new CancellationTokenSource();
        Assert.Equal(
            plain,
            RecordedPlanner.KeyFor(
                LongStatement, PushdownLevel.Full, SqlConformance.Default, [], "corpus", null,
                new PlanningOptions { StopToken = stop.Token }));
    }

    /// <summary>
    /// A narrowing is governed exactly as a prepare is (D238): the same statement narrowed under a
    /// set of options is the plan preparing with the union under those same options gives.
    /// </summary>
    [Fact]
    public async Task A_narrowing_under_options_is_the_union_prepare_under_the_same_options()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions
            {
                ContextId = TenancyFixture.ContextId,
                Sources = [TenancyFixture.Shared.Source],
                Planner = sidecar.CreatePlanner(),
                Functions = TenancyAdoFixture.RegisterFunctions,
                // The engine-level default, so every prepare below is governed without saying so.
                Planning = new PlanningOptions
                {
                    ConvergencePatience = 3,
                    ConvergenceEvaluationInterval = 1,
                },
            },
            TestContext.Current.CancellationToken);

        var entitled = engine.WithEntitlements();
        const string sql = "SELECT id, first_name FROM members ORDER BY id";
        var full = TenancyFixture.U1;

        var baseline = await entitled.PrepareAsync(sql, full.Shape());
        var narrowed = await baseline.NarrowAsync(full);
        var union = baseline.Query.Context!.Narrow(full);
        var fromScratch = await entitled.PrepareAsync(sql, union);

        Assert.Equal(fromScratch.PlanDigest, narrowed.PlanDigest);
        Assert.Equal(
            Google.Protobuf.MessageExtensions.ToByteArray(fromScratch.Plan),
            Google.Protobuf.MessageExtensions.ToByteArray(narrowed.Plan));

        // Both were governed: the retained pipeline takes this run's governor like any other.
        Assert.True(narrowed.Query.PlanningState.EvaluationCount > 0);
        Assert.True(fromScratch.Query.PlanningState.EvaluationCount > 0);
        Assert.Equal(
            PlanningTerminationReason.Converged, narrowed.Query.PlanningState.TerminationReason);
    }

    private static async Task<ulong> ReadEvaluationsAsync(GrpcQueryPlanner planner, string requestId) =>
        await planner.PlanningEvaluationsAsync(requestId, TestContext.Current.CancellationToken);
}
