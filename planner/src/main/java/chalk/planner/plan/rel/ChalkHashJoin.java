package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.CostModel;
import com.google.common.collect.ImmutableList;
import java.util.List;
import java.util.Set;
import org.apache.calcite.plan.DeriveMode;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollationTraitDef;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.CorrelationId;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.metadata.RelMdCollation;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.util.Pair;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * An equi-join executed by building a hash table on the <b>right</b> input and streaming the left
 * one through it. Maps to the IR's {@code HashJoin} (D43).
 *
 * <p>Build side right by convention; {@code JOIN_COMMUTE} offers the other orientation and cost
 * decides, exactly as it does for {@code EnumerableHashJoin}.
 */
public final class ChalkHashJoin extends Join implements ChalkRel {

  private ChalkHashJoin(
      RelOptCluster cluster,
      RelTraitSet traits,
      RelNode left,
      RelNode right,
      RexNode condition,
      Set<CorrelationId> variablesSet,
      JoinRelType joinType) {
    super(cluster, traits, ImmutableList.of(), left, right, condition, variablesSet, joinType);
  }

  /** Claims the left input's orderings: the probe side streams through in order. */
  public static ChalkHashJoin create(
      RelNode left,
      RelNode right,
      RexNode condition,
      Set<CorrelationId> variablesSet,
      JoinRelType joinType) {
    RelOptCluster cluster = left.getCluster();
    RelMetadataQuery mq = cluster.getMetadataQuery();
    RelTraitSet traits =
        cluster
            .traitSetOf(ChalkConvention.LOCAL)
            .replaceIfs(
                RelCollationTraitDef.INSTANCE,
                () -> RelMdCollation.enumerableHashJoin(mq, left, right, joinType));
    return new ChalkHashJoin(cluster, traits, left, right, condition, variablesSet, joinType);
  }

  @Override
  public ChalkHashJoin copy(
      RelTraitSet traitSet,
      RexNode condition,
      RelNode left,
      RelNode right,
      JoinRelType joinType,
      boolean semiJoinDone) {
    return new ChalkHashJoin(
        getCluster(), traitSet, left, right, condition, variablesSet, joinType);
  }

  @Override
  public @Nullable Pair<RelTraitSet, List<RelTraitSet>> passThroughTraits(RelTraitSet required) {
    return ChalkTraits.passThroughForJoin(
        required, joinType, left.getRowType().getFieldCount(), getTraitSet());
  }

  @Override
  public @Nullable Pair<RelTraitSet, List<RelTraitSet>> deriveTraits(
      RelTraitSet childTraits, int childId) {
    return ChalkTraits.deriveForJoin(
        childTraits, childId, joinType, getTraitSet(), right.getTraitSet());
  }

  @Override
  public DeriveMode getDeriveMode() {
    return joinType == JoinRelType.FULL || joinType == JoinRelType.RIGHT
        ? DeriveMode.PROHIBITED
        : DeriveMode.LEFT_FIRST;
  }

  /**
   * {@code probe + 2 × build + output} (cost model v3, {@code 12-joins.md} §3), with Calcite's
   * epsilon so that the two orientations {@code JOIN_COMMUTE} produces cannot tie — a tie would make
   * the winner depend on registration order, and the golden plans would flake.
   */
  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double work =
        CostModel.hashJoin(mq.getRowCount(left), mq.getRowCount(right), mq.getRowCount(this));
    work = ChalkJoins.breakTies(work, this);
    return planner.getCostFactory().makeCost(work, work, 0);
  }
}
