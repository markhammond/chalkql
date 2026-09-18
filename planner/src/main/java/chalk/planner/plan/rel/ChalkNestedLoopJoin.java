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
 * The join that always applies: buffer the right input, and for each left row evaluate the whole
 * condition against every buffered right row. Maps to the IR's {@code NestedLoopJoin} (D43).
 *
 * <p>Costed at left × right, so it wins only when nothing else can run — a non-equi condition, or a
 * cross join.
 */
public final class ChalkNestedLoopJoin extends Join implements ChalkRel {

  private ChalkNestedLoopJoin(
      RelOptCluster cluster,
      RelTraitSet traits,
      RelNode left,
      RelNode right,
      RexNode condition,
      Set<CorrelationId> variablesSet,
      JoinRelType joinType) {
    super(cluster, traits, ImmutableList.of(), left, right, condition, variablesSet, joinType);
  }

  /** Claims the left input's orderings: the outer loop runs over the left side in order. */
  public static ChalkNestedLoopJoin create(
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
                () -> RelMdCollation.enumerableNestedLoopJoin(mq, left, right, joinType));
    return new ChalkNestedLoopJoin(cluster, traits, left, right, condition, variablesSet, joinType);
  }

  @Override
  public ChalkNestedLoopJoin copy(
      RelTraitSet traitSet,
      RexNode condition,
      RelNode left,
      RelNode right,
      JoinRelType joinType,
      boolean semiJoinDone) {
    return new ChalkNestedLoopJoin(
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
   * {@code left × right + output} (cost model v3), plus what a <b>remote</b> inner side costs to
   * instantiate per outer row (F51).
   *
   * <p>The inner side is the one a streaming implementation re-reads per outer row, and a remote
   * query there is a round trip per outer row. Charging nothing for it made this join win against a
   * hash join that would have been valid — and {@code CrossSourceSupport} then refused the plan as
   * UNSUPPORTED, which is the backstop working for a defect upstream of it. The cheapest shape to
   * reach it is a {@code through} child whose own projection must stay in process beside a parent in
   * the same remote source (§3.13, V161).
   *
   * <p>One round trip per outer row, and <b>not</b> {@link ChalkLookupJoin}'s {@code calls - 1}
   * (V144). That correction exists so a plan the planner should be free to choose is not priced out
   * of existence by a call its own subtree already charged; this shape is the opposite — a plan
   * {@code CrossSourceSupport} refuses outright at any size, so what the cost has to do is keep the
   * optimiser away from it. Subtracting the subtree's own call does not: where the outer side's
   * estimate is a single row — which is what a tenancy predicate over a small parent folds to — the
   * correction is zero and the nested loop still wins by the two units a hash join spends on its
   * build side, leaving the refusal exactly where it was. The one extra call is the pessimistic
   * reading D104 already takes of this shape, and it can only ever turn a refused plan into a
   * planned one: an inner side that is local is charged nothing, and no plan that survived the check
   * had a remote one.
   *
   * <p>"Remote" is {@link SourceCosts#fetchesRemotely}, the whole subtree and not the boundary chain
   * above it (F55): what is re-read per outer row is everything in there, including a fetch under a
   * join. The boundary reading walked past exactly the shape a context with an open half makes of a
   * {@code through} parent, so that plan was costed as free and refused by the client's I-IR-20
   * instead of never being chosen.
   */
  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double work =
        CostModel.nestedLoopJoin(mq.getRowCount(left), mq.getRowCount(right), mq.getRowCount(this));
    work += remoteInstantiations(mq);
    work = ChalkJoins.breakTies(work, this);
    return planner.getCostFactory().makeCost(work, work, 0);
  }

  /** One round trip per outer row, or nothing at all for an inner side that is local. */
  private double remoteInstantiations(RelMetadataQuery mq) {
    if (!SourceCosts.fetchesRemotely(right)) {
      return 0;
    }
    Double outer = mq.getRowCount(left);
    return Math.max(1, outer == null ? 1 : outer)
        * CostModel.of(SourceCosts.profileOf(right)).remoteCallCost();
  }
}
