using Apache.Arrow;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// The <c>edge</c> family (D165, <c>docs/design/25-coverage-graft.md</c> §2 and §6): the coverage
/// graft's adversarial queries, run at every pushdown level and at three batch sizes against the
/// reference executor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance.</b> The queries are grafted from <c>ikvmnet/calcite-dotnet</c> (Apache-2.0); see
/// <c>corpus/queries/edge/README.md</c> and the repository's <c>NOTICE</c>. No expected value is
/// taken (D163) — every answer here comes from Chalk's own reference executor, and
/// <see cref="EdgeDuckDbOracleTests"/> asks DuckDB the same questions.
/// </para>
/// <para>
/// A query whose <c>-- compare: top-k-under-ties</c> header says so is compared under D166 instead
/// of the default rule: its <c>LIMIT</c> boundary falls inside a tie, so the answer is determined
/// everywhere except at the boundary keys. That comparison wants the reference's <em>untruncated</em>
/// ordered rows, which <see cref="LimitLiftingPlanner"/> produces by taking the plan's own
/// <c>Fetch</c> or <c>TopN</c> bound off and running what is left.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class EdgeDifferentialTests(SharedSidecar sidecar)
{
    /// <summary>The family's directory name, which is also its plans directory.</summary>
    public const string Milestone = "edge";

    private static readonly CorpusFixture Fixture = CorpusFixture.Shared;

    /// <summary>
    /// A second fixture at a hundredth of the size, for the batch-size runs. <c>sales</c> and
    /// <c>sorted</c> are literal and identical in both; the two <c>UNNEST</c> queries read
    /// <c>symbols</c>, which is five rows either way.
    /// </summary>
    private static readonly CorpusFixture Small =
        CorpusFixture.Create(barMinutes: 61, lineItemRows: 293);

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadEdge())
        {
            data.Add(query.Name);
        }

        return data;
    }

    public static TheoryData<string, PushdownLevel> QueriesAtEveryLevel()
    {
        var data = new TheoryData<string, PushdownLevel>();
        foreach (var query in CorpusQueries.LoadEdge())
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

    public static TheoryData<string, int> QueriesAtSmallBatchSizes()
    {
        var data = new TheoryData<string, int>();
        foreach (var query in CorpusQueries.LoadEdge())
        {
            data.Add(query.Name, 1);
            data.Add(query.Name, 7);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(QueriesAtEveryLevel))]
    public async Task Every_level_agrees_with_the_reference_executor(string name, PushdownLevel level)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await CompareAsync(name, level, batchSize: 4096, Fixture);
    }

    [Theory]
    [MemberData(nameof(QueriesAtSmallBatchSizes))]
    public async Task Both_engines_agree_at_small_batch_sizes(string name, int batchSize)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await CompareAsync(name, PushdownLevel.Full, batchSize, Small);
    }

    /// <summary>
    /// The arena contract over the family (08-execution-arena.md §3): one host-owned arena serves
    /// every query and each one leaves it empty.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task One_host_arena_serves_every_query_and_is_empty_after_each(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadEdge().Single(q => q.Name == name);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised, 4096, Fixture);
        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions());

        var (batches, stats) = await DifferentialRunner.RunAsync(engine, prepared, query, HostArena);
        try
        {
            Assert.Equal(stats.RowsProduced, batches.Sum(b => (long)b.Length));
        }
        finally
        {
            DifferentialRunner.Dispose(batches);
        }

        Assert.Equal(0, HostArena.OutstandingBytes);
    }

    private static readonly ExecutionArena HostArena =
        new(new ArenaOptions { RetainBytes = 8 << 20 });

    private async Task CompareAsync(
        string name, PushdownLevel level, int batchSize, CorpusFixture fixture)
    {
        var query = CorpusQueries.LoadEdge().Single(q => q.Name == name);

        await using var vectorised = await CreateEngineAsync(
            ExecutionEngine.Vectorised, batchSize, fixture);
        await using var reference = await CreateEngineAsync(
            ExecutionEngine.Reference, batchSize, fixture);

        var actualPrepared = await vectorised.PrepareAsync(query.Sql, query.PrepareOptions(level));
        var expectedPrepared = await reference.PrepareAsync(
            query.Sql, query.PrepareOptions(PushdownLevel.None));

        var (actual, _, actualPlan) =
            await DifferentialRunner.RunWithPlanAsync(vectorised, actualPrepared, query);
        var (expected, _) = await DifferentialRunner.RunAsync(reference, expectedPrepared, query);

        try
        {
            var options = ComparisonFor(query, actualPlan, fixture.Catalog);
            if (options.Mode == ResultComparisonMode.TopKUnderTies)
            {
                DifferentialRunner.Dispose(expected);
                expected = await UnboundedReferenceAsync(query, batchSize, fixture);
            }

            CorpusDifferentialTests.Compare(
                $"{query.Name}: vectorised@{level} vs reference@NONE at batch size {batchSize}",
                expected,
                actual,
                options);
        }
        finally
        {
            DifferentialRunner.Dispose(actual);
            DifferentialRunner.Dispose(expected);
        }
    }

    /// <summary>
    /// The comparison rules, plus the header's mode (D166). The boundary is read off the plan that
    /// actually ran, so a level that plans <c>TopN</c> and one that plans <c>Fetch</c> over
    /// <c>Sort</c> are both understood.
    /// </summary>
    internal static ResultComparisonOptions ComparisonFor(
        CorpusQuery query, Plan plan, CatalogContext catalog)
    {
        var options = DifferentialRunner.ComparisonFor(plan, catalog);
        if (query.Comparison != ResultComparisonMode.TopKUnderTies)
        {
            return options;
        }

        var boundary = BoundaryOf(plan.Root)
            ?? throw new InvalidOperationException(
                $"{query.Name} asks for a top-k-under-ties comparison, but its plan carries no "
                + $"fetch (its root is a {plan.Root.KindCase}).");

        return new ResultComparisonOptions
        {
            Mode = ResultComparisonMode.TopKUnderTies,
            Boundary = boundary,
            OrderKeys = options.OrderKeys,
            AggregatedFloatColumns = options.AggregatedFloatColumns,
            UlpTolerance = options.UlpTolerance,
        };
    }

    /// <summary>
    /// The plan's own fetch and offset. The bound is not always the root: at <c>NONE</c> a
    /// projection can sit above it, and at <c>FULL</c> the same query plans a bare <c>TopN</c>. The
    /// topmost one is the query's, because these queries have exactly one.
    /// </summary>
    private static TopKBoundary? BoundaryOf(Rel rel)
    {
        switch (rel.KindCase)
        {
            case Rel.KindOneofCase.Fetch:
                return new TopKBoundary
                {
                    Offset = rel.Fetch.Offset,
                    Count = rel.Fetch.HasCount ? rel.Fetch.Count : null,
                };
            case Rel.KindOneofCase.TopN:
                return new TopKBoundary { Offset = rel.TopN.Offset, Count = rel.TopN.Count };
            default:
                return SoleInput(rel) is { } input ? BoundaryOf(input) : null;
        }
    }

    /// <summary>
    /// The one input of a single-input node, or null. Only the kinds a bound can hide under in this
    /// family: anything else means the plan is not the shape the header claims, and saying so is
    /// better than searching a tree for a node that should be near its top.
    /// </summary>
    internal static Rel? SoleInput(Rel rel) => rel.KindCase switch
    {
        Rel.KindOneofCase.Project => rel.Project.Input,
        Rel.KindOneofCase.Filter => rel.Filter.Input,
        Rel.KindOneofCase.Sort => rel.Sort.Input,
        _ => null,
    };

    /// <summary>
    /// The reference executor's rows for the same query with the plan's bound lifted — the fully
    /// ordered result a top-k-under-ties comparison is judged against.
    /// </summary>
    private async Task<IReadOnlyList<RecordBatch>> UnboundedReferenceAsync(
        CorpusQuery query, int batchSize, CorpusFixture fixture)
    {
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = fixture.Sources,
            Planner = new LimitLiftingPlanner(sidecar.CreatePlanner()),
            Execution = new ExecutionOptions
            {
                Engine = ExecutionEngine.Reference,
                BatchSize = batchSize,
            },
        });

        var prepared = await engine.PrepareAsync(
            query.Sql, query.PrepareOptions(PushdownLevel.None));
        var (batches, _) = await DifferentialRunner.RunAsync(engine, prepared, query);
        return batches;
    }

    private async Task<ChalkEngine> CreateEngineAsync(
        ExecutionEngine engine, int batchSize, CorpusFixture fixture) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = fixture.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = engine, BatchSize = batchSize },
        });
}

