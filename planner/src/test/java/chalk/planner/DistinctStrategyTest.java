package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.diag.PlanText;
import chalk.planner.plan.DistinctStrategy;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.rel.RelNode;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * D54 — which of the two correct strategies a {@code DISTINCT} aggregate gets
 * ({@code 13-window-functions.md} §5), on both sides of the threshold.
 *
 * <p>The estimate is the number of distinct (group, value) pairs the executor's native pass would
 * have to hold: {@code distinct_count(column) × groups}, bounded by the input's rows, summed over
 * the distinct measures. Under the threshold the aggregate keeps its distinct measures and one
 * {@code HashAggregate} does the work; over it, Calcite's join expansion runs instead.
 */
class DistinctStrategyTest {
  private static RegisteredCatalog catalog;
  private static CorpusPlanner planner;

  @BeforeAll
  static void registerCatalog() {
    catalog = new CatalogRegistry().register(TestCatalogs.corpus());
    planner = new CorpusPlanner();
  }

  private static String plan(String sql) {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      return PlanText.withAttributes(pipeline.plan(sql, false).physical());
    } catch (Exception e) {
      throw new IllegalStateException("planning failed for: " + sql, e);
    }
  }

  /**
   * {@code symbol} has five distinct values in a five-group aggregate, so the native pass holds
   * twenty-five values at most and wins by a wide margin. This is M1 corpus query 11, whose plan
   * D54 changes from two nested aggregates to one.
   */
  @Test
  void a_small_distinct_set_stays_native() {
    String text = plan("SELECT COUNT(DISTINCT symbol) AS n FROM bars");

    assertThat(text).contains("COUNT(DISTINCT $0)");
    assertThat(countAggregates(text)).isEqualTo(1);
  }

  /**
   * {@code trade_count} carries no declared distinct count — the POCO source measures only the
   * columns an index or a collation leads with — so the bound is the input's 100 800 rows, over the
   * 100 000 threshold, and the join expansion runs. This is corpus query 19.
   */
  @Test
  void an_unmeasured_column_over_a_large_table_expands() {
    String text =
        plan("SELECT symbol, COUNT(DISTINCT trade_count), SUM(volume) FROM bars GROUP BY symbol");

    assertThat(text).doesNotContain("DISTINCT");
    assertThat(countAggregates(text)).isGreaterThan(1);
  }

  /** Whichever branch it takes, the answer is one grouping: the IR has no grouping sets. */
  @Test
  void neither_branch_produces_grouping_sets() {
    for (String sql :
        List.of(
            "SELECT COUNT(DISTINCT symbol) AS n FROM bars",
            "SELECT symbol, COUNT(DISTINCT trade_count), SUM(volume) FROM bars GROUP BY symbol",
            "SELECT l_orderkey, COUNT(DISTINCT l_partkey), COUNT(DISTINCT l_suppkey), "
                + "SUM(l_quantity) FROM lineitem GROUP BY l_orderkey")) {
      Plan ir = planner.plan(sql);
      assertThat(groupings(ir.getRoot()))
          .as("%s must produce one grouping per aggregate", sql)
          .allMatch(count -> count == 1);
    }
  }

  /** The threshold is a number, and the choice moves when it does. */
  @Test
  void the_threshold_decides_and_moves_the_choice() {
    RelNode tree =
        aggregateOf(
            "SELECT symbol, COUNT(DISTINCT trade_count), SUM(volume) FROM bars GROUP BY symbol");

    assertThat(DistinctStrategy.of(tree, 1_000_000L)).isEqualTo(DistinctStrategy.NATIVE);
    assertThat(DistinctStrategy.of(tree, 100L)).isEqualTo(DistinctStrategy.JOIN);
    assertThat(DistinctStrategy.of(tree)).isEqualTo(DistinctStrategy.JOIN);
  }

  /** A tree with no distinct aggregate at all is native, which costs nothing either way. */
  @Test
  void a_tree_with_no_distinct_aggregate_is_native() {
    assertThat(DistinctStrategy.of(aggregateOf("SELECT symbol, SUM(volume) FROM bars GROUP BY symbol")))
        .isEqualTo(DistinctStrategy.NATIVE);
  }

  /** A small measured set stays native even beside a plain aggregate — the shape M1 could not plan. */
  @Test
  void a_measured_column_beside_a_plain_aggregate_stays_native() {
    RelNode tree = aggregateOf("SELECT COUNT(DISTINCT symbol), SUM(volume) FROM bars");
    assertThat(DistinctStrategy.of(tree)).isEqualTo(DistinctStrategy.NATIVE);

    String text = plan("SELECT COUNT(DISTINCT symbol), SUM(volume) FROM bars");
    assertThat(text).contains("DISTINCT");
    assertThat(countAggregates(text)).isEqualTo(1);
  }

  /** The logical tree the strategy is asked about: exactly what the pipeline hands it. */
  private static RelNode aggregateOf(String sql) {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      return pipeline.logical(sql);
    } catch (Exception e) {
      throw new IllegalStateException("planning failed for: " + sql, e);
    }
  }

  private static int countAggregates(String planText) {
    int count = 0;
    int from = 0;
    while (true) {
      int at = planText.indexOf("ChalkHashAggregate", from);
      if (at < 0) {
        return count;
      }

      count++;
      from = at + 1;
    }
  }

  private static List<Integer> groupings(Rel rel) {
    List<Integer> counts = new ArrayList<>();
    walk(rel, counts);
    return counts;
  }

  private static void walk(Rel rel, List<Integer> counts) {
    switch (rel.getKindCase()) {
      case HASH_AGGREGATE -> {
        counts.add(rel.getHashAggregate().getAggregate().getGroupingsCount());
        walk(rel.getHashAggregate().getAggregate().getInput(), counts);
      }
      case AGGREGATE -> {
        counts.add(rel.getAggregate().getGroupingsCount());
        walk(rel.getAggregate().getInput(), counts);
      }
      case FILTER -> walk(rel.getFilter().getInput(), counts);
      case PROJECT -> walk(rel.getProject().getInput(), counts);
      case SORT -> walk(rel.getSort().getInput(), counts);
      case HASH_JOIN -> {
        walk(rel.getHashJoin().getLeft(), counts);
        walk(rel.getHashJoin().getRight(), counts);
      }
      case MERGE_JOIN -> {
        walk(rel.getMergeJoin().getLeft(), counts);
        walk(rel.getMergeJoin().getRight(), counts);
      }
      case NESTED_LOOP_JOIN -> {
        walk(rel.getNestedLoopJoin().getLeft(), counts);
        walk(rel.getNestedLoopJoin().getRight(), counts);
      }
      default -> {
        // A leaf.
      }
    }
  }
}
