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
import org.apache.calcite.rel.core.AsofJoin;
import org.apache.calcite.rel.core.CorrelationId;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.metadata.RelMdCollation;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.util.Pair;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code ASOF JOIN}: for each left row, the right row with equal keys whose time is closest to the
 * left row's under the match comparison. Maps to the IR's {@code AsOfJoin} (D41, D43).
 *
 * <p>Extends Calcite's {@code AsofJoin} core class, so the match condition travels with the rel and
 * {@code LogicalAsofJoin}'s validation — one comparison between one left and one right column, and a
 * pure-equality ON clause — has already run by the time the rule sees it (V14).
 */
public final class ChalkAsOfJoin extends AsofJoin implements ChalkRel {

  private ChalkAsOfJoin(
      RelOptCluster cluster,
      RelTraitSet traits,
      RelNode left,
      RelNode right,
      RexNode condition,
      RexNode matchCondition,
      Set<CorrelationId> variablesSet,
      JoinRelType joinType) {
    super(
        cluster,
        traits,
        ImmutableList.of(),
        left,
        right,
        condition,
        matchCondition,
        variablesSet,
        joinType);
  }

  /** Claims the left input's orderings: rows come out in left-input order (D42). */
  public static ChalkAsOfJoin create(
      RelNode left,
      RelNode right,
      RexNode condition,
      RexNode matchCondition,
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
    return new ChalkAsOfJoin(
        cluster, traits, left, right, condition, matchCondition, variablesSet, joinType);
  }

  /**
   * Never called: {@code Join.copy(traitSet, condition, left, right, joinType, semiJoinDone)} has
   * nowhere to carry the match condition, so Calcite's own {@code EnumerableAsofJoin} throws here
   * too. The two-argument {@code copy} below is the one the planner uses.
   */
  @Override
  public Join copy(
      RelTraitSet traitSet,
      RexNode condition,
      RelNode left,
      RelNode right,
      JoinRelType joinType,
      boolean semiJoinDone) {
    throw new UnsupportedOperationException(
        "ChalkAsOfJoin.copy cannot carry the match condition; use copy(traitSet, inputs)");
  }

  @Override
  public Join copy(RelTraitSet traitSet, List<RelNode> inputs) {
    if (inputs.size() != 2) {
      throw new IllegalArgumentException("ChalkAsOfJoin takes exactly two inputs");
    }
    return new ChalkAsOfJoin(
        getCluster(),
        traitSet,
        inputs.get(0),
        inputs.get(1),
        getCondition(),
        getMatchCondition(),
        variablesSet,
        joinType);
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
    return DeriveMode.LEFT_FIRST;
  }

  /**
   * {@code build log build + probe log build + output} (cost model v3): the right side is
   * partitioned and sorted once, then binary-searched per left row.
   */
  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double work =
        CostModel.asOfJoin(mq.getRowCount(left), mq.getRowCount(right), mq.getRowCount(this));
    return planner.getCostFactory().makeCost(work, work, 0);
  }
}
