using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The oracle (<c>docs/design/16-entitlements.md</c> §3.12, D207): what this principal's policy
/// resolved to, without executing anything and without the caller needing a plan.
/// </summary>
/// <remarks>
/// It is what a host reads when a row is missing and nothing in the result says why, and it is what
/// the conformance run compares between two executors. Built by the pass that enforces the policy,
/// from the same folded expressions, so it cannot drift from what runs.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class ExplainEntitlementsTests(SharedSidecar sidecar)
{
    /// <summary>
    /// §8's query 16 as <c>u1</c>, a manager in one organization and an agent in the other: the row
    /// predicate folded to the two organisations they hold a grant in, and <c>first_name</c> is
    /// decided per row, so the explanation shows the folded conditions rather than a name.
    /// </summary>
    [Fact]
    public async Task A_mixed_principal_is_told_the_conditions_that_decide_each_column()
    {
        await using var engine = await EngineAsync();

        var explained = await engine.WithEntitlements().ExplainAsync(
            "SELECT id, first_name, national_id, postcode FROM members", TenancyFixture.U1);

        var members = Assert.Single(explained.Tables, t => t.Table == "members");
        Assert.Equal("main", members.Schema);
        Assert.Contains("$1", members.RowPredicate, StringComparison.Ordinal);

        // A local table has no source to push into, so the whole predicate is the residual.
        Assert.False(members.RowPredicatePushed);
        Assert.NotEmpty(members.Residual);

        var id = Assert.Single(members.Columns, c => c.Column == "id");
        Assert.Equal("FULL", id.Disclosure);

        var firstName = Assert.Single(members.Columns, c => c.Column == "first_name");
        Assert.Contains("WHEN", firstName.Disclosure, StringComparison.Ordinal);
        Assert.Contains("THEN FULL", firstName.Disclosure, StringComparison.Ordinal);
        Assert.Contains("THEN MASKED", firstName.Disclosure, StringComparison.Ordinal);
        Assert.Contains("SUBSTRING", firstName.Mask, StringComparison.Ordinal);

        var nationalId = Assert.Single(members.Columns, c => c.Column == "national_id");
        Assert.Equal("REDACTED", nationalId.Disclosure);
    }

    /// <summary>A principal the fold settled is told a name and not a condition.</summary>
    [Fact]
    public async Task A_single_tenancy_principal_is_told_a_constant()
    {
        await using var engine = await EngineAsync();

        var explained = await engine.WithEntitlements().ExplainAsync(
            "SELECT id, first_name FROM members", TenancyFixture.U2);

        var members = Assert.Single(explained.Tables);
        Assert.Equal("MASKED", Assert.Single(members.Columns, c => c.Column == "first_name").Disclosure);
        Assert.Equal("FULL", Assert.Single(members.Columns, c => c.Column == "id").Disclosure);
    }

    /// <summary>And the floor a population-only column takes, after the host's default is applied.</summary>
    [Fact]
    public async Task A_population_only_column_is_told_its_floor()
    {
        await using var engine = await EngineAsync();

        var explained = await engine.WithEntitlements().ExplainAsync(
            "SELECT AVG(amount) FROM orders", TenancyFixture.U6);

        var amount = Assert.Single(
            Assert.Single(explained.Tables, t => t.Table == "orders").Columns,
            c => c.Column == "amount");
        Assert.Equal(TenancyFixture.MinGroupSize, amount.MinGroupSize);
        Assert.Contains("AGGREGATE", amount.Disclosure, StringComparison.Ordinal);
    }

    /// <summary>A catalog without entitlements explains nothing, and costs nothing to ask.</summary>
    [Fact]
    public async Task An_unentitled_catalog_explains_nothing()
    {
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyFixture.Unentitled.Source],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });

        Assert.Empty((await engine.WithEntitlements().ExplainAsync("SELECT id FROM members")).Tables);
    }

    private async Task<ChalkEngine> EngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyFixture.Shared.Source],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });
}
