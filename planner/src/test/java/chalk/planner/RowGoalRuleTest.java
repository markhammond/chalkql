package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import chalk.planner.diag.PlanText;
import chalk.planner.plan.ChalkRelMetadata;
import chalk.planner.plan.CostModel;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.RowGoal;
import chalk.planner.plan.SqlConfigs;
import chalk.planner.plan.rel.ChalkIndexLookup;
import chalk.planner.plan.rel.ChalkTableScan;
import java.nio.file.Files;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.junit.jupiter.api.Test;

/**
 * D276 — what a limit tells the leaf below it ({@code docs/design/46-row-goals.md} §2, §7).
 *
 * <p>Planned against the recorded corpus catalog, whose {@code bars} is 100 800 rows with a unique
 * ordered index on {@code (symbol, ts)} and a non-unique ordered one on {@code (ts, symbol)}, so the
 * numbers below are the ones the optimiser actually compared.
 */
class RowGoalRuleTest {

  @Test
  void the_goal_a_limit_states_is_offset_plus_fetch_saturating() {
    assertThat(new RowGoal(0, 1).required()).isEqualTo(1);
    assertThat(new RowGoal(3, 5).required()).isEqualTo(8);
    assertThat(new RowGoal(Long.MAX_VALUE, 5).required()).isEqualTo(Long.MAX_VALUE);
  }

  /**
   * The worked example of §2.3, in the corpus's own terms: a range on an ordered index's leading key
   * bound by a parameter, ordered by that key, one row wanted. The lookup used to lose — it was
   * costed for half the table at a guessed selectivity — and with a goal of one it seeks once and
   * stops.
   *
   * <p>The order is {@code (symbol, ts)}, the unique index's, and not {@code bars}'s declared
   * {@code (ts, symbol)}: a plain scan delivers the declared one for nothing, so it is the wrong
   * comparison to make. What §2.3 is about is the statement whose order only an index can serve.
   */
  @Test
  void a_limit_over_an_ordered_lookup_goals_the_lookup_at_one() {
    String text = text(ORDERED_LOOKUP);

    assertThat(text).contains("ChalkIndexLookup");
    assertThat(text).contains("goal=[1]");
    assertThat(text).doesNotContain("ChalkTopN");
  }

  /** The statement of §2.3 whose order only the index serves. */
  private static final String ORDERED_LOOKUP =
      "SELECT symbol, ts FROM bars WHERE symbol >= ? ORDER BY symbol, ts LIMIT 1";

  /**
   * The same shape with no {@code ORDER BY} at all: both goaled alternatives exist, and the scan
   * wins, because any matching row is a right answer and at a guessed half the first one is expected
   * two rows in. The goal at the leaf is {@code ceil(1 / 0.5)}.
   */
  @Test
  void a_limit_over_a_filtered_scan_goals_the_scan_by_the_filters_selectivity() {
    String text = text("SELECT symbol, ts FROM bars WHERE ts >= ? LIMIT 1");

    assertThat(text).contains("ChalkTableScan");
    assertThat(text).contains("goal=[2]");
    assertThat(text).doesNotContain("ChalkIndexLookup");
  }

  /** {@code OFFSET 3 LIMIT 5} wants eight rows of its input, and says so. */
  @Test
  void an_offset_and_a_fetch_sum() {
    assertThat(text("SELECT symbol, ts FROM bars LIMIT 5 OFFSET 3")).contains("goal=[8]");
  }

  /** An offset with no fetch bounds nothing, so there is no goal to state. */
  @Test
  void an_offset_with_no_fetch_states_no_goal() {
    assertThat(text("SELECT symbol, ts FROM bars OFFSET 3 ROWS")).doesNotContain("goal=");
  }

  /**
   * A top-N consumes its input whole, so nothing below it is goaled. {@code volume} carries no
   * index, so this is the shape the rule must leave alone rather than the shape it declines to
   * improve.
   */
  @Test
  void a_top_n_over_an_unindexed_column_keeps_an_ungoaled_scan() {
    String text = text("SELECT symbol, ts FROM bars ORDER BY volume, symbol, ts LIMIT 5");

    assertThat(text).contains("ChalkTopN");
    assertThat(text).doesNotContain("goal=");
  }

  /**
   * A residual the lookup cannot enforce inflates the goal on the way down: to see one row above the
   * filter, the lookup must be read for {@code ceil(1 / sel)} of them. {@code volume > ?} is a
   * comparison against a parameter, which is Calcite's guessed half, so the goal at the lookup is
   * two.
   *
   * <p>The bound on {@code ts} is a literal on purpose. Against a parameter the whole condition is
   * a guess, the goal at a <em>scan</em> is then small as well, and a goaled scan at one unit a row
   * beats a seek at seventeen — which is the right answer and the one §2.3 works through, but it is
   * not the arithmetic this case is about.
   */
  @Test
  void a_residual_above_a_lookup_inflates_the_goal_by_its_selectivity() {
    String text =
        text(
            "SELECT symbol, ts FROM bars "
                + "WHERE ts >= TIMESTAMP '2026-01-14 00:00:00' AND volume > ? "
                + "ORDER BY ts, symbol LIMIT 1");

    assertThat(text).contains("ChalkIndexLookup");
    assertThat(text).contains("goal=[2]");
  }

