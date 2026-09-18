package chalk.planner.plan.rel;

import chalk.ir.v1.JoinStrategy;
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
 * A cross-source join executed by asking the right side for the left side's keys (D103's
 * {@code LOOKUP}, and its one-call variant {@code BROADCAST}).
 *
 * <p>The left input streams; the right input is a {@link SourceToLocalConverter} whose pushed
 * predicate contains a key set the executor binds once per call. Modelled as a {@link Join} so that
 * {@code analyzeCondition}, the metadata handlers and {@code RelToIr}'s residual extraction are the
 * ones every other join uses — the only thing new about it is where its right input's rows come
 * from and what it costs.
 *
 * <p>Cost, from §2: {@code calls × remote_call_cost + matched rows × remote_row_cost}, plus the
 * local hash join over what comes back. Compare that with {@code LOCAL}, whose right side is one
 * call for the <em>whole</em> table: a lookup wins exactly when the keys it sends are worth less
 * than the rows it does not fetch.
 */
public final class ChalkLookupJoin extends Join implements ChalkRel {

  private final int maxKeysPerCall;
  private final JoinStrategy strategy;

  private ChalkLookupJoin(
      RelOptCluster cluster,
      RelTraitSet traits,
      RelNode left,
      RelNode right,
      RexNode condition,
      Set<CorrelationId> variablesSet,
      JoinRelType joinType,
      int maxKeysPerCall,
      JoinStrategy strategy) {
    super(cluster, traits, List.of(), left, right, condition, variablesSet, joinType);
    this.maxKeysPerCall = maxKeysPerCall;
    this.strategy = strategy;
  }

  /**
   * @param left the driving side, streamed
   * @param right a {@code SourceToLocalConverter} carrying exactly one key set in its predicate
   * @param maxKeysPerCall the most distinct keys one call may carry
   * @param strategy {@code LOOKUP} or {@code BROADCAST} — which spelling the key set was given
   */
  public static ChalkLookupJoin create(
      RelNode left,
      RelNode right,
      RexNode condition,
      Set<CorrelationId> variablesSet,
      JoinRelType joinType,
      int maxKeysPerCall,
      JoinStrategy strategy) {
    RelOptCluster cluster = left.getCluster();
    return new ChalkLookupJoin(
        cluster,
        cluster.traitSetOf(ChalkConvention.LOCAL),
        left,
        right,
        condition,
        variablesSet,
        joinType,
        maxKeysPerCall,
        strategy);
  }

  /** The most distinct keys one call carries. */
  public int maxKeysPerCall() {
    return maxKeysPerCall;
  }

  /** Which of the two key-set spellings this join uses. */
  public JoinStrategy strategy() {
    return strategy;
  }

  /** Whether the key set is shipped as {@code VALUES} rows rather than as an {@code IN} list. */
  public boolean keySetAsRows() {
    return strategy == JoinStrategy.JOIN_STRATEGY_BROADCAST;
  }

  @Override
  public Join copy(
      RelTraitSet traitSet,
      RexNode conditionExpr,
      RelNode left,
      RelNode right,
      JoinRelType joinType,
      boolean semiJoinDone) {
    return new ChalkLookupJoin(
        getCluster(),
        traitSet,
        left,
        right,
        conditionExpr,
        getVariablesSet(),
        joinType,
        maxKeysPerCall,
        strategy);
  }

  @Override
  public RelWriter explainTerms(RelWriter pw) {
    return super.explainTerms(pw)
        .item("strategy", strategy.name())
        .item("max_keys_per_call", maxKeysPerCall);
  }

  /**
   * What the calls cost, plus the rows they bring back and the join over them.
   *
   * <p>{@code calls - 1} and not {@code calls}: the right input is the pushed subtree this join
   * <em>instantiates</em> per call, not a subtree that runs once beside it, and its own
   * {@code SourceToLocalConverter} already charges one {@code remote_call_cost}. Charging {@code
   * calls} here counted the first call twice, which made a lookup lose to a full fetch of a table
   * whose whole content was cheaper than one extra imaginary round trip — the shape a small fixture
   * makes and a real table does not, and the reason a bound list above the fold ceiling never
   * reached a source as a key set (F45).
   */
  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double driving = mq.getRowCount(getLeft());
    double distinctKeys = distinctDrivingKeys(mq, driving);
    double matched = mq.getRowCount(this);
    CostModel costs = CostModel.of(SourceCosts.profileOf(getRight()));

    double calls = Math.max(1.0, Math.ceil(distinctKeys / Math.max(1, maxKeysPerCall)));
    double shipped =
        strategy == JoinStrategy.JOIN_STRATEGY_BROADCAST ? distinctKeys * costs.remoteRowCost() : 0;
    double work =
        ((calls - 1) * costs.remoteCallCost())
            + (matched * costs.remoteRowCost())
            + shipped
            + CostModel.hashJoin(driving, matched, matched);
    double tied = ChalkJoins.breakTies(work, this);
    return planner.getCostFactory().makeCost(tied, tied, 0);
  }

  /**
   * How many calls the driving side will cost, which is its <em>distinct</em> key count and not its
   * row count: a hundred rows over five symbols is one call, not a hundred. Falls back to the row
   * count when the planner cannot say, which is the pessimistic answer.
   */
  private double distinctDrivingKeys(RelMetadataQuery mq, double drivingRows) {
    org.apache.calcite.rel.core.JoinInfo info = analyzeCondition();
    if (info.leftKeys.isEmpty()) {
      return drivingRows;
    }
    Double distinct =
        mq.getDistinctRowCount(
            getLeft(), org.apache.calcite.util.ImmutableBitSet.of(info.leftKeys), null);
    return distinct == null ? drivingRows : Math.min(distinct, drivingRows);
  }
}
