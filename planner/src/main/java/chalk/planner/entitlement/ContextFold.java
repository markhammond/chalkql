package chalk.planner.entitlement;

import chalk.ir.v1.Expr;
import chalk.ir.v1.Field;
import chalk.ir.v1.VirtualRow;
import java.util.ArrayList;
import java.util.Collections;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Set;
import org.apache.calcite.sql.SqlBasicCall;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlJoin;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlLiteral;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlNodeList;
import org.apache.calcite.sql.SqlOrderBy;
import org.apache.calcite.sql.SqlSelect;
import org.apache.calcite.sql.SqlWith;
import org.apache.calcite.sql.SqlWithItem;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.parser.SqlParseException;
import org.apache.calcite.sql.parser.SqlParser;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.util.SqlShuttle;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The fold: substitution over the parse tree, and then Calcite's own reduction
 * (docs/design/16-entitlements.md §2, D197).
 *
 * <p>There is no expression unroller here and no regular expression anywhere. A scalar becomes a
 * typed literal, a small list becomes a literal {@code IN} list — which Calcite's converter turns
 * into an OR chain or a {@code SEARCH} natively, because the parser configuration already keeps
 * every literal list out of the sub-query path (D29) — a composite list becomes a list of row
 * constructors, which the same conversion turns into an OR of ANDs, and a list too large to fold
 * becomes a sub-query over a {@code ContextTable}, which decorrelates into a semi-join like any
 * other.
 *
 * <p>The empty list is the case worth naming: {@code x IN ()} is not SQL, so the membership test
 * itself is replaced by {@code FALSE} and its negation by {@code TRUE}, which is what an empty set
 * means under three-valued logic and what a principal with no grants must get.
 *
 * <p>For a name bound as a <b>shape</b> (D209, D232) there is nothing to substitute: a scalar becomes
 * {@code CAST(? AS <its declared type>)} — the cast is what gives the validator a type to infer,
 * since a bare placeholder inside a mask has nothing to take one from — and its list stays a
 * relation whatever the fold ceiling says, because its rows are not here to be counted. The
 * placeholder is turned into a {@link BoundParam} after conversion, by the caller, which is where a
 * per-statement index becomes the request-wide slot.
 *
 * <p>The question is asked per <em>name</em> and never of the context as a whole, which is all that
 * partial binding needs here: one descriptor's rules may hold a folded membership beside a shape's,
 * and each is what its own binding made it.
 */
public final class ContextFold {
  private ContextFold() {}

  /**
   * A folded statement, and — under a shape-only context — the scalar each placeholder stands for,
   * by the index the folded SQL gave it.
   */
  public record Folded(SqlNode node, List<String> boundScalars) {}

  /**
   * Folds the context into a parsed statement.
   *
   * @param parsed the statement, after {@link ContextSql#rewrite}
   * @param context the request's bindings
   * @param parserConfig the request's parser configuration, for the sub-selects an unfolded list
   *     needs
   * @param typeFactory the request's type factory, for the declared type a shape's placeholder casts
   *     to
   */
  public static Folded fold(
      SqlNode parsed,
      BoundContext context,
      SqlParser.Config parserConfig,
      org.apache.calcite.rel.type.RelDataTypeFactory typeFactory) {
    Set<SqlNode> fromPositions = Collections.newSetFromMap(new IdentityHashMap<>());
    collectFromPositions(parsed, false, fromPositions);
    Shuttle shuttle = new Shuttle(context, parserConfig, fromPositions, typeFactory);
    SqlNode folded = parsed.accept(shuttle);
    return new Folded(folded == null ? parsed : folded, List.copyOf(shuttle.boundScalars));
  }

  /** The shuttle. One instance per statement; it holds nothing but the bindings. */
  private static final class Shuttle extends SqlShuttle {
    private final BoundContext context;
    private final SqlParser.Config parserConfig;
    private final Set<SqlNode> fromPositions;
    private final org.apache.calcite.rel.type.RelDataTypeFactory typeFactory;

    /** The scalar each placeholder this fold wrote stands for, by its index in the folded SQL. */
    private final List<String> boundScalars = new ArrayList<>();

    Shuttle(
        BoundContext context,
        SqlParser.Config parserConfig,
        Set<SqlNode> fromPositions,
        org.apache.calcite.rel.type.RelDataTypeFactory typeFactory) {
      this.context = context;
      this.parserConfig = parserConfig;
      this.fromPositions = fromPositions;
      this.typeFactory = typeFactory;
    }

    @Override
    public @Nullable SqlNode visit(SqlCall call) {
      if (isCtxCall(call)) {
        return scalar(call);
      }
      if (call.getKind() == SqlKind.IN || call.getKind() == SqlKind.NOT_IN) {
        SqlNode replaced = membership((SqlBasicCall) call);
        if (replaced != null) {
          return replaced;
        }
      }
      return super.visit(call);
    }

