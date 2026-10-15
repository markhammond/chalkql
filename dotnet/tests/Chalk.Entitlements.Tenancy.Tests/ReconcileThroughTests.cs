using Chalk.Entitlements.Tenancy;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// What the policy says a column of a table entitled <b>through a parent</b> discloses, predicted from
/// the grants alone (F98, ADR 0061 §6).
/// </summary>
/// <remarks>
/// A message has no tenancy of its own: its <c>content</c> is decided by the roles held in its
/// thread's organisation, on the parent's side, and read back through the same ordinal a path's
/// verdict is. So the rule F87 states for paths holds here word for word: the verdict is a constant
/// only where the parent is reached in one tenancy. In two, the ordinal arrives on the child as a
/// column, and the column is decided per row even where the rules answer the two tenancies alike —
/// which is what the plan reports for a manager, or an agent, in two organisations (ADR 0061 §6's
/// measurement), and what the fixture's two principals for it now hold the corpus to.
/// </remarks>
public sealed class ReconcileThroughTests
{
    private static readonly TenancyEntitlements Entitlements =
        TenancyPolicyFixture.Shared.Entitlements;

    /// <summary><c>messages(id, thread_id, content, first_viewed_at)</c>.</summary>
    private const int Columns = 4;

    private const string Content = "content";

    /// <summary>One organisation: the constant the parent's rules give there.</summary>
    [Fact]
    public void A_parent_reached_in_one_tenancy_gives_its_rules_level()
    {
        Assert.Equal(DisclosureOutcome.Full, Expected(InOrgs(TenancyPolicyFixture.Manager, 1)));
        Assert.Equal(DisclosureOutcome.Masked, Expected(InOrgs(TenancyPolicyFixture.Agent, 1)));
    }

    /// <summary>Two organisations in the one role: per row, though the rules answer them alike.</summary>
    [Fact]
    public void A_parent_reached_in_two_tenancies_is_per_row()
    {
        Assert.Equal(DisclosureOutcome.PerRow, Expected(InOrgs(TenancyPolicyFixture.Manager, 1, 2)));
        Assert.Equal(DisclosureOutcome.PerRow, Expected(InOrgs(TenancyPolicyFixture.Agent, 1, 2)));
    }

    /// <summary>A grant held everywhere makes the parent's routes decide nothing.</summary>
    [Fact]
    public void A_grant_held_everywhere_is_not_met_along_the_parent()
    {
        var global = TenancyPolicyFixture.Principal(4, Grant.Global(TenancyPolicyFixture.Manager));

        Assert.Equal(DisclosureOutcome.Full, Expected(global));
    }

    // ---------------------------------------------------------------- helpers

    private static TenancyPrincipal InOrgs(Role role, params int[] orgs) =>
        TenancyPolicyFixture.Principal(
            16, [.. orgs.Select(org => Grant.ForTenancy(TenancyPolicyFixture.Org, org, role))]);

    /// <summary>
    /// What the policy expects of <c>content</c>, read off the difference it reports against a read
    /// that claims every column in full — no difference being the claim itself.
    /// </summary>
    private static DisclosureOutcome Expected(TenancyPrincipal principal)
    {
        foreach (var difference in Entitlements.Reconcile(Plan(), principal))
        {
            if (string.Equals(difference.Column, Content, StringComparison.Ordinal))
            {
                return difference.Expected;
            }
        }

        return DisclosureOutcome.Full;
    }

    /// <summary>A plan of one read of <c>messages</c>, claiming every column in full.</summary>
    private static Plan Plan()
    {
        var read = new Read
        {
            Table = new TableRef { SourceId = "", Schema = "main", Table = "messages" },
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
