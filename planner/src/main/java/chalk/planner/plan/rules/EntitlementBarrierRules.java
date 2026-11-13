package chalk.planner.plan.rules;

import chalk.planner.entitlement.Leakproof;
import chalk.planner.plan.rel.ChalkEntitlementBarrier;
import chalk.planner.plan.rel.EntitlementBarrier;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.plan.RelOptRule;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.plan.RelRule;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Values;
import org.apache.calcite.rel.logical.LogicalAggregate;
import org.apache.calcite.rel.logical.LogicalFilter;
import org.apache.calcite.rel.logical.LogicalJoin;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rel.logical.LogicalSort;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.tools.RelBuilder;

/**
 * What may cross an entitled leaf's fence (F288), and the fence's conversion for the search.
 *
 * <p>The fence keeps the statement's work above the entitlement, so that it reads only the rows the
 * principal is admitted to. What cannot leak may go below it, so the leaf still pushes what it did
 * to an index or a source:
 *
 * <ul>
 *   <li>a filter's leakproof conjuncts ({@link Leakproof}); the rest stay above;
 *   <li>a projection every expression of which is leakproof, which is what column pruning is;
 *   <li>an aggregate, whose functions run over the rows its input gives, after the entitlement's
 *       filter, however far down it goes;
 *   <li>a sort or a bound, which only orders and counts those rows;
 *   <li>a join whose condition is leakproof, so two entitled tables of one source still join there.
 * </ul>
 *
 * <p>A conjunct that reads a correlation variable stays above, as a condition of the join the
 * decorrelator makes of it: below the fence it would be a correlation the decorrelator cannot see
 * past, and a join's condition is evaluated over the admitted rows in any case.
 */
public final class EntitlementBarrierRules {
  private EntitlementBarrierRules() {}

  /** The fence in the local convention, so a source's subtree ends below it. */
  public static final RelOptRule CONVERT =
      ChalkRuleConfig.of(
              "EntitlementBarrierConvert",
              b -> b.operand(EntitlementBarrier.class).trait(Convention.NONE).anyInputs(),
              Convert::new)
          .toRule();

  /** A filter's leakproof conjuncts go below the fence; the rest stay above it. */
  public static final RelOptRule FILTER =
      ChalkRuleConfig.of(
              "EntitlementBarrierFilter",
              b ->
                  b.operand(LogicalFilter.class)
                      .oneInput(i -> i.operand(EntitlementBarrier.class).anyInputs()),
              Filter::new)
          .toRule();

  /** A leakproof projection goes below the fence. */
  public static final RelOptRule PROJECT =
      ChalkRuleConfig.of(
              "EntitlementBarrierProject",
              b ->
                  b.operand(LogicalProject.class)
                      .oneInput(i -> i.operand(EntitlementBarrier.class).anyInputs()),
              Project::new)
          .toRule();

  /** An aggregate goes below the fence. */
  public static final RelOptRule AGGREGATE =
      ChalkRuleConfig.of(
              "EntitlementBarrierAggregate",
              b ->
                  b.operand(LogicalAggregate.class)
                      .oneInput(i -> i.operand(EntitlementBarrier.class).anyInputs()),
              Aggregate::new)
          .toRule();

  /** A sort or a bound goes below the fence. */
  public static final RelOptRule SORT =
      ChalkRuleConfig.of(
              "EntitlementBarrierSort",
              b ->
                  b.operand(LogicalSort.class)
                      .oneInput(i -> i.operand(EntitlementBarrier.class).anyInputs()),
              Sort::new)
          .toRule();

  /** A join with a leakproof condition goes below a fence over its left input. */
  public static final RelOptRule JOIN_LEFT =
      ChalkRuleConfig.of(
              "EntitlementBarrierJoinLeft",
              b ->
                  b.operand(LogicalJoin.class)
                      .inputs(
                          l -> l.operand(EntitlementBarrier.class).anyInputs(),
                          r -> r.operand(RelNode.class).anyInputs()),
              Join::new)
          .toRule();

  /** And below a fence over its right input. */
  public static final RelOptRule JOIN_RIGHT =
      ChalkRuleConfig.of(
              "EntitlementBarrierJoinRight",
              b ->
                  b.operand(LogicalJoin.class)
                      .inputs(
                          l -> l.operand(RelNode.class).anyInputs(),
                          r -> r.operand(EntitlementBarrier.class).anyInputs()),
              Join::new)
          .toRule();

