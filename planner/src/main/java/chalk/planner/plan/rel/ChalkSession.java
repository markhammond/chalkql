package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.CostModel;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelFieldCollation;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelWriter;
import org.apache.calcite.rel.SingleRel;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.util.Pair;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A session window (D55, {@code 14-windows-ii.md} §1). Maps 1:1 onto the IR's {@code Session}.
 *
 * <p><b>It requires its input ordered</b> by (partition keys ASC NULLS LAST, then the time column
 * ascending), exactly as {@link ChalkWindow} does and for the same reason: a session is a run of rows
 * whose gaps are small, and finding the runs needs the rows in order. Saying so as a trait is what
 * lets the declared collation of a table, or an index-ordered scan (D50), satisfy the requirement for
 * free — and what makes {@code corpus/queries/m5-windows-ii/02} plan with no {@code Sort}.
 *
 * <p>The output delivers that same ordering, so a {@code GROUP BY symbol, window_start} above it can
 * stream.
 */
public final class ChalkSession extends SingleRel implements ChalkRel {
  private final ImmutableList<Integer> partitionKeys;
  private final int timeColumn;
  private final RexNode gap;

  private ChalkSession(
      RelOptCluster cluster,
      RelTraitSet traits,
      RelNode input,
      RelDataType rowType,
      List<Integer> partitionKeys,
      int timeColumn,
      RexNode gap) {
    super(cluster, traits, input);
    this.rowType = rowType;
    this.partitionKeys = ImmutableList.copyOf(partitionKeys);
    this.timeColumn = timeColumn;
    this.gap = gap;
  }

  /**
   * A session over {@code input}. The input is asked for {@link #requiredCollation} and the node
   * claims the same ordering for its own output.
   */
  public static ChalkSession create(
      RelNode input,
      RelDataType rowType,
      List<Integer> partitionKeys,
      int timeColumn,
      RexNode gap) {
    RelOptCluster cluster = input.getCluster();
    RelCollation collation = requiredCollation(partitionKeys, timeColumn);
    RelTraitSet traits = cluster.traitSetOf(ChalkConvention.LOCAL).replace(collation).simplify();
    return new ChalkSession(
        cluster, traits, input, rowType, partitionKeys, timeColumn, gap);
  }

  public List<Integer> partitionKeys() {
    return partitionKeys;
  }

  public int timeColumn() {
    return timeColumn;
  }

  /** How long a quiet interval ends a session. A positive interval constant. */
  public RexNode gap() {
    return gap;
  }

  /**
   * The ordering the input must deliver: the partition keys ascending with NULLs last, then the time
   * column ascending with NULLs last. A time column that is also a partition key appears once.
   */
  public static RelCollation requiredCollation(List<Integer> partitionKeys, int timeColumn) {
    List<RelFieldCollation> fields = new ArrayList<>();
    Set<Integer> seen = new LinkedHashSet<>();
    for (int key : partitionKeys) {
      if (seen.add(key)) {
        fields.add(ascending(key));
      }
    }

    if (seen.add(timeColumn)) {
      fields.add(ascending(timeColumn));
    }

    return RelCollations.of(fields);
  }

  private static RelFieldCollation ascending(int index) {
    return new RelFieldCollation(
        index, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.LAST);
  }

  /** The ordering this node's output delivers, which is the one it required of its input. */
  public RelCollation collation() {
    return requiredCollation(partitionKeys, timeColumn);
  }

  @Override
  public ChalkSession copy(RelTraitSet traitSet, List<RelNode> inputs) {
    if (inputs.size() != 1) {
      throw new IllegalArgumentException("a ChalkSession has exactly one input");
    }
    return new ChalkSession(
        getCluster(), traitSet, inputs.get(0), getRowType(), partitionKeys, timeColumn, gap);
  }

  /**
   * A parent's required ordering goes down only when this node's own ordering already satisfies it
   * over input columns — the session has to see its input in its own order and cannot offer another.
   */
  @Override
  public @Nullable Pair<RelTraitSet, List<RelTraitSet>> passThroughTraits(RelTraitSet required) {
    RelCollation wanted = required.getCollation();
    if (wanted == null || wanted.getFieldCollations().isEmpty()) {
      return null;
    }

    int width = getInput().getRowType().getFieldCount();
    for (RelFieldCollation field : wanted.getFieldCollations()) {
      if (field.getFieldIndex() >= width) {
        return null;
      }
    }

    RelCollation own = collation();
    if (!own.satisfies(wanted)) {
      return null;
    }

    RelTraitSet passed = traitSet.replace(own);
    return Pair.of(passed, ImmutableList.of(passed));
  }

  /** One output row per input row. */
  @Override
  public double estimateRowCount(RelMetadataQuery mq) {
    return mq.getRowCount(getInput());
  }

  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double work = CostModel.session(mq.getRowCount(getInput()));
    return planner.getCostFactory().makeCost(work, work, 0);
  }

  @Override
  public RelWriter explainTerms(RelWriter pw) {
    return super.explainTerms(pw)
        .item("partition", partitionKeys)
        .item("time", timeColumn)
        .item("gap", gap);
  }
}
