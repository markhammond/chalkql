using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The pushdown corpus (§5, D87): every query over a remote source, at every pushdown level, with
/// its results compared against the reference executor's answer on a plan where nothing was pushed.
/// </summary>
/// <remarks>
/// <para>
/// This is the milestone's correctness backbone and it is the same one M1 has, aimed at a new
/// target. The reference executor reads the <em>same</em> SQLite and DuckDB tables through their
/// scan path — no filter, no projection, no aggregate — and evaluates every predicate itself, so
/// agreement means the pushed query answered the question the plan asked. A pushdown bug that
/// changes an answer shows up as a disagreement rather than as a plausible-looking result.
/// </para>
/// <para>
/// The counters are asserted in the direction the design names: at FULL a pushed filter fetches
/// fewer rows than at NONE, and a plan with a <c>RemoteQuery</c> makes exactly as many remote calls
/// as it has of them.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class PushdownCorpusTests(SharedSidecar sidecar)
{
    /// <summary>The corpus family, for the test names.</summary>
    public const string Milestone = "m7-pushdown";

    private static RemoteFixture Fixture => RemoteFixture.Shared;

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM7())
        {
            data.Add(query.Name);
        }

        return data;
    }

    public static TheoryData<string, PushdownLevel> QueriesAtEveryLevel()
    {
        var data = new TheoryData<string, PushdownLevel>();
        foreach (var query in CorpusQueries.LoadM7())
        {
            foreach (var level in new[]
            {
                PushdownLevel.Full,
                PushdownLevel.FiltersOnly,
                PushdownLevel.ProjectionOnly,
                PushdownLevel.None,
            })
            {
                data.Add(query.Name, level);
            }
        }

        return data;
    }

    /// <summary>
    /// The heart of it: the same query at every level gives the same answer, and the reference
    /// executor — which pushes nothing anywhere — is what "the same" is measured against.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueriesAtEveryLevel))]
    public async Task Every_level_agrees_with_the_reference_executor(string name, PushdownLevel level)
    {
        Assert.SkipWhen(!Available, SkipReason);

        var query = CorpusQueries.LoadM7().Single(q => q.Name == name);
        await using var vectorised = await CreateEngineAsync(ExecutionEngine.Vectorised);
        await using var reference = await CreateEngineAsync(ExecutionEngine.Reference);

        var actualPrepared = await vectorised.PrepareAsync(query.Sql, query.PrepareOptions(level));
        var expectedPrepared = await reference.PrepareAsync(
            query.Sql, query.PrepareOptions(PushdownLevel.None));

        var (actual, _, actualPlan) =
            await DifferentialRunner.RunWithPlanAsync(vectorised, actualPrepared, query);
        var (expected, _) = await DifferentialRunner.RunAsync(reference, expectedPrepared, query);

        try
        {
            CorpusDifferentialTests.Compare(
                $"{name} at {level}",
                expected,
                actual,
                DifferentialRunner.ComparisonFor(actualPlan, Fixture.Catalog()));
        }
        finally
        {
            DifferentialRunner.Dispose(actual);
            DifferentialRunner.Dispose(expected);
        }
    }

    /// <summary>
    /// The plan shape each query's <c>-- expect:</c> block asks for. A query that says
    /// <c>has(RemoteQuery)</c> and stops producing one is the failure this catches, and it is the
    /// one that matters: the results would still be right.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task A_query_pushes_what_its_expectations_say(string name)
    {
        Assert.SkipWhen(!Available, SkipReason);

        var query = CorpusQueries.LoadM7().Single(q => q.Name == name);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);
        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions());
        PlanExpectations.Check(query, prepared.Plan);
    }

    /// <summary>
    /// A remote source is read through its <em>scan</em> path at NONE and through a query at FULL,
    /// and the counters say so: one remote call per <c>RemoteQuery</c> at FULL, none at all at NONE.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Remote_calls_are_made_at_full_and_not_at_none(string name)
    {
        Assert.SkipWhen(!Available, SkipReason);

        var query = CorpusQueries.LoadM7().Single(q => q.Name == name);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);

        var full = await engine.PrepareAsync(query.Sql, query.PrepareOptions());
        var (fullBatches, fullStats, fullPlan) =
            await DifferentialRunner.RunWithPlanAsync(engine, full, query);
        DifferentialRunner.Dispose(fullBatches);

        var none = await engine.PrepareAsync(query.Sql, query.PrepareOptions(PushdownLevel.None));
        var (noneBatches, noneStats, nonePlan) =
            await DifferentialRunner.RunWithPlanAsync(engine, none, query);
        DifferentialRunner.Dispose(noneBatches);

        // One call per RemoteQuery — except under a LookupJoin, where one query is asked once per
        // batch of keys (M5, §4), so the count becomes a floor rather than an equality.
        var queries = Count(fullPlan, Rel.KindOneofCase.RemoteQuery);
        if (Count(fullPlan, Rel.KindOneofCase.LookupJoin) == 0)
        {
            Assert.Equal(queries, fullStats.RemoteCalls);
        }
        else
        {
            Assert.True(
                fullStats.RemoteCalls >= queries,
                $"{queries} remote queries but only {fullStats.RemoteCalls} calls");
        }

        Assert.Equal(0, Count(nonePlan, Rel.KindOneofCase.RemoteQuery));
        Assert.Equal(0, noneStats.RemoteCalls);
    }

    /// <summary>
    /// The number the design asks for by name: a query whose filter is pushed fetches fewer rows
    /// than the same query with nothing pushed. Only the queries that actually push a predicate are
    /// asserted on — a query that pushes only a projection reads the same rows, fewer columns.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task A_pushed_filter_fetches_fewer_rows(string name)
    {
        Assert.SkipWhen(!Available, SkipReason);

        var query = CorpusQueries.LoadM7().Single(q => q.Name == name);
        if (!query.Expectations.Contains("not(Filter)"))
        {
            return;
        }

        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);

        var full = await engine.PrepareAsync(query.Sql, query.PrepareOptions());
        var (fullBatches, fullStats, _) = await DifferentialRunner.RunWithPlanAsync(engine, full, query);
        DifferentialRunner.Dispose(fullBatches);

        var projection = await engine.PrepareAsync(
            query.Sql, query.PrepareOptions(PushdownLevel.ProjectionOnly));
        var (projectionBatches, projectionStats, _) =
            await DifferentialRunner.RunWithPlanAsync(engine, projection, query);
        DifferentialRunner.Dispose(projectionBatches);

        Assert.True(
            fullStats.RowsFetched < projectionStats.RowsFetched,
            $"{name}: FULL fetched {fullStats.RowsFetched} rows and PROJECTION_ONLY "
            + $"{projectionStats.RowsFetched}; pushing the filter should have fetched fewer.");
    }

    /// <summary>
    /// One host-owned arena serves every pushed query at every level, and each one leaves it empty.
    /// A rental the remote operator or the source forgot to return shows up here as a non-zero
    /// balance, on whichever query and level forgot it — which is why this runs at every level
    /// rather than only at FULL: the scan path and the query path rent differently.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueriesAtEveryLevel))]
    public async Task One_host_arena_serves_every_pushed_query_and_is_empty_after_each(
        string name, PushdownLevel level)
    {
        Assert.SkipWhen(!Available, SkipReason);

        var query = CorpusQueries.LoadM7().Single(q => q.Name == name);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);
        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions(level));

        using var arena = new ExecutionArena();
        var (batches, stats) = await DifferentialRunner.RunAsync(engine, prepared, query, arena);
        try
        {
            // Touching every batch keeps the run honest: the arena's memory has to still be readable.
            Assert.Equal(stats.RowsProduced, batches.Sum(b => (long)b.Length));
        }
        finally
        {
            DifferentialRunner.Dispose(batches);
        }

        Assert.Equal(0, arena.OutstandingBytes);
    }

    private static int Count(Plan plan, Rel.KindOneofCase kind) =>
        PlanWalker.Rels(plan.Root).Count(r => r.KindCase == kind);

    private static bool Available => RemoteFixture.SkipReason is null;

    private static string SkipReason => RemoteFixture.SkipReason ?? string.Empty;

    private async Task<ChalkEngine> CreateEngineAsync(ExecutionEngine engine)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        return await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = engine, BatchSize = 4096 },
        });
    }
}