  /** Two fences, one directly over the other, are one. */
  public static final RelOptRule MERGE =
      ChalkRuleConfig.of(
              "EntitlementBarrierMerge",
              b ->
                  b.operand(EntitlementBarrier.class)
                      .oneInput(i -> i.operand(EntitlementBarrier.class).anyInputs()),
              Merge::new)
          .toRule();

  /**
   * A fence over no rows is no fence: an empty leaf has nothing to leak, and the rules that prune
   * what an empty relation feeds must still see it.
   */
  public static final RelOptRule EMPTY =
      ChalkRuleConfig.of(
              "EntitlementBarrierEmpty",
              b ->
                  b.operand(EntitlementBarrier.class)
                      .oneInput(i -> i.operand(Values.class).predicate(Values::isEmpty).noInputs()),
              Empty::new)
          .toRule();

  /** The rules that move work below the fence, for the Hep passes and the search alike. */
  public static List<RelOptRule> transposes() {
    return List.of(FILTER, PROJECT, AGGREGATE, SORT, JOIN_LEFT, JOIN_RIGHT, MERGE, EMPTY);
  }

  /** Whether {@code rex} may go below a fence: leakproof, and reading no correlation variable. */
  private static boolean crosses(RexNode rex) {
    return Leakproof.is(rex) && !RexUtil.containsCorrelation(rex) && RexUtil.SubQueryFinder.find(rex) == null;
  }

  private static final class Convert extends RelRule<ChalkRuleConfig> {
    Convert(ChalkRuleConfig config) {
      super(config);
    }

    @Override
    public void onMatch(RelOptRuleCall call) {
      EntitlementBarrier barrier = call.rel(0);
      call.transformTo(
          ChalkEntitlementBarrier.create(ChalkInputs.unordered(barrier.getInput())));
    }
  }

  private static final class Filter extends RelRule<ChalkRuleConfig> {
    Filter(ChalkRuleConfig config) {
      super(config);
    }

    @Override
    public void onMatch(RelOptRuleCall call) {
      LogicalFilter filter = call.rel(0);
      EntitlementBarrier barrier = call.rel(1);
      if (!filter.getVariablesSet().isEmpty()) {
        return;
      }
      List<RexNode> below = new ArrayList<>();
      List<RexNode> above = new ArrayList<>();
      for (RexNode conjunct : RelOptUtil.conjunctions(filter.getCondition())) {
        (crosses(conjunct) ? below : above).add(conjunct);
      }
      if (below.isEmpty()) {
        return;
      }
      RelBuilder builder = call.builder();
      RelNode fenced =
          barrier.copy(
              barrier.getTraitSet(), List.of(builder.push(barrier.getInput()).filter(below).build()));
      builder.push(fenced);
      if (!above.isEmpty()) {
        builder.filter(above);
      }
      call.transformTo(builder.build());
    }
  }

  private static final class Project extends RelRule<ChalkRuleConfig> {
    Project(ChalkRuleConfig config) {
      super(config);
    }

    @Override
    public void onMatch(RelOptRuleCall call) {
      LogicalProject project = call.rel(0);
      EntitlementBarrier barrier = call.rel(1);
      for (RexNode expression : project.getProjects()) {
        if (!crosses(expression)) {
          return;
        }
      }
      RelNode moved =
          project.copy(
              project.getTraitSet(), barrier.getInput(), project.getProjects(), project.getRowType());
      call.transformTo(barrier.copy(barrier.getTraitSet(), List.of(moved)));
    }
  }

  private static final class Aggregate extends RelRule<ChalkRuleConfig> {
    Aggregate(ChalkRuleConfig config) {
      super(config);
    }

    @Override
    public void onMatch(RelOptRuleCall call) {
      LogicalAggregate aggregate = call.rel(0);
      EntitlementBarrier barrier = call.rel(1);
      RelNode moved = aggregate.copy(aggregate.getTraitSet(), List.of(barrier.getInput()));
      call.transformTo(barrier.copy(barrier.getTraitSet(), List.of(moved)));
    }
  }

  private static final class Join extends RelRule<ChalkRuleConfig> {
    Join(ChalkRuleConfig config) {
      super(config);
    }

