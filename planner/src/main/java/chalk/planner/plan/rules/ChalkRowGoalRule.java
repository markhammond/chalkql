package chalk.planner.plan.rules;

import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.RowGoal;
import chalk.planner.plan.rel.ChalkFilter;
import chalk.planner.plan.rel.ChalkIndexLookup;
import chalk.planner.plan.rel.ChalkLimit;
import chalk.planner.plan.rel.ChalkProject;
import chalk.planner.plan.rel.ChalkTableScan;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.RelOptRule;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelRule;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.metadata.RelMetadataQuery;

/**
 * D276 — the goaled alternative ({@code docs/design/46-row-goals.md} §2.1).
 *
 * <p>A {@code ChalkLimit} <b>with a fetch</b> over a chain of at most one {@link ChalkProject} and
 * one {@link ChalkFilter} down to a leaf registers the same chain over a <em>goaled copy of the
 * leaf</em>: the same rows, estimated and costed for the ones the limit will actually pull. The goal
 * travels down the chain
 *
 * <pre>
 *   at the limit    offset + fetch, saturating
 *   through Project unchanged            (a projection neither filters nor expands)
 *   through Filter  ceil(goal / sel)     sel from the leaf's own statistics
 *   at the leaf     min(goal, the leaf's own row estimate)
 * </pre>
 *
 * <p>and only the new {@code ChalkLimit} is registered as equivalent to the matched one, so the
 * goaled leaf lands in a set of its own and is reachable only under the limit that asked for it. The
 * rule declines a leaf that already carries a goal, which is what stops it firing on its own output,
 * and a limit with an offset and no fetch, which bounds nothing.
 *
 * <p>Nothing else ever receives a goal. A {@code ChalkTopN}, a {@code ChalkSort}, an aggregate, a
 * join, a window and a set operation consume their input whole or reshape it, so a goal above them
 * says nothing about the leaf below — and the operand shapes here are precisely the list of chains
 * that can produce the first N useful rows without consuming their input.
 *
 * <p>Gated by {@code policy.allowsIndexLookup()}, the gate the index rules use, so a plan at {@code
 * PUSHDOWN_LEVEL_NONE} — the reference executor's — carries no goal at all.
 */
public final class ChalkRowGoalRule extends RelRule<ChalkRuleConfig> {

  /** The nodes between the limit and the leaf, and where each sits in the operand tree. */
  private enum Chain {
    LEAF(-1, -1, 1),
    FILTER(-1, 1, 2),
    PROJECT(1, -1, 2),
    PROJECT_FILTER(1, 2, 3);

    private final int project;
    private final int filter;
    private final int leaf;

    Chain(int project, int filter, int leaf) {
      this.project = project;
      this.filter = filter;
      this.leaf = leaf;
    }
  }

  private final PushdownPolicy policy;
  private final Chain chain;

  private ChalkRowGoalRule(ChalkRuleConfig config, PushdownPolicy policy, Chain chain) {
    super(config);
    this.policy = policy;
    this.chain = chain;
  }

  /**
   * The eight shapes of §2.1: four chains over each of the two leaf kinds. Stated as eight operand
   * trees rather than as one rule that walks whatever it finds, because an operand tree is what
   * Volcano indexes its rule queue by — and because the list of shapes <em>is</em> the claim about
   * which descendants a goal may reach.
   */
  public static List<RelOptRule> rules(PushdownPolicy policy) {
    List<RelOptRule> rules = new ArrayList<>(8);
    for (Class<? extends RelNode> leaf : List.of(ChalkTableScan.class, ChalkIndexLookup.class)) {
      for (Chain chain : Chain.values()) {
        rules.add(rule(policy, chain, leaf));
      }
    }

    return rules;
  }

  private static RelOptRule rule(
      PushdownPolicy policy, Chain chain, Class<? extends RelNode> leaf) {
    ChalkRuleConfig config =
        ChalkRuleConfig.of(
            "ChalkRowGoalRule:" + chain + ":" + leaf.getSimpleName(),
            b -> operands(b, chain, leaf),
            c -> new ChalkRowGoalRule(c, policy, chain));
    return config.toRule();
  }

