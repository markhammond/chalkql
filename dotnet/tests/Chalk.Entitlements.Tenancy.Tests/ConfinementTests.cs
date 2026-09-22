using Chalk.Catalog;
using Chalk.TestKit;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// Conjoined confinement: a grant confined along several tenancy kinds at once
/// (<c>docs/design/40-conjoined-confinement.md</c>, D266).
/// </summary>
/// <remarks>
/// Grants OR with each other and restrictions OR, so a principal holding a region grant and an
/// organisation grant sees the union of the two. The conjunction lives <em>inside</em> one grant,
/// and what is asserted here is where that conjunction is refused: at the grant, where a kind is
/// named twice, and at the binding, where a kind confines something the policy never said it could.
/// </remarks>
public sealed class ConfinementTests
{
    private static TenancyEntitlements Compiled => TenancyPolicyFixture.Shared.Entitlements;

    private static readonly SchemaDescriptor Schema =
        TenancyFixture.Unentitled.Source.DescribeSchema();

    /// <summary>
    /// The catalog the little policies below are declared over, which here is that one schema: a
    /// table is obtained from the source it belongs to and from nowhere else (D270 §1).
    /// </summary>
    private static readonly CatalogContext Catalog = new()
    {
        ContextId = TenancyFixture.ContextId,
        Epoch = TenancyFixture.Epoch,
        Schemas = [Schema],
    };

    /// <summary>The kinds and roles §8's grants name, as the fixture declared them (D270 §1).</summary>
    private static Kind Org => TenancyPolicyFixture.Org;

    private static Kind Region => TenancyPolicyFixture.Region;

    private static SubjectKind Member => TenancyPolicyFixture.Member;

    private static Role Manager => TenancyPolicyFixture.Manager;

    private static Role Self => TenancyPolicyFixture.Self;

    /// <summary>Another host's policy over the same catalog, which declares nothing of its own.</summary>
    private static readonly TenancyPolicy Elsewhere = TenancyPolicy.Declare(Catalog);

    /// <summary>
    /// A kind the policy under test never declared. Since D270 a name enters once, at its
    /// declaration, and every later mention is the handle it came back as (§1) — so a kind a policy
    /// does not declare is necessarily one <em>another</em> policy declared, and that is the grant
    /// the refusals below are held to.
    /// </summary>
    private static Kind Undeclared(string name) => Elsewhere.Tenancy(name);

    private static CatalogValidationException Refused(Func<Grant> build) =>
        Assert.Throws<CatalogValidationException>(() => build());

    private static CatalogValidationException RefusedAtBind(params Grant[] grants) =>
        Assert.Throws<CatalogValidationException>(
            () => Compiled.Bind(TenancyPolicyFixture.Principal(3, grants)));

    // ---- the grant (D266 a) ----

