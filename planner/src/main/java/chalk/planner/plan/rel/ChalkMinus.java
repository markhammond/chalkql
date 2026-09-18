package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.CostModel;
import java.util.List;
import org.apache.calcite.plan.DeriveMode;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Minus;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code EXCEPT} and {@code EXCEPT ALL} (D69, {@code 15-zero-allocation-execution.md} §6). Maps onto
 * the IR's {@code SetOp} with kind {@code EXCEPT_DISTINCT} or {@code EXCEPT_ALL}.
 *
 * <p>{@code MINUS_TO_ANTI_JOIN} is in the rule set, so a single-column {@code EXCEPT DISTINCT} is
 * costed against the anti join it can also be — which is where the joins corpus finally gets an
 * {@code ANTI} join of its own (V29).
 */
public final class ChalkMinus extends Minus implements ChalkRel {
  private ChalkMinus(RelOptCluster cluster, RelTraitSet traits, List<RelNode> inputs, boolean all) {
    super(cluster, traits, inputs, all);
  }

  public static ChalkMinus create(RelOptCluster cluster, List<RelNode> inputs, boolean all) {
    return new ChalkMinus(cluster, cluster.traitSetOf(ChalkConvention.LOCAL), inputs, all);
  }

  @Override
  public ChalkMinus copy(RelTraitSet traitSet, List<RelNode> inputs, boolean all) {
    return new ChalkMinus(getCluster(), traitSet, inputs, all);
  }

  @Override
  public DeriveMode getDeriveMode() {
    return DeriveMode.PROHIBITED;
  }

  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    return CostModel.setOpCost(this, planner, mq, /* passThrough= */ false);
  }
}
