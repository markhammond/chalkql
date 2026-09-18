package chalk.planner.ir;

import chalk.ir.v1.DialectProfile;
import chalk.planner.plan.SourceConvention;
import chalk.planner.plan.SourceDialects;
import chalk.planner.plan.rel.ProjectedRelOptTable;
import chalk.planner.plan.rel.SourceScan;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.rel2sql.RelToSqlConverter;
import org.apache.calcite.rel.rel2sql.SqlImplementor;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlDialect;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlJoin;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlLiteral;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlNodeList;
import org.apache.calcite.sql.SqlOperator;
import org.apache.calcite.sql.SqlSelect;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.SqlWriterConfig;
import org.apache.calcite.sql.type.SqlTypeUtil;
import org.apache.calcite.sql.util.SqlShuttle;

/**
 * Generated SQL for a pushed subtree (§2). Calcite's {@code RelToSqlConverter} does the work; the
 * only thing Chalk adds is the table name, because a {@code SourceScan}'s qualified name is Chalk's
 * schema name for the source, not the name the source knows the table by.
 *
 * <p>Two facts about the converter that the JDBC-adapter template does not make obvious and that
 * matter here (recorded in ADR 0020):
 *
 * <ul>
 *   <li><b>Dynamic parameters.</b> {@code SqlImplementor} converts a {@code RexDynamicParam} into a
 *       {@code SqlDynamicParam}, which every dialect unparses as {@code ?}. So a source that takes
 *       parameters gets positional {@code ?} placeholders in the order the plan's parameters are
 *       numbered, and one that does not never sees a {@code RexDynamicParam} at all, because
 *       {@link chalk.planner.plan.PushdownGate} refuses to push an expression containing one.
 *   <li><b>Casts and quoting.</b> The converter emits a cast wherever the rel tree has a {@code
 *       CAST}, spelled through {@code SqlDialect.getCastSpec}, and quotes every identifier with the
 *       dialect's quote string. Both therefore follow the profile, which is what {@link
 *       SourceDialects} exists to arrange.
 * </ul>
 */
public final class SourceSql {
  private SourceSql() {}

  /** The SQL for {@code pushed}, in its source's dialect. */
  public static String generate(RelNode pushed, SourceConvention convention) {
    DialectProfile profile = convention.profile();
    SqlDialect dialect = SourceDialects.of(profile);
    SqlImplementor.Result result =
        new SourceRelToSqlConverter(dialect)
            .visitRoot(SourceRelToSqlConverter.forDialect(pushed, dialect));
    // Never a star (F35): the source would answer with its physical columns in its own order.
    SqlNode node = SelectStar.named(result.asStatement(), pushed);

    return singleLine(
        node.toSqlString(
                config ->
                    config
                        .withDialect(dialect)
                        .withLineFolding(SqlWriterConfig.LineFolding.WIDE)
                        .withFoldLength(Integer.MAX_VALUE)
                        .withIndentation(0)
                        .withClauseStartsLine(false)
                        .withSelectListItemsOnSeparateLines(false))
            .getSql());
  }

  /**
   * The query text on one line: a wire value and a golden file, not something a person reads in a
   * terminal, and a golden that differed by line breaks between platforms would be a false failure.
   *
   * <p>Written as a scanner rather than a regular expression because a regular expression over the
   * finished text would also rewrite the inside of a string literal — turning
   * {@code WHERE name = 'John\n Smith'} into a different predicate, which is a wrong-answer bug
   * rather than a formatting one. Quoted identifiers are skipped for the same reason.
   */
  public static String singleLine(String sql) {
    StringBuilder out = new StringBuilder(sql.length());
    boolean pendingSpace = false;
    for (int i = 0; i < sql.length(); i++) {
      char c = sql.charAt(i);
      if (c == '\'' || c == '"' || c == '`') {
        if (pendingSpace) {
          out.append(' ');
          pendingSpace = false;
        }
        i = copyQuoted(sql, i, c, out);
        continue;
      }
      if (Character.isWhitespace(c)) {
        pendingSpace = out.length() > 0;
        continue;
      }
      if (pendingSpace) {
        out.append(' ');
        pendingSpace = false;
      }
      out.append(c);
    }
    return out.toString();
  }

