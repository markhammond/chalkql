package chalk.planner.ir;

import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql2rel.SqlRexContext;
import org.apache.calcite.sql2rel.SqlRexConvertlet;

/**
 * {@code LEAST} and {@code GREATEST} as the comparisons they stand for (D337, design 63 §6). Two
 * arguments {@code x} and {@code y} are {@code CASE WHEN y IS NULL OR x <= y THEN COALESCE(x, y)
 * ELSE y END}, with {@code >=} for {@code GREATEST}: a NULL is skipped, and the result is NULL only
 * when both are. More arguments pair up in a balanced tree, so a plan grows with the arguments
 * rather than doubling with each. Each argument is cast to the call's type first, so the
 * comparisons are of one type.
 */
final class ExtremumConvertlet implements SqlRexConvertlet {
  static final ExtremumConvertlet INSTANCE = new ExtremumConvertlet();

  private ExtremumConvertlet() {}

  @Override
  public RexNode convertCall(SqlRexContext cx, SqlCall call) {
    RexBuilder rexBuilder = cx.getRexBuilder();
    RelDataType type = cx.getValidator().getValidatedNodeType(call);
    RelDataType nullable = cx.getTypeFactory().createTypeWithNullability(type, true);
    List<RexNode> level = new ArrayList<>(call.operandCount());
    for (int i = 0; i < call.operandCount(); i++) {
      level.add(rexBuilder.makeCast(nullable, cx.convertExpression(call.operand(i)), true, false));
    }

    boolean least = call.getKind() == SqlKind.LEAST;
    while (level.size() > 1) {
      List<RexNode> next = new ArrayList<>((level.size() + 1) / 2);
      for (int i = 0; i + 1 < level.size(); i += 2) {
        next.add(pair(rexBuilder, level.get(i), level.get(i + 1), least));
      }
      if (level.size() % 2 == 1) {
        next.add(level.get(level.size() - 1));
      }
      level = next;
    }
    return rexBuilder.makeCast(type, level.get(0), true, false);
  }

  private static RexNode pair(RexBuilder rexBuilder, RexNode x, RexNode y, boolean least) {
    RexNode chooseX =
        rexBuilder.makeCall(
            SqlStdOperatorTable.OR,
            rexBuilder.makeCall(SqlStdOperatorTable.IS_NULL, y),
            rexBuilder.makeCall(
                least
                    ? SqlStdOperatorTable.LESS_THAN_OR_EQUAL
                    : SqlStdOperatorTable.GREATER_THAN_OR_EQUAL,
                x,
                y));
    return rexBuilder.makeCall(
        SqlStdOperatorTable.CASE,
        chooseX,
        rexBuilder.makeCall(SqlStdOperatorTable.COALESCE, x, y),
        y);
  }
}
