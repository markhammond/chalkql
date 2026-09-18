using Chalk.Catalog;
using Chalk.TestKit;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// The compiler from §8's declarations to layer A's descriptors
/// (<c>docs/design/16-entitlements.md</c> §5).
/// </summary>
/// <remarks>
/// What is asserted here is the <em>text</em> the compiler emits, because that text is the contract
/// with the core: the planner folds it, the pushdown gate reads it, and a reviewer of an audit log
/// sees it. The rows it produces are the corpus's job, and the two paths are compared there.
/// </remarks>
public sealed class CompilerTests
{
    private static readonly TenancyEntitlements Compiled = TenancyPolicyFixture.Shared.Entitlements;

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

    private static TableEntitlementDescriptor Members =>
        Compiled.For(TenancyPolicyFixture.SourceName, "members")!;

    private static TableEntitlementDescriptor Orders =>
        Compiled.For(TenancyPolicyFixture.SourceName, "orders")!;

    // ---- the row predicate ----

    /// <summary>
    /// One membership test per dimension and role, OR-ed across both, then the global wildcard —
    /// and nothing that is not a column, a scalar or a list (§2).
    /// </summary>
    [Fact]
    public void The_row_predicate_is_a_membership_test_per_dimension_and_role()
    {
        Assert.Equal(
            "((org_id IN (@ctx.org_manager) OR (id, org_id) IN (@ctx.member_manager_pairs) "
            + "OR id IN (@ctx.member_manager_ids) OR @ctx.global_manager) "
            + "OR (org_id IN (@ctx.org_agent) OR (id, org_id) IN (@ctx.member_agent_pairs) "
            + "OR id IN (@ctx.member_agent_ids) OR @ctx.global_agent) "
            + "OR (org_id IN (@ctx.org_auditor) OR (id, org_id) IN (@ctx.member_auditor_pairs) "
            + "OR id IN (@ctx.member_auditor_ids) OR @ctx.global_auditor) "
            + "OR (org_id IN (@ctx.org_self) OR (id, org_id) IN (@ctx.member_self_pairs) "
            + "OR id IN (@ctx.member_self_ids) OR @ctx.global_self) "
            // The two roles D261 adds are roles like any other: each is one membership test per
            // dimension, OR-ed in where the others are, and a principal holding neither binds them
            // empty and the disjunct folds away.
            + "OR (org_id IN (@ctx.org_support) OR (id, org_id) IN (@ctx.member_support_pairs) "
            + "OR id IN (@ctx.member_support_ids) OR @ctx.global_support) "
            + "OR (org_id IN (@ctx.org_counter) OR (id, org_id) IN (@ctx.member_counter_pairs) "
            + "OR id IN (@ctx.member_counter_ids) OR @ctx.global_counter) "
            // And the role D265 clause (h) adds, on a table that holds no vendor kind at all: a
            // role is admitted on every table the policy does not restrict it on, so `members`
            // gains one membership per dimension it *does* hold, in the vendor role. No principal
            // of the fixture holds an organisation or a member grant in that role, so every one of
            // these binds empty and the disjunct folds away — which is what makes adding a role
            // cost nothing to a table that has nothing to do with it.
            + "OR (org_id IN (@ctx.org_vendor) OR (id, org_id) IN (@ctx.member_vendor_pairs) "
            + "OR id IN (@ctx.member_vendor_ids) OR @ctx.global_vendor)) "
            + "OR @ctx.global",
            Members.RowPredicate);
    }

    /// <summary>
    /// A table with a created-by column carries the fail-safe: a row the principal wrote is visible
    /// wherever it is (D206). And the tenancy came through the path <c>member.org</c>, which
    /// resolved to a column of this table.
    /// </summary>
    [Fact]
    public void The_created_by_fail_safe_is_a_disjunct_of_the_row_predicate()
    {
        Assert.Contains("OR created_by = @ctx.user OR @ctx.global", Orders.RowPredicate, StringComparison.Ordinal);
        Assert.Contains("org_id IN (@ctx.org_manager)", Orders.RowPredicate, StringComparison.Ordinal);
    }