  /**
   * Copies the quoted run starting at {@code start} verbatim, doubled quotes included, and returns
   * the index of its closing quote.
   */
  private static int copyQuoted(String sql, int start, char quote, StringBuilder out) {
    out.append(quote);
    int i = start + 1;
    while (i < sql.length()) {
      char c = sql.charAt(i);
      out.append(c);
      if (c == quote) {
        if (i + 1 < sql.length() && sql.charAt(i + 1) == quote) {
          out.append(quote);
          i += 2;
          continue;
        }
        return i;
      }
      i++;
    }
    return i - 1;
  }

  /**
   * The converter, with one override: a scan names the table the way the <em>source</em> does.
   * Chalk's schema name is how a query addresses the source from outside; inside the source's own
   * query it would be wrong, and often would not exist.
   */
  public static final class SourceRelToSqlConverter extends RelToSqlConverter {
    // Public, and so is its visit method: Calcite dispatches to it reflectively, and a public
    // method on a package-private class is not callable that way.
    public SourceRelToSqlConverter(SqlDialect dialect) {
      super(dialect);
    }

    /**
     * An aggregate as Calcite writes it, with one repair (F57): where the dialect answers an integer
     * {@code SUM} in a wider type than its argument — DuckDB's {@code HUGEINT}, PostgreSQL's
     * {@code BIGINT} and {@code NUMERIC} — the measure is cast back to the type the plan declares
     * for it, so the column the source returns is the column the scan contract reads. A
     * {@code SUM0} arrives here as {@code COALESCE(SUM(x), 0)} and is cast whole. Every other
     * dialect keeps its argument's type and is left as written.
     */
    @Override
    public Result visit(org.apache.calcite.rel.core.Aggregate e) {
      Result result = super.visit(e);
      if (!SourceDialects.widensIntegerSums(dialect)) {
        return result;
      }
      SqlSelect select = result.asSelect();
      SqlNodeList items = select.getSelectList();
      if (items == null) {
        return result;
      }
      int keys = e.getGroupCount();
      List<org.apache.calcite.rel.core.AggregateCall> calls = e.getAggCallList();
      for (int i = 0; i < calls.size(); i++) {
        org.apache.calcite.rel.core.AggregateCall call = calls.get(i);
        SqlKind kind = call.getAggregation().getKind();
        if ((kind != SqlKind.SUM && kind != SqlKind.SUM0) || !SqlTypeUtil.isIntType(call.getType())) {
          continue;
        }
        int index = keys + i;
        if (index >= items.size()) {
          continue;
        }
        SqlNode item = items.get(index);
        SqlNode alias = null;
        SqlNode measure = item;
        if (item.getKind() == SqlKind.AS) {
          alias = ((SqlCall) item).operand(1);
          measure = ((SqlCall) item).operand(0);
        }
        SqlNode cast =
            SqlStdOperatorTable.CAST.createCall(POS, measure, dialect.getCastSpec(call.getType()));
        items.set(index, alias == null ? cast : SqlStdOperatorTable.AS.createCall(POS, cast, alias));
      }
      return result;
    }

    @Override
    public Result visit(TableScan scan) {
      SqlIdentifier identifier =
          new SqlIdentifier(remoteName(scan.getTable()), SqlParserPos.ZERO);
      return result(identifier, ImmutableList.of(Clause.FROM), scan, null);
    }

    /**
     * The subtree this converter is given, with the dialect's own operator substituted wherever the
     * standard one would mean something else there (F27, ADR 0027).
     *
     * <p>One substitution today: a {@code DIVIDE} whose result type is an integer kind, on a
     * dialect whose {@code /} is real division. It is made here rather than in the dialect because
     * only the rel tree knows the type — a {@code SqlCall} carries none — and it is made on the way
     * in rather than by overriding a visit method because {@code SqlImplementor.visitRoot} is
     * final and every {@code Project}, {@code Filter} and {@code Join} would otherwise need its
     * own override.
     *
     * <p>The rewrite is total over the subtree: a division in a projection reaches the client as
     * the declared type, and one folded inside a predicate — which never reaches the client, and
     * whose wrong answer would therefore be silent — is evaluated the way Chalk evaluates it.
     */
    public static RelNode forDialect(RelNode pushed, SqlDialect dialect) {
      SqlOperator integerDivision = SourceDialects.integerDivision(dialect);
      return integerDivision == null ? pushed : substitute(pushed, integerDivision);
    }

