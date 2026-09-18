package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.ChalkRelMetadata;
import chalk.planner.plan.CostModel;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import chalk.planner.plan.rel.ChalkIndexLookup;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.junit.jupiter.api.Test;

/**
 * The clustered index in the planner (D257, {@code docs/design/34-clustered-indexes.md} §3): it
 * matches every rule the ordered kind matches, it is named in the plan text, and a lookup whose
 * projection its copy covers is priced as the sequential scan it is.
 *
 * <p>Planned against {@link TestCatalogs#declared()} rather than the recorded corpus, so the claims
 * here hold whether or not the corpus has been recorded.
 */
class ClusteredIndexTest {

  /** Corpus query 02b: the moving average, whose window wants (symbol, ts) and projects close. */
  private static final String WINDOW_CLUSTERED =
      "SELECT symbol, ts, AVG(\"close\") OVER "
          + "(PARTITION BY symbol ORDER BY ts ROWS 19 PRECEDING) AS sma20 FROM bars_clustered";

  /** The same statement over the permutation-indexed twin, which is corpus query 02. */
  private static final String WINDOW_PERMUTATION =
      "SELECT symbol, ts, AVG(\"close\") OVER "
          + "(PARTITION BY symbol ORDER BY ts ROWS 19 PRECEDING) AS sma20 FROM bars_small";

  @Test
  void the_collation_rule_takes_a_clustered_index_with_no_predicate_at_all() {
    Planned planned = plan(WINDOW_CLUSTERED);

    // No Sort: the window's partition-and-order is served by the index's own key order, which is
    // the claim ChalkIndexOrderedScanRule makes for the ordered kind and now makes for this one.
    assertThat(planned.text()).contains("ChalkIndexLookup");
    assertThat(planned.text()).doesNotContain("ChalkSort");
    assertThat(planned.lookup().index().getName()).isEqualTo("ix_bars_clustered_symbol_ts");
  }

  @Test
  void the_collation_rule_takes_a_clustered_index_for_an_order_by() {
    Planned planned = plan("SELECT symbol, ts FROM bars_clustered ORDER BY symbol, ts");

    assertThat(planned.text()).contains("ChalkIndexLookup");
    assertThat(planned.text()).doesNotContain("ChalkSort");
  }

  @Test
  void the_plan_text_names_the_kind_and_whether_the_projection_is_covered() {
    assertThat(plan(WINDOW_CLUSTERED).text())
        .contains("kind=[INDEX_KIND_CLUSTERED]")
        .contains("covered=[true]");

    // And the permutation twin's text is exactly what it was before D257: a lookup that holds no
    // copy has no kind to distinguish and nothing to say about covering (ADR 0036 §3).
    assertThat(plan(WINDOW_PERMUTATION).text())
        .contains("index=[ix_bars_small_symbol_ts]")
        .doesNotContain("kind=[")
        .doesNotContain("covered=[");
  }

  @Test
  void a_covered_lookup_is_priced_as_a_scan() {
    Planned planned = plan(WINDOW_CLUSTERED);
    ChalkIndexLookup lookup = planned.lookup();
    double rows = planned.mq().getRowCount(lookup);

    CostModel costs = CostModel.defaults();
    assertThat(planned.cost(lookup).getRows())
        .isEqualTo(costs.clusteredLookup(lookup.ranges().size(), rows));

    // Which is the whole point: the gather's price is four times the scan's per row, and the copy
    // is what removes it.
    assertThat(planned.cost(lookup).getRows())
        .isLessThan(costs.lookup(lookup.ranges().size(), rows));
  }

