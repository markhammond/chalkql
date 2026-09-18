package chalk.planner.plan;

import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelShuttleImpl;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.logical.LogicalJoin;

/**
 * How a query's joins are ordered (D44).
 *
 * <p>Up to {@link #VOLCANO_MAX_JOINS} joins, Volcano does it: {@code JOIN_COMMUTE} and
 * {@code JOIN_ASSOCIATE} enumerate the orders and the cost model picks one. The search is
 * exponential in the number of joins and that is fine while the number is small.
 *
 * <p>Beyond the threshold the Hep pre-pass collapses the joins into a {@code MultiJoin} and
 * {@code LoptOptimizeJoinRule} — Calcite's heuristic left-deep ordering, driven by the M2 row counts
 * and distinct counts — picks one order before Volcano ever sees the tree. Volcano then only chooses
 * physical operators.
 *
 * <p>The threshold is a named constant and part of {@code planner_config_hash}: it changes plans.
 */
public final class JoinOrdering {
  /**
   * The largest number of joins in one query for which Volcano enumerates orders itself. Four joins
   * is five tables — TPC-H Q3 and Q10 are under it, Q5 is over.
   */
  public static final int VOLCANO_MAX_JOINS = 4;

  private JoinOrdering() {}

  /** How many joins the tree holds. */
  public static int count(RelNode rel) {
    Counter counter = new Counter();
    rel.accept(counter);
    return counter.joins;
  }

  /** True when this tree has too many joins for Volcano to enumerate. */
  public static boolean needsHeuristicOrdering(RelNode rel) {
    return count(rel) > VOLCANO_MAX_JOINS;
  }

  /** For {@code PlannerConfig.configHash}. */
  public static String summary() {
    return "volcanoMaxJoins=" + VOLCANO_MAX_JOINS;
  }

  private static final class Counter extends RelShuttleImpl {
    private int joins;

    @Override
    public RelNode visit(LogicalJoin join) {
      joins++;
      return super.visit(join);
    }

    @Override
    public RelNode visit(RelNode other) {
      if (other instanceof Join) {
        joins++;
      }
      return super.visit(other);
    }
  }
}
