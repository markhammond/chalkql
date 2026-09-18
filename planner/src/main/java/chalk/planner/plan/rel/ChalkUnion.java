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
import org.apache.calcite.rel.core.Union;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code UNION} and {@code UNION ALL}, in {@code ChalkConvention.LOCAL} (D69,
 * {@code 15-zero-allocation-execution.md} §6). Maps onto the IR's {@code SetOp} with kind
 * {@code UNION_ALL} or {@code UNION_DISTINCT}.
 *
 * <p>It claims no ordering: an n-ary set operation delivers none, so {@code deriveTraits} and
 * {@code passThroughTraits} both decline and a {@code Sort} above one is a real sort. That is what
 * the corpus asserts, and it is why Calcite's {@code SORT_UNION_TRANSPOSE} rules stay out of the
 * rule set.
 */
public final class ChalkUnion extends Union implements ChalkRel {
  private ChalkUnion(RelOptCluster cluster, RelTraitSet traits, List<RelNode> inputs, boolean all) {
    super(cluster, traits, inputs, all);
  }

  public static ChalkUnion create(RelOptCluster cluster, List<RelNode> inputs, boolean all) {
    return new ChalkUnion(cluster, cluster.traitSetOf(ChalkConvention.LOCAL), inputs, all);
  }

  @Override
  public ChalkUnion copy(RelTraitSet traitSet, List<RelNode> inputs, boolean all) {
    return new ChalkUnion(getCluster(), traitSet, inputs, all);
  }

  /** No ordering to derive from any input: the operator concatenates them. */
  @Override
  public DeriveMode getDeriveMode() {
    return DeriveMode.PROHIBITED;
  }

  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    return CostModel.setOpCost(this, planner, mq, /* passThrough= */ all);
  }
}
