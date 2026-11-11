package chalk.planner.entitlement;

import chalk.planner.plan.ChalkContextMembership;
import java.util.Set;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexCorrelVariable;
import org.apache.calcite.rex.RexDynamicParam;
import org.apache.calcite.rex.RexFieldAccess;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlOperator;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;

/**
 * Whether an expression can fail, or say anything, depending on which row it reads (F288): the
 * test an entitlement barrier lets an expression through by.
 *
 * <p>An expression over a row the entitlement has not yet excluded must not raise for that row, nor
 * fail where another row would not: an error quoting the row's value, or a failure only some rows
 * cause, discloses what the principal is not admitted to. So an expression is <em>leakproof</em>
 * when nothing in it can raise per row: comparisons, the connectives, the null and truth tests, an
 * {@code IN} list, a lossless cast, a context list, {@code COALESCE}, {@code CASE}, case folding and
 * {@code LIKE} (whose pattern is validated before any row is read) over leakproof operands, and any
 * expression that reads no column at all, whose failure would not depend on a row. Arithmetic, a
 * cast that can fail, a division and every other function are not, as PostgreSQL treats a qual
 * that is not marked leakproof against a security barrier.
 */
public final class Leakproof {
  private Leakproof() {}

  /** The kinds whose own evaluation cannot raise, given operands that cannot. */
  private static final Set<SqlKind> KINDS =
      Set.of(
          SqlKind.EQUALS,
          SqlKind.NOT_EQUALS,
          SqlKind.LESS_THAN,
          SqlKind.LESS_THAN_OR_EQUAL,
          SqlKind.GREATER_THAN,
          SqlKind.GREATER_THAN_OR_EQUAL,
          SqlKind.IS_DISTINCT_FROM,
          SqlKind.IS_NOT_DISTINCT_FROM,
          SqlKind.AND,
          SqlKind.OR,
          SqlKind.NOT,
          SqlKind.IS_NULL,
          SqlKind.IS_NOT_NULL,
          SqlKind.IS_TRUE,
          SqlKind.IS_NOT_TRUE,
          SqlKind.IS_FALSE,
          SqlKind.IS_NOT_FALSE,
          SqlKind.SEARCH,
          SqlKind.COALESCE,
          SqlKind.CASE,
          SqlKind.LIKE);

  /** Functions of the same standing that have no kind of their own. */
  private static final Set<SqlOperator> FUNCTIONS =
      Set.of(SqlStdOperatorTable.UPPER, SqlStdOperatorTable.LOWER, SqlStdOperatorTable.TRIM);

  /** Whether {@code rex} cannot raise, or fail, differently from one row to the next. */
  public static boolean is(RexNode rex) {
    if (!RexUtil.containsInputRef(rex) && !containsCorrelation(rex)) {
      return true;
    }
    if (rex instanceof RexInputRef
        || rex instanceof RexLiteral
        || rex instanceof RexDynamicParam
        || rex instanceof RexCorrelVariable) {
      return true;
    }
    if (rex instanceof RexFieldAccess access) {
      return is(access.getReferenceExpr());
    }
    if (!(rex instanceof RexCall call)) {
      return false;
    }
    boolean own =
        KINDS.contains(call.getKind())
            || FUNCTIONS.contains(call.getOperator())
            || ChalkContextMembership.is(call)
            || chalk.planner.ReservedNames.CTX.equalsIgnoreCase(call.getOperator().getName())
            || (call.getKind() == SqlKind.CAST && RexUtil.isLosslessCast(call));
    if (!own) {
      return false;
    }
    for (RexNode operand : call.getOperands()) {
      if (!is(operand)) {
        return false;
      }
    }
    return true;
  }

  private static boolean containsCorrelation(RexNode rex) {
    return RexUtil.containsCorrelation(rex);
  }
}
