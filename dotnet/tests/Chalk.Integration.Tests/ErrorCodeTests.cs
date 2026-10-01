using Chalk.Catalog;
using Chalk.Client;
using Chalk.Client.Rpc;
using Chalk.Entitlements;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The code a host reads on each path an error reaches it by (design 61): registering a catalog,
/// preparing a statement the planner refuses, and binding a value as the statement runs. Every
/// message ends with its code in brackets, and every error carries the rules it breaks as a list of
/// violations, one today.
/// </summary>
/// <remarks>
/// Refusals by the entitlements, the tenancy policy's own refusals, a missing context and a
/// cancelled prepare are asserted where those paths are tested: <c>StructuredRefusalTests</c>,
/// <c>CombinationTests</c>, <c>TenancyExecuteTimeTests</c> and <c>PlanningOptionsTests</c>.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class ErrorCodeTests(SharedSidecar sidecar)
{
    public sealed record Item(int Id, string Name);

    private static readonly Item[] Items = [new(1, "one"), new(2, "two")];

    [Fact]
    public async Task A_catalog_whose_sources_declare_two_zones_is_refused_as_InvalidZone()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var refused = await Assert.ThrowsAsync<CatalogValidationException>(async () =>
        {
            await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "codes-zones",
                Sources =
                [
                    new PocoSourceBuilder("eu", "eu").Zone("eu").AddTable("items", Items).Build(),
                    new PocoSourceBuilder("us", "us").Zone("us").AddTable("items", Items).Build(),
                ],
                Planner = sidecar.CreatePlanner(),
            });
        });

        Assert.Equal(ChalkErrorCodes.InvalidZone, refused.Code);
        Assert.Contains("one catalog is one zone", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith(" [InvalidZone]", refused.Message, StringComparison.Ordinal);

        // Found by the client, so it carries no planner's kind; its message is the exception's,
        // without the code.
        var violation = Assert.Single(refused.Violations);
        Assert.Equal(ChalkErrorCodes.InvalidZone, violation.Code);
        Assert.Null(violation.Kind);
        Assert.Equal(refused.Message, $"{violation.Message} [InvalidZone]");
    }

    [Fact]
    public async Task A_statement_that_does_not_parse_is_SqlSyntax_and_one_naming_no_column_SqlValidation()
    {
        await using var engine = await EngineAsync();

        var syntax = await Assert.ThrowsAsync<PlanningException>(
            async () => await engine.PrepareAsync("SELEC id FROM items"));
        Assert.Equal(PlanErrorKinds.Parse, syntax.Kind);
        Assert.Equal(ChalkErrorCodes.SqlSyntax, syntax.Code);
        Assert.EndsWith(" [SqlSyntax]", syntax.Message, StringComparison.Ordinal);
        var violation = Assert.Single(syntax.Violations);
        Assert.Equal(PlanErrorKinds.Parse, violation.Kind);
        Assert.Equal(ChalkErrorCodes.SqlSyntax, violation.Code);

        var validation = await Assert.ThrowsAsync<PlanningException>(
            async () => await engine.PrepareAsync("SELECT nothing FROM items"));
        Assert.Equal(PlanErrorKinds.Validation, validation.Kind);
        Assert.Equal(ChalkErrorCodes.SqlValidation, validation.Code);
        Assert.EndsWith(" [SqlValidation]", validation.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The code the planner raised an error with is the one the host reads, finer than the error's
    /// kind: a name the planner keeps for its own markers is <c>ReservedName</c>, where the kind
    /// alone would say no more than an invalid request.
    /// </summary>
    [Fact]
    public async Task The_planners_code_travels_with_its_error()
    {
        await using var engine = await EngineAsync();

        var refused = await Assert.ThrowsAsync<PlanningException>(
            async () => await engine.PrepareAsync("SELECT id AS \"$chalk$id\" FROM items"));

        Assert.Equal(PlanErrorKinds.InvalidRequest, refused.Kind);
        Assert.Equal(ChalkErrorCodes.ReservedName, refused.Code);
        Assert.EndsWith(" [ReservedName]", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_its_parameter_does_not_hold_is_refused_as_ParameterBinding_before_anything_runs()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.PrepareAsync("SELECT name FROM items WHERE id = @id");

        var refused = await Assert.ThrowsAsync<ParameterBindingException>(async () =>
        {
            await using var execution = await engine.ExecuteAsync(
                prepared, new Dictionary<string, object?> { ["id"] = 3.5 });
        });

        Assert.Equal(ChalkErrorCodes.ParameterBinding, refused.Code);
        Assert.StartsWith("Parameter @id is", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("3.5", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith(" [ParameterBinding]", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An error carries every rule the planner says it breaks, in its order, though the planner stops
    /// at the first today. The first is the code and kind the exception reports, a code or kind this
    /// client has no constant for is kept as it was sent, and a violation that names no rule is left
    /// out.
    /// </summary>
    [Fact]
    public void Every_violation_the_planner_sends_reaches_the_host_in_its_order()
    {
        var wire = new PlanError
        {
            Message = "first",
            Violations =
            {
                new Violation { Code = "SomethingNewer", Kind = "SomeNewerKind", Message = "first" },
                new Violation { Code = ChalkErrorCodes.SqlValidation, Kind = PlanErrorKinds.Validation, Message = "second" },
                new Violation { Kind = PlanErrorKinds.Validation, Message = "names no rule" },
            },
        };

        var error = new PlanningException(
            ErrorCodes.Violations(wire), wire.Message, position: null, planningState: null, innerException: null);

        Assert.Equal(new[] { "SomethingNewer", ChalkErrorCodes.SqlValidation }, error.Violations.Select(v => v.Code));
        Assert.Equal(new[] { "first", "second" }, error.Violations.Select(v => v.Message));
        Assert.Equal("SomethingNewer", error.Code);
        Assert.Equal("SomeNewerKind", error.Kind);
        Assert.EndsWith(" [SomethingNewer]", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal for each rule a statement breaks, each with its own code, reason and column, and
    /// the one table model between them.
    /// </summary>
    [Fact]
    public void Every_refusal_the_planner_sends_reaches_the_host_with_its_own_reason()
    {
        var wire = new PlanError
        {
            Message = "refused",
            Violations =
            {
                new Violation
                {
                    Code = ChalkErrorCodes.PopulationOnly,
                    Kind = PlanErrorKinds.Policy,
                    Message = "refused",
                    Table = "main.members",
                    Column = "national_id",
                    Use = "a projection to the result",
                    Permitted = { "COUNT" },
                },
                new Violation { Code = ChalkErrorCodes.Star, Kind = PlanErrorKinds.Policy, Message = "also refused" },
            },
        };

        var refusals = EntitlementRefusal.From(wire, StringLayouts.Utf8View);

        Assert.Equal(new[] { ChalkErrorCodes.PopulationOnly, ChalkErrorCodes.Star }, refusals.Select(r => r.Code));
        Assert.Equal(new[] { RefusalReason.PopulationOnly, RefusalReason.Star }, refusals.Select(r => r.Reason));
        Assert.Equal("national_id", refusals[0].Column);
        Assert.Equal(new[] { "COUNT" }, refusals[0].Permitted);
        Assert.Null(refusals[1].Column);
        Assert.Empty(refusals[1].Permitted);
    }

    private async Task<ChalkEngine> EngineAsync()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        return await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "codes",
            Sources = [new PocoSourceBuilder("mem").AddTable("items", Items, t => t.UniqueKey(i => i.Id)).Build()],
            Planner = sidecar.CreatePlanner(),
        });
    }
}
