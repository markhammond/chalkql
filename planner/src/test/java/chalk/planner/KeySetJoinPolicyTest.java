package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.CrossSourceJoinPolicy;
import chalk.ir.v1.Enforcement;
import chalk.ir.v1.JoinStrategy;
import chalk.ir.v1.SourcePairRule;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.PolicyException;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.JoinPolicy;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.RequestContext;
import java.util.List;
import org.junit.jupiter.api.Test;

/**
 * F139: the two entitlement exchanges honour a pair rule of the join policy.
 *
 * <p>{@code ParentKeySetRule} read the policy for {@code lookup_max_calls} alone and never asked
 * {@code allowed(left, right)}, and {@code ContextKeySetRule} did not take the policy at all — so a
 * host that had forbidden every look-up into a source could still have the entitlement pass send a
 * principal's memberships, or a parent's visible keys, there. Each exchange is a {@code LOOKUP} by
 * every other measure and the plan text shows it as one, so the pair rule binds it: where
 * {@code LOOKUP} is not allowed for the pair, the join stays here and the report says so.
 */
class KeySetJoinPolicyTest {

  /** "Nothing may look up into pg": the empty left side matches every driving side. */
  private static final JoinPolicy NOTHING_INTO_PG =
      policy("", "pg", JoinStrategy.JOIN_STRATEGY_LOCAL);

  // ------------------------------------------------------------------ the context key set

