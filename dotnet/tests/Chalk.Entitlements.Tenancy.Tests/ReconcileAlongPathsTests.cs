using Chalk.Entitlements.Tenancy;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// What the policy says a column of a table entitled along <b>declared paths</b> discloses, predicted
/// from the grants alone (F87, D269 (a), <c>docs/design/38-existential-visibility.md</c> §4).
/// </summary>
/// <remarks>
/// <para>
/// <c>order_items</c> holds no tenancy of its own. It inherits its organisation and its member
/// through its order and its vendor through its item (design 38 §8), so the verdict for
/// <c>unit_price</c> is decided at each route's endpoint, at the endpoint's own cardinality, and the
/// target reads back the least of the ordinals the routes project. The rule the prediction states is
/// the one the pass folds: that meet is a constant for the whole read only where a <em>single</em>
/// route reaches the table in a <em>single</em> tenancy — every row it reached then matched the same
/// rule at the endpoint. With a second route, or a second tenancy along one route, the ordinal
/// arrives on the target as a column: a row this principal can see may be one that route did not
/// reach, which is the LEFT JOIN's NULL, and the level under every rule is reachable beside the ones
/// the routes grant.
/// </para>
/// <para>
/// The expected labels are not this package's own: each is the one layer A recorded for
/// <c>unit_price</c> on <c>m7-tenancy</c> 39, the statement that joins the orders to the lines, in
/// <c>corpus/plans/m7-tenancy/39_orders_join_the_lines.txt</c>. That is what makes the theory a
/// statement about the pass rather than about the prediction: the numbers came from the other layer.
/// The integration corpus runs the same comparison over the planned statement for every principal;
/// this one runs it without a sidecar, on a plan of one read, so the rule can be read off one file.
/// </para>
/// </remarks>
public sealed class ReconcileAlongPathsTests
{
    private static readonly TenancyEntitlements Entitlements =
        TenancyPolicyFixture.Shared.Entitlements;

    /// <summary><c>order_items(id, order_id, item_id, quantity, unit_price)</c>.</summary>
    private const int Columns = 5;

    private const string UnitPrice = "unit_price";

    [Theory]
    // One route, one tenancy: the constant the route's rules give at its endpoint.
    [InlineData("u2", DisclosureOutcome.Full)]
    [InlineData("u3", DisclosureOutcome.Full)]
    [InlineData("u6", DisclosureOutcome.Full)]
    [InlineData("u8", DisclosureOutcome.Full)]
    [InlineData("u10-in-a-region", DisclosureOutcome.Full)]
    [InlineData("u11-subject-in-a-region", DisclosureOutcome.Full)]
    // The vendor's own route, alone: its rule withholds the price, and constantly.
    [InlineData("u14-a-vendor", DisclosureOutcome.Redacted)]
    // One route, two tenancies — two organisations, whether in one role or two.
    [InlineData("u1", DisclosureOutcome.PerRow)]
    [InlineData("u6-two-orgs", DisclosureOutcome.PerRow)]
    [InlineData("u9", DisclosureOutcome.PerRow)]
    // Two routes, which here also grant different levels.
    [InlineData("u15-a-vendor-and-an-org", DisclosureOutcome.PerRow)]
    // No route at all: no row is kept and every column takes the table's otherwise.
    [InlineData("u5", DisclosureOutcome.Redacted)]
    [InlineData("u7", DisclosureOutcome.Redacted)]
    // And a grant held everywhere, which makes the routes decide nothing.
    [InlineData("u4", DisclosureOutcome.Full)]
    public void The_meet_along_the_routes_is_what_layer_a_recorded(
        string principal, DisclosureOutcome expected) =>
        Assert.Equal(expected, Expected(UnitPrice, Principal(principal)));

    /// <summary>
    /// The two organisations are what makes it per-row, and one of them alone is not: the same
    /// grants minus one leave the same route reaching one tenancy, and the column is a constant
    /// again.
    /// </summary>
    /// <remarks>
    /// Stated as a pair because the theory above cannot: <c>u1</c> and <c>u2</c> differ in their
    /// roles as well as in how many organisations they hold, so neither of them on its own says
    /// which of the two differences the answer turned on. These two principals differ in nothing
    /// else.
    /// </remarks>
    [Fact]
    public void A_second_tenancy_along_one_route_is_what_makes_the_column_per_row()
    {
        var org = TenancyPolicyFixture.Org;
        var auditor = TenancyPolicyFixture.Auditor;

        Assert.Equal(
            DisclosureOutcome.Full,
            Expected(UnitPrice, TenancyPolicyFixture.Principal(6, Grant.ForTenancy(org, 1, auditor))));
        Assert.Equal(
            DisclosureOutcome.PerRow,
            Expected(
                UnitPrice,
                TenancyPolicyFixture.Principal(
                    6, Grant.ForTenancy(org, 1, auditor), Grant.ForTenancy(org, 2, auditor))));
    }

    /// <summary>
    /// A column of the same table that no rule names takes the table's default and is untouched by
    /// the routes: the meet decides what the <em>rules</em> come to, and a column with none of them
    /// is never consulted at all (D215).
    /// </summary>
    [Theory]
    [InlineData("u1")]
    [InlineData("u15-a-vendor-and-an-org")]
    public void A_column_no_rule_names_is_not_met_along_the_routes(string principal) =>
        Assert.Equal(DisclosureOutcome.Full, Expected("quantity", Principal(principal)));

    // ---------------------------------------------------------------- helpers

    private static TenancyPrincipal Principal(string name)
    {
        foreach (var (declared, principal) in TenancyPolicyFixture.Principals)
        {
            if (string.Equals(declared, name, StringComparison.Ordinal))
            {
                return principal;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(name), name, "no such principal");
    }

    /// <summary>
    /// What the policy expects of one column of <c>order_items</c>, read off the difference it
    /// reports against a read that claims every column in full — no difference being the claim
    /// itself.
    /// </summary>
    private static DisclosureOutcome Expected(string column, TenancyPrincipal principal)
    {
        foreach (var difference in Entitlements.Reconcile(Plan(), principal))
        {
            if (string.Equals(difference.Column, column, StringComparison.Ordinal))
            {
                return difference.Expected;
            }
        }

        return DisclosureOutcome.Full;
    }

    /// <summary>A plan of one read of <c>order_items</c>, claiming every column in full.</summary>
    private static Plan Plan()
    {
        var read = new Read
        {
            Table = new TableRef { SourceId = "", Schema = "main", Table = "order_items" },
        };

        for (var i = 0; i < Columns; i++)
        {
            read.Projection.Add((uint)i);
            read.Disclosures.Add(new ColumnDisclosure
            {
                Column = (uint)i,
                Outcome = DisclosureOutcome.Full,
            });
        }

        return new Plan { Root = new Rel { Read = read } };
    }
}
