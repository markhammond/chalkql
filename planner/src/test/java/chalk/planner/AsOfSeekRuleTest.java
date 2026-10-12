package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.AsOfLookupJoin;
import chalk.ir.v1.AsOfMatch;
import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.IndexReversal;
import chalk.ir.v1.JoinType;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.ir.v1.Schema;
import chalk.ir.v1.Table;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.diag.PlanText;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import java.util.List;
import org.junit.jupiter.api.Test;

/**
 * An ASOF join that seeks an index (D338, design 63 §4): offered beside the plain join over an
 * in-process table whose ordered index begins with the join's keys and goes on with its time, and
 * chosen by cost. {@code bars} has 100 800 rows and a unique index on {@code (symbol, ts)}, read
 * backwards over any range.
 */
class AsOfSeekRuleTest {
  private static final String PROBES =
      "WITH q (symbol, t0) AS (VALUES ('BTCUSDT', TIMESTAMP '2026-01-01 00:00:00'),"
          + " ('ETHUSDT', TIMESTAMP '2026-01-02 00:00:00')) ";

  private static String physical(CatalogContext catalog, String sql) {
    RegisteredCatalog registered = new CatalogRegistry().register(catalog);
    try (PlannerPipeline pipeline = PlannerPipeline.create(registered, PushdownPolicy.full())) {
      return PlanText.withAttributes(pipeline.plan(sql, false).physical());
    } catch (Exception e) {
      throw new AssertionError("planning failed for: " + sql, e);
    }
  }

  private static AsOfLookupJoin seek(Plan plan) {
    List<Rel> found = IrNodes.of(plan, Rel.KindCase.AS_OF_LOOKUP_JOIN);
    assertThat(found).hasSize(1);
    return found.get(0).getAsOfLookupJoin();
  }

  /** Two seeks of a 100 800-row table: the keys, then the time, read backwards for {@code >=}. */
  @Test
  void a_few_left_rows_seek_a_long_table() {
    AsOfLookupJoin seek =
        seek(
            new CorpusPlanner()
                .plan(
                    PROBES
                        + "SELECT q.symbol, b.vwap FROM q ASOF JOIN bars b"
                        + " MATCH_CONDITION q.t0 >= b.ts ON q.symbol = b.symbol"));

    assertThat(seek.getIndex()).isEqualTo("ix_bars_symbol_ts");
    assertThat(seek.getRightKeysCount()).isEqualTo(1);
    assertThat(seek.getMatch()).isEqualTo(AsOfMatch.AS_OF_MATCH_GE);
    assertThat(seek.getType()).isEqualTo(JoinType.JOIN_TYPE_INNER);
    assertThat(seek.getFixedColumnsCount()).isZero();
  }

  /** A condition on the right input that fixes a key column becomes part of the seek. */
  @Test
  void a_constant_key_is_a_fixed_column() {
    AsOfLookupJoin seek =
        seek(
            new CorpusPlanner()
                .plan(
                    "WITH q (t0) AS (VALUES (TIMESTAMP '2026-01-01 00:00:00'))"
                        + " SELECT b.vwap FROM q LEFT ASOF JOIN bars b"
                        + " MATCH_CONDITION q.t0 >= b.ts ON b.symbol = 'BTCUSDT'"));

    assertThat(seek.getLeftKeysCount()).isZero();
    assertThat(seek.getFixedColumnsList()).containsExactly(0);
    assertThat(seek.getFixedValues(0).getLiteral().getStringValue()).isEqualTo("BTCUSDT");
    assertThat(seek.getType()).isEqualTo(JoinType.JOIN_TYPE_LEFT);
  }

  /** A match on a column the index does not go on with has no index to seek. */
  @Test
  void no_index_fits_a_time_the_index_does_not_have() {
    String plan =
        physical(
            TestCatalogs.corpus(),
            "WITH q (symbol, x) AS (VALUES ('BTCUSDT', 1.0E0))"
                + " SELECT q.symbol, b.vwap FROM q ASOF JOIN bars b"
                + " MATCH_CONDITION q.x >= b.high ON q.symbol = b.symbol");

    assertThat(plan).contains("ChalkAsOfJoin").doesNotContain("ChalkAsOfLookupJoin");
  }

  /**
   * Reading backwards from a bound needs an index that reads backwards over any range. Without it,
   * {@code >=} reads the table, and {@code <=}, which reads forwards, still seeks.
   */
  @Test
  void a_backward_seek_needs_an_index_that_reads_backwards() {
    CatalogContext.Builder catalog = TestCatalogs.corpus().toBuilder();
    for (Schema.Builder schema : catalog.getSchemasBuilderList()) {
      for (Table.Builder table : schema.getTablesBuilderList()) {
        if (table.getName().equals("bars")) {
          for (chalk.ir.v1.Index.Builder index : table.getIndexesBuilderList()) {
            index.setReversal(IndexReversal.INDEX_REVERSAL_UNSPECIFIED);
          }
        }
      }
    }

    String backwards =
        physical(
            catalog.build(),
            PROBES
                + "SELECT q.symbol, b.vwap FROM q ASOF JOIN bars b"
                + " MATCH_CONDITION q.t0 >= b.ts ON q.symbol = b.symbol");
    String forwards =
        physical(
            catalog.build(),
            PROBES
                + "SELECT q.symbol, b.vwap FROM q ASOF JOIN bars b"
                + " MATCH_CONDITION q.t0 <= b.ts ON q.symbol = b.symbol");

    assertThat(backwards).doesNotContain("ChalkAsOfLookupJoin");
    assertThat(forwards).contains("ChalkAsOfLookupJoin");
  }

  /**
   * Cost chooses: every one of 100 800 bars seeking the 5 000-row {@code bars_small} costs more
   * than reading it once.
   */
  @Test
  void many_left_rows_read_a_short_table() {
    String plan =
        physical(
            TestCatalogs.corpus(),
            "SELECT b.symbol, s.vwap FROM bars b ASOF JOIN bars_small s"
                + " MATCH_CONDITION b.ts >= s.ts ON b.symbol = s.symbol");

    assertThat(plan).contains("ChalkAsOfJoin").doesNotContain("ChalkAsOfLookupJoin");
  }
}
