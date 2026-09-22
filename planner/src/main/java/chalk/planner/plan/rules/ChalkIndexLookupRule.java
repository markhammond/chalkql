package chalk.planner.plan.rules;

import chalk.ir.v1.Index;
import chalk.ir.v1.IndexKind;
import chalk.planner.plan.ChalkSelectivity;
import chalk.planner.plan.IndexMatcher;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.rel.ChalkFilter;
import chalk.planner.plan.rel.ChalkIndexLookup;
import chalk.planner.plan.rel.ChalkTableScan;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelRule;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;

/**
 * {@code Filter(ChalkTableScan)} → {@code Filter(ChalkIndexLookup)}, or the lookup alone when the
 * index answers the whole condition (D38).
 *
 * <p>One alternative per index that matches anything, registered beside the scan; the scan is never
 * removed, so Volcano chooses by cost and a predicate that selects most of the table keeps its full
 * scan. That is the rev 3 criterion the corpus's 07/08 pair checks.
 */
public final class ChalkIndexLookupRule extends RelRule<ChalkRuleConfig> {
  private final PushdownPolicy policy;

  public ChalkIndexLookupRule(PushdownPolicy policy) {
    this(config(policy), policy);
  }

  private ChalkIndexLookupRule(ChalkRuleConfig config, PushdownPolicy policy) {
    super(config);
    this.policy = policy;
  }

  private static ChalkRuleConfig config(PushdownPolicy policy) {
    return ChalkRuleConfig.of(
        "ChalkIndexLookupRule",
        b ->
            b.operand(ChalkFilter.class)
                .oneInput(b1 -> b1.operand(ChalkTableScan.class).noInputs()),
        config -> new ChalkIndexLookupRule(config, policy));
  }

  @Override
  public boolean matches(RelOptRuleCall call) {
    return policy.allowsIndexLookup();
  }

  @Override
  public void onMatch(RelOptRuleCall call) {
    ChalkFilter filter = call.rel(0);
    ChalkTableScan scan = call.rel(1);

    // D276: a goaled scan belongs to one limit, and its goal was computed for a scan. Converting it
    // here would produce a lookup goaled by that number — which is the wrong number, because a
    // lookup consumes part of the condition the goal was inflated by — and would merge the goaled
    // chain's sets back into the plain ones. ChalkRowGoalRule offers the goaled lookup chains
    // itself, from the plain lookup this rule has already produced, so nothing is lost by
    // declining.
    if (scan.rowGoal() > 0) {
      return;
    }

    List<Index> indexes = scan.chalkTable().indexes();
    if (indexes.isEmpty()) {
      return;
    }

    RexBuilder rexBuilder = scan.getCluster().getRexBuilder();
    List<Integer> projection = scan.projection();

    for (Index index : indexes) {
      // The condition is written against the scan's row, so the key has to be stated in the same
      // terms. A key column the scan does not project truncates the usable key (-1).
      List<Integer> keyFields = new ArrayList<>(index.getColumnsCount());
      for (int column : index.getColumnsList()) {
        keyFields.add(projection.indexOf(column));
      }

      IndexMatcher.Result result =
          IndexMatcher.split(
              filter.getCondition(),
              keyFields,
              scan.getRowType(),
              index.getKind() == IndexKind.INDEX_KIND_HASH,
              rexBuilder);
      if (!result.matched()) {
        continue;
      }

      ChalkSelectivity.Estimate selectivity =
          ChalkSelectivity.of(
              consumed(filter.getCondition(), result.residual(), rexBuilder),
              scan.chalkTable(),
              projection,
              scan.getCluster());

      ChalkIndexLookup lookup =
          ChalkIndexLookup.create(scan, index, ImmutableList.copyOf(result.ranges()), selectivity);
      RexNode residual = result.residual();
      RelNode alternative = residual == null ? lookup : ChalkFilter.create(lookup, residual);
      call.transformTo(alternative);
    }
  }

  /**
   * The part of the condition the ranges enforce, which is what the lookup's row count estimates.
   * Stated as "the whole condition unless the residual is the whole condition" rather than rebuilt
   * from the ranges: the ranges hold bounds, and selectivity is a question about predicates.
   */
  private static RexNode consumed(RexNode condition, RexNode residual, RexBuilder rexBuilder) {
    if (residual == null) {
      return condition;
    }

    List<RexNode> conjuncts =
        new ArrayList<>(org.apache.calcite.plan.RelOptUtil.conjunctions(
            org.apache.calcite.rex.RexUtil.expandSearch(rexBuilder, null, condition)));
    conjuncts.removeAll(org.apache.calcite.plan.RelOptUtil.conjunctions(residual));
    return conjuncts.isEmpty()
        ? rexBuilder.makeLiteral(true)
        : org.apache.calcite.rex.RexUtil.composeConjunction(rexBuilder, conjuncts);
  }
}
