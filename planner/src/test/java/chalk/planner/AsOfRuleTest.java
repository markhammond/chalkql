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
