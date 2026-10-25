package chalk.planner.plan;

import chalk.planner.ErrorCode;
import chalk.planner.InvalidArgumentException;
import java.math.BigDecimal;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.runtime.CalciteContextException;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlCharStringLiteral;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlNodeList;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.sql.util.SqlBasicVisitor;
import org.apache.calcite.sql.validate.SqlValidator;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Refuses text compared with a number that it cannot be read as (F278): {@code
 * qty = 'x'}, where {@code qty} is a {@code DOUBLE}, with the position of the text, and naming what
 * it is compared with. Calcite casts the text to the other side's type, and a cast that can never
 * succeed reached the planner's own literal conversion as an internal assertion. Text that does
 * read as the type, {@code qty = '1.5'}, is a number written in quotes and stands. Text compared
 * with a date or a time is left to ChalkQL's cast, which reads more than Calcite does, offsets among
 * it, and reads it when the plan runs.
 *
 * <p>Read as the cast reads it when it runs, so that nothing it would take is refused: a whole
 * number type takes a whole number, an approximate one anything {@code Double.parseDouble} does,
 * and a {@code DECIMAL} anything {@code BigDecimal} does, each trimmed of
 * spaces. It runs over the validated statement, before conversion, where every operand
 * has its type and every literal its position.
 */
public final class TextLiteralCheck {
  private TextLiteralCheck() {}

  /** Throws a positioned validation error for the first text that cannot be read as the number it meets. */
  public static void check(SqlNode statement, SqlValidator validator) {
    statement.accept(
        new SqlBasicVisitor<Void>() {
          @Override
          public @Nullable Void visit(SqlCall call) {
            checkCall(call, validator);
            return super.visit(call);
          }
        });
  }

  private static void checkCall(SqlCall call, SqlValidator validator) {
    switch (call.getKind()) {
      case EQUALS, NOT_EQUALS, LESS_THAN, LESS_THAN_OR_EQUAL, GREATER_THAN, GREATER_THAN_OR_EQUAL -> {
        if (call.operandCount() == 2) {
          checkPair(call.operand(0), call.operand(1), validator);
          checkPair(call.operand(1), call.operand(0), validator);
        }
      }
      case IN, NOT_IN -> {
        if (call.operandCount() == 2 && call.operand(1) instanceof SqlNodeList values) {
          for (SqlNode value : values) {
            checkPair(call.operand(0), value, validator);
          }
        }
      }
      case BETWEEN -> {
        for (int i = 1; i < call.operandCount(); i++) {
          checkPair(call.operand(0), call.operand(i), validator);
        }
      }
      default -> {}
    }
  }

  /**
   * {@code text}, if it is a text literal, read as the type of {@code other}. Validation's implicit
   * coercion has already wrapped such a literal in a cast to that type, so the literal is looked for
   * under one too.
   */
  private static void checkPair(SqlNode other, SqlNode text, SqlValidator validator) {
    if (text instanceof SqlCall cast
        && cast.getKind() == SqlKind.CAST
        && cast.operandCount() >= 1
        && cast.operand(0) instanceof SqlCharStringLiteral) {
      text = cast.operand(0);
    }

    if (!(text instanceof SqlCharStringLiteral literal) || unwrapped(other) instanceof SqlCharStringLiteral) {
      return;
    }

    RelDataType type = validator.getValidatedNodeTypeIfKnown(other);
    if (type == null) {
      return;
    }

    String value = literal.getValueAs(String.class);
    SqlTypeName name = type.getSqlTypeName();
    if (readsAs(value, name)) {
      return;
    }

    String what = unwrapped(other) instanceof SqlIdentifier identifier
        ? identifier.names.get(identifier.names.size() - 1)
        : null;
    String as = ("AEIOU".indexOf(name.getName().charAt(0)) >= 0 ? "an " : "a ") + name.getName();
    String message = what == null
        ? "the text '" + value + "' cannot be read as " + as + ", which what it is compared with is: "
            + "compare that with " + example(name) + ", or with text that reads as one"
        : "the text '" + value + "' cannot be read as " + as + ", which " + what + " is: "
            + "compare " + what + " with " + example(name) + ", or with text that reads as one";
    throw refusal(literal.getParserPosition(), message);
  }

  /** The node a cast wraps, or the node itself. */
  private static SqlNode unwrapped(SqlNode node) {
    return node instanceof SqlCall cast && cast.getKind() == SqlKind.CAST && cast.operandCount() >= 1
        ? cast.operand(0)
        : node;
  }

  /** Whether the cast to {@code type} takes {@code text}; true for a type this check does not judge. */
  private static boolean readsAs(String text, SqlTypeName type) {
    String trimmed = text.trim();
    try {
      switch (type) {
        case TINYINT -> Byte.parseByte(trimmed);
        case SMALLINT -> Short.parseShort(trimmed);
        case INTEGER -> Integer.parseInt(trimmed);
        case BIGINT -> Long.parseLong(trimmed);
        case DECIMAL -> new BigDecimal(trimmed);
        case FLOAT, REAL -> Float.parseFloat(trimmed);
        case DOUBLE -> Double.parseDouble(trimmed);
        default -> {
          return true;
        }
      }

      return true;
    } catch (RuntimeException unreadable) {
      return false;
    }
  }

  private static String example(SqlTypeName type) {
    return switch (type) {
      case TINYINT, SMALLINT, INTEGER, BIGINT -> "a whole number, such as 42";
      default -> "a number, such as 1.5";
    };
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
