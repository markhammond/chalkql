using Chalk.Client;
using Chalk.TestKit;
using PlanErrorKind = Chalk.Client.Rpc.PlanErrorKind;
using PlanRequest = Chalk.Client.PlanRequest;
using SqlConformance = Chalk.Catalog.SqlConformance;

namespace Chalk.Integration.Tests;

/// <summary>
/// D34: the SQL dialect is chosen per statement, and <see cref="SqlConformance.Default"/> — standard
/// SQL — is what a statement gets when it asks for nothing.
/// </summary>
/// <remarks>
/// Four constructs that Calcite's own conformance rules gate, one of which (<c>OFFSET</c> before
/// <c>LIMIT</c>) the parser decides and three of which the validator does, so a level that reached
/// only one of the two configurations would show up here. The fifth case goes the other way: a
/// stricter dialect rejects something the default accepts, which is the half that proves the option
/// is not simply "be more permissive".
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class ConformanceTests(SharedSidecar sidecar)
{
    private static readonly CorpusFixture Fixture = CorpusFixture.Shared;

    /// <summary>The constructs D34 names, with the error the default dialect answers with.</summary>
    public static TheoryData<string, string, PlanErrorKind> LenientOnly() => new()
    {
        { "!=", "SELECT symbol FROM bars WHERE symbol != 'BTCUSDT'", PlanErrorKind.Parse },
        { "%", "SELECT symbol FROM bars WHERE volume % 2 = 0", PlanErrorKind.Parse },
        {
            "OFFSET before LIMIT",
            "SELECT symbol, ts FROM bars ORDER BY ts, symbol OFFSET 3 LIMIT 5",
            PlanErrorKind.Parse
        },
        {
            "GROUP BY on a SELECT alias",
            "SELECT symbol AS s, COUNT(*) AS n FROM bars GROUP BY s",
            PlanErrorKind.Validation
        },
    };

    [Theory]
    [MemberData(nameof(LenientOnly))]
    public async Task The_default_dialect_rejects_what_only_lenient_allows(
        string construct, string sql, PlanErrorKind expected)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var error = await Assert.ThrowsAsync<PlanningException>(
            () => PlanAsync(sql, SqlConformance.Default).AsTask());

        Assert.Equal(expected, error.Kind);
        Assert.False(
            string.IsNullOrWhiteSpace(error.Message),
            $"{construct}: the default dialect rejected it without saying why");
    }

    [Theory]
    [MemberData(nameof(LenientOnly))]
    public async Task Lenient_accepts_all_of_them(string construct, string sql, PlanErrorKind expected)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        _ = expected;

        var plan = await PlanAsync(sql, SqlConformance.Lenient);

        Assert.NotEqual(0UL, plan.PlanDigest);
        Assert.True(plan.Root is not null, construct);
    }

    [Theory]
    [MemberData(nameof(LenientOnly))]
    public async Task Babel_accepts_all_of_them(string construct, string sql, PlanErrorKind expected)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        _ = expected;

        var plan = await PlanAsync(sql, SqlConformance.Babel);

        Assert.NotEqual(0UL, plan.PlanDigest);
        Assert.True(plan.Root is not null, construct);
    }

    /// <summary>
    /// The other direction. The construct is a <c>SELECT</c> with no <c>FROM</c>, which Calcite gates
    /// on <c>isFromRequired</c>: the default dialect allows it — corpus query 23 is exactly that —
    /// and SQL:2003 requires the <c>FROM</c>. It is the validator that says so, not the parser.
    /// </summary>
    [Fact]
    public async Task Strict_2003_rejects_a_select_with_no_from_that_the_default_accepts()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var plan = await PlanAsync("SELECT 1 AS n", SqlConformance.Default);
        Assert.Single(plan.OutputType.Fields);

        var error = await Assert.ThrowsAsync<PlanningException>(
            () => PlanAsync("SELECT 1 AS n", SqlConformance.Strict2003).AsTask());

        Assert.Equal(PlanErrorKind.Validation, error.Kind);
    }

    /// <summary>An unset conformance is the default one, not whatever the planner was built with.</summary>
    [Fact]
    public async Task An_unset_conformance_behaves_as_default()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var planner = sidecar.CreatePlanner();
        await planner.RegisterCatalogAsync(Fixture.Catalog);

        var error = await Assert.ThrowsAsync<PlanningException>(
            () => planner.PlanAsync(new PlanRequest
            {
                Sql = "SELECT symbol FROM bars WHERE symbol != 'BTCUSDT'",
                ContextId = Fixture.Catalog.ContextId,
                CatalogEpoch = Fixture.Catalog.Epoch,
                // No Options at all: the wire enum's zero value means DEFAULT.
            }).AsTask());

        Assert.Equal(PlanErrorKind.Parse, error.Kind);
    }

    /// <summary>The dialect is a per-statement option, not part of the planner's identity (D34).</summary>
    [Fact]
    public async Task The_planner_config_hash_does_not_depend_on_the_request_dialect()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var planner = sidecar.CreatePlanner();
        var before = (await planner.GetInfoAsync()).PlannerConfigHash;

        await planner.RegisterCatalogAsync(Fixture.Catalog);
        await PlanAsync("SELECT symbol FROM bars WHERE symbol != 'BTCUSDT'", SqlConformance.Babel);

        Assert.Equal(before, (await planner.GetInfoAsync()).PlannerConfigHash);
    }

    /// <summary>
    /// A statement means different things in different dialects, so a recorded plan filed under one
    /// must not be served for another (D34). No sidecar needed: this is the key, not the plan.
    /// </summary>
    [Fact]
    public void A_recorded_plan_key_separates_the_dialects()
    {
        const string Sql = "SELECT symbol FROM bars";
        var keys = Levels
            .Select(c => RecordedPlanner.KeyFor(Sql, PushdownLevel.Full, c, [], "corpus"))
            .ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// A key spells a level and a library as 0.3's enums did, so a directory recorded before names
    /// went on the wire keeps its keys (D318): these are 0.3's keys for the corpus's own headers.
    /// </summary>
    [Fact]
    public void A_recorded_plan_key_is_the_one_0_3_wrote()
    {
        Assert.Equal(
            "73b9f0135fd904cdcaf8210bbc479f02",
            RecordedPlanner.KeyFor("SELECT 1", PushdownLevel.Full, SqlConformance.Lenient, [Chalk.Catalog.SqlLibrary.Postgresql, Chalk.Catalog.SqlLibrary.BigQuery], "corpus"));
    }

    /// <summary>A level the sidecar does not have is refused at prepare, naming the option and the levels it does (D318).</summary>
    [Fact]
    public async Task An_unknown_level_is_refused_at_prepare()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
        });

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => engine.PrepareAsync(
                "SELECT symbol FROM bars",
                new PrepareOptions { Conformance = SqlConformance.Named("POSTGRES_16") }).AsTask());

        Assert.Contains("PrepareOptions.Conformance 'POSTGRES_16'", error.Message, StringComparison.Ordinal);
        Assert.Contains("LENIENT", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The sidecar is the authority: a request that reaches it with an unknown name is refused there too.</summary>
    [Fact]
    public async Task The_sidecar_refuses_an_unknown_level_itself()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => PlanAsync("SELECT 1 AS n", SqlConformance.Named("POSTGRES_16")).AsTask());

        Assert.Contains("'POSTGRES_16' is not a conformance level this planner knows", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Every level 0.4 has a member for.</summary>
    private static readonly SqlConformance[] Levels =
    [
        SqlConformance.Default, SqlConformance.Lenient, SqlConformance.Babel, SqlConformance.Strict92,
        SqlConformance.Strict99, SqlConformance.Pragmatic99, SqlConformance.Strict2003,
        SqlConformance.Pragmatic2003, SqlConformance.MySql5, SqlConformance.Oracle10, SqlConformance.Oracle12,
        SqlConformance.SqlServer2008, SqlConformance.Presto, SqlConformance.BigQuery,
    ];

    /// <summary>An engine-level statement carries its dialect too, not only a raw plan request.</summary>
    [Fact]
    public async Task Prepare_options_carry_the_dialect_through_the_engine()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
        });

        const string Sql = "SELECT symbol, ts FROM bars WHERE symbol != 'BTCUSDT' ORDER BY ts, symbol";

        await Assert.ThrowsAsync<PlanningException>(
            () => engine.PrepareAsync(Sql).AsTask());

        var prepared = await engine.PrepareAsync(
            Sql, new PrepareOptions { Conformance = SqlConformance.Lenient });

        Assert.Equal(["symbol", "ts"], prepared.OutputSchema.FieldsList.Select(f => f.Name));
    }

    private async ValueTask<Chalk.Ir.Plan> PlanAsync(string sql, SqlConformance conformance)
    {
        await using var planner = sidecar.CreatePlanner();
        await planner.RegisterCatalogAsync(Fixture.Catalog);
        var result = await planner.PlanAsync(new PlanRequest
        {
            Sql = sql,
            ContextId = Fixture.Catalog.ContextId,
            CatalogEpoch = Fixture.Catalog.Epoch,
            Options = new Chalk.Client.PlannerOptions { Conformance = conformance },
        });
        return result.Plan;
    }
}
