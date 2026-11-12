using Chalk.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.DuckDb;
using Chalk.TestKit;
using DuckDB.NET.Data;

namespace Chalk.Integration.Tests;

/// <summary>
/// A source that raises inside the quarantine (F302, design 82 §6): the policy's part the source was
/// sent is evaluated here instead, over a plain read of the table, and the statement answers as an
/// in-process plan would; the host's observer is told, with the source named. A table whose row
/// predicate must be enforced at the source gets no second read, and its result is withheld, quoting
/// nothing the source said.
/// </summary>
/// <remarks>
/// DuckDB's <c>reports</c> holds a level written as text, as <see cref="QuarantineTests"/>' does: R2's
/// is <c>X</c>, and DuckDB refuses to cast it, quoting it. The row predicate's cast is sent to DuckDB.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class QuarantineSourceTests(SharedSidecar sidecar)
{
    private const string Predicate = "CAST(Level AS INTEGER) <= 2";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _duckFile;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// DuckDB raises on R2's level before it returns a row; the read is made again here, the predicate
    /// evaluated by ChalkQL, and R1 is the answer, as it is in process.
    /// </summary>
    [Fact]
    public async Task A_source_that_raises_has_its_part_evaluated_here()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Decisions();
        await using var engine = await EngineAsync(new TableEntitlementDescriptor { RowPredicate = Predicate });

        Assert.Equal(["R1"], await RowsAsync(engine, "SELECT ReportId FROM reports ORDER BY ReportId", observer));

        var here = Assert.Single(observer.Seen, d => d.Resolution == ClosedResolution.EvaluatedHere);
        Assert.Equal(IndeterminateAt.Source, here.At);
        Assert.Equal("main.reports", here.Tables);
        Assert.Contains("'X'", here.Reason, StringComparison.Ordinal);
        var local = Assert.Single(observer.Seen, d => d.Resolution == ClosedResolution.RowsNotAdmitted);
        Assert.Equal(IndeterminateAt.ChalkQL, local.At);
        Assert.Equal(here.CorrelationId, local.CorrelationId);
    }

    /// <summary>The same with no observer: nothing is built for one, and the answer is the same.</summary>
    [Fact]
    public async Task Unobserved_the_answer_is_the_same()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(new TableEntitlementDescriptor { RowPredicate = Predicate });

        Assert.Equal(["R1"], await RowsAsync(engine, "SELECT ReportId FROM reports WHERE Note <> 'second'"));
    }

    /// <summary>
    /// A table whose row predicate must be enforced at the source is not read without it: the result is
    /// withheld, naming the table and quoting nothing DuckDB said, and the observer is told.
    /// </summary>
    [Fact]
    public async Task Where_the_predicate_must_be_enforced_at_the_source_the_result_is_withheld()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Decisions();
        await using var engine = await EngineAsync(new TableEntitlementDescriptor
        {
            RowPredicate = Predicate,
            Enforcement = Enforcement.PushdownRequired,
        });

        var withheld = await Assert.ThrowsAsync<PolicyIndeterminateException>(
            () => RowsAsync(engine, "SELECT ReportId FROM reports", observer));

        Assert.Equal("main.reports", withheld.Tables);
        Assert.DoesNotContain("'X'", withheld.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Conversion", withheld.ToString(), StringComparison.Ordinal);
        var decision = Assert.Single(observer.Seen);
        Assert.Equal(ClosedResolution.StatementWithheld, decision.Resolution);
        Assert.Equal(IndeterminateAt.Source, decision.At);
        Assert.Equal(withheld.CorrelationId, decision.CorrelationId);
    }

    private async Task<ChalkEngine> EngineAsync(TableEntitlementDescriptor descriptor) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "quarantine-source",
            Sources =
            [
                DuckDbSources.AddDuckDbSource("duck", "DataSource=" + await DuckFileAsync())
                    .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
                    .DiscoverTables()
                    .Entitlement("reports", descriptor)
                    .Build(),
            ],
            Planner = sidecar.CreatePlanner(),
        }, Ct);

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

    private sealed class Decisions : IIndeterminateDecisions
    {
        public List<IndeterminateDecision> Seen { get; } = [];

        public void Resolved(IndeterminateDecision decision) => Seen.Add(decision);
    }
}
