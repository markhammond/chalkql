using Chalk.Catalog;
using Chalk.TestKit;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// The two explicit grantees an access rule may name beside its roles (F75, D269 (c), design 43).
/// </summary>
/// <remarks>
/// A rule's role half is "the roles held in this row's tenancy", which is narrower than "everyone
/// who may see this row": a row admitted by the resource owner's fail-safe alone, or by a global
/// grant, is within no role's scope. §3.3's fold reads a column as a constant only where a rule's
/// condition <em>is</em> a conjunct the leaf's filter already carries, so a host that meant the
/// wider grant got a per-row verdict where the same grant written by hand got a constant — which is
/// what made the two layers' transcripts diverge. The host now spells it.
/// </remarks>
public sealed class ExplicitGranteeTests
{
    private static readonly SchemaDescriptor Schema =
        TenancyFixture.Unentitled.Source.DescribeSchema();

    /// <summary>
    /// The catalog these policies are declared over, which here is that one schema: a table is
    /// obtained from the source it belongs to and from nowhere else (D270 §1).
    /// </summary>
    private static readonly CatalogContext Catalog = new()
    {
        ContextId = TenancyFixture.ContextId,
        Epoch = TenancyFixture.Epoch,
        Schemas = [Schema],
    };

    private static CatalogValidationException Refused(TenancyPolicy policy) =>
        Assert.Throws<CatalogValidationException>(() => policy.Compile([Schema]));

    /// <summary>
    /// <c>orders</c> as the fixture declares it, with whatever access rule a test wants: two
    /// tenancy dimensions, the subject's, and the resource owner the fail-safe reads.
    /// </summary>
    /// <remarks>
    /// The rule is written against the policy and the table it will be declared on, because since
    /// D270 a rule names the handles that policy declared and no string produces one (§1). The two
    /// explicit grantees are the exception the same decision makes: they are handles no policy
    /// declares.
    /// </remarks>
    private static TenancyPolicy Orders(Func<TenancyPolicy, Table, AccessRule> access)
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var source = policy.Source(TenancyPolicyFixture.SourceName);
        policy.Role("manager");
        policy.Role("analyst");
        policy.AllowGlobalGrants();

        var org = policy.Tenancy("org");
        var member = policy.Subject("member", within: [org]);

        var members = source.Table("members");
        members.Tenancy(r => r.Direct(org, members.Column("org_id")));

        var orders = source.Table("orders");
        orders
            .Tenancy(r => r
                .Direct(org, orders.Column("member.org"))
                .Direct(member, orders.Column("member_id"))
                .ResourceOwner(orders.Column("created_by")))
            .Access(access(policy, orders));

