package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.CostModel;
import java.util.List;
import java.util.Set;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelWriter;
import org.apache.calcite.rel.core.CorrelationId;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rex.RexNode;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A cross-source join whose strategy is chosen at execution (D97, {@code JOIN_STRATEGY_ADAPTIVE}).
 *
 * <p>The left input is the <em>small</em> side, materialised first; the right input is the other
 * side as a plain boundary — the {@code LOCAL} alternative. {@link #lookupSide()} is the same right
 * side with a key set pushed into its predicate — the {@code LOOKUP} alternative. At execution the
 * small side is materialised, its distinct keys are counted, and the branch is chosen; both are in
 * the plan and the digest covers both, so the <em>plan</em> stays deterministic and only
 * {@code Stats.AdaptiveDecisions} records which one ran.
 *
 * <p>The lookup side is a field rather than a third input on purpose. It is a finished subtree the
 * rule built — a converter over a filtered scan — with nothing left to optimise, and Volcano has no
 * vocabulary for "an input that is an alternative to another input"; registering it would make the
 * optimiser cost a branch it must not choose between. {@code RelToIr} reads it directly, which is
 * the one place it needs to be read.
 *
 * <p>Its cost is the better of the two branches, because that is what will actually run. Costing it
 * as the average would make a join with one cheap branch lose to a plain {@code LOCAL} that has no
 * cheap branch at all.
 */
public final class ChalkAdaptiveJoin extends Join implements ChalkRel {

  private final RelNode lookupSide;
  private final int maxKeys;
  private final int maxKeysPerCall;

  private ChalkAdaptiveJoin(
      RelOptCluster cluster,
      RelTraitSet traits,
      RelNode left,
      RelNode right,
      RexNode condition,
      Set<CorrelationId> variablesSet,
      JoinRelType joinType,
      RelNode lookupSide,
      int maxKeys,
      int maxKeysPerCall) {
    super(cluster, traits, List.of(), left, right, condition, variablesSet, joinType);
    this.lookupSide = lookupSide;
    this.maxKeys = maxKeys;
    this.maxKeysPerCall = maxKeysPerCall;
  }

  /**
   * @param left the small side, materialised at execution
   * @param right the other side as a plain boundary — the LOCAL branch
   * @param lookupSide the same side with a key set pushed in — the LOOKUP branch
   * @param maxKeys the distinct-key threshold: {@code max_in_list × lookup_max_calls}
   * @param maxKeysPerCall the most keys one lookup call may carry
   */
  public static ChalkAdaptiveJoin create(
      RelNode left,
      RelNode right,
      RexNode condition,
      Set<CorrelationId> variablesSet,
      JoinRelType joinType,
      RelNode lookupSide,
      int maxKeys,
      int maxKeysPerCall) {
    RelOptCluster cluster = left.getCluster();
    return new ChalkAdaptiveJoin(
        cluster,
        cluster.traitSetOf(ChalkConvention.LOCAL),
        left,
        right,
        condition,
        variablesSet,
        joinType,
        lookupSide,
        maxKeys,
        maxKeysPerCall);
  }

  /** The LOOKUP branch's right side: the same source, with a key set in its predicate. */
  public RelNode lookupSide() {
    return lookupSide;
  }

  /** The threshold the execution applies to the small side's distinct keys. */
  public int maxKeys() {
    return maxKeys;
  }

  /** The most keys one lookup call may carry. */
  public int maxKeysPerCall() {
    return maxKeysPerCall;
  }

  @Override
  public Join copy(
      RelTraitSet traitSet,
      RexNode conditionExpr,
      RelNode left,
      RelNode right,
      JoinRelType joinType,
      boolean semiJoinDone) {
    return new ChalkAdaptiveJoin(
        getCluster(),
        traitSet,
        left,
        right,
        conditionExpr,
        getVariablesSet(),
        joinType,
        lookupSide,
        maxKeys,
        maxKeysPerCall);
  }

  @Override
  public RelWriter explainTerms(RelWriter pw) {
    return super.explainTerms(pw)
        .item("strategy", "ADAPTIVE")
        .item("max_keys", maxKeys)
        .item("max_keys_per_call", maxKeysPerCall);
  }

  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double small = mq.getRowCount(getLeft());
    double matched = mq.getRowCount(this);
    double other = mq.getRowCount(getRight());
    CostModel costs = CostModel.of(SourceCosts.profileOf(getRight()));

    // Both branches run *this node's* right input, and that subtree's own SourceToLocalConverter
    // has already charged one `remote_call_cost` — which Volcano adds to this self cost. So the
    // first call of each branch is already paid for and only the calls beyond it belong here: the
    // lookup's `calls - 1`, and the local branch's none at all. This is the correction ADR 0025 V144
    // made to ChalkLookupJoin, applied to the node that holds its lookup branch as a field rather
    // than as an input. The row charges stay, because the two branches fetch different numbers of
    // rows and that difference is the whole of what the minimum below is choosing between.
    double calls = Math.max(1.0, Math.ceil(small / Math.max(1, maxKeysPerCall)));
    double lookup =
        ((calls - 1) * costs.remoteCallCost())
            + (matched * costs.remoteRowCost())
            + CostModel.hashJoin(small, matched, matched);
    double local =
        (other * costs.remoteRowCost()) + CostModel.hashJoin(small, other, matched);

    // Plus what the materialisation itself costs, which LOCAL does not pay: the small side is read
    // once into the arena before either branch starts.
    double work = Math.min(lookup, local) + small;
    double tied = ChalkJoins.breakTies(work, this);
    return planner.getCostFactory().makeCost(tied, tied, 0);
  }
}
