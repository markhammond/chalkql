package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.ChalkSelectivity;
import java.util.List;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollationTraitDef;
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelWriter;
import org.apache.calcite.rel.core.Filter;
import org.apache.calcite.rel.metadata.RelMdCollation;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.SqlExplainLevel;
import org.apache.calcite.util.Pair;
import org.checkerframework.checker.nullness.qual.Nullable;

/** Keeps rows where the condition is TRUE. Maps to the IR's {@code Filter}. */
public final class ChalkFilter extends Filter implements ChalkRel {

  private ChalkFilter(
      RelOptCluster cluster, RelTraitSet traitSet, RelNode input, RexNode condition) {
    super(cluster, traitSet, input, condition);
  }

  /** Derives the collation trait from the input exactly as {@code EnumerableFilter} does. */
  public static ChalkFilter create(RelNode input, RexNode condition) {
    RelOptCluster cluster = input.getCluster();
    RelMetadataQuery mq = cluster.getMetadataQuery();
    RelTraitSet traits =
        cluster
            .traitSetOf(ChalkConvention.LOCAL)
            .replaceIfs(RelCollationTraitDef.INSTANCE, () -> RelMdCollation.filter(mq, input));
    return new ChalkFilter(cluster, traits, input, condition);
  }

  @Override
  public ChalkFilter copy(RelTraitSet traitSet, RelNode input, RexNode condition) {
    return new ChalkFilter(getCluster(), traitSet, input, condition);
  }

  /**
   * One unit per row the filter reads, which is what makes a filter above a scan cost two units per
   * table row and gives the index lookup something commensurate to beat ({@code
   * 11-m2-index-support.md} §4). The same number goes in both slots because Calcite's {@code
   * VolcanoCost} orders costs by its {@code rowCount} field alone (ADR 0015).
   */
  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double rows = mq.getRowCount(getInput());
    // One unit per row, plus whatever the user functions in the condition declare (D78).
    double perRow = 1 + chalk.planner.plan.CostModel.expressionFunctionCost(getCondition());
    return planner.getCostFactory().makeCost(rows, rows * perRow, 0);
  }

  /**
   * The condition's selectivity and whether it was measured, for the plan text only — never at
   * {@code DIGEST_ATTRIBUTES}, so a cost estimate cannot change what Volcano thinks two rels are.
   */
  @Override
  public RelWriter explainTerms(RelWriter pw) {
    RelWriter writer = super.explainTerms(pw);
    if (pw.getDetailLevel() == SqlExplainLevel.ALL_ATTRIBUTES) {
      ChalkSelectivity.Estimate estimate = estimate();
      if (estimate != null) {
        writer.item("sel", estimate.text());
      }
    }

    return writer;
  }

  /** The selectivity of this filter's condition, when its input is a Chalk leaf that knows. */
  private ChalkSelectivity.@Nullable Estimate estimate() {
    RelNode input = getInput();
    if (input instanceof org.apache.calcite.plan.volcano.RelSubset subset) {
      RelNode best = subset.getBest();
      input = best == null ? subset.getOriginal() : best;
    }

    if (input instanceof ChalkTableScan scan) {
      return ChalkSelectivity.of(condition, scan.chalkTable(), scan.projection(), getCluster());
    }

    if (input instanceof ChalkIndexLookup lookup) {
      return ChalkSelectivity.of(
          condition, lookup.chalkTable(), lookup.projection(), getCluster());
    }

    return null;
  }

  @Override
  public List<RelNode> getInputs() {
    return super.getInputs();
  }

  /** A filter keeps its input's order, so any required collation goes straight down. */
  @Override
  public @Nullable Pair<RelTraitSet, List<RelTraitSet>> passThroughTraits(RelTraitSet required) {
    return sameCollation(required);
  }

  /** And it delivers whatever its input delivers. */
  @Override
  public @Nullable Pair<RelTraitSet, List<RelTraitSet>> deriveTraits(
      RelTraitSet childTraits, int childId) {
    return sameCollation(childTraits);
  }

  private @Nullable Pair<RelTraitSet, List<RelTraitSet>> sameCollation(RelTraitSet other) {
    RelCollation collation = other.getCollation();
    if (collation == null || collation == RelCollations.EMPTY) {
      return null;
    }

    RelTraitSet traits = traitSet.replace(collation);
    return Pair.of(traits, com.google.common.collect.ImmutableList.of(traits));
  }
}
