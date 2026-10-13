package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.rules.OneRowJoinRules;
import java.util.List;
import org.apache.calcite.plan.RelOptRule;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.plan.hep.HepPlanner;
import org.apache.calcite.plan.hep.HepProgramBuilder;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.rules.SingleValuesOptimizationRules;
import org.apache.calcite.tools.Frameworks;
import org.apache.calcite.tools.RelBuilder;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * A join with a one-row constant table is a projection of its other side (D348), and the joins it
 * is kept off stay joins: an ASOF join, a one-row side that is not deterministic, and a semi join
 * whose one row is its left side.
 */
class OneRowJoinTest {
  private static RegisteredCatalog catalog;

  @BeforeAll
  static void registerCatalog() {
    catalog = new CatalogRegistry().register(TestCatalogs.corpus());
  }

  /** The table a statement names its constant in, crossed in, is the constant in the projection. */
  @Test
  void a_cross_join_with_a_one_row_table_is_a_projection_of_its_literal() throws Exception {
    String plan =
        plan(
            "WITH reporting AS (SELECT CAST('EUR' AS VARCHAR(32)) AS symbol)"
                + " SELECT f.symbol, f.rate, r.symbol AS reporting FROM funding f CROSS JOIN reporting r");

    assertThat(plan).doesNotContain("Join").contains("'EUR'");
  }

  /** An inner join's condition, with the row's values put in, filters the other side. */
  @Test
  void an_inner_join_with_a_one_row_table_filters_the_other_side() throws Exception {
    String plan =
        plan(
            "SELECT f.symbol, f.rate FROM funding f JOIN (VALUES ('BTC')) AS r (symbol)"
                + " ON f.symbol = r.symbol");

    assertThat(plan).doesNotContain("Join").contains("ChalkFilter").contains("'BTC'");
  }

  /** A left join keeps every row of the other side, the row's values where the condition holds. */
  @Test
  void a_left_join_with_a_one_row_table_keeps_every_row_of_the_other_side() throws Exception {
    String plan =
        plan(
            "SELECT f.symbol, r.flag FROM funding f LEFT JOIN (VALUES ('BTC', 1)) AS r (symbol, flag)"
                + " ON f.symbol = r.symbol");

    assertThat(plan).doesNotContain("Join").doesNotContain("ChalkFilter").contains("CASE");
  }

  /** And a right join of one row on the left, the same from the other side. */
  @Test
  void a_right_join_of_a_one_row_table_keeps_every_row_of_the_other_side() throws Exception {
    String plan =
        plan(
            "SELECT r.flag, f.symbol FROM (VALUES ('BTC', 1)) AS r (symbol, flag)"
                + " RIGHT JOIN funding f ON f.symbol = r.symbol");

    assertThat(plan).doesNotContain("Join").doesNotContain("ChalkFilter").contains("CASE");
  }

  /**
   * The statement that found it: a one-row table crossed in and keying an ASOF join. Left a join,
   * its column was folded into the key, its columns went unused, and the cross join became a semi
   * join against one row.
   */
  @Test
  void a_one_row_table_keying_an_asof_join_leaves_no_semi_join() throws Exception {
    String plan =
        plan(
            "WITH reporting AS (SELECT CAST('BTC' AS VARCHAR(32)) AS symbol)"
                + " SELECT b.ts, f.rate FROM bars b CROSS JOIN reporting r"
                + " ASOF JOIN funding f MATCH_CONDITION f.ts <= b.ts ON f.symbol = r.symbol");

    assertThat(plan).containsPattern("ChalkAsOf(Lookup)?Join").doesNotContain("semi");
    assertThat(plan.replace("ChalkAsOfJoin", "").replace("ChalkAsOfLookupJoin", ""))
        .doesNotContain("Join");
  }

  /**
   * An ASOF join counts as a join to Calcite's rules, and its one row on the left would become a
   * filter keeping every row of the right its key matches, not the closest one.
   */
  @Test
  void an_asof_join_with_one_row_on_the_left_stays_an_asof_join() throws Exception {
    String plan =
        plan(
            "SELECT s.symbol, f.rate FROM"
                + " (SELECT CAST('BTC' AS VARCHAR) AS symbol, TIMESTAMP '2026-01-02 00:00:00' AS ts) s"
                + " ASOF JOIN funding f MATCH_CONDITION f.ts <= s.ts ON f.symbol = s.symbol");

    assertThat(plan).containsPattern("ChalkAsOf(Lookup)?Join");
  }

  /** A one-row side that is not deterministic would be computed once for each row of the other. */
  @Test
  void a_one_row_side_that_is_not_deterministic_stays_a_join() throws Exception {
    String plan = plan("SELECT f.symbol, s.n FROM funding f CROSS JOIN (SELECT next_seq() AS n) s");

    assertThat(plan).contains("Join");
  }

  /**
   * An {@code EXISTS} against one outer row is decorrelated into a join with the distinct values it
   * reads, so it becomes a filter of those values, and at most one of them is the row's.
   */
  @Test
  void an_exists_of_one_row_is_a_filter_of_the_distinct_values() throws Exception {
    String plan =
        plan(
            "SELECT v.x FROM (VALUES (1)) AS v (x)"
                + " WHERE EXISTS (SELECT 1 FROM bars b WHERE b.trade_count = v.x)");

    assertThat(plan).doesNotContain("Join").contains("ChalkHashAggregate");
  }

  /**
   * A semi join keeps its one left row once however many rows of the right match it, which a filter
   * of the right side would not, so the rules leave it. No statement reaches one by way of SQL, which
   * turns an {@code EXISTS} into a join with distinct values first, so it is built here.
   */
  @Test
  void a_semi_join_with_one_row_on_the_left_is_left_a_semi_join() {
    RelNode after = rewritten(oneRowBeside(JoinRelType.SEMI), OneRowJoinRules.ALL);

    assertThat(RelOptUtil.toString(after)).contains("joinType=[semi]");
  }

  /** And it is why: Calcite's own rule makes it a filter of the right side, one row per match. */
  @Test
  void calcites_own_rule_would_answer_it_once_per_match() {
    RelNode after =
        rewritten(
            oneRowBeside(JoinRelType.SEMI), List.of(SingleValuesOptimizationRules.JOIN_LEFT_INSTANCE));

    assertThat(RelOptUtil.toString(after)).doesNotContain("Join");
  }

  /** The same shape as an inner join is rewritten, so the rules are matching in this harness. */
  @Test
  void the_inner_join_of_the_same_shape_is_rewritten() {
    RelNode after = rewritten(oneRowBeside(JoinRelType.INNER), OneRowJoinRules.ALL);

    assertThat(RelOptUtil.toString(after)).doesNotContain("Join");
  }

  /** One row, {@code x = 1}, joined on {@code x = y} with three rows of {@code y = 1}. */
  private static RelNode oneRowBeside(JoinRelType type) {
    RelBuilder b =
        RelBuilder.create(
            Frameworks.newConfigBuilder().defaultSchema(Frameworks.createRootSchema(true)).build());
    return b.values(new String[] {"x"}, 1)
        .values(new String[] {"y"}, 1, 1, 1)
        .join(type, b.equals(b.field(2, 0, "x"), b.field(2, 1, "y")))
        .build();
  }

  private static RelNode rewritten(RelNode rel, List<RelOptRule> rules) {
    HepPlanner hep = new HepPlanner(new HepProgramBuilder().addRuleCollection(rules).build());
    hep.setRoot(rel);
    return hep.findBestExp();
  }

  private static String plan(String sql) throws Exception {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      return pipeline.plan(sql, true).physicalPlanText();
    }
  }
}
