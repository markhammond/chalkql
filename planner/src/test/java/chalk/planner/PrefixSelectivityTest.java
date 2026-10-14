package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.within;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.Column;
import chalk.ir.v1.ColumnStatistics;
import chalk.ir.v1.FrequentValue;
import chalk.ir.v1.Histogram;
import chalk.ir.v1.HistogramBucket;
import chalk.ir.v1.Literal;
import chalk.ir.v1.Schema;
import chalk.ir.v1.StatisticsLevel;
import chalk.ir.v1.Table;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.ChalkSelectivity;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import chalk.planner.plan.rel.ChalkFilter;
import chalk.planner.plan.rel.ChalkTableScan;
import java.nio.file.Files;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * {@code col LIKE 'p%'} is estimated from the column's statistics, as the share of its texts that
 * start with the prefix, rather than at Calcite's flat quarter (ADR 0072 §3), which made a prefix
 * index worth a seek only above about thirty-four rows.
 *
 * <p>Planned against the recorded corpus catalog, whose {@code bars} is 100 800 rows with five
 * symbols from {@code ADAUSDT} to {@code XRPUSDT}, and against the same catalog with a histogram on
 * {@code symbol} put in its place.
 */
class PrefixSelectivityTest {
  private static final double ROWS = 100_800;

  @BeforeAll
  static void requireRecordedCatalog() {
    assumeTrue(
        Files.exists(CorpusQueries.corpusDir().resolve("schemas/corpus.binpb")),
        "the recorded corpus catalog carries the statistics the cost model reads");
  }

  /**
   * Five symbols and nothing more: spread evenly from {@code ADAUSDT} to {@code XRPUSDT}, three
   * letters of prefix would hold almost none of them, but a prefix is never less likely than one of
   * the column's values, so it holds a fifth.
   */
  @Test
  void a_prefix_is_never_less_likely_than_one_of_the_columns_values() {
    ChalkSelectivity.Estimate estimate =
        estimate(TestCatalogs.corpus(), "SELECT * FROM bars WHERE symbol LIKE 'BTC%'");

    assertThat(estimate.value()).isEqualTo(1 / 5.0);
    assertThat(estimate.guessed()).isFalse();
  }

  /**
   * Four buckets of a quarter each, the second from {@code FFFF} to {@code MMMM}: {@code H} is about
   * one of that bucket's seven letters.
   */
  @Test
  void a_histogram_places_the_prefix_within_its_bucket() {
    ChalkSelectivity.Estimate estimate =
        estimate(withSymbols(histogram()), "SELECT * FROM bars WHERE symbol LIKE 'H%'");

    assertThat(estimate.value()).isCloseTo(0.25 / 7, within(0.006));
    assertThat(estimate.guessed()).isFalse();
  }

  /** A frequent value the prefix starts is counted whole, whatever the buckets say around it. */
  @Test
  void the_frequent_values_a_prefix_starts_are_counted() {
    ColumnStatistics statistics =
        histogram().toBuilder()
            .addFrequentValues(
                FrequentValue.newBuilder().setValue(text("HHHH")).setCount((long) (ROWS / 2)))
            .build();

    ChalkSelectivity.Estimate estimate =
        estimate(withSymbols(statistics), "SELECT * FROM bars WHERE symbol LIKE 'H%'");

    assertThat(estimate.value()).isEqualTo(0.5);
  }

  /**
   * {@code LIKE '%'} is every text there is: the rows that are not NULL. Calcite folds it to TRUE
   * over {@code bars.symbol}, which is NOT NULL, so the predicate is put to the estimate directly.
   */
  @Test
  void the_empty_prefix_is_every_text() {
    ColumnStatistics statistics = histogram().toBuilder().setNullCount((long) (ROWS / 10)).build();
    Scanned scanned = scan(withSymbols(statistics), "SELECT * FROM bars WHERE symbol LIKE 'H%'");
    RexBuilder rex = scanned.scan().getCluster().getRexBuilder();
    RexNode everything =
        rex.makeCall(
            SqlStdOperatorTable.LIKE,
            rex.makeInputRef(scanned.scan(), 0),
            rex.makeLiteral("%"));

    ChalkSelectivity.Estimate estimate =
        ChalkSelectivity.of(
            everything,
            scanned.scan().chalkTable(),
            scanned.scan().projection(),
            scanned.scan().getCluster());

    assertThat(estimate.value()).isCloseTo(0.9, within(1e-9));
    assertThat(estimate.guessed()).isFalse();
  }

