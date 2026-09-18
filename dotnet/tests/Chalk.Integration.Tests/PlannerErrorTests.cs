using Chalk.Client;
using Chalk.Client.Rpc;
using Chalk.TestKit;
using PlanRequest = Chalk.Client.PlanRequest;

namespace Chalk.Integration.Tests;

/// <summary>
/// Error attribution (work plan §2): the negative corpus must map to the right
/// <see cref="PlanErrorKind"/>, with a SQL position where the planner knew one. A host that cannot
/// tell "your SQL is wrong" from "this milestone cannot do that" from "the planner is down" cannot
/// build anything sensible on top.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class PlannerErrorTests(SharedSidecar sidecar)
{
    private static readonly CorpusFixture Fixture = CorpusFixture.Shared;

    public static TheoryData<string> ErrorQueries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadErrors())
        {
            data.Add(query.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ErrorQueries))]
    public async Task The_negative_corpus_maps_to_the_expected_error_kind(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadErrors().Single(q => q.Name == name);
        var expected = Expected(query);

        var error = await Assert.ThrowsAsync<PlanningException>(() => PlanAsync(query).AsTask());

        Assert.Equal(expected, error.Kind);
        if (query.Expectations.Contains("position"))
        {
            Assert.NotNull(error.Position);
            Assert.True(error.Position!.Value.Line > 0, $"{name}: no line in '{error.Message}'");
            Assert.True(error.Position!.Value.Column > 0, $"{name}: no column in '{error.Message}'");
        }
    }

    [Fact]
    public async Task A_parse_error_points_at_the_first_bad_token()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var error = await Assert.ThrowsAsync<PlanningException>(
            () => PlanAsync("SELEC * FROM bars").AsTask());

        Assert.Equal(PlanErrorKind.Parse, error.Kind);
        Assert.Equal(1, error.Position!.Value.Line);
        Assert.Equal(1, error.Position!.Value.Column);
    }

    [Fact]
    public async Task An_unknown_column_names_the_column()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var error = await Assert.ThrowsAsync<PlanningException>(
            () => PlanAsync("SELECT nope FROM bars").AsTask());

        Assert.Equal(PlanErrorKind.Validation, error.Kind);
        Assert.Contains("nope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_context_is_distinguishable_from_bad_sql()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var planner = sidecar.CreatePlanner();

        var error = await Assert.ThrowsAsync<PlanningException>(
            () => planner.PlanAsync(new PlanRequest
            {
                Sql = "SELECT symbol FROM bars",
                ContextId = "never-registered",
                CatalogEpoch = 1,
            }).AsTask());

        // D271 (b): UNKNOWN_CONTEXT and EPOCH_MISMATCH retire from the request path — a catalog
        // version the planner does not hold is one error kind now, and it names the version so a
        // client can register it and retry. The message names the instance nothing is held for.
        Assert.Equal(PlanErrorKind.UnknownCatalogVersion, error.Kind);
        Assert.Contains("never-registered", error.Message, StringComparison.Ordinal);
        Assert.Null(error.Position);
    }

    /// <summary>
    /// A version of a known instance that the planner does not hold is the same kind of answer, and
    /// names the version rather than the instance (D271 (b)). This is what an eviction and a restart
    /// both look like; an engine recovers from it by registering and retrying once, so a host only
    /// ever sees it on a hand-built request like this one.
    /// </summary>
    [Fact]
    public async Task A_version_the_planner_does_not_hold_is_answered_by_name()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var planner = sidecar.CreatePlanner();
        await planner.RegisterCatalogAsync(Fixture.Catalog);

        var error = await Assert.ThrowsAsync<PlanningException>(
            () => planner.PlanAsync(new PlanRequest
            {
                Sql = "SELECT symbol FROM bars",
                ContextId = Fixture.Catalog.ContextId,
                CatalogEpoch = 99,
            }).AsTask());

        Assert.Equal(PlanErrorKind.UnknownCatalogVersion, error.Kind);
        Assert.Contains("99", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An unreachable planner is an operational failure, not a problem with the SQL.</summary>
    [Fact]
    public async Task An_unreachable_planner_is_reported_as_unavailable()
    {
        await using var planner = new GrpcQueryPlanner(new GrpcPlannerOptions
        {
            // Port 1 is reserved and nothing listens there.
            Address = new Uri("http://127.0.0.1:1"),
            Deadline = TimeSpan.FromSeconds(5),
        });

        var error = await Assert.ThrowsAsync<PlannerUnavailableException>(
            () => planner.GetInfoAsync().AsTask());

        Assert.Contains("127.0.0.1:1", error.Address, StringComparison.Ordinal);
    }

    private static PlanErrorKind Expected(CorpusQuery query)
    {
        var line = query.Expectations.FirstOrDefault(e => e.StartsWith("error=", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"{query.Name} has no '-- expect: error=' line");
        var name = line["error=".Length..].Trim();
        return name switch
        {
            "PARSE" => PlanErrorKind.Parse,
            "VALIDATION" => PlanErrorKind.Validation,
            "UNSUPPORTED" => PlanErrorKind.Unsupported,
            _ => throw new FormatException($"unknown expected error kind '{name}'"),
        };
    }

    private ValueTask<Chalk.Ir.Plan> PlanAsync(string sql) =>
        PlanAsync(sql, new Chalk.Client.PlannerOptions());

    /// <summary>
    /// A negative query is planned with the dialect and the libraries its headers ask for (D34,
    /// D60): a query that fails <em>because</em> it named a library is a different claim from one
    /// that fails because it did not.
    /// </summary>
    private ValueTask<Chalk.Ir.Plan> PlanAsync(CorpusQuery query) =>
        PlanAsync(
            query.Sql,
            new Chalk.Client.PlannerOptions
            {
                Conformance = query.Conformance,
                Libraries = query.Libraries,
            });

    private async ValueTask<Chalk.Ir.Plan> PlanAsync(string sql, Chalk.Client.PlannerOptions options)
    {
        await using var planner = sidecar.CreatePlanner();
        await planner.RegisterCatalogAsync(Fixture.Catalog);
        var result = await planner.PlanAsync(new PlanRequest
        {
            Sql = sql,
            ContextId = Fixture.Catalog.ContextId,
            CatalogEpoch = Fixture.Catalog.Epoch,
            Options = options,
        });
        return result.Plan;
    }
}