  /** The baseline the rule is measured against: with no pair rule the list travels as a key set. */
  @Test
  void a_list_above_the_ceiling_travels_as_a_key_set_under_the_shipped_policy() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remote(),
            "SELECT id FROM remote.members",
            aboveTheCeiling(),
            JoinPolicy.DEFAULT);

    assertThat(result.physicalPlanText()).contains("ChalkLookupJoin");
    assertThat(result.physicalPlanText()).contains("CHALK_KEY_SET_IN");
    assertThat(result.pushedRowPredicates()).containsExactly("remote.members");
  }

  /**
   * A pair rule forbidding every look-up into the table's source keeps the semi-join against the
   * context relation here: no key set, and the flag honestly false.
   */
  @Test
  void a_pair_rule_forbidding_lookups_into_the_source_keeps_the_context_key_set_home()
      throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remote(),
            "SELECT id FROM remote.members",
            aboveTheCeiling(),
            NOTHING_INTO_PG);

    assertThat(result.physicalPlanText()).doesNotContain("ChalkLookupJoin");
    assertThat(result.physicalPlanText()).doesNotContain("CHALK_KEY_SET");
    assertThat(result.pushedRowPredicates()).isEmpty();
  }

  /** A rule about another source changes nothing here. */
  @Test
  void a_pair_rule_about_another_source_leaves_the_context_key_set_alone() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remote(),
            "SELECT id FROM remote.members",
            aboveTheCeiling(),
            policy("", "elsewhere", JoinStrategy.JOIN_STRATEGY_LOCAL));

    assertThat(result.physicalPlanText()).contains("CHALK_KEY_SET_IN");
    assertThat(result.pushedRowPredicates()).containsExactly("remote.members");
  }

  /**
   * And a rule that names a left source cannot match the context, which belongs to no source: the
   * context is asked about as the empty id, and only a rule whose left side is empty — "anything" —
   * reaches it.
   */
  @Test
  void a_pair_rule_naming_a_left_source_does_not_reach_the_context() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remote(),
            "SELECT id FROM remote.members",
            aboveTheCeiling(),
            policy("mem", "pg", JoinStrategy.JOIN_STRATEGY_LOCAL));

    assertThat(result.physicalPlanText()).contains("CHALK_KEY_SET_IN");
  }

  /**
   * Under {@code PUSHDOWN_REQUIRED} the forbidden exchange is refused at prepare, and the refusal
   * names the setting that stopped it rather than a shape the source does declare.
   */
  @Test
  void pushdown_required_names_the_pair_rule_that_kept_the_context_key_set_home() {
    assertThatThrownBy(
            () ->
                plan(
                    TenancyCatalogs.remote(
                        Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED,
                        TestCatalogs.fullSqlCapabilities().build(),
                        false),
                    "SELECT id FROM remote.members",
                    aboveTheCeiling(),
                    NOTHING_INTO_PG))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("remote.members")
        .hasMessageContaining("PUSHDOWN_REQUIRED")
        .hasMessageContaining("the join policy forbids a LOOKUP into the source 'pg'")
        .hasMessageContaining("PrepareOptions.JoinPolicy")
        // §3.12: never a context value in a refusal.
        .hasMessageNotContaining("1, 2");
  }

  // ------------------------------------------------------------------ the parent's visible keys

  /** The baseline: a cross-source parent's keys reach the child's source as a key set. */
  @Test
  void a_cross_source_parents_keys_travel_under_the_shipped_policy() throws Exception {
    PlannerPipeline.Result result =
        plan(
            acrossSources(Enforcement.ENFORCEMENT_PUSHDOWN),
            "SELECT id FROM remote.messages",
            TenancyCatalogs.manager(1, 2),
            JoinPolicy.DEFAULT);

    assertThat(result.physicalPlanText()).contains("CHALK_KEY_SET_IN");
    assertThat(result.pushedRowPredicates()).contains("remote.messages");
  }

  /**
   * A rule forbidding look-ups from the parent's source into the child's keeps the join here: the
   * child is fetched whole and joined in process, and {@code row_predicate_pushed} is false.
   */
  @Test
  void a_pair_rule_forbidding_the_pair_keeps_the_parents_keys_home() throws Exception {
    PlannerPipeline.Result result =
        plan(
            acrossSources(Enforcement.ENFORCEMENT_PUSHDOWN),
            "SELECT id FROM remote.messages",
            TenancyCatalogs.manager(1, 2),
            policy("mem", "pg", JoinStrategy.JOIN_STRATEGY_LOCAL));

    assertThat(result.physicalPlanText()).doesNotContain("ChalkLookupJoin");
    assertThat(result.physicalPlanText()).doesNotContain("CHALK_KEY_SET");
    assertThat(result.pushedRowPredicates()).doesNotContain("remote.messages");
  }

  /** And so does the rule that forbids every look-up into the child's source. */
  @Test
  void a_pair_rule_forbidding_everything_into_the_source_keeps_them_home_too() throws Exception {
    PlannerPipeline.Result result =
        plan(
            acrossSources(Enforcement.ENFORCEMENT_PUSHDOWN),
            "SELECT id FROM remote.messages",
            TenancyCatalogs.manager(1, 2),
            NOTHING_INTO_PG);

    assertThat(result.physicalPlanText()).doesNotContain("CHALK_KEY_SET");
    assertThat(result.pushedRowPredicates()).doesNotContain("remote.messages");
  }

  /** A rule that allows the lookup, or one about another pair, leaves the exchange as it was. */
  @Test
  void a_pair_rule_allowing_lookup_leaves_the_exchange() throws Exception {
    for (JoinPolicy allowing :
        List.of(
            policy("mem", "pg", JoinStrategy.JOIN_STRATEGY_LOOKUP),
            policy("pg", "mem", JoinStrategy.JOIN_STRATEGY_LOCAL))) {
      PlannerPipeline.Result result =
          plan(
              acrossSources(Enforcement.ENFORCEMENT_PUSHDOWN),
              "SELECT id FROM remote.messages",
              TenancyCatalogs.manager(1, 2),
              allowing);

      assertThat(result.physicalPlanText()).as(allowing.toString()).contains("CHALK_KEY_SET_IN");
      assertThat(result.pushedRowPredicates()).contains("remote.messages");
    }
  }

  /**
   * Under a shape the parent side is not one source's — its memberships are joins to context
   * relations (F55) — and every source its keys are computed from is asked: the parent's own
   * source here, forbidden by name.
   */
  @Test
  void under_a_shape_the_parents_own_source_is_asked_too() throws Exception {
    PlannerPipeline.Result allowed =
        plan(
            acrossSources(Enforcement.ENFORCEMENT_PUSHDOWN),
            "SELECT id FROM remote.messages",
            TenancyCatalogs.shape(),
            JoinPolicy.DEFAULT);
    assertThat(allowed.physicalPlanText()).contains("CHALK_KEY_SET_IN");

    PlannerPipeline.Result forbidden =
        plan(
            acrossSources(Enforcement.ENFORCEMENT_PUSHDOWN),
            "SELECT id FROM remote.messages",
            TenancyCatalogs.shape(),
            policy("mem", "pg", JoinStrategy.JOIN_STRATEGY_LOCAL));
    assertThat(forbidden.physicalPlanText()).doesNotContain("CHALK_KEY_SET");
    assertThat(forbidden.pushedRowPredicates()).doesNotContain("remote.messages");
  }

  /**
   * Under {@code PUSHDOWN_REQUIRED} the child fetched whole is refused, naming the pair rule and
   * the two sources, and the way out.
   */
  @Test
  void pushdown_required_names_the_pair_rule_that_kept_the_parents_keys_home() {
    assertThatThrownBy(
            () ->
                plan(
                    acrossSources(Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED),
                    "SELECT id FROM remote.messages",
                    TenancyCatalogs.manager(1, 2),
                    policy("mem", "pg", JoinStrategy.JOIN_STRATEGY_LOCAL)))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("remote.messages")
        .hasMessageContaining("PUSHDOWN_REQUIRED")
        .hasMessageContaining("derives through 'main.threads'")
        .hasMessageContaining("the join policy forbids a LOOKUP from 'mem' into 'pg'")
        .hasMessageContaining("PrepareOptions.JoinPolicy");
  }

  // ------------------------------------------------------------------ helpers

  /** A manager of two organisations with a fold ceiling of one, so the list stays a relation. */
  private static RequestContext aboveTheCeiling() {
    return TenancyCatalogs.manager(1, 2).toBuilder().setFoldMaxRows(1).build();
  }

  private static CatalogContext acrossSources(Enforcement enforcement) {
    return TenancyCatalogs.throughAcrossSources(
        enforcement, TestCatalogs.fullSqlCapabilities().build());
  }

  private static JoinPolicy policy(String left, String right, JoinStrategy allowed) {
    return JoinPolicy.of(
        CrossSourceJoinPolicy.newBuilder()
            .addPairs(
                SourcePairRule.newBuilder()
                    .setLeftSource(left)
                    .setRightSource(right)
                    .addAllowed(allowed))
            .build());
  }

  private static PlannerPipeline.Result plan(
      CatalogContext catalog, String sql, RequestContext context, JoinPolicy joinPolicy)
      throws Exception {
    RegisteredCatalog registered = new CatalogRegistry().register(catalog);
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            registered,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            joinPolicy,
            BoundContext.of(context),
            PolicyOptions.DEFAULTS)) {
      return pipeline.plan(sql, true);
    }
  }
}
