package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import com.google.common.collect.ImmutableList;
import java.util.List;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.InvalidRelException;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.core.AggregateCall;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.util.ImmutableBitSet;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Hash-based grouping. Maps to the IR's {@code HashAggregate}; output order is first-seen group
 * order, which the IR does not promise, so a planner needing order adds a {@code Sort}.
 */
public final class ChalkHashAggregate extends Aggregate implements ChalkRel {

  private ChalkHashAggregate(
      RelOptCluster cluster,
      RelTraitSet traitSet,
      RelNode input,
      ImmutableBitSet groupSet,
      @Nullable List<ImmutableBitSet> groupSets,
      List<AggregateCall> aggCalls) {
    super(cluster, traitSet, ImmutableList.of(), input, groupSet, groupSets, aggCalls);
  }

  public static ChalkHashAggregate create(
      RelNode input,
      ImmutableBitSet groupSet,
      @Nullable List<ImmutableBitSet> groupSets,
      List<AggregateCall> aggCalls)
      throws InvalidRelException {
    RelOptCluster cluster = input.getCluster();
    // Hashing destroys the input ordering, so the trait set claims none.
    RelTraitSet traits = cluster.traitSetOf(ChalkConvention.LOCAL);
    ChalkHashAggregate aggregate =
        new ChalkHashAggregate(cluster, traits, input, groupSet, groupSets, aggCalls);
    if (aggregate.getGroupSets().size() != 1) {
      throw new InvalidRelException("grouping sets are not supported in this milestone");
    }
    return aggregate;
  }

  @Override
  public ChalkHashAggregate copy(
      RelTraitSet traitSet,
      RelNode input,
      ImmutableBitSet groupSet,
      @Nullable List<ImmutableBitSet> groupSets,
      List<AggregateCall> aggCalls) {
    return new ChalkHashAggregate(getCluster(), traitSet, input, groupSet, groupSets, aggCalls);
  }

  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double inputRows = mq.getRowCount(getInput());
    double groups = mq.getRowCount(this);
    return planner.getCostFactory().makeCost(groups, inputRows + groups, 0);
  }
}