  /**
   * The three numbers the choice is made from, at the corpus's 5 000 rows: the covered lookup pays
   * one seek and a scan per row; the same lookup uncovered pays the random-access rate; and the sort
   * the window would otherwise need pays {@code rows × log2 rows × keys} on top of a full scan.
   */
  @Test
  void the_covered_lookup_beats_both_the_gather_and_the_sort() {
    Planned planned = plan(WINDOW_CLUSTERED);
    ChalkIndexLookup lookup = planned.lookup();
    double rows = planned.mq().getRowCount(lookup);
    CostModel costs = CostModel.defaults();

    assertThat(rows).isEqualTo(5_000.0);
    assertThat(planned.cost(lookup).getRows()).isEqualTo(5_017.0);
    assertThat(costs.lookup(1, rows)).isEqualTo(20_017.0);

    // ChalkSort over the scan: rows × log2(rows + 1) × 2 keys, plus rows × scan_row_cost for the
    // scan under it. Twenty-five times what the copy costs, which is why no Sort survives.
    double sort = (rows * (Math.log(rows + 1) / Math.log(2)) * 2) + costs.scan(rows, 1.0);
    assertThat(sort).isCloseTo(127_880.0, org.assertj.core.data.Offset.offset(0.5));
    assertThat(planned.cost(lookup).getRows()).isLessThan(sort);
  }

  @Test
  void the_same_index_prices_an_uncovered_projection_as_a_gather() {
    // `volume` is outside the copy, so this lookup is served by the permutation and priced like one.
    Planned planned = plan("SELECT symbol, ts, volume FROM bars_clustered ORDER BY symbol, ts");
    ChalkIndexLookup lookup = planned.lookup();
    double rows = planned.mq().getRowCount(lookup);

    assertThat(planned.text()).contains("covered=[false]");
    assertThat(planned.cost(lookup).getRows())
        .isEqualTo(CostModel.defaults().lookup(lookup.ranges().size(), rows));
  }

  @Test
  void an_index_that_covers_every_column_covers_every_projection() {
    // The corpus's clustered index names its covering set; an index that names none carries every
    // column, and the planner reads the empty set as "everything" rather than as "nothing".
    chalk.ir.v1.Table table =
        TestCatalogs.barsClustered().toBuilder()
            .setName("bars_all")
            .clearIndexes()
            .addIndexes(
                chalk.ir.v1.Index.newBuilder()
                    .setName("cx_bars_all")
                    .setKind(chalk.ir.v1.IndexKind.INDEX_KIND_CLUSTERED)
                    .setUnique(true)
                    .addColumns(0)
                    .addColumns(1))
            .clearForeignKeys()
            .build();

    Planned planned =
        plan(
            "SELECT symbol, ts, volume FROM bars_all ORDER BY symbol, ts",
            new CatalogRegistry()
                .register(
                    TestCatalogs.declared().toBuilder()
                        .setContextId("clustered")
                        .setSchemas(
                            0, TestCatalogs.declared().getSchemas(0).toBuilder().addTables(table))
                        .build()));

    assertThat(planned.text()).contains("covered=[true]");
  }

  // ---- support ----

  private record Planned(RelNode physical, ChalkIndexLookup lookup, RelMetadataQuery mq) {
    String text() {
      return chalk.planner.diag.PlanText.withAttributes(physical);
    }

    RelOptCost cost(ChalkIndexLookup node) {
      RelOptCost cost = node.computeSelfCost(node.getCluster().getPlanner(), mq);
      assertThat(cost).isNotNull();
      return cost;
    }
  }

  private static Planned plan(String sql) {
    return plan(sql, new CatalogRegistry().register(TestCatalogs.declared()));
  }

  private static Planned plan(String sql, RegisteredCatalog catalog) {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(catalog, PushdownPolicy.full(), SqlConfigs.DEFAULT_CONFORMANCE)) {
      RelNode physical = pipeline.plan(sql, true).physical();
      ChalkIndexLookup lookup = find(physical);
      assertThat(lookup).as("an index lookup for: " + sql).isNotNull();

      lookup.getCluster().setMetadataProvider(ChalkRelMetadata.SOURCE);
      lookup.getCluster().invalidateMetadataQuery();
      return new Planned(physical, lookup, RelMetadataQuery.instance());
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException("planning failed for: " + sql, e);
    }
  }

  private static ChalkIndexLookup find(RelNode rel) {
    if (rel instanceof ChalkIndexLookup lookup) {
      return lookup;
    }

    for (RelNode input : rel.getInputs()) {
      ChalkIndexLookup found = find(input);
      if (found != null) {
        return found;
      }
    }

    return null;
  }
}
