using Chalk.Catalog;
using Chalk.TestKit;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// A <b>conjoined</b> grant (D266) where the tenancy is reached along a declared path (D265):
/// what travels to the endpoint, and what a policy may not ask for.
/// </summary>
/// <remarks>
/// ADR 0049 §9 left this to clause (h), because a conjoined term along a path needs a fixture that
/// declares a path at all. §8's is that fixture: an order line inherits its organisation from its
/// order, and an organisation grant may be confined to a region — so the conjoined term is written
/// over the <em>endpoint's</em> own row and evaluated there, which is the one place both columns
/// exist together.
/// </remarks>
public sealed class ConfinedAlongAPathTests
{
    private static TableEntitlementDescriptor OrderItems =>
        TenancyPolicyFixture.Shared.Entitlements.For(TenancyPolicyFixture.SourceName, "order_items")!;

    /// <summary>
    /// The conjoined term travels: the endpoint predicate of the line's <c>Inherited("org")</c> path
    /// carries the auditor's <c>(org_id, region_id)</c> tuple beside the unconfined membership, over
    /// the order's own columns. Both columns are the endpoint's, which is what makes the conjunction
    /// answerable there at all (design 40 §3, D266).
    /// </summary>
    [Fact]
    public void The_conjoined_term_travels_along_an_inherited_path_to_its_endpoint()
    {
        var org = OrderItems.Inherited.Single(i => i.Kind == "org");

        Assert.Contains(
            "(org_id, region_id) IN (@ctx.org_auditor_within_region)",
            org.EndpointPredicate,
            StringComparison.Ordinal);
        Assert.Contains(
            "org_id IN (@ctx.org_auditor)", org.EndpointPredicate, StringComparison.Ordinal);
        Assert.Equal("orders", org.EndpointTable);
    }

    /// <summary>
    /// And the subject's triple with it, on the line's <c>member</c> path: a grant confined along an
    /// organisation <em>and</em> a region is one membership over a tuple of arity three, written
    /// over the same endpoint row.
    /// </summary>
    [Fact]
    public void The_subjects_triple_travels_along_the_member_path()
    {
        var member = OrderItems.Inherited.Single(i => i.Kind == "member");

        Assert.Contains(
            "(member_id, org_id, region_id) IN (@ctx.member_self_within_org_region)",
            member.EndpointPredicate,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The vendor's path carries no confining term at all, and that is the decision rather than an
    /// omission: <c>vendors</c> and <c>items</c> hold no region, so there is no row on which both
    /// columns exist, and a confinement of that kind has nothing to be answered against.
    /// </summary>
    [Fact]
    public void The_vendor_path_carries_no_confining_term()
    {
        var vendor = OrderItems.Inherited.Single(i => i.Kind == "vendor");

        Assert.DoesNotContain("region", vendor.EndpointPredicate, StringComparison.Ordinal);
        Assert.Contains(
            "vendor_id IN (@ctx.vendor_vendor)", vendor.EndpointPredicate, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>F80.</b> And the unconfined vendor grant, which this fixture does answer: the refusal the
    /// confined one now meets (<see cref="ConfinementTests"/>) is about the confinement and not
    /// about the vendor dimension, whose own membership binds here as it always did.
    /// </summary>
    [Fact]
    public void An_unconfined_vendor_grant_binds_the_vendor_list()
    {
        var unconfined = TenancyPolicyFixture.Shared.Entitlements.Bind(
            TenancyPolicyFixture.Principal(
                22,
                Grant.ForTenancy(TenancyPolicyFixture.Vendor, 1, TenancyPolicyFixture.VendorRole)));

        Assert.Equal([[1]], unconfined.Lists["vendor_vendor"].Rows);
    }
}
