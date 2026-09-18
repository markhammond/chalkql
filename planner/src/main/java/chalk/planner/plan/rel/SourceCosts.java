package chalk.planner.plan.rel;

import chalk.ir.v1.CostProfile;
import org.apache.calcite.plan.volcano.RelSubset;
import org.apache.calcite.rel.RelNode;

/**
 * The cost profile of the table a remote subtree reads (D38), shared by the boundary and by every
 * federation node that pays for a call across one.
 *
 * <p>The first {@link SourceScan} leaf wins. With one leaf that is exact; with several — a pushed
 * join — it is a choice, and a deterministic choice is what a cost model needs. The alternative,
 * averaging profiles, would make a plan's cost depend on which side of a commuted join was visited
 * first.
 */
public final class SourceCosts {
  private SourceCosts() {}

  /**
   * Whether this input is a <b>remote boundary</b> — the thing that actually costs a round trip.
   *
   * <p>A projection or a filter above the boundary is still the same fetch, so the walk goes through
   * anything with one input and stops at anything with more: a join's rows are not one boundary's.
   */
  public static boolean isRemote(RelNode rel) {
    RelNode node = rel;
    if (node instanceof RelSubset subset) {
      RelNode best = subset.getBest();
      node = best != null ? best : subset.getOriginal();
    }
    if (node instanceof SourceToLocalConverter) {
      return true;
    }
    if (node instanceof org.apache.calcite.rel.SingleRel single) {
      return isRemote(single.getInput());
    }
    return false;
  }

  /**
   * Whether <b>reading this subtree at all</b> means asking a source for rows — the question a
   * nested loop's inner side raises, which is not the same as {@link #isRemote}'s.
   *
   * <p>{@link #isRemote} answers about a <em>boundary</em>: is this input, once the projections and
   * filters above it are peeled, one source's fetch? That is the right question when what is being
   * priced is the fetch itself. A nested loop's inner side raises a different one: whatever is in
   * there is re-read once per outer row, so a remote query <em>anywhere</em> under it is a plan
   * whose cost is a lie — and a join, a set operation or a lookup join's own lookup branch is
   * exactly where one can hide from the boundary reading. This is {@code I-IR-20}'s own walk
   * (docs/design/02-ir.md §8), so that the cost, the planner's backstop and the IR validator all
   * read one shape the same way; before F55 they did not, and a plan the cost let through and the
   * backstop passed was refused by the client instead.
   */
  public static boolean fetchesRemotely(RelNode rel) {
    RelNode node = rel;
    if (node instanceof RelSubset subset) {
      RelNode best = subset.getBest();
      node = best != null ? best : subset.getOriginal();
    }
    if (node instanceof SourceToLocalConverter || node instanceof SourceScan) {
      return true;
    }
    // An adaptive join keeps its LOOKUP branch out of the inputs Volcano materialises, and that
    // branch is where the fetch is; the IR walker visits it, so this does too.
    if (node instanceof ChalkAdaptiveJoin adaptive && fetchesRemotely(adaptive.lookupSide())) {
      return true;
    }
    for (RelNode input : node.getInputs()) {
      if (fetchesRemotely(input)) {
        return true;
      }
    }
    return false;
  }

  /** The profile of the first {@code SourceScan} under {@code rel}, or the planner's defaults. */
  public static CostProfile profileOf(RelNode rel) {
    if (rel instanceof RelSubset subset) {
      RelNode best = subset.getBest();
      return profileOf(best != null ? best : subset.getOriginal());
    }
    if (rel instanceof SourceScan scan) {
      return scan.chalkTable().costProfile();
    }
    if (rel instanceof ChalkTableScan scan) {
      return scan.chalkTable().costProfile();
    }
    for (RelNode input : rel.getInputs()) {
      CostProfile found = profileOf(input);
      if (!found.equals(CostProfile.getDefaultInstance())) {
        return found;
      }
    }
    return CostProfile.getDefaultInstance();
  }
}
