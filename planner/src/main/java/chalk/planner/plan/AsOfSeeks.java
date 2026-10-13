package chalk.planner.plan;

import chalk.planner.plan.rel.ChalkAsOfJoin;
import chalk.planner.plan.rel.ChalkAsOfLookupJoin;
import chalk.planner.plan.rel.ChalkFilter;
import chalk.planner.plan.rel.ChalkTableScan;
import chalk.planner.plan.rules.ChalkAsOfLookupRule;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollationTraitDef;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * ASOF joins priced again once the search is over (F198).
 *
 * <p>The search prices each alternative once, at the row estimates its inputs have at that moment,
 * and those can be far from the final plan's (F199). A plain ASOF join chosen on early numbers can
 * then read a whole indexed table that a seek would reach in a few probes. So this pass walks the
 * chosen plan from the leaves up and, for each plain ASOF join that {@link ChalkAsOfLookupRule}
 * would seek, compares the two on the final plan's numbers and keeps the cheaper one.
 *
 * <p>A swap changes how a join reads its table and nothing else: the seek returns the same rows, of
 * the same row type. It claims no ordering, so a join that claims one, which a parent may rely on,
 * is left as it is.
 */
public final class AsOfSeeks {
  private AsOfSeeks() {}

  /** The plan after the pass, and how many joins now seek. */
  public record Result(RelNode plan, int swapped) {}

  /** {@code plan} with each plain ASOF join replaced by its seek wherever the seek is now cheaper. */
  public static Result reprice(RelNode plan) {
    plan.getCluster().invalidateMetadataQuery();
    RelMetadataQuery mq = plan.getCluster().getMetadataQuery();
    int[] swapped = {0};
    RelNode repriced = visit(plan, mq, swapped);
    return new Result(repriced, swapped[0]);
  }

  /** Leaves first, so that a join is priced over the inputs the plan will actually have. */
  private static RelNode visit(RelNode rel, RelMetadataQuery mq, int[] swapped) {
    List<RelNode> inputs = rel.getInputs();
    List<RelNode> visited = new ArrayList<>(inputs.size());
    boolean changed = false;
    for (RelNode input : inputs) {
      RelNode next = visit(input, mq, swapped);
      changed |= next != input;
      visited.add(next);
    }

    RelNode node = changed ? rel.copy(rel.getTraitSet(), visited) : rel;
    if (node instanceof ChalkAsOfJoin join) {
      ChalkAsOfLookupJoin seek = cheaperSeek(join, mq);
      if (seek != null) {
        swapped[0]++;
        return seek;
      }
    }
    return node;
  }

  /** The seek {@code join} can be, when it costs less than the join and its table's read. */
  private static @Nullable ChalkAsOfLookupJoin cheaperSeek(ChalkAsOfJoin join, RelMetadataQuery mq) {
    RelNode right = join.getRight();
    ChalkFilter filter = right instanceof ChalkFilter f ? f : null;
    if (!((filter == null ? right : filter.getInput()) instanceof ChalkTableScan scan)) {
      return null;
    }

    RelCollation ordering = join.getTraitSet().getTrait(RelCollationTraitDef.INSTANCE);
    if (ordering != null && !ordering.getFieldCollations().isEmpty()) {
      return null;
    }

    ChalkAsOfLookupJoin seek = ChalkAsOfLookupRule.seek(join, filter, scan);
    if (seek == null) {
      return null;
    }

    // The left input is the same either way, so the comparison is the join and its table's read
    // against the seek alone.
    RelOptCost joinCost = mq.getNonCumulativeCost(join);
    RelOptCost readCost = mq.getCumulativeCost(right);
    RelOptCost seekCost = mq.getNonCumulativeCost(seek);
    if (joinCost == null || readCost == null || seekCost == null) {
      return null;
    }
    return seekCost.isLt(joinCost.plus(readCost)) ? seek : null;
  }
}
