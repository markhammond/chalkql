package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import java.util.stream.Stream;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * The set operations of D69 ({@code 15-zero-allocation-execution.md} §6): each kind converts, the
 * three-way union merges into one n-ary node, {@code MINUS_TO_ANTI_JOIN} fires, and nothing claims
 * an ordering it does not have.
 */
class SetOpRulesTest {
  private static RegisteredCatalog catalog;

  @BeforeAll
  static void registerCatalog() {
    catalog = new CatalogRegistry().register(TestCatalogs.corpus());
  }

  @ParameterizedTest(name = "{1} for {0}")
  @CsvSource(
      delimiter = '|',
      value = {
        "SELECT symbol FROM bars_small UNION ALL SELECT symbol FROM symbols | ChalkUnion",
        "SELECT symbol FROM bars_small UNION SELECT symbol FROM symbols | ChalkUnion",
        "SELECT symbol FROM bars_small INTERSECT SELECT symbol FROM symbols | ChalkIntersect",
        "SELECT symbol FROM bars_small INTERSECT ALL SELECT symbol FROM symbols | ChalkIntersect",
        "SELECT symbol FROM bars_small EXCEPT ALL SELECT symbol FROM symbols | ChalkMinus",
      })
  void each_kind_converts(String sql, String expected) throws Exception {
    assertThat(plan(sql)).contains(expected);
  }

  /** {@code UNION_MERGE}: a nested union is one n-ary node, not two. */
  @Test
  void a_three_way_union_merges_into_one_node() throws Exception {
    String plan =
        plan(
            "SELECT symbol FROM bars_small WHERE symbol = 'BTCUSDT'"
                + " UNION SELECT symbol FROM bars_small WHERE symbol = 'ETHUSDT'"
                + " UNION SELECT symbol FROM bars_small WHERE symbol = 'SOLUSDT'");

    assertThat(plan.lines().filter(line -> line.contains("ChalkUnion")).count()).isEqualTo(1);
  }

  /**
   * V29: {@code CoreRules.MINUS_TO_ANTI_JOIN} exists in Calcite 1.42 and fires on a single-column
   * {@code EXCEPT DISTINCT}, which is where the corpus finally gets an {@code ANTI} join.
   */
  @Test
  void a_single_column_except_becomes_an_anti_join() throws Exception {
    String plan = plan("SELECT symbol FROM symbols EXCEPT SELECT symbol FROM events");

    assertThat(plan).contains("anti");
    assertThat(plan).doesNotContain("ChalkMinus");
  }

  /** {@code EXCEPT ALL} keeps multiplicities, so the anti-join rewrite does not apply to it. */
  @Test
  void except_all_keeps_the_operator() throws Exception {
    String plan = plan("SELECT symbol FROM symbols EXCEPT ALL SELECT symbol FROM events");

    assertThat(plan).contains("ChalkMinus");
  }

  /**
   * A set operation delivers no ordering, so an {@code ORDER BY} above one is a real sort rather
   * than a claim that the inputs' order survived.
   */
  @Test
  void an_order_by_above_a_union_is_a_real_sort() throws Exception {
    String plan =
        plan(
            "SELECT symbol, ts FROM bars_small WHERE symbol = 'BTCUSDT'"
                + " UNION ALL SELECT symbol, ts FROM bars_small WHERE symbol = 'ETHUSDT'"
                + " ORDER BY ts");

    assertThat(plan).contains("ChalkUnion");
    assertThat(plan).containsPattern("ChalkSort|ChalkTopN");
  }

  static Stream<CorpusQueries.Query> setOpCorpus() {
    return CorpusQueries.m6().stream();
  }

  /** Every set-operation corpus query plans, and none needs an operator this milestone lacks. */
  @ParameterizedTest(name = "{0}")
  @MethodSource("setOpCorpus")
  void every_set_op_corpus_query_plans(CorpusQueries.Query query) throws Exception {
    assertThat(plan(query.sql())).isNotBlank();
  }

  private static String plan(String sql) throws Exception {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      return pipeline.plan(sql, true).physicalPlanText();
    }
  }
}
