using Chalk.Client;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The third oracle (D28, work-plan step 12). Every corpus query is answered by an in-memory DuckDB
/// loaded from the same fixture and compared with both Chalk executors under the rules of
/// <c>05-testing.md</c> §5.
/// </summary>
/// <remarks>
/// The differential tests prove the two Chalk executors agree; a LINQ oracle proves they agree with
/// what a test author expected. Neither catches the case where Chalk has simply misread SQL — a
/// NULL that should propagate, a rounding rule, the semantics of an empty <c>IN</c> list. DuckDB has
/// no stake in Chalk's design, which is the whole point of asking it.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class DuckDbOracleTests(SharedSidecar sidecar)
{
    // Smaller than the shared corpus fixture: DuckDB loads every row through the Appender, and the
    // questions being asked are about semantics, not scale.
    private static readonly CorpusFixture Fixture =
        CorpusFixture.Create(barMinutes: 720, lineItemRows: 4_000);

    /// <summary>
    /// The one corpus query DuckDB is not asked about. <c>06_asof_null_left_time</c> is precisely the
    /// case where DuckDB's ASOF join and SQL's three-valued logic part company: given a left row
    /// whose time is NULL, DuckDB matched the last right row rather than none, and did so only once
    /// the right partition was large enough to take its sort-merge path (ADR 0016). Chalk follows
    /// Calcite and D41 — a NULL time matches nothing — so the oracle for this one is the reference
    /// executor, which the differential suite runs over it like every other query.
    /// </summary>
    private static readonly string[] NotPortable =
    [
        "06_asof_null_left_time",

        // Step 19's two window table functions: DuckDB has neither a HOP nor a SESSION, and the
        // reference executor plus the LINQ oracle of LinqOracleTests answer for them instead
        // (14-windows-ii.md §9).
        "01_hop_group_by",
        "02_session_windows",
        "02b_session_grouped",
        "13_hop_joined_and_windowed",

        // DuckDB refuses DISTINCT inside a window function outright ("DISTINCT is not implemented
        // for window functions"), so D56 has no oracle here.
        "03_count_distinct_over",
        "04_sum_distinct_running",
        "12_distinct_exclude",

        // MODE's tie rule is Chalk's (D57 pins it to the smallest value) and DuckDB's is
        // unspecified — measured at 1.5.1 it keeps the first value it saw — so the two disagree
        // exactly where the corpus is most interesting, over a group and over a frame alike.
        "09_mode",
        "24_mode_over_window",
    ];

    /// <summary>
    /// M1, the join corpus and the two window corpora. The M2 queries are index-shape claims with
    /// bound parameters, and an oracle that has no indexes has nothing to say about them.
    /// </summary>
    private static IEnumerable<CorpusQuery> Portable() =>
        CorpusQueries.Load()
            .Concat(CorpusQueries.LoadM3())
            .Concat(CorpusQueries.LoadM4())
            .Concat(CorpusQueries.LoadM5())
            .Concat(CorpusQueries.LoadM6());

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        // M1 and the join corpus. The M2 queries are index-shape claims with bound parameters, and
        // an oracle that has no indexes has nothing to say about them.
        foreach (var query in Portable())
        {
            if (!NotPortable.Contains(query.Name))
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

        var query = Portable().Single(q => q.Name == name);
        await using var chalk = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = engine },
        });

        var prepared = await chalk.PrepareAsync(query.Sql, query.PrepareOptions());
        var (actual, _, plan) = await DifferentialRunner.RunWithPlanAsync(chalk, prepared, query);

        using var oracle = DuckDbOracle.Create(Fixture);
        var expected = oracle.Run(DuckDbDialect.For(query), prepared.OutputSchema);

        try
        {
            CorpusDifferentialTests.Compare(
                $"{query.Name}: DuckDB vs {engine}",
                expected,
                actual,
                Comparison(plan));
        }
        finally
        {
            DifferentialRunner.Dispose(expected);
            DifferentialRunner.Dispose(actual);
        }
    }

    /// <summary>
    /// The differential rules, loosened twice.
    ///
    /// <para>
    /// The comparison is always a multiset. A Chalk plan's collation is a promise about Chalk's
    /// output, not about SQL: corpus query 25 is <c>SELECT DISTINCT ts, symbol FROM bars</c>, whose
    /// plan is a bare scan carrying the table's declared order, and DuckDB — asked a question with no
    /// ORDER BY — is free to answer in any order at all. Chalk's side is still held to every ordering
    /// its plan claims, because <see cref="ResultComparer"/> asserts the order keys against the actual
    /// rows before sorting either side.
    /// </para>
    ///
    /// <para>
    /// And the ULP tolerance is wider, because DuckDB reduces <c>AVG</c> over a DECIMAL to a DOUBLE
    /// where Calcite keeps a DECIMAL: such a column crosses a type boundary on its way here and
    /// cannot be compared bit for bit.
    /// </para>
    /// </summary>
    private static ResultComparisonOptions Comparison(Plan plan)
    {
        var options = DifferentialRunner.ComparisonFor(plan, Fixture.Catalog);
        return new ResultComparisonOptions
        {
            CompareAsMultiset = true,
            OrderKeys = options.OrderKeys,
            AggregatedFloatColumns = options.AggregatedFloatColumns,
            UlpTolerance = 64,
        };
    }
}
