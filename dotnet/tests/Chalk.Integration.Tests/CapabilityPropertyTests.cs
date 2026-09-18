using System.Collections.Concurrent;
using Chalk.Client;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The property the capability model rests on (D87, §6): a capability is an <em>optimisation</em>.
/// Turning one off may change the plan and must not change the answer.
/// </summary>
/// <remarks>
/// <para>
/// Every corpus query is planned again with each capability disabled on its own, and then with them
/// disabled progressively — one, two, three, up to all nine — and every one of those answers is
/// compared against the reference executor, which pushes nothing anywhere. That is a stronger claim
/// than the level sweep makes: a level is a coarse dial the planner reads once, while a disabled
/// capability changes what the gate says yes to, one predicate and one operator at a time, and it
/// is exactly where an off-by-one in a rule's guard would hide.
/// </para>
/// <para>
/// Two structural properties come with it. With every capability off nothing is pushed at all, which
/// is the same execution the I4 reference gets — so the switch really does reach every rule, rather
/// than most of them. And with one capability off, no pushed subtree anywhere contains the operator
/// that capability names: this is the "nothing undeclared is pushed" rule of §1 checked through the
/// one handle that can turn a declaration off without editing a catalog.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class CapabilityPropertyTests(SharedSidecar sidecar)
{
    /// <summary>
    /// Every capability, in the order the progressive sweep switches them off: the ones that shrink
    /// a result first, so each step is a plan that can only do more work than the step before it.
    /// </summary>
    private static readonly DisabledCapability[] All =
    [
        DisabledCapability.Filter,
        DisabledCapability.Aggregate,
        DisabledCapability.Limit,
        DisabledCapability.Distinct,
        DisabledCapability.InList,
        DisabledCapability.Sort,
        DisabledCapability.Join,
        DisabledCapability.Project,
        DisabledCapability.Parameters,
    ];

    private static RemoteFixture Fixture => RemoteFixture.Shared;

    public static TheoryData<string, DisabledCapability> QueriesWithOneCapabilityOff()
    {
        var data = new TheoryData<string, DisabledCapability>();
        foreach (var query in CorpusQueries.LoadM7())
        {
            foreach (var capability in All)
            {
                data.Add(query.Name, capability);
            }
        }

        return data;
    }

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM7())
        {
            data.Add(query.Name);
        }

        return data;
    }

    /// <summary>One capability off: the same answer, and that operator pushed nowhere.</summary>
    [Theory]
    [MemberData(nameof(QueriesWithOneCapabilityOff))]
    public async Task One_capability_off_changes_the_plan_and_not_the_answer(
        string name, DisabledCapability capability)
    {
        Assert.SkipWhen(!Available, SkipReason);

        var query = CorpusQueries.LoadM7().Single(q => q.Name == name);
        await using var vectorised = await CreateEngineAsync(ExecutionEngine.Vectorised);
        await using var reference = await CreateEngineAsync(ExecutionEngine.Reference);

        var prepared = await vectorised.PrepareAsync(query.Sql, query.PrepareOptions([capability]));
        var expectedPrepared = await reference.PrepareAsync(
            query.Sql, query.PrepareOptions(PushdownLevel.None));

        var (actual, _, plan) = await DifferentialRunner.RunWithPlanAsync(vectorised, prepared, query);
        var (expected, _) = await DifferentialRunner.RunAsync(reference, expectedPrepared, query);

        try
        {
            CorpusDifferentialTests.Compare(
                $"{name} with {capability} disabled",
                expected,
                actual,
                DifferentialRunner.ComparisonFor(plan, Fixture.Catalog()));
        }
        finally
        {
            DifferentialRunner.Dispose(actual);
            DifferentialRunner.Dispose(expected);
        }

        // The structural half: whatever the descriptor claims, the disabled operator did not travel.
        foreach (var kind in PushedKindsFor(capability))
        {
            Assert.False(
                PushedSubtreesOf(plan).Any(rel => Count(rel, kind) > 0),
                $"{name}: {capability} is disabled, but a pushed subtree still contains a {kind}:\n"
                + PlanPrinter.Print(plan));
        }
    }

    /// <summary>
    /// The progressive sweep: nine cumulative subsets, every one of them giving the reference
    /// answer, and the last of them — everything off — pushing nothing at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Capabilities_disabled_progressively_never_change_the_answer(string name)
    {
        Assert.SkipWhen(!Available, SkipReason);

        var query = CorpusQueries.LoadM7().Single(q => q.Name == name);
        await using var vectorised = await CreateEngineAsync(ExecutionEngine.Vectorised);
        await using var reference = await CreateEngineAsync(ExecutionEngine.Reference);

        var expectedPrepared = await reference.PrepareAsync(
            query.Sql, query.PrepareOptions(PushdownLevel.None));
        var (expected, _) = await DifferentialRunner.RunAsync(reference, expectedPrepared, query);

        try
        {
            for (var take = 1; take <= All.Length; take++)
            {
                var disabled = All[..take];
                var prepared = await vectorised.PrepareAsync(query.Sql, query.PrepareOptions(disabled));
                var (actual, _, plan) =
                    await DifferentialRunner.RunWithPlanAsync(vectorised, prepared, query);

                try
                {
                    CorpusDifferentialTests.Compare(
                        $"{name} with {take} of {All.Length} capabilities disabled",
                        expected,
                        actual,
                        DifferentialRunner.ComparisonFor(plan, Fixture.Catalog()));
                }
                finally
                {
                    DifferentialRunner.Dispose(actual);
                }

                if (take == All.Length)
                {
                    // With every capability off, a pushed subtree holds nothing but the read. Note
                    // that this is not the same as the NONE level: NONE says a remote source is
                    // read through its scan path and gets no query at all, while the capability
                    // switches say what a query may contain. A source that only takes queries has
                    // no other way in, so the boundary stays and the query becomes a bare table
                    // read — which is the honest spelling of "nothing was pushed".
                    var pushed = PushedSubtreesOf(plan).ToList();
                    foreach (var rel in pushed)
                    {
                        foreach (var kind in Pushable)
                        {
                            Assert.False(
                                Count(rel, kind) > 0,
                                $"{name}: every capability is disabled, but a pushed subtree still "
                                + $"contains a {kind}:\n{PlanPrinter.Print(plan)}");
                        }
                    }
                }
            }
        }
        finally
        {
            DifferentialRunner.Dispose(expected);
        }
    }

    /// <summary>
    /// Turning a capability off cannot make the source do more work. Asserted only where the plan
    /// kept its remote shape: a disabled <c>Join</c> may split one pushed query into two, and two
    /// queries over unjoined tables legitimately fetch fewer rows than one over their product.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task A_disabled_capability_never_fetches_fewer_rows(string name)
    {
        Assert.SkipWhen(!Available, SkipReason);

        var query = CorpusQueries.LoadM7().Single(q => q.Name == name);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);

        var all = await engine.PrepareAsync(query.Sql, query.PrepareOptions());
        var (allBatches, allStats, allPlan) =
            await DifferentialRunner.RunWithPlanAsync(engine, all, query);
        DifferentialRunner.Dispose(allBatches);
        var remotes = Count(allPlan.Root, Rel.KindOneofCase.RemoteQuery);

        foreach (var capability in All)
        {
            var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions([capability]));
            var (batches, stats, plan) =
                await DifferentialRunner.RunWithPlanAsync(engine, prepared, query);
            DifferentialRunner.Dispose(batches);

            if (Count(plan.Root, Rel.KindOneofCase.RemoteQuery) != remotes)
            {
                continue;
            }

            Assert.True(
                stats.RowsFetched >= allStats.RowsFetched,
                $"{name}: with {capability} disabled the plan kept its {remotes} remote quer"
                + $"{(remotes == 1 ? "y" : "ies")} but fetched {stats.RowsFetched} rows, fewer than "
                + $"the {allStats.RowsFetched} it fetches with every capability on.");
        }
    }

    /// <summary>
    /// The operators a disabled capability forbids inside a pushed subtree.
    /// </summary>
    /// <remarks>
    /// Four of the nine have no entry, for two different reasons. <c>Distinct</c> and
    /// <c>InList</c> have no node of their own in the IR — a DISTINCT is an <c>Aggregate</c> with
    /// no measures and an IN list is a <c>Filter</c> holding one expression — so a structural check
    /// here would forbid the wrong thing; both are covered by the answer comparison and by the
    /// planner's own rule tests. <c>Project</c> and <c>Parameters</c> are legitimately present in a
    /// pushed subtree even when disabled: a <c>Project</c> that renames or computes is not the
    /// column pruning the capability names, and a plan with no pushed parameters can still carry a
    /// literal the planner folded.
    /// </remarks>
    private static IReadOnlyList<Rel.KindOneofCase> PushedKindsFor(DisabledCapability capability) =>
        capability switch
        {
            DisabledCapability.Filter => [Rel.KindOneofCase.Filter],
            DisabledCapability.Aggregate => [Rel.KindOneofCase.Aggregate],
            DisabledCapability.Sort => [Rel.KindOneofCase.Sort, Rel.KindOneofCase.TopN],
            DisabledCapability.Limit => [Rel.KindOneofCase.Fetch, Rel.KindOneofCase.TopN],
            DisabledCapability.Join => [Rel.KindOneofCase.Join],
            _ => [],
        };

    /// <summary>Every operator a capability can put into a pushed subtree.</summary>
    private static readonly Rel.KindOneofCase[] Pushable =
    [
        Rel.KindOneofCase.Filter,
        Rel.KindOneofCase.Aggregate,
        Rel.KindOneofCase.Sort,
        Rel.KindOneofCase.Fetch,
        Rel.KindOneofCase.TopN,
        Rel.KindOneofCase.Join,
    ];

    /// <summary>Every <c>RemoteQuery.pushed_plan</c> in the plan — what actually travelled (D84).</summary>
    private static IEnumerable<Rel> PushedSubtreesOf(Plan plan) =>
        PlanWalker.Rels(plan.Root)
            .Where(r => r.KindCase == Rel.KindOneofCase.RemoteQuery)
            .Select(r => r.RemoteQuery.PushedPlan)
            .Where(p => p is not null && p.KindCase != Rel.KindOneofCase.None);

    private static int Count(Rel root, Rel.KindOneofCase kind) =>
        PlanWalker.Rels(root).Count(r => r.KindCase == kind);

    private static bool Available => RemoteFixture.SkipReason is null;

    private static string SkipReason => RemoteFixture.SkipReason ?? string.Empty;

    // private async Task<ChalkEngine> CreateEngineAsync(ExecutionEngine engine)
    // {
    //     Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
    //     return await ChalkEngine.CreateAsync(new ChalkEngineOptions
    //     {
    //         ContextId = CorpusFixture.ContextId,
    //         Functions = CorpusFunctions.Register,
    //         Sources = Fixture.Sources,
    //         Planner = sidecar.CreatePlanner(),
    //         Execution = new ExecutionOptions { Engine = engine, BatchSize = 4096 },
    //     });
    // }
    
    private readonly ConcurrentDictionary<
        ExecutionEngine,
        Lazy<Task<ChalkEngine>>> _engines = new();

    private Task<ChalkEngine> CreateEngineAsync(ExecutionEngine engine) =>
        _engines.GetOrAdd(
            engine,
            static (engine, self) => new(
                () => self.CreateEngineCoreAsync(engine),
                LazyThreadSafetyMode.ExecutionAndPublication),
            this).Value;

    private async Task<ChalkEngine> CreateEngineCoreAsync(ExecutionEngine engine)
    {
        Assert.SkipWhen(
            !sidecar.Sidecar.IsAvailable,
            sidecar.SkipReason ?? string.Empty);

        return await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions
            {
                Engine = engine,
                BatchSize = 4096
            },
        });
    }
}