/// <summary>
/// A planner that takes the bound off the plan it is given: a root <c>Fetch</c> is dropped and a
/// root <c>TopN</c> becomes the <c>Sort</c> it would have been without one (D166).
/// </summary>
/// <remarks>
/// It exists so a top-k-under-ties comparison can see the tied group a truncated result no longer
/// shows, and it is deliberately a <em>planner</em> rather than SQL surgery: the bound it lifts is
/// the one the plan actually carries, so the two runs cannot end up asking different questions.
/// </remarks>
internal sealed class LimitLiftingPlanner(IQueryPlanner inner) : IQueryPlanner
{
    public ValueTask<PlannerInfo> GetInfoAsync(CancellationToken ct = default) =>
        inner.GetInfoAsync(ct);

    public bool AcceptsCatalogDeltas => inner.AcceptsCatalogDeltas;

    public ValueTask RegisterCatalogAsync(
        CatalogRegistration registration, CancellationToken ct = default) =>
        inner.RegisterCatalogAsync(registration, ct);

    public ValueTask RegisterStatisticsAsync(
        StatisticsRegistration statistics, CancellationToken ct = default) =>
        inner.RegisterStatisticsAsync(statistics, ct);

    public async ValueTask<PlanResult> PlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        var result = await inner.PlanAsync(request, ct).ConfigureAwait(false);
        var lifted = Lift(result.Plan.Root);
        if (lifted is null)
        {
            return result;
        }

