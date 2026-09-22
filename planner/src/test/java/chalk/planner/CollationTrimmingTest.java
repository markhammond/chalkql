package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.diag.PlanText;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * What {@link chalk.planner.plan.ChalkFieldTrimmer} changes about Calcite's trimming (F114).
 *
 * <p>Calcite's {@code RelFieldTrimmer} never discards a column that defines an input's collation.
 * For a catalog whose tables declare one that is the ordering key of every table in every scan,
 * however little of it the query reads. Chalk's trimmer drops the rule; what is asserted here is
 * that the column goes when nothing reads it and stays when something does — which is the whole of
 * why Calcite kept it.
 */
class CollationTrimmingTest {
  private static RegisteredCatalog corpus;

  @BeforeAll
  static void registerCatalog() {
    corpus = new CatalogRegistry().register(TestCatalogs.corpus());
  }

  private static String plan(String sql) {
    try (PlannerPipeline pipeline = PlannerPipeline.create(corpus, PushdownPolicy.full())) {
      return PlanText.withAttributes(pipeline.plan(sql, false).physical());
    } catch (RuntimeException failure) {
      throw failure;
    } catch (Exception failure) {
      throw new AssertionError("planning failed for: " + sql, failure);
    }
  }

  /** {@code bars} declares {@code (ts, symbol)}; this query names neither {@code ts} nor an order. */
  @Test
  void a_filter_does_not_read_an_ordering_column_the_query_never_names() {
    String plan = plan("SELECT symbol, volume FROM bars WHERE volume < 0");

    assertThat(plan).contains("ChalkTableScan(table=[[main, bars]], projection=[[0, 6]])");
  }

  /** The same query without the filter already pruned, and still does. */
  @Test
  void a_plain_projection_is_unchanged() {
    String plan = plan("SELECT symbol, volume FROM bars");

    assertThat(plan).contains("ChalkTableScan(table=[[main, bars]], projection=[[0, 6]])");
  }

  /**
   * An ordering column the query orders by is read, and the declared collation still removes the
   * sort — which is the claim corpus query 42 makes and the reason the columns were kept at all.
   */
  @Test
  void an_ordering_column_the_query_orders_by_is_still_read() {
    String plan = plan("SELECT symbol FROM bars ORDER BY ts");

    assertThat(plan)
        .contains("ChalkTableScan(table=[[main, bars]], projection=[[0, 1]])")
        .doesNotContain("ChalkSort");
  }

  /** And an index is still matched against a narrower scan than the ordering key spans. */
  @Test
  void an_index_still_answers_a_predicate_on_a_narrowed_scan() {
    String plan = plan("SELECT COUNT(*) FROM bars WHERE ts >= TIMESTAMP '2026-01-14 00:00:00'");

    assertThat(plan).contains("ChalkIndexLookup(table=[[main, bars]]");
  }
}
