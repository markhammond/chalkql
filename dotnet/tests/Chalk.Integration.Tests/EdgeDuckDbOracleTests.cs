using Apache.Arrow;
using Chalk.Client;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The third oracle over the <c>edge</c> family (D163, D165, <c>25-coverage-graft.md</c> §0 and §6):
/// DuckDB is asked the same adversarial questions and its answers are compared with both Chalk
/// executors.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance.</b> The queries are grafted from <c>ikvmnet/calcite-dotnet</c> (Apache-2.0); see
/// <c>corpus/queries/edge/README.md</c> and the repository's <c>NOTICE</c>. This class is the reason
/// none of that project's expected values had to be taken: where it pins Calcite's behaviour, Chalk
/// asks an engine with no stake in either design.
/// </para>
/// <para>
/// A query with a <c>-- compare: top-k-under-ties</c> header is judged against the reference
/// executor's <em>untruncated</em> ordered rows under D166, exactly as
/// <see cref="EdgeDifferentialTests"/> judges Chalk's own answer: DuckDB's top-k is then held to
/// being <em>a</em> right answer rather than to being the same one.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class EdgeDuckDbOracleTests(SharedSidecar sidecar)
{
    private static readonly CorpusFixture Fixture = CorpusFixture.Shared;

    /// <summary>
    /// The queries DuckDB is not asked about, and why.
    /// </summary>
    private static readonly Dictionary<string, string> NotPortable = new(StringComparer.Ordinal)
    {
        // ROW_NUMBER() over a partition with no ORDER BY: SQL does not determine which row gets
        // which number, only the multiset per partition. Both Chalk executors read the fixture in
        // one order and agree; DuckDB has no reason to pick the same one, and asserting that it
        // does would be asserting an accident.
        ["a3_08_row_number_with_no_order"] =
            "ROW_NUMBER() with no ORDER BY assigns numbers SQL does not determine",

        // LATERAL UNNEST of a NULL and an empty list. DuckDB's UNNEST of a NULL list produces one
        // NULL row rather than none, so a LEFT LATERAL over it cannot be compared: the difference
        // is DuckDB's own, and the reference executor answers for these two instead (ADR 0024).
        ["a5_01_unnest_a_null_array"] = "DuckDB's UNNEST of a NULL list is not the standard's",
        ["a5_05_unnest_an_empty_array"] = "DuckDB's UNNEST of an empty list is not the standard's",
    };

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadEdge())
        {
            if (!NotPortable.ContainsKey(query.Name))
            {
                data.Add(query.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task DuckDB_agrees_with_the_vectorised_executor(string name) =>
        await CompareAsync(name, ExecutionEngine.Vectorised);

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task DuckDB_agrees_with_the_reference_executor(string name) =>
        await CompareAsync(name, ExecutionEngine.Reference);

    private async Task CompareAsync(string name, ExecutionEngine engine)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.SkipWhen(DuckDbOracle.SkipReason is not null, DuckDbOracle.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadEdge().Single(q => q.Name == name);
        await using var chalk = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = engine },
        });

        var prepared = await chalk.PrepareAsync(query.Sql, query.PrepareOptions());
        var (chalkRows, _, plan) = await DifferentialRunner.RunWithPlanAsync(chalk, prepared, query);

        using var oracle = DuckDbOracle.Create(Fixture);
        var duck = oracle.Run(DuckDbDialect.For(query), prepared.OutputSchema);

        IReadOnlyList<RecordBatch>? unbounded = null;
        try
        {
            var options = Comparison(query, plan);
            if (options.Mode == ResultComparisonMode.TopKUnderTies)
            {
                // Both sides are held to the same standard: each must be *a* right answer to the
                // top-k, judged against the fully ordered result. Comparing the two to each other
                // would fail on a legitimate difference and prove nothing about either.
                unbounded = await UnboundedReferenceAsync(query);
                CorpusDifferentialTests.Compare(
                    $"{query.Name}: DuckDB against the ordered reference", unbounded, duck, options);
                CorpusDifferentialTests.Compare(
                    $"{query.Name}: {engine} against the ordered reference",
                    unbounded,
                    chalkRows,
                    options);
                return;
            }

            CorpusDifferentialTests.Compare(
                $"{query.Name}: DuckDB vs {engine}", duck, chalkRows, options);
        }
        finally
        {
            DifferentialRunner.Dispose(duck);
            DifferentialRunner.Dispose(chalkRows);
            if (unbounded is not null)
            {
                DifferentialRunner.Dispose(unbounded);
            }
        }
    }

    /// <summary>
    /// The differential rules, loosened as <see cref="DuckDbOracleTests"/> loosens them — the
    /// comparison is always a multiset, because a Chalk plan's collation is a promise about Chalk's
    /// output and not about SQL — and then given the header's mode (D166), whose own check is
    /// order-sensitive on the actual side and therefore stricter than the multiset it replaces.
    /// </summary>
    private static ResultComparisonOptions Comparison(CorpusQuery query, Plan plan)
    {
        var options = EdgeDifferentialTests.ComparisonFor(query, plan, Fixture.Catalog);
        if (options.Mode == ResultComparisonMode.TopKUnderTies)
        {
            return options;
        }

        return new ResultComparisonOptions
        {
            CompareAsMultiset = true,
            OrderKeys = options.OrderKeys,
            AggregatedFloatColumns = options.AggregatedFloatColumns,
            UlpTolerance = 64,
        };
    }

    private async Task<IReadOnlyList<RecordBatch>> UnboundedReferenceAsync(CorpusQuery query)
    {
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = new LimitLiftingPlanner(sidecar.CreatePlanner()),
            Execution = new ExecutionOptions { Engine = ExecutionEngine.Reference },
        });

        var prepared = await engine.PrepareAsync(
            query.Sql, query.PrepareOptions(PushdownLevel.None));
        var (batches, _) = await DifferentialRunner.RunAsync(engine, prepared, query);
        return batches;
    }
}
