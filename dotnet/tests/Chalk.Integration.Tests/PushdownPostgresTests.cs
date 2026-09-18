using System.Text;
using Chalk.Client;
using Chalk.Ir;
using Chalk.TestKit;
using Npgsql;

namespace Chalk.Integration.Tests;

/// <summary>
/// The pushdown corpus queries PostgreSQL can serve, against a real PostgreSQL server (§5, D133
/// §0b): the same differential against the reference executor that every other pushdown query gets,
/// and the generated SQL recorded as a golden.
/// </summary>
/// <remarks>
/// <para>
/// Not the whole corpus. <see cref="RemoteFixture.PostgresTables"/> deliberately leaves
/// <c>lineitem</c> out — sixty thousand rows to prove nothing the other two copies do not — and
/// most of <c>m7-pushdown</c> reads it. <see cref="Servable"/> is therefore an explicit list of the
/// queries whose tables PostgreSQL holds, not a filter that might quietly widen.
/// </para>
/// <para>
/// Each query is rewritten once, <c>duck.</c> to <c>pg.</c>, exactly as
/// <see cref="FederationPostgresTests"/> rewrites the federation corpus. The goldens live beside
/// the DuckDB ones in <c>corpus/plans/m7-pushdown-postgres/</c> for the same reason the federation
/// corpus has a PostgreSQL directory of its own: the plan differs, so the text does, and a
/// converter change against a dialect nobody has in-process has to be reviewable rather than merely
/// tested.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class PushdownPostgresTests(SharedSidecar sidecar, SharedPostgres postgres)
{
    /// <summary>Where the PostgreSQL half of the pushdown corpus records its generated SQL.</summary>
    private static DirectoryInfo Goldens =>
        new(Path.Combine(RepoLayout.Corpus.FullName, "plans", "m7-pushdown-postgres"));

    private static RemoteFixture Fixture => RemoteFixture.Shared;

    /// <summary>
    /// The pushdown queries whose tables PostgreSQL holds. The first two are F27's (ADR 0027):
    /// PostgreSQL's <c>/</c> over two integers is integer division truncating towards zero, as
    /// Chalk's is — measured on 16.13, <c>-7 / 3 = -2</c> in an <c>integer</c> — so it is the
    /// dialect that says what the DuckDB pair would have said had DuckDB's <c>/</c> meant the same
    /// thing. The third is D273's conditional: <c>supports_case</c> defaults to true on the claim
    /// that every dialect spells a <c>CASE</c> the same way, and a third recorded text is what makes
    /// the claim checkable on a dialect nobody here runs in process.
    /// </summary>
    private static IReadOnlyList<string> Servable { get; } =
        ["17_duck_integer_division", "19_duck_integer_division_in_a_predicate", "20_duck_case"];

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var name in Servable)
        {
            data.Add(name);
        }

        return data;
    }

    public static TheoryData<string, PushdownLevel> QueriesAtEveryLevel()
    {
        var data = new TheoryData<string, PushdownLevel>();
        foreach (var name in Servable)
        {
            foreach (var level in new[]
            {
                PushdownLevel.Full,
                PushdownLevel.FiltersOnly,
                PushdownLevel.ProjectionOnly,
                PushdownLevel.None,
            })
            {
                data.Add(name, level);
            }
        }

        return data;
    }

    /// <summary>
    /// The same query at every level gives the same answer, and the reference executor — which
    /// pushes nothing anywhere — is what "the same" is measured against.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueriesAtEveryLevel))]
    public async Task Every_level_agrees_with_the_reference_executor_over_a_real_server(
        string name, PushdownLevel level)
    {
        var query = Variant(name);
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
                $"{name} at {level} over PostgreSQL",
                expected,
                actual,
                DifferentialRunner.ComparisonFor(actualPlan, Fixture.PostgresPartitionedCatalog()));
        }
        finally
        {
            DifferentialRunner.Dispose(actual);
            DifferentialRunner.Dispose(expected);
        }
    }

    /// <summary>The plan shape the query's <c>-- expect:</c> block asks for, over this dialect too.</summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task A_query_pushes_what_its_expectations_say(string name)
    {
        var query = Variant(name);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);
        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions());
        PlanExpectations.Check(query, prepared.Plan);
    }

    /// <summary>The SQL PostgreSQL is actually sent, recorded (§0b).</summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task The_generated_sql_matches_the_postgresql_golden(string name)
    {
        var query = Variant(name);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);

        var text = new StringBuilder();
        foreach (var level in
            new[] { PushdownLevel.Full, PushdownLevel.FiltersOnly, PushdownLevel.ProjectionOnly })
        {
            var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions(level));
            text.Append("-- ").Append(level).Append('\n');
            var found = 0;
            foreach (var rel in PlanWalker.Rels(prepared.Plan.Root))
            {
                if (rel.KindCase != Rel.KindOneofCase.RemoteQuery)
                {
                    continue;
                }

                found++;
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

            if (found == 0)
            {
                text.Append("(nothing pushed)\n");
            }

            text.Append('\n');
        }

        var file = new FileInfo(Path.Combine(Goldens.FullName, name + ".sql"));
        if (Environment.GetEnvironmentVariable("CHALK_WRITE_FIXTURES") == "1")
        {
            Goldens.Create();
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
    /// The corpus query with PostgreSQL in place of DuckDB. Skips the whole class's tests when there
    /// is no server.
    /// </summary>
    private CorpusQuery Variant(string name)
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.SkipWhen(
            postgres.Fixture.SkipReason is not null, postgres.Fixture.SkipReason ?? string.Empty);

        Attach();
        var query = CorpusQueries.LoadM7().Single(q => q.Name == name);
        return new CorpusQuery
        {
            Name = query.Name,
            Milestone = query.Milestone,
            Sql = Rewrite(query.Sql),
            PlannerSql = Rewrite(query.PlannerSql),
            Expectations = query.Expectations,
            Conformance = query.Conformance,
            Libraries = query.Libraries,
            JoinPolicy = query.JoinPolicy,
        };
    }

    private static string Rewrite(string sql) =>
        sql.Replace("duck.", RemoteFixture.PostgresSourceId + ".", StringComparison.Ordinal);

    /// <summary>Loads the corpus into PostgreSQL, once per run, exactly as the federation leg does.</summary>
    private void Attach()
    {
        lock (AttachGate)
        {
            if (Fixture.Postgres is not null)
            {
                return;
            }

            Fixture.AttachPostgres(
                Database(postgres.Fixture),
                connectionString => new NpgsqlConnection(connectionString));
        }
    }

    private static readonly Lock AttachGate = new();

    private static string Database(PostgresFixture fixture)
    {
        try
        {
            return fixture.CreateDatabase("chalk_federation");
        }
        catch (InvalidOperationException)
        {
            return fixture.ConnectionString!;
        }
    }

    private async Task<ChalkEngine> CreateEngineAsync(ExecutionEngine engine) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.PostgresSources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = engine, BatchSize = 4096 },
        });
}
