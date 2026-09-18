package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.CrossSourceJoinPolicy;
import chalk.ir.v1.JoinStrategy;
import chalk.ir.v1.SourcePairRule;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.JoinPolicy;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import com.google.common.collect.ImmutableList;
import org.junit.jupiter.api.Test;

/**
 * The cross-source join strategies (D103, {@code 20-m5-federation.md} §1 and §2): what the rule
 * enumerates for a pair of sources, what the policy allows, and what cost then chooses.
 */
class CrossSourceJoinRuleTest {

  private static String plan(String sql) {
    return plan(sql, CrossSourceJoinPolicy.getDefaultInstance());
  }

  private static String plan(String sql, CrossSourceJoinPolicy policy) {
    RegisteredCatalog catalog =
        new CatalogRegistry()
            .register(
                TestCatalogs.withRemote(
                    "db", TestCatalogs.fullSqlCapabilities().setSupportsValuesJoin(true).build(),
                    TestCatalogs.duckDbProfile()));
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            catalog,
            PushdownPolicy.full(),
            SqlConfigs.DEFAULT_CONFORMANCE,
            ImmutableList.of(),
            JoinPolicy.of(CrossSourceJoinPolicy.getDefaultInstance(), policy))) {
      return pipeline.plan(sql, true).physicalPlanText();
    } catch (Exception failure) {
      throw new AssertionError("planning failed for: " + sql, failure);
    }
  }

  private static CrossSourceJoinPolicy pair(SourcePairRule.Builder rule) {
    return CrossSourceJoinPolicy.newBuilder().addPairs(rule).build();
  }

  /** The whole point: the smaller side drives and the larger one is asked for its keys. */
  @Test
  public void a_measured_small_side_drives_a_lookup() {
    String plan =
        plan(
            "SELECT c.c_name, o.o_orderkey FROM main.customer c"
                + " JOIN db.orders o ON o.o_custkey = c.c_custkey");

    assertThat(plan).contains("ChalkLookupJoin");
    assertThat(plan).contains("CHALK_KEY_SET");
  }

  /**
   * D97: when the small side's estimate is a guess rather than a measured statistic — which a filter
   * makes it — the planner emits an adaptive join instead, and the branch is chosen at execution.
   */
  @Test
  public void a_guessed_small_side_gets_an_adaptive_join() {
    String plan =
        plan(
            "SELECT c.c_name, o.o_orderkey FROM"
                + " (SELECT c_custkey, c_name FROM main.customer WHERE c_mktsegment = 'BUILDING') c"
                + " JOIN db.orders o ON o.o_custkey = c.c_custkey");

    assertThat(plan).contains("ChalkAdaptiveJoin");
    assertThat(plan).contains("strategy=[ADAPTIVE]");
  }

  /**
   * §1's fourth strategy: the small side's rows are shipped as VALUES and the source does the join,
   * in one call rather than one per batch of keys. Cost prefers it here without being told to —
   * fifteen hundred keys against a two-hundred-key ceiling is eight calls, and one call plus fifteen
   * hundred shipped values is cheaper.
   */
  @Test
  public void broadcast_ships_the_small_side_as_rows() {
    String plan =
        plan(
            "SELECT c.c_name, o.o_orderkey FROM main.customer c"
                + " JOIN db.orders o ON o.o_custkey = c.c_custkey");

    assertThat(plan).contains("ChalkLookupJoin");
    assertThat(plan).contains("strategy=[JOIN_STRATEGY_BROADCAST]");
    assertThat(plan).contains("CHALK_KEY_SET_ROWS");
  }

  /**
   * The same shape with the ceiling raised: one call carries every key, so there is nothing for a
   * broadcast to save and the ordinary lookup wins.
   */
  @Test
  public void a_generous_in_list_ceiling_makes_a_plain_lookup_the_cheaper_one() {
    RegisteredCatalog catalog =
        new CatalogRegistry()
            .register(
                TestCatalogs.withRemote(
                    "db",
                    TestCatalogs.fullSqlCapabilities()
                        .setMaxInList(5000)
                        .setSupportsValuesJoin(true)
                        .build(),
                    TestCatalogs.duckDbProfile()));
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      String plan =
          pipeline
              .plan(
                  "SELECT c.c_name, o.o_orderkey FROM main.customer c"
                      + " JOIN db.orders o ON o.o_custkey = c.c_custkey",
                  true)
              .physicalPlanText();
      assertThat(plan).contains("strategy=[JOIN_STRATEGY_LOOKUP]");
      assertThat(plan).contains("CHALK_KEY_SET_IN");
    } catch (Exception failure) {
      throw new AssertionError(failure);
    }
  }

  /** A pair rule that allows only LOCAL leaves the join where M4 left it. */
  @Test
  public void a_policy_that_forbids_lookup_leaves_a_local_join() {
    String plan =
        plan(
            "SELECT c.c_name, o.o_orderkey FROM main.customer c"
                + " JOIN db.orders o ON o.o_custkey = c.c_custkey",
            pair(
                SourcePairRule.newBuilder()
                    .setLeftSource("mem")
                    .setRightSource("db")
                    .addAllowed(JoinStrategy.JOIN_STRATEGY_LOCAL)));

    assertThat(plan).doesNotContain("ChalkLookupJoin");
    assertThat(plan).doesNotContain("ChalkAdaptiveJoin");
    assertThat(plan).contains("SourceToLocalConverter");
  }

  /** A rule whose pair does not match this one says nothing about it. */
  @Test
  public void a_pair_rule_for_another_pair_is_ignored() {
    String plan =
        plan(
            "SELECT c.c_name, o.o_orderkey FROM main.customer c"
                + " JOIN db.orders o ON o.o_custkey = c.c_custkey",
            pair(
                SourcePairRule.newBuilder()
                    .setLeftSource("somewhere")
                    .setRightSource("else")
                    .addAllowed(JoinStrategy.JOIN_STRATEGY_LOCAL)));

    assertThat(plan).contains("ChalkLookupJoin");
  }

  /** A same-source join is still one query the source runs: this rule must not touch it. */
  @Test
  public void a_same_source_join_is_untouched() {
    String plan =
        plan(
            "SELECT c.c_name, o.o_orderkey FROM db.customer c"
                + " JOIN db.orders o ON o.o_custkey = c.c_custkey");

    assertThat(plan).contains("SourceJoin");
    assertThat(plan).doesNotContain("ChalkLookupJoin");
  }

  /**
   * The guardrail (D104): a policy that caps what a local join may fetch refuses the plan naming
   * the join and the estimate, rather than running it.
   */
  @Test
  public void the_local_join_guardrail_refuses_a_plan_that_would_fetch_too_much() {
    CrossSourceJoinPolicy policy =
        CrossSourceJoinPolicy.newBuilder()
            .setLocalJoinMaxRows(100)
            .addPairs(
                SourcePairRule.newBuilder().addAllowed(JoinStrategy.JOIN_STRATEGY_LOCAL))
            .build();

    Throwable failure =
        org.assertj.core.api.Assertions.catchThrowable(
            () ->
                plan(
                    "SELECT c.c_name, o.o_orderkey FROM main.customer c"
                        + " JOIN db.orders o ON o.o_custkey = c.c_custkey",
                    policy));

    assertThat(failure).hasRootCauseInstanceOf(UnsupportedFeatureException.class);
    assertThat(failure.getCause()).hasMessageContaining("local_join_max_rows");
    assertThat(failure.getCause()).hasMessageContaining("fetched rows");
  }

  /**
   * A source that accepts no IN list has nothing to look anything up in, whatever the policy says.
   */
  @Test
  public void a_source_with_no_in_list_ceiling_gets_a_local_join() {
    RegisteredCatalog catalog =
        new CatalogRegistry()
            .register(
                TestCatalogs.withRemote(
                    "db",
                    TestCatalogs.fullSqlCapabilities().setMaxInList(0).build(),
                    TestCatalogs.duckDbProfile()));
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      String plan =
          pipeline
              .plan(
                  "SELECT c.c_name, o.o_orderkey FROM main.customer c"
                      + " JOIN db.orders o ON o.o_custkey = c.c_custkey",
                  true)
              .physicalPlanText();
      assertThat(plan).doesNotContain("ChalkLookupJoin");
    } catch (Exception failure) {
      throw new AssertionError(failure);
    }
  }
}
