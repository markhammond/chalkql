package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.CostModel;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import java.util.Set;
import org.apache.calcite.plan.DeriveMode;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelFieldCollation;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelWriter;
import org.apache.calcite.rel.core.CorrelationId;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.util.ImmutableIntList;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * An equi-join over two inputs that are already sorted on their keys: both are streamed once and
 * equal-key groups are cross-joined. Maps to the IR's {@code MergeJoin} (D43).
 *
 * <p>The key lists are in the order the two collations agree on, which need not be the order
 * {@code JoinInfo} produced — {@code bars b JOIN bars q ON b.symbol = q.symbol AND b.ts = q.ts} has
 * both inputs collated {@code (ts, symbol)}, so the join's keys are {@code (ts, symbol)} too. The
 * rule permutes them; this node just records the result and claims the ordering that follows.
 */
public final class ChalkMergeJoin extends Join implements ChalkRel {
  private final ImmutableIntList leftKeys;
  private final ImmutableIntList rightKeys;
  private final RelCollation collation;

  private ChalkMergeJoin(
      RelOptCluster cluster,
      RelTraitSet traits,
      RelNode left,
      RelNode right,
      RexNode condition,
      Set<CorrelationId> variablesSet,
      JoinRelType joinType,
      ImmutableIntList leftKeys,
      ImmutableIntList rightKeys,
      RelCollation collation) {
    super(cluster, traits, ImmutableList.of(), left, right, condition, variablesSet, joinType);
    this.leftKeys = leftKeys;
    this.rightKeys = rightKeys;
    this.collation = collation;
  }

  /**
   * @param leftKeys left field indexes, in the order both inputs are sorted in
   * @param rightKeys the matching right field indexes, in the same order
   * @param directions one per key, the direction both inputs deliver
   */
  public static ChalkMergeJoin create(
      RelNode left,
      RelNode right,
      RexNode condition,
      Set<CorrelationId> variablesSet,
      JoinRelType joinType,
      ImmutableIntList leftKeys,
      ImmutableIntList rightKeys,
      List<RelFieldCollation> directions) {
    RelOptCluster cluster = left.getCluster();
    RelCollation collation = keyCollation(leftKeys, directions);
    RelTraitSet traits = cluster.traitSetOf(ChalkConvention.LOCAL).replace(collation);
    return new ChalkMergeJoin(
        cluster,
        traits,
        left,
        right,
        condition,
        variablesSet,
        joinType,
        leftKeys,
        rightKeys,
        collation);
  }

  /** The ordering the merge delivers: the key order, over the join's output row. */
  private static RelCollation keyCollation(
      ImmutableIntList leftKeys, List<RelFieldCollation> directions) {
    List<RelFieldCollation> fields = new ArrayList<>(leftKeys.size());
    for (int i = 0; i < leftKeys.size(); i++) {
      RelFieldCollation template = directions.get(i);
      fields.add(
          new RelFieldCollation(leftKeys.get(i), template.getDirection(), template.nullDirection));
    }
    return RelCollations.of(fields);
  }

  public ImmutableIntList leftKeys() {
    return leftKeys;
  }

  public ImmutableIntList rightKeys() {
    return rightKeys;
  }

  public RelCollation collation() {
    return collation;
  }

  @Override
  public ChalkMergeJoin copy(
      RelTraitSet traitSet,
      RexNode condition,
      RelNode left,
      RelNode right,
      JoinRelType joinType,
      boolean semiJoinDone) {
    return new ChalkMergeJoin(
        getCluster(),
        traitSet,
        left,
        right,
        condition,
        variablesSet,
        joinType,
        leftKeys,
        rightKeys,
        collation);
  }

  /**
   * The key order is part of what makes two merge joins different: the same condition merged on
   * {@code (symbol, ts)} and on {@code (ts, symbol)} are different plans with different input
   * requirements, and Volcano compares rels by their digest.
   */
  @Override
  public RelWriter explainTerms(RelWriter pw) {
    return super.explainTerms(pw).item("leftKeys", leftKeys).item("rightKeys", rightKeys);
  }

  /**
   * Nothing to pass down — the operator's output order <em>is</em> its key order, and its inputs'
   * requirements were fixed when the rule chose that order. Nothing to derive either, for the same
   * reason: the trait set already claims what this node delivers.
   */
  @Override
  public DeriveMode getDeriveMode() {
    return DeriveMode.PROHIBITED;
  }

  /** {@code left + right + output} (cost model v3): one pass over each input. */
  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double work =
        CostModel.mergeJoin(mq.getRowCount(left), mq.getRowCount(right), mq.getRowCount(this));
    work = ChalkJoins.breakTies(work, this);
    return planner.getCostFactory().makeCost(work, work, 0);
  }
}