    @Override
    public @Nullable SqlNode visit(SqlIdentifier id) {
      BoundContext.Relation relation = contextRelation(id);
      if (relation == null) {
        return id;
      }
      if (fromPositions.contains(id)) {
        if (relation.isList()) {
          throw new IllegalArgumentException(
              "context list '" + relation.name() + "' is used as a table. A list is a membership "
                  + "set and belongs inside an IN list; bind it as a relation to put it in a FROM.");
        }
        return id;
      }
      throw new IllegalArgumentException(
          "context "
              + (relation.isList() ? "list" : "relation")
              + " '"
              + relation.name()
              + "' is used as a value. A list may appear only as the right side of an IN, and a "
              + "relation only as a table in a FROM.");
    }

    /** {@code CTX('name')} → the bound scalar as a typed literal, or a shape's typed placeholder. */
    private SqlNode scalar(SqlCall call) {
      String name = ctxName(call);
      Expr value = context.scalar(name);
      if (value == null) {
        // ContextSql only emits CTX(…) for a bound scalar, so this is a descriptor that wrote the
        // call itself.
        throw new IllegalArgumentException(
            "'@ctx." + name + "' names no context scalar. Write @ctx." + name + " and bind it,"
                + " or remove it from the descriptor.");
      }
      if (context.isShape(name)) {
        return placeholder(name, value, call.getParserPosition());
      }
      return ContextLiterals.toSqlNode(
          value.getLiteral(), value.getType(), "context scalar '" + name + "'", call.getParserPosition());
    }

    /**
     * {@code CAST(? AS <declared type>)} for one scalar of a shape-only context (D209).
     *
     * <p>The cast is not decoration. A bare placeholder is typed by inference from where it stands,
     * and a mask — {@code FINGERPRINT(first_name, @ctx.mask_key)} — is exactly the position where
     * there is nothing to infer from; the validator refuses it by name. The cast gives the type the
     * host declared, and it costs nothing: the placeholder becomes a {@link BoundParam} of that very
     * type after conversion, and the cast collapses.
     *
     * <p>One index per name, so two uses of one scalar are one parameter.
     */
    private SqlNode placeholder(String name, Expr declared, SqlParserPos pos) {
      int index = boundScalars.indexOf(name);
      if (index < 0) {
        index = boundScalars.size();
        boundScalars.add(name);
      }
      org.apache.calcite.rel.type.RelDataType type =
          new chalk.planner.types.TypeMapper(typeFactory).toCalcite(declared.getType());
      return SqlStdOperatorTable.CAST.createCall(
          pos,
          new org.apache.calcite.sql.SqlDynamicParam(index, pos),
          org.apache.calcite.sql.type.SqlTypeUtil.convertTypeToSpec(type));
    }

    /**
     * {@code x IN ("$chalk$ctx"."list")} and its negation, or null when this membership test has nothing to
     * do with the context and the ordinary walk should continue.
     */
    private @Nullable SqlNode membership(SqlBasicCall call) {
      if (call.operandCount() != 2 || !(call.operand(1) instanceof SqlNodeList values)) {
        return null;
      }
      if (values.size() != 1 || !(values.get(0) instanceof SqlIdentifier id)) {
        return null;
      }
      BoundContext.Relation relation = contextRelation(id);
      if (relation == null) {
        return null;
      }
      if (!relation.isList()) {
        throw new IllegalArgumentException(
            "context relation '" + relation.name() + "' is used in an IN list. A relation is a "
                + "table: write EXISTS (SELECT 1 FROM @ctx." + relation.name() + " …), or bind it "
                + "as a list.");
      }

      SqlParserPos pos = call.getParserPosition();
      boolean negated = call.getKind() == SqlKind.NOT_IN;
      boolean shape = context.isShape(relation.name());
      if (!shape && relation.rows().isEmpty()) {
        // Nothing is in the empty set, and everything is not in it. Never under a shape, where the
        // rows are absent rather than empty and the answer is a question for execution.
        return SqlLiteral.createBoolean(negated, pos);
      }

      SqlNode left = call.operand(0).accept(this);
      int arity = arityOf(left);
      if (arity != relation.columnCount()) {
        throw new IllegalArgumentException(
            "context list '" + relation.name() + "' has " + relation.columnCount()
                + " column(s) and the membership test compares " + arity);
      }

      SqlNode right =
          shape || relation.rows().size() > context.foldMaxRows()
              ? unfolded(relation, pos)
              : literalList(relation, pos);
      return call.getOperator().createCall(pos, left, right);
    }

    /** The rows as a literal list: one literal per row, or one row constructor per row. */
    private SqlNodeList literalList(BoundContext.Relation relation, SqlParserPos pos) {
      List<Field> fields = relation.rowType().getFieldsList();
      List<SqlNode> members = new ArrayList<>(relation.rows().size());
      for (VirtualRow row : relation.rows()) {
        if (fields.size() == 1) {
          members.add(literal(relation, row, 0, fields, pos));
          continue;
        }
        List<SqlNode> parts = new ArrayList<>(fields.size());
        for (int c = 0; c < fields.size(); c++) {
          parts.add(literal(relation, row, c, fields, pos));
        }
        members.add(SqlStdOperatorTable.ROW.createCall(pos, parts));
      }
      return new SqlNodeList(members, pos);
    }

