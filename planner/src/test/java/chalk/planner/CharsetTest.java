package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.diag.PlanText;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import java.nio.charset.StandardCharsets;
import org.apache.calcite.util.Util;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * The SQL character set is UTF-8 (docs/design/25-coverage-graft.md §0b, F22 from ADR 0023).
 *
 * <p>Calcite's default is ISO-8859-1, and under it a string literal outside Latin-1 fails to plan —
 * {@code WHERE symbol = '株式'} raises "Failed to encode '株式' in character set 'ISO-8859-1'" —
 * while the same value read out of a column compares correctly, so the defect is invisible until a
 * literal appears. Calcite 1.42 has no charset setting on {@code SqlParser.Config} or on the type
 * factory: the setting is the system property {@code calcite.default.charset}, seeded from
 * {@code saffron.properties} on the classpath (V47), which is where Chalk sets it.
 */
class CharsetTest {
  private static RegisteredCatalog catalog;

  @BeforeAll
  static void registerCatalog() {
    catalog = new CatalogRegistry().register(TestCatalogs.corpus());
  }

  @Test
  void the_default_charset_is_utf_8() {
    assertThat(Util.getDefaultCharset()).isEqualTo(StandardCharsets.UTF_8);
    assertThat(SqlConfigs.summary()).contains("charset=UTF-8");
  }

  /** The literal that could not be planned before. */
  @Test
  void a_non_latin_1_literal_plans() throws Exception {
    String text = plan("SELECT symbol, ts FROM bars WHERE symbol = '株式'");
    assertThat(text).contains("株式");
  }

  /** The same value as a parameter: the placeholder is typed, and the value never reaches Calcite. */
  @Test
  void a_non_latin_1_parameter_plans() throws Exception {
    assertThat(plan("SELECT symbol, ts FROM bars WHERE symbol = ? AND symbol <> 'ムーン'"))
        .contains("ムーン");
  }

  /** Every string function the corpus uses, over a non-Latin-1 literal, still folds and plans. */
  @Test
  void non_latin_1_literals_survive_the_string_kernels() throws Exception {
    assertThat(plan("SELECT UPPER('株式') AS u, CHAR_LENGTH('株式会社') AS n, '株' || '式' AS c"))
        .isNotEmpty();
  }

  private static String plan(String sql) throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(catalog, PushdownPolicy.none(), SqlConfigs.DEFAULT_CONFORMANCE)) {
      return PlanText.withAttributes(pipeline.plan(sql, false).physical());
    }
  }
}
