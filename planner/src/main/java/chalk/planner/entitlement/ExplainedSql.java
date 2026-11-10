package chalk.planner.entitlement;

import chalk.planner.plan.ChalkContextMembership;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import org.apache.calcite.avatica.util.Casing;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.rel2sql.SqlImplementor;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexSimplify;
import org.apache.calcite.rex.RexSubQuery;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlDialect;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlSpecialOperator;
import org.apache.calcite.sql.SqlWriter;
import org.apache.calcite.sql.util.SqlShuttle;
import org.apache.calcite.sql.parser.SqlAbstractParserImpl;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.parser.impl.SqlParserImpl;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The oracle's expressions as ChalkQL SQL over the names a host knows (D207, F286): a table's own
 * columns by name, a parent's or a path's as {@code table.column}, an unbound context value as
 * {@code @ctx.name}, and a row's reaching a parent it is entitled through as the {@code EXISTS}
 * that says so.
 *
 * <p>The expressions are the folded ones the pass enforces, unchanged: this only says them in the
 * host's terms rather than the plan's ({@code =($1, 'KESTREL')} is {@code MissionId = 'KESTREL'}).
 * A name is quoted where it must be to read as that name, as the policy compiler writes them. An
 * expression it cannot say is given as the plan's own text rather than not at all.
 */
final class ExplainedSql {
  private ExplainedSql() {}

  /**
   * How each position of the row a leaf's folded expressions read is said: an expression, and, for
   * a parent side's match marker, the {@code EXISTS} that its being non-null means.
   */
  record Row(
      List<SqlNode> fields,
      Map<Integer, String> reached,
      Map<Integer, RexNode> decided,
      @Nullable RexSimplify simplify) {

    Row(List<SqlNode> fields, Map<Integer, String> reached) {
      this(fields, reached, Map.of(), null);
    }
  }

  /** A stand-in identifier for text spliced in after unparsing; no unquoted name can spell it. */
  private static final String SPLICE = chalk.planner.ReservedNames.PREFIX + "text";

  private static final Pattern SPLICED =
      Pattern.compile(Pattern.quote("\"" + SPLICE + "\".\"") + "(\\d+)\"");

  private static final Pattern PLAIN = Pattern.compile("[A-Za-z_][A-Za-z0-9_]*");

  private static final SqlAbstractParserImpl.Metadata RESERVED =
      SqlParserImpl.FACTORY.getParser(new java.io.StringReader("")).getMetadata();

  /** Double quotes, names kept as written, so only the names that need it are quoted. */
  private static final SqlDialect DIALECT =
      new SqlDialect(
          SqlDialect.EMPTY_CONTEXT
              .withIdentifierQuoteString("\"")
              .withUnquotedCasing(Casing.UNCHANGED)
              .withQuotedCasing(Casing.UNCHANGED)
              .withCaseSensitive(false));

  /** {@code parts} as one identifier, each part quoted where it would not otherwise read as itself. */
  static SqlIdentifier name(String... parts) {
    List<SqlParserPos> positions = new ArrayList<>(parts.length);
    for (String part : parts) {
      positions.add(needsQuote(part) ? SqlParserPos.QUOTED_ZERO : SqlParserPos.ZERO);
    }
    // A one-part name is unparsed as a nullary function such as USER unless the whole identifier
    // is marked quoted, whatever its part says.
    SqlParserPos whole =
        parts.length == 1 && needsQuote(parts[0]) ? SqlParserPos.QUOTED_ZERO : SqlParserPos.ZERO;
    return new SqlIdentifier(List.of(parts), null, whole, positions);
  }

  /** {@code parts} as the text of one identifier, by the same rule. */
  static String text(String... parts) {
    StringBuilder text = new StringBuilder();
    for (String part : parts) {
      text.append(text.isEmpty() ? "" : ".")
          .append(needsQuote(part) ? "\"" + part.replace("\"", "\"\"") + "\"" : part);
    }
    return text.toString();
  }

  private static boolean needsQuote(String part) {
    return !PLAIN.matcher(part).matches() || RESERVED.isReservedWord(part.toUpperCase(Locale.ROOT));
  }

  /** The row of one table's own columns, each as {@code table.column}. */
  static Row qualified(String table, List<String> columns) {
    List<SqlNode> fields = new ArrayList<>(columns.size());
    for (String column : columns) {
      fields.add(name(table, column));
    }
    return new Row(fields, Map.of());
  }