    private SqlNode literal(
        BoundContext.Relation relation, VirtualRow row, int c, List<Field> fields, SqlParserPos pos) {
      Expr value = row.getValues(c);
      return ContextLiterals.toSqlNode(
          value.getLiteral(),
          value.hasType() ? value.getType() : fields.get(c).getType(),
          "context list '" + relation.name() + "' column '" + fields.get(c).getName() + "'",
          pos);
    }

    /**
     * A list above the fold ceiling stays a relation: the membership test becomes a sub-query over
     * the {@code ctx} schema's table, which the Hep pre-pass decorrelates into a semi-join and M5
     * ships as a key set. The rows are then not in the plan — only the name and the row type are,
     * which is the whole point of the ceiling.
     */
    private SqlNode unfolded(BoundContext.Relation relation, SqlParserPos pos) {
      StringBuilder sql = new StringBuilder("SELECT ");
      List<Field> fields = relation.rowType().getFieldsList();
      for (int c = 0; c < fields.size(); c++) {
        sql.append(c == 0 ? "" : ", ").append('"').append(fields.get(c).getName()).append('"');
      }
      sql.append(" FROM ")
          .append(BoundContext.SCHEMA)
          .append(".\"")
          .append(relation.name())
          .append('"');
      try {
        return SqlParser.create(sql.toString(), parserConfig).parseQuery();
      } catch (SqlParseException failure) {
        throw new IllegalArgumentException(
            "context list '" + relation.name() + "' could not be planned as a context table: "
                + failure.getMessage(),
            failure);
      }
    }

    private static int arityOf(SqlNode left) {
      return left.getKind() == SqlKind.ROW ? ((SqlCall) left).operandCount() : 1;
    }

    private BoundContext.@Nullable Relation contextRelation(SqlIdentifier id) {
      if (id.names.size() != 2 || !BoundContext.SCHEMA.equals(id.names.get(0))) {
        return null;
      }
      return context.relation(id.names.get(1));
    }
  }

  /**
   * The scalar marker, by its reserved name (D223).
   *
   * <p>By name and not by operator identity, because this call is in no operator table: the parser
   * leaves it an unresolved function and there is no instance to compare against. The name is what
   * makes that safe — no unquoted identifier can spell it, and a quoted one is refused before the
   * rewrite ({@link chalk.planner.ReservedNames}).
   */
  private static boolean isCtxCall(SqlCall call) {
    return call.getKind() == SqlKind.OTHER_FUNCTION
        && chalk.planner.ReservedNames.CTX.equalsIgnoreCase(call.getOperator().getName())
        && call.operandCount() == 1
        && call.operand(0) instanceof SqlLiteral;
  }

  private static String ctxName(SqlCall call) {
    return ((SqlLiteral) call.operand(0)).getValueAs(String.class);
  }

  /**
   * Every identifier that stands where a table stands. Identity, not equality: two occurrences of
   * the same name are different uses, and only the one in the FROM is a table.
   */
  private static void collectFromPositions(@Nullable SqlNode node, boolean inFrom, Set<SqlNode> into) {
    if (node == null) {
      return;
    }
    if (node instanceof SqlIdentifier) {
      if (inFrom) {
        into.add(node);
      }
      return;
    }
    if (node instanceof SqlSelect select) {
      collectFromPositions(select.getFrom(), true, into);
      collectFromPositions(select.getSelectList(), false, into);
      collectFromPositions(select.getWhere(), false, into);
      collectFromPositions(select.getGroup(), false, into);
      collectFromPositions(select.getHaving(), false, into);
      collectFromPositions(select.getOrderList(), false, into);
      collectFromPositions(select.getOffset(), false, into);
      collectFromPositions(select.getFetch(), false, into);
      return;
    }
    if (node instanceof SqlJoin join) {
      collectFromPositions(join.getLeft(), true, into);
      collectFromPositions(join.getRight(), true, into);
      collectFromPositions(join.getCondition(), false, into);
      return;
    }
    if (node instanceof SqlOrderBy orderBy) {
      collectFromPositions(orderBy.query, false, into);
      collectFromPositions(orderBy.orderList, false, into);
      return;
    }
    if (node instanceof SqlWith with) {
      collectFromPositions(with.withList, false, into);
      collectFromPositions(with.body, false, into);
      return;
    }
    if (node instanceof SqlWithItem item) {
      collectFromPositions(item.query, false, into);
      return;
    }
    if (node instanceof SqlNodeList list) {
      for (SqlNode child : list) {
        collectFromPositions(child, inFrom, into);
      }
      return;
    }
    if (node instanceof SqlCall call) {
      // AS, LATERAL, TABLE and UNNEST keep the FROM-ness of the position they stand in; every other
      // call is an expression, and a table identifier cannot appear under one.
      boolean keeps =
          inFrom
              && switch (call.getKind()) {
                case AS, LATERAL, COLLECTION_TABLE, UNNEST, TABLESAMPLE -> true;
                default -> false;
              };
      for (SqlNode operand : call.getOperandList()) {
        collectFromPositions(operand, keeps, into);
      }
    }
  }
}
