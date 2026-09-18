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
 * Which physical join each shape gets ({@code 12-joins.md} §6). The corpus checks the same thing
 * end to end; this checks it where the reason is visible, and fails with a plan rather than a digest.
 */
class JoinRulesTest {
  private static RegisteredCatalog catalog;

  @BeforeAll
  static void registerCatalog() {
    catalog = new CatalogRegistry().register(TestCatalogs.corpus());
  }

  @ParameterizedTest(name = "{1} for {0}")
  @CsvSource(
      delimiter = '|',
      value = {
        // An equality between the two inputs: hash join.
        "SELECT b.symbol, s.base FROM bars b JOIN symbols s ON b.symbol = s.symbol | ChalkHashJoin",
        // Both inputs collated (ts, symbol) and the keys are exactly those two: merge join. A right
        // column has to be projected, or JOIN_ON_UNIQUE_TO_SEMI_JOIN makes it a semi join, which
        // this step does not merge (§8).
        "SELECT b.ts, q.volume FROM bars b JOIN bars q ON b.symbol = q.symbol AND b.ts = q.ts"
            + " | ChalkMergeJoin",
        // No equality at all: nothing but a nested loop can run it.
        "SELECT b.ts FROM bars b JOIN events e ON b.ts BETWEEN e.start_ts AND e.end_ts"
            + " | ChalkNestedLoopJoin",
        "SELECT s.symbol, r.r_name FROM symbols s CROSS JOIN region r | ChalkNestedLoopJoin",
        // ASOF has one operator and no alternative.
        "SELECT b.symbol FROM bars b LEFT ASOF JOIN funding f MATCH_CONDITION b.ts >= f.ts"
            + " ON b.symbol = f.symbol | ChalkAsOfJoin",
        // V50: a null-safe equality over two nullable keys is not an equi-key — the NULL pair the
        // hash join would drop is a row of the answer — so the nested loop carries it whole.
        "SELECT a.id, b.id FROM sales a JOIN sales b ON a.amount IS NOT DISTINCT FROM b.amount"
            + " | ChalkNestedLoopJoin",
        // F82: and only over *two* nullable keys. `sales.id` is NOT NULL, so there is no NULL on
        // that side for a NULL amount to pair with and the two readings agree row for row.
        "SELECT a.id, b.id FROM sales a JOIN sales b ON a.amount IS NOT DISTINCT FROM b.id"
            + " | ChalkHashJoin",
      })
  void the_shape_decides_the_operator(String sql, String expected) throws Exception {
    assertThat(plan(sql)).contains(expected);
  }

  /**
   * The merge join takes the keys in whatever order the collations agree on, not the order {@code
   * JoinInfo} lists them in. {@code bars} is collated {@code (ts, symbol)} and the ON clause names
   * {@code symbol} first; refusing the permutation would throw the merge join away for nothing.
   */
  @Test
  void a_merge_join_permutes_its_keys_to_match_the_collation() throws Exception {
    String plan =
        plan("SELECT b.ts, q.volume FROM bars b JOIN bars q ON b.symbol = q.symbol AND b.ts = q.ts");

    assertThat(plan).contains("ChalkMergeJoin");
    assertThat(plan).contains("leftKeys=[[1, 0]]").contains("rightKeys=[[1, 0]]");
  }

  /** Without the collation on both sides there is no merge join to have — a hash join runs it. */
  @Test
  void no_collation_means_no_merge_join() throws Exception {
    // `events` is collated by id, and nothing joins on id, so neither side offers the key order.
    String plan =
        plan("SELECT b.ts FROM bars b JOIN events e ON b.symbol = e.symbol AND b.ts = e.start_ts");

    assertThat(plan).doesNotContain("ChalkMergeJoin");
    assertThat(plan).contains("ChalkHashJoin");
  }

  /** The remainder of an equi-join's condition travels with the join, not as a filter above it. */
  @Test
  void a_non_equi_remainder_stays_inside_the_hash_join() throws Exception {
    String plan =
        plan(
            "SELECT b.ts FROM bars b JOIN events e"
                + " ON b.symbol = e.symbol AND b.ts BETWEEN e.start_ts AND e.end_ts");

    assertThat(plan).contains("ChalkHashJoin");
    assertThat(plan.lines().filter(l -> l.contains("ChalkFilter")).count()).isZero();
  }

  static Stream<CorpusQueries.Query> joinCorpus() {
    return CorpusQueries.m3().stream();
  }

  /** Every join corpus query plans, and none of them needs an operator this milestone lacks. */
  @ParameterizedTest(name = "{0}")
  @MethodSource("joinCorpus")
  void every_join_corpus_query_plans(CorpusQueries.Query query) throws Exception {
    assertThat(plan(query.sql())).isNotBlank();
  }

  private static String plan(String sql) throws Exception {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      return pipeline.plan(sql, true).physicalPlanText();
    }
  }
}
