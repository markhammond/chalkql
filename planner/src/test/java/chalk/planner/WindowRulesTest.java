package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.FrameBoundKind;
import chalk.ir.v1.FrameExclusion;
import chalk.ir.v1.FrameMode;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.ir.v1.SortDirection;
import chalk.ir.v1.Window;
import chalk.ir.v1.WindowFunctionId;
import chalk.planner.UnsupportedFeatureException;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.diag.PlanText;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * The window planner rules (D48–D50, {@code 13-window-functions.md} §3 and §7): how a
 * {@code LogicalWindow}'s groups become a stack, what ordering a {@code ChalkWindow} asks its input
 * for, and whether that ordering is served by an index-ordered scan or by a sort.
 *
 * <p>These plan against the <b>large</b> tables, which is where §8's "queries 02, 03 and 16 plan with
 * no Sort" claim belongs: the corpus runs the same shapes on {@code bars_small} so the reference
 * executor can check the results, and the claim about a 100 800-row table is checked here.
 */
class WindowRulesTest {
  private static RegisteredCatalog catalog;
  private static CorpusPlanner planner;

  @BeforeAll
  static void registerCatalog() {
    catalog = new CatalogRegistry().register(TestCatalogs.corpus());
    planner = new CorpusPlanner();
  }

  private static String plan(String sql) {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      return PlanText.withAttributes(pipeline.plan(sql, false).physical());
    } catch (Exception e) {
      throw new IllegalStateException("planning failed for: " + sql, e);
    }
  }

  private static List<Window> windows(Plan plan) {
    List<Window> found = new ArrayList<>();
    collect(plan.getRoot(), found);
    return found;
  }

  private static void collect(Rel rel, List<Window> found) {
    if (rel.getKindCase() == Rel.KindCase.WINDOW) {
      found.add(rel.getWindow());
      collect(rel.getWindow().getInput(), found);
      return;
    }

    for (Rel input : inputs(rel)) {
      collect(input, found);
    }
  }

  private static List<Rel> inputs(Rel rel) {
    return switch (rel.getKindCase()) {
      case FILTER -> List.of(rel.getFilter().getInput());
      case PROJECT -> List.of(rel.getProject().getInput());
      case SORT -> List.of(rel.getSort().getInput());
      case FETCH -> List.of(rel.getFetch().getInput());
      case TOP_N -> List.of(rel.getTopN().getInput());
      case HASH_AGGREGATE -> List.of(rel.getHashAggregate().getAggregate().getInput());
      case AGGREGATE -> List.of(rel.getAggregate().getInput());
      case HASH_JOIN -> List.of(rel.getHashJoin().getLeft(), rel.getHashJoin().getRight());
      case MERGE_JOIN -> List.of(rel.getMergeJoin().getLeft(), rel.getMergeJoin().getRight());
      case NESTED_LOOP_JOIN ->
          List.of(rel.getNestedLoopJoin().getLeft(), rel.getNestedLoopJoin().getRight());
      default -> List.of();
    };
  }

  /**
   * D50 on the table the design names: a per-symbol moving average over 100 800 rows reads
   * {@code bars} through its {@code (symbol, ts)} index instead of sorting it.
   */
  @Test
  void a_per_symbol_window_over_bars_streams_out_of_the_index_rather_than_sorting() {
    String text =
        plan("SELECT symbol, ts, AVG(\"close\") OVER "
            + "(PARTITION BY symbol ORDER BY ts ROWS 19 PRECEDING) FROM bars");

    assertThat(text).contains("ChalkWindow");
    assertThat(text).contains("ChalkIndexLookup");
    assertThat(text).contains("ix_bars_symbol_ts");
    assertThat(text).doesNotContain("ChalkSort");
  }

  /** The same for the interval RANGE frame, which is §8's query 03. */
  @Test
  void a_range_frame_over_bars_streams_out_of_the_index_too() {
    String text =
        plan("SELECT symbol, ts, AVG(\"close\") OVER "
            + "(PARTITION BY symbol ORDER BY ts RANGE INTERVAL '1' HOUR PRECEDING) FROM bars");

    assertThat(text).contains("ChalkIndexLookup");
    assertThat(text).doesNotContain("ChalkSort");
  }

  /** §8's query 16: {@code lineitem} already declares the ordering, so no index is needed either. */
  @Test
  void a_window_over_lineitem_uses_the_declared_collation() {
    String text =
        plan("SELECT l_orderkey, l_linenumber, SUM(l_quantity) OVER "
            + "(PARTITION BY l_orderkey ORDER BY l_linenumber) FROM lineitem");

    assertThat(text).contains("ChalkWindow");
    assertThat(text).doesNotContain("ChalkSort");
    assertThat(text).doesNotContain("ChalkIndexLookup");
  }

  /**
   * And where no index fits, a sort is what satisfies the requirement — the other half of the
   * choice. {@code bars} has no index on {@code volume}.
   */
  @Test
  void a_window_ordered_by_an_unindexed_column_sorts() {
    String text =
        plan("SELECT symbol, RANK() OVER (PARTITION BY symbol ORDER BY volume DESC) FROM bars");

    assertThat(text).contains("ChalkWindow");
    assertThat(text).contains("ChalkSort");
  }

  /**
   * The index is ascending, so a DESC order key is not one of the orderings it delivers and the sort
   * comes back. This is what corpus query 01 records.
   */
  @Test
  void a_descending_order_key_is_not_served_by_an_ascending_index() {
    String text =
        plan("SELECT symbol, ROW_NUMBER() OVER (PARTITION BY symbol ORDER BY ts DESC) FROM bars");

    assertThat(text).contains("ChalkSort");
  }

  /** D48: a {@code LogicalWindow} with several groups becomes a stack, in Calcite's group order. */
  @Test
  void several_groups_become_a_stack_of_windows_in_calcite_order() {
    Plan plan =
        planner.plan(
            "SELECT symbol, SUM(volume) OVER (PARTITION BY symbol ORDER BY ts) AS a, "
                + "COUNT(*) OVER (PARTITION BY ts) AS b FROM bars_small");

    List<Window> windows = windows(plan);
    assertThat(windows).hasSize(2);

    // Stacked outermost first: the outer node is the later group, and its input is the earlier one.
    assertThat(windows.get(0).getPartitionKeysList()).containsExactly(1);
    assertThat(windows.get(1).getPartitionKeysList()).containsExactly(0);
    assertThat(windows.get(0).getInput().getKindCase()).isEqualTo(Rel.KindCase.SORT);
  }

  /** One group, however many calls use it: a named window is not two windows. */
  @Test
  void one_group_serves_every_call_that_shares_it() {
    Plan plan =
        planner.plan(
            "SELECT symbol, SUM(volume) OVER w, COUNT(*) OVER w FROM bars_small "
                + "WINDOW w AS (PARTITION BY symbol ORDER BY ts)");

    List<Window> windows = windows(plan);
    assertThat(windows).hasSize(1);
    assertThat(windows.get(0).getCallsCount()).isEqualTo(2);
  }

  /** D48: the planner resolves SQL's default frames and writes down the bounds it resolved. */
  @Test
  void the_default_frames_are_resolved_and_written_down() {
    Window ordered =
        windows(planner.plan(
                "SELECT symbol, SUM(volume) OVER (PARTITION BY symbol ORDER BY ts) FROM bars_small"))
            .get(0);
    assertThat(ordered.getFrame().getMode()).isEqualTo(FrameMode.FRAME_MODE_RANGE);
    assertThat(ordered.getFrame().getLower().getKind())
        .isEqualTo(FrameBoundKind.FRAME_BOUND_KIND_UNBOUNDED_PRECEDING);
    assertThat(ordered.getFrame().getUpper().getKind())
        .isEqualTo(FrameBoundKind.FRAME_BOUND_KIND_CURRENT_ROW);
    assertThat(ordered.getFrame().getExclusion()).isEqualTo(FrameExclusion.FRAME_EXCLUSION_NO_OTHERS);

    Window whole =
        windows(planner.plan(
                "SELECT symbol, COUNT(*) OVER (PARTITION BY symbol) FROM bars_small"))
            .get(0);
    assertThat(whole.getFrame().getUpper().getKind())
        .isEqualTo(FrameBoundKind.FRAME_BOUND_KIND_UNBOUNDED_FOLLOWING);
  }

  /** The required input ordering is (partition keys ASC NULLS LAST, then the order keys). */
  @Test
  void the_window_asks_its_input_for_partition_keys_then_order_keys() {
    Window window =
        windows(planner.plan(
                "SELECT symbol, ROW_NUMBER() OVER (PARTITION BY symbol ORDER BY ts DESC) "
                    + "FROM bars_small"))
            .get(0);

    assertThat(window.getPartitionKeysList()).containsExactly(0);
    assertThat(window.getOrderCount()).isEqualTo(1);
    assertThat(window.getOrder(0).getExpr().getFieldRef().getIndex()).isEqualTo(1);
    assertThat(window.getOrder(0).getDirection())
        .isEqualTo(SortDirection.SORT_DIRECTION_DESC_NULLS_FIRST);
  }

  /** Calcite's constant references are resolved into literals before the IR is built. */
  @Test
  void a_frame_offset_and_a_bucket_count_reach_the_ir_as_literals() {
    Window rows =
        windows(planner.plan(
                "SELECT symbol, SUM(volume) OVER "
                    + "(PARTITION BY symbol ORDER BY ts ROWS 19 PRECEDING) FROM bars_small"))
            .get(0);
    assertThat(rows.getFrame().getMode()).isEqualTo(FrameMode.FRAME_MODE_ROWS);
    assertThat(rows.getFrame().getLower().getOffset().getLiteral().getI64Value()).isEqualTo(19);

    Window ntile =
        windows(planner.plan(
                "SELECT symbol, NTILE(4) OVER (PARTITION BY symbol ORDER BY ts) FROM bars_small"))
            .get(0);
    assertThat(ntile.getCalls(0).getWindowFunction()).isEqualTo(WindowFunctionId.WINDOW_FUNCTION_ID_NTILE);
    assertThat(ntile.getCalls(0).getArgs(0).getLiteral().getI64Value()).isEqualTo(4);
  }

  /** An interval RANGE offset arrives as microseconds, whatever unit the query wrote it in. */
  @Test
  void an_interval_range_offset_arrives_in_microseconds() {
    Window window =
        windows(planner.plan(
                "SELECT symbol, AVG(\"close\") OVER (PARTITION BY symbol ORDER BY ts "
                    + "RANGE INTERVAL '1' HOUR PRECEDING) FROM bars_small"))
            .get(0);

    assertThat(window.getFrame().getMode()).isEqualTo(FrameMode.FRAME_MODE_RANGE);
    assertThat(window.getFrame().getLower().getOffset().getLiteral().getIntervalDayValue())
        .isEqualTo(3_600_000_000L);
  }

  /** V17: EXCLUDE reaches the IR as the exclusion the query wrote. */
  @Test
  void exclude_reaches_the_ir() {
    Window window =
        windows(planner.plan(
                "SELECT symbol, SUM(volume) OVER (PARTITION BY symbol ORDER BY ts "
                    + "ROWS 2 PRECEDING EXCLUDE CURRENT ROW) FROM bars_small"))
            .get(0);

    assertThat(window.getFrame().getExclusion())
        .isEqualTo(FrameExclusion.FRAME_EXCLUSION_CURRENT_ROW);
  }

  /** V18: IGNORE NULLS reaches the IR on the call it belongs to. */
  @Test
  void ignore_nulls_reaches_the_ir() {
    Window window =
        windows(planner.plan(
                "SELECT symbol, LAG(vwap, 1) IGNORE NULLS OVER (PARTITION BY symbol ORDER BY ts) "
                    + "FROM bars_small"))
            .get(0);

    assertThat(window.getCalls(0).getIgnoreNulls()).isTrue();
    assertThat(window.getCalls(0).getWindowFunction())
        .isEqualTo(WindowFunctionId.WINDOW_FUNCTION_ID_LAG);
  }

  /** §9: what the step refuses, and with the message that names where it is written down. */
  @Test
  void distinct_inside_a_window_aggregate_reaches_the_ir(){
    // D56: what step 18 refused, step 19 executes through a counted multiset.
    Plan plan =
        planner.plan(
            "SELECT symbol, COUNT(DISTINCT volume) OVER (PARTITION BY symbol) FROM bars_small");

    assertThat(windows(plan)).hasSize(1);
    assertThat(windows(plan).get(0).getCalls(0).getDistinct()).isTrue();
  }

  @Test
  void an_aggregate_the_ir_has_no_id_for_is_refused() {
    assertThatThrownBy(
            () -> planner.plan(
                "SELECT symbol, COLLECT(volume) OVER (PARTITION BY symbol ORDER BY ts) "
                    + "FROM bars_small"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("COLLECT");
  }

  /** Planning is deterministic: the same SQL gives the same plan text a hundred times over. */
  @Test
  void a_window_plan_is_deterministic() {
    String first =
        plan("SELECT symbol, SUM(volume) OVER (PARTITION BY symbol ORDER BY ts), "
            + "COUNT(*) OVER (PARTITION BY ts) FROM bars");
    for (int i = 0; i < 50; i++) {
      assertThat(
              plan("SELECT symbol, SUM(volume) OVER (PARTITION BY symbol ORDER BY ts), "
                  + "COUNT(*) OVER (PARTITION BY ts) FROM bars"))
          .isEqualTo(first);
    }
  }
}