    /// <summary>A <c>Custom</c> table's predicate is the host's own, untouched.</summary>
    [Fact]
    public void A_host_predicate_is_emitted_as_written()
    {
        Assert.Equal(
            "org_id IN (@ctx.allowed_orgs)",
            Compiled.For(TenancyPolicyFixture.SourceName, "invites")!.RowPredicate);
    }

    /// <summary>An <c>Unrestricted</c> table gets no descriptor at all, which is what costs nothing.</summary>
    [Fact]
    public void An_unrestricted_table_has_no_entitlement()
    {
        Assert.Null(Compiled.For(TenancyPolicyFixture.SourceName, "symbols"));
    }

    // ---- the column rules ----

    /// <summary>
    /// A realm column, most permissive first: the manager's FULL, then the two MASK rules in
    /// declaration order, because they mask differently — the agent an initial, the auditor a token.
    /// </summary>
    [Fact]
    public void A_realm_column_is_ordered_most_permissive_first()
    {
        var first = Column(Members, 2);

        Assert.Equal(3, first.Rules.Count);
        Assert.Equal(Disclosure.Full, first.Rules[0].Then);
        Assert.Equal(
            "(org_id IN (@ctx.org_manager) OR (id, org_id) IN (@ctx.member_manager_pairs) "
            + "OR id IN (@ctx.member_manager_ids) OR @ctx.global_manager)",
            first.Rules[0].When);
        Assert.Equal(Disclosure.Masked, first.Rules[1].Then);
        Assert.Equal("SUBSTRING(first_name, 1, 1)", first.Rules[1].Mask);
        Assert.Equal(Disclosure.Masked, first.Rules[2].Then);
        Assert.Equal("FINGERPRINT(first_name, @ctx.mask_key)", first.Rules[2].Mask);
        Assert.Equal(Disclosure.None, first.Otherwise);
    }

    /// <summary>
    /// A column in a realm nobody grants is disclosed to nobody — the global grant included. That is
    /// what putting a column in a realm means: a rule discloses it, or nothing does.
    /// </summary>
    /// <remarks>
    /// Since D261 the fixture's <c>restricted</c> realm carries two rules over <c>national_id</c>,
    /// and neither of them discloses it: one grants a <b>test</b> and one a counted population, so
    /// what the column resolves to for every principal is still the placeholder and the default is
    /// still <c>None</c>. The claim is therefore made where it was and made twice: no rule of this
    /// column discloses a value, and there is still no rule at all on a realm column nobody names —
    /// <c>orders.note</c> under the policy variant no role grants.
    /// </remarks>
    [Fact]
    public void A_realm_column_no_role_grants_has_no_rule_at_all()
    {
        var restricted = Column(Members, 4);

        Assert.All(
            restricted.Rules,
            rule => Assert.True(
                rule.Then is Disclosure.Test or Disclosure.AggregateOnly,
                $"a rule of the restricted realm discloses {rule.Then}"));
        Assert.Equal(Disclosure.None, restricted.Otherwise);

        var policy = TenancyPolicy.Declare(Catalog);
        var members = policy.Source(TenancyPolicyFixture.SourceName).Table("members");
        policy.Role("analyst");
        members
            .Tenancy(r => r.Direct(policy.Tenancy("org"), members.Column("org_id")))
            .Realm(policy.Realm("restricted"), members.Column("national_id"));

        var ungranted = policy.Compile([Schema]).For(members)!;

        Assert.Empty(Column(ungranted, 4).Rules);
        Assert.Equal(Disclosure.None, Column(ungranted, 4).Otherwise);
    }

    /// <summary>A column in no realm and named by no rule takes the table's default: full.</summary>
    [Fact]
    public void A_column_in_no_realm_is_not_mentioned()
    {
        Assert.Null(Members.FindColumn(5));
        Assert.Equal(Disclosure.Full, Members.DefaultDisclosure);
    }