  /**
   * And the arithmetic compounds down a chain of conjuncts a scan has to carry: an equality guessed
   * at 0.15 and a comparison guessed at a half leave the leaf expecting to be read for
   * {@code ceil(1 / 0.075)} rows.
   */
  @Test
  void a_filters_conjuncts_multiply_into_the_goal() {
    String text =
        text("SELECT symbol, ts FROM bars WHERE symbol = ? AND volume > ? ORDER BY symbol, ts LIMIT 1");

    assertThat(text).contains("ChalkTableScan");
    assertThat(text).contains("goal=[14]");
  }

  /** A projection between the limit and the leaf passes the goal through unchanged. */
  @Test
  void a_projection_passes_the_goal_through_unchanged() {
    String text = text("SELECT symbol || '!' AS tag FROM bars LIMIT 4");

    assertThat(text).contains("ChalkProject");
    assertThat(text).contains("goal=[4]");
  }

  /**
   * The reference (I4) configuration states no goal at all, which is what keeps the corpus's
   * {@code .none} recordings byte-identical: the rule is behind the gate the index rules are behind.
   */
  @Test
  void a_plan_at_none_carries_no_goal() {
    assertThat(text("SELECT symbol, ts FROM bars WHERE ts >= ? LIMIT 1", PushdownPolicy.none()))
        .doesNotContain("goal=");
  }

  /**
   * A goal at or above the leaf's own estimate says nothing, so no alternative is offered: two rels
   * of equal cost would make which one is chosen a matter of iteration order.
   */
  @Test
  void a_goal_the_leaf_already_beats_is_not_offered() {
    // symbols is five rows, so a goal of a thousand says nothing the scan did not already know.
    assertThat(text("SELECT symbol FROM symbols LIMIT 1000")).doesNotContain("goal=");
  }

  /** The goaled leaf estimates and costs for the rows it will be pulled for (cost model v8). */
  @Test
  void a_goaled_scan_estimates_and_costs_for_its_goal() {
    Planned planned = plan("SELECT symbol, ts FROM bars LIMIT 5 OFFSET 3");
    ChalkTableScan scan = find(planned.physical(), ChalkTableScan.class);

    assertThat(scan).isNotNull();
    assertThat(scan.rowGoal()).isEqualTo(8);
    assertThat(planned.mq().getRowCount(scan)).isEqualTo(8.0);

    // The eight rows it will be read for, at one unit a row, for the share of the table's columns
    // it projects — the v6 formula, over the goaled count rather than the table's.
    double share =
        (double) scan.getRowType().getFieldCount()
            / scan.chalkTable().descriptor().getColumnsCount();
    assertThat(cost(scan, planned))
        .isCloseTo(CostModel.defaults().scan(8, share), org.assertj.core.data.Offset.offset(1e-9));
    assertThat(cost(scan, planned)).isLessThan(CostModel.defaults().scan(100_800, share));
  }

  /** And a goaled lookup pays its seeks and the rows it will be pulled for, not its whole range. */
  @Test
  void a_goaled_lookup_costs_a_seek_and_its_goal() {
    Planned planned = plan(ORDERED_LOOKUP);
    ChalkIndexLookup lookup = find(planned.physical(), ChalkIndexLookup.class);

    assertThat(lookup).isNotNull();
    assertThat(lookup.rowGoal()).isEqualTo(1);
    assertThat(planned.mq().getRowCount(lookup)).isEqualTo(1.0);

    // One seek at 17 and one row at 4, against the 50 400 rows the guessed half would have cost.
    assertThat(cost(lookup, planned)).isEqualTo(CostModel.defaults().lookup(1, 1));
    assertThat(cost(lookup, planned)).isEqualTo(21.0);
  }

  /** Both the estimate and the goal are visible to a human staring at a bad plan. */
  @Test
  void a_goaled_lookup_shows_its_selectivity_and_its_goal_at_all_attributes() {
    String text = PlanText.withCosts(plan(ORDERED_LOOKUP).physical());

    assertThat(text).contains("sel=");
    assertThat(text).contains("goal=[1]");
  }

  // ---- support ----

  private record Planned(RelNode physical, RelMetadataQuery mq) {}

  private static double cost(RelNode node, Planned planned) {
    org.apache.calcite.plan.RelOptCost cost =
        node.computeSelfCost(node.getCluster().getPlanner(), planned.mq());
    assertThat(cost).isNotNull();
    return cost.getRows();
  }

  private static String text(String sql) {
    return text(sql, PushdownPolicy.full());
  }

  private static String text(String sql, PushdownPolicy policy) {
    return PlanText.withAttributes(plan(sql, policy).physical());
  }

  private static Planned plan(String sql) {
    return plan(sql, PushdownPolicy.full());
  }

  private static Planned plan(String sql, PushdownPolicy policy) {
    assumeTrue(
        Files.exists(CorpusQueries.corpusDir().resolve("schemas/corpus.binpb")),
        "the recorded corpus catalog carries the statistics the cost model reads");

    CorpusPlanner planner = new CorpusPlanner();
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(planner.catalog(), policy, SqlConfigs.DEFAULT_CONFORMANCE)) {
      RelNode physical = pipeline.plan(sql, true).physical();
      physical.getCluster().setMetadataProvider(ChalkRelMetadata.SOURCE);
      physical.getCluster().invalidateMetadataQuery();
      return new Planned(physical, RelMetadataQuery.instance());
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
