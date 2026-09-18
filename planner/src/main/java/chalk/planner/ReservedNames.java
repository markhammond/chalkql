package chalk.planner;

/**
 * The names the planner keeps for itself (docs/design/16-entitlements.md §2, D223).
 *
 * <p>The rewrites the planner does are written as ordinary SQL and then recognised again on the parse
 * tree: the context marker a scalar becomes, the schema a context relation lives in, the guard a
 * strict function body is wrapped in. Each of those was once a plain name — {@code CTX}, {@code ctx},
 * {@code CHALK_STRICT_GUARD} — and a host that registered a function or a schema of that name would
 * have changed what the pass saw, silently and in the direction of disclosing more.
 *
 * <p>So every internal marker takes Calcite's own convention for a name no user wrote: the prefix
 * {@code $chalk$}. It is <b>unwritable as an unquoted identifier</b>, because {@code $} is not a
 * letter in Calcite's grammar and an identifier begins with a letter; the planner writes it quoted,
 * which the parser accepts, and {@link #check} refuses a <em>quoted</em> one in text the host wrote
 * before any rewrite touches it. Between the two, the only {@code $chalk$} name in any tree is one
 * the planner put there.
 *
 * <p>Matching a marker is by operator identity wherever the request's operator table resolves it to
 * Chalk's own instance — the strict guard's convertlet is that case — and by the reserved name
 * otherwise, which is what the context marker needs: it is in no operator table at all, so the parser
 * leaves it an unresolved function call and the name is all there is.
 */
public final class ReservedNames {
  /** The prefix every planner-internal name takes. */
  public static final String PREFIX = "$chalk$";

  /**
   * The scalar context marker, and the per-request schema its relations live in — one name for both,
   * because they are two spellings of the same thing and a reader should not have to learn two.
   */
  public static final String CTX = PREFIX + "ctx";

  /** The marker a strict function body's null guard is written as, until its convertlet runs. */
  public static final String STRICT_GUARD = PREFIX + "strict_guard";

  /**
   * The first segment of a chain scan's qualified name (F84, D265 §7): what tells the mechanism's
   * own occurrence of a table from the statement's, in a {@code TableScan}'s digest and in the plan
   * text. It names no schema and resolves nowhere —
   * {@link chalk.planner.entitlement.CorrelationRelOptTable} wears it and delegates everything else.
   */
  public static final String CORRELATION = PREFIX + "correlation";

  /**
   * The first segment of an entitled scan's qualified name where the table takes queries (F95): what
   * tells two occurrences of one table that disclose differently apart, in a {@code TableScan}'s
   * digest and in the plan text. It names no schema and resolves nowhere —
   * {@link chalk.planner.entitlement.EntitledRelOptTable} wears it, with the occurrence's own
   * fingerprint after a colon, and delegates everything else. A table the client scans needs none:
   * its {@code ChalkTableScan} carries the map in its own terms.
   */
  public static final String ENTITLED = PREFIX + "entitled";

  private ReservedNames() {}

  /**
   * Refused: a quoted identifier or function name beginning with {@link #PREFIX} in text the host
   * wrote.
   *
   * <p>Runs on the raw SQL, before any rewrite and before the parser, so that the name the host wrote
   * is the name the message quotes. The scan is the same four-state one {@code ContextSql} does —
   * string literal, quoted identifier, line comment, block comment — because a pattern over SQL text
   * would fire inside a string literal and refuse a perfectly good mask.
   *
   * @param sql the statement, a descriptor's expression, or a function body
   * @param what how the caller names the text, for the message
   * @throws ReservedNameException naming the identifier
   */
  public static void check(String sql, String what) {
    if (sql == null || sql.indexOf('"') < 0) {
      // No quoted identifier at all, so nothing here can be a reserved name: an unquoted one does not
      // lex.
      return;
    }

    int i = 0;
    int n = sql.length();
    while (i < n) {
      char c = sql.charAt(i);
      switch (c) {
        case '\'' -> i = skipQuoted(sql, i, '\'');
        case '"' -> {
          int end = skipQuoted(sql, i, '"');
          String name = sql.substring(i + 1, Math.max(i + 1, end - 1)).replace("\"\"", "\"");
          if (name.regionMatches(true, 0, PREFIX, 0, PREFIX.length())) {
            throw new ReservedNameException(
                "the identifier \"" + name + "\" in " + what + " begins with " + PREFIX + ", which"
                    + " the planner keeps for its own markers — the context marker, the per-request"
                    + " context schema and the strict-function guard"
                    + " (docs/design/16-entitlements.md §2, D223). No unquoted identifier can spell"
                    + " it, so a quoted one is refused rather than silently shadowing a rewrite."
                    + " Rename it.");
          }
          i = end;
        }
        case '-' -> i = i + 1 < n && sql.charAt(i + 1) == '-' ? skipLineComment(sql, i) : i + 1;
        case '/' -> i = i + 1 < n && sql.charAt(i + 1) == '*' ? skipBlockComment(sql, i) : i + 1;
        default -> i++;
      }
    }
  }

  /** Whether this name is one of the planner's own, however it was spelled. */
  public static boolean isReserved(String name) {
    return name != null && name.regionMatches(true, 0, PREFIX, 0, PREFIX.length());
  }

  /** The index just past a quoted run, doubled quote characters included. */
  private static int skipQuoted(String sql, int start, char quote) {
    int i = start + 1;
    while (i < sql.length()) {
      char c = sql.charAt(i);
      i++;
      if (c == quote) {
        if (i < sql.length() && sql.charAt(i) == quote) {
          i++;
        } else {
          return i;
        }
      }
    }
    return i;
  }

  private static int skipLineComment(String sql, int start) {
    int end = sql.indexOf('\n', start);
    return end < 0 ? sql.length() : end + 1;
  }

  private static int skipBlockComment(String sql, int start) {
    int end = sql.indexOf("*/", start + 2);
    return end < 0 ? sql.length() : end + 2;
  }

  /**
   * A host's text naming one of the planner's own markers. {@code INVALID_REQUEST} for a statement;
   * a descriptor's is turned into {@code InvalidCatalogException} by its caller, since a catalog's
   * mistakes are named by the field they are in.
   */
  public static final class ReservedNameException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    public ReservedNameException(String message) {
      super(message);
    }
  }
}
