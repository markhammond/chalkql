package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.CostModel;
import com.google.common.collect.ImmutableList;
import java.util.List;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollation;
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
 * A hopping window (D55, {@code 14-windows-ii.md} §1). Maps 1:1 onto the IR's {@code Hop}.
 *
 * <p>A row at time <i>t</i> is emitted once per window {@code [s, s + size)} whose start is a
 * multiple of {@code slide} from the epoch and which contains <i>t</i> — {@code size / slide} copies
 * in the usual case, none when {@code size < slide} leaves <i>t</i> in the gap, and none for a NULL
 * time. The copies of one row come out together, in ascending {@code window_start}, so <b>the input's
 * ordering survives</b>: this node derives whatever collation its input delivers rather than
 * requiring one.
 *
 * <p>That is the difference from {@link ChalkSession}, which has to see its rows in order to find the
 * gaps between them and therefore requires an ordering.
 */
public final class ChalkHop extends SingleRel implements ChalkRel {
  private final int timeColumn;
  private final RexNode slide;
  private final RexNode size;

  private ChalkHop(
      RelOptCluster cluster,
      RelTraitSet traits,
      RelNode input,
      RelDataType rowType,
      int timeColumn,
      RexNode slide,
      RexNode size) {
    super(cluster, traits, input);
    this.rowType = rowType;
    this.timeColumn = timeColumn;
    this.slide = slide;
    this.size = size;
  }

  /** A hop over {@code input}, claiming the input's own ordering for its output. */
  public static ChalkHop create(
      RelNode input, RelDataType rowType, int timeColumn, RexNode slide, RexNode size) {
    RelOptCluster cluster = input.getCluster();
    RelTraitSet traits =
        cluster.traitSetOf(ChalkConvention.LOCAL).replace(collationOf(input)).simplify();
    return new ChalkHop(cluster, traits, input, rowType, timeColumn, slide, size);
  }

  /** The input field the windows are computed from. */
  public int timeColumn() {
    return timeColumn;
  }

  /** How far apart consecutive window starts are. A positive interval constant. */
  public RexNode slide() {
    return slide;
  }

  /** How wide each window is. A positive interval constant. */
  public RexNode size() {
    return size;
  }

  /**
   * The ordering an input delivers, restricted to the fields this node passes through — which is all
   * of them, since a hop only appends. A collation naming a field past the input's width cannot
   * arise, but the guard keeps the claim honest if one ever does.
   */
  private static RelCollation collationOf(RelNode input) {
    RelCollation collation = input.getTraitSet().getCollation();
    if (collation == null) {
      return org.apache.calcite.rel.RelCollations.EMPTY;
    }

    int width = input.getRowType().getFieldCount();
    for (RelFieldCollation field : collation.getFieldCollations()) {
      if (field.getFieldIndex() >= width) {
        return org.apache.calcite.rel.RelCollations.EMPTY;
      }
    }

    return collation;
  }

  @Override
  public ChalkHop copy(RelTraitSet traitSet, List<RelNode> inputs) {
    if (inputs.size() != 1) {
      throw new IllegalArgumentException("a ChalkHop has exactly one input");
    }
    return new ChalkHop(
        getCluster(), traitSet, inputs.get(0), getRowType(), timeColumn, slide, size);
  }

  /**
   * A parent's ordering over input columns goes straight down: the hop appends two columns and
   * reorders nothing, so whatever its input delivers, it delivers.
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

    RelTraitSet passed = traitSet.replace(wanted);
    return Pair.of(passed, ImmutableList.of(passed));
  }

  /** What the hop delivers is what its input delivers. */
  @Override
  public @Nullable Pair<RelTraitSet, List<RelTraitSet>> deriveTraits(
      RelTraitSet childTraits, int childId) {
    RelCollation collation = childTraits.getCollation();
    if (collation == null || collation.getFieldCollations().isEmpty()) {
      return null;
    }

    RelTraitSet derived = traitSet.replace(collation);
    return Pair.of(derived, ImmutableList.of(childTraits.replace(ChalkConvention.LOCAL)));
  }

  /** One output row per (input row × window), which is what {@link #estimateRowCount} counts. */
  @Override
  public double estimateRowCount(RelMetadataQuery mq) {
    return mq.getRowCount(getInput()) * CostModel.HOP_WINDOWS_PER_ROW;
  }

  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double work = CostModel.hop(mq.getRowCount(getInput()));
    return planner.getCostFactory().makeCost(work, work, 0);
  }

  @Override
  public RelWriter explainTerms(RelWriter pw) {
    return super.explainTerms(pw).item("time", timeColumn).item("slide", slide).item("size", size);
  }
}
