package chalk.planner.plan;

import chalk.planner.ErrorCode;
import chalk.planner.InvalidArgumentException;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.type.SqlTypeFamily;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * What a {@code LIKE} pattern and its {@code ESCAPE} mean, and when they are malformed (D312, D313).
 *
 * <p>The rules are the ones Calcite's own runtime applies, which are the SQL standard's: an escape
 * is exactly one character, and in the pattern it must be followed by {@code %}, {@code _} or
 * itself. A two-operand {@code LIKE} has no escape character at all, so every pattern it is given is
 * well formed and a backslash is an ordinary character. ChalkQL refuses what these rules refuse —
 * at planning for a literal, at execution for a parameter — rather than guessing, because the
 * databases it pushes to guess differently and the answer would depend on where the {@code LIKE}
 * ran.
 *
 * <p>Positions are 1-based and count code points, and no message quotes the pattern: a pattern is
 * the statement's own data, and a refusal is a log line.
 */
public final class LikePatterns {
  private LikePatterns() {}

  /**
   * A pattern's literal start — its text up to the first wildcard the escape does not quote, with
   * the escapes removed — and whether the pattern is exactly that start followed by one {@code %},
   * which is the only shape a range can answer on its own.
   */
  public record Prefix(String text, boolean bare) {}

  /** Why {@code escape} cannot be an escape, or null when it can. Null means no ESCAPE clause. */
  public static @Nullable String escapeDefect(@Nullable String escape) {
    if (escape == null || escape.codePointCount(0, escape.length()) == 1) {
      return null;
    }

    return escape.isEmpty()
        ? "a LIKE ESCAPE must be exactly one character, not the empty string"
        : "a LIKE ESCAPE must be exactly one character, not "
            + escape.codePointCount(0, escape.length())
            + " characters";
  }

  /**
   * Why {@code pattern} is malformed under {@code escape}, or null when it is well formed. The
   * escape is assumed to have passed {@link #escapeDefect}.
   */
  public static @Nullable String patternDefect(String pattern, @Nullable String escape) {
    if (escape == null) {
      return null;
    }

    int marker = escape.codePointAt(0);
    int position = 0;
    for (int i = 0; i < pattern.length(); ) {
      int codePoint = pattern.codePointAt(i);
      i += Character.charCount(codePoint);
      position++;
      if (codePoint != marker) {
        continue;
      }

      if (i >= pattern.length()) {
        return "a LIKE pattern ends with its escape character "
            + quoted(marker)
            + " at position "
            + position
            + "; an escape must be followed by '%', '_' or itself";
      }

      int next = pattern.codePointAt(i);
      if (next != '%' && next != '_' && next != marker) {
        return "a LIKE pattern has its escape character "
            + quoted(marker)
            + " before an ordinary character at position "
            + position
            + "; an escape must be followed by '%', '_' or itself";
      }

      i += Character.charCount(next);
      position++;
    }

    return null;
  }

  /** Throws when the escape or the pattern is malformed; see {@link #patternDefect}. */
  public static void check(String pattern, @Nullable String escape) {
    String defect = escapeDefect(escape);
    if (defect == null) {
      defect = patternDefect(pattern, escape);
    }

    if (defect != null) {
      throw new InvalidArgumentException(ErrorCode.SQL_VALIDATION, defect);
    }
  }

  /** The literal start of a well-formed pattern, and whether the pattern is a bare prefix. */
  public static Prefix prefix(String pattern, @Nullable String escape) {
    int marker = escape == null ? -1 : escape.codePointAt(0);
    StringBuilder text = new StringBuilder();
    for (int i = 0; i < pattern.length(); ) {
      int codePoint = pattern.codePointAt(i);
      i += Character.charCount(codePoint);
      if (codePoint == marker && i < pattern.length()) {
        int quotedPoint = pattern.codePointAt(i);
        i += Character.charCount(quotedPoint);
        text.appendCodePoint(quotedPoint);
        continue;
      }

      if (codePoint == '%' || codePoint == '_') {
        // The start ends here. Bare when what ends it is the pattern's last character, a '%'.
        return new Prefix(text.toString(), codePoint == '%' && i == pattern.length());
      }

      text.appendCodePoint(codePoint);
    }

    // No wildcard at all: an equality, which a prefix range covers but does not decide.
    return new Prefix(text.toString(), false);
  }

  /**
   * The escape of a {@code LIKE} call: null for the two-operand form, the literal's text for the
   * three-operand one. Throws for an escape that is not a character literal, which is refused at
   * validation before a call ever reaches here; this is the backstop.
   */
  public static @Nullable String escapeOf(RexCall call) {
    if (call.getOperands().size() < 3) {
      return null;
    }

    RexNode escape = call.getOperands().get(2);
    String text = characterLiteral(escape);
    if (text == null) {
      throw new IllegalArgumentException("a LIKE ESCAPE must be a character literal");
    }

    return text;
  }

  /** The text of a character literal, or null for anything else (a NULL literal included). */
  public static @Nullable String characterLiteral(RexNode node) {
    if (node instanceof RexLiteral literal
        && literal.getType().getSqlTypeName().getFamily() == SqlTypeFamily.CHARACTER) {
      return literal.getValueAs(String.class);
    }

    return null;
  }

  /**
   * Checks a {@code LIKE} call whose escape and pattern are both known: the escape if it has one,
   * and the pattern when it is a literal. A parameter pattern is checked when it is bound.
   */
  public static void checkCall(RexCall call) {
    String escape = escapeOf(call);
    String defect = escapeDefect(escape);
    if (defect == null) {
      String pattern = characterLiteral(call.getOperands().get(1));
      defect = pattern == null ? null : patternDefect(pattern, escape);
    }

    if (defect != null) {
      throw new InvalidArgumentException(ErrorCode.SQL_VALIDATION, defect);
    }
  }

  private static String quoted(int codePoint) {
    return "'" + new String(Character.toChars(codePoint)) + "'";
  }
}
