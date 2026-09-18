package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import com.google.common.collect.ImmutableList;
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
import org.apache.calcite.rel.SingleRel;
import org.apache.calcite.rel.metadata.RelMdCollation;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.util.Pair;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Skip {@code offset} rows then emit at most {@code fetch}. Maps to the IR's {@code Fetch}.
 *
 * <p>A {@link SingleRel} rather than a {@code Sort} with an empty collation, so that the input's
 * ordering survives in the trait set — the IR says {@code Fetch} preserves input order and
 * collations, and a {@code Sort} node would advertise "no ordering" instead.
 */
public final class ChalkLimit extends SingleRel implements ChalkRel {
  private final @Nullable RexNode offset;
  private final @Nullable RexNode fetch;

  private ChalkLimit(
      RelOptCluster cluster,
      RelTraitSet traitSet,
      RelNode input,
      @Nullable RexNode offset,
      @Nullable RexNode fetch) {
    super(cluster, traitSet, input);
    this.offset = offset;
    this.fetch = fetch;
  }

  public static ChalkLimit create(RelNode input, @Nullable RexNode offset, @Nullable RexNode fetch) {
    RelOptCluster cluster = input.getCluster();
    RelMetadataQuery mq = cluster.getMetadataQuery();
    RelTraitSet traits =
        cluster
            .traitSetOf(ChalkConvention.LOCAL)
            .replaceIfs(RelCollationTraitDef.INSTANCE, () -> RelMdCollation.limit(mq, input));
    return new ChalkLimit(cluster, traits, input, offset, fetch);
  }

  public @Nullable RexNode offset() {
    return offset;
  }

  public @Nullable RexNode fetch() {
    return fetch;
  }

  public long offsetValue() {
    return offset == null ? 0L : RexLiteral.longValue(offset);
  }

  public @Nullable Long fetchValue() {
    return fetch == null ? null : RexLiteral.longValue(fetch);
  }

  @Override
  public RelNode copy(RelTraitSet traitSet, List<RelNode> inputs) {
    return new ChalkLimit(getCluster(), traitSet, onlyInput(inputs), offset, fetch);
  }

  @Override
  public RelWriter explainTerms(RelWriter pw) {
    return super.explainTerms(pw).itemIf("offset", offset, offset != null).itemIf("fetch", fetch, fetch != null);
  }

  @Override
  public double estimateRowCount(RelMetadataQuery mq) {
    double rows = mq.getRowCount(getInput());
    Long limit = fetchValue();
    return limit == null ? Math.max(rows - offsetValue(), 0) : Math.min(rows, offsetValue() + limit);
  }

  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double rows = estimateRowCount(mq);
    return planner.getCostFactory().makeCost(rows, rows, 0);
  }

  /** {@code Fetch} preserves input order, so a required collation goes straight down. */
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
    return Pair.of(traits, ImmutableList.of(traits));
  }

  private static RelNode onlyInput(List<RelNode> inputs) {
    if (inputs.size() != 1) {
      throw new IllegalArgumentException("ChalkLimit takes exactly one input");
    }
    return inputs.get(0);
  }
}