    /// <summary>
    /// A population-only column: the explicit AGGREGATE_ONLY of the one role that names it, the
    /// default FULL of the roles that do not, and the allow-list and floor the rule declared.
    /// </summary>
    [Fact]
    public void A_population_only_column_carries_its_allow_list_and_floor()
    {
        var amount = Column(Orders, 3);

        Assert.Equal(["COUNT", "SUM", "SUM0", "AVG"], amount.AggregateOnlyFunctions);
        Assert.Equal(TenancyFixture.MinGroupSize, amount.MinGroupSize);
        Assert.Contains(amount.Rules, r => r.Then == Disclosure.AggregateOnly);
        Assert.Contains(amount.Rules, r => r.Then == Disclosure.Full);
        Assert.Equal(Disclosure.None, amount.Otherwise);
    }

    /// <summary>
    /// A table restricted by a dimension <em>and</em> a host predicate (D215): the two are OR-ed in
    /// declaration order, and the grant-driven route carries the global escape while the host's own
    /// text is emitted exactly as written.
    /// </summary>
    [Fact]
    public void A_dimension_and_a_predicate_are_or_ed_in_declaration_order()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var members = policy.Source(TenancyPolicyFixture.SourceName).Table("members");
        policy.Role("manager");
        members.Tenancy(r => r
            .Direct(policy.Tenancy("org"), members.Column("org_id"))
            .Predicate(Sql.Of("postcode IN (@ctx.open_postcodes)")));

        var compiled = policy.Compile([Schema]);

