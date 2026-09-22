package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import java.util.List;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollationTraitDef;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelWriter;
import org.apache.calcite.rel.SingleRel;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code Sort} and {@code Fetch} fused: rows {@code offset .. offset + count} of the sorted input,
 * kept in a bounded heap rather than by sorting everything. Maps to the IR's {@code TopN}.
 */
public final class ChalkTopN extends SingleRel implements ChalkRel {
  private final RelCollation collation;
  private final @Nullable RexNode offset;
  private final RexNode fetch;

  private ChalkTopN(
      RelOptCluster cluster,
      RelTraitSet traitSet,
      RelNode input,
      RelCollation collation,
      @Nullable RexNode offset,
      RexNode fetch) {
    super(cluster, traitSet, input);
    this.collation = collation;
    this.offset = offset;
    this.fetch = fetch;
  }

  public static ChalkTopN create(
      RelNode input, RelCollation collation, @Nullable RexNode offset, RexNode fetch) {
    RelOptCluster cluster = input.getCluster();
    RelTraitSet traits =
        cluster.traitSetOf(ChalkConvention.LOCAL).replace(collation);
    return new ChalkTopN(cluster, traits, input, collation, offset, fetch);
  }

  public RelCollation collation() {
    return collation;
  }

  public long offsetValue() {
    return offset == null ? 0L : RexLiteral.longValue(offset);
  }

  public long fetchValue() {
    return RexLiteral.longValue(fetch);
  }

  @Override
  public RelNode copy(RelTraitSet traitSet, List<RelNode> inputs) {
    if (inputs.size() != 1) {
      throw new IllegalArgumentException("ChalkTopN takes exactly one input");
    }
    return new ChalkTopN(getCluster(), traitSet, inputs.get(0), collation, offset, fetch);
  }

  @Override
  public RelWriter explainTerms(RelWriter pw) {
    return super.explainTerms(pw)
        .item("collation", collation)
        .itemIf("offset", offset, offset != null)
        .item("fetch", fetch);
  }

  @Override
  public double estimateRowCount(RelMetadataQuery mq) {
    return Math.min(mq.getRowCount(getInput()), offsetValue() + fetchValue());
  }

  /**
   * {@code input rows × log2(offset + fetch + 1)}, in <b>both</b> slots (F112).
   *
   * <p>It used to put its <em>output</em> row count in the first slot and the heap pass in the
   * second. Only the first is ever compared — {@code VolcanoCost.isLt} reads {@code rowCount} and
   * nothing else (ADR 0015) — so a top-N over a hundred thousand rows cost the optimiser the same as
   * a top-N over ten: the pass that reads the whole input was invisible, exactly as {@link
   * ChalkSort}'s was before ADR 0017, and for the same reason.
   *
   * <p>It is fixed here and not earlier because with its true cost a top-N over a scan can lose to
   * the whole-range ordered scan of D50 for a fetch above a handful of rows, and the plan it should
   * lose to is the goaled ordered lookup — which only exists once D276's rule does.
   */
  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double inputRows = mq.getRowCount(getInput());
    double heap = offsetValue() + fetchValue() + 1;
    double work = inputRows * ChalkSort.log2(heap);
    return planner.getCostFactory().makeCost(work, work, 0);
  }
}