    /**
     * The subtree with {@code shuttle} applied to every rel's expressions, bottom up. Calcite has
     * no "apply a {@code RexShuttle} to a whole tree" helper: {@code RelNode.accept(RexShuttle)}
     * rewrites one node's own expressions and leaves its inputs alone.
     */
    private static RelNode substitute(RelNode rel, SqlOperator integerDivision) {
      RelNode rewritten = rel;
      List<RelNode> inputs = rel.getInputs();
      if (!inputs.isEmpty()) {
        List<RelNode> substituted = new ArrayList<>(inputs.size());
        boolean changed = false;
        for (RelNode input : inputs) {
          RelNode next = substitute(input, integerDivision);
          changed |= next != input;
          substituted.add(next);
        }
        if (changed) {
          rewritten = rel.copy(rel.getTraitSet(), substituted);
        }
      }

      return rewritten.accept(
          new IntegerDivision(integerDivision, rel.getCluster().getRexBuilder()));
    }

    /**
     * Rewrites {@code a / b} into the dialect's integer-division operator wherever the call's
     * result type is an integer kind.
     *
     * <p>The new call carries the <em>same</em> type rather than inferring one, so nothing above it
     * — a cast, a comparison, the row type the client was promised — can move underneath the
     * substitution. Only {@link SqlStdOperatorTable#DIVIDE} is matched: {@code DIVIDE_INTEGER} is
     * Calcite's internal interval operator and shares the kind.
     */
    private static final class IntegerDivision extends RexShuttle {
      private final SqlOperator operator;
      private final RexBuilder rexBuilder;

      IntegerDivision(SqlOperator operator, RexBuilder rexBuilder) {
        this.operator = operator;
        this.rexBuilder = rexBuilder;
      }

      @Override
      public RexNode visitCall(RexCall call) {
        RexNode visited = super.visitCall(call);
        if (!(visited instanceof RexCall divide)
            || divide.getOperator() != SqlStdOperatorTable.DIVIDE
            || divide.getOperands().size() != 2
            || !SqlTypeUtil.isIntType(divide.getType())) {
          return visited;
        }

        return rexBuilder.makeCall(divide.getType(), operator, divide.getOperands());
      }
    }

