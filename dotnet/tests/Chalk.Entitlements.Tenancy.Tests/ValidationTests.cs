using Chalk.Catalog;
using Chalk.Ir;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// What the compiler refuses at registration (<c>docs/design/16-entitlements.md</c> §5).
/// </summary>
/// <remarks>
/// A policy is declarations about a schema, and the two can disagree in ways that would otherwise
/// surface as a silently narrower — or wider — disclosure at the first request. Every refusal here
/// names the declaration and what would have made it resolve.
/// </remarks>
public sealed class ValidationTests
{
    private static SchemaDescriptor Schema => TenancyFixture.Unentitled.Source.DescribeSchema();

    /// <summary>The name of the one source the fixture's tables live in (§1, D270).</summary>
    private const string SourceName = "main";

    private static CatalogValidationException Refused(TenancyPolicy policy) =>
        Assert.Throws<CatalogValidationException>(() => policy.Compile([Schema]));

    /// <summary>
    /// A policy over the fixture's schema. Every name a test speaks enters here, at its declaration
    /// on the policy, and every later mention is the handle it came back as (§1, D270) — so the only
    /// strings left are the ones a name enters at.
    /// </summary>
    private static TenancyPolicy Declare() => TenancyPolicy.Declare(new CatalogContext
    {
        ContextId = TenancyFixture.ContextId,
        Epoch = TenancyFixture.Epoch,
        Schemas = [Schema],
    });

    // ---- dimensions and paths ----

