using Chalk.Client;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A descriptor's SQL is type-checked when the catalog is registered
/// (<c>docs/design/16-entitlements.md</c> §1, §3.2; F42).
/// </summary>
/// <remarks>
/// The battery's cases 182 and 183 are the two the design named and they run there; this holds the
/// message itself, because the whole point of moving the check to registration is that a host is
/// told which column of which table it got wrong, and a substring assertion inside a 211-case
/// battery does not show what it actually reads like.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class EntitlementRegistrationTests(SharedSidecar sidecar)
{
    [Fact]
    public async Task A_rule_condition_that_is_not_boolean_is_refused_at_registration()
    {
        var refusal = await RefusalAsync(PolicyEntitlements.BadWhenNotBoolean);

        Assert.Equal(Chalk.Client.Rpc.PlanErrorKind.InvalidCatalog, refusal.Kind);
        Assert.Equal(
            "Invalid catalog: Invalid catalog at schemas[0] (main).tables[1] (members)"
            + ".entitlement.columns[0] (first_name).rules[0].when: on main.members, the rule "
            + "condition is not boolean but STRING (docs/design/16-entitlements.md §1). "
            + "The text is: first_name",
            refusal.Message);
    }

    [Fact]
    public async Task A_mask_of_the_wrong_type_is_refused_at_registration()
    {
        var refusal = await RefusalAsync(PolicyEntitlements.BadRuleMaskType);

        Assert.Equal(Chalk.Client.Rpc.PlanErrorKind.InvalidCatalog, refusal.Kind);
        Assert.Equal(
            "Invalid catalog: Invalid catalog at schemas[0] (main).tables[1] (members)"
            + ".entitlement.columns[0] (dob).rules[0].mask: on main.members, mask type STRING does "
            + "not match column type DATE (docs/design/16-entitlements.md §1: a mask and a "
            + "placeholder have the column's type, so every principal gets the same row shape). "
            + "The text is: '****'",
            refusal.Message);
    }

    /// <summary>
    /// A mask that reads another <b>protected</b> column, refused naming both (D220): the sanitiser
    /// is evaluated over the raw row, so such a mask would put <c>last_name</c>'s raw value inside
    /// the disclosed <c>first_name</c> of a principal entitled to neither.
    /// </summary>
    [Fact]
    public async Task A_mask_that_reads_another_protected_column_is_refused_at_registration()
    {
        var refusal = await RefusalAsync(PolicyEntitlements.BadMaskReadsProtected);

        Assert.Equal(Chalk.Client.Rpc.PlanErrorKind.InvalidCatalog, refusal.Kind);
        Assert.Equal(
            "Invalid catalog: Invalid catalog at schemas[0] (main).tables[1] (members)"
            + ".entitlement.columns[0] (first_name).rules[0].mask: on main.members, mask for "
            + "'first_name' reads 'last_name', which is itself a protected column. A mask is "
            + "evaluated over the raw row, so this one would disclose 'last_name' inside "
            + "'first_name' to a principal entitled to neither "
            + "(docs/design/16-entitlements.md §1, D220). A mask may read its own column, an "
            + "unprotected column and the context; a rule condition may read anything. The text "
            + "is: SUBSTRING(last_name FROM 1 FOR 1)",
            refusal.Message);
    }

    /// <summary>
    /// A descriptor whose every expression is well typed registers, which is the other half of the
    /// claim: the check refuses what §1 names and nothing else.
    /// </summary>
    [Fact]
    public async Task The_battery_s_own_descriptors_register()
    {
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = PolicyFixture.ContextId,
            Sources = [PolicyFixture.Create(PolicyCatalog.Default).Source],
            Planner = sidecar.CreatePlanner(),
            Functions = PolicyFixture.RegisterFunctions,
        });

        Assert.NotNull(engine);
    }

    private async Task<PlanningException> RefusalAsync(string entitlement)
    {
        var catalog = PolicyCatalog.Default with { Members = entitlement };
        return await Assert.ThrowsAsync<PlanningException>(async () =>
        {
            await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = PolicyFixture.ContextId,
                Sources = [PolicyFixture.Create(catalog).Source],
                Planner = sidecar.CreatePlanner(),
                Functions = PolicyFixture.RegisterFunctions,
            });
        });
    }
}
