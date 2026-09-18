package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.RuleSets;
import chalk.planner.rpc.v1.RequestContext;
import java.util.List;
import org.apache.calcite.rel.rules.CoreRules;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * {@code AGGREGATE_CASE_TO_FILTER} in the Hep pre-pass (ADR 0058 §4;
 * docs/design/03-planner.md §4.2).
 *
 * <p>{@code SUM(CASE WHEN c THEN x ELSE NULL END)} and {@code SUM(x) FILTER (WHERE c)} are the same
 * number — a NULL contributes nothing to either — and the second is the one the IR carries and the
 * executor runs, with the argument read once per row that is counted rather than per row of the
 * input. The rule is in the pinned Calcite and was not in Chalk's lists.
 *
 * <p>What matters beside the rewrite is what it leaves alone: a sanitiser whose other arm is a
 * <em>mask</em> is not this shape, because a mask is a value the row contributes and not an absence,
 * and turning it into a FILTER would drop rows from the population.
 */
class AggregateCaseToFilterTest {
  private static RegisteredCatalog corpus;
  private static RegisteredCatalog tenancy;

  @BeforeAll
  static void register() {
    corpus = new CatalogRegistry().register(TestCatalogs.declared());
    tenancy = new CatalogRegistry().register(TenancyCatalogs.catalog());
  }

  /** The rule is in the collection the hash reads, which is what makes it configuration (F76). */
  @Test
  void the_rule_is_in_the_hep_pre_pass() {
    assertThat(RuleSets.hep()).contains(CoreRules.AGGREGATE_CASE_TO_FILTER);
  }

  @Test
  void a_sum_over_a_case_with_a_null_arm_becomes_a_filtered_sum() throws Exception {
    String plan =
        plan(
            corpus,
            "SELECT SUM(CASE WHEN region = 'east' THEN amount ELSE NULL END) AS s FROM sales");

    assertThat(plan).contains("FILTER");
    // The argument is the column itself now, not a per-row CASE the executor evaluates and throws
    // away: nothing above the aggregate rebuilds one.
    assertThat(plan).doesNotContain("CASE(=($1, 'east'), $2, null");
  }

  /** The same for a count, whose absent arm the rule also recognises. */
  @Test
  void a_count_over_a_case_with_a_null_arm_becomes_a_filtered_count() throws Exception {
    String plan =
        plan(
            corpus,
            "SELECT region, COUNT(CASE WHEN amount > 10 THEN amount ELSE NULL END) AS n "
                + "FROM sales GROUP BY region");

    assertThat(plan).contains("FILTER");
  }

  /**
   * And a mask in the other arm is left alone. {@code -1} is a value every row contributes, so the
   * sum over it is not the sum over the rows the condition admits, and the rule does not fire.
   */
  @Test
  void a_case_whose_other_arm_is_a_value_is_left_alone() throws Exception {
    String plan =
        plan(
            corpus,
            "SELECT SUM(CASE WHEN region = 'east' THEN amount ELSE -1 END) AS s FROM sales");

    assertThat(plan).doesNotContain("FILTER");
  }

  /**
   * The entitlement case the rule was measured against: a population aggregate over a column the
   * leaf sanitised, whose sanitiser is a {@code CASE} with the placeholder in the other arm. For
   * this principal the rules fold and no {@code CASE} is left for the rule to meet at all, which is
   * the fold doing its own work — and the point of the measurement is that the entitled plans do
   * not move.
   */
  @Test
  void a_guarded_population_aggregate_over_a_sanitised_column_still_plans() throws Exception {
    String plan =
        plan(tenancy, TenancyCatalogs.auditor(1), "SELECT SUM(amount) AS s FROM orders");

    assertThat(plan).contains("orders");
  }

  private static String plan(RegisteredCatalog catalog, String sql) throws Exception {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.none())) {
      return pipeline.plan(sql, true).physicalPlanText();
    }
  }

  private static String plan(RegisteredCatalog catalog, RequestContext context, String sql)
      throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            catalog,
            PushdownPolicy.none(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            BoundContext.of(context),
            PolicyOptions.DEFAULTS)) {
      return pipeline.plan(sql, true).physicalPlanText();
    }
  }
}
