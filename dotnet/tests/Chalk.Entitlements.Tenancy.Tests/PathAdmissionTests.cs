using Chalk.TestKit;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// What a path takes from its parent in the shared fixture (D358): the parent's visibility, so a
/// role the parent's rule leaves out has no term along the path.
/// </summary>
/// <remarks>
/// The shared fixture's <c>orders</c> names the seven roles it admits and leaves the keeper and the
/// picker out (F100); every other table admits every role. So the keeper is in neither the orders'
/// own predicate nor the line's path to its order, and is in the line's path to its item: a keeper
/// in an organisation reads none of that organisation's orders, and none of their lines either. The
/// picker, which the line's path to its order names exempt, is in that path, and nowhere in the
/// orders' predicate.
/// </remarks>
public sealed class PathAdmissionTests
{
    private static readonly TenancyEntitlements Compiled = TenancyPolicyFixture.Shared.Entitlements;

    [Fact]
    public void A_path_reaches_only_the_roles_its_parent_admits()
    {
        var lines = Compiled.For(TenancyPolicyFixture.SourceName, "order_items")!;
        var org = Assert.Single(lines.Inherited, path => path.Kind == "org");
        var vendor = Assert.Single(lines.Inherited, path => path.Kind == "vendor");

        Assert.Contains("org_id IN (@ctx.org_manager)", org.EndpointPredicate, StringComparison.Ordinal);
        Assert.DoesNotContain("keeper", org.EndpointPredicate, StringComparison.Ordinal);
        Assert.Contains("org_id IN (@ctx.org_picker)", org.EndpointPredicate, StringComparison.Ordinal);
        Assert.Contains("vendor_id IN (@ctx.vendor_keeper)", vendor.EndpointPredicate, StringComparison.Ordinal);
        var orders = Compiled.For(TenancyPolicyFixture.SourceName, "orders")!.RowPredicate;
        Assert.DoesNotContain("keeper", orders, StringComparison.Ordinal);
        Assert.DoesNotContain("picker", orders, StringComparison.Ordinal);

        var exemption = Assert.Single(Compiled.Exemptions);
        Assert.Equal(("order_items", "org", "orders"), (exemption.Table, exemption.Kind, exemption.Endpoint));
        Assert.Equal(["picker"], exemption.Roles);
    }
}
