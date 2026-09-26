using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The <c>tenancy</c> fixture end to end (step 26, <c>docs/design/16-entitlements.md</c> §8): what
/// each principal sees of each statement, what the report says about it, and what is refused.
/// </summary>
/// <remarks>
/// Layer A alone — the entitlements are written directly as descriptors by
/// <see cref="TenancyFixture"/>, which never references a policy package — under prepare-time
/// binding, which is the primary mode. The numbering follows §8's table so a reader can compare.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class EntitlementsTests(SharedSidecar sidecar)
{
    private static readonly TenancyFixture Fixture = TenancyFixture.Shared;

    // ---------------------------------------------------------------- 01: the star

    /// <summary>
    /// §8's query 01. <c>u1</c> manages O1 and acts as an agent in O2, so one result holds raw names
    /// from O1 and initial-masked ones from O2 — the mixed principal's <c>CASE</c>, per row.
    /// </summary>
    [Fact]
    public async Task Q01_a_star_over_members_discloses_per_organisation()
    {
        var rows = await RowsAsync("SELECT id, first_name FROM members ORDER BY id", TenancyFixture.U1);

        Assert.Equal(
            [$"1|{First(1)}", $"2|{First(2)}", "3|T", "4|A", "7|T"],
            rows);
    }

    /// <summary>
    /// An entitled prepare carries every prepare option the host set (F141): the redacted text and the
    /// plan text arrive, because the entitled engine composes its options with <c>with</c> over the
    /// record rather than copying the fields it knew about.
    /// </summary>
    [Fact]
    public async Task An_entitled_prepare_carries_every_option_the_host_set()
    {
        await using var engine = await EngineAsync();

        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id FROM members WHERE postcode = '2000' ORDER BY id",
            TenancyFixture.U1,
            new PrepareOptions { IncludeRedactedSql = true, IncludePlanText = true });

        Assert.NotNull(prepared.Query.PlanText);
        Assert.NotNull(prepared.Query.RedactedSql);
        Assert.Contains("REDACTED", prepared.Query.RedactedSql, StringComparison.Ordinal);
        Assert.DoesNotContain("2000", prepared.Query.RedactedSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Q01_an_agent_sees_initials_and_an_auditor_a_token()
    {
        Assert.Equal(
            ["1|T", "2|B"],
            await RowsAsync("SELECT id, first_name FROM members ORDER BY id", TenancyFixture.U2));
        // The auditor's per-rule mask is FINGERPRINT, keyed by the principal's own mask key: a
        // stable pseudonym rather than a constant, so equal names give equal tokens and nothing
        // says what the name was. The vectors are openssl's, not the engine's own
        // (`printf '<the fixture value>' | openssl dgst -sha256 -hmac k`, first 16 bytes) — and
        // because the value carries a canary (D252) the digest is of the whole of it.
        Assert.Equal(
            ["1|ed23bc5f944a7ec955ace055dba31f68", "2|6657767d30a1fb0444393493e42f5527"],
            await RowsAsync("SELECT id, first_name FROM members ORDER BY id", TenancyFixture.U6));
    }

    // ---------------------------------------------------------------- 04, 05, 11: the query-field rule is gone

    /// <summary>
    /// §8's query 04, and D195's whole point: the predicate compares the <em>disclosed</em> value, so
    /// <c>u1</c> gets O1's rows whose name starts with T and O2's rows whose initial is T.
    /// </summary>
    [Fact]
    public async Task Q04_a_predicate_compares_the_disclosed_value()
    {
        Assert.Equal(
            [$"1|{First(1)}", "3|T", "7|T"],
            await RowsAsync(
                "SELECT id, first_name FROM members WHERE first_name LIKE 'T%' ORDER BY id",
                TenancyFixture.U1));
    }

    /// <summary>§8's query 05: a predicate on a masked column compares the mask, never the raw value.</summary>
    [Fact]
    public async Task Q05_a_predicate_on_a_withheld_column_compares_the_placeholder()
    {
        Assert.Equal(
            ["1", "2"],
            await RowsAsync(
                "SELECT id FROM members WHERE national_id IS NULL ORDER BY id", TenancyFixture.U2));
    }

    /// <summary>§8's query 11: a sort on a masked column sorts the masks.</summary>
    [Fact]
    public async Task Q11_a_sort_on_a_masked_column_sorts_the_masks()
    {
        Assert.Equal(
            ["2|I"],
            await RowsAsync(
                "SELECT id, last_name FROM members ORDER BY last_name LIMIT 1", TenancyFixture.U2));
    }

    // ---------------------------------------------------------------- 06: a withheld column

    [Fact]
    public async Task Q06_a_withheld_column_is_a_placeholder_per_row_and_is_reported()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT national_id FROM members", TenancyFixture.U2);

        Assert.Equal(ReportedDisclosure.Redacted, prepared.Columns[0].Disclosure);
        Assert.True(prepared.OutputSchema.FieldsList[0].IsNullable);
        Assert.Equal(
            "REDACTED",
            prepared.OutputSchema.FieldsList[0].Metadata!["chalk.disclosure"]);
        Assert.Equal(["", ""], await RowsAsync(engine, prepared));
    }

    /// <summary>
    /// D223: a quoted identifier beginning with <c>$chalk$</c> is refused before anything is parsed,
    /// as <c>INVALID_REQUEST</c>. It is the one spelling the grammar would allow for a name the
    /// planner writes for its own markers — the context marker, the per-request context schema, the
    /// strict-function guard — and a statement that spelled one would be shadowing a rewrite.
    /// </summary>
    [Fact]
    public async Task A_statement_naming_a_reserved_marker_is_refused_as_an_invalid_request()
    {
        await using var engine = await EngineAsync();

        var refusal = await Assert.ThrowsAsync<PlanningException>(
            () => engine.WithEntitlements()
                .PrepareAsync("SELECT id FROM \"$chalk$ctx\".\"members\"", TenancyFixture.U1)
                .AsTask());

        Assert.Equal(Chalk.Client.Rpc.PlanErrorKind.InvalidRequest, refusal.Kind);
        Assert.Contains(
            "the identifier \"$chalk$ctx\" in this statement begins with $chalk$",
            refusal.Message,
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 02, 07, 13: population-only

    /// <summary>§8's query 07: the aggregate is allowed and guarded; the value use is refused.</summary>
    [Fact]
    public async Task Q07_an_auditors_aggregate_is_allowed_and_a_value_use_is_refused()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT AVG(amount) FROM orders", TenancyFixture.U6);
        Assert.Equal(ReportedDisclosure.Aggregate, prepared.Columns[0].Disclosure);

        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            () => engine.WithEntitlements().PrepareAsync("SELECT amount FROM orders", TenancyFixture.U6).AsTask());
        Assert.Contains("main.orders.amount", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("population-only", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>§8's query 13: a group below the floor of three is NULL, not a number.</summary>
    [Fact]
    public async Task Q13_a_group_below_the_floor_is_null()
    {
        // u6 audits O1 alone, so only O1's four orders are visible and they clear the floor.
        Assert.Equal(
            [$"1|{TenancyFixture.Orders.Where(o => o.OrgId == 1).Sum(o => o.Amount)}"],
            await RowsAsync(
                "SELECT org_id, SUM(amount) FROM orders GROUP BY org_id ORDER BY org_id",
                TenancyFixture.U6));

        // A manager in O2, where there are two orders: below the floor is not the same as no rows,
        // and the report says Aggregate so a grid cannot read the NULL as "no rows".
        var mixed = TenancyFixture.Principal(
            user: 7, managerOrgs: [], agentOrgs: [], auditorOrgs: [2], subjectPairs: []);
        Assert.Equal(
            ["2|"],
            await RowsAsync(
                "SELECT org_id, SUM(amount) FROM orders GROUP BY org_id ORDER BY org_id", mixed));
    }

    /// <summary>§8's query 12: every value and query use of a population-only column is refused.</summary>
    [Theory]
    [InlineData("SELECT amount - amount FROM orders")]
    [InlineData("SELECT CASE WHEN amount > 0 THEN 1 ELSE 0 END FROM orders")]
    [InlineData("SELECT id FROM orders ORDER BY amount")]
    [InlineData("SELECT amount, COUNT(*) FROM orders GROUP BY amount")]
    [InlineData("SELECT o.id FROM orders o JOIN orders p ON o.amount = p.amount")]
    [InlineData("SELECT SUM(amount * 2) FROM orders")]
    [InlineData("SELECT SUM(amount) FILTER (WHERE amount > 0) FROM orders")]
    [InlineData("SELECT SUM(amount) OVER () FROM orders")]
    [InlineData("SELECT MAX(amount) FROM orders")]
    public async Task Q12_every_other_use_of_a_population_only_column_is_refused(string sql)
    {
        await using var engine = await EngineAsync();
        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            () => engine.WithEntitlements().PrepareAsync(sql, TenancyFixture.U6).AsTask());
        Assert.Contains("main.orders.amount", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Q12_a_numeric_cast_is_the_shape_people_write_and_is_allowed()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT AVG(CAST(amount AS DOUBLE)) FROM orders", TenancyFixture.U6);
        Assert.Equal(ReportedDisclosure.Aggregate, prepared.Columns[0].Disclosure);
    }

    // ---------------------------------------------------------------- 03, 24: the subject grant

    /// <summary>§8's query 03: a confined subject grant reaches that member's own rows.</summary>
    [Fact]
    public async Task Q03_a_subject_grant_reaches_its_own_rows()
    {
        Assert.Equal(
            ["5", "6"],
            await RowsAsync("SELECT id FROM orders ORDER BY id", TenancyFixture.U3));
    }

    /// <summary>§8's query 24: the same grant confined elsewhere reaches nothing.</summary>
    [Fact]
    public async Task Q24_a_subject_grant_confined_elsewhere_reaches_nothing()
    {
        var confinedElsewhere = TenancyFixture.Principal(
            user: 30, managerOrgs: [], agentOrgs: [], auditorOrgs: [], subjectPairs: [[3, 1]]);
        Assert.Empty(await RowsAsync("SELECT id FROM orders", confinedElsewhere));
    }

    // ---------------------------------------------------------------- 10, 14, 15, 15b, 20

    /// <summary>§8's query 10: no grant is zero rows and no error, and NONE under the option.</summary>
    [Fact]
    public async Task Q10_no_grant_is_zero_rows_and_visibility_none()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT body FROM notes WHERE org_id = 2", TenancyFixture.U5);

        // u5 created two notes in O1, so the predicate is not FALSE; restricting to O2 returns none.
        Assert.Empty(await RowsAsync(engine, prepared));

        // `members` has no created-by fail-safe, so a principal with no grant folds its predicate
        // to FALSE and the report says NONE — which is an acknowledgement about the context and the
        // statement, never about hidden rows (D207).
        var never = TenancyFixture.Principal(
            user: 99, managerOrgs: [], agentOrgs: [], auditorOrgs: [], subjectPairs: []);
        var none = await engine.WithEntitlements().PrepareAsync("SELECT id FROM members", never);
        Assert.Equal(TableVisibility.None, none.Entitlements.Tables[0].Visibility);
        Assert.Empty(await RowsAsync(engine, none));

        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            () => engine.WithEntitlements(new EntitlementsOptions { RefuseWhenNoVisibleRows = true }).PrepareAsync(
                "SELECT id FROM members",
                never).AsTask());
        Assert.Contains("no grant on main.members", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// §8's query 20, part 2b: the statement asks for an organization outside this principal's
    /// scope. The result is empty, and to a caller that reads the same as an empty table — so the
    /// report says which it was, naming the column both sides speak about.
    /// </summary>
    /// <remarks>
    /// It is an acknowledgement D207 permits because it depends on the statement's own text and this
    /// principal's own context and on no hidden row: what makes it true is that the statement
    /// <em>alone</em> is satisfiable and the statement with the policy is not.
    /// </remarks>
    [Fact]
    public async Task Q20_a_tenancy_outside_the_scope_is_reported_as_a_contradiction()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id, first_name FROM members WHERE org_id = 3", TenancyFixture.U1);

        var members = Assert.Single(prepared.Entitlements.Tables);
        Assert.Equal(TableVisibility.Some, members.Visibility);
        Assert.True(members.Contradiction);
        Assert.Equal("org_id", members.ContradictionColumn);
        Assert.Empty(await RowsAsync(engine, prepared));

        // And a host that prefers an exception is given one, naming the column.
        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            () => engine.WithEntitlements(new EntitlementsOptions { RefuseWhenNoVisibleRows = true }).PrepareAsync(
                "SELECT id, first_name FROM members WHERE org_id = 3",
                TenancyFixture.U1).AsTask());
        Assert.Contains("'org_id' outside this principal's scope", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A statement inside the scope is not a contradiction, so the flag is not free.</summary>
    [Fact]
    public async Task A_tenancy_inside_the_scope_is_not_a_contradiction()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id FROM members WHERE org_id = 1", TenancyFixture.U1);

        Assert.False(Assert.Single(prepared.Entitlements.Tables).Contradiction);
    }

    /// <summary>§8's query 14: a global grant is every row, raw, and <c>visibility = ALL</c>.</summary>
    [Fact]
    public async Task Q14_a_global_grant_sees_every_row_raw()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id, first_name FROM members ORDER BY id", TenancyFixture.U4);

        Assert.Equal(TableVisibility.All, prepared.Entitlements.Tables[0].Visibility);
        Assert.Equal(ReportedDisclosure.Full, prepared.Columns[1].Disclosure);
        string[] whole = [.. TenancyFixture.Members.Select(m => $"{m.Id}|{m.FirstName}")];
        Assert.Equal(whole, await RowsAsync(engine, prepared));
    }

    /// <summary>§8's query 15: unrestricted reference data is visible with no grants at all.</summary>
    [Fact]
    public async Task Q15_unrestricted_data_is_visible_with_no_grants()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT name FROM symbols ORDER BY name", TenancyFixture.U5);

        Assert.Empty(prepared.Entitlements.Tables);
        Assert.Equal(["EURUSD", "GBPUSD"], await RowsAsync(engine, prepared));
    }

    /// <summary>§8's query 15b: the custom predicate bound from a host-computed list.</summary>
    [Fact]
    public async Task Q15b_a_custom_predicate_reads_a_host_computed_list()
    {
        var scoped = TenancyFixture.Principal(
            user: 1, managerOrgs: [1], agentOrgs: [], auditorOrgs: [], subjectPairs: [],
            allowedOrgs: [1]);
        Assert.Equal(["1"], await RowsAsync("SELECT id FROM invites ORDER BY id", scoped));
    }

    /// <summary>§8's query 20: an organization outside the scope is zero rows and SOME, not NONE.</summary>
    [Fact]
    public async Task Q20_a_tenancy_outside_the_scope_is_zero_rows_and_some()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id FROM members WHERE org_id = 3", TenancyFixture.U1);

        Assert.Equal(TableVisibility.Some, prepared.Entitlements.Tables[0].Visibility);
        Assert.Empty(await RowsAsync(engine, prepared));
    }

    /// <summary>§8's query 25: what a creator sees of their own rows is an option (D206).</summary>
    [Fact]
    public async Task Q25_the_creator_sees_their_own_rows_by_the_option()
    {
        Assert.Equal(
            [
                $"1|{TenancyFixture.Notes[0].Body}",
                $"2|{TenancyFixture.Notes[1].Body}",
            ],
            await RowsAsync(
                "SELECT id, body FROM notes ORDER BY id", TenancyFixture.U5, TenancyFixture.Shared));

        var byRules = TenancyFixture.Create(entitled: true, creatorSeesFull: false);
        Assert.Equal(
            ["1|", "2|"],
            await RowsAsync("SELECT id, body FROM notes ORDER BY id", TenancyFixture.U5, byRules));
    }

    // ---------------------------------------------------------------- 08, 09: composition

    /// <summary>§8's query 08: entitlements compose across a join, each side by its own tenancy.</summary>
    [Fact]
    public async Task Q08_entitlements_compose_across_a_join()
    {
        Assert.Equal(
            [
                $"{Last(1)}|{OrderNote(1)}",
                $"{Last(1)}|{OrderNote(2)}",
                $"{Last(2)}|{OrderNote(3)}",
                $"{Last(2)}|{OrderNote(4)}",
                "E|********",
                "R|********",
            ],
            await RowsAsync(
                "SELECT m.last_name, o.note FROM members m JOIN orders o ON o.member_id = m.id "
                + "ORDER BY o.id",
                TenancyFixture.U1));
    }

    /// <summary>§8's query 09: a decorrelated lateral, with the entitlement inside it.</summary>
    [Fact]
    public async Task Q09_a_lateral_carries_the_entitlement_inside_it()
    {
        Assert.Equal(
            ["1|2", "2|2"],
            await RowsAsync(
                "SELECT m.id, c.n FROM members m CROSS JOIN LATERAL "
                + "(SELECT COUNT(*) AS n FROM orders o WHERE o.member_id = m.id) c ORDER BY m.id",
                TenancyFixture.U2));
    }

    // ---------------------------------------------------------------- 17, 18: stars and placeholders

    /// <summary>§8's query 17: what a star's redacted column becomes is the client's choice.</summary>
    [Fact]
    public async Task Q17_redacted_columns_are_a_placeholder_omitted_or_refused()
    {
        await using var engine = await EngineAsync();

        var placeholder = await engine.WithEntitlements().PrepareAsync(
            "SELECT * FROM members", TenancyFixture.U2);
        Assert.Contains("national_id", placeholder.OutputSchema.FieldsList.Select(f => f.Name));
        Assert.Equal(
            ReportedDisclosure.Redacted,
            placeholder.Columns.Single(c => c.Name == "national_id").Disclosure);

        var omitted = await engine.WithEntitlements(Redacting(StarExpansion.Omit)).PrepareAsync(
            "SELECT * FROM members",
            TenancyFixture.U2);
        Assert.DoesNotContain("national_id", omitted.OutputSchema.FieldsList.Select(f => f.Name));

        // A column the statement named is never omitted, however the option is set.
        var named = await engine.WithEntitlements(Redacting(StarExpansion.Omit)).PrepareAsync(
            "SELECT national_id FROM members",
            TenancyFixture.U2);
        Assert.Single(named.OutputSchema.FieldsList);

        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            () => engine.WithEntitlements(Redacting(StarExpansion.Refuse)).PrepareAsync(
                "SELECT * FROM members",
                TenancyFixture.U2).AsTask());
        Assert.Contains("national_id", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>The redaction policy with one member set (D217 as amended).</summary>
    private static EntitlementsOptions Redacting(StarExpansion stars) =>
        new() { Redaction = new RedactionPolicy { StarExpansion = stars } };

    /// <summary>§8's query 18: the empty value keeps the declared nullability.</summary>
    [Fact]
    public async Task Q18_placeholders_as_empty_keeps_the_declared_nullability()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements(new EntitlementsOptions {
                PlaceholderPolicy = Chalk.Entitlements.PlaceholderPolicy.PlaceholdersAsEmpty,
            }).PrepareAsync(
                "SELECT national_id FROM members",
            TenancyFixture.U2);

        Assert.False(prepared.OutputSchema.FieldsList[0].IsNullable);
        Assert.Equal(ReportedDisclosure.Redacted, prepared.Columns[0].Disclosure);
        Assert.Equal(["", ""], await RowsAsync(engine, prepared));
    }

    // ---------------------------------------------------------------- the star policy

    [Fact]
    public async Task A_star_over_an_entitled_table_is_refused_under_the_refusing_policies()
    {
        await using var engine = await EngineAsync();
        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            () => engine.WithEntitlements(new EntitlementsOptions { StarPolicy = StarPolicy.RefuseOverEntitled }).PrepareAsync(
                "SELECT * FROM members",
                TenancyFixture.U1).AsTask());
        Assert.Contains("main.members", refusal.Message, StringComparison.Ordinal);

        // Nothing changes for a table with no entitlement.
        var allowed = await engine.WithEntitlements(new EntitlementsOptions { StarPolicy = StarPolicy.RefuseOverEntitled }).PrepareAsync(
                "SELECT * FROM symbols",
            TenancyFixture.U1);
        Assert.Equal(2, allowed.OutputSchema.FieldsList.Count);
    }

    // ---------------------------------------------------------------- the report

    /// <summary>§8's report class: the meet over a derived column's origins (D202).</summary>
    [Fact]
    public async Task The_report_meets_a_derived_columns_origins()
    {
        await using var engine = await EngineAsync();

        var agent = await engine.WithEntitlements().PrepareAsync(
            "SELECT UPPER(first_name) AS u FROM members", TenancyFixture.U2);
        Assert.Equal(ReportedDisclosure.Masked, agent.Columns[0].Disclosure);

        var manager = await engine.WithEntitlements().PrepareAsync(
            "SELECT UPPER(first_name) AS u FROM members WHERE org_id = 1", TenancyFixture.U1);
        Assert.Equal(ReportedDisclosure.Full, manager.Columns[0].Disclosure);

        var mixed = await engine.WithEntitlements().PrepareAsync(
            "SELECT first_name FROM members", TenancyFixture.U1);
        Assert.Equal(ReportedDisclosure.PerRow, mixed.Columns[0].Disclosure);
        Assert.Equal("PER_ROW", mixed.OutputSchema.FieldsList[0].Metadata!["chalk.disclosure"]);
    }

    [Fact]
    public async Task The_report_names_each_entitled_table_and_is_honest_about_pushdown()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT m.id FROM members m JOIN orders o ON o.member_id = m.id", TenancyFixture.U1);

        Assert.Equal(
            ["main.members", "main.orders"],
            prepared.Entitlements.Tables.Select(t => $"{t.Schema}.{t.Table}").Order().ToArray());
        // A POCO table has no source to push into, and the flag says so rather than claiming it.
        Assert.All(prepared.Entitlements.Tables, t => Assert.False(t.RowPredicatePushed));
        Assert.All(prepared.Entitlements.Tables, t => Assert.Equal(32, t.DescriptorHash.Length));
    }

    // ---------------------------------------------------------------- the audit event

    [Fact]
    public async Task The_audit_observer_gets_one_value_free_event_per_execution()
    {
        var observed = new List<EntitlementsAuditEvent>();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [Fixture.Source],
            Planner = sidecar.CreatePlanner(),
        });
        var entitled = engine.WithEntitlements(audit: new Observer(observed));

        var context = TenancyFixture.Principal(
            user: 1, managerOrgs: [1], agentOrgs: [], auditorOrgs: [], subjectPairs: [],
            purpose: "support ticket 4711", actor: "agent-7");
        var prepared = await entitled.PrepareAsync("SELECT id FROM members", context);
        _ = await RowsAsync(engine, prepared);

        var audited = Assert.Single(observed);
        Assert.Equal(prepared.PlanDigest, audited.PlanDigest);
        Assert.Equal(prepared.Entitlements.Tables[0].DescriptorHash, audited.DescriptorHashes[0]);
        Assert.Equal("support ticket 4711", audited.Purpose);
        Assert.Equal("agent-7", audited.Actor);
        Assert.Equal(1, audited.ContextListRowCounts["manager_orgs"]);
        Assert.Equal(0, audited.ContextListRowCounts["agent_orgs"]);

        // Nothing is raised for a statement the rewrite did not touch.
        var unentitled = await entitled.PrepareAsync("SELECT name FROM symbols", context);
        _ = await RowsAsync(engine, unentitled);
        Assert.Single(observed);
    }

    private sealed class Observer(List<EntitlementsAuditEvent> observed) : IEntitlementsAudit
    {
        public void Executed(EntitlementsAuditEvent audited) => observed.Add(audited);
    }

    // ---------------------------------------------------------------- the negatives

    [Fact]
    public async Task Executing_without_a_context_against_an_entitled_catalog_is_refused()
    {
        await using var engine = await EngineAsync();
        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            () => engine.WithEntitlements().PrepareAsync("SELECT id FROM members").AsTask());
        // The refusal names both ways out: bind the values, or bind the shape and bind the values
        // at execution (D209).
        Assert.Contains("binds no execution context", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("shape-only", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_list_member_is_refused_at_bind()
    {
        await using var engine = await EngineAsync();
        var lists = new Dictionary<string, ContextRelation>(
            TenancyFixture.U1.Lists, StringComparer.Ordinal)
        {
            ["manager_orgs"] = new ContextRelation
            {
                Columns = ["id"],
                Rows = [[1], [null]],
                ColumnTypes = [ChalkType.Int32(nullable: true)],
            },
        };
        var context = new RequestContext { Scalars = TenancyFixture.U1.Scalars, Lists = lists };

        var refusal = await Assert.ThrowsAnyAsync<Exception>(
            () => engine.WithEntitlements().PrepareAsync("SELECT id FROM members", context).AsTask());
        Assert.Contains("NULL", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entitlement_referencing_a_missing_column_is_refused_at_registration()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Broken(new TableEntitlementDescriptor
            {
                Columns = [new ColumnEntitlementDescriptor { Column = 99 }],
            })));
        Assert.Contains("99", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Otherwise_full_on_a_tenanted_table_is_refused_at_registration()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Broken(new TableEntitlementDescriptor
            {
                RowPredicate = "org_id = 1",
                Columns =
                [
                    new ColumnEntitlementDescriptor { Column = 2, Otherwise = Disclosure.Full },
                ],
            })));
        Assert.Contains("D208", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Max_in_an_allow_list_is_refused_at_registration()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Broken(new TableEntitlementDescriptor
            {
                Columns =
                [
                    new ColumnEntitlementDescriptor
                    {
                        Column = 2,
                        AggregateOnlyFunctions = ["MAX"],
                        Rules =
                        [
                            new DisclosureRule { When = "TRUE", Then = Disclosure.AggregateOnly },
                        ],
                    },
                ],
            })));
        Assert.Contains("MAX", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_mask_under_a_rule_that_is_not_masked_is_refused_at_registration()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Broken(new TableEntitlementDescriptor
            {
                Columns =
                [
                    new ColumnEntitlementDescriptor
                    {
                        Column = 2,
                        Rules =
                        [
                            new DisclosureRule
                            {
                                When = "TRUE",
                                Then = Disclosure.Full,
                                Mask = "'x'",
                            },
                        ],
                    },
                ],
            })));
        Assert.Contains("mask", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A non-boolean <c>when</c> is caught by the sidecar, which is the side with a parser.</summary>
    [Fact]
    public async Task A_when_that_is_not_boolean_is_refused_by_the_planner()
    {
        var broken = TenancyFixture.Create(entitled: true);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [broken.Source],
            Planner = sidecar.CreatePlanner(),
        });
        _ = await engine.WithEntitlements().PrepareAsync("SELECT id FROM members", TenancyFixture.U1);
    }

    private static CatalogContext Broken(TableEntitlementDescriptor entitlement)
    {
        var schema = Fixture.Catalog.Schemas[0];
        var members = schema.Tables.Single(t => t.Name == "members");
        return new CatalogContext
        {
            ContextId = "broken",
            Epoch = 1,
            Schemas =
            [
                new SchemaDescriptor
                {
                    SourceId = schema.SourceId,
                    Name = schema.Name,
                    Kind = schema.Kind,
                    Capabilities = schema.Capabilities,
                    Tables =
                    [
                        new TableDescriptor
                        {
                            Name = members.Name,
                            Columns = members.Columns,
                            RowCount = members.RowCount,
                            RowCountKind = members.RowCountKind,
                            Entitlement = entitlement,
                        },
                    ],
                },
            ],
        };
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A protected value read off the fixture rather than restated here. Every value the policy can
    /// mask or hide carries a canary (D252, <c>32-adversarial-entitlements.md</c> §2), and the point
    /// each of these cases makes is about what the <em>policy</em> did to the value, never about
    /// which letters it is made of.
    /// </summary>
    private static string First(int member) =>
        TenancyFixture.Members.Single(m => m.Id == member).FirstName;

    private static string Last(int member) =>
        TenancyFixture.Members.Single(m => m.Id == member).LastName;

    private static string OrderNote(int order) =>
        TenancyFixture.Orders.Single(o => o.Id == order).Note;

    private async Task<ChalkEngine> EngineAsync(TenancyFixture? fixture = null) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [(fixture ?? Fixture).Source],
            Planner = sidecar.CreatePlanner(),
        });

    private async Task<string[]> RowsAsync(
        string sql, RequestContext context, TenancyFixture? fixture = null)
    {
        await using var engine = await EngineAsync(fixture);
        var prepared = await engine.WithEntitlements().PrepareAsync(sql, context);
        return await RowsAsync(engine, prepared);
    }

    private static async Task<string[]> RowsAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        var rows = new List<string>();
        await using var execution =
            await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(
                    r => string.Join("|", r.Select(v => v?.ToString() ?? ""))));
            }
        }

        return [.. rows];
    }
}
