using Chalk.TestKit;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// What a path takes from its parent: the parent's value of the kind, as a marking, for every role
/// the table it starts from admits — and not the parent's visibility (F208).
/// </summary>
/// <remarks>
/// The shared fixture's <c>orders</c> names the seven roles it admits and leaves the keeper out
/// (F100); every other table admits every role. So the keeper is nowhere in the orders' own
/// predicate, and is in the line's path to its order, which the line admits it to: a keeper in an
/// organisation reads that organisation's lines and none of its orders.
/// </remarks>
public sealed class PathAdmissionTests
{
    private static readonly TenancyEntitlements Compiled = TenancyPolicyFixture.Shared.Entitlements;

    [Fact]
    public void A_path_admits_the_roles_its_table_admits_whatever_the_parents_rule()
    {
        var lines = Compiled.For(TenancyPolicyFixture.SourceName, "order_items")!;
        var org = Assert.Single(lines.Inherited, path => path.Kind == "org");

        Assert.Contains("org_id IN (@ctx.org_keeper)", org.EndpointPredicate, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "keeper",
            Compiled.For(TenancyPolicyFixture.SourceName, "orders")!.RowPredicate,
            StringComparison.Ordinal);
    }
}