    /// <summary>
    /// All the confinements of one grant must hold at once, so two of one kind would reach no row
    /// at all — which is a mistake in the host's code rather than a grant that grants nothing.
    /// </summary>
    [Fact]
    public void A_kind_named_twice_is_refused()
    {
        var error = Refused(() => Grant
            .ForTenancy(Org, 1, Manager)
            .Within(Region, 7)
            .Within(Region, 8));

        Assert.Contains("names the confining kind 'region' twice", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A grant names one tenancy of its kind already; two of one kind are two grants.</summary>
    [Fact]
    public void A_confinement_along_the_grants_own_kind_is_refused()
    {
        var error = Refused(() => Grant.ForTenancy(Org, 1, Manager).Within(Org, 2));

        Assert.Contains("which is its own kind", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A global grant reaches every tenancy by construction, so confining one is a contradiction.</summary>
    [Fact]
    public void A_confined_global_grant_is_refused()
    {
        var error = Refused(() => Grant.Global(Manager).Within(Org, 1));

        Assert.Contains("a global grant is confined along 'org'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Tenancy.Anywhere</c> is the explicit form and binds to the bare identifier list, never to
    /// the pair list (D266 §1).
    /// </summary>
    [Fact]
    public void A_subject_grant_that_says_anywhere_binds_the_bare_identifier_list()
    {
        var said = Compiled.Bind(TenancyPolicyFixture.Principal(
            3, Grant.ForSubject(Member, 3, Self, within: Tenancy.Anywhere)));

        Assert.Single(said.Lists["member_self_ids"].Rows);
        Assert.Empty(said.Lists["member_self_pairs"].Rows);
    }

    /// <summary>
    /// And saying <b>nothing</b> is not the same thing: it is refused at binding (§5, design 40 §1
    /// as amended 2026-09-16).
    /// </summary>
    /// <remarks>
    /// Reaching every tenancy is the widest thing a grant can do, and the vocabulary has a word for
    /// it. D266's run had let the omission mean "anywhere", which turned a forgotten <c>within:</c>
    /// into that widest grant; the word is required again, and the refusal names both spellings that
    /// satisfy it.
    /// </remarks>
    [Fact]
    public void A_subject_grant_with_no_confinement_and_no_anywhere_is_refused()
    {
        var error = RefusedAtBind(Grant.ForSubject(Member, 3, Self));

        Assert.Contains(
            "a subject grant must name the tenancy it is confined to, or Tenancy.Anywhere",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains("Tenancy.Anywhere)", error.Message, StringComparison.Ordinal);
        Assert.Contains(".Within(kind, id)", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <em>tenancy</em> grant needs no such word: it names its own container already, and nothing
    /// about it is implied by omission.
    /// </summary>
    [Fact]
    public void A_tenancy_grant_needs_no_anywhere()
    {
        var bound = Compiled.Bind(TenancyPolicyFixture.Principal(
            1, Grant.ForTenancy(Org, 1, Manager)));

        Assert.Single(bound.Lists["org_manager"].Rows);
    }

    /// <summary>
    /// <c>.Within(kind, id)</c> after an unconfined <c>ForSubject</c> is the confined form and
    /// satisfies the rule: what is refused is a grant that names no tenancy at all.
    /// </summary>
    [Fact]
    public void A_subject_grant_confined_by_within_alone_is_accepted()
    {
        var bound = Compiled.Bind(TenancyPolicyFixture.Principal(
            3, Grant.ForSubject(Member, 3, Self).Within(Org, 2)));

        Assert.Single(bound.Lists["member_self_pairs"].Rows);
    }

    /// <summary>
    /// The <c>within:</c> sugar is one confinement of the subject dimension's <b>first declared</b>
    /// confining kind, so it and the explicit spelling are the same thing said twice ways.
    /// </summary>
    [Fact]
    public void The_within_sugar_is_the_first_declared_confining_kind()
    {
        var sugar = Compiled.Bind(TenancyPolicyFixture.Principal(
            3, Grant.ForSubject(Member, 3, Self, within: 2)));
        var spelled = Compiled.Bind(TenancyPolicyFixture.Principal(
            3, Grant.ForSubject(Member, 3, Self).Within(Org, 2)));

        Assert.Equal(
            spelled.Lists["member_self_pairs"].Rows.Select(r => string.Join("|", r)),
            sugar.Lists["member_self_pairs"].Rows.Select(r => string.Join("|", r)));
    }

    // ---- the declaration (D266 b) ----

    /// <summary>
    /// A grant naming a confining kind the policy never declared could never match a group, so it
    /// would silently grant <em>more</em> than the host meant. The message names both.
    /// </summary>
    [Fact]
    public void A_subject_grant_confined_along_an_undeclared_kind_is_refused()
    {
        var error = RefusedAtBind(
            Grant.ForSubject(Member, 3, Self, within: 1).Within(Undeclared("desk"), 1));

        Assert.Contains(
            "the grant on 'member' is confined along 'desk'", error.Message, StringComparison.Ordinal);
        Assert.Contains("It declares 'org', 'region'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tenancy kind may be confined by any <em>other</em> declared tenancy kind (§2), so a policy
    /// declaring one declares nothing that may confine it and a confined grant on it is refused.
    /// </summary>
    [Fact]
    public void A_tenancy_grant_confined_where_the_policy_declares_no_other_kind_is_refused()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var members = policy.Source(TenancyPolicyFixture.SourceName).Table("members");
        var manager = policy.Role("manager");
        var org = policy.Tenancy("org");
        members.Tenancy(r => r.Direct(org, members.Column("org_id")));

        var one = policy.Compile([Schema]);

        var error = Assert.Throws<CatalogValidationException>(() => one.Bind(new TenancyPrincipal
        {
            User = 1,
            Grants = [Grant.ForTenancy(org, 1, manager).Within(Undeclared("region"), 1)],
        }));

        Assert.Contains("this policy does not declare", error.Message, StringComparison.Ordinal);
        Assert.Contains("It declares nothing", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>F80.</b> The policy may permit the pair and still have nowhere to answer it: a vendor
    /// grant confined within a region is permitted by §2 — a tenancy kind is confinable by any other
    /// declared tenancy kind — and no table of the fixture resolves a vendor and a region on one
    /// row, so no list of that group exists and the grant would bind, reach nothing and say nothing.
    /// </summary>
    /// <remarks>
    /// This is the other half of §3, asked at binding rather than at compilation: the three refusals
    /// above are about what the <em>policy</em> declares, and this one is about what a <em>table</em>
    /// resolves. It names what the dimension can be confined along instead, which for the vendor is
    /// nothing at all.
    /// </remarks>
    [Fact]
    public void A_grant_confined_along_kinds_no_table_resolves_together_is_refused()
    {
        var error = RefusedAtBind(
            Grant.ForTenancy(TenancyPolicyFixture.Vendor, 1, TenancyPolicyFixture.VendorRole)
                .Within(Region, 1));

        Assert.Contains(
            "the grant on 'vendor' is confined along 'region', and no table of this policy resolves "
            + "'vendor' and 'region' on one row",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "What 'vendor' can be confined along here for role 'vendor' is: nothing",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "hold the grant unconfined, or declare the dimension on a table that has both",
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And where the dimension <em>does</em> have confinements the policy can answer, the refusal
    /// names those instead of "nothing" — which is what turns the message into an instruction.
    /// </summary>
    /// <remarks>
    /// The policy below declares three tenancy kinds, and §2 makes each of them permitted to confine
    /// the organisation. Only the desk is on an order's row beside it; the region is on its own table
    /// alone. So an organisation grant confined along a desk binds, and the same grant confined along
    /// a region is refused with the desk named as what it can be confined along here.
    /// </remarks>
    [Fact]
    public void The_refusal_names_the_confinements_the_dimension_does_have()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var source = policy.Source(TenancyPolicyFixture.SourceName);
        var self = policy.Role("self");
        var org = policy.Tenancy("org");
        var desk = policy.Tenancy("desk");
        var region = policy.Tenancy("region");

        var members = source.Table("members");
        members.Tenancy(r => r.Direct(org, members.Column("org_id")));
        var orders = source.Table("orders");
        orders.Tenancy(r => r
            .Direct(org, orders.Column("member.org"))
            .Direct(desk, orders.Column("created_by")));
        var regions = source.Table("regions");
        regions.Tenancy(r => r.Direct(region, regions.Column("id")));

        var compiled = policy.Compile([Schema]);

        var bound = compiled.Bind(new TenancyPrincipal
        {
            User = 1,
            Grants = [Grant.ForTenancy(org, 1, self).Within(desk, 9)],
        });
        Assert.Equal([[1, 9]], bound.Lists["org_self_within_desk"].Rows.Select(r => r.ToArray()));

        var error = Assert.Throws<CatalogValidationException>(() => compiled.Bind(
            new TenancyPrincipal
            {
                User = 1,
                Grants = [Grant.ForTenancy(org, 1, self).Within(region, 1)],
            }));

        Assert.Contains(
            "no table of this policy resolves 'org' and 'region' on one row",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "What 'org' can be confined along here for role 'self' is: 'desk'",
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>F101.</b> The shape the tutorial ran into: a table that holds the confining kind
    /// <b>directly</b> and reaches the grant's own kind along an <c>Inherited</c> path. It is one
    /// perspective on two rows — the organisation on the endpoint's, the region on the target's —
    /// and D279 builds it as a <em>cross-row</em> group: one list, filled from the grants exactly as
    /// any other group's is, a projection of it that admits the endpoint rows the chain must reach,
    /// and a path predicate that decides them above the join.
    /// </summary>
    /// <remarks>
    /// Before this the binding was refused by name: a conjoined term was written over the endpoint's
    /// own row alone, and a kind the target held directly was on the wrong row for it. The tutorial's
    /// workaround was to declare both axes directly and denormalise the inherited key onto the target
    /// (ADR 0063 §3 b), which is the dead end D279 removes.
    /// </remarks>
    [Fact]
    public void A_confinement_across_an_inherited_dimension_binds_as_a_cross_row_group()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var source = policy.Source(TenancyPolicyFixture.SourceName);
        var auditor = policy.Role("auditor");
        var org = policy.Tenancy("org");
        var region = policy.Tenancy("region");

        // The endpoint: it resolves the organisation on its own row and holds no region.
        var members = source.Table("members");
        members.Tenancy(r => r.Direct(org, members.Column("org_id")));

        // The target: the region is on its own row, the organisation is a join away.
        var orders = source.Table("orders");
        orders.Tenancy(r => r
            .Inherited(org).Through(members)
            .Direct(region, orders.Column("region_id")));

        var compiled = policy.Compile([Schema]);

        // The unconfined grant is unaffected: it fills the bare list and the conjoined one stays
        // empty, which is what folds its term away.
        var unconfined = compiled.Bind(new TenancyPrincipal
        {
            User = 1,
            Grants = [Grant.ForTenancy(org, 1, auditor)],
        });
        Assert.Equal([[1]], unconfined.Lists["org_auditor"].Rows);
        Assert.Empty(unconfined.Lists["org_auditor_within_region"].Rows);
        Assert.Empty(unconfined.Lists["org_auditor_within_region_ids"].Rows);

        // And the conjoined grant now binds: the pair fills the cross-row list, and its projection
        // onto the organisation is what the chain admits before the path predicate decides.
        var confined = compiled.Bind(new TenancyPrincipal
        {
            User = 1,
            Grants = [Grant.ForTenancy(org, 1, auditor).Within(region, 2)],
        });
        Assert.Equal([[1, 2]], confined.Lists["org_auditor_within_region"].Rows);
        Assert.Equal([[1]], confined.Lists["org_auditor_within_region_ids"].Rows);
        Assert.Empty(confined.Lists["org_auditor"].Rows);

        var path = Assert.Single(compiled.For(orders)!.Inherited);

        // The admission, inside the chain, over the endpoint's own row: the projection stands where
        // the cross-row term cannot be written.
        Assert.Equal(
            "(org_id IN (@ctx.org_auditor_within_region_ids) OR org_id IN (@ctx.org_auditor) "
            + "OR @ctx.global_auditor)",
            path.EndpointPredicate);

        // And the decider, above the join, over both rows: the endpoint's column qualified, the
        // target's own plain.
        Assert.Equal(
            "((members.org_id, region_id) IN (@ctx.org_auditor_within_region) "
            + "OR members.org_id IN (@ctx.org_auditor) OR @ctx.global_auditor)",
            path.PathPredicate);
    }

    /// <summary>
    /// The cross-row group is the <em>target's</em> own dimension beside the endpoint's, and a kind
    /// one join further out is on neither row. A table entitled through a parent that holds the
    /// confining kind is that shape, and the refusal says so rather than denying the table exists.
    /// </summary>
    [Fact]
    public void A_confining_kind_on_a_through_parent_is_refused_naming_the_parent()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var source = policy.Source(TenancyPolicyFixture.SourceName);
        var keeper = policy.Role("keeper");
        var org = policy.Tenancy("org");
        var region = policy.Tenancy("region");

        var members = source.Table("members");
        members.Tenancy(r => r.Direct(org, members.Column("org_id")));

        var regions = source.Table("regions");
        regions.Tenancy(r => r.Direct(region, regions.Column("id")));

        // The target holds nothing of its own: the organisation is its parent's, and the region is
        // an inherited path away.
        var orders = source.Table("orders");
        orders.Tenancy(r => r
            .Through(orders.Column("member_id"))
            .Inherited(region).Through(regions));

        var error = Assert.Throws<CatalogValidationException>(() => policy.Compile([Schema]).Bind(
            new TenancyPrincipal
            {
                User = 1,
                Grants = [Grant.ForTenancy(region, 1, keeper).Within(org, 2)],
            }));

        Assert.Contains(
            "'orders' comes closest and is not close enough: it reaches 'region' along a path whose "
            + "endpoint is 'regions', and 'org' is held by 'members', the parent it derives "
            + "visibility through, rather than on its own row",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "Either 'org' is declared directly on 'orders', or the grant is held unconfined (F101)",
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And two kinds at the ends of two <em>different</em> paths are two endpoint rows, neither of
    /// which holds the other's kind: the decider reads the target's row and one endpoint's.
    /// </summary>
    [Fact]
    public void Two_kinds_along_different_paths_are_refused_naming_both_endpoints()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var source = policy.Source(TenancyPolicyFixture.SourceName);
        var keeper = policy.Role("keeper");
        var org = policy.Tenancy("org");
        var region = policy.Tenancy("region");

        var members = source.Table("members");
        members.Tenancy(r => r.Direct(org, members.Column("org_id")));

        var regions = source.Table("regions");
        regions.Tenancy(r => r.Direct(region, regions.Column("id")));

        var orders = source.Table("orders");
        orders.Tenancy(r => r
            .Inherited(org).Through(members)
            .Inherited(region).Through(regions));

        var error = Assert.Throws<CatalogValidationException>(() => policy.Compile([Schema]).Bind(
            new TenancyPrincipal
            {
                User = 1,
                Grants = [Grant.ForTenancy(org, 1, keeper).Within(region, 2)],
            }));

        Assert.Contains(
            "'orders' comes closest and is not close enough: it reaches 'org' along a path whose "
            + "endpoint is 'members' and 'region' along another whose endpoint is 'regions'",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "two endpoints are two rows neither of which holds the other's kind",
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every path a policy could declare before D279 is decided on the endpoint's own row alone, so
    /// it carries no path predicate at all — the field emits no bytes, the descriptor hashes as it
    /// did, and the recorded plans are byte-identical.
    /// </summary>
    [Fact]
    public void A_policy_that_needs_one_row_carries_no_path_predicate()
    {
        var paths = 0;
        foreach (var (_, descriptor) in Compiled.Tables)
        {
            foreach (var path in descriptor.Inherited)
            {
                paths++;
                Assert.Equal("", path.PathPredicate);
            }
        }

        Assert.True(paths > 0, "the shared fixture declares paths for this to be about");
    }

    /// <summary>
    /// And where the endpoint holds the confining kind on its own row, that sibling still wins: the
    /// group stays the endpoint's, written over one row, and no path predicate is emitted.
    /// </summary>
    [Fact]
    public void An_endpoints_own_sibling_wins_over_the_targets_column()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var source = policy.Source(TenancyPolicyFixture.SourceName);
        var keeper = policy.Role("keeper");
        var org = policy.Tenancy("org");
        var member = policy.Subject("member", within: [org]);

        // The endpoint resolves both halves of the conjunction on its own row.
        var members = source.Table("members");
        members.Tenancy(r => r
            .Direct(org, members.Column("org_id"))
            .Direct(member, members.Column("id")));

        var orders = source.Table("orders");
        orders.Tenancy(r => r
            .Inherited(member).Through(members)
            .Direct(org, orders.Column("member.org")));

        var compiled = policy.Compile([Schema]);
        var path = Assert.Single(compiled.For(orders)!.Inherited);

        // No cross-row group, so no decider above the join; the conjunction is one term over the
        // endpoint's own row, exactly as it was written before D279.
        Assert.Equal("", path.PathPredicate);
        Assert.Contains(
            $"IN (@ctx.member_{keeper.Name}_pairs)",
            path.EndpointPredicate,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_pairs_ids", path.EndpointPredicate, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the near miss is named only when there is one. The fixture's own vendor-within-region
    /// refusal (F80 above) is about two kinds no table brings together by any route, so the message
    /// stays the one ADR 0064 §1 wrote and gains nothing.
    /// </summary>
    [Fact]
    public void A_refusal_with_no_path_to_name_says_nothing_about_one()
    {
        var error = RefusedAtBind(
            Grant.ForTenancy(TenancyPolicyFixture.Vendor, 1, TenancyPolicyFixture.VendorRole)
                .Within(Region, 1));

        Assert.DoesNotContain("comes closest", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>F99.</b> The same silence reached by the other member of the key: a role the policy
    /// declares, named on a dimension no table admits it for, matches no list either — so the grant
    /// would bind and reach nothing. The owner's decision of 2026-09-17 is to refuse at binding,
    /// where the result is known to be terminal, and the message names the roles the dimension has.
    /// </summary>
    /// <remarks>
    /// A role is declared once and <em>admitted per table</em>, so the way to write this is a table
    /// whose visibility rules admit one role and a grant on that table's dimension in another. Both
    /// roles are ones the policy declares, so the first clause of the check — a role no policy
    /// declared, refused by name (<see cref="ValidationTests"/>) — has nothing to say about either;
    /// and the same role on the dimension that does carry it binds, so what is refused is the
    /// pairing rather than the role.
    /// </remarks>
    [Fact]
    public void A_grant_naming_a_role_no_list_of_its_dimension_carries_is_refused()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var source = policy.Source(TenancyPolicyFixture.SourceName);
        var manager = policy.Role("manager");
        var keeper = policy.Role("keeper");
        var org = policy.Tenancy("org");
        var region = policy.Tenancy("region");

        var members = source.Table("members");
        members.Tenancy(r => r.Direct(org, members.Column("org_id")));

        // The regions are the keeper's, and no other role is admitted to them.
        var regions = source.Table("regions");
        regions
            .Tenancy(r => r.Direct(region, regions.Column("id")))
            .Visible(new VisibilityRule { Roles = [keeper] });

        var compiled = policy.Compile([Schema]);

        var error = Assert.Throws<CatalogValidationException>(() => compiled.Bind(
            new TenancyPrincipal { User = 1, Grants = [Grant.ForTenancy(region, 1, manager)] }));

        Assert.Contains(
            "the grant on tenancy dimension 'region' names role 'manager', and no table that "
            + "resolves 'region' admits that role",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "The roles this policy holds a membership of 'region' for are 'keeper'",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "admit 'manager' on a table that resolves 'region'",
            error.Message,
            StringComparison.Ordinal);

        // The role the regions do admit binds, and so does the manager on the dimension that
        // carries it: what is terminal is the pairing.
        Assert.Equal(
            [[1]],
            compiled.Bind(new TenancyPrincipal
            {
                User = 1,
                Grants = [Grant.ForTenancy(region, 1, keeper)],
            }).Lists["region_keeper"].Rows.Select(r => r.ToArray()));
        Assert.Equal(
            [[1]],
            compiled.Bind(new TenancyPrincipal
            {
                User = 1,
                Grants = [Grant.ForTenancy(org, 1, manager)],
            }).Lists["org_manager"].Rows.Select(r => r.ToArray()));
    }

    /// <summary>The sugar and the explicit spelling of one kind are still that kind named twice.</summary>
    [Fact]
    public void The_sugar_and_the_same_kind_spelled_out_are_refused_together()
    {
        var error = RefusedAtBind(Grant.ForSubject(Member, 3, Self, within: 1).Within(Org, 1));

        Assert.Contains("names the confining kind 'org' twice", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Subject(kind, within: [..])</c> stands beside the single-kind form, and the single-kind
    /// form keeps meaning exactly what it did: one confining kind, the pair list of today.
    /// </summary>
    /// <remarks>
    /// The declarations are read off a compiled policy, because the kinds are frozen into the model
    /// the compiler reads when the policy compiles and not before (D270 §1).
    /// </remarks>
    [Fact]
    public void A_subject_kind_may_declare_several_confining_kinds()
    {
        var several = TenancyPolicy.Declare(Catalog);
        var members = several.Source(TenancyPolicyFixture.SourceName).Table("members");
        several.Role("self");
        var org = several.Tenancy("org");
        var region = several.Tenancy("region");
        var member = several.Subject("member", within: [org, region]);
        members.Tenancy(r => r
            .Direct(org, members.Column("org_id"))
            .Direct(member, members.Column("id")));
        several.Compile([Schema]);

        var kind = several.Kinds.Single(k => k.IsSubject);

        Assert.Equal(["org", "region"], kind.WithinKinds);
        Assert.Equal("org", kind.Within);

        var one = TenancyPolicy.Declare(Catalog);
        one.Role("self");
        one.Subject("member", within: [one.Tenancy("org")]);
        one.Compile([Schema]);

        Assert.Equal(["org"], one.Kinds.Single(k => k.IsSubject).WithinKinds);
    }

    // ---- resolution and compilation (D266 c, d) ----

    /// <summary>
    /// A second tenancy kind over the fixture's own schema: <c>orders.created_by</c> read as a desk,
    /// which <c>orders</c> holds and <c>members</c> does not. It is the shape of §3's own example —
    /// a table that resolves every kind, and a table that resolves all but one.
    /// </summary>
    /// <remarks>
    /// The handles come back with the entitlements, because a grant names the kind and the role the
    /// policy declared them as and no string produces either (D270 §1).
    /// </remarks>
    private static (TenancyEntitlements Entitlements, Kind Org, Kind Desk, SubjectKind Member, Role Self)
        TwoKinds()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var source = policy.Source(TenancyPolicyFixture.SourceName);
        var self = policy.Role("self");
        var org = policy.Tenancy("org");
        var desk = policy.Tenancy("desk");
        var member = policy.Subject("member", within: [org, desk]);

        var members = source.Table("members");
        members.Tenancy(r => r
            .Direct(org, members.Column("org_id"))
            .Direct(member, members.Column("id")));

        var orders = source.Table("orders");
        orders.Tenancy(r => r
            .Direct(org, orders.Column("member.org"))
            .Direct(member, orders.Column("member_id"))
            .Direct(desk, orders.Column("created_by")));

        return (policy.Compile([Schema]), org, desk, member, self);
    }

    /// <summary>
    /// One list per group, and the term is the tuple of arity <c>1 + n</c> over the very columns the
    /// row predicate admits the row by (D266 §4). The bare list and the pair list keep their names,
    /// so a context or a fixture that bound them still binds them.
    /// </summary>
    [Fact]
    public void A_group_of_two_confining_kinds_is_one_tuple_list_of_arity_three()
    {
        var orders = TwoKinds().Entitlements
            .For(TenancyPolicyFixture.SourceName, "orders")!.RowPredicate;

        Assert.Contains(
            "(member_id, org_id, created_by) IN (@ctx.member_self_within_org_desk)",
            orders,
            StringComparison.Ordinal);
        Assert.Contains("(member_id, org_id) IN (@ctx.member_self_pairs)", orders, StringComparison.Ordinal);
        Assert.Contains("member_id IN (@ctx.member_self_ids)", orders, StringComparison.Ordinal);
        Assert.Contains("(org_id, created_by) IN (@ctx.org_self_within_desk)", orders, StringComparison.Ordinal);
    }

    /// <summary>
    /// A confined grant applies to a table where its own kind <b>and every confining kind</b> resolve
    /// there, and contributes no term at all where one does not (D266 §3): the desk is on an order's
    /// row and not on a member's, so the grant reaches <c>orders</c> and never the member's own row.
    /// </summary>
    [Fact]
    public void A_grant_reaches_only_the_tables_where_every_confining_kind_resolves()
    {
        var compiled = TwoKinds().Entitlements;
        var members = compiled.For(TenancyPolicyFixture.SourceName, "members")!.RowPredicate;

        Assert.Contains(
            "member_self_within_org_desk",
            compiled.For(TenancyPolicyFixture.SourceName, "orders")!.RowPredicate,
            StringComparison.Ordinal);
        Assert.DoesNotContain("member_self_within_org_desk", members, StringComparison.Ordinal);
        Assert.DoesNotContain("desk", members, StringComparison.Ordinal);
    }

    /// <summary>
    /// The grant fills its own group's list and no other, so an unconfined grant, a grant confined
    /// along one kind and a grant confined along both never answer for each other.
    /// </summary>
    [Fact]
    public void A_grant_binds_into_the_list_of_its_own_group()
    {
        var two = TwoKinds();
        var context = two.Entitlements.Bind(new TenancyPrincipal
        {
            User = 1,
            Grants = [Grant.ForSubject(two.Member, 1, two.Self, within: 1).Within(two.Desk, 9)],
        });

        Assert.Equal(
            [[1, 1, 9]],
            context.Lists["member_self_within_org_desk"].Rows.Select(r => r.ToArray()));
        Assert.Empty(context.Lists["member_self_pairs"].Rows);
        Assert.Empty(context.Lists["member_self_ids"].Rows);
        Assert.Equal(
            ["subject", "org", "desk"],
            context.Lists["member_self_within_org_desk"].Columns);
    }

    /// <summary>
    /// The fixture's own conjoined shape, as the compiler writes it (D266 §6): <c>orders</c> holds
    /// the organisation, the member and the region, so the regional auditor's term is the pair over
    /// the two columns of the one row the grant admits it by.
    /// </summary>
    /// <remarks>
    /// The other groups of that role stand beside it — the bare organisation list the unconfined
    /// auditor fills, the pair list a subject confined to an organisation fills, and the triple a
    /// subject confined to both fills — and each is empty for a principal holding no grant of it,
    /// which folds its term away.
    /// </remarks>
    [Fact]
    public void The_fixtures_orders_predicate_carries_a_term_per_group()
    {
        var orders = TenancyPolicyFixture.Shared.Entitlements
            .For(TenancyPolicyFixture.SourceName, "orders")!.RowPredicate;

        Assert.Contains(
            "(org_id, region_id) IN (@ctx.org_auditor_within_region)",
            orders,
            StringComparison.Ordinal);
        Assert.Contains("org_id IN (@ctx.org_auditor)", orders, StringComparison.Ordinal);
        Assert.Contains(
            "(member_id, org_id, region_id) IN (@ctx.member_self_within_org_region)",
            orders,
            StringComparison.Ordinal);
        Assert.Contains(
            "(member_id, org_id) IN (@ctx.member_self_pairs)", orders, StringComparison.Ordinal);
        Assert.Contains("member_id IN (@ctx.member_self_ids)", orders, StringComparison.Ordinal);

        // And `members`, which resolves no region: not one of the region's groups reaches it, so a
        // grant confined along a region contributes no term there at all (D266 §3).
        var members = TenancyPolicyFixture.Shared.Entitlements
            .For(TenancyPolicyFixture.SourceName, "members")!.RowPredicate;
        Assert.DoesNotContain("region", members, StringComparison.Ordinal);
    }

    // ---- explain (D266 e) ----

    /// <summary>
    /// Explain names each group's confining kinds beside its list, and says how many of the
    /// principal's grants fell into it — which is the question a host asks when a conjoined grant
    /// reaches fewer rows than it expected (D266 §5).
    /// </summary>
    [Fact]
    public void Explain_names_each_groups_confining_kinds_beside_its_list()
    {
        var two = TwoKinds();
        var groups = two.Entitlements.Explain(new TenancyPrincipal
        {
            User = 1,
            Grants =
            [
                Grant.ForSubject(two.Member, 1, two.Self, within: 1).Within(two.Desk, 9),
                Grant.ForSubject(two.Member, 2, two.Self, within: Tenancy.Anywhere),
            ],
        });

        var conjoined = groups.Single(g => g.List == "member_self_within_org_desk");
        Assert.Equal(["org", "desk"], conjoined.Confining);
        Assert.Equal("member", conjoined.Kind);
        Assert.Equal("self", conjoined.Role);
        Assert.True(conjoined.IsSubject);
        Assert.Equal(1, conjoined.Grants);
        Assert.Contains("confined by org, desk", conjoined.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, groups.Single(g => g.List == "member_self_ids").Grants);
        Assert.Equal(0, groups.Single(g => g.List == "member_self_pairs").Grants);
        Assert.Empty(groups.Single(g => g.List == "org_self").Confining);
    }

    /// <summary>
    /// A <c>Related</c> path's marker says that <em>some</em> related row belongs to the tenancy, so
    /// a confinement of that perspective would have to be borne by the marker. D266 §3 refuses it by
    /// name and leaves it to a later decision (§7).
    /// </summary>
    [Fact]
    public void Confinement_along_a_related_kind_is_refused()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var source = policy.Source(TenancyPolicyFixture.SourceName);
        policy.Role("self");
        var org = policy.Tenancy("org");
        var desk = policy.Tenancy("desk");

        var members = source.Table("members");
        var orders = source.Table("orders");
        members.Tenancy(r => r
            .Direct(org, members.Column("org_id"))
            .Related(desk).Through(orders));
        orders.Tenancy(r => r
            .Direct(org, orders.Column("member.org"))
            .Direct(desk, orders.Column("created_by")));

        var error = Assert.Throws<CatalogValidationException>(() => policy.Compile([Schema]));

        Assert.Contains("Related(\"desk\") on 'members'", error.Message, StringComparison.Ordinal);
        Assert.Contains("also holds 'org'", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            "confinement along a Related kind is refused", error.Message, StringComparison.Ordinal);
    }
}
