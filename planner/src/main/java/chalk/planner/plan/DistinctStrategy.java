package chalk.planner.plan;

import chalk.ir.v1.ColumnStatistics;
import chalk.ir.v1.StatisticsLevel;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.plan.rel.ChalkTableScan;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.core.AggregateCall;
import org.apache.calcite.rel.core.Filter;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.core.Sort;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.util.ImmutableBitSet;

/**
 * D54 — how a {@code DISTINCT} aggregate is evaluated ({@code 13-window-functions.md} §5).
 *
 * <p>There are two correct strategies and the planner picks by cost:
 *
 * <ul>
 *   <li><b>{@link #NATIVE}</b> — no expansion at all. One {@code HashAggregate} carries the distinct
 *       measures and the executor keeps a per-group set, which costs one extra pass over the values
 *       and memory proportional to the number of distinct (group, value) pairs.
 *   <li><b>{@link #JOIN}</b> — Calcite's {@code AggregateExpandDistinctAggregatesRule} in its JOIN
 *       configuration: one aggregate per distinct column, joined back on the grouping keys. It costs
 *       a join and a second pass and bounds memory by the group count instead.
 * </ul>
 *
 * <p>The grouping-sets configuration, which M1 used, is not an option: the IR carries exactly one
 * grouping (I-IR-7) and the executor has no grouping-sets path, so mixing a {@code DISTINCT}
 * aggregate with a plain one was simply unplannable before this.
 *
 * <p>The estimate is the one the M2 statistics can actually answer: for each distinct measure, the
 * number of distinct (group, value) pairs is at most {@code distinct_count(column) × groups} and at
 * most the input's rows — every row contributes at most one value to at most one group's set.
 * Summed over the measures and compared with {@link #MAX_DISTINCT_VALUES}. A column the source has
 * not measured falls back to the row-count bound alone, which is honest rather than a guess and
 * errs towards the strategy whose memory is bounded by the group count.
 */
public enum DistinctStrategy {
  /** Keep the distinct measures; the executor evaluates them in one pass. */
  NATIVE,

  /** Expand into one aggregate per distinct column, joined on the grouping keys. */
  JOIN;

  /**
   * The named threshold of §5: the total number of distinct values, across every group and every
   * distinct measure, below which the single native pass is chosen.
   */
  public static final long MAX_DISTINCT_VALUES = 100_000L;

  /** Which strategy this tree gets. A tree with no distinct aggregate at all gets {@link #NATIVE}. */
  public static DistinctStrategy of(RelNode logical) {
    return of(logical, MAX_DISTINCT_VALUES);
  }

  /** The same, against a given threshold, so the choice is testable on both sides of it. */
  public static DistinctStrategy of(RelNode logical, long maxDistinctValues) {
    return worstCase(logical) > maxDistinctValues ? JOIN : NATIVE;
  }

  /**
   * The largest number of distinct values any aggregate in the tree would have to hold at once, or
   * {@link Double#MAX_VALUE} when a measure's column has no statistics.
   */
  private static double worstCase(RelNode rel) {
    double worst = 0;
    if (rel instanceof Aggregate aggregate) {
      worst = distinctValues(aggregate);
    }

    for (RelNode input : rel.getInputs()) {
      worst = Math.max(worst, worstCase(input));
    }

    return worst;
  }

  private static double distinctValues(Aggregate aggregate) {
    double total = 0;
    boolean any = false;
    RelMetadataQuery mq = aggregate.getCluster().getMetadataQuery();
    double inputRows = mq.getRowCount(aggregate.getInput());
    double groups = mq.getRowCount(aggregate);

    for (AggregateCall call : aggregate.getAggCallList()) {
      if (!call.isDistinct()) {
        continue;
      }

      any = true;
      if (call.getArgList().size() != 1) {
        // COUNT(DISTINCT a, b) has no native path: the executor's set is keyed on one lane.
        return Double.MAX_VALUE;
      }

      // Every row contributes at most one value to at most one group's set, so the input's row
      // count is the bound whatever the column's cardinality turns out to be. That is the honest
      // answer for a column the source has not measured: not "infinite", and not a guess either.
      Double distinct = distinctCount(aggregate.getInput(), call.getArgList().get(0));
      total +=
          distinct == null
              ? inputRows
              : Math.min(inputRows, distinct * Math.max(groups, 1.0));
    }

    // Grouping sets have no native path either; a tree that still has them has already lost.
    if (any && aggregate.getGroupSets().size() != 1) {
      return Double.MAX_VALUE;
    }

    return total;
  }

  /**
   * The declared {@code distinct_count} of the table column a field of {@code rel} comes from, or
   * null when the chain of projections and filters does not lead to one.
   *
   * <p>Deliberately structural rather than a metadata query: Calcite's {@code RelMdDistinctRowCount}
   * has no handler that reads a source's declared statistics, and guessing from row counts would
   * make the strategy depend on an estimate nobody measured.
   */
  private static Double distinctCount(RelNode rel, int field) {
    RelNode current = rel;
    int index = field;
    while (true) {
      if (current instanceof org.apache.calcite.plan.volcano.RelSubset subset) {
        RelNode best = subset.getBest();
        current = best != null ? best : subset.getOriginal();
        continue;
      }

      if (current instanceof Project project) {
        RexNode expression = project.getProjects().get(index);
        if (!(expression instanceof RexInputRef ref)) {
          return null;
        }

        index = ref.getIndex();
        current = project.getInput();
        continue;
      }

      if (current instanceof Filter || current instanceof Sort) {
        current = current.getInput(0);
        continue;
      }

      break;
    }

    if (!(current instanceof TableScan scan)) {
      return null;
    }

    ChalkTable table = scan.getTable().unwrap(ChalkTable.class);
    if (table == null) {
      return null;
    }

    int column =
        scan instanceof ChalkTableScan chalkScan ? chalkScan.projection().get(index) : index;
    ColumnStatistics statistics = table.statistics(column);
    if (statistics.getLevel() == StatisticsLevel.STATISTICS_LEVEL_UNKNOWN
        || statistics.getDistinctCount() <= 0) {
      return null;
    }

    return (double) statistics.getDistinctCount();
  }

  /** Unused today; kept so a caller can name the columns a decision was made from. */
  static ImmutableBitSet columns(Aggregate aggregate) {
    ImmutableBitSet.Builder builder = ImmutableBitSet.builder();
    for (AggregateCall call : aggregate.getAggCallList()) {
      if (call.isDistinct()) {
        call.getArgList().forEach(builder::set);
      }
    }
    return builder.build();
  }

  /** A stable summary for {@code PlannerConfig.configHash()}. */
  public static String summary() {
    return "distinct=native<=" + MAX_DISTINCT_VALUES + ",join>";
  }
}
