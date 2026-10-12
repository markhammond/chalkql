package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;

/**
 * What an {@code ASOF JOIN} may be, and what each refusal says ({@code 12-joins.md} §6). Calcite's
 * validator catches most of it; the rule re-checks so that a shape Chalk cannot run is named rather
 * than becoming "not enough rules to produce a node".
 */
class AsOfRuleTest {
  private static RegisteredCatalog catalog;

  @BeforeAll
  static void registerCatalog() {
    catalog = new CatalogRegistry().register(TestCatalogs.corpus());
  }

  @Test
  void an_asof_join_plans_to_the_asof_operator() throws Exception {
    String plan =
        plan(
            "SELECT b.symbol, f.rate FROM bars b LEFT ASOF JOIN funding f"
                + " MATCH_CONDITION b.ts >= f.ts ON b.symbol = f.symbol");

    assertThat(plan).contains("ChalkAsOfJoin").contains("left_asof");
  }

  @Test
  void an_inner_asof_join_keeps_its_type() throws Exception {
    String plan =
        plan(
            "SELECT b.symbol, f.rate FROM bars b ASOF JOIN funding f"
                + " MATCH_CONDITION b.ts <= f.ts ON b.symbol = f.symbol");

    assertThat(plan).contains("ChalkAsOfJoin").contains("joinType=[asof]");
  }

  /**
   * D337: a condition on the right input filters the right input, under the join, as if the
   * statement had said so with a subquery; the ON keeps the equalities between the inputs.
   */
  @Test
  void a_condition_on_the_right_input_filters_it_under_the_join() throws Exception {
    String plan =
        plan(
            "SELECT b.symbol, f.rate FROM bars b LEFT ASOF JOIN funding f"
                + " MATCH_CONDITION b.ts >= f.ts ON b.symbol = f.symbol AND f.rate > 0");

    assertThat(plan).contains("ChalkAsOfJoin").contains("left_asof");
    assertThat(plan.substring(plan.indexOf("ChalkAsOfJoin"))).contains("ChalkFilter");
  }

  /** And for an inner ASOF join, a condition on the left input filters the left input. */
  @Test
  void a_condition_on_the_left_input_of_an_inner_asof_join_filters_it() throws Exception {
    String plan =
        plan(
            "SELECT b.symbol, f.rate FROM bars b ASOF JOIN funding f"
                + " MATCH_CONDITION b.ts >= f.ts ON b.symbol = f.symbol AND b.volume > 10");

    assertThat(plan).contains("ChalkAsOfJoin").contains("joinType=[asof]");
    assertThat(plan.substring(plan.indexOf("ChalkAsOfJoin"))).contains("ChalkFilter");
  }

  @Test
  void a_condition_on_the_left_input_of_a_left_asof_join_is_refused_saying_why() {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT b.symbol, f.rate FROM bars b LEFT ASOF JOIN funding f"
                        + " MATCH_CONDITION b.ts >= f.ts ON b.symbol = f.symbol AND b.volume > 10"))
        .isInstanceOf(InvalidArgumentException.class)
        .hasMessageContaining("a left row that fails 'b.volume > 10' must still appear, with NULLs");
  }

  @Test
  void a_condition_that_does_not_qualify_its_column_is_refused_saying_why() {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT b.symbol, f.rate FROM bars b ASOF JOIN funding f"
                        + " MATCH_CONDITION b.ts >= f.ts ON rate > 0"))
        .isInstanceOf(InvalidArgumentException.class)
        .hasMessageContaining("'rate > 0' names a column without its input's alias");
  }

  /**
   * D338, design 63 §3: a match column the left side computes. Calcite casts a {@code VALUES}
   * timestamp to the right side's precision inside the match condition, which was a planner
   * internal error; the cast is now a column of the left input.
   */
  @Test
  void a_computed_match_column_plans() throws Exception {
    String plan =
        plan(
            "WITH days (d, closes) AS (VALUES ('a', TIMESTAMP '2026-01-02 00:00:00'),"
                + " ('b', TIMESTAMP '2026-01-03 00:00:00'))"
                + " SELECT d.d, f.rate FROM days d CROSS JOIN bars b"
                + " ASOF JOIN funding f MATCH_CONDITION f.ts <= d.closes ON f.symbol = b.symbol");

    assertThat(plan).contains("ChalkAsOfJoin").contains("joinType=[asof]");
  }

  /** And a row-value key, which Calcite's ASOF check refused as a single equality. */
  @Test
  void a_row_value_key_plans() throws Exception {
    String plan =
        plan(
            "SELECT b.symbol FROM bars b ASOF JOIN funding f MATCH_CONDITION f.ts <= b.ts"
                + " ON (f.symbol, f.symbol) = (b.symbol, b.symbol)");

    assertThat(plan).contains("ChalkAsOfJoin");
  }

  @ParameterizedTest(name = "{1}")
  @CsvSource(
      delimiter = '|',
      value = {
        // Calcite has no RIGHT ASOF at all: the parser refuses it.
        "SELECT b.symbol FROM bars b RIGHT ASOF JOIN funding f MATCH_CONDITION b.ts >= f.ts"
            + " ON b.symbol = f.symbol | RIGHT ASOF",
        // The match condition has to be a comparison of two columns.
        "SELECT b.symbol FROM bars b ASOF JOIN funding f MATCH_CONDITION b.ts <> f.ts"
            + " ON b.symbol = f.symbol | MATCH_CONDITION must be a comparison",
        // And the ON clause has to be equalities.
        "SELECT b.symbol FROM bars b ASOF JOIN funding f MATCH_CONDITION b.ts >= f.ts"
            + " ON b.symbol > f.symbol | ASOF",
      })
  void an_unsupported_asof_join_is_refused(String sql, String because) {
    assertThatThrownBy(() -> plan(sql))
        .as("%s should be refused (%s)", sql, because)
        .isInstanceOf(Exception.class);
  }

  private static String plan(String sql) throws Exception {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      return pipeline.plan(sql, true).physicalPlanText();
    }
  }
}
