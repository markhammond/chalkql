package chalk.planner.ir;

import chalk.planner.plan.ChalkOperatorTable;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.sql2rel.SqlRexContext;
import org.apache.calcite.sql2rel.SqlRexConvertlet;

/**
 * {@code CURRENT_TIME} and {@code LOCALTIME} as what they stand for (F191): the time of day of
 * {@code CURRENT_TIMESTAMP}, {@code CAST(CURRENT_TIMESTAMP(p) AS TIME(p))}. The executors read the
 * execution's one instant, as they do for the timestamp, and take its time of day as they cast any
 * timestamp to a time. No source is sent it: the timestamp is a session's, which a source would
 * answer from its own clock (D89).
 */
final class TimeOfDayConvertlet implements SqlRexConvertlet {
  static final TimeOfDayConvertlet INSTANCE = new TimeOfDayConvertlet();

  private TimeOfDayConvertlet() {}

  @Override
  public RexNode convertCall(SqlRexContext cx, SqlCall call) {
    RexBuilder rex = cx.getRexBuilder();
    RelDataType time = cx.getValidator().getValidatedNodeType(call);
    RelDataType timestamp =
        cx.getTypeFactory()
            .createTypeWithNullability(
                cx.getTypeFactory().createSqlType(SqlTypeName.TIMESTAMP, time.getPrecision()),
                time.isNullable());

    // The precision written, if any, is the timestamp's too, so the two agree to the digit.
    List<RexNode> operands = new ArrayList<>();
    for (SqlNode operand : call.getOperandList()) {
      operands.add(cx.convertExpression(operand));
    }

    RexNode now = rex.makeCall(timestamp, ChalkOperatorTable.CURRENT_TIMESTAMP, operands);
    return rex.makeCast(time, now);
  }
}