  /** An escaped wildcard is part of the prefix, and the pattern is still a bare one. */
  @Test
  void an_escaped_wildcard_is_part_of_the_prefix() {
    ChalkSelectivity.Estimate estimate =
        estimate(
            withSymbols(histogram()), "SELECT * FROM bars WHERE symbol LIKE 'H\\_%' ESCAPE '\\'");

    assertThat(estimate.guessed()).isFalse();
    assertThat(estimate.value()).isLessThan(0.25 / 7);
  }

  /** What is not a bare prefix is still Calcite's guess: a range would not be its answer. */
  @Test
  void a_pattern_that_is_not_a_bare_prefix_is_still_a_guess() {
    CatalogContext catalog = withSymbols(histogram());

    assertThat(estimate(catalog, "SELECT * FROM bars WHERE symbol LIKE '%USDT'").guessed()).isTrue();
    assertThat(estimate(catalog, "SELECT * FROM bars WHERE symbol LIKE 'B_C%'").guessed()).isTrue();
    assertThat(estimate(catalog, "SELECT * FROM bars WHERE symbol LIKE 'BTC%T'").guessed()).isTrue();
  }

  // ---- the parts ----

  /** Four equal buckets of {@code symbol}, a thousand distinct texts, from {@code AAAA} to {@code ZZZZ}. */
  private static ColumnStatistics histogram() {
    Histogram.Builder buckets = Histogram.newBuilder();
    for (String upper : new String[] {"FFFF", "MMMM", "TTTT", "ZZZZ"}) {
      buckets.addBuckets(
          HistogramBucket.newBuilder()
              .setUpper(text(upper))
              .setCount((long) (ROWS / 4))
              .setDistinctCount(250));
    }

    return ColumnStatistics.newBuilder()
        .setLevel(StatisticsLevel.STATISTICS_LEVEL_HISTOGRAM)
        .setDistinctCount(1_000)
        .setNullCount(0)
        .setMin(text("AAAA"))
        .setMax(text("ZZZZ"))
        .setHistogram(buckets)
        .build();
  }

  private static Literal text(String value) {
    return Literal.newBuilder().setStringValue(value).build();
  }

  /** The corpus catalog with {@code bars.symbol}'s statistics replaced. */
  private static CatalogContext withSymbols(ColumnStatistics statistics) {
    CatalogContext.Builder catalog = TestCatalogs.corpus().toBuilder();
    for (Schema.Builder schema : catalog.getSchemasBuilderList()) {
      for (Table.Builder table : schema.getTablesBuilderList()) {
        if (table.getName().equals("bars")) {
          for (Column.Builder column : table.getColumnsBuilderList()) {
            if (column.getName().equals("symbol")) {
              column.setStatistics(statistics);
            }
          }
        }
      }
    }

    return catalog.build();
  }

  /** The estimate the optimiser reads for the statement's filter, over the scan it filters. */
  private static ChalkSelectivity.Estimate estimate(CatalogContext catalog, String sql) {
    Scanned scanned = scan(catalog, sql);
    return ChalkSelectivity.of(
        scanned.filter().getCondition(),
        scanned.scan().chalkTable(),
        scanned.scan().projection(),
        scanned.scan().getCluster());
  }

  /** The statement's filter and the scan under it, as planned at NONE. */
  private record Scanned(ChalkFilter filter, ChalkTableScan scan) {}

  private static Scanned scan(CatalogContext catalog, String sql) {
    RegisteredCatalog registered = new CatalogRegistry().register(catalog);
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(registered, PushdownPolicy.none(), SqlConfigs.DEFAULT_CONFORMANCE)) {
      RelNode physical = pipeline.plan(sql, true).physical();
      ChalkFilter filter = find(physical, ChalkFilter.class);
      assertThat(filter).as("a Filter(Scan) at NONE for: " + sql).isNotNull();
      ChalkTableScan scan = find(filter, ChalkTableScan.class);
      assertThat(scan).as("a scan under it").isNotNull();
      return new Scanned(filter, scan);
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException("planning failed for: " + sql, e);
    }
  }

  private static <T extends RelNode> T find(RelNode root, Class<T> type) {
    if (type.isInstance(root)) {
      return type.cast(root);
    }

    for (RelNode input : root.getInputs()) {
      T found = find(input, type);
      if (found != null) {
        return found;
      }
    }

    return null;
  }
}
