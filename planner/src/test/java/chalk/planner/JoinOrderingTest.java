package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.JoinOrdering;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import java.util.LinkedHashSet;
import java.util.Set;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * Join ordering (D44): Volcano enumerates orders up to {@link JoinOrdering#VOLCANO_MAX_JOINS}, the
 * Hep {@code MultiJoin} pass fixes one beyond it, and either way the answer is the same every time.
 */
class JoinOrderingTest {
  private static RegisteredCatalog catalog;

  @BeforeAll
  static void registerCatalog() {
    catalog = new CatalogRegistry().register(TestCatalogs.corpus());
  }

  /** The threshold is a constant a reader can find, and it is in the config hash. */
  @Test
  void the_threshold_is_named_and_hashed() {
    assertThat(JoinOrdering.VOLCANO_MAX_JOINS).isEqualTo(4);
    assertThat(JoinOrdering.summary()).contains("volcanoMaxJoins=4");
  }

  /** Three tables — two joins — stay on the exhaustive path and are ordered by cost. */
  @Test
  void a_small_query_is_ordered_by_volcano() throws Exception {
    String plan = plan(query("14_tpch_q3"));

    assertThat(plan).contains("ChalkHashJoin");
    assertThat(plan).doesNotContain("MultiJoin");
  }

  /**
   * Six tables are past the threshold, so {@code LoptOptimizeJoinRule} chooses a left-deep order
   * from the M2 statistics before Volcano sees them. The evidence that it ran is that the query
   * plans at all: with the join-order rules still active it never finished (ADR 0016).
   */
  @Test
  void a_six_table_query_is_ordered_heuristically_and_plans() throws Exception {
    String plan = plan(query("15_tpch_q5"));

    assertThat(plan).contains("ChalkHashJoin");
    assertThat(plan).doesNotContain("MultiJoin");
    assertThat(plan.lines().filter(line -> line.contains("Join")).count()).isEqualTo(5);
  }

  /**
   * The same query planned a hundred times is the same plan a hundred times (§6). Volcano's search
   * is driven by rule order and cost, both of which are fixed, but a hash iteration leaking in would
   * show up here and nowhere else.
   */
  @Test
  void the_six_table_plan_is_the_same_every_time() throws Exception {
    Set<String> plans = new LinkedHashSet<>();
    String sql = query("15_tpch_q5");
    for (int i = 0; i < 100; i++) {
      plans.add(plan(sql));
    }

    assertThat(plans).hasSize(1);
  }

  @Test
  void the_three_table_plan_is_the_same_every_time() throws Exception {
    Set<String> plans = new LinkedHashSet<>();
    String sql = query("14_tpch_q3");
    for (int i = 0; i < 100; i++) {
      plans.add(plan(sql));
    }

    assertThat(plans).hasSize(1);
  }

  private static String query(String name) {
    return CorpusQueries.m3().stream()
        .filter(q -> q.name().equals(name))
        .findFirst()
        .orElseThrow(() -> new IllegalStateException("no corpus query " + name))
        .sql();
  }

  private static String plan(String sql) throws Exception {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      // The cost annotations carry rel ids, which are per-request; the shape is what is compared.
      return PlannerPipeline.explain(pipeline.plan(sql, false).physical());
    }
  }
}
