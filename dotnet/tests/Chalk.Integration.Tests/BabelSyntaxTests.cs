using Apache.Arrow;
using Chalk.Client;
using Chalk.TestKit;
using PlanErrorKind = Chalk.Client.Rpc.PlanErrorKind;

namespace Chalk.Integration.Tests;

/// <summary>
/// D259 end to end: a statement in syntax only Calcite's Babel parser accepts, prepared through the
/// sidecar under <see cref="SqlConformance.Babel"/> and <em>run</em>, against the same statement
/// spelled in standard SQL under <see cref="SqlConformance.Default"/>. The rows must be identical.
/// </summary>
/// <remarks>
/// <para>
/// Planning it is not enough. A dialect that parses a statement into a slightly different tree would
/// still plan, and would still produce a plan the client can carry; the thing worth asserting is
/// that the answer does not depend on how the statement was spelled. So each pair runs on the same
/// fixture through the same engine and the batches are compared row by row.
/// </para>
/// <para>
/// No recorded plan fixture is added by this class. It plans against the live sidecar, exactly as
/// the rest of <c>Chalk.Integration.Tests</c> does; <c>RecordedPlanner.KeyFor</c> would file these
/// under their own keys, because it takes the conformance and the libraries, so a later run that
/// wants them recorded gets separate files and moves nothing that exists.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class BabelSyntaxTests(SharedSidecar sidecar)
{
    private static readonly CorpusFixture Fixture = CorpusFixture.Shared;

    /// <summary>
    /// PostgreSQL's <c>::</c> cast. Two things are needed and the test would pass with neither
    /// noticed if it only planned: the Babel parser, which <see cref="SqlConformance.Babel"/>
    /// selects, and <see cref="SqlLibrary.Postgresql"/>, where the <c>::</c> operator lives.
    /// </summary>
    [Fact]
    public async Task A_postgres_cast_under_babel_returns_the_rows_the_standard_cast_does()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await AssertSameRowsAsync(
            babel: "SELECT id::VARCHAR AS id_text FROM events ORDER BY id",
            standard: "SELECT CAST(id AS VARCHAR) AS id_text FROM events ORDER BY id",
            libraries: [SqlLibrary.Postgresql]);
    }

    /// <summary>
    /// <c>SELECT * EXCLUDE</c> under Babel against the explicit column list under the default
    /// dialect. Measured note, recorded in ADR 0039 §2: <c>EXCLUDE</c> is the <em>core</em> parser
    /// on the pinned build, so this pair passes because the two spellings mean the same thing, not
    /// because Babel gave anyone a construct they did not have.
    /// </summary>
    [Fact]
    public async Task A_star_exclude_under_babel_returns_the_rows_the_explicit_list_does()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await AssertSameRowsAsync(
            babel: "SELECT * EXCLUDE (kind) FROM events ORDER BY id",
            standard: "SELECT id, symbol, start_ts, end_ts FROM events ORDER BY id",
            libraries: []);
    }

    /// <summary>
    /// A keyword Babel's grammar does not reserve, used as an output name, against the same
    /// statement with the name quoted — which is the only way to write it in standard SQL.
    /// </summary>
    [Fact]
    public async Task A_non_reserved_keyword_as_an_output_name_returns_the_quoted_spellings_rows()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await AssertSameRowsAsync(
            babel: "SELECT id AS value FROM events ORDER BY id",
            standard: "SELECT id AS \"value\" FROM events ORDER BY id",
            libraries: []);
    }

    /// <summary>The other direction: the same <c>::</c> statement is a parse failure by default.</summary>
    [Fact]
    public async Task The_default_dialect_refuses_the_postgres_cast_at_parsing()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateAsync();

        var error = await Assert.ThrowsAsync<PlanningException>(
            () => engine.PrepareAsync(
                "SELECT id::VARCHAR AS id_text FROM events",
                new PrepareOptions
                {
                    Conformance = SqlConformance.Default,
                    Libraries = [SqlLibrary.Postgresql],
                },
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(PlanErrorKind.Parse, error.Kind);
    }

    /// <summary>
    /// And a statement Babel parses that Chalk is not a home for fails at planning with a named
    /// kind and a message that says what was unsupported — never a wrong answer, and never the
    /// internal error it was before <c>BabelStatementSupport</c> (ADR 0039 §2).
    /// </summary>
    [Fact]
    public async Task A_babel_statement_that_is_not_a_query_is_unsupported_not_internal()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateAsync();

        var error = await Assert.ThrowsAsync<PlanningException>(
            () => engine.PrepareAsync(
                "CREATE TABLE t (a INTEGER)",
                new PrepareOptions { Conformance = SqlConformance.Babel },
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(PlanErrorKind.Unsupported, error.Kind);
        Assert.Contains("CREATE TABLE", error.Message, StringComparison.Ordinal);
        Assert.Contains("Chalk plans queries", error.Message, StringComparison.Ordinal);
    }

    private async Task AssertSameRowsAsync(
        string babel, string standard, IReadOnlyList<SqlLibrary> libraries)
    {
        await using var engine = await CreateAsync();

        var actual = await RunAsync(engine, babel, SqlConformance.Babel, libraries);
        var expected = await RunAsync(engine, standard, SqlConformance.Default, []);
        try
        {
            Assert.NotEmpty(actual);
            ResultComparer.AssertEquivalent(expected, actual, ResultComparisonOptions.Ordered);
        }
        finally
        {
            foreach (var batch in actual.Concat(expected))
            {
                batch.Dispose();
            }
        }
    }

    private static async Task<List<RecordBatch>> RunAsync(
        ChalkEngine engine,
        string sql,
        SqlConformance conformance,
        IReadOnlyList<SqlLibrary> libraries)
    {
        var prepared = await engine.PrepareAsync(
            sql,
            new PrepareOptions { Conformance = conformance, Libraries = libraries },
            TestContext.Current.CancellationToken);

        var execution = await engine.ExecuteAsync(
            prepared, (IReadOnlyList<object?>?)null, arena: null);
        await using (execution)
        {
            var batches = new List<RecordBatch>();
            await foreach (var batch in execution.Batches)
            {
                batches.Add(batch);
            }

            return batches;
        }
    }

    private async Task<ChalkEngine> CreateAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
        });
}
