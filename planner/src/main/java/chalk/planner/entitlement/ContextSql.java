package chalk.planner.entitlement;

import chalk.planner.ReservedNames;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code @ctx.<name>} → something the SQL parser accepts, token by token
 * (docs/design/16-entitlements.md §2).
 *
 * <p>Not a regular expression, and deliberately so: a pattern over SQL text would rewrite inside a
 * string literal, inside a quoted identifier and inside a comment, and a policy's mask is exactly
 * the kind of expression that holds a string. This is a scanner that knows those four states and
 * rewrites only in the fifth.
 *
 * <p>What a name becomes depends on the kind it was bound as, which is why the rewrite reads the
 * context rather than the text alone:
 *
 * <ul>
 *   <li>a <b>scalar</b> becomes {@code "$chalk$ctx"('<name>')}, a call the fold shuttle replaces
 *       with a typed literal before the validator ever sees it;
 *   <li>a <b>list</b> and a <b>relation</b> become the qualified identifier
 *       {@code "$chalk$ctx"."<name>"}, which parses both as a table in a {@code FROM} and as an
 *       expression inside an {@code IN} list — so the shuttle, not the parser, is what decides that
 *       a list in a {@code FROM} and a relation in an {@code IN} are mistakes, and can name them.
 * </ul>
 *
 * <p>Both markers are written <b>quoted</b>, and the name is one no unquoted identifier can spell
 * (D223): a host may register a function called {@code CTX} or a schema called {@code ctx} and the
 * fold is unaffected, because what it looks for is a name the grammar will not let anyone else
 * write. {@link ReservedNames#check} refuses a quoted one before any of this runs.
 * </ul>
 *
 * <p>An unbound name is refused here, naming what is bound: under prepare-time binding every name a
 * descriptor mentions has a value, and a typo would otherwise become "column not found" from the
 * validator.
 */
public final class ContextSql {
  /** The prefix, matched case-insensitively as the lexer matches unquoted identifiers. */
  private static final String PREFIX = "@ctx.";

  private ContextSql() {}

  /** Whether the text mentions the context at all — the cheap test that skips the scan. */
  public static boolean mentionsContext(String sql) {
    return indexOfIgnoreCase(sql, 0) >= 0;
  }

  /**
   * The text with every {@code @ctx.<name>} replaced by the scalar marker, whatever it is bound as —
   * the form the <b>registration</b> check folds, where there is no context to consult and any name
   * is accepted (§1, F42).
   */
  public static String rewriteForRegistration(String sql) {
    return rewrite(sql, null);
  }

  /** The text with every {@code @ctx.<name>} replaced by its bound form. */
  public static String rewrite(String sql, @Nullable BoundContext context) {
    if (!mentionsContext(sql)) {
      return sql;
    }

    StringBuilder out = new StringBuilder(sql.length() + 16);
    int i = 0;
    int n = sql.length();
    while (i < n) {
      char c = sql.charAt(i);
      switch (c) {
        case '\'' -> i = copyQuoted(sql, i, '\'', out);
        case '"' -> i = copyQuoted(sql, i, '"', out);
        case '-' -> {
          if (i + 1 < n && sql.charAt(i + 1) == '-') {
            i = copyLineComment(sql, i, out);
          } else {
            out.append(c);
            i++;
          }
        }
        case '/' -> {
          if (i + 1 < n && sql.charAt(i + 1) == '*') {
            i = copyBlockComment(sql, i, out);
          } else {
            out.append(c);
            i++;
          }
        }
        case '@' -> {
          if (startsWithIgnoreCase(sql, i, PREFIX)) {
            i = substitute(sql, i, context, out);
          } else {
            out.append(c);
            i++;
          }
        }
        default -> {
          out.append(c);
          i++;
        }
      }
    }
    return out.toString();
  }

  /** One {@code @ctx.<name>}; returns the index just past it. A null context accepts any name. */
  private static int substitute(
      String sql, int start, @Nullable BoundContext context, StringBuilder out) {
    int nameStart = start + PREFIX.length();
    int i = nameStart;
    while (i < sql.length() && isNameChar(sql.charAt(i))) {
      i++;
    }
    if (i == nameStart) {
      throw new IllegalArgumentException(
          "'@ctx.' at offset " + start + " names nothing. Write @ctx.<name> for a value the "
              + "execution context binds.");
    }

    String name = sql.substring(nameStart, i);
    if (context == null) {
      // Registration: the shape is the host's, per request, so any name is accepted and the fold
      // that follows decides from the syntax what to put in its place (RegistrationFold).
      out.append(marker()).append("('").append(name).append("')");
      return i;
    }
    if (context.scalars().containsKey(name)) {
      out.append(marker()).append("('").append(name).append("')");
    } else if (context.relations().containsKey(name)) {
      out.append(marker()).append(".\"").append(name).append('"');
    } else {
      throw new IllegalArgumentException(
          "'@ctx." + name + "' is not bound by this execution context. Bound: " + bound(context)
              + ".");
    }
    return i;
  }

  /** The reserved name, quoted — the only spelling of it the parser accepts (D223). */
  private static String marker() {
    return '"' + ReservedNames.CTX + '"';
  }

  private static String bound(BoundContext context) {
    if (context.isEmpty()) {
      return "nothing";
    }
    StringBuilder names = new StringBuilder();
    for (String name : context.scalars().keySet()) {
      names.append(names.isEmpty() ? "" : ", ").append(name).append(" (scalar)");
    }
    for (BoundContext.Relation relation : context.relations().values()) {
      names
          .append(names.isEmpty() ? "" : ", ")
          .append(relation.name())
          .append(relation.isList() ? " (list)" : " (relation)");
    }
    return names.toString();
  }

  private static boolean isNameChar(char c) {
    return Character.isLetterOrDigit(c) || c == '_';
  }

  /** Copies a quoted run, doubled quote characters included, and returns the index past it. */
  private static int copyQuoted(String sql, int start, char quote, StringBuilder out) {
    out.append(quote);
    int i = start + 1;
    while (i < sql.length()) {
      char c = sql.charAt(i);
      out.append(c);
      i++;
      if (c == quote) {
        if (i < sql.length() && sql.charAt(i) == quote) {
          out.append(quote);
          i++;
        } else {
          return i;
        }
      }
    }
    return i;
  }

  private static int copyLineComment(String sql, int start, StringBuilder out) {
    int end = sql.indexOf('\n', start);
    if (end < 0) {
      out.append(sql, start, sql.length());
      return sql.length();
    }
    out.append(sql, start, end + 1);
    return end + 1;
  }

  private static int copyBlockComment(String sql, int start, StringBuilder out) {
    int end = sql.indexOf("*/", start + 2);
    if (end < 0) {
      out.append(sql, start, sql.length());
      return sql.length();
    }
    out.append(sql, start, end + 2);
    return end + 2;
  }

  private static int indexOfIgnoreCase(String sql, int from) {
    for (int i = from; i + PREFIX.length() <= sql.length(); i++) {
      if (startsWithIgnoreCase(sql, i, PREFIX)) {
        return i;
      }
    }
    return -1;
  }

  private static boolean startsWithIgnoreCase(String sql, int at, String prefix) {
    return sql.regionMatches(true, at, prefix, 0, prefix.length());
  }
}
