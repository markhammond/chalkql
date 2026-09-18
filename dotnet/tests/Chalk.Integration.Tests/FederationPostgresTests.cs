using System.Text;
using Chalk.Client;
using Chalk.Ir;
using Chalk.TestKit;
using Npgsql;
using CrossSourceJoinPolicy = Chalk.Catalog.CrossSourceJoinPolicy;
using SourcePairRule = Chalk.Catalog.SourcePairRule;

namespace Chalk.Integration.Tests;

/// <summary>
/// The federation corpus again, with a real PostgreSQL server standing in for one of the in-process
/// sources (D133 §0b, <c>docs/design/20-m5-federation.md</c>). Every other source in the fixture is
/// in-process, so this is the only leg where a cross-source join genuinely crosses a socket: a real
/// dialect, a real driver, real round trips, and the same rows out.
/// </summary>
/// <remarks>
/// <para>
/// Each query is rewritten once — <c>sqlite.</c> becomes <c>pg.</c>, or <c>duck.</c> does when the
/// query names no SQLite table — so that exactly one side of every federated query is the network
/// source and the query stays federated. The two partitioned queries need no rewriting: they name
/// the logical table, and the catalog puts its XRPUSDT partition in PostgreSQL.
/// </para>
/// <para>
/// The oracle is the same as everywhere else, the reference executor pulling everything local. The
/// generated SQL is recorded as goldens per §0b, which is what makes a converter change against a
/// dialect nobody has in-process reviewable rather than merely tested.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class FederationPostgresTests(SharedSidecar sidecar, SharedPostgres postgres)
{
    /// <summary>Where the PostgreSQL half of the federation corpus records its generated SQL.</summary>
    private static DirectoryInfo Goldens =>
        new(Path.Combine(RepoLayout.Corpus.FullName, "plans", "m9-federation-postgres"));

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

    /// <summary>
    /// The whole point: whichever strategy the planner chose against a network source, the answer is
    /// the reference executor's answer.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Every_federation_query_agrees_with_the_reference_executor_over_a_real_server(
        string name)
    {
        var query = Variant(name);
        await using var vectorised = await CreateEngineAsync(ExecutionEngine.Vectorised);
        await using var reference = await CreateEngineAsync(ExecutionEngine.Reference);

        var actualPrepared = await vectorised.PrepareAsync(query.Sql, query.PrepareOptions());
        var expectedPrepared = await reference.PrepareAsync(
            query.Sql, query.PrepareOptions(PushdownLevel.None));

        var (actual, _, actualPlan) =
            await DifferentialRunner.RunWithPlanAsync(vectorised, actualPrepared, query);
        var (expected, _) = await DifferentialRunner.RunAsync(reference, expectedPrepared, query);

        try
        {
            CorpusDifferentialTests.Compare(
                $"{name} over PostgreSQL",
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

    /// <summary>
    /// The SQL PostgreSQL is actually sent, recorded (§0b). A lookup call's key set is a
    /// <c>?</c>-placeholder list the executor binds, so this is also where the shape of a bound key
    /// set in a real dialect is visible to a reviewer.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task The_generated_sql_matches_the_postgresql_golden(string name)
    {
        var query = Variant(name);
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
    /// The corpus query with PostgreSQL in place of one in-process source, and its policy header
    /// pointed at the same place. Skips the whole class's tests when there is no server.
    /// </summary>
    private CorpusQuery Variant(string name)
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.SkipWhen(
            postgres.Fixture.SkipReason is not null, postgres.Fixture.SkipReason ?? string.Empty);

        Attach();
        var query = CorpusQueries.LoadM9().Single(q => q.Name == name);
        var replaced = query.Sql.Contains("sqlite.", StringComparison.Ordinal)
            ? "sqlite"
            : query.Sql.Contains("duck.", StringComparison.Ordinal) ? "duck" : null;
        if (replaced is null)
        {
            return query;
        }

        return new CorpusQuery
        {
            Name = query.Name,
            Milestone = query.Milestone,
            Sql = Rewrite(query.Sql, replaced),
            PlannerSql = Rewrite(query.PlannerSql, replaced),
            Expectations = query.Expectations,
            Conformance = query.Conformance,
            Libraries = query.Libraries,
            JoinPolicy = Rewrite(query.JoinPolicy, replaced),
        };
    }

    private static string Rewrite(string sql, string source) =>
        sql.Replace(source + ".", RemoteFixture.PostgresSourceId + ".", StringComparison.Ordinal);

    /// <summary>A policy's pair rules follow the source they were written about.</summary>
    private static CrossSourceJoinPolicy? Rewrite(CrossSourceJoinPolicy? policy, string source)
    {
        if (policy is null)
        {
            return null;
        }

        return new CrossSourceJoinPolicy
        {
            DefaultStrategy = policy.DefaultStrategy,
            BroadcastMaxRows = policy.BroadcastMaxRows,
            LookupMaxCalls = policy.LookupMaxCalls,
            LocalJoinMaxRows = policy.LocalJoinMaxRows,
            UnknownRowCountAssumption = policy.UnknownRowCountAssumption,
            Pairs =
            [
                .. policy.Pairs.Select(pair => new SourcePairRule
                {
                    LeftSource = Rename(pair.LeftSource, source),
                    RightSource = Rename(pair.RightSource, source),
                    Allowed = pair.Allowed,
                    Preferred = pair.Preferred,
                    BroadcastMaxRows = pair.BroadcastMaxRows,
                }),
            ],
        };
    }

    private static string Rename(string sourceId, string replaced) =>
        string.Equals(sourceId, replaced, StringComparison.Ordinal)
            ? RemoteFixture.PostgresSourceId
            : sourceId;

    /// <summary>
    /// Loads the corpus into PostgreSQL, once per run. The server itself is the collection's, so a
    /// run that never reaches this class never starts one.
    /// </summary>
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

    /// <summary>
    /// A database of this run's own on the fixture's server, or the one an existing server named by
    /// <c>CHALK_TEST_POSTGRES</c> already points at — which is the only case the fixture will not
    /// create a database on.
    /// </summary>
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
