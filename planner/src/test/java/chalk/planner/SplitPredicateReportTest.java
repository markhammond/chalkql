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
 * F146: {@code row_predicate_pushed} is the row predicate's own conjuncts reaching the source, not
 * any filter the source runs over the table. A statement's conjunct that travels beside a
 * membership kept at home — because the source takes no IN list, or because a pair rule keeps the
 * key set here — is not the predicate pushed, and {@code PUSHDOWN_REQUIRED}, which trusts the same
 * set, refuses the plan as it refuses the bare one.
 */
class SplitPredicateReportTest {
  private static final String WITH_A_CONJUNCT = "SELECT id FROM remote.members WHERE postcode = '2000'";

  /** "Nothing may look up into pg": the empty left side matches every driving side. */
  private static final JoinPolicy NOTHING_INTO_PG =
      JoinPolicy.of(
          CrossSourceJoinPolicy.newBuilder()
              .addPairs(
                  SourcePairRule.newBuilder()
                      .setLeftSource("")
                      .setRightSource("pg")
                      .addAllowed(JoinStrategy.JOIN_STRATEGY_LOCAL))
              .build());

  @Test
  void the_membership_pushed_beside_the_statements_conjunct_is_reported_pushed() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remote(
                Enforcement.ENFORCEMENT_PUSHDOWN, TestCatalogs.fullSqlCapabilities().build(), false),
            WITH_A_CONJUNCT,
            TenancyCatalogs.manager(1, 2),
            JoinPolicy.DEFAULT);

    assertThat(result.pushedRowPredicates()).containsExactly("remote.members");
  }

  @Test
  void a_travelling_conjunct_does_not_report_a_kept_membership_as_pushed() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remote(
                Enforcement.ENFORCEMENT_PUSHDOWN, TenancyCatalogs.withoutInLists(), false),
            WITH_A_CONJUNCT,
            TenancyCatalogs.manager(1, 2),
            JoinPolicy.DEFAULT);

    assertThat(result.pushedRowPredicates()).isEmpty();
  }

  @Test
  void pushdown_required_refuses_a_kept_membership_whatever_travels_beside_it() {
    assertThatThrownBy(
            () ->
                plan(
                    TenancyCatalogs.remote(
                        Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED,
                        TenancyCatalogs.withoutInLists(),
                        false),
                    WITH_A_CONJUNCT,
                    TenancyCatalogs.manager(1, 2),
                    JoinPolicy.DEFAULT))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("remote.members")
        .hasMessageContaining("PUSHDOWN_REQUIRED");
  }

  /**
   * Cost may keep a membership home on its own: with the statement's conjunct pushed the remote side
   * is small, and a local semi-join against the context relation beats shipping the key set. Nothing
   * of the membership reached the source, and the report says so — where any filter used to count.
   */
  @Test
  void a_membership_cost_kept_home_beside_a_travelling_conjunct_is_not_reported_pushed()
      throws Exception {
    PlannerPipeline.Result result =
        plan(TenancyCatalogs.remote(), WITH_A_CONJUNCT, aboveTheCeiling(), JoinPolicy.DEFAULT);

    assertThat(result.physicalPlanText()).doesNotContain("CHALK_KEY_SET");
    assertThat(result.physicalPlanText()).contains("joinType=[semi]");
    assertThat(result.pushedRowPredicates()).isEmpty();
  }

  @Test
  void a_key_set_kept_home_by_a_pair_rule_is_not_reported_pushed_when_a_conjunct_travels()
      throws Exception {
    PlannerPipeline.Result result =
        plan(TenancyCatalogs.remote(), WITH_A_CONJUNCT, aboveTheCeiling(), NOTHING_INTO_PG);

    assertThat(result.physicalPlanText()).doesNotContain("CHALK_KEY_SET");
    assertThat(result.pushedRowPredicates()).isEmpty();
  }

  @Test
  void pushdown_required_refuses_the_kept_key_set_whatever_travels_beside_it() {
    assertThatThrownBy(
            () ->
                plan(
                    TenancyCatalogs.remote(
                        Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED,
                        TestCatalogs.fullSqlCapabilities().build(),
                        false),
                    WITH_A_CONJUNCT,
                    aboveTheCeiling(),
                    NOTHING_INTO_PG))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("remote.members")
        .hasMessageContaining("PUSHDOWN_REQUIRED");
  }

  /**
   * F147: under PUSHDOWN_REQUIRED cost is not free to keep the membership home. The boundary that
   * would not carry the predicate is charged, so the key set is preferred over the local semi-join
   * that wins under plain PUSHDOWN, the report says pushed, and nothing is refused.
   */
  @Test
  void pushdown_required_makes_cost_prefer_the_key_set_over_a_local_semi_join() throws Exception {
    PlannerPipeline.Result result =
        plan(
            TenancyCatalogs.remote(
                Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED,
                TestCatalogs.fullSqlCapabilities().build(),
                false),
            WITH_A_CONJUNCT,
            aboveTheCeiling(),
            JoinPolicy.DEFAULT);

    assertThat(result.physicalPlanText()).contains("CHALK_KEY_SET_IN");
    assertThat(result.pushedRowPredicates()).containsExactly("remote.members");
  }

  private static RequestContext aboveTheCeiling() {
    return TenancyCatalogs.manager(1, 2).toBuilder().setFoldMaxRows(1).build();
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
