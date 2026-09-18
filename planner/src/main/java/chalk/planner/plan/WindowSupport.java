package chalk.planner.plan;

import chalk.planner.UnsupportedFeatureException;
import chalk.planner.ir.FunctionMapping;
import chalk.planner.plan.rules.ChalkTumbleRule;
import chalk.planner.plan.rules.ChalkWindowTableFunctionRule;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.TableFunctionScan;
import org.apache.calcite.rel.core.Window;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.sql.SqlAggFunction;

/**
 * What the window steps refuse, checked once on the logical tree before Volcano sees it
 * ({@code 13-window-functions.md} §9, {@code 14-windows-ii.md} §1).
 *
 * <p>It runs here rather than in a converter rule because a rule that declines to convert leaves the
 * optimiser with nothing to do and produces "cannot plan", which tells a caller nothing. Refusing on
 * the logical tree gives a {@code PLAN_ERROR_KIND_UNSUPPORTED} that names the feature and says where
 * it is written down.
 */
public final class WindowSupport {
  private WindowSupport() {}

  /** Walks the tree and throws for anything the engine cannot express. */
  public static void check(RelNode rel) {
    if (rel instanceof TableFunctionScan scan) {
      checkTableFunction(scan);
    } else if (rel instanceof Window window) {
      checkWindow(window);
    }

    for (RelNode input : rel.getInputs()) {
      check(input);
    }
  }

  /**
   * Any table function still standing after {@link ChalkTumbleRule} has run in the Hep pre-pass.
   * {@code TUMBLE} is the one Chalk rewrites into a projection; {@code HOP} and {@code SESSION}
   * become operators in Volcano (D55), so the shape their rule needs is checked here — on the
   * logical tree, where the failure can name the function — rather than left to fail as "cannot
   * plan".
   */
  private static void checkTableFunction(TableFunctionScan scan) {
    String name =
        scan.getCall() instanceof RexCall call ? call.getOperator().getName() : "table function";
    // A table function the catalog declared is step 22's business, not this check's: it becomes a
    // macro expansion or a ChalkTableFunctionScan (D78).
    if (scan.getCall() instanceof RexCall userCall
        && UserOperators.declarationOf(userCall.getOperator()) != null) {
      return;
    }
    if (ChalkTumbleRule.isTumble(scan)) {
      throw new UnsupportedFeatureException(
          "TUMBLE with an alignment offset",
          "Chalk rewrites TUMBLE(TABLE t, DESCRIPTOR(c), INTERVAL) into TIME_BUCKET; the optional "
              + "third operand is not implemented (docs/design/13-window-functions.md §9).");
    }

    if (ChalkWindowTableFunctionRule.isWindowFunction(scan)) {
      if (ChalkWindowTableFunctionRule.analyse(scan) != null) {
        return;
      }

      throw new UnsupportedFeatureException(
          name + " in this form",
          "Chalk implements HOP(TABLE t, DESCRIPTOR(c), slide, size) and "
              + "SESSION(TABLE t, DESCRIPTOR(c), [DESCRIPTOR(key),] gap) over a column of the input "
              + "(docs/design/14-windows-ii.md §1).");
    }

    throw new UnsupportedFeatureException(
        "table function " + name,
        "Chalk implements the window table functions TUMBLE, HOP and SESSION "
            + "(docs/design/14-windows-ii.md §1).");
  }

  private static void checkWindow(Window window) {
    for (Window.Group group : window.groups) {
      for (Window.RexWinAggCall call : group.aggCalls) {
        // Throws, naming the function, when the IR has no id for it. Asked here so an unsupported
        // window function is refused on the logical tree rather than surviving into RelToIr.
        SqlAggFunction function = (SqlAggFunction) call.getOperator();
        if (UserOperators.declarationOf(function) != null) {
          // A user aggregate over a frame travels by name (D80); the IR has no id to look up.
          continue;
        }
        if (FunctionMapping.windowFunction(function) == null) {
          FunctionMapping.aggregate(function);
        }
      }
    }
  }
}
