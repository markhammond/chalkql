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
import org.apache.calcite.rel.core.Intersect;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code INTERSECT} and {@code INTERSECT ALL} (D69, {@code 15-zero-allocation-execution.md} §6).
 * Maps onto the IR's {@code SetOp} with kind {@code INTERSECT_DISTINCT} or {@code INTERSECT_ALL}.
 *
 * <p>{@code INTERSECT_TO_DISTINCT} stays out of the rule set: the operator counts keys in one hash
 * table and probes, which is cheaper than the aggregate-and-join rewrite it would become.
 */
public final class ChalkIntersect extends Intersect implements ChalkRel {
  private ChalkIntersect(
      RelOptCluster cluster, RelTraitSet traits, List<RelNode> inputs, boolean all) {
    super(cluster, traits, inputs, all);
  }

  public static ChalkIntersect create(RelOptCluster cluster, List<RelNode> inputs, boolean all) {
    return new ChalkIntersect(cluster, cluster.traitSetOf(ChalkConvention.LOCAL), inputs, all);
  }

  @Override
  public ChalkIntersect copy(RelTraitSet traitSet, List<RelNode> inputs, boolean all) {
    return new ChalkIntersect(getCluster(), traitSet, inputs, all);
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