  /** {@code rex} as SQL over {@code row}; empty for none, and the plan's own text where it cannot. */
  static String render(@Nullable RexNode rex, Row row) {
    if (rex == null) {
      return "";
    }
    try {
      List<String> spliced = new ArrayList<>();
      String sql = unparse(node(rex, row, spliced));
      Matcher matcher = SPLICED.matcher(sql);
      StringBuilder text = new StringBuilder();
      while (matcher.find()) {
        matcher.appendReplacement(
            text, Matcher.quoteReplacement(spliced.get(Integer.parseInt(matcher.group(1)))));
      }
      return matcher.appendTail(text).toString();
    } catch (RuntimeException e) {
      return rex.toString();
    }
  }

  /** {@code rex} as a SQL tree over {@code row}, for nesting in another; null where it cannot. */
  static @Nullable SqlNode tree(RexNode rex, Row row) {
    try {
      List<String> spliced = new ArrayList<>();
      SqlNode node = node(rex, row, spliced);
      return spliced.isEmpty() ? node : null;
    } catch (RuntimeException e) {
      return null;
    }
  }

  /** Where spliced text is numbered from: past any position a row this wide can hold. */
  private static final int SPLICED_FROM = 1 << 24;

  private static SqlNode node(RexNode rex, Row row, List<String> spliced) {
    int width = row.fields().size();
    // The verdicts are put in place once, here; a key rendered on its own inside is past that.
    Row placed = new Row(row.fields(), row.reached());
    RexNode said =
        decided(rex, row).accept(
            new RexShuttle() {
              @Override
              public RexNode visitCall(RexCall call) {
                // First match is IS TRUE throughout, and says nothing a reader needs.
                if (call.getKind() == SqlKind.IS_TRUE) {
                  return call.getOperands().get(0).accept(this);
                }
                if (call.getKind() == SqlKind.IS_NOT_NULL
                    && call.getOperands().get(0) instanceof RexInputRef ref
                    && row.reached().containsKey(ref.getIndex())) {
                  return splice(call, row.reached().get(ref.getIndex()));
                }
                if (ChalkContextMembership.is(call)) {
                  List<String> keys = new ArrayList<>();
                  for (RexNode key : call.getOperands().subList(1, call.getOperands().size())) {
                    keys.add(render(key, placed));
                  }
                  return splice(call, membership(keys, ChalkContextMembership.list(call)));
                }
                if (chalk.planner.ReservedNames.CTX.equalsIgnoreCase(call.getOperator().getName())
                    && call.getOperands().size() == 1
                    && call.getOperands().get(0) instanceof RexLiteral literal) {
                  return splice(call, "@ctx." + literal.getValueAs(String.class));
                }
                return super.visitCall(call);
              }

              @Override
              public RexNode visitSubQuery(RexSubQuery subQuery) {
                String list = contextList(subQuery.rel);
                if (subQuery.getKind() == SqlKind.IN && list != null) {
                  List<String> keys = new ArrayList<>();
                  for (RexNode key : subQuery.getOperands()) {
                    keys.add(render(key, placed));
                  }
                  return splice(subQuery, membership(keys, list));
                }
                return super.visitSubQuery(subQuery);
              }

              private RexNode splice(RexNode replaced, String text) {
                spliced.add(text);
                return new RexInputRef(SPLICED_FROM + spliced.size() - 1, replaced.getType());
              }
            });

    SqlImplementor.SimpleContext context =
        new SqlImplementor.SimpleContext(
            DIALECT,
            i ->
                i >= SPLICED_FROM
                    ? new SqlIdentifier(
                        List.of(SPLICE, Integer.toString(i - SPLICED_FROM)),
                        null,
                        SqlParserPos.ZERO,
                        List.of(SqlParserPos.QUOTED_ZERO, SqlParserPos.QUOTED_ZERO))
                    : i < width ? row.fields().get(i) : name("$" + i));
    return context.toSql(null, said);
  }

  /**
   * The expression with each verdict a parent decided put in its place, over the parent's own
   * columns, and simplified: a child reads a verdict as a code, as in {@code COALESCE(CASE WHEN
   * activities.site_id = 1 THEN 1 ELSE 2 END, 2) = 1}, which says no more than {@code
   * activities.site_id = 1}. Read as the pass reads a condition, an unknown as false. Where no
   * verdict is read, the expression is as it was.
   */
  private static RexNode decided(RexNode rex, Row row) {
    if (row.decided().isEmpty() || row.simplify() == null) {
      return rex;
    }
    RexBuilder builder = row.simplify().rexBuilder;
    boolean[] read = {false};
    RexNode substituted =
        rex.accept(
            new RexShuttle() {
              @Override
              public RexNode visitInputRef(RexInputRef ref) {
                RexNode verdict = row.decided().get(ref.getIndex());
                if (verdict == null) {
                  return ref;
                }
                read[0] = true;
                return builder.ensureType(ref.getType(), verdict, true);
              }
            });
    if (!read[0]) {
      return rex;
    }
    if (substituted.getType().getSqlTypeName() != org.apache.calcite.sql.type.SqlTypeName.BOOLEAN) {
      return row.simplify().simplifyPreservingType(substituted);
    }
    RexNode simplified = row.simplify().simplifyUnknownAsFalse(substituted);
    return row.simplify().simplifyUnknownAsFalse(intoBranches(simplified, builder));
  }

