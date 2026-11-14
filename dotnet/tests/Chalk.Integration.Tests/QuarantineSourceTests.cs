using Chalk.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.DuckDb;
using Chalk.TestKit;
using DuckDB.NET.Data;
using Npgsql;

namespace Chalk.Integration.Tests;

/// <summary>
/// The policy's quarantine at a source (F302, design 82 §6). A part of the policy that can raise for a
/// row is not sent: ChalkQL evaluates it over the rows the source returns. What ChalkQL judges cannot
/// raise is sent, and a source that raises on it all the same has its part evaluated here instead,
/// over a plain read of the table, and the statement answers as an in-process plan would; the host's
/// observer is told, with the source's error class. A table whose row predicate must be enforced at
/// the source gets no second read, and its result is withheld, quoting nothing the source said.
/// </summary>
/// <remarks>
/// DuckDB's <c>reports</c> holds a level written as text: R2's is <c>X</c>, which does not convert.
/// PostgreSQL's holds a weight: R2's is zero, and PostgreSQL raises on a floating-point division by
/// zero (SQLSTATE 22012) where ChalkQL answers infinity.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class QuarantineSourceTests(SharedSidecar sidecar, SharedPostgres postgres)
{
    private const string Cast = "CAST(Level AS INTEGER) <= 2";
    private const string Division = "score / weight < 2.0";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _duckFile;
    private static string? _database;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The cast is not sent to DuckDB: the rows come back and ChalkQL casts them under the quarantine,
    /// so R1 is the answer, R2 is not admitted, and DuckDB never raised.
    /// </summary>
    [Fact]
    public async Task A_part_that_can_raise_is_evaluated_here_and_not_sent()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Decisions();
        await using var engine = await EngineAsync(await DuckAsync(new TableEntitlementDescriptor { RowPredicate = Cast }));

        Assert.Equal(["R1"], await RowsAsync(engine, "SELECT ReportId FROM reports ORDER BY ReportId", observer));

        var decision = Assert.Single(observer.Seen);
        Assert.Equal(ClosedResolution.RowsNotAdmitted, decision.Resolution);
        Assert.Equal(IndeterminateAt.ChalkQL, decision.At);
    }

    /// <summary>
    /// On a table whose row predicate must be enforced at the source, a predicate that cannot be sent
    /// is refused when the statement is prepared, as one the source cannot take is.
    /// </summary>
    [Fact]
    public async Task Where_the_predicate_must_be_enforced_at_the_source_a_part_that_can_raise_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(await DuckAsync(new TableEntitlementDescriptor
        {
            RowPredicate = Cast,
            Enforcement = Enforcement.PushdownRequired,
        }));

        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            () => RowsAsync(engine, "SELECT ReportId FROM reports"));
        Assert.Contains("PUSHDOWN_REQUIRED", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// PostgreSQL raises on R2's division before it returns a row; the read is made again here, the
    /// predicate evaluated by ChalkQL, and R1 and R3 are the answer, as they are in process. The
    /// observer hears the source's error class and its message.
    /// </summary>
    [Fact]
    public async Task A_source_that_raises_has_its_part_evaluated_here()
    {
        var observer = new Decisions();
        await using var engine = await EngineAsync(await PostgresAsync(new TableEntitlementDescriptor { RowPredicate = Division }));

        Assert.Equal(["R1", "R3"], await RowsAsync(engine, "SELECT report_id FROM reports ORDER BY report_id", observer));

        var decision = Assert.Single(observer.Seen);
        Assert.Equal(ClosedResolution.EvaluatedHere, decision.Resolution);
        Assert.Equal(IndeterminateAt.Source, decision.At);
        Assert.Equal("22012", decision.SqlState);
        Assert.Equal("main.reports", decision.Tables);
        Assert.Contains("division by zero", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>The same with no observer: nothing is built for one, and the answer is the same.</summary>
    [Fact]
    public async Task Unobserved_the_answer_is_the_same()
    {
        await using var engine = await EngineAsync(await PostgresAsync(new TableEntitlementDescriptor { RowPredicate = Division }));

        Assert.Equal(["R1"], await RowsAsync(engine, "SELECT report_id FROM reports WHERE note <> 'third'"));
    }

    /// <summary>
    /// A table whose row predicate must be enforced at the source is not read without it: the result is
    /// withheld, naming the table and quoting nothing PostgreSQL said, and the observer is told.
    /// </summary>
    [Fact]
    public async Task Where_the_predicate_must_be_enforced_at_the_source_the_result_is_withheld()
    {
        var observer = new Decisions();
        await using var engine = await EngineAsync(await PostgresAsync(new TableEntitlementDescriptor
        {
            RowPredicate = Division,
            Enforcement = Enforcement.PushdownRequired,
        }));

        var withheld = await Assert.ThrowsAsync<PolicyIndeterminateException>(
            () => RowsAsync(engine, "SELECT report_id FROM reports", observer));

        Assert.Equal("main.reports", withheld.Tables);
        Assert.Equal("22012", withheld.SqlState);
        Assert.DoesNotContain("division", withheld.ToString(), StringComparison.Ordinal);
        var decision = Assert.Single(observer.Seen);
        Assert.Equal(ClosedResolution.StatementWithheld, decision.Resolution);
        Assert.Equal(IndeterminateAt.Source, decision.At);
        Assert.Equal(withheld.CorrelationId, decision.CorrelationId);
    }

    private async Task<ChalkEngine> EngineAsync(ISourceRuntime source) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "quarantine-source",
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
        }, Ct);

    private static async Task<ISourceRuntime> DuckAsync(TableEntitlementDescriptor descriptor) =>
        DuckDbSources.AddDuckDbSource("duck", "DataSource=" + await DuckFileAsync())
            .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
            .DiscoverTables()
            .Entitlement("reports", descriptor)
            .Build();

    private async Task<ISourceRuntime> PostgresAsync(TableEntitlementDescriptor descriptor) =>
        new AdoSourceBuilder("pg", NpgsqlFactory.Instance, await DatabaseAsync())
            .Dialect(DialectProfiles.PostgreSql)
            .Capabilities(AdoCapabilities.For(DialectProfiles.PostgreSql))
            .DiscoverTables("public")
            .Entitlement("reports", descriptor)
            .Build();

    private static async Task<string[]> RowsAsync(ChalkEngine engine, string sql, IIndeterminateDecisions? observer = null)
    {
        var entitled = engine.WithEntitlements(indeterminate: observer);
        var context = new RequestContext
        {
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["unused"] = new() { Columns = ["id"], Rows = [[1]], ColumnTypes = [ChalkType.Int32()] },
            },
        };
        var prepared = await entitled.PrepareAsync(sql, context, ct: Ct);
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared.Query, ct: Ct);
        await foreach (var batch in execution.Batches.WithCancellation(Ct))
        {
            using (batch)
            {
                rows.AddRange(batch.ToRows().Select(r => Convert.ToString(r[0])!));
            }
        }

        return [.. rows];
    }

    private static async Task<string> DuckFileAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_duckFile is not null)
            {
                return _duckFile;
            }

            var path = Path.Combine(Path.GetTempPath(), $"chalk-quarantine-{Environment.ProcessId}.duckdb");
            File.Delete(path);
            await using var connection = new DuckDBConnection("DataSource=" + path);
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText =
                "CREATE TABLE reports(ReportId VARCHAR NOT NULL PRIMARY KEY, Level VARCHAR NOT NULL, Note VARCHAR NOT NULL);"
                + "INSERT INTO reports VALUES ('R1', '1', 'first'), ('R2', 'X', 'second'), ('R3', '3', 'third');";
            await create.ExecuteNonQueryAsync();
            return _duckFile = path;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<string> DatabaseAsync()
    {
        var fixture = postgres.Fixture;
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await Gate.WaitAsync();
        try
        {
            if (_database is not null)
            {
                return _database;
            }

            var connection = fixture.CreateDatabase("quarantine_" + Environment.ProcessId);
            await using var db = new NpgsqlConnection(connection);
            await db.OpenAsync();
            await using var create = new NpgsqlCommand(
                "DROP TABLE IF EXISTS reports;"
                + "CREATE TABLE reports(report_id text PRIMARY KEY, note text NOT NULL, score float8 NOT NULL, weight float8 NOT NULL);"
                + "INSERT INTO reports VALUES ('R1', 'first', 1.3, 1.0), ('R2', 'second', 2.6, 0.0), ('R3', 'third', 3.9, 2.0);",
                db);
            await create.ExecuteNonQueryAsync();
            return _database = connection;
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed class Decisions : IIndeterminateDecisions
    {
        public List<IndeterminateDecision> Seen { get; } = [];

        public void Resolved(IndeterminateDecision decision) => Seen.Add(decision);
    }
}