    /**
     * A semi-join or an anti-join, which the converter writes as a correlated {@code EXISTS} over
     * the right side — Calcite's own rendering, with one repair: the reference back to the outer
     * row keeps a qualifier that names it (F72, F83; ADR 0059).
     *
     * <p>Calcite builds the join condition against the left side's qualified context, which spells
     * the outer row's columns {@code "t"."id"}, and then — because {@code "t"} is the alias of the
     * left <em>result</em> rather than of anything bound at that moment — replaces each such
     * reference with the left select list's own expression for that field. That expression is
     * written in the left select's scope, so its column references are <b>bare</b>:
     * {@code "t"."id"} becomes {@code "id"}, and {@code "t"."first_name"} becomes
     * {@code CAST("first_name" AS VARCHAR)}. A bare name inside the generated {@code EXISTS} is
     * resolved by the sub-query's own {@code FROM} first, so a right side that happens to expose a
     * column of the same name <b>captures</b> it: the membership test silently becomes a test the
     * outer row is not in, and the semi-join stops filtering. That is a wrong answer in the wide
     * direction, and the two findings this repairs are exactly it — an {@code IN (SELECT …)} over a
     * second occurrence of the entitled table whose row predicate folded to TRUE, and the
     * {@code IN}/{@code = ANY} spellings of a semi-join over a table that carries an {@code id}.
     *
     * <p>The repair is to keep the qualifier wherever one is bound. Three shapes, and the
     * difference between them is only whether the left's {@code FROM} already names itself:
     *
     * <ul>
     *   <li>The condition's qualifier is <b>already bound</b> by the left's {@code FROM} — a bare
     *       scan, whose reference reads {@code "members"."id"} — so nothing is substituted, which
     *       is what Calcite does too.
     *   <li>The left's {@code FROM} is a <b>single item</b>. The qualifier is bound onto it at the
     *       end of this method ({@code FROM "members" AS "t"}), so the substituted expression's
     *       bare column references are qualified with it and the sub-query can no longer capture
     *       them.
     *   <li>The left's {@code FROM} is a <b>join</b>. No qualifier is bound — Calcite does not
     *       alias a join — but a join's select list is written against its inputs' own aliases and
     *       is therefore already qualified, so the substitution alone is sound.
     * </ul>
     *
     * <p>Everything else is Calcite's method as it stands, reproduced rather than delegated to
     * because the substitution it performs is not reachable from a subclass.
     */
    @Override
    protected SqlImplementor.Result visitAntiOrSemiJoin(Join join) {
      SqlImplementor.Result left = visitInput(join, 0).resetAlias();
      SqlImplementor.Result right = visitInput(join, 1).resetAlias();
      SqlSelect select = left.asSelect();
      SqlNode condition =
          convertConditionToSqlNode(
              join.getCondition(), left.qualifiedContext(), right.qualifiedContext());

      SqlNode from = select.getFrom();
      String outer = outerQualifier(left, from);
      if (outer != null) {
        boolean binds = from != null && from.getKind() != SqlKind.JOIN;
        condition =
            condition.accept(
                new OuterRow(
                    outer,
                    join.getLeft().getRowType(),
                    select.getSelectList(),
                    binds ? outer : null));
      }

      SqlNode rightFrom = right.asFrom();
      SqlSelect exists;
      if (rightFrom.getKind() == SqlKind.SELECT) {
        exists = (SqlSelect) rightFrom;
        exists.setSelectList(new SqlNodeList(ImmutableList.of(one()), POS));
        if (exists.getWhere() != null) {
          condition = SqlStdOperatorTable.AND.createCall(POS, exists.getWhere(), condition);
        }
        exists.setWhere(condition);
      } else {
        exists =
            new SqlSelect(
                POS,
                null,
                new SqlNodeList(ImmutableList.of(one()), POS),
                rightFrom,
                condition,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
      }

      SqlNode test = SqlStdOperatorTable.EXISTS.createCall(POS, exists);
      if (join.getJoinType() == JoinRelType.ANTI) {
        test = SqlStdOperatorTable.NOT.createCall(POS, test);
      }
      if (select.getWhere() != null) {
        test = SqlStdOperatorTable.AND.createCall(POS, select.getWhere(), test);
      }
      select.setWhere(test);
      if (outer != null && from != null && from.getKind() != SqlKind.JOIN) {
        select.setFrom(as(from, outer));
      }
      return result(select, ImmutableList.of(SqlImplementor.Clause.FROM), join, null);
    }

    /** The {@code 1} a generated {@code EXISTS} selects. */
    private static SqlNode one() {
      return SqlLiteral.createExactNumeric("1", POS);
    }

    /**
     * The qualifier the join condition uses for the outer row, when it is one the left's
     * {@code FROM} does not already bind — and null when it is, because then the condition already
     * reads the way it must and there is nothing to substitute.
     */
    private static String outerQualifier(SqlImplementor.Result left, SqlNode from) {
      SqlNode field = left.qualifiedContext().field(0);
      if (!(field instanceof SqlIdentifier identifier) || identifier.names.size() != 2) {
        return null;
      }

      String qualifier = identifier.names.get(0);
      Set<String> bound = new HashSet<>();
      collectBound(from, bound);
      return bound.contains(qualifier) ? null : qualifier;
    }

    /** The names a {@code FROM} binds: an alias, a table's own name, or both sides of a join. */
    private static void collectBound(SqlNode from, Set<String> into) {
      if (from == null) {
        return;
      }
      if (from instanceof SqlJoin sqlJoin) {
        collectBound(sqlJoin.getLeft(), into);
        collectBound(sqlJoin.getRight(), into);
        return;
      }
      if (from.getKind() == SqlKind.AS) {
        SqlCall as = (SqlCall) from;
        if (as.operand(1) instanceof SqlIdentifier alias) {
          into.add(alias.getSimple());
        }
        return;
      }
      if (from instanceof SqlIdentifier identifier) {
        into.add(identifier.names.get(identifier.names.size() - 1));
      }
    }

    /**
     * The outer row's columns, substituted the way Calcite substitutes them and then qualified so
     * the generated sub-query cannot capture them.
     */
    private static final class OuterRow extends SqlShuttle {
      private final String qualifier;
      private final RelDataType leftType;
      private final SqlNodeList selectList;
      private final String binds;

      OuterRow(String qualifier, RelDataType leftType, SqlNodeList selectList, String binds) {
        this.qualifier = qualifier;
        this.leftType = leftType;
        this.selectList = selectList;
        this.binds = binds;
      }

      @Override
      public SqlNode visit(SqlIdentifier identifier) {
        if (identifier.names.size() != 2 || !qualifier.equals(identifier.names.get(0))) {
          return identifier;
        }
        // A star stands for the whole row and has no per-field expression to put in its place.
        if (selectList.get(0) instanceof SqlIdentifier first && first.isStar()) {
          return identifier;
        }

        RelDataTypeField field = leftType.getField(identifier.names.get(1), false, false);
        if (field == null || field.getIndex() >= selectList.size()) {
          return identifier;
        }

        SqlNode item = selectList.get(field.getIndex());
        if (item.getKind() == SqlKind.AS) {
          item = ((SqlCall) item).operand(0);
        }

        SqlNode substituted = item.clone(identifier.getParserPosition());
        return binds == null ? substituted : substituted.accept(new Qualify(binds));
      }
    }

    /**
     * Every bare column reference in one expression, qualified with the alias the expression's own
     * relation is bound by. A nested query is left alone: its names are resolved in its own scope.
     */
    private static final class Qualify extends SqlShuttle {
      private final String alias;

      Qualify(String alias) {
        this.alias = alias;
      }

      @Override
      public SqlNode visit(SqlIdentifier identifier) {
        if (identifier.names.size() != 1 || identifier.isStar()) {
          return identifier;
        }
        return new SqlIdentifier(
            ImmutableList.of(alias, identifier.names.get(0)), identifier.getParserPosition());
      }

      @Override
      public SqlNode visit(SqlCall call) {
        return call.getKind() == SqlKind.SELECT ? call : super.visit(call);
      }
    }

    /**
     * The name the source knows this table by: its own table name, unqualified. Chalk's schema is a
     * federation-level name (A4) and the source's catalog and schema — where it has them — belong to
     * the connection the adapter opened, not to the query text.
     */
    private static List<String> remoteName(RelOptTable table) {
      List<String> qualified = table.getQualifiedName();
      List<String> name = new ArrayList<>(1);
      name.add(qualified.get(qualified.size() - 1));
      return name;
    }
  }

  /** Whether this subtree reads only one table — the shape the corpus's simplest goldens have. */
  public static boolean isSingleTable(RelNode rel) {
    if (rel instanceof SourceScan) {
      return true;
    }
    if (rel.getInputs().size() != 1) {
      return false;
    }
    return isSingleTable(rel.getInput(0));
  }

  /** The pruned projection of the scan at the bottom, or null when there is more than one. */
  public static ProjectedRelOptTable projectionOf(RelNode rel) {
    if (rel instanceof SourceScan scan) {
      return scan.getTable().unwrap(ProjectedRelOptTable.class);
    }
    for (RelNode input : rel.getInputs()) {
      ProjectedRelOptTable found = projectionOf(input);
      if (found != null) {
        return found;
      }
    }
    return null;
  }
}