        Assert.Equal(
            "((org_id IN (@ctx.org_manager) OR @ctx.global_manager)) "
            + "OR postcode IN (@ctx.open_postcodes) OR @ctx.global",
            compiled.For(members)!.RowPredicate);
    }

    /// <summary>
    /// <c>Public()</c> is the explicit spelling of a catalogue (D272): every row readable by every
    /// principal, the dimension kept so that a path to the kind still walks to its column. It
    /// compiles to exactly what <c>Predicate(Sql.Of("TRUE"))</c> wrote by hand, and unlike
    /// <c>Unrestricted()</c> the descriptor still carries the dimension's own membership test.
    /// </summary>
    [Fact]
    public void Public_keeps_the_dimension_and_ors_true_onto_the_row_predicate()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var members = policy.Source(TenancyPolicyFixture.SourceName).Table("members");
        policy.Role("manager");
        members.Tenancy(r => r
            .Direct(policy.Tenancy("org"), members.Column("org_id"))
            .Public());

        var byHand = TenancyPolicy.Declare(Catalog);
        var handMembers = byHand.Source(TenancyPolicyFixture.SourceName).Table("members");
        byHand.Role("manager");
        handMembers.Tenancy(r => r
            .Direct(byHand.Tenancy("org"), handMembers.Column("org_id"))
            .Predicate(Sql.Of("TRUE")));

        var compiled = policy.Compile([Schema]).For(members)!;
        var hand = byHand.Compile([Schema]).For(handMembers)!;

        Assert.Equal(hand.RowPredicate, compiled.RowPredicate);
        Assert.Equal(hand.DescriptorHash, compiled.DescriptorHash);
        Assert.Contains("org_id IN (@ctx.org_manager)", compiled.RowPredicate, StringComparison.Ordinal);
        Assert.Contains(" OR TRUE", compiled.RowPredicate, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unrestricted table may still mask a column (D215): every row is visible, and the column
    /// rules apply over all of them. The descriptor carries no row predicate at all.
    /// </summary>
    [Fact]
    public void An_unrestricted_table_masks_a_column_over_every_row()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var members = policy.Source(TenancyPolicyFixture.SourceName).Table("members");
        var pii = policy.Realm("pii");
        members
            .Unrestricted()
            .Realm(pii, members.Column("first_name"))
            .Access(new AccessRule
            {
                Roles = [policy.Role("manager")],
                Realm = pii,
                Grants = Verdict.Full,
            })
            .Access(new AccessRule
            {
                Roles = [policy.Role("agent")],
                Realm = pii,
                Grants = Verdict.Mask,
                Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 1)"),
            });

        var compiled = policy.Compile([Schema]);

        var descriptor = compiled.For(members)!;
        Assert.Equal("", descriptor.RowPredicate);
        var column = Assert.Single(descriptor.Columns);
        Assert.Equal(Disclosure.Full, column.Rules[0].Then);
        Assert.Equal(Disclosure.Masked, column.Rules[1].Then);
        Assert.Equal("SUBSTRING(first_name, 1, 1)", column.Rules[1].Mask);
        Assert.Equal(Disclosure.None, column.Otherwise);
    }

    /// <summary>
    /// <c>ResourceOwnerSees.Full</c> prepends the resource-owner rule to every protected column (D206), and
    /// <c>ByRules</c> does not: on an ungranted tenancy the owner then sees the row and nothing of
    /// what is in it.
    /// </summary>
    [Fact]
    public void Resource_owner_sees_full_prepends_a_rule_that_by_rules_does_not()
    {
        var full = Column(
            TenancyPolicyFixture.Create().Entitlements.For(TenancyPolicyFixture.SourceName, "notes")!,
            3);
        var byRules = Column(
            TenancyPolicyFixture.Create(ResourceOwnerSees.ByRules).Entitlements
                .For(TenancyPolicyFixture.SourceName, "notes")!,
            3);

        Assert.Equal("created_by = @ctx.user", full.Rules[0].When);
        Assert.Equal(Disclosure.Full, full.Rules[0].Then);
        Assert.Equal(full.Rules.Count - 1, byRules.Rules.Count);
        Assert.DoesNotContain(byRules.Rules, r => r.When == "created_by = @ctx.user");
    }

    /// <summary>
    /// A visibility rule restricts: the auditor holds no view of <c>notes</c>, so their grants are
    /// not a disjunct of its row predicate and no rule of its columns names them.
    /// </summary>
    [Fact]
    public void A_visibility_rule_keeps_a_role_out_of_the_row_predicate()
    {
        var notes = Compiled.For(TenancyPolicyFixture.SourceName, "notes")!;

        Assert.DoesNotContain("auditor", notes.RowPredicate, StringComparison.Ordinal);
        Assert.Contains("org_id IN (@ctx.org_manager)", notes.RowPredicate, StringComparison.Ordinal);
        Assert.Contains("org_id IN (@ctx.org_agent)", notes.RowPredicate, StringComparison.Ordinal);
    }

    // ---- AccessRule.When (D221) ----

    /// <summary>
    /// A rule's own condition is AND-ed onto the roles it speaks for (D221), so a rule can say
    /// "in full, but only for a row that looks like this".
    /// </summary>
    [Fact]
    public void A_rules_own_condition_is_anded_onto_the_role_condition()
    {
        var first = Column(WithCondition(Sql.Of("role = 'analyst'")), 2);

        Assert.Equal(
            "(org_id IN (@ctx.org_analyst) OR (id, org_id) IN (@ctx.member_analyst_pairs) "
            + "OR id IN (@ctx.member_analyst_ids) OR @ctx.global_analyst) "
            + "AND (role = 'analyst')",
            first.Rules[0].When);
        Assert.Equal(Disclosure.Full, first.Rules[0].Then);
    }

    /// <summary>
    /// A rule condition may read any column of the row, protected or not: what it discloses is one
    /// bit and the policy author chose it (§1, D220, D221). The mask is what may not.
    /// </summary>
    [Fact]
    public void A_rules_own_condition_may_read_a_protected_column() =>
        Assert.Contains(
            "last_name <> ''",
            Column(WithCondition(Sql.Of("last_name <> ''")), 2).Rules[0].When,
            StringComparison.Ordinal);

    /// <summary>A rule with no condition is what every rule was: the roles alone.</summary>
    [Fact]
    public void A_rule_without_a_condition_is_the_roles_alone() =>
        Assert.DoesNotContain(" AND (", Column(WithCondition(null), 2).Rules[0].When, StringComparison.Ordinal);

    /// <summary>One realm, one role, one <c>Full</c> rule whose condition is what varies.</summary>
    private static TableEntitlementDescriptor WithCondition(Sql? when)
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var members = policy.Source(TenancyPolicyFixture.SourceName).Table("members");
        var org = policy.Tenancy("org");
        var pii = policy.Realm("pii");
        members
            .Tenancy(r => r
                .Direct(org, members.Column("org_id"))
                .Direct(policy.Subject("member", within: [org]), members.Column("id")))
            .Realm(pii, members.Column("first_name"), members.Column("last_name"))
            .Access(new AccessRule
            {
                Roles = [policy.Role("analyst")],
                Realm = pii,
                Grants = Verdict.Full,
                When = when,
            });

        return policy.Compile([Schema]).For(members)!;
    }

    // ---- AccessRule.Placeholder (D224) ----

    /// <summary>
    /// A <c>None</c> rule's placeholder rides on the rule it was written on, verbatim: the rule that
    /// withholds the value is the one that says what stands in its place, and a second role reading
    /// the same column keeps whatever its own rule says.
    /// </summary>
    [Fact]
    public void A_none_rules_placeholder_travels_on_its_own_rule()
    {
        var first = Column(WithPlaceholder(Sql.Of("'withheld'")), 2);

        Assert.Equal(Disclosure.None, first.Rules[0].Then);
        Assert.Equal("'withheld'", first.Rules[0].Placeholder);

        // The column's own placeholder is untouched: the rule's wins where it matches, and the
        // column's answers everywhere else (D224's precedence).
        Assert.Equal("", first.Placeholder);
    }

    /// <summary>A rule that states none leaves the field empty, as every rule did before D224.</summary>
    [Fact]
    public void A_rule_without_a_placeholder_states_none() =>
        Assert.Equal("", Column(WithPlaceholder(null), 2).Rules[0].Placeholder);

    /// <summary>
    /// The placeholder is part of the descriptor's identity: two policies differing only in it are
    /// two descriptors, so a cache can never serve one for the other.
    /// </summary>
    [Fact]
    public void A_rules_placeholder_changes_the_descriptor_hash() =>
        Assert.NotEqual(
            WithPlaceholder(Sql.Of("'withheld'")).DescriptorHash,
            WithPlaceholder(Sql.Of("'redacted'")).DescriptorHash);

    /// <summary>
    /// A placeholder beside a verdict that discloses something is refused where it was written
    /// (D224): a rule that hands over a value has nothing to stand in for it.
    /// </summary>
    [Theory]
    [InlineData(Verdict.Full)]
    [InlineData(Verdict.Mask)]
    public void A_placeholder_on_a_rule_that_discloses_is_refused(Verdict grants)
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => Declared(Sql.Of("'withheld'"), grants).Compile([Schema]));

        Assert.Contains("realm 'pii'", error.Message, StringComparison.Ordinal);
        Assert.Contains($"grants {grants} and states a placeholder", error.Message, StringComparison.Ordinal);
        Assert.Contains("meaningful only with Verdict.None", error.Message, StringComparison.Ordinal);
    }

    /// <summary>One realm, one role, one <c>None</c> rule whose placeholder is what varies.</summary>
    private static TableEntitlementDescriptor WithPlaceholder(Sql? placeholder) =>
        Declared(placeholder, Verdict.None)
            .Compile([Schema])
            .For(TenancyPolicyFixture.SourceName, "members")!;

    private static TenancyPolicy Declared(Sql? placeholder, Verdict grants)
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var members = policy.Source(TenancyPolicyFixture.SourceName).Table("members");
        var pii = policy.Realm("pii");
        members
            .Tenancy(r => r.Direct(policy.Tenancy("org"), members.Column("org_id")))
            .Realm(pii, members.Column("first_name"), members.Column("last_name"))
            .Access(new AccessRule
            {
                Roles = [policy.Role("analyst")],
                Realm = pii,
                Grants = grants,
                Placeholder = placeholder,
            });

        return policy;
    }

    private static ColumnEntitlementDescriptor Column(TableEntitlementDescriptor table, int ordinal) =>
        table.FindColumn(ordinal)
        ?? throw new InvalidOperationException($"no entitlement for column {ordinal}");

    // ---- AccessRule.Tests (D261) ----

    /// <summary>
    /// A <c>Test</c> rule compiles to a <c>TEST</c> disclosure carrying its shapes, deduplicated and
    /// in the order the host wrote them (<c>docs/design/36-test-verdict.md</c> §1).
    /// </summary>
    [Fact]
    public void A_test_rules_shapes_travel_to_layer_a()
    {
        var column = Column(
            WithTests(Verdict.Test, [Test.Equals, Test.NotEquals, Test.Equals]), 2);

        Assert.Equal(Disclosure.None, column.Otherwise);
        Assert.Equal(Disclosure.Test, column.Rules[0].Then);
        Assert.Equal(
            [Chalk.Entitlements.TestShape.Equals, Chalk.Entitlements.TestShape.NotEquals],
            column.Rules[0].Tests);
    }

    /// <summary>
    /// The aggregate form: an <c>AggregateOnly</c> rule carries its shapes the same way, which is
    /// what permits a test as the <c>FILTER</c> of a permitted aggregate (D261 §2).
    /// </summary>
    [Fact]
    public void An_aggregate_only_rules_shapes_travel_too()
    {
        var column = Column(
            WithTests(Verdict.AggregateOnly, [Test.Equals], aggregates: [Aggregate.Count]), 2);

        Assert.Equal(Disclosure.AggregateOnly, column.Rules[0].Then);
        Assert.Equal([Chalk.Entitlements.TestShape.Equals], column.Rules[0].Tests);
    }

    /// <summary>A rule that grants no test states no shape, as every rule did before D261.</summary>
    [Fact]
    public void A_rule_without_shapes_states_none() =>
        Assert.Empty(Column(WithTests(Verdict.Mask, []), 2).Rules[0].Tests);

    /// <summary>
    /// The shapes are part of the descriptor's identity: two policies differing only in them are two
    /// descriptors, so a cache can never serve one for the other.
    /// </summary>
    [Fact]
    public void The_shapes_change_the_descriptor_hash() =>
        Assert.NotEqual(
            WithTests(Verdict.Test, [Test.Equals]).DescriptorHash,
            WithTests(Verdict.Test, [Test.Equals, Test.In]).DescriptorHash);

    /// <summary>
    /// A <c>Test</c> rule naming no shape discloses neither the value nor any comparison of it, and
    /// is refused where it was written (D261 §1).
    /// </summary>
    [Fact]
    public void A_test_rule_naming_no_shape_is_refused()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => DeclaredTests(Verdict.Test, [], []).Compile([Schema]));

        Assert.Contains("realm 'pii'", error.Message, StringComparison.Ordinal);
        Assert.Contains("names no comparison shape", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Shapes beside a verdict that neither tests nor counts are refused where they were written: a
    /// rule that discloses the value has nothing to test, and one that withholds it entirely says so.
    /// </summary>
    [Theory]
    [InlineData(Verdict.Full)]
    [InlineData(Verdict.Mask)]
    [InlineData(Verdict.None)]
    public void Shapes_on_a_rule_that_reads_none_are_refused(Verdict grants)
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => DeclaredTests(grants, [Test.Equals], []).Compile([Schema]));

        Assert.Contains($"grants {grants} and names comparison shapes", error.Message, StringComparison.Ordinal);
    }

    private static TableEntitlementDescriptor WithTests(
        Verdict grants, IReadOnlyList<Test> tests, IReadOnlyList<Aggregate>? aggregates = null) =>
        DeclaredTests(grants, tests, aggregates ?? [])
            .Compile([Schema])
            .For(TenancyPolicyFixture.SourceName, "members")!;

    private static TenancyPolicy DeclaredTests(
        Verdict grants, IReadOnlyList<Test> tests, IReadOnlyList<Aggregate> aggregates)
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var members = policy.Source(TenancyPolicyFixture.SourceName).Table("members");
        var pii = policy.Realm("pii");
        members
            .Tenancy(r => r.Direct(policy.Tenancy("org"), members.Column("org_id")))
            .Realm(pii, members.Column("first_name"), members.Column("last_name"))
            .Access(new AccessRule
            {
                Roles = [policy.Role("support")],
                Realm = pii,
                Grants = grants,
                Mask = grants == Verdict.Mask ? Sql.Of("lambda v: SUBSTRING(v, 1, 1)") : null,
                Aggregates = aggregates,
                MinGroupSize = aggregates.Count > 0 ? 3 : 0,
                Tests = tests,
            });

        return policy;
    }
}