    [Fact]
    public void A_dimension_on_a_column_the_table_does_not_hold_is_refused()
    {
        var policy = Declare();
        policy.Role("manager");
        var org = policy.Tenancy("org");
        var orders = policy.Source(SourceName).Table("orders");
        orders.Tenancy(r => r.Direct(org, orders.Column("tenant_id")));

        var error = Refused(policy);

        Assert.Contains("'tenant_id' is not a column of 'orders'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_through_an_undeclared_association_is_refused()
    {
        var policy = Declare();
        policy.Role("manager");
        var org = policy.Tenancy("org");
        var source = policy.Source(SourceName);
        var members = source.Table("members");
        var notes = source.Table("notes");
        members.Tenancy(r => r.Direct(org, members.Column("org_id")));
        notes.Tenancy(r => r.Direct(org, notes.Column("member.org")));

        var error = Refused(policy);

        Assert.Contains("'member' names no declared foreign key of 'notes'", error.Message, StringComparison.Ordinal);
        Assert.Contains("resolves through foreign keys", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A path that resolves through the association but ends at a column this table does not carry:
    /// answering it would need the parent table in the predicate, which §2 does not permit.
    /// </summary>
    [Fact]
    public void A_path_ending_at_a_column_the_table_does_not_carry_is_refused()
    {
        var policy = Declare();
        policy.Role("manager");
        var postcode = policy.Tenancy("postcode");
        var source = policy.Source(SourceName);
        var members = source.Table("members");
        var orders = source.Table("orders");
        members.Tenancy(r => r.Direct(postcode, members.Column("postcode")));
        orders.Tenancy(r => r.Direct(postcode, orders.Column("member.postcode")));

        var error = Refused(policy);

        Assert.Contains("carries no column 'postcode'", error.Message, StringComparison.Ordinal);
        Assert.Contains("has to be on the row", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_that_ends_at_something_that_is_not_a_dimension_is_refused()
    {
        var policy = Declare();
        policy.Role("manager");
        var org = policy.Tenancy("org");
        var source = policy.Source(SourceName);
        var members = source.Table("members");
        var orders = source.Table("orders");
        members.Tenancy(r => r.Direct(org, members.Column("org_id")));
        orders.Tenancy(r => r.Direct(org, orders.Column("member.branch")));

        var error = Refused(policy);

        Assert.Contains("is not a tenancy dimension of 'members'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A foreign-key path that returns to a table it already visited. The fixture's <c>members</c>
    /// gets a self-reference for the occasion, which is the shape a real hierarchy has.
    /// </summary>
    [Fact]
    public void A_foreign_key_path_cycle_is_refused()
    {
        var schema = new SchemaDescriptor
        {
            SourceId = "mem",
            Name = "main",
            Kind = SourceKind.Local,
            Tables =
            [
                new TableDescriptor
                {
                    Name = "nodes",
                    RowCount = 0,
                    Columns =
                    [
                        new ColumnDescriptor { Name = "id", Type = ChalkType.Int32() },
                        new ColumnDescriptor { Name = "parent_id", Type = ChalkType.Int32() },
                        new ColumnDescriptor { Name = "org_id", Type = ChalkType.Int32() },
                    ],
                    ForeignKeys =
                    [
                        new ForeignKeyDescriptor
                        {
                            Name = "parent",
                            Columns = [1],
                            ParentTable = "nodes",
                            ParentColumns = [0],
                        },
                    ],
                },
            ],
        };

        var policy = TenancyPolicy.Declare(new CatalogContext
        {
            ContextId = "nodes",
            Epoch = 1,
            Schemas = [schema],
        });
        policy.Role("manager");
        var org = policy.Tenancy("org");
        var nodes = policy.Source(schema.Name).Table("nodes");
        nodes.Tenancy(r => r.Direct(org, nodes.Column("parent.parent.org")));

        var error = Assert.Throws<CatalogValidationException>(() => policy.Compile([schema]));

        Assert.Contains("may not cycle", error.Message, StringComparison.Ordinal);
    }

    // ---- subject dimensions ----

    [Fact]
    public void A_subject_dimension_whose_within_names_no_tenancy_dimension_is_refused()
    {
        var policy = Declare();
        policy.Role("manager");
        var org = policy.Tenancy("org");
        var member = policy.Subject("member", within: [policy.Tenancy("region")]);
        var orders = policy.Source(SourceName).Table("orders");
        orders.Tenancy(r => r
            .Direct(org, orders.Column("org_id"))
            .Direct(member, orders.Column("member_id")));

        var error = Refused(policy);

        Assert.Contains("confined within 'region'", error.Message, StringComparison.Ordinal);
    }

    // ---- realms, access rules, modes ----

    [Fact]
    public void A_realm_naming_a_column_the_table_does_not_hold_is_refused()
    {
        var policy = Declare();
        policy.Role("manager");
        var org = policy.Tenancy("org");
        var pii = policy.Realm("pii");
        var orders = policy.Source(SourceName).Table("orders");
        orders
            .Tenancy(r => r.Direct(org, orders.Column("org_id")))
            .Realm(pii, orders.Column("comment"));

        var error = Refused(policy);

        Assert.Contains("realm 'pii' names 'comment'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_access_rule_naming_an_undeclared_realm_is_refused()
    {
        var policy = Declare();
        var manager = policy.Role("manager");
        var org = policy.Tenancy("org");
        var pii = policy.Realm("pii");
        var orders = policy.Source(SourceName).Table("orders");
        orders
            .Tenancy(r => r.Direct(org, orders.Column("org_id")))
            .Access(new AccessRule { Roles = [manager], Realm = pii, Grants = Verdict.Full });

        var error = Refused(policy);

        Assert.Contains("realm 'pii', which the table does not declare", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_aggregate_only_rule_with_no_aggregates_is_refused()
    {
        var policy = Declare();
        var manager = policy.Role("manager");
        var org = policy.Tenancy("org");
        var orders = policy.Source(SourceName).Table("orders");
        orders
            .Tenancy(r => r.Direct(org, orders.Column("org_id")))
            .Access(new AccessRule
            {
                Roles = [manager],
                Column = orders.Column("amount"),
                Grants = Verdict.AggregateOnly,
            });

        var error = Refused(policy);

        Assert.Contains("lists no population aggregate", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rule speaks for roles, and a role this policy does not declare is a role no grant it binds
    /// can name — so the rule could never match and the host meant something else.
    /// </summary>
    /// <remarks>
    /// Since D270 a rule can only name a <c>Role</c>, so the way to write this mistake is to hand it
    /// a role <em>another</em> policy declared. That is the shape a host running two policies has,
    /// and the check is the one the access rules have too.
    /// </remarks>
    [Fact]
    public void A_visibility_rule_naming_an_undeclared_role_is_refused()
    {
        var policy = Declare();
        policy.Role("manager");
        var org = policy.Tenancy("org");
        var orders = policy.Source(SourceName).Table("orders");
        orders
            .Tenancy(r => r.Direct(org, orders.Column("org_id")))
            .Visible(new VisibilityRule { Roles = [Elsewhere.Role("reviewer")] });

        var error = Refused(policy);

        Assert.Contains("'reviewer' is not one of the roles", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_the_schema_does_not_hold_is_refused()
    {
        var policy = Declare();
        policy.Role("manager");
        policy.Source(SourceName).Table("audits").Unrestricted();

        var error = Refused(policy);

        Assert.Contains("the policy declares 'main.audits'", error.Message, StringComparison.Ordinal);
    }

    // ---- grants ----

    private static TenancyEntitlements Compiled => TenancyPolicyFixture.Shared.Entitlements;

    /// <summary>
    /// A policy of its own, for the names the fixture's policy never declared. A role, a scope or a
    /// kind exists only where some policy declared one (§1, D270), so a grant that names one this
    /// policy does not know is written with another policy's handles — which is the shape a host
    /// running two policies has, and the mistake the bind check is there to catch.
    /// </summary>
    private static TenancyPolicy Elsewhere { get; } = Declare();

    [Fact]
    public void A_grant_naming_an_undeclared_role_is_refused_at_bind()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => Compiled.Bind(TenancyPolicyFixture.Principal(
                1,
                Grant.ForTenancy(
                    TenancyPolicyFixture.Org, 1, Elsewhere.Role("reviewer")))));

        Assert.Contains("role 'reviewer'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_grant_naming_an_undeclared_scope_is_refused_at_bind()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => Compiled.Bind(TenancyPolicyFixture.Principal(
                1,
                Grant.ForTenancy(
                    TenancyPolicyFixture.Org,
                    1,
                    TenancyPolicyFixture.Manager,
                    scope: Elsewhere.Scope("overnight")))));

        Assert.Contains("scope 'overnight'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_grant_naming_an_undeclared_dimension_is_refused_at_bind()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => Compiled.Bind(TenancyPolicyFixture.Principal(
                1,
                Grant.ForTenancy(
                    Elsewhere.Tenancy("tenant"), 1, TenancyPolicyFixture.Manager))));

        Assert.Contains("dimension 'tenant'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A global grant needs the policy's permission (D205), and the default is to refuse: a tier
    /// that may see every tenancy is a decision a deployment makes once.
    /// </summary>
    [Fact]
    public void A_global_grant_is_refused_unless_the_policy_permits_it()
    {
        var policy = Declare();
        var manager = policy.Role("manager");
        var org = policy.Tenancy("org");
        var members = policy.Source(SourceName).Table("members");
        members.Tenancy(r => r.Direct(org, members.Column("org_id")));
        var guarded = policy.Compile([Schema]);

        var error = Assert.Throws<CatalogValidationException>(
            () => guarded.Bind(TenancyPolicyFixture.Principal(1, Grant.Global(manager))));

        Assert.Contains("AllowGlobalGrants is off", error.Message, StringComparison.Ordinal);

        // And with it on, the same principal binds — which is what u4 is.
        Assert.True((bool)Compiled.Bind(TenancyPolicyFixture.Principal(
            4, Grant.Global(TenancyPolicyFixture.Manager))).Scalars["global"]!);
    }

    /// <summary>
    /// A grant carrying a scope is bound only in that scope; one carrying none is bound in every
    /// scope. The scope never reaches a membership test, so a request is one plan per role set and
    /// not one per scope.
    /// </summary>
    [Fact]
    public void A_scoped_grant_binds_only_in_its_own_scope()
    {
        var principal = new TenancyPrincipal
        {
            User = 1,
            Scope = TenancyPolicyFixture.Handles.Scope("batch"),
            Grants =
            [
                Grant.ForTenancy(
                    TenancyPolicyFixture.Org,
                    1,
                    TenancyPolicyFixture.Manager,
                    scope: TenancyPolicyFixture.Handles.Scope("interactive")),
            ],
        };

        Assert.Empty(Compiled.Bind(principal).Lists["org_manager"].Rows);

        var interactive = new TenancyPrincipal
        {
            User = 1,
            Scope = TenancyPolicyFixture.Handles.Scope("interactive"),
            Grants = principal.Grants,
        };

        Assert.Single(Compiled.Bind(interactive).Lists["org_manager"].Rows);
    }

    /// <summary>
    /// A confined subject grant binds as a pair and an <c>Anywhere</c> one as a bare identifier, so
    /// the two never answer for each other.
    /// </summary>
    [Fact]
    public void A_confined_subject_grant_and_an_anywhere_one_bind_to_different_lists()
    {
        var confined = Compiled.Bind(TenancyPolicyFixture.Principal(
            3,
            Grant.ForSubject(
                TenancyPolicyFixture.Member, 3, TenancyPolicyFixture.Self, within: 2)));
        Assert.Single(confined.Lists["member_self_pairs"].Rows);
        Assert.Empty(confined.Lists["member_self_ids"].Rows);

        var anywhere = Compiled.Bind(TenancyPolicyFixture.Principal(
            3,
            Grant.ForSubject(
                TenancyPolicyFixture.Member,
                3,
                TenancyPolicyFixture.Self,
                within: Tenancy.Anywhere)));
        Assert.Empty(anywhere.Lists["member_self_pairs"].Rows);
        Assert.Single(anywhere.Lists["member_self_ids"].Rows);
    }

    /// <summary>
    /// <c>Bind(principal, fold:)</c> names the dimensions whose grants become literals; everything
    /// that tells one principal of a tenant from another stays open (§2.1, D232).
    /// </summary>
    [Fact]
    public void Folding_one_dimension_leaves_the_rest_open()
    {
        var principal = TenancyPolicyFixture.Principal(
            3,
            Grant.ForTenancy(TenancyPolicyFixture.Org, 1, TenancyPolicyFixture.Manager),
            Grant.ForSubject(
                TenancyPolicyFixture.Member, 3, TenancyPolicyFixture.Self, within: 1));

        var partial = Compiled.Bind(principal, fold: ["org"]);

        Assert.False(partial.IsShape("org_manager"));
        Assert.Single(partial.Lists["org_manager"].Rows);
        Assert.True(partial.IsShape("member_self_pairs"));
        Assert.Empty(partial.Lists["member_self_pairs"].Rows);
        Assert.True(partial.IsShape("user"));
        Assert.True(partial.IsShape("mask_key"));
        Assert.True(partial.IsShape("global"));

        // The two unchanged ends of the same axis.
        Assert.Empty(Compiled.Bind(principal).ShapeNames);
        Assert.Equal(
            Compiled.Bind(principal).ShapeNames.Count,
            Compiled.Bind(principal, fold: null).ShapeNames.Count);
        Assert.True(Compiled.Bind(principal).Shape().IsShape("org_manager"));
    }

    /// <summary>A dimension this policy does not declare is named rather than silently ignored.</summary>
    [Fact]
    public void Folding_a_dimension_the_policy_does_not_declare_is_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => Compiled.Bind(TenancyPolicyFixture.Principal(1), fold: ["tenant"]));
        Assert.Contains("is not a dimension this policy declares", refusal.Message, StringComparison.Ordinal);
    }
}
