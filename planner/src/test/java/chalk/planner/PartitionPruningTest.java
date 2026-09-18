package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import org.junit.jupiter.api.Test;

/**
 * Partitioned tables (D106, {@code 20-m5-federation.md} §3): a scan expands into its partitions,
 * each in its own source's convention, and a predicate on the partition column drops the ones it
 * cannot match.
 */
class PartitionPruningTest {

  private static String plan(String sql) {
    RegisteredCatalog catalog =
        new CatalogRegistry().register(TestCatalogs.withPartitions(TestCatalogs.duckDbProfile()));
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      return pipeline.plan(sql, true).physicalPlanText();
    } catch (Exception failure) {
      throw new AssertionError("planning failed for: " + sql, failure);
    }
  }

  @Test
  public void a_scan_of_a_partitioned_table_expands_into_every_partition() {
    String plan = plan("SELECT symbol, ts FROM federated.bars_by_symbol");

    assertThat(plan).contains("ChalkPartitionedScan");
    for (String symbol : new String[] {"btcusdt", "ethusdt", "solusdt", "adausdt", "xrpusdt"}) {
      assertThat(plan).contains("bars_" + symbol);
    }
  }

  @Test
  public void an_equality_on_the_partition_column_keeps_one_partition() {
    String plan = plan("SELECT ts FROM federated.bars_by_symbol WHERE symbol = 'BTCUSDT'");

    assertThat(plan).contains("bars_btcusdt");
    assertThat(plan).doesNotContain("bars_ethusdt");
    assertThat(plan).doesNotContain("bars_xrpusdt");
  }

  @Test
  public void an_in_list_on_the_partition_column_keeps_the_partitions_it_names() {
    String plan =
        plan(
            "SELECT symbol, ts FROM federated.bars_by_symbol"
                + " WHERE symbol IN ('BTCUSDT', 'XRPUSDT')");

    assertThat(plan).contains("bars_btcusdt");
    assertThat(plan).contains("bars_xrpusdt");
    assertThat(plan).doesNotContain("bars_ethusdt");
    assertThat(plan).doesNotContain("bars_solusdt");
    assertThat(plan).doesNotContain("bars_adausdt");
  }

  /**
   * A predicate on any other column prunes nothing: the partitions are keyed by symbol, and a
   * filter on `volume` says nothing about which of them can hold a matching row.
   */
  @Test
  public void a_predicate_on_another_column_prunes_nothing() {
    String plan = plan("SELECT symbol, ts FROM federated.bars_by_symbol WHERE volume > 5000");

    for (String symbol : new String[] {"btcusdt", "ethusdt", "solusdt", "adausdt", "xrpusdt"}) {
      assertThat(plan).contains("bars_" + symbol);
    }
  }

  /** Every partition converts on its own, so each is a query its own source runs. */
  @Test
  public void every_partition_is_pushed_into_the_source_that_holds_it() {
    String plan = plan("SELECT symbol, ts FROM federated.bars_by_symbol WHERE symbol = 'ADAUSDT'");

    assertThat(plan).contains("SourceScan(table=[[b, bars_adausdt]]");
    assertThat(plan).contains("SourceToLocalConverter");
  }

  /**
   * The projection reaches the partitions, so a branch's query asks for the columns the statement
   * named rather than for the table. {@code RelFieldTrimmer} would normally do this and cannot —
   * it prunes by rebuilding, and rebuilding a partitioned scan loses its partition values — so
   * {@code PartitionRules.PROJECT} does it instead, and this is the assertion that says so.
   */
  @Test
  public void a_projection_is_pushed_into_every_partition() {
    String plan = plan("SELECT symbol, ts FROM federated.bars_by_symbol");

    assertThat(plan).contains("ChalkPartitionedScan");
    assertThat(plan).contains("SourceProject(symbol=[$0], ts=[$1])");
  }
}
