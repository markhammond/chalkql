package chalk.planner.plan;

import chalk.ir.v1.RowCountKind;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.plan.rel.SourceToLocalConverter;
import org.apache.calcite.plan.volcano.RelSubset;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.core.Sort;
import org.apache.calcite.rel.core.TableScan;

/**
 * Whether a subtree's estimated row count is a <em>measured statistic</em> or a guess (D97, D103).
 *
 * <p>This is the one question the adaptive join turns on: §1 says {@code Adaptive} is emitted
 * "whenever the small side's estimate is not a measured statistic — the default whenever the
 * estimate is {@code (guess)} or {@code (hint)}". Calcite has no such notion; every
 * {@code RelMetadataQuery.getRowCount} answer is a double, and the provenance is lost at the first
 * handler. So it is reconstructed here, from the two things that actually decide it.
 *
 * <p>A row count is measured when the leaf is a table whose catalog entry says
 * {@code ROW_COUNT_KIND_EXACT}, and nothing between the leaf and the node changed the count by
 * anything the planner had to guess. A {@code Project} does not change the count and a {@code Sort}
 * without a fetch does not either, so both pass through; a {@code Filter}, a {@code Join}, an
 * {@code Aggregate}, a {@code Fetch} and a remote boundary over any of them do, so all of them
 * answer no. That is deliberately conservative: answering "measured" wrongly plans a fixed strategy
 * on a number that was invented, which is the failure adaptive execution exists to avoid, while
 * answering "guess" wrongly costs one materialisation and a count.
 */
public final class RowCountProvenance {
  private RowCountProvenance() {}

  /** Whether {@code rel}'s estimated row count comes from a statistic somebody measured. */
  public static boolean isMeasured(RelNode rel) {
    RelNode node = resolve(rel);
    if (node instanceof TableScan scan) {
      ChalkTable table = scan.getTable().unwrap(ChalkTable.class);
      return table != null
          && table.descriptor().getRowCountKind() == RowCountKind.ROW_COUNT_KIND_EXACT
          && table.descriptor().getRowCount() >= 0;
    }
    if (node instanceof Project project) {
      return isMeasured(project.getInput());
    }
    if (node instanceof Sort sort) {
      return sort.fetch == null && sort.offset == null && isMeasured(sort.getInput());
    }
    if (node instanceof SourceToLocalConverter boundary) {
      // The boundary does not change the count; what is under it might.
      return isMeasured(boundary.getInput());
    }
    return false;
  }

  /** A {@code RelSubset}'s best (or original) member, so the walk works mid-optimisation. */
  private static RelNode resolve(RelNode rel) {
    if (rel instanceof RelSubset subset) {
      RelNode best = subset.getBest();
      return best != null ? best : subset.getOriginal();
    }
    return rel;
  }
}
