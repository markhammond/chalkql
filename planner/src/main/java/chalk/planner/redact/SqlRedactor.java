package chalk.planner.redact;

import chalk.planner.plan.SqlConfigs;
import java.util.ArrayList;
import java.util.Collections;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Set;
import org.apache.calcite.sql.SqlBasicCall;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlJoin;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlLiteral;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlNodeList;
import org.apache.calcite.sql.SqlNumericLiteral;
import org.apache.calcite.sql.SqlOrderBy;
import org.apache.calcite.sql.SqlSelect;
import org.apache.calcite.sql.SqlWindow;
import org.apache.calcite.sql.SqlWriterConfig;
import org.apache.calcite.sql.dialect.CalciteSqlDialect;
import org.apache.calcite.sql.parser.SqlParser;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.sql.util.SqlBasicVisitor;
import org.apache.calcite.sql.util.SqlShuttle;
import org.apache.calcite.sql.validate.SqlConformanceEnum;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Every literal in a statement as a keyed pseudonym, so the text can be logged (D262, {@code
 * docs/design/37-redacted-sql.md}).
 *
 * <p>A {@link SqlShuttle} over the parsed tree replaces each {@link SqlLiteral} with a {@link
 * RedactedMarker}, and the tree is then unparsed twice through one fixed dialect — {@link
 * CalciteSqlDialect#DEFAULT}, so spacing, case and quoting are canonical and {@code WHERE x = 'a'}
 * and {@code where x='b'} share one structure:
 *
 * <ol>
 *   <li>the <b>structural form</b>, every literal printed as its type marker, whose SHA-256 is the
 *       structural hash and therefore the seed's material;
 *   <li>the <b>redacted form</b>, each literal printed as its pseudonym under that seed — or as
 *       itself, where the position is one this visitor keeps or the scope leaves it alone.
 * </ol>
 *
 * <p>The structural hash is taken over <em>every</em> literal whatever the scope and whatever is
 * kept: the structure of a statement is the statement with no values in it, so turning the scope
 * down or the structural positions off never moves a pseudonym a host has already logged.
 *
 * <p><b>Positions this visitor keeps</b> (under {@code keep_structural}, and each an exact numeric
 * literal or nothing at all): {@code LIMIT}/{@code FETCH}, {@code OFFSET}, the offset of a window
 * frame bound, and an ordinal in {@code GROUP BY} or {@code ORDER BY}. Each is a count of rows or a
 * reference to a select item; none is ever compared against a column, so none can carry a value out
 * of the data. ADR 0043 §2 records the reasoning position by position. The token fallback keeps
 * none of them, because without a tree there is no position to trust.
 */
public final class SqlRedactor {
  /** What one redaction produced. */
  public record Result(String redactedSql, byte[] structuralHash, boolean parsed) {}

  private SqlRedactor() {}

  /**
   * Redacts a parsed statement — the prepare-time path, over the tree the sidecar already holds.
   */
  public static Result redact(SqlNode statement, RedactionPolicy policy) {
    RedactedMarker.Mode mode = new RedactedMarker.Mode();
    Keep keep = Keep.of(statement, policy);
    List<RedactedMarker> markers = new ArrayList<>();
    SqlNode marked = statement.accept(new Marking(mode, keep, policy, markers));
    SqlNode tree = marked == null ? statement : marked;

    String structuralForm = unparse(tree);
    byte[] structuralHash = Pseudonyms.structuralHash(structuralForm);
    Pseudonyms pseudonyms = Pseudonyms.forStatement(structuralHash, policy);
    for (RedactedMarker marker : markers) {
      if (marker.isReplaced()) {
        marker.resolve(pseudonyms);
      }
    }

    mode.redacted();
    return new Result(unparse(tree), structuralHash, true);
  }

  /**
   * Redacts text that was never prepared: parses it with {@code conformance} and falls back to the
   * parser's own token stream when it does not parse, which is where a host most wants a loggable
   * form and is exactly where nothing may be kept.
   */
  public static Result redact(String sql, SqlConformanceEnum conformance, RedactionPolicy policy) {
    SqlNode parsed;
    try {
      parsed = SqlParser.create(sql, SqlConfigs.parser(conformance)).parseStmt();
    } catch (Exception | StackOverflowError unparseable) {
      return TokenRedactor.redact(sql, conformance, policy);
    }
    return redact(parsed, policy);
  }

  /** The seed for a statement whose structural hash is already known. */
  public static Pseudonyms pseudonyms(byte[] structuralHash, RedactionPolicy policy) {
    return Pseudonyms.forStatement(structuralHash, policy);
  }

  /**
   * One canonical line, through the fixed dialect. The same writer settings the generated SQL uses
   * ({@code SourceSql}): a redacted statement is a log line, and a form that differed by line
   * breaks between platforms would not be the stable structure §2 asks for.
   */
  private static String unparse(SqlNode node) {
    return chalk.planner.ir.SourceSql.singleLine(
        node.toSqlString(
                config ->
                    config
                        .withDialect(CalciteSqlDialect.DEFAULT)
                        .withLineFolding(SqlWriterConfig.LineFolding.WIDE)
                        .withFoldLength(Integer.MAX_VALUE)
                        .withIndentation(0)
                        .withClauseStartsLine(false)
                        .withSelectListItemsOnSeparateLines(false))
            .getSql());
  }

  // ---- the shuttle ----

  /** Replaces every literal with a marker, saying of each whether its value is shown. */
  private static final class Marking extends SqlShuttle {
    private final RedactedMarker.Mode mode;
    private final Keep keep;
    private final RedactionPolicy policy;
    private final List<RedactedMarker> markers;

    Marking(
        RedactedMarker.Mode mode,
        Keep keep,
        RedactionPolicy policy,
        List<RedactedMarker> markers) {
      this.mode = mode;
      this.keep = keep;
      this.policy = policy;
      this.markers = markers;
    }

    @Override
    public @Nullable SqlNode visit(SqlLiteral literal) {
      if (literal.getTypeName() == SqlTypeName.SYMBOL || keep.isSyntax(literal)) {
        // Not a literal in the sense this is about: a keyword the grammar happens to carry as one
        // — DISTINCT, a join type, a trim flag, a window's ROWS — and nothing a host ever wrote a
        // value into. Left exactly as it is, in both forms.
        return literal;
      }

      boolean inScope =
          policy.scope() == RedactionPolicy.Scope.ALL || isString(literal.getTypeName());
      boolean kept = policy.keepStructural() && keep.isKept(literal);
      RedactedMarker marker = new RedactedMarker(literal, mode, inScope && !kept);
      markers.add(marker);
      return marker;
    }
  }

  /** What {@code RedactionScope.STRINGS} covers: the character and binary string literals. */
  private static boolean isString(SqlTypeName type) {
    return SqlTypeName.CHAR_TYPES.contains(type) || SqlTypeName.BINARY_TYPES.contains(type);
  }

  // ---- the structural positions ----

  /**
   * The literals a redaction shows rather than replaces, and the ones that are syntax rather than
   * data at all — both by identity in the tree the shuttle is about to walk.
   */
  private static final class Keep {
    private final Set<SqlNode> kept = Collections.newSetFromMap(new IdentityHashMap<>());
    private final Set<SqlNode> syntax = Collections.newSetFromMap(new IdentityHashMap<>());

    static Keep of(SqlNode statement, RedactionPolicy policy) {
      Keep keep = new Keep();
      statement.accept(keep.new Walk(policy.keepStructural()));
      return keep;
    }

    boolean isKept(SqlNode node) {
      return kept.contains(node);
    }

    boolean isSyntax(SqlNode node) {
      return syntax.contains(node);
    }

    private void keepInteger(@Nullable SqlNode node) {
      if (isExactInteger(node)) {
        kept.add(node);
      }
    }

    private void keepOrdinals(@Nullable SqlNodeList list) {
      if (list == null) {
        return;
      }
      for (SqlNode entry : list) {
        keepInteger(unwrapOrdering(entry));
      }
    }

    /**
     * An {@code ORDER BY} item's own expression: {@code DESC}, {@code NULLS FIRST} and {@code NULLS
     * LAST} wrap it, and an ordinal underneath any of them is still an ordinal.
     */
    private static @Nullable SqlNode unwrapOrdering(@Nullable SqlNode node) {
      SqlNode current = node;
      while (current instanceof SqlBasicCall call
          && (current.getKind() == SqlKind.DESCENDING
              || current.getKind() == SqlKind.NULLS_FIRST
              || current.getKind() == SqlKind.NULLS_LAST)
          && call.operandCount() == 1) {
        current = call.operand(0);
      }
      return current;
    }

    /**
     * Exact and whole. A frame bound or a {@code LIMIT} is a count; anything else in one of those
     * positions — a {@code RANGE INTERVAL '1' HOUR PRECEDING}, say — is a quantity written in the
     * vocabulary of the data and is redacted like any other literal (ADR 0043 §2).
     */
    private static boolean isExactInteger(@Nullable SqlNode node) {
      return node instanceof SqlNumericLiteral numeric
          && numeric.isExact()
          && (numeric.getScale() == null || numeric.getScale() == 0);
    }

    /** Walks the tree once, before anything is replaced, marking the positions by identity. */
    private final class Walk extends SqlBasicVisitor<Void> {
      private final boolean keepStructural;

      Walk(boolean keepStructural) {
        this.keepStructural = keepStructural;
      }

      @Override
      public Void visit(SqlNodeList nodeList) {
        for (SqlNode node : nodeList) {
          if (node != null) {
            node.accept(this);
          }
        }
        return null;
      }

      @Override
      public Void visit(SqlCall call) {
        if (call instanceof SqlSelect select) {
          if (keepStructural) {
            keepInteger(select.getFetch());
            keepInteger(select.getOffset());
            keepOrdinals(select.getGroup());
            keepOrdinals(select.getOrderList());
          }
        } else if (call instanceof SqlOrderBy orderBy) {
          if (keepStructural) {
            keepInteger(orderBy.fetch);
            keepInteger(orderBy.offset);
            keepOrdinals(orderBy.orderList);
          }
        } else if (call instanceof SqlWindow window) {
          // ROWS-or-RANGE and the partial-window flag are booleans the grammar carries and the
          // unparser reads as keywords; they are syntax, not a value anyone wrote.
          syntax.add(window.operand(4));
          syntax.add(window.operand(7));
          if (keepStructural) {
            keepInteger(frameOffset(window.getLowerBound()));
            keepInteger(frameOffset(window.getUpperBound()));
          }
        } else if (call instanceof SqlJoin join) {
          // NATURAL, likewise: a boolean the grammar carries and the unparser reads as a keyword.
          syntax.add(join.operand(1));
        }

        return super.visit(call);
      }
    }
  }

  /** The count inside a {@code n PRECEDING} / {@code n FOLLOWING} bound, or null for a keyword one. */
  private static @Nullable SqlNode frameOffset(@Nullable SqlNode bound) {
    return bound instanceof SqlCall call
            && (call.getKind() == SqlKind.PRECEDING || call.getKind() == SqlKind.FOLLOWING)
            && call.operandCount() == 1
        ? call.operand(0)
        : null;
  }
}
