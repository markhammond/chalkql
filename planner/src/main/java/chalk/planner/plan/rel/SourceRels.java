package chalk.planner.plan.rel;

import chalk.planner.plan.SourceConvention;
import com.google.common.collect.ImmutableList;
import com.google.common.collect.ImmutableSet;
import java.util.List;
import java.util.Set;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.core.AggregateCall;
import org.apache.calcite.rel.core.CorrelationId;
import org.apache.calcite.rel.core.Filter;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.core.Sort;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.util.ImmutableBitSet;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The operators a source may run above its own scan (D83), one class per Calcite core operator. Each
 * extends the core class, which is what makes {@code RelToSqlConverter} dispatch to the right
 * visitor and what lets the standard metadata handlers estimate rows and selectivity for it.
 *
 * <p>None of them costs anything by itself. The source's work is charged twice over in the cost
 * model already — once per row at {@link SourceScan} and once per call at {@link
 * SourceToLocalConverter} — and charging the pushed operators again would make "push more" lose to
 * "push less" for no reason: a filter pushed into the source is free to Chalk, and its whole value
 * is the rows it stops the boundary from paying for.
 */
public final class SourceRels {
  private SourceRels() {}

  /** Zero-cost: what a pushed operator costs Chalk is nothing. */
  private static RelOptCost free(RelOptPlanner planner) {
    return planner.getCostFactory().makeCost(0, 0, 0);
  }

  /** A predicate the source evaluates. */
  public static final class SourceFilter extends Filter implements SourceRel {
    public SourceFilter(
        RelOptCluster cluster, RelTraitSet traits, RelNode child, RexNode condition) {
      super(cluster, traits, child, condition);
    }

    @Override
    public Filter copy(RelTraitSet traitSet, RelNode input, RexNode condition) {
      return new SourceFilter(getCluster(), traitSet, input, condition);
    }

    @Override
    public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
      return free(planner);
    }
  }

  /** A projection the source evaluates — column pruning and scalar expressions alike. */
  public static final class SourceProject extends Project implements SourceRel {
    public SourceProject(
        RelOptCluster cluster,
        RelTraitSet traits,
        RelNode input,
        List<? extends RexNode> projects,
        RelDataType rowType) {
      super(cluster, traits, ImmutableList.of(), input, projects, rowType, ImmutableSet.of());
    }

    @Override
    public Project copy(
        RelTraitSet traitSet, RelNode input, List<RexNode> projects, RelDataType rowType) {
      return new SourceProject(getCluster(), traitSet, input, projects, rowType);
    }

    @Override
    public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
      return free(planner);
    }
  }

  /** A {@code GROUP BY} the source evaluates, with or without {@code DISTINCT}. */
  public static final class SourceAggregate extends Aggregate implements SourceRel {
    public SourceAggregate(
        RelOptCluster cluster,
        RelTraitSet traits,
        RelNode input,
        ImmutableBitSet groupSet,
        @Nullable List<ImmutableBitSet> groupSets,
        List<AggregateCall> aggCalls) {
      super(cluster, traits, ImmutableList.of(), input, groupSet, groupSets, aggCalls);
    }

    @Override
    public Aggregate copy(
        RelTraitSet traitSet,
        RelNode input,
        ImmutableBitSet groupSet,
        @Nullable List<ImmutableBitSet> groupSets,
        List<AggregateCall> aggCalls) {
      return new SourceAggregate(getCluster(), traitSet, input, groupSet, groupSets, aggCalls);
    }

    @Override
    public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
      return free(planner);
    }
  }

  /** An {@code ORDER BY} / {@code LIMIT} / {@code OFFSET} the source evaluates. */
  public static final class SourceSort extends Sort implements SourceRel {
    public SourceSort(
        RelOptCluster cluster,
        RelTraitSet traits,
        RelNode child,
        RelCollation collation,
        @Nullable RexNode offset,
        @Nullable RexNode fetch) {
      super(cluster, traits, child, collation, offset, fetch);
    }

    @Override
    public Sort copy(
        RelTraitSet traitSet,
        RelNode newInput,
        RelCollation newCollation,
        @Nullable RexNode offset,
        @Nullable RexNode fetch) {
      return new SourceSort(getCluster(), traitSet, newInput, newCollation, offset, fetch);
    }

    @Override
    public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
      return free(planner);
    }
  }

  /** A join between two tables of the <em>same</em> source, which that source runs itself. */
  public static final class SourceJoin extends Join implements SourceRel {
    public SourceJoin(
        RelOptCluster cluster,
        RelTraitSet traits,
        RelNode left,
        RelNode right,
        RexNode condition,
        Set<CorrelationId> variablesSet,
        JoinRelType joinType) {
      super(cluster, traits, ImmutableList.of(), left, right, condition, variablesSet, joinType);
    }

    @Override
    public Join copy(
        RelTraitSet traitSet,
        RexNode conditionExpr,
        RelNode left,
        RelNode right,
        JoinRelType joinType,
        boolean semiJoinDone) {
      return new SourceJoin(
          getCluster(), traitSet, left, right, conditionExpr, getVariablesSet(), joinType);
    }

    @Override
    public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
      return free(planner);
    }
  }

  /** The convention a rel belongs to, or null when it is not in a source convention. */
  public static @Nullable SourceConvention conventionOf(RelNode rel) {
    return rel.getTraitSet().getConvention() instanceof SourceConvention convention
        ? convention
        : null;
  }
}
