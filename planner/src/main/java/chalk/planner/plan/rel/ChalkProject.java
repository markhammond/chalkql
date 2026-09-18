package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import com.google.common.collect.ImmutableList;
import com.google.common.collect.ImmutableSet;
import java.util.List;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollationTraitDef;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.metadata.RelMdCollation;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.util.Pair;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Output row = the expressions, in order (Calcite semantics; the recorded divergence from Substrait
 * is D23 / docs/design/02-ir.md §9). Maps to the IR's {@code Project}.
 */
public final class ChalkProject extends Project implements ChalkRel {

  private ChalkProject(
      RelOptCluster cluster,
      RelTraitSet traitSet,
      RelNode input,
      List<? extends RexNode> projects,
      RelDataType rowType) {
    super(cluster, traitSet, ImmutableList.of(), input, projects, rowType, ImmutableSet.of());
  }

  /**
   * Derives the collation trait as {@code EnumerableProject} does: a projection that carries a
   * sorted column through keeps the ordering, which is what lets {@code ORDER BY ts} survive a
   * {@code SELECT symbol, ts, close}.
   */
  public static ChalkProject create(
      RelNode input, List<? extends RexNode> projects, RelDataType rowType) {
    RelOptCluster cluster = input.getCluster();
    RelMetadataQuery mq = cluster.getMetadataQuery();
    RelTraitSet traits =
        cluster
            .traitSetOf(ChalkConvention.LOCAL)
            .replaceIfs(
                RelCollationTraitDef.INSTANCE,
                () -> RelMdCollation.project(mq, input, projects));
    return new ChalkProject(cluster, traits, input, projects, rowType);
  }

  @Override
  public ChalkProject copy(
      RelTraitSet traitSet, RelNode input, List<RexNode> projects, RelDataType rowType) {
    return new ChalkProject(getCluster(), traitSet, input, projects, rowType);
  }

  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double rows = mq.getRowCount(getInput());
    // A projection normally costs one unit per expression per row. A user function declares what it
    // costs instead (D78), which is what makes a filter over a client-bodied one worth doing after
    // a join rather than before it.
    double perRow =
        Math.max(getProjects().size(), 1)
            + chalk.planner.plan.CostModel.expressionFunctionCost(getProjects());
    return planner.getCostFactory().makeCost(rows, rows * perRow, 0);
  }

  /**
   * A required collation goes down when every field it names is carried by the projection — the
   * indexes are rewritten into the input's row on the way ({@code 12-joins.md} §0).
   */
  @Override
  public @Nullable Pair<RelTraitSet, List<RelTraitSet>> passThroughTraits(RelTraitSet required) {
    return ChalkTraits.passThroughForProject(
        required, exps, input.getRowType(), getCluster().getTypeFactory(), traitSet);
  }

  /** And what the input delivers comes up, truncated at the first field the projection drops. */
  @Override
  public @Nullable Pair<RelTraitSet, List<RelTraitSet>> deriveTraits(
      RelTraitSet childTraits, int childId) {
    return ChalkTraits.deriveForProject(
        childTraits, exps, input.getRowType(), getCluster().getTypeFactory(), traitSet);
  }
}
