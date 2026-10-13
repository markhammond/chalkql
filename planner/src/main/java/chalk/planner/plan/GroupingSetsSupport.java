package chalk.planner.plan;

import chalk.planner.ErrorCode;
import chalk.planner.UnsupportedFeatureException;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.core.AggregateCall;

/**
 * What grouping sets refuse, checked once on the converted tree, before anything rewrites it
 * (D338, design 63 §8 a).
 *
 * <p>A {@code DISTINCT} aggregate under {@code GROUPING SETS}, {@code ROLLUP} or {@code CUBE} is
 * refused, naming the aggregate. It is checked here, on the tree SQL converted to, because the
 * distinct strategy's join expansion rewrites such an aggregate on a large input and leaves it on a
 * small one, and a statement must not be refused or answered by the size of its table.
 */
public final class GroupingSetsSupport {
  private GroupingSetsSupport() {}

  /** Walks the tree and throws for an aggregate grouping sets cannot compute. */
  public static void check(RelNode rel) {
    if (rel instanceof Aggregate aggregate && aggregate.getGroupSets().size() > 1) {
      for (AggregateCall call : aggregate.getAggCallList()) {
        if (call.isDistinct()) {
          throw new UnsupportedFeatureException(
              ErrorCode.UNSUPPORTED_SQL,
              call.getAggregation().getName() + "(DISTINCT …) with GROUPING SETS, ROLLUP or CUBE",
              "A DISTINCT aggregate is computed for one grouping. Write each grouping as its own"
                  + " GROUP BY, and join them with UNION ALL.");
        }
      }
    }

    for (RelNode input : rel.getInputs()) {
      check(input);
    }
  }
}