    @Override
    public void onMatch(RelOptRuleCall call) {
      LogicalJoin join = call.rel(0);
      if (!join.getVariablesSet().isEmpty() || !crosses(join.getCondition())) {
        return;
      }
      // The fence is the root of the policy's quarantine (F302), so what goes below it is settled as
      // the policy's own work: a row its filter raises for is left out without a word. The other
      // side goes only where it cannot raise, so a statement's own sub-query keeps its errors.
      if (!fencedOrCannotRaise(call.rel(1)) || !fencedOrCannotRaise(call.rel(2))) {
        return;
      }
      RelNode left = unfenced(call.rel(1));
      RelNode right = unfenced(call.rel(2));
      RelNode moved =
          join.copy(
              join.getTraitSet(), join.getCondition(), left, right, join.getJoinType(), join.isSemiJoinDone());
      call.transformTo(EntitlementBarrier.over(moved));
    }

    /**
     * Whether {@code input} is a fence, or a tree nothing in which can raise for a row: every
     * expression leakproof and every aggregate a count or an extreme. A fence deeper down is its own
     * quarantine, and what is under it is its policy's.
     */
    private static boolean fencedOrCannotRaise(RelNode input) {
      RelNode node = resolved(input);
      if (node instanceof EntitlementBarrier) {
        return true;
      }
      if (node instanceof org.apache.calcite.rel.core.Aggregate aggregate) {
        for (org.apache.calcite.rel.core.AggregateCall measure : aggregate.getAggCallList()) {
          switch (measure.getAggregation().getKind()) {
            case COUNT, MIN, MAX, ANY_VALUE -> {}
            default -> {
              return false;
            }
          }
        }
      } else if (!(node instanceof org.apache.calcite.rel.core.TableScan
          || node instanceof org.apache.calcite.rel.core.Filter
          || node instanceof org.apache.calcite.rel.core.Project
          || node instanceof org.apache.calcite.rel.core.Join
          || node instanceof org.apache.calcite.rel.core.Sort
          || node instanceof org.apache.calcite.rel.core.Values)) {
        return false;
      }
      boolean[] raises = {false};
      node.accept(
          new org.apache.calcite.rex.RexShuttle() {
            @Override
            public RexNode visitCall(org.apache.calcite.rex.RexCall call) {
              raises[0] |= !Leakproof.is(call);
              return call;
            }
          });
      if (raises[0]) {
        return false;
      }
      for (RelNode child : node.getInputs()) {
        if (!fencedOrCannotRaise(child)) {
          return false;
        }
      }
      return true;
    }

    private static RelNode resolved(RelNode rel) {
      if (rel instanceof org.apache.calcite.plan.hep.HepRelVertex vertex) {
        return vertex.getCurrentRel();
      }
      if (rel instanceof org.apache.calcite.plan.volcano.RelSubset subset) {
        RelNode best = subset.getBest();
        return best != null ? best : subset.getOriginal();
      }
      return rel;
    }

    private static RelNode unfenced(RelNode input) {
      return input instanceof EntitlementBarrier barrier ? barrier.getInput() : input;
    }
  }

  private static final class Empty extends RelRule<ChalkRuleConfig> {
    Empty(ChalkRuleConfig config) {
      super(config);
    }

    @Override
    public void onMatch(RelOptRuleCall call) {
      Values empty = call.rel(1);
      call.transformTo(empty);
    }
  }

  private static final class Merge extends RelRule<ChalkRuleConfig> {
    Merge(ChalkRuleConfig config) {
      super(config);
    }

    @Override
    public void onMatch(RelOptRuleCall call) {
      EntitlementBarrier outer = call.rel(0);
      EntitlementBarrier inner = call.rel(1);
      call.transformTo(outer.copy(outer.getTraitSet(), List.of(inner.getInput())));
    }
  }

  private static final class Sort extends RelRule<ChalkRuleConfig> {
    Sort(ChalkRuleConfig config) {
      super(config);
    }

    @Override
    public void onMatch(RelOptRuleCall call) {
      LogicalSort sort = call.rel(0);
      EntitlementBarrier barrier = call.rel(1);
      RelNode moved = sort.copy(sort.getTraitSet(), List.of(barrier.getInput()));
      call.transformTo(barrier.copy(barrier.getTraitSet(), List.of(moved)));
    }
  }
}
