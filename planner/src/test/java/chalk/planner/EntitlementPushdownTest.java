package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.Enforcement;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.ir.v1.RemoteQuery;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.PolicyException;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.ir.IrVersionGate;
import chalk.planner.ir.RelToIr;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.RequestContext;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.Test;

/**
 * The row predicate reaches the source, and the masks do not
 * (docs/design/16-entitlements.md §3.7, §3.8; D199, D200, D156).
 *
 * <p>Everything here is asserted on the generated SQL and the physical plan of the whole pipeline,
 * because what is at stake is exactly what leaves the process: a tenancy predicate that stayed local
 * is a full fetch, and a mask that travelled is a disclosure to a system the policy does not govern.
 */
class EntitlementPushdownTest {

  // ------------------------------------------------------------------ the row predicate travels

  /**
   * §3.7's first three points at once: {@code Filter_R} sits directly on the scan, the folded
   * tenancy predicate is an IN list, and the pushdown rules meet it over the {@code SourceScan}.
   */
  @Test
  void the_folded_tenancy_predicate_is_pushed_as_an_in_list() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remote(),
            "SELECT id FROM remote.members",
            TenancyCatalogs.manager(1, 2));

    assertThat(result.pushedRowPredicates()).containsExactly("remote.members");
    String sql = onlyRemote(result).getQueryText();
    assertThat(sql).contains("\"org_id\" IN (1, 2)");
    assertThat(sql).doesNotContain("*");
  }

  /**
   * §8's query 16: a mixed filter pushes the tenancy conjunct and keeps the residual local. The
   * residual here is a client-bodied user function, which is what the filter-residual preliminary
   * exists for — before it, one such conjunct held the tenancy predicate back with it.
   */
  @Test
  void a_mixed_filter_pushes_the_tenancy_conjunct_and_keeps_the_residual_local() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remote(),
            "SELECT id FROM remote.members WHERE postcode = '2000' AND is_vip(id)",
            TenancyCatalogs.manager(1, 2));

    String sql = onlyRemote(result).getQueryText();
    assertThat(sql).contains("\"org_id\" IN (1, 2)");
    assertThat(sql).contains("\"postcode\" = '2000'");
    assertThat(sql).doesNotContainIgnoringCase("is_vip");
    // The residual is a local filter above the boundary, and the report still says the row
    // predicate reached the source.
    assertThat(result.physicalPlanText()).containsIgnoringCase("is_vip");
    assertThat(result.pushedRowPredicates()).containsExactly("remote.members");
  }

  // ------------------------------------------------------------------ locality (§3.8, D200)

  /**
   * A principal who sees the column in full loses nothing: {@code UPPER(first_name) = 'T'} is an
   * ordinary predicate on an ordinary column and it pushes.
   */
  @Test
  void a_managers_expression_over_a_column_they_see_in_full_pushes() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remote(),
            "SELECT id FROM remote.members WHERE UPPER(first_name) = 'T'",
            TenancyCatalogs.manager(1));

    String sql = onlyRemote(result).getQueryText();
    assertThat(sql).contains("UPPER(\"first_name\") = 'T'");
    assertThat(sql).contains("\"org_id\" = 1");
  }

  /**
   * An agent's is the mask, and the mask stays here. The optimiser transposes the statement's own
   * predicate through {@code Project_D}, so what the gate is offered is
   * {@code UPPER(SUBSTRING(first_name, 1, 1)) = 'T'} — an expression over a column this leaf does
   * not disclose plainly — and it is evaluated locally, over the tenancy's rows the pushed row
   * predicate returned.
   */
  @Test
  void an_agents_expression_over_a_masked_column_stays_local() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remote(),
            "SELECT id FROM remote.members WHERE UPPER(first_name) = 'T'",
            TenancyCatalogs.agent(2));

    String sql = onlyRemote(result).getQueryText();
    assertThat(sql).doesNotContain("SUBSTRING");
    assertThat(sql).doesNotContain("UPPER");
    assertThat(sql).contains("\"org_id\" = 2");
    // The residual is above the boundary, where a reader of the plan can see it.
    assertThat(result.physicalPlanText()).contains("SUBSTRING");
  }

  /** A mask travels only when the table and the source both opt in (D153). */
  @Test
  void a_mask_reaches_the_source_only_with_both_flags() throws Exception {
    RequestContext agent = TenancyCatalogs.agent(2);
    String sql = "SELECT SUBSTRING(first_name, 1, 1) AS ini FROM remote.members";

    // Neither flag, one flag, the other flag: the mask stays here in all three.
    assertThat(remoteTextOf(TenancyCatalogs.remote(), sql, agent)).doesNotContain("SUBSTRING");
    assertThat(
            remoteTextOf(
                TenancyCatalogs.remote(
                    Enforcement.ENFORCEMENT_PUSHDOWN,
                    TestCatalogs.fullSqlCapabilities().build(),
                    /* pushMasks= */ true),
                sql,
                agent))
        .doesNotContain("SUBSTRING");
    assertThat(
            remoteTextOf(
                TenancyCatalogs.remote(
                    Enforcement.ENFORCEMENT_PUSHDOWN,
                    TenancyCatalogs.takingMasks(),
                    /* pushMasks= */ false),
                sql,
                agent))
        .doesNotContain("SUBSTRING");

    // Both, and it goes.
    CatalogContext both =
        TenancyCatalogs.remote(
            Enforcement.ENFORCEMENT_PUSHDOWN,
            TenancyCatalogs.takingMasks(),
            /* pushMasks= */ true);
    assertThat(remoteTextOf(both, sql, agent)).contains("SUBSTRING");

    // And when the statement merely *reads* the masked column, the mask the leaf applies is what
    // travels — which is the shape a host actually writes.
    assertThat(remoteTextOf(both, "SELECT id, first_name FROM remote.members", agent))
        .contains("SUBSTRING");
  }

  // ------------------------------------------------------------------ LOCAL (D156)

  /**
   * Under {@code LOCAL} the source receives a projected scan and no predicate at all, so the tenant
   * set never appears in remote query text — which is the point of the setting for a host that will
   * not have its tenancies in another system's query log.
   */
  @Test
  void local_enforcement_pushes_no_predicate_and_shows_no_tenant_set() throws Exception {
    CatalogContext local =
        TenancyCatalogs.remote(
            Enforcement.ENFORCEMENT_LOCAL,
            TestCatalogs.fullSqlCapabilities().build(),
            /* pushMasks= */ false);
    PlannerPipeline.Result result =
        plan(local, "SELECT id FROM remote.members WHERE postcode = '2000'", TenancyCatalogs.manager(1, 2));

    String sql = onlyRemote(result).getQueryText();
    assertThat(sql).doesNotContain("WHERE");
    assertThat(sql).doesNotContain("org_id\" IN");
    assertThat(sql).doesNotContain(" 1");
    // A projected scan, not the whole table: locality is about predicates, not about columns.
    assertThat(sql).startsWith("SELECT ");
    assertThat(result.pushedRowPredicates()).isEmpty();
  }

  // ------------------------------------------------------------------ PUSHDOWN_REQUIRED (D199)

  /**
   * §8's query 21: the same statements against a source that does not declare the IN shape. Under
   * {@code PUSHDOWN} the predicate is honestly evaluated locally; under {@code PUSHDOWN_REQUIRED}
   * the plan is refused naming the table and the shape, because for a source holding every
   * tenancy's rows a silent full fetch is the worse failure.
   *
   * <p>The shape is named in the <b>host's</b> vocabulary (F46): what the source declared and did
   * not is a {@code PredicateShape}, not Calcite's {@code SEARCH($1, Sarg[1, 2])} — a word no host
   * writes, wrapped around this principal's own tenancy identifiers, which §3.12 keeps out of an
   * error message.
   */
  @Test
  void pushdown_required_refuses_a_plan_that_would_filter_locally() throws Exception {
    CatalogContext permissive =
        TenancyCatalogs.remote(
            Enforcement.ENFORCEMENT_PUSHDOWN, TenancyCatalogs.withoutInLists(), false);
    PlannerPipeline.Result honest =
        plan(permissive, "SELECT id FROM remote.members", TenancyCatalogs.manager(1, 2));
    assertThat(honest.pushedRowPredicates()).isEmpty();

    CatalogContext required =
        TenancyCatalogs.remote(
            Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED, TenancyCatalogs.withoutInLists(), false);
    assertThatThrownBy(
            () -> plan(required, "SELECT id FROM remote.members", TenancyCatalogs.manager(1, 2)))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("remote.members")
        .hasMessageContaining("PUSHDOWN_REQUIRED")
        .hasMessageContaining("does not declare PREDICATE_SHAPE_IN")
        .hasMessageNotContaining("SEARCH")
        .hasMessageNotContaining("Sarg");
  }

  /**
   * One organisation folds to an equality, which that profile <em>does</em> declare — so the same
   * descriptor is satisfied, and the refusal above is about the shape and not about the setting.
   */
  @Test
  void pushdown_required_names_eq_when_that_is_the_shape_a_source_lacks() throws Exception {
    chalk.ir.v1.SourceCapabilities withoutEq =
        TestCatalogs.fullSqlCapabilities().build().toBuilder()
            .clearPushablePredicates()
            .addPushablePredicates(chalk.ir.v1.PredicateShape.PREDICATE_SHAPE_RANGE)
            .setMaxInList(0)
            .build();
    CatalogContext required =
        TenancyCatalogs.remote(Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED, withoutEq, false);

    assertThatThrownBy(
            () -> plan(required, "SELECT id FROM remote.members", TenancyCatalogs.manager(1)))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("does not declare PREDICATE_SHAPE_EQ");
  }

  /** And it is satisfied, silently, when the shape does reach the source. */
  @Test
  void pushdown_required_is_satisfied_by_a_source_that_takes_the_shape() throws Exception {
    CatalogContext required =
        TenancyCatalogs.remote(
            Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED,
            TestCatalogs.fullSqlCapabilities().build(),
            false);
    PlannerPipeline.Result result =
        plan(required, "SELECT id FROM remote.members", TenancyCatalogs.manager(1, 2));

    assertThat(result.pushedRowPredicates()).containsExactly("remote.members");
  }

  // ------------------------------------------------------------------ trust (D156)

  /**
   * {@code trust_source_row_level_security} skips {@code Filter_R} for that source's tables <b>and
   * nothing else</b>: the disclosures are still Chalk's, so an agent still sees the mask, and the
   * report says the row predicate was not pushed — because there was none to push.
   *
   * <p>What the disclosure is, though, is {@code PER_ROW} and not {@code MASKED} (F44). With the
   * filter skipped, the rows the source returns are the ones its own row security let through, and
   * the pass may not assume the scope it did not enforce: inside the scope the agent's rule matches
   * and the value is masked, outside it no rule matches and the value is the placeholder. One origin
   * that is both, which §3.12's meet resolves to {@code PER_ROW}.
   */
  @Test
  void a_trusted_source_gets_no_row_predicate_and_still_gets_the_disclosures() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.trustedRemote(),
            "SELECT first_name FROM remote.members",
            TenancyCatalogs.agent(2));

    String sql = onlyRemote(result).getQueryText();
    // No predicate at all reached the source, because the pass emitted none for its tables.
    assertThat(sql).doesNotContain("WHERE");
    assertThat(result.pushedRowPredicates()).isEmpty();

    // The mask is still applied, here, over the rows in scope — and only over those.
    assertThat(result.physicalPlanText()).contains("SUBSTRING");
    assertThat(result.entitledLeaves()).hasSize(1);
    assertThat(result.entitledLeaves().get(0).of(2))
        .isEqualTo(chalk.planner.entitlement.Disclosed.PER_ROW);

    // And the sanitiser is the CASE that says so, rather than the bare mask a folded-away scope
    // would have left: a row the source returned from outside the agent's organisation is redacted.
    assertThat(result.physicalPlanText()).contains("CASE");
  }

  /**
   * The other half of F44: the report still says how much of the table this principal's grants
   * reach. D156 skips the filter and nothing else, so `visibility` stays the folded predicate's own
   * answer — saying ALL would tell a caller its grants reach every row, which is the one thing the
   * setting does not mean.
   */
  @Test
  void a_trusted_source_reports_the_visibility_the_grants_come_to() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.trustedRemote(),
            "SELECT first_name FROM remote.members",
            TenancyCatalogs.agent(2));

    assertThat(result.entitledLeaves().get(0).visibility())
        .isEqualTo(chalk.planner.entitlement.DisclosureMap.Visibility.SOME);
  }

  // ------------------------------------------------------------------ the ceiling, remotely (§2)

  /**
   * A tenant list above the fold ceiling is not made literal even over a remote source: it becomes a
   * semi-join against the context relation, which is M5's key-set path — the executor ships the keys
   * in calls sized by the source's own ceiling — and no tenancy identifier is in the plan (F45).
   */
  @Test
  void a_list_above_the_ceiling_reaches_a_remote_source_as_a_key_set() throws Exception {
    RequestContext manager =
        TenancyCatalogs.manager(1, 2).toBuilder().setFoldMaxRows(1).build();
    PlannerPipeline.Result result =
        plan(TenancyCatalogs.remote(), "SELECT id FROM remote.members", manager);

    String text = result.physicalPlanText();
    // The list drives and the entitled table is looked up: the keys travel and the rows do not.
    assertThat(text).contains("ChalkLookupJoin");
    assertThat(text).contains("strategy=[JOIN_STRATEGY_LOOKUP]");
    assertThat(text).contains("ContextScan");
    // The identifiers are the host's binding, not the plan's: nothing renders them.
    assertThat(text).doesNotContain("Sarg[1, 2]");
    // And the row predicate did reach the source, as a key set in its predicate.
    assertThat(result.pushedRowPredicates()).containsExactly("remote.members");
    // The key set is in the pushed predicate, where the source will evaluate it, and the executor
    // binds the rows into its one placeholder in calls of at most `max_in_list` keys.
    assertThat(text).contains("SourceFilter(condition=[CHALK_KEY_SET_IN($1)])");
    assertThat(text).contains("max_keys_per_call=[200]");
  }

  /**
   * The same, over a source that declares no {@code IN} list at all: there is no key set to ship, so
   * the semi-join stays a local join and the flag is honestly false. The rule offers an alternative;
   * it never asks a source for a shape the source did not declare.
   */
  @Test
  void a_list_above_the_ceiling_stays_local_where_the_source_takes_no_in_list() throws Exception {
    RequestContext manager =
        TenancyCatalogs.manager(1, 2).toBuilder().setFoldMaxRows(1).build();
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remote(
                Enforcement.ENFORCEMENT_PUSHDOWN, TenancyCatalogs.withoutInLists(), false),
            "SELECT id FROM remote.members",
            manager);

    assertThat(result.physicalPlanText()).doesNotContain("ChalkLookupJoin");
    assertThat(result.pushedRowPredicates()).isEmpty();
  }

  /**
   * And under {@code LOCAL} the list never leaves either: D156's barrier is a gate rule, so no
   * predicate of the table is pushed whatever shape it has.
   */
  @Test
  void a_list_above_the_ceiling_ships_no_key_set_under_local_enforcement() throws Exception {
    RequestContext manager =
        TenancyCatalogs.manager(1, 2).toBuilder().setFoldMaxRows(1).build();
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remote(
                Enforcement.ENFORCEMENT_LOCAL, TestCatalogs.fullSqlCapabilities().build(), false),
            "SELECT id FROM remote.members",
            manager);

    assertThat(result.physicalPlanText()).doesNotContain("CHALK_KEY_SET");
    assertThat(result.physicalPlanText()).doesNotContain("ChalkLookupJoin");
    assertThat(result.pushedRowPredicates()).isEmpty();
  }

  // ------------------------------------------------------- a composite key set (F50, ADR 0027)

  /**
   * A bound list of <b>pairs</b> above the ceiling reaches the source as a key set over both
   * columns (F50). The tenancy package writes a confined subject grant as
   * {@code (id, org_id) IN (@ctx.subject_pairs)}, which decorrelates into a two-key semi-join; the
   * rule matched only one key, so the entitled table was fetched whole and
   * {@code row_predicate_pushed} was honestly false.
   */
  @Test
  void a_composite_list_above_the_ceiling_reaches_a_remote_source_as_a_key_set() throws Exception {
    RequestContext subjects =
        TenancyCatalogs.subjects(new int[][] {{1, 1}, {2, 1}, {3, 2}}).toBuilder()
            .setFoldMaxRows(1)
            .build();
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remotePairs(TenancyCatalogs.withRowValueInLists()),
            "SELECT id FROM remote.members",
            subjects);

    String text = result.physicalPlanText();
    assertThat(text).contains("ChalkLookupJoin");
    assertThat(text).contains("strategy=[JOIN_STRATEGY_LOOKUP]");
    assertThat(text).contains("ContextScan");
    // Both key columns are in the one key set, and the pair values are the host's binding: nothing
    // renders them into the plan.
    assertThat(text).contains("CHALK_KEY_SET_IN($0, $1)");
    assertThat(text).doesNotContain("Sarg");
    assertThat(result.pushedRowPredicates()).containsExactly("remote.members");

    // And the generated SQL is the row-constructor form, with one placeholder for the whole list.
    assertThat(onlyRemote(result).getQueryText())
        .contains("(\"id\", \"org_id\") IN (?)");
  }

  /**
   * The same over a source that does not declare the spelling: {@code (a, b) IN ((?, ?), …)} is not
   * a shape every engine parses, so nothing is pushed, the hash join still applies, and the flag is
   * honestly false. SQLite's profile is in exactly this position.
   */
  @Test
  void a_composite_list_stays_local_where_the_source_takes_no_row_constructor() throws Exception {
    RequestContext subjects =
        TenancyCatalogs.subjects(new int[][] {{1, 1}, {2, 1}}).toBuilder()
            .setFoldMaxRows(1)
            .build();
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remotePairs(TestCatalogs.fullSqlCapabilities().build()),
            "SELECT id FROM remote.members",
            subjects);

    assertThat(result.physicalPlanText()).doesNotContain("CHALK_KEY_SET");
    assertThat(result.physicalPlanText()).doesNotContain("ChalkLookupJoin");
    assertThat(result.pushedRowPredicates()).isEmpty();
  }

  // ------------------------- a statement's own sub-query over the entitled leaf (F72, F83)

  /**
   * A statement's own {@code IN (SELECT …)} over a <b>second occurrence</b> of the entitled table,
   * for the grant whose row predicate folds to TRUE: the outer leaf is a bare scan, and the pushed
   * text must still ask the source the membership question the statement asked.
   *
   * <p>It did not. A pushed semi-join is written as a correlated {@code EXISTS}, and the reference
   * back to the outer row was substituted for the left select list's own expression — which, being
   * written in that select's scope, is bare. The sub-query reads {@code members} too, so it exposes
   * a column of the same name, and a bare name is resolved by the sub-query's own {@code FROM}
   * first: {@code "postcode" = "t1"."postcode"} compared the inner row with itself, was true of
   * every row the inner query returned, and the semi-join stopped filtering. The whole outer table
   * came back.
   */
  @Test
  void a_membership_over_a_second_occurrence_survives_a_predicate_that_folds_to_true()
      throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remoteWithGlobalEscape(),
            "SELECT id FROM remote.members WHERE postcode IN "
                + "(SELECT m2.postcode FROM remote.members m2 WHERE m2.org_id = 1)",
            TenancyCatalogs.global());

    String sql = onlyRemote(result).getQueryText();
    // The leaf's own predicate is gone — that is what folding to TRUE means — and the statement's
    // is not.
    assertThat(sql).doesNotContain("@ctx");
    assertThat(sql).contains("\"org_id\" = 1");
    assertThat(sql).contains("EXISTS");

    // And the outer row is named by the alias its own FROM binds, so the sub-query cannot capture
    // the reference: a comparison of the inner row with itself would be a tautology.
    String outer = outerAliasOf(sql, "members");
    assertThat(outer).as("the outer leaf's bound alias in: " + sql).isNotNull();
    assertThat(sql).contains("\"" + outer + "\".\"postcode\" = ");
  }

  /**
   * The same defect from the other side: the sub-query's {@code FROM} exposes the outer row's own
   * key because it <b>joins</b> a table that has one. This is the corpus's {@code IN} and
   * {@code = ANY} spellings of a semi-join over the order lines, where the joined table carries an
   * {@code id} and the outer table's key is called {@code id} too — so the bare {@code "id"} the
   * substitution left behind bound to the wrong relation and the test became one about the inner
   * rows alone.
   */
  @Test
  void a_membership_whose_sub_query_joins_a_table_carrying_the_outer_key_survives()
      throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remoteWithGlobalEscape(),
            "SELECT id FROM remote.orders WHERE id IN "
                + "(SELECT o2.member_id FROM remote.orders o2 "
                + "JOIN remote.members m ON m.id = o2.member_id)",
            TenancyCatalogs.global());

    String sql = onlyRemote(result).getQueryText();
    assertThat(sql).contains("EXISTS");
    // The sub-query does expose an `id` of its own, which is the whole difficulty.
    assertThat(sql).contains("\"id\" FROM \"members\"");

    String outer = outerAliasOf(sql, "orders");
    assertThat(outer).as("the outer leaf's bound alias in: " + sql).isNotNull();
    assertThat(sql).contains("\"" + outer + "\".\"id\" = ");
  }

  /** The alias the generated text binds onto {@code FROM "table"}, or null where it binds none. */
  private static String outerAliasOf(String sql, String table) {
    java.util.regex.Matcher matcher =
        java.util.regex.Pattern.compile("FROM \"" + table + "\" AS \"([^\"]+)\"").matcher(sql);
    return matcher.find() ? matcher.group(1) : null;
  }

  // ------------------------------ a cross-source parent's keys (F52, §3.13, D229)

  /**
   * The parent in another source than its child: the join cannot go to one source, so the
   * <b>parent</b> drives M5's lookup and its visible keys reach the child's source as a key set.
   *
   * <p>Before F52 the child was fetched whole and joined here, with {@code row_predicate_pushed}
   * honestly false. What makes the flag true now is the same thing it means everywhere else — the
   * rows the source is asked for are the visible ones.
   */
  @Test
  void a_cross_source_parents_visible_keys_reach_the_childs_source_as_a_key_set()
      throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.throughAcrossSources(
                Enforcement.ENFORCEMENT_PUSHDOWN, TestCatalogs.fullSqlCapabilities().build()),
            "SELECT id FROM remote.messages",
            TenancyCatalogs.manager(1, 2));

    String text = result.physicalPlanText();
    assertThat(text).contains("ChalkLookupJoin");
    assertThat(text).contains("strategy=[JOIN_STRATEGY_LOOKUP]");
    assertThat(text).contains("CHALK_KEY_SET_IN($1)");
    // The parent drives: its own entitled scan, with its folded predicate, is the left input, and
    // the child's scan is what the keys are bound into.
    assertThat(text.indexOf("main, threads")).isLessThan(text.indexOf("CHALK_KEY_SET_IN"));
    assertThat(result.pushedRowPredicates()).contains("remote.messages");

    assertThat(onlyRemote(result).getQueryText())
        .isEqualTo("SELECT \"id\", \"thread_id\" FROM \"messages\" WHERE \"thread_id\" IN (?)");
  }

  /**
   * And over a source that takes no {@code IN} list there is no key set to ship: the local join
   * stands, the child is fetched whole, and the flag says so.
   */
  @Test
  void a_cross_source_parent_keeps_the_local_join_where_the_source_takes_no_in_list()
      throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.throughAcrossSources(
                Enforcement.ENFORCEMENT_PUSHDOWN, TenancyCatalogs.withoutInLists()),
            "SELECT id FROM remote.messages",
            TenancyCatalogs.manager(1, 2));

    assertThat(result.physicalPlanText()).doesNotContain("CHALK_KEY_SET");
    assertThat(result.physicalPlanText()).doesNotContain("ChalkLookupJoin");
    assertThat(result.pushedRowPredicates()).doesNotContain("remote.messages");
  }

  /**
   * And where the child's mask folds to a <b>constant</b> — an agent sees the excerpt whatever the
   * row — the mask is not a CASE above the join but a projection of the child's own row below it.
   * That projection is not a permutation, and until F54 a key set could not be pushed under one, so
   * this was the one shape of the family that still fetched the child whole (V172).
   *
   * <p>Now the key set goes to the <em>scan</em> beneath the projection and the projection stays
   * above the boundary, which is what §3.8 requires of a mask: the source is asked for the visible
   * rows and the mask is computed here, over them.
   */
  @Test
  void a_mask_that_folds_to_a_constant_still_gets_the_key_set() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.throughAcrossSources(
                Enforcement.ENFORCEMENT_PUSHDOWN, TestCatalogs.fullSqlCapabilities().build()),
            "SELECT id, content FROM remote.messages",
            TenancyCatalogs.agent(1));

    String text = result.physicalPlanText();
    assertThat(text).contains("CHALK_KEY_SET");
    assertThat(result.pushedRowPredicates()).contains("remote.messages");

    // The mask is above the boundary and the source is asked for columns alone: everything from the
    // converter down is a projection of plain references over the filtered scan.
    int boundary = text.indexOf("SourceToLocalConverter");
    assertThat(text.indexOf("SUBSTRING")).isGreaterThanOrEqualTo(0).isLessThan(boundary);
    assertThat(text.substring(boundary)).doesNotContain("SUBSTRING");
  }

  /**
   * {@code PUSHDOWN_REQUIRED} on such a child is satisfied by the exchange — and was never checked
   * at all before F52, because a child entitled through a parent has no row predicate of its own for
   * the walk to find.
   */
  @Test
  void pushdown_required_on_a_cross_source_child_is_satisfied_by_the_key_set() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.throughAcrossSources(
                Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED,
                TestCatalogs.fullSqlCapabilities().build()),
            "SELECT id FROM remote.messages",
            TenancyCatalogs.manager(1, 2));

    assertThat(result.pushedRowPredicates()).contains("remote.messages");
  }

  /** And refused, naming the shape, where the key set has nothing to travel in (F46's rule). */
  @Test
  void pushdown_required_on_a_cross_source_child_names_the_shape_it_could_not_use() {
    assertThatThrownBy(
            () ->
                plan(
                    TenancyCatalogs.throughAcrossSources(
                        Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED,
                        TenancyCatalogs.withoutInLists()),
                    "SELECT id FROM remote.messages",
                    TenancyCatalogs.manager(1, 2)))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("remote.messages")
        .hasMessageContaining("PUSHDOWN_REQUIRED")
        .hasMessageContaining("derives through 'main.threads'")
        .hasMessageContaining("does not declare PREDICATE_SHAPE_IN")
        // §3.12: never a context value in a refusal.
        .hasMessageNotContaining("1, 2");
  }

  /**
   * The same constraint on a child beside its parent in <em>one</em> source, where the mask keeps
   * the child's own projection in process and the join with it (§3.8, V160): the plan fetches the
   * child whole, and that is what the constraint refuses.
   */
  @Test
  void pushdown_required_on_a_masked_child_in_one_source_is_refused() {
    assertThatThrownBy(
            () ->
                plan(
                    TenancyCatalogs.throughInOneSource(
                        Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED),
                    "SELECT id, content FROM remote.messages",
                    TenancyCatalogs.agent(1)))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("PUSHDOWN_REQUIRED")
        .hasMessageContaining("does declare PREDICATE_SHAPE_IN");
  }

  /** Unmasked and in one source it is one remote query, which is D229's first half unchanged. */
  @Test
  void pushdown_required_on_a_child_beside_its_parent_is_satisfied_by_the_one_query()
      throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.throughInOneSource(Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED),
            "SELECT id FROM remote.messages",
            TenancyCatalogs.manager(1, 2));

    assertThat(result.pushedRowPredicates())
        .containsExactlyInAnyOrder("remote.threads", "remote.messages");
  }

  // -------------------- a parent whose visible keys are computed here (F55, §2.1, §3.13)

  /**
   * A context with an <b>open half</b> — D209's shape, or the open half of D232's partial binding —
   * makes the parent's entitled leaf a <em>local</em> subtree: every membership is a join to a
   * context table, and a context table belongs to no source. The join a {@code through} compiles
   * into then has a local driving side and a remote child, in one source or in two, and what the
   * driving side is made of is not the rule's business: the parent's visible keys are computed
   * here and travel as the key set either way.
   *
   * <p>Before F55 the rule declined a parent side that was not one source's, which under a shape is
   * every parent side, and what was left was a local join over the whole child — and, where the
   * statement joined the child to another table of that source, a plan the IR refuses (I-IR-20).
   */
  @Test
  void a_shape_ships_the_parents_visible_keys_to_a_child_in_the_same_source() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.throughInOneSource(Enforcement.ENFORCEMENT_PUSHDOWN),
            "SELECT id, content FROM remote.messages",
            TenancyCatalogs.shape());

    String text = result.physicalPlanText();
    assertThat(text).contains("ChalkLookupJoin");
    assertThat(text).contains("strategy=[JOIN_STRATEGY_LOOKUP]");
    assertThat(text).contains("CHALK_KEY_SET_IN($1)");
    // The marker joins are the driving side, above the parent's own scan and below the key set.
    assertThat(text.indexOf("ChalkContextScan")).isLessThan(text.indexOf("CHALK_KEY_SET_IN"));
    assertThat(result.pushedRowPredicates()).contains("remote.messages");

    assertThat(remoteFor(result, "messages"))
        .isEqualTo(
            "SELECT \"id\", \"thread_id\", \"content\" FROM \"messages\""
                + " WHERE \"thread_id\" IN (?)");
  }

  /** And across a boundary, where the parent is in process and only the markers are new. */
  @Test
  void a_shape_ships_them_across_a_source_boundary_too() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.throughAcrossSources(
                Enforcement.ENFORCEMENT_PUSHDOWN, TestCatalogs.fullSqlCapabilities().build()),
            "SELECT id, content FROM remote.messages",
            TenancyCatalogs.shape());

    assertThat(result.physicalPlanText()).contains("CHALK_KEY_SET_IN($1)");
    assertThat(result.pushedRowPredicates()).contains("remote.messages");
    assertThat(remoteFor(result, "messages"))
        .isEqualTo(
            "SELECT \"id\", \"thread_id\", \"content\" FROM \"messages\""
                + " WHERE \"thread_id\" IN (?)");
  }

  /**
   * A <b>partial</b> binding, which is the shape the motivating case takes: the tenancy folded into
   * the parent's own predicate and one membership still open. The key set is the same exchange; what
   * differs is that the parent's own leaf carries a literal beside its one remaining marker join.
   */
  @Test
  void a_partly_bound_parent_ships_its_keys_with_the_tenancy_folded() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.throughInOneSource(Enforcement.ENFORCEMENT_PUSHDOWN),
            "SELECT id, content FROM remote.messages",
            TenancyCatalogs.managerWithAgentShape(1));

    String text = result.physicalPlanText();
    assertThat(text).contains("CHALK_KEY_SET_IN($1)");
    // One marker join left, for the list that stayed open, and the folded one as a literal.
    assertThat(text).contains("agent_orgs");
    assertThat(text).doesNotContain("manager_orgs");
    assertThat(result.pushedRowPredicates()).contains("remote.messages");
  }

  /**
   * And with <b>nothing</b> open the rule declines exactly as it did: parent and child in one
   * source are one remote query, which is D229's first half and is better than any exchange.
   */
  @Test
  void a_folded_context_in_one_source_is_still_one_remote_query() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.throughInOneSource(Enforcement.ENFORCEMENT_PUSHDOWN),
            "SELECT id FROM remote.messages",
            TenancyCatalogs.manager(1, 2));

    assertThat(result.physicalPlanText()).doesNotContain("ChalkLookupJoin");
    assertThat(result.physicalPlanText()).doesNotContain("CHALK_KEY_SET");
    // One query, with the parent's folded predicate inside it, which is D229's first half.
    assertThat(remoteFor(result, "messages")).contains("\"org_id\" IN (1, 2)");
  }

  /**
   * The fallback, where the exchange is not available: a source that takes no {@code IN} list. The
   * child is fetched whole and joined here — and the statement's own join to another table of that
   * source is a <b>hash</b> join rather than a nested loop with a remote inner side, which is the
   * charge F51 added reaching this shape at last (see {@code JoinCallCostTest}). Before that the
   * plan was not a worse plan, it was no plan at all.
   */
  @Test
  void a_shape_over_a_source_that_takes_no_in_list_falls_back_to_a_hash_join() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.throughAcrossSources(
                Enforcement.ENFORCEMENT_PUSHDOWN, TenancyCatalogs.withoutInLists()),
            "SELECT m.id, m.content, t.member_id FROM remote.messages m"
                + " JOIN main.threads t ON t.id = m.thread_id",
            TenancyCatalogs.shape());

    String text = result.physicalPlanText();
    assertThat(text).doesNotContain("CHALK_KEY_SET");
    assertThat(text).contains("ChalkHashJoin");
    // Every nested loop left is a membership marker, whose inner side is a context table.
    assertThat(result.pushedRowPredicates()).doesNotContain("remote.messages");
  }

  /** {@code PUSHDOWN_REQUIRED} under a shape is satisfied by the exchange, as it is under a value. */
  @Test
  void pushdown_required_under_a_shape_is_satisfied_by_the_key_set() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.throughAcrossSources(
                Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED,
                TestCatalogs.fullSqlCapabilities().build()),
            "SELECT id FROM remote.messages",
            TenancyCatalogs.shape());

    assertThat(result.pushedRowPredicates()).contains("remote.messages");
  }

  /** And refused, naming the shape the source does not declare, where there is no exchange. */
  @Test
  void pushdown_required_under_a_shape_names_the_shape_it_could_not_use() {
    assertThatThrownBy(
            () ->
                plan(
                    TenancyCatalogs.throughAcrossSources(
                        Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED,
                        TenancyCatalogs.withoutInLists()),
                    "SELECT id FROM remote.messages",
                    TenancyCatalogs.shape()))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("remote.messages")
        .hasMessageContaining("PUSHDOWN_REQUIRED")
        .hasMessageContaining("derives through 'main.threads'")
        .hasMessageContaining("does not declare PREDICATE_SHAPE_IN");
  }

  // ------------------------------------------------------------------ helpers

  private static String remoteTextOf(CatalogContext catalog, String sql, RequestContext context)
      throws Exception {
    return onlyRemote(plan(catalog, sql, context)).getQueryText();
  }

  private static PlannerPipeline.Result plan(
      CatalogContext catalog, String sql, RequestContext context) throws Exception {
    RegisteredCatalog registered = new CatalogRegistry().register(catalog);
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            registered,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            BoundContext.of(context),
            PolicyOptions.DEFAULTS)) {
      return pipeline.plan(sql, true);
    }
  }

  /** The generated SQL of the one remote query that reads {@code table}. */
  private static String remoteFor(PlannerPipeline.Result result, String table) {
    List<RemoteQuery> found = new ArrayList<>();
    collect(irOf(result).getRoot(), found);
    List<String> matching =
        found.stream().map(RemoteQuery::getQueryText).filter(q -> q.contains(table)).toList();
    assertThat(matching).as("one RemoteQuery reading " + table).hasSize(1);
    return matching.get(0);
  }

  private static Plan irOf(PlannerPipeline.Result result) {
    return new RelToIr(
            new chalk.planner.types.TypeMapper(result.physical().getCluster().getTypeFactory()),
            result.physical().getCluster().getRexBuilder(),
            org.apache.calcite.rel.metadata.RelMetadataQuery.instance(),
            IrVersionGate.current())
        .toPlan(result.physical(), result.parameterRowType(), "test", 1L);
  }

  private static RemoteQuery onlyRemote(PlannerPipeline.Result result) {
    Plan plan =
        new RelToIr(
                new chalk.planner.types.TypeMapper(result.physical().getCluster().getTypeFactory()),
                result.physical().getCluster().getRexBuilder(),
                org.apache.calcite.rel.metadata.RelMetadataQuery.instance(),
                IrVersionGate.current())
            .toPlan(result.physical(), result.parameterRowType(), "test", 1L);
    List<RemoteQuery> found = new ArrayList<>();
    collect(plan.getRoot(), found);
    assertThat(found).as("exactly one RemoteQuery").hasSize(1);
    return found.get(0);
  }

  private static void collect(Rel rel, List<RemoteQuery> into) {
    if (rel.getKindCase() == Rel.KindCase.REMOTE_QUERY) {
      into.add(rel.getRemoteQuery());
      return;
    }
    // A lookup join holds its two sides outside the ordinary input list, and the one that matters
    // here — the query the keys are bound into — is the right one (F45, F50).
    if (rel.getKindCase() == Rel.KindCase.LOOKUP_JOIN) {
      collect(rel.getLookupJoin().getDriving(), into);
      collect(rel.getLookupJoin().getLookup(), into);
      return;
    }
    for (Rel input : IrNodes.inputs(rel)) {
      collect(input, into);
    }
  }
}
