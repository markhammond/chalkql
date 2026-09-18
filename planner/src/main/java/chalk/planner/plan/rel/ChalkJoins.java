package chalk.planner.plan.rel;

import org.apache.calcite.rel.RelNodes;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.metadata.RelMdUtil;

/** Shared join helpers. */
final class ChalkJoins {
  private ChalkJoins() {}

  /**
   * Calcite's tie-break for a commutable join: make one of the two orientations infinitesimally more
   * expensive, so a plan whose two inputs cost the same does not depend on the order the rules
   * happened to fire in. Copied from {@code EnumerableHashJoin.computeSelfCost}; without it the
   * golden plans would flake whenever both orientations cost the same.
   */
  static double breakTies(double work, Join join) {
    switch (join.getJoinType()) {
      case SEMI:
      case ANTI:
        // Neither can be flipped, so there is nothing to break.
        return work;
      case RIGHT:
        return RelMdUtil.addEpsilon(work);
      default:
        return RelNodes.COMPARATOR.compare(join.getLeft(), join.getRight()) > 0
            ? RelMdUtil.addEpsilon(work)
            : work;
    }
  }

  /** True for the two ASOF join types Calcite 1.42 defines (V14). */
  static boolean isAsOf(JoinRelType type) {
    return type == JoinRelType.ASOF || type == JoinRelType.LEFT_ASOF;
  }
}