        return policy;
    }

    private static TableEntitlementDescriptor Compile(Func<TenancyPolicy, Table, AccessRule> access) =>
        Orders(access).Compile([Schema]).For(TenancyPolicyFixture.SourceName, "orders")!;

    private static string ConditionOf(TableEntitlementDescriptor table, string column, int rule) =>
        table.Columns.Single(c => c.Column == Ordinal(column)).Rules[rule].When;

    private static int Ordinal(string column) =>
        column switch { "amount" => 3, "org_id" => 2, _ => throw new ArgumentException(column) };

    // ---------------------------------------------------------------- Roles.Owner

    /// <summary>
    /// The owner's disjunct stands <b>beside</b> the roles' scopes, in the order the list names
    /// them: any permutation of roles and the owner is a list.
    /// </summary>
    [Fact]
    public void The_owner_is_a_disjunct_beside_the_roles_scopes()
    {
        var compiled = Compile((p, orders) => new AccessRule
        {
            Roles = [p.Role("manager"), p.Role("analyst"), Roles.Owner],
            Column = orders.Column("amount"),
            Grants = Verdict.Full,
        });

        // The resource-owner fail-safe is rule 0 and is not one of the host's (D206, D215); the
        // host's rule is rule 1.
        Assert.Equal(
            "(org_id IN (@ctx.org_manager) "
            + "OR (member_id, org_id) IN (@ctx.member_manager_pairs) "
            + "OR member_id IN (@ctx.member_manager_ids) OR @ctx.global_manager) "
            + "OR (org_id IN (@ctx.org_analyst) "
            + "OR (member_id, org_id) IN (@ctx.member_analyst_pairs) "
            + "OR member_id IN (@ctx.member_analyst_ids) OR @ctx.global_analyst) "
            + "OR created_by = @ctx.user",
            ConditionOf(compiled, "amount", 1));
    }

    /// <summary>The owner alone is a rule about the owner alone, and nothing else.</summary>
    [Fact]
    public void The_owner_may_be_the_whole_of_a_rules_grantees()
    {
        var compiled = Compile((_, orders) => new AccessRule
        {
            Roles = [Roles.Owner],
            Column = orders.Column("amount"),
            Grants = Verdict.Full,
        });

        Assert.Equal("created_by = @ctx.user", ConditionOf(compiled, "amount", 1));
    }

    /// <summary>
    /// A table with no <c>ResourceOwner</c> has no column to compare the caller with, and the
    /// alternative — a condition that quietly grants nothing — is refused by name instead.
    /// </summary>
    [Fact]
    public void The_owner_on_a_table_that_declares_none_is_refused()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var members = policy.Source(TenancyPolicyFixture.SourceName).Table("members");
        members
            .Tenancy(r => r.Direct(policy.Tenancy("org"), members.Column("org_id")))
            .Access(new AccessRule
            {
                Roles = [policy.Role("manager"), Roles.Owner],
                Column = members.Column("first_name"),
                Grants = Verdict.Full,
            });

        var error = Refused(policy);

        Assert.Contains("names Roles.Owner", error.Message, StringComparison.Ordinal);
        Assert.Contains("declares no ResourceOwner", error.Message, StringComparison.Ordinal);
        Assert.Contains("ResourceOwner(column)", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- Roles.Visible

    /// <summary>
    /// The condition <b>is</b> the row predicate, character for character: that is what lets §3.3's
    /// <c>holds</c> read the column as a constant, because the fold's test is textual identity with
    /// a conjunct the leaf's filter already carries.
    /// </summary>
    [Fact]
    public void Roles_visible_compiles_to_the_row_predicate_textually()
    {
        var compiled = Compile((_, orders) => new AccessRule
        {
            Roles = [Roles.Visible],
            Column = orders.Column("amount"),
            Grants = Verdict.Full,
        });

        Assert.Equal(compiled.RowPredicate, ConditionOf(compiled, "amount", 1));
        // And that predicate really is every way in: the roles' scopes, the owner and the global
        // grant, which is what the shorthand means.
        Assert.Contains("org_id IN (@ctx.org_manager)", compiled.RowPredicate, StringComparison.Ordinal);
        Assert.Contains("created_by = @ctx.user", compiled.RowPredicate, StringComparison.Ordinal);
        Assert.Contains("@ctx.global", compiled.RowPredicate, StringComparison.Ordinal);
    }

    /// <summary>
    /// A grantee beside it says nothing more — the predicate already admits every role — and would
    /// only make the condition something other than the predicate, so it is refused rather than
    /// silently widened.
    /// </summary>
    [Fact]
    public void A_grantee_beside_roles_visible_is_refused()
    {
        var error = Refused(Orders((p, orders) => new AccessRule
        {
            Roles = [p.Role("manager"), Roles.Visible],
            Column = orders.Column("amount"),
            Grants = Verdict.Full,
        }));

        Assert.Contains("names Roles.Visible beside other grantees", error.Message, StringComparison.Ordinal);
        Assert.Contains("every way into the row", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A table that restricts no row has no predicate to write, and says so.</summary>
    [Fact]
    public void Roles_visible_on_an_unrestricted_table_is_refused()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var members = policy.Source(TenancyPolicyFixture.SourceName).Table("members");
        policy.Role("manager");
        members.Access(new AccessRule
        {
            Roles = [Roles.Visible],
            Column = members.Column("first_name"),
            Grants = Verdict.Full,
        });

        var error = Refused(policy);

        Assert.Contains("names Roles.Visible", error.Message, StringComparison.Ordinal);
        Assert.Contains("restricts no row", error.Message, StringComparison.Ordinal);
    }

    // There is no test here for the reservation the two markers used to need, because the
    // reservation is gone with the strings it checked (D270 §6 (b)): each marker is a `Role`
    // instance that only naming it produces, so a policy is free to declare a role called
    // "Roles.Owner" — it is an ordinary role and not the marker, equality being identity of the
    // declaration — and there is no spelling left to keep out of a directory's role names. The two
    // tests that held that promise, one on what `WithRoles` refused and one on the markers' own
    // spelling, went with the promise.
}
