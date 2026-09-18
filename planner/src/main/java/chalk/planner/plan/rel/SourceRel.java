package chalk.planner.plan.rel;

import chalk.planner.plan.SourceConvention;
import org.apache.calcite.rel.RelNode;

/**
 * A rel the source runs, not Chalk (D83). Everything in a {@link SourceConvention} implements this;
 * the whole subtree below the boundary converter becomes one {@code RemoteQuery}.
 *
 * <p>Every implementation extends the Calcite core class for its operator — {@code Filter}, {@code
 * Project}, {@code Aggregate}, {@code Sort}, {@code Join}, {@code TableScan} — because that is what
 * makes {@code RelToSqlConverter}'s reflective dispatch find a visitor for it without a line of
 * per-node conversion code, and what lets the standard metadata handlers estimate its cost.
 */
public interface SourceRel extends RelNode {

  /** The convention this rel belongs to, which carries the source's descriptor. */
  default SourceConvention sourceConvention() {
    return (SourceConvention) getTraitSet().getConvention();
  }

  /** The source this subtree will be handed to. */
  default String sourceId() {
    return sourceConvention().sourceId();
  }
}
