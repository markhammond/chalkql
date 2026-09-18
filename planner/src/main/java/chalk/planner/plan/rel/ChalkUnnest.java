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
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelFieldCollation;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelWriter;
import org.apache.calcite.rel.SingleRel;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.util.Pair;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Flattening a {@code LIST} column (D66, {@code 14-windows-ii.md} §7). Maps 1:1 onto the IR's
 * {@code Unnest}.
 *
 * <p>One input row becomes one output row per element of its list, and those rows are adjacent, so
 * the input's ordering survives and this node derives it rather than requiring one — the same shape
 * as {@link ChalkHop}. {@code keepEmpty} is what distinguishes {@code CROSS JOIN UNNEST}, where an
 * empty or NULL list contributes nothing, from {@code LEFT JOIN LATERAL UNNEST … ON TRUE}, where it
 * contributes one NULL-padded row.
 */
public final class ChalkUnnest extends SingleRel implements ChalkRel {
  private final int listColumn;
  private final boolean withOrdinality;
  private final boolean keepEmpty;

  private ChalkUnnest(
      RelOptCluster cluster,
      RelTraitSet traits,
      RelNode input,
      RelDataType rowType,
      int listColumn,
      boolean withOrdinality,
      boolean keepEmpty) {
    super(cluster, traits, input);
    this.rowType = rowType;
    this.listColumn = listColumn;
    this.withOrdinality = withOrdinality;
    this.keepEmpty = keepEmpty;
  }

  /** An unnest over {@code input}, claiming the input's own ordering for its output. */
  public static ChalkUnnest create(
      RelNode input,
      RelDataType rowType,
      int listColumn,
      boolean withOrdinality,
      boolean keepEmpty) {
    RelOptCluster cluster = input.getCluster();
    RelCollation collation = input.getTraitSet().getCollation();
    RelTraitSet traits =
        cluster
            .traitSetOf(ChalkConvention.LOCAL)
            .replace(collation == null ? RelCollations.EMPTY : collation)
            .simplify();
    return new ChalkUnnest(
        cluster, traits, input, rowType, listColumn, withOrdinality, keepEmpty);
  }

  public int listColumn() {
    return listColumn;
  }

  public boolean withOrdinality() {
    return withOrdinality;
  }

  public boolean keepEmpty() {
    return keepEmpty;
  }

  @Override
  public ChalkUnnest copy(RelTraitSet traitSet, List<RelNode> inputs) {
    if (inputs.size() != 1) {
      throw new IllegalArgumentException("a ChalkUnnest has exactly one input");
    }
    return new ChalkUnnest(
        getCluster(), traitSet, inputs.get(0), getRowType(), listColumn, withOrdinality, keepEmpty);
  }

  /** A parent's ordering over input columns goes straight down; unnest appends and reorders nothing. */
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

  /**
   * The planner has no statistic for how long a list is, so this is the same assumption the cost
   * model states once: {@link CostModel#UNNEST_ELEMENTS_PER_ROW} elements per row.
   */
  @Override
  public double estimateRowCount(RelMetadataQuery mq) {
    return mq.getRowCount(getInput()) * CostModel.UNNEST_ELEMENTS_PER_ROW;
  }

  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double work = CostModel.unnest(mq.getRowCount(getInput()));
    return planner.getCostFactory().makeCost(work, work, 0);
  }

  @Override
  public RelWriter explainTerms(RelWriter pw) {
    return super.explainTerms(pw)
        .item("list", listColumn)
        .item("ordinality", withOrdinality)
        .item("keepEmpty", keepEmpty);
  }
}
