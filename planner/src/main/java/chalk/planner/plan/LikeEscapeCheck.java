package chalk.planner.plan;

import chalk.planner.ErrorCode;
import chalk.planner.InvalidArgumentException;
import org.apache.calcite.runtime.CalciteContextException;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlCharStringLiteral;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.util.SqlBasicVisitor;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Refuses a malformed {@code LIKE} escape, or a literal pattern malformed under its escape, in the
 * statement as written (D312) — with the position, which is why it runs over the validated tree
 * rather than over the plan. The rules are {@link LikePatterns}'s.
 *
 * <p>An escape has to be a character literal: it decides what the pattern means, and a pattern
 * whose meaning is known only at execution cannot be planned for. A parameter <em>pattern</em> is
 * fine and is checked when it is bound; a pattern that becomes a literal only once constants are
 * folded is checked again where the plan is written, without a position.
 */
public final class LikeEscapeCheck {
  private LikeEscapeCheck() {}

  /** Throws a positioned validation error for the first malformed LIKE; silent otherwise. */
  public static void check(SqlNode statement) {
    statement.accept(
        new SqlBasicVisitor<Void>() {
          @Override
          public @Nullable Void visit(SqlCall call) {
            if (call.getKind() == SqlKind.LIKE) {
              checkLike(call);
            }

            return super.visit(call);
          }
        });
  }

  private static void checkLike(SqlCall call) {
    String escape = null;
    if (call.operandCount() > 2) {
      SqlNode node = call.operand(2);
      if (!(node instanceof SqlCharStringLiteral literal)) {
        throw refusal(
            node.getParserPosition(),
            "a LIKE ESCAPE must be a character literal: it decides what the pattern means, and "
                + "the plan is made for one meaning");
      }

      escape = literal.getValueAs(String.class);
      String defect = LikePatterns.escapeDefect(escape);
      if (defect != null) {
        throw refusal(node.getParserPosition(), defect);
      }
    }

    if (call.operandCount() > 1 && call.operand(1) instanceof SqlCharStringLiteral pattern) {
      String defect = LikePatterns.patternDefect(pattern.getValueAs(String.class), escape);
      if (defect != null) {
        throw refusal(pattern.getParserPosition(), defect);
      }
    }
  }

  private static CalciteContextException refusal(SqlParserPos pos, String message) {
    return new CalciteContextException(
        message,
        new InvalidArgumentException(ErrorCode.SQL_VALIDATION, message),
        pos.getLineNum(),
        pos.getColumnNum(),
        pos.getEndLineNum(),
        pos.getEndColumnNum());
  }
}
