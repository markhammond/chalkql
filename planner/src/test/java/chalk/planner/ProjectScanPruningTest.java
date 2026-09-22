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
 * Column pruning under a projection that computes (F114).
 *
 * <p>{@code ChalkProjectScanRule} used to return at the first computed column, so a query whose own
 * projection computes anything read every column of the table: the trimmer's column-subset
 * {@code Project} and the query's own are merged into one computing {@code Project} by the time the
 * rule sees them, and there is nothing pure left to match. What is asserted here is the narrowed
 * {@code Read} and that the expressions above it index the narrowed row — the second is the part
 * that would be a wrong answer rather than a slow one.
 */
class ProjectScanPruningTest {
  private static RegisteredCatalog corpus;

  @BeforeAll
  static void registerCatalog() {
    corpus = new CatalogRegistry().register(TestCatalogs.corpus());
  }

  private static String plan(String sql) {
    return plan(corpus, sql, PushdownPolicy.full());
  }

  private static String plan(RegisteredCatalog catalog, String sql, PushdownPolicy policy) {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, policy)) {
      return PlanText.withAttributes(pipeline.plan(sql, false).physical());
    } catch (RuntimeException failure) {
      throw failure;
    } catch (Exception failure) {
      throw new AssertionError("planning failed for: " + sql, failure);
    }
  }

  @Test
  void a_computing_project_prunes_the_scan_to_the_columns_it_reads() {
    String plan = plan("SELECT symbol, volume + 1 AS v FROM bars");

    assertThat(plan)
        .contains("ChalkTableScan(table=[[main, bars]], projection=[[0, 6]])")
        .contains("ChalkProject(symbol=[$0], v=[+($1, 1)])");
  }

  /**
   * The probe's own shape: the {@code FILTER} of a measure is a computed column of the projection
   * under the aggregate, and it read the whole nine-column row for the three columns it names.
   */
  @Test
  void a_measure_filter_reads_only_the_columns_it_names() {
    String plan =
        plan(
            "SELECT symbol, SUM(volume) FILTER (WHERE \"close\" > \"open\") AS up"
                + " FROM bars GROUP BY symbol");

    assertThat(plan)
        .contains("ChalkTableScan(table=[[main, bars]], projection=[[0, 2, 5, 6]])")
        .contains("ChalkProject(symbol=[$0], volume=[$3], $f2=[>($2, $1)])");
  }

  /** One column read twice is not a pure mapping either, and still names one column. */
  @Test
  void a_column_projected_twice_prunes_to_that_column() {
    String plan = plan("SELECT symbol AS a, symbol AS b FROM bars");

    assertThat(plan).contains("ChalkTableScan(table=[[main, bars]], projection=[[0]])");
  }

  @Test
  void a_project_that_reads_every_column_is_unchanged() {
    String plan =
        plan(
            "SELECT symbol, EXTRACT(HOUR FROM ts) AS h, \"open\" + high AS oh,"
                + " low + \"close\" AS lc, volume + 1 AS v, vwap * 2 AS w,"
                + " trade_count + 1 AS t FROM bars");

    assertThat(plan)
        .contains("ChalkTableScan(table=[[main, bars]], projection=[[0, 1, 2, 3, 4, 5, 6, 7, 8]])");
  }

  /**
   * A projection of constants alone names no column, and a {@code Read} that reads none is not a
   * {@code Read} (02-ir.md §4). The scan is left as the trimmer sized it.
   */
  @Test
  void a_projection_of_constants_alone_leaves_the_scan_alone() {
    String plan = plan("SELECT 1 AS n, 'x' AS x FROM bars");

    assertThat(plan).contains("ChalkTableScan").doesNotContain("projection=[[]]");
  }

  @Test
  void the_reference_configuration_is_unchanged() {
    String plan =
        plan(corpus, "SELECT symbol, volume + 1 AS v FROM bars", PushdownPolicy.none());

    assertThat(plan)
        .contains("ChalkTableScan(table=[[main, bars]], projection=[[0, 1, 2, 3, 4, 5, 6, 7, 8]])")
        .contains("ChalkProject(symbol=[$0], v=[+($6, 1)])");
  }

  /**
   * A source that takes queries keeps its own projection, in its own convention: pruning it into a
   * local scan here would put the leaf back in {@code CHALK_LOCAL} and the subtree would stop being
   * pushable (D83).
   */
  @Test
  void a_remote_scan_is_left_to_its_own_pushdown() {
    RegisteredCatalog remote =
        new CatalogRegistry()
            .register(
                TestCatalogs.withRemote(
                    "db", TestCatalogs.fullSqlCapabilities().build(), TestCatalogs.duckDbProfile()));

    String plan =
        plan(remote, "SELECT l_orderkey, l_quantity + 1 AS q FROM db.lineitem", PushdownPolicy.full());

    assertThat(plan).contains("SourceToLocalConverter").doesNotContain("ChalkTableScan");
  }
}
