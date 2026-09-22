package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import com.google.common.collect.ImmutableList;
import java.util.List;
import org.apache.calcite.plan.DeriveMode;
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

  /**
   * A limit does <b>not</b> derive from its input, and this is not an optimisation left on the
   * table (F113).
   *
   * <p>Derivation registers a copy of this node, over a differently-ordered child, in the same
   * {@code RelSet}. For a {@code Filter} or a {@code Project} that is sound: swap the child for an
   * equivalent one in another order and the node still produces the same rows. For a limit it is
   * not: which rows come out is decided by the order they arrive in. A {@code ChalkLimit} that
   * {@link ChalkSortRule} put over an input converted to {@code (volume, symbol, ts)} means "the
   * first five by that order"; a derived twin over the plain scan means "the first five rows",
   * Volcano may satisfy the set's collation by sorting <em>above</em> it, and
   * {@code Sort(Limit(Scan))} is not {@code Limit(Sort(Scan))}.
   *
   * <p>It cost nothing while a top-N was mispriced at its output row count, because the top-N won
   * anyway; the moment {@code ChalkTopN} started costing its heap pass, the derived twin became the
   * cheapest plan in the set and the answers changed.
   *
   * <p>Pushing a requirement <em>down</em> stays: {@link #passThroughTraits} is the sound direction
   * — sort the input, then take the first n — and is what serves every ordering a parent asks for.
   */
  @Override
  public DeriveMode getDeriveMode() {
    return DeriveMode.PROHIBITED;
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
