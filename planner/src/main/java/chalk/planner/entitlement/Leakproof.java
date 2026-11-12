package chalk.planner.entitlement;

import chalk.planner.plan.ChalkContextMembership;
import chalk.planner.plan.ChalkOperatorTable;
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
import org.apache.calcite.sql.type.SqlTypeUtil;
import org.checkerframework.checker.nullness.qual.Nullable;

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
 *
 * <p>The functions a mask is written with that cannot raise are leakproof too (F300): {@code
 * FINGERPRINT} and {@code PRESENT}, which answer NULL for a NULL and nothing else; concatenation and
 * {@code CHAR_LENGTH}; {@code SUBSTRING} whose length is absent or a literal that is not negative,
 * the one thing the function refuses; and {@code ROUND}, {@code FLOOR} and {@code CEIL} over a
 * floating-point value, which cannot overflow as a decimal's can, and {@code FLOOR} and {@code
 * CEIL} of a date or a timestamp to a unit. A host function its catalog declares leakproof is the
 * host's promise and is taken at its word, as PostgreSQL takes {@code LEAKPROOF}. Every one of them is
 * barrier-relevant as well: a condition written with them crosses the barrier and reaches the
 * source, so a function joins this list only once nothing in its kernel can raise for a row.
 */
public final class Leakproof {
  private Leakproof() {}

  /** What a leakproof expression may be written with, in an author's words, for a refusal to say. */
  public static final String SPELLING =
      "comparisons, AND, OR and NOT, the IS tests, IN lists, LIKE, lossless casts, literals and"
          + " context values, and COALESCE, CASE, UPPER, LOWER, TRIM, concatenation, CHAR_LENGTH,"
          + " FINGERPRINT, PRESENT, SUBSTRING of a literal length, ROUND, FLOOR and CEIL of a"
          + " floating-point value, FLOOR and CEIL of a date or a timestamp, and host functions"
          + " declared leakproof, over those";

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
      Set.of(
          SqlStdOperatorTable.UPPER,
          SqlStdOperatorTable.LOWER,
          SqlStdOperatorTable.TRIM,
          SqlStdOperatorTable.CONCAT,
          SqlStdOperatorTable.CHAR_LENGTH,
          SqlStdOperatorTable.CHARACTER_LENGTH,
          ChalkOperatorTable.FINGERPRINT,
          ChalkOperatorTable.PRESENT,
          ChalkOperatorTable.MASKED);

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
    if (!own(call)) {
      return false;
    }
    for (RexNode operand : call.getOperands()) {
      if (!is(operand)) {
        return false;
      }
    }
    return true;
  }

  /** Whether {@code call}'s own evaluation cannot raise, given operands that cannot. */
  private static boolean own(RexCall call) {
    return KINDS.contains(call.getKind())
        || FUNCTIONS.contains(call.getOperator())
        || ChalkContextMembership.is(call)
        || chalk.planner.ReservedNames.CTX.equalsIgnoreCase(call.getOperator().getName())
        || (call.getKind() == SqlKind.CAST && RexUtil.isLosslessCast(call))
        || substringOfLiteralLength(call)
        || roundingOfFloatingPoint(call)
        || declaredLeakproof(call);
  }

  /**
   * A host function its catalog declares leakproof: the host's promise, which nothing here can
   * check, that it raises for no row and says nothing of one beyond its value — as PostgreSQL takes
   * a function marked {@code LEAKPROOF}. It may then stand in a mask, and cross the barrier.
   */
  private static boolean declaredLeakproof(RexCall call) {
    return chalk.planner.plan.UserOperators.declarationOf(call.getOperator())
            instanceof chalk.planner.catalog.UserFunction declared
        && declared.descriptor().getLeakproof();
  }

  /** {@code SUBSTRING(v FROM s)}, or {@code FOR} a literal length that is not negative. */
  private static boolean substringOfLiteralLength(RexCall call) {
    if (call.getOperator() != SqlStdOperatorTable.SUBSTRING) {
      return false;
    }
    if (call.getOperands().size() < 3) {
      return true;
    }
    return call.getOperands().get(2) instanceof RexLiteral length
        && (length.isNull()
            || (length.getValueAs(java.math.BigDecimal.class) instanceof java.math.BigDecimal value
                && value.signum() >= 0));
  }

  /**
   * {@code ROUND}, {@code FLOOR} or {@code CEIL} of a floating-point value, and {@code FLOOR} or
   * {@code CEIL} of a date or a timestamp to a unit, which truncate and cannot overflow.
   */
  private static boolean roundingOfFloatingPoint(RexCall call) {
    boolean rounding =
        call.getOperator() == SqlStdOperatorTable.ROUND
            || call.getKind() == SqlKind.FLOOR
            || call.getKind() == SqlKind.CEIL;
    if (!rounding || call.getOperands().isEmpty()) {
      return false;
    }
    org.apache.calcite.rel.type.RelDataType operand = call.getOperands().get(0).getType();
    return SqlTypeUtil.isApproximateNumeric(operand)
        || (call.getKind() != SqlKind.OTHER_FUNCTION
            && call.getOperator() != SqlStdOperatorTable.ROUND
            && SqlTypeUtil.isDatetime(operand));
  }

  /**
   * The outermost part of {@code rex} that can raise, or fail, differently from one row to the
   * next, or null when it is leakproof: what a refusal names, so the author sees which call to
   * rewrite and not only that the whole expression will not do.
   */
  public static @Nullable RexNode offending(RexNode rex) {
    if (is(rex)) {
      return null;
    }
    if (rex instanceof RexFieldAccess access) {
      RexNode inner = offending(access.getReferenceExpr());
      return inner != null ? inner : rex;
    }
    if (rex instanceof RexCall call && own(call)) {
      for (RexNode operand : call.getOperands()) {
        RexNode inner = offending(operand);
        if (inner != null) {
          return inner;
        }
      }
    }
    return rex;
  }

  private static boolean containsCorrelation(RexNode rex) {
    return RexUtil.containsCorrelation(rex);
  }
}
