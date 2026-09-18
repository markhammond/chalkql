package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollationTraitDef;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Sort;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rex.RexNode;
import org.checkerframework.checker.nullness.qual.Nullable;

/** A total sort with no limit. Maps to the IR's {@code Sort}. */
public final class ChalkSort extends Sort implements ChalkRel {

  private ChalkSort(
      RelOptCluster cluster, RelTraitSet traitSet, RelNode input, RelCollation collation) {
    super(cluster, traitSet, input, collation);
  }

  public static ChalkSort create(RelNode input, RelCollation collation) {
    RelOptCluster cluster = input.getCluster();
    RelTraitSet traits =
        cluster.traitSetOf(ChalkConvention.LOCAL).replace(collation);
    return new ChalkSort(cluster, traits, input, collation);
  }

  @Override
  public ChalkSort copy(
      RelTraitSet traitSet,
      RelNode input,
      RelCollation collation,
      @Nullable RexNode offset,
      @Nullable RexNode fetch) {
    if (offset != null || fetch != null) {
      throw new IllegalArgumentException("ChalkSort never carries offset or fetch; use ChalkTopN");
    }
    return new ChalkSort(getCluster(), traitSet, input, collation);
  }

  /**
   * {@code rows × log2(rows) × keys}, in <b>both</b> slots.
   *
   * <p>Only the first argument of {@code makeCost} is ever compared — {@code VolcanoCost.isLt} reads
   * {@code rowCount} and nothing else (ADR 0015) — so a sort that put its row count there and its
   * work in {@code cpu} was, to the optimiser, exactly as expensive as reading its input. Nothing
   * could ever beat it. That was harmless while the only alternative to sorting was "the input
   * already delivers the order, for free"; D50's index-ordered scan is the first alternative that
   * costs something, and it can only be chosen if a sort costs something too
   * ({@code 13-window-functions.md} §3, ADR 0017).
   */
  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double rows = mq.getRowCount(getInput());
    int keys = Math.max(collation.getFieldCollations().size(), 1);
    double work = rows * log2(rows + 1) * keys;
    return planner.getCostFactory().makeCost(work, work, 0);
  }

  static double log2(double value) {
    return Math.log(value) / Math.log(2);
  }
}