        var plan = result.Plan.Clone();
        plan.Root = lifted;

        // The digest is over the canonical encoding, so a rewritten plan needs a rewritten digest;
        // the client validates the two against each other before it will run anything (I-IR-9).
        plan.PlanDigest = PlanDigest.Compute(plan);
        return new PlanResult { Plan = plan, PlanText = result.PlanText, Stats = result.Stats };
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    /// <summary>
    /// This node with its bound lifted, or null when there is none below it. A projection may sit
    /// above the bound at the lower pushdown levels, so the rewrite recurses through the
    /// single-input nodes rather than looking only at the root.
    /// </summary>
    private static Rel? Lift(Rel rel)
    {
        switch (rel.KindCase)
        {
            case Rel.KindOneofCase.Fetch:
                return rel.Fetch.Input;

            case Rel.KindOneofCase.TopN:
            {
                var sort = new Rel
                {
                    RowType = rel.RowType,
                    EstRowCount = rel.EstRowCount,
                    Sort = new Sort { Input = rel.TopN.Input },
                };
                sort.Sort.Fields.AddRange(rel.TopN.Fields);
                sort.Collations.AddRange(rel.Collations);
                return sort;
            }

            default:
            {
                var input = EdgeDifferentialTests.SoleInput(rel);
                if (input is null || Lift(input) is not { } replacement)
                {
                    return null;
                }

                var rewritten = rel.Clone();
                switch (rewritten.KindCase)
                {
                    case Rel.KindOneofCase.Project: rewritten.Project.Input = replacement; break;
                    case Rel.KindOneofCase.Filter: rewritten.Filter.Input = replacement; break;
                    case Rel.KindOneofCase.Sort: rewritten.Sort.Input = replacement; break;
                    default: return null;
                }

                return rewritten;
            }
        }
    }
}
