package chalk.planner.diag;

import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.sql.SqlExplainFormat;
import org.apache.calcite.sql.SqlExplainLevel;

/** {@code RelOptUtil.toString} wrappers. Log-only: nothing parses these back. */
public final class PlanText {
  private PlanText() {}

  /** Node names and attributes, no costs — stable enough to check in as a Java-side golden file. */
  public static String withAttributes(RelNode rel) {
    return RelOptUtil.dumpPlan("", rel, SqlExplainFormat.TEXT, SqlExplainLevel.EXPPLAN_ATTRIBUTES);
  }

  /** Everything, including estimated row counts and costs. For a human staring at a bad plan. */
  public static String withCosts(RelNode rel) {
    return RelOptUtil.dumpPlan("", rel, SqlExplainFormat.TEXT, SqlExplainLevel.ALL_ATTRIBUTES);
  }
}