  /**
   * Each comparison of a {@code CASE} with a literal, put into the branches: {@code CASE WHEN c1
   * THEN 1 WHEN c2 THEN 2 ELSE 3 END = 2} is {@code CASE WHEN c1 THEN FALSE WHEN c2 THEN TRUE ELSE
   * FALSE END}, which the simplifier then says as the conditions it comes to. That is the shape a
   * verdict decided as the number of the first rule that holds takes once it is in place.
   */
  private static RexNode intoBranches(RexNode rex, RexBuilder builder) {
    return rex.accept(
        new RexShuttle() {
          @Override
          public RexNode visitCall(RexCall call) {
            RexCall visited = (RexCall) super.visitCall(call);
            if (!visited.isA(SqlKind.COMPARISON) || visited.getOperands().size() != 2) {
              return visited;
            }
            RexNode left = visited.getOperands().get(0);
            RexNode right = visited.getOperands().get(1);
            if (left.isA(SqlKind.CASE) && right instanceof RexLiteral) {
              return branches((RexCall) left, right, visited, true);
            }
            if (right.isA(SqlKind.CASE) && left instanceof RexLiteral) {
              return branches((RexCall) right, left, visited, false);
            }
            return visited;
          }

          private RexNode branches(RexCall cases, RexNode literal, RexCall comparison, boolean caseFirst) {
            List<RexNode> operands = new ArrayList<>(cases.getOperands().size());
            List<RexNode> given = cases.getOperands();
            for (int i = 0; i < given.size(); i++) {
              boolean value = i % 2 == 1 || i == given.size() - 1;
              if (!value) {
                operands.add(given.get(i));
                continue;
              }
              operands.add(
                  builder.makeCall(
                      comparison.getOperator(),
                      caseFirst ? given.get(i) : literal,
                      caseFirst ? literal : given.get(i)));
            }
            return builder.makeCall(org.apache.calcite.sql.fun.SqlStdOperatorTable.CASE, operands);
          }
        });
  }

  /**
   * Writes a conjunction under a disjunction in parentheses: {@code (a AND b) OR (c AND d)} rather
   * than {@code a AND b OR c AND d}, which reads the same to the parser and not to every reader. A
   * disjunction under a conjunction is parenthesised already, by precedence.
   */
  private static final SqlSpecialOperator GROUPED =
      new SqlSpecialOperator("GROUPED", SqlKind.OTHER, 100) {
        @Override
        public void unparse(SqlWriter writer, SqlCall call, int leftPrec, int rightPrec) {
          SqlWriter.Frame frame = writer.startList("(", ")");
          call.operand(0).unparse(writer, 0, 0);
          writer.endList(frame);
        }
      };

  private static final SqlShuttle GROUPING =
      new SqlShuttle() {
        @Override
        public @Nullable SqlNode visit(SqlCall call) {
          SqlNode visited = super.visit(call);
          if (!(visited instanceof SqlCall or) || or.getKind() != SqlKind.OR) {
            return visited;
          }
          List<SqlNode> operands = new ArrayList<>(or.operandCount());
          for (SqlNode operand : or.getOperandList()) {
            operands.add(
                operand != null && operand.getKind() == SqlKind.AND
                    ? GROUPED.createCall(SqlParserPos.ZERO, operand)
                    : operand);
          }
          return or.getOperator().createCall(SqlParserPos.ZERO, operands);
        }
      };

  private static String unparse(SqlNode node) {
    SqlNode grouped = node.accept(GROUPING);
    return (grouped == null ? node : grouped).toSqlString(
            c ->
                c.withDialect(DIALECT)
                    .withQuoteAllIdentifiers(false)
                    .withAlwaysUseParentheses(false)
                    .withClauseStartsLine(false)
                    .withClauseEndsLine(false)
                    .withIndentation(0))
        .getSql();
  }

  private static String membership(List<String> keys, String list) {
    String left = keys.size() == 1 ? keys.get(0) : "(" + String.join(", ", keys) + ")";
    return left + " IN (@ctx." + list + ")";
  }

  /** The context list a sub-query reads, where it reads one and nothing else; else null. */
  private static @Nullable String contextList(RelNode rel) {
    if (rel instanceof TableScan scan) {
      List<String> names = scan.getTable().getQualifiedName();
      return names.size() == 2 && BoundContext.SCHEMA.equals(names.get(0)) ? names.get(1) : null;
    }
    return rel.getInputs().size() == 1 ? contextList(rel.getInputs().get(0)) : null;
  }
}
