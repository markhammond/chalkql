using System.Text;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;
using CrossSourceJoinPolicy = Chalk.Catalog.CrossSourceJoinPolicy;
using JoinStrategy = Chalk.Catalog.JoinStrategy;
using SourcePairRule = Chalk.Catalog.SourcePairRule;

namespace Chalk.Integration.Tests;

/// <summary>
/// The federation corpus (§6, D103–D110): every query across two or more sources, compared against
/// the reference executor that pulls everything local and joins with no pushdown at all.
/// </summary>
/// <remarks>
/// <para>
/// This is rev 3's M5 oracle, and it is the same one every earlier milestone has. The reference
/// executor reads the <em>same</em> SQLite, DuckDB and POCO tables through their scan paths and does
/// the join itself, so agreement means the strategy the planner chose answered the question the
/// query asked — whichever strategy that was. A lookup that binds the wrong keys, an adaptive join
/// that materialises the wrong side, a partitioned scan that loses a partition: all of them show up
/// here as a disagreement rather than as a plausible-looking result.
/// </para>
/// <para>
/// The counters are asserted in the direction §6's table names them, and never a timing.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class FederationCorpusTests(SharedSidecar sidecar)
{
    /// <summary>The corpus family, for the test names and the recorded plans.</summary>
    public const string Milestone = "m9-federation";

    private static RemoteFixture Fixture => RemoteFixture.Shared;

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM9())
        {
            data.Add(query.Name);
        }

        return data;
    }

    public static TheoryData<string, PushdownLevel> QueriesAtEveryLevel()
    {
        var data = new TheoryData<string, PushdownLevel>();
        foreach (var query in CorpusQueries.LoadM9())
        {
            foreach (var level in new[] { PushdownLevel.Full, PushdownLevel.FiltersOnly, PushdownLevel.None })
            {
                data.Add(query.Name, level);
            }
        }

        return data;
    }

    /// <summary>
    /// Every strategy, forced in turn, over every query (§6 corpus 13). Identical results; only the
    /// counters and the plan shape move. This is the property that makes the four strategies four
    /// spellings of one answer rather than four answers.
    /// </summary>
    public static TheoryData<string, JoinStrategy> QueriesWithEachStrategy()
    {
        var data = new TheoryData<string, JoinStrategy>();
        foreach (var query in CorpusQueries.LoadM9())
        {
            // A query with a policy of its own is *about* that policy; forcing another over it would
            // be testing something the file did not say.
            if (query.JoinPolicy is not null)
            {
                continue;
            }

            foreach (var strategy in new[]
            {
                JoinStrategy.Local,
                JoinStrategy.Lookup,
                JoinStrategy.Broadcast,
                JoinStrategy.Adaptive,
            })
            {
                data.Add(query.Name, strategy);
            }
        }

        return data;
    }

    /// <summary>
    /// The heart of it: whatever the planner chose, the answer is the reference executor's answer.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueriesAtEveryLevel))]
    public async Task Every_level_agrees_with_the_reference_executor(string name, PushdownLevel level)
    {
        Assert.SkipWhen(!Available, SkipReason);

        var query = CorpusQueries.LoadM9().Single(q => q.Name == name);
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
                DifferentialRunner.ComparisonFor(actualPlan, Fixture.PartitionedCatalog()));
        }
        finally
        {
            DifferentialRunner.Dispose(actual);
            DifferentialRunner.Dispose(expected);
        }
    }

    /// <summary>
    /// §6 corpus 13: every strategy forced in turn gives the same rows. What moves is the plan and
    /// the counters — which is the whole claim.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueriesWithEachStrategy))]
    public async Task Every_strategy_forced_gives_the_same_answer(string name, JoinStrategy strategy)
    {
        Assert.SkipWhen(!Available, SkipReason);

        var query = CorpusQueries.LoadM9().Single(q => q.Name == name);
        await using var vectorised = await CreateEngineAsync(ExecutionEngine.Vectorised);
        await using var reference = await CreateEngineAsync(ExecutionEngine.Reference);

        var forced = new CrossSourceJoinPolicy
        {
            Pairs = [new SourcePairRule { Preferred = strategy }],
        };

        var actualPrepared = await vectorised.PrepareAsync(query.Sql, query.PrepareOptions(forced));
        var expectedPrepared = await reference.PrepareAsync(
            query.Sql, query.PrepareOptions(PushdownLevel.None));

        var (actual, _, actualPlan) =
            await DifferentialRunner.RunWithPlanAsync(vectorised, actualPrepared, query);
        var (expected, _) = await DifferentialRunner.RunAsync(reference, expectedPrepared, query);

        try
        {
            CorpusDifferentialTests.Compare(
                $"{name} forced to {strategy}",
                expected,
                actual,
                DifferentialRunner.ComparisonFor(actualPlan, Fixture.PartitionedCatalog()));
        }
        finally
        {
            DifferentialRunner.Dispose(actual);
            DifferentialRunner.Dispose(expected);
        }
    }

    /// <summary>
    /// The SQL each source is actually sent, recorded as golden text — the review signal for a
    /// converter or strategy change (§2's practice, applied to the federation family). One file per
    /// query, every <c>RemoteQuery</c> in plan order; the PostgreSQL half of the same corpus records
    /// its own beside this one (D133 §0b).
    /// </summary>
    /// <remarks>Regenerate with <c>CHALK_WRITE_FIXTURES=1</c> and review the diff.</remarks>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task The_generated_sql_matches_the_golden(string name)
    {
        Assert.SkipWhen(!Available, SkipReason);

        var query = CorpusQueries.LoadM9().Single(q => q.Name == name);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);
        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions());

        var text = new StringBuilder();
        foreach (var rel in PlanWalker.Rels(prepared.Plan.Root))
        {
            if (rel.KindCase != Rel.KindOneofCase.RemoteQuery)
            {
                continue;
            }

            var remote = rel.RemoteQuery;
            text.Append("source=").Append(remote.SourceId)
                .Append(" dialect=").Append(remote.Dialect)
                .Append(" parameters=").Append(remote.Parameters.Count)
                .Append('\n');
            text.Append(remote.QueryText).Append('\n');
            Assert.True(
                remote.PushedPlan is not null,
                $"{name}: the RemoteQuery for '{remote.SourceId}' carries no pushed plan");
        }

        if (text.Length == 0)
        {
            text.Append("(nothing pushed)\n");
        }

        var goldens = new DirectoryInfo(Path.Combine(RepoLayout.Corpus.FullName, "plans", Milestone));
        var file = new FileInfo(Path.Combine(goldens.FullName, name + ".sql"));
        if (Environment.GetEnvironmentVariable("CHALK_WRITE_FIXTURES") == "1")
        {
            goldens.Create();
            await File.WriteAllTextAsync(file.FullName, text.ToString());
            return;
        }

        Assert.True(
            file.Exists,
            $"{file.FullName} does not exist. Regenerate with CHALK_WRITE_FIXTURES=1 and review it.");
        Assert.Equal(
            (await File.ReadAllTextAsync(file.FullName)).ReplaceLineEndings("\n"),
            text.ToString().ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// <b>Exit criterion:</b> a host replaces the shipped policy without forking (§8, D104). The
    /// interface half — the descriptor half is corpus 08, which overrides one statement. This one
    /// hands the engine an <see cref="ICrossSourceJoinPolicy"/> at creation, and the strategy every
    /// query in the family may use changes as a result.
    /// </summary>
    [Fact]
    public async Task A_host_policy_object_replaces_the_shipped_one()
    {
        Assert.SkipWhen(!Available, SkipReason);

        var query = CorpusQueries.LoadM9().Single(q => q.Name == "01_lookup_local_dimension");
        var policy = new NoLookupsIntoDuck();

        await using var withPolicy = await CreateEngineAsync(ExecutionEngine.Vectorised, policy);
        await using var shipped = await CreateEngineAsync(ExecutionEngine.Vectorised);

        // Called once at engine creation, and given the catalog it is about to describe.
        Assert.Equal(1, policy.Calls);
        Assert.Contains(policy.Saw, s => s.Name == "duck");

        var forbidden = await withPolicy.PrepareAsync(query.Sql, query.PrepareOptions());
        var allowed = await shipped.PrepareAsync(query.Sql, query.PrepareOptions());

        Assert.DoesNotContain(
            PlanWalker.Rels(forbidden.Plan.Root), r => r.KindCase == Rel.KindOneofCase.LookupJoin);
        Assert.DoesNotContain(
            PlanWalker.Rels(forbidden.Plan.Root), r => r.KindCase == Rel.KindOneofCase.AdaptiveJoin);
        Assert.Contains(
            PlanWalker.Rels(allowed.Plan.Root), r => r.KindCase == Rel.KindOneofCase.LookupJoin);
        Assert.NotEqual(allowed.Plan.PlanDigest, forbidden.Plan.PlanDigest);

        // And it is still the same question, answered the same way.
        await using var reference = await CreateEngineAsync(ExecutionEngine.Reference);
        var expectedPrepared = await reference.PrepareAsync(
            query.Sql, query.PrepareOptions(PushdownLevel.None));
        var (actual, _, actualPlan) =
            await DifferentialRunner.RunWithPlanAsync(withPolicy, forbidden, query);
        var (expected, _) = await DifferentialRunner.RunAsync(reference, expectedPrepared, query);
        try
        {
            CorpusDifferentialTests.Compare(
                "01 under a host policy",
                expected,
                actual,
                DifferentialRunner.ComparisonFor(actualPlan, Fixture.PartitionedCatalog()));
        }
        finally
        {
            DifferentialRunner.Dispose(actual);
            DifferentialRunner.Dispose(expected);
        }
    }

    /// <summary>A policy that returns nothing is the host's bug, and says so by name.</summary>
    [Fact]
    public async Task A_policy_that_returns_null_is_refused_by_name()
    {
        Assert.SkipWhen(!Available, SkipReason);
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateEngineAsync(ExecutionEngine.Vectorised, new NoPolicyAtAll()));

        Assert.Contains("NoPolicyAtAll", failure.Message, StringComparison.Ordinal);
        Assert.Contains("must return a descriptor", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host's policy: no lookups from the POCO source into DuckDB, whatever cost would prefer.
    /// Records what it was asked, because "given the catalog it is about to describe" is part of the
    /// contract.
    /// </summary>
    private sealed class NoLookupsIntoDuck : ICrossSourceJoinPolicy
    {
        public int Calls { get; private set; }

        public IReadOnlyList<SchemaDescriptor> Saw { get; private set; } = [];

        public CrossSourceJoinPolicy Build(CatalogContext catalog)
        {
            Calls++;
            Saw = catalog.Schemas;
            return new CrossSourceJoinPolicy
            {
                Pairs =
                [
                    new SourcePairRule
                    {
                        LeftSource = "mem",
                        RightSource = "duck",
                        Allowed = [JoinStrategy.Local],
                    },
                ],
            };
        }
    }

    private sealed class NoPolicyAtAll : ICrossSourceJoinPolicy
    {
        public CrossSourceJoinPolicy Build(CatalogContext catalog) => null!;
    }

    /// <summary>The plan shape each query's <c>-- expect:</c> block asks for.</summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task A_query_plans_the_strategy_its_expectations_say(string name)
    {
        Assert.SkipWhen(!Available, SkipReason);

        var query = CorpusQueries.LoadM9().Single(q => q.Name == name);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);
        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions());
        PlanExpectations.Check(query, prepared.Plan);
    }

    /// <summary>
    /// <b>Exit criterion:</b> no <c>RemoteQuery</c> sits under a <c>NestedLoopJoin</c> on the inner
    /// side in any corpus plan (§8). Asserted here rather than left to the validator, so the claim
    /// is visible as a test even though a plan that broke it could not be compiled at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueriesAtEveryLevel))]
    public async Task No_remote_query_sits_under_a_nested_loop(string name, PushdownLevel level)
    {
        Assert.SkipWhen(!Available, SkipReason);

        var query = CorpusQueries.LoadM9().Single(q => q.Name == name);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);
        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions(level));

        foreach (var rel in PlanWalker.Rels(prepared.Plan.Root))
        {
            if (rel.KindCase == Rel.KindOneofCase.NestedLoopJoin)
            {
                Assert.False(
                    Fetches(rel.NestedLoopJoin.Right),
                    $"{name} at {level}: a RemoteQuery is on a nested loop's inner side");
            }
        }
    }

    /// <summary>
    /// <b>Exit criterion:</b> arena discipline holds across every strategy — a fan-out, a lookup's
    /// per-call hash table and an adaptive join's materialisation all give their memory back.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task The_arena_is_empty_after_every_federation_query(string name)
    {
        Assert.SkipWhen(!Available, SkipReason);

        var query = CorpusQueries.LoadM9().Single(q => q.Name == name);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);
        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions());

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

    private static bool Fetches(Rel? rel)
    {
        if (rel is null)
        {
            return false;
        }

        if (rel.KindCase == Rel.KindOneofCase.RemoteQuery)
        {
            return true;
        }

        return PlanWalker.Inputs(rel).Any(Fetches);
    }

    private static bool Available => RemoteFixture.SkipReason is null;

    private static string SkipReason => RemoteFixture.SkipReason ?? string.Empty;

    internal async Task<ChalkEngine> CreateEngineAsync(
        ExecutionEngine engine, ICrossSourceJoinPolicy? policy = null) =>
        await CreateEngineAsync(sidecar, engine, execution: null, policy);

    /// <summary>
    /// An engine over all four data sources plus the source that declares the partitioned table.
    /// Shared with the operator and failure tests, which want the same catalog.
    /// </summary>
    internal static async Task<ChalkEngine> CreateEngineAsync(
        SharedSidecar sidecar,
        ExecutionEngine engine,
        ExecutionOptions? execution = null,
        ICrossSourceJoinPolicy? policy = null)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        return await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.FederatedSources,
            Planner = sidecar.CreatePlanner(),
            JoinPolicy = policy,
            Execution = execution ?? new ExecutionOptions { Engine = engine, BatchSize = 4096 },
        });
    }
}
