package chalk.planner.ir;

import java.util.List;
import org.apache.calcite.avatica.util.TimeUnit;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlIntervalQualifier;
import org.apache.calcite.sql.SqlLiteral;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql2rel.SqlRexContext;
import org.apache.calcite.sql2rel.SqlRexConvertlet;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code TIMESTAMPDIFF(unit, from, to)} as the call it is (D337, F175), which the IR has a function
 * for: whole units from {@code from} to {@code to}, truncated toward zero. Calcite's own convertlet
 * expands it into an interval subtraction cast to an integer, and on the way counts {@code
 * MICROSECOND} and {@code MILLISECOND} as whole seconds multiplied up, losing the fraction and
 * overflowing an INTEGER after about 35 minutes. A unit with a time frame of its own, such as
 * {@code WEEK(MONDAY)}, is left to Calcite.
 */
final class TimestampDiffConvertlet implements SqlRexConvertlet {
  static final TimestampDiffConvertlet INSTANCE = new TimestampDiffConvertlet();

  private TimestampDiffConvertlet() {}

  /** The call's unit, or null when it names a time frame rather than a plain unit. */
  static @Nullable TimeUnit unitOf(SqlCall call) {
    SqlNode unit = call.operand(0);
    if (unit instanceof SqlIntervalQualifier qualifier && qualifier.timeFrameName == null) {
      return qualifier.getStartUnit();
    }
    if (unit instanceof SqlLiteral literal && literal.getValue() instanceof TimeUnit plain) {
      return plain;
    }
    return null;
  }

  @Override
  public RexNode convertCall(SqlRexContext cx, SqlCall call) {
    TimeUnit unit = unitOf(call);
    RexBuilder rexBuilder = cx.getRexBuilder();
    return rexBuilder.makeCall(
        cx.getValidator().getValidatedNodeType(call),
        call.getOperator(),
        List.of(
            rexBuilder.makeFlag(unit),
            cx.convertExpression(call.operand(1)),
            cx.convertExpression(call.operand(2))));
  }
}
