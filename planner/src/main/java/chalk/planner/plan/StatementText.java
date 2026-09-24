package chalk.planner.plan;

import chalk.planner.catalog.UserFunctions;
import chalk.planner.ir.SqlBodyInliner;
import java.util.EnumSet;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;
import java.util.function.Consumer;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlNodeList;
import org.apache.calcite.sql.parser.SqlParseException;
import org.apache.calcite.sql.parser.SqlParser;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The statement's own words for a node it wrote, recovered from the parser's positions (D296), so a
 * refusal names what the statement said rather than the validated form with its expansions and
 * coercion casts: {@code price_move("open", "close")} and not {@code
 * `main`.`price_move`(CAST(`bars`.`open` AS DOUBLE), …)}.
 *
 * <p>Every node Calcite's parser builds carries its start and end line and column, and the text
 * between them is what the statement wrote for it. A node of the validated statement keeps the
 * position of the node it was copied or rewritten from — the expander's qualified identifiers and
 * copied calls, a {@code COALESCE} rewritten into a {@code CASE} — so its text is still there. Three
 * kinds of node carry a position that is not a span of the statement, and a quote is never taken
 * from one:
 *
 * <ul>
 *   <li>a node something synthesised has no position ({@link SqlParserPos#ZERO}), and a call built
 *       over one starts at line 0;
 *   <li>a node of a SQL body the inliner substituted was parsed from the body's own text, whose
 *       positions index that text;
 *   <li>a call the inliner rebuilt around a substituted body has, as its position, the union of
 *       the statement's and the body's — a span of neither.
 * </ul>
 *
 * <p>So a node is quoted only when the statement's own parse gave a node exactly its position, of a
 * kind it could have become, and nothing under it carries a position a substitutable body gave any
 * node. Anywhere else the node prints as Calcite prints it, as every refusal did before: the quote
 * is never wrong, only sometimes the verbose form.
 *
 * <p>Nothing is computed until a refusal asks for a quote. The statement is then parsed again from
 * its text with the pipeline's own parser configuration, which gives the tree the validator never
 * touched and the positions the first parse gave, and only on the path that is about to throw.
 */
public final class StatementText {
  /** The longest quote a refusal carries; a longer span is cut and marked. */
  static final int LONGEST = 160;

  /** No statement text: every quote is the node as Calcite prints it. */
  public static final StatementText NONE = new StatementText(null, null, null);

  private final @Nullable String sql;
  private final SqlParser.@Nullable Config parserConfig;
  private final @Nullable UserFunctions functions;

  /** The statement's own nodes' positions and kinds, once a refusal has asked; null until then. */
  private @Nullable Map<SqlParserPos, Set<SqlKind>> statement;

  /** Every position a substitutable SQL body's nodes carry, once a refusal has asked. */
  private @Nullable Set<SqlParserPos> bodies;

  private StatementText(
      @Nullable String sql,
      SqlParser.@Nullable Config parserConfig,
      @Nullable UserFunctions functions) {
    this.sql = sql;
    this.parserConfig = parserConfig;
    this.functions = functions;
  }

  /**
   * The text of {@code sql}, as the pipeline parsed it with {@code parserConfig}, with {@code
   * functions}' SQL bodies the ones that may have been substituted into it.
   */
  public static StatementText of(
      String sql, SqlParser.Config parserConfig, UserFunctions functions) {
    return new StatementText(sql, parserConfig, functions);
  }

  /**
   * What the statement wrote for {@code node}, trimmed and bounded, or {@code node} as Calcite
   * prints it when no span of the statement can be recovered for it.
   */
  public String quote(SqlNode node) {
    String span = span(node);
    return span != null ? span : node.toString();
  }

  /** The statement's text for {@code node}, or null when its position is not a span of it. */
  @Nullable String span(SqlNode node) {
    SqlParserPos pos = node.getParserPosition();
    if (sql == null || pos == null || pos.getLineNum() < 1 || pos.getColumnNum() < 1) {
      return null;
    }
    load();
    Set<SqlKind> kinds = java.util.Objects.requireNonNull(statement).get(pos);
    if (kinds == null || !compatible(node.getKind(), kinds) || fromBody(node)) {
      return null;
    }
    int start = index(sql, pos.getLineNum(), pos.getColumnNum());
    int end = index(sql, pos.getEndLineNum(), pos.getEndColumnNum());
    if (start < 0 || end < start) {
      return null;
    }
    return bounded(sql.substring(start, end + 1));
  }

  /**
   * Whether a validated node of {@code kind} could have come from a parsed one of {@code kinds} at
   * the same position. A named call is parsed unresolved and resolved or rewritten by the
   * validator — {@code COALESCE} and {@code NULLIF} into a {@code CASE}, {@code MAX} into the
   * aggregate — so an unresolved call's position may hold a node of any kind.
   */
  private static boolean compatible(SqlKind kind, Set<SqlKind> kinds) {
    return kinds.contains(kind)
        || kinds.contains(SqlKind.OTHER_FUNCTION)
        || (kind == SqlKind.CASE
            && (kinds.contains(SqlKind.COALESCE) || kinds.contains(SqlKind.NULLIF)));
  }

  /** Whether {@code node}, or anything under it, carries a position a substituted body gave. */
  private boolean fromBody(SqlNode node) {
    Set<SqlParserPos> known = java.util.Objects.requireNonNull(bodies);
    if (known.isEmpty()) {
      return false;
    }
    boolean[] found = {false};
    walk(node, child -> found[0] |= known.contains(child.getParserPosition()));
    return found[0];
  }

  private void load() {
    if (statement != null) {
      return;
    }
    Map<SqlParserPos, Set<SqlKind>> parsed = new HashMap<>();
    Set<SqlParserPos> substitutable = new HashSet<>();
    try {
      walk(
          SqlParser.create(java.util.Objects.requireNonNull(sql), parserConfig).parseStmt(),
          node ->
              parsed
                  .computeIfAbsent(node.getParserPosition(), p -> EnumSet.noneOf(SqlKind.class))
                  .add(node.getKind()));
      if (functions != null && !functions.isEmpty()) {
        for (SqlNode body : SqlBodyInliner.substitutableBodies(functions)) {
          walk(body, node -> substitutable.add(node.getParserPosition()));
        }
      }
    } catch (SqlParseException | RuntimeException unparsable) {
      // A statement that got as far as a refusal has parsed once already, and a body registration
      // accepted parses too; if either somehow does not, no position can be relied on.
      parsed.clear();
    }
    parsed.remove(SqlParserPos.ZERO);
    substitutable.remove(SqlParserPos.ZERO);
    for (SqlParserPos pos : substitutable) {
      parsed.remove(pos);
    }
    statement = parsed;
    bodies = substitutable;
  }

  /** {@code node} and every node under it, parents first. */
  private static void walk(@Nullable SqlNode node, Consumer<SqlNode> visitor) {
    if (node == null) {
      return;
    }
    visitor.accept(node);
    if (node instanceof SqlNodeList list) {
      for (SqlNode child : list) {
        walk(child, visitor);
      }
    } else if (node instanceof SqlCall call) {
      for (SqlNode child : call.getOperandList()) {
        walk(child, visitor);
      }
    }
  }

  /**
   * The zero-based index into {@code text} of a one-based line and column, counting lines as the
   * parser does — {@code \r\n}, {@code \r} and {@code \n} each end one — and every character, a tab
   * included, as one column, which is the tab size Calcite gives its parser. -1 past the end.
   */
  static int index(String text, int line, int column) {
    if (line < 1 || column < 1) {
      return -1;
    }
    int i = 0;
    int current = 1;
    while (current < line) {
      if (i >= text.length()) {
        return -1;
      }
      char c = text.charAt(i++);
      if (c == '\r') {
        if (i < text.length() && text.charAt(i) == '\n') {
          i++;
        }
        current++;
      } else if (c == '\n') {
        current++;
      }
    }
    int index = i + column - 1;
    return index < text.length() ? index : -1;
  }

  /**
   * A span as a refusal prints it: trimmed, each line break with the space around it made one
   * space, and cut at {@link #LONGEST} characters with the cut marked.
   */
  static String bounded(String span) {
    String flat = span.strip().replaceAll("\\s*(\\r\\n|\\r|\\n)\\s*", " ");
    return flat.length() <= LONGEST ? flat : flat.substring(0, LONGEST - 1) + "…";
  }
}