  private static RelRule.Done operands(
      RelRule.OperandBuilder b, Chain chain, Class<? extends RelNode> leaf) {
    return switch (chain) {
      case LEAF -> b.operand(ChalkLimit.class).oneInput(l -> l.operand(leaf).noInputs());
      case FILTER ->
          b.operand(ChalkLimit.class)
              .oneInput(
                  f ->
                      f.operand(ChalkFilter.class).oneInput(l -> l.operand(leaf).noInputs()));
      case PROJECT ->
          b.operand(ChalkLimit.class)
              .oneInput(
                  p ->
                      p.operand(ChalkProject.class).oneInput(l -> l.operand(leaf).noInputs()));
      case PROJECT_FILTER ->
          b.operand(ChalkLimit.class)
              .oneInput(
                  p ->
                      p.operand(ChalkProject.class)
                          .oneInput(
                              f ->
                                  f.operand(ChalkFilter.class)
                                      .oneInput(l -> l.operand(leaf).noInputs())));
    };
  }

  @Override
  public boolean matches(RelOptRuleCall call) {
    return policy.allowsIndexLookup();
  }

  @Override
  public void onMatch(RelOptRuleCall call) {
    ChalkLimit limit = call.rel(0);
    Long fetch = limit.fetchValue();
    if (fetch == null) {
      // An offset with no fetch states no goal: nothing above bounds how far the input is read.
      return;
    }

    long goal = new RowGoal(limit.offsetValue(), fetch).required();
    if (goal <= 0) {
      return;
    }

    RelNode leaf = call.rel(chain.leaf);
    if (rowGoalOf(leaf) > 0) {
      // Already goaled. Declining here is what stops the rule firing on its own output.
      return;
    }

    ChalkFilter filter = chain.filter < 0 ? null : call.rel(chain.filter);
    ChalkProject project = chain.project < 0 ? null : call.rel(chain.project);
    RelMetadataQuery mq = call.getMetadataQuery();

    // A projection neither filters nor expands, so the goal passes through it unchanged. A filter
    // does: to see `goal` rows above it, the leaf must be read for `goal / sel` of them. The
    // selectivity is asked of the leaf the operand matched — which *is* this filter's input, and
    // the node ChalkRelMetadata answers for with the table's own statistics rather than a guess
    // about a subset.
    if (filter != null) {
      Double selectivity = mq.getSelectivity(leaf, filter.getCondition());
      if (selectivity == null || selectivity <= 0) {
        return;
      }

      goal = inflate(goal, selectivity);
    }

    // The goal is capped at the leaf's own estimate (§2.1). A goal at or above it says nothing the
    // leaf did not already know, so the alternative is not offered at all rather than offered at
    // the same cost — two rels of equal cost would make which one is chosen a matter of iteration
    // order, and a plan digest has to be stable.
    Double leafRows = mq.getRowCount(leaf);
    if (leafRows == null || goal >= leafRows) {
      return;
    }

    RelNode goaled = withRowGoal(leaf, goal);
    if (filter != null) {
      goaled = ChalkFilter.create(goaled, filter.getCondition());
    }
    if (project != null) {
      goaled = ChalkProject.create(goaled, project.getProjects(), project.getRowType());
    }

    call.transformTo(ChalkLimit.create(goaled, limit.offset(), limit.fetch()));
  }

  /** {@code ceil(goal / selectivity)}, saturating: a tiny selectivity must not wrap the goal. */
  private static long inflate(long goal, double selectivity) {
    double inflated = Math.ceil(goal / selectivity);
    return inflated >= Long.MAX_VALUE ? Long.MAX_VALUE : (long) inflated;
  }

  private static long rowGoalOf(RelNode leaf) {
    if (leaf instanceof ChalkTableScan scan) {
      return scan.rowGoal();
    }

    return ((ChalkIndexLookup) leaf).rowGoal();
  }

  private static RelNode withRowGoal(RelNode leaf, long goal) {
    if (leaf instanceof ChalkTableScan scan) {
      return scan.withRowGoal(goal);
    }

    return ((ChalkIndexLookup) leaf).withRowGoal(goal);
  }
}
