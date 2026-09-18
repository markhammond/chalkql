package chalk.planner.plan.rules;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.rel.ChalkLimit;
import chalk.planner.plan.rel.ChalkSort;
import chalk.planner.plan.rel.ChalkTopN;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelRule;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.logical.LogicalSort;

/**
 * The rule that decides whether a query sorts at all (docs/design/03-planner.md §4.3).
 *
 * <ul>
 *   <li>Ask the input for the required ordering as a trait and emit no sort — a {@code ChalkLimit}
 *       when there is an offset or fetch, otherwise nothing at all. This is what deletes the {@code
 *       Sort} from {@code SELECT * FROM bars ORDER BY ts}: the scan carries the declared collation
 *       and filters and projects propagate it, so Volcano satisfies the requirement for free.
 *   <li>Also offer the explicit alternative — a {@code ChalkTopN} when there is a fetch (a bounded
 *       heap rather than a full sort), otherwise a {@code ChalkSort} with a {@code ChalkLimit} above
 *       it for an offset. When nothing delivers the ordering this is the only plan; when something
 *       does, it loses on cost.
 * </ul>
 *
 * <p>A pure {@code LIMIT} with no {@code ORDER BY} is also a {@code LogicalSort} in Calcite and
 * takes the first branch, because the empty collation is satisfied by anything.
 *
 * <p>A {@link RelRule} over {@link ChalkRuleConfig} (D134): Calcite 1.42 deprecates the static
 * operand helpers, and Chalk states its one operand through {@link RelRule.OperandBuilder} instead.
 */
public final class ChalkSortRule extends RelRule<ChalkRuleConfig> {
  public static final ChalkSortRule INSTANCE =
      new ChalkSortRule(
          ChalkRuleConfig.of(
              "ChalkSortRule",
              b -> b.operand(LogicalSort.class).trait(Convention.NONE).anyInputs(),
              ChalkSortRule::new));

  private ChalkSortRule(ChalkRuleConfig config) {
    super(config);
  }

  @Override
  public void onMatch(RelOptRuleCall call) {
    LogicalSort sort = call.rel(0);
    RelCollation collation = sort.getCollation();
    boolean bounded = sort.offset != null || sort.fetch != null;

    RelNode ordered = convertInput(sort, collation);
    call.transformTo(bounded ? ChalkLimit.create(ordered, sort.offset, sort.fetch) : ordered);

    if (collation.getFieldCollations().isEmpty()) {
      // Pure LIMIT / OFFSET: there is nothing to sort, so there is no second alternative.
      return;
    }

    RelNode unordered = convertInput(sort, RelCollations.EMPTY);
    if (sort.fetch != null) {
      call.transformTo(ChalkTopN.create(unordered, collation, sort.offset, sort.fetch));
    } else {
      RelNode explicitSort = ChalkSort.create(unordered, collation);
      call.transformTo(
          sort.offset == null ? explicitSort : ChalkLimit.create(explicitSort, sort.offset, null));
    }
  }

  private static RelNode convertInput(LogicalSort sort, RelCollation collation) {
    return convert(
        sort.getInput(),
        sort.getInput().getTraitSet().replace(ChalkConvention.LOCAL).replace(collation));
  }
}
