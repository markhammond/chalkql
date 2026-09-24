package chalk.planner.plan;

import chalk.ir.v1.TypeKind;
import chalk.planner.UnsupportedFeatureException;
import chalk.planner.catalog.UserFunction;
import java.util.List;
import java.util.Locale;
import java.util.function.Consumer;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.sql.SqlAggFunction;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlNodeList;
import org.apache.calcite.sql.SqlNumericLiteral;
import org.apache.calcite.sql.SqlOrderBy;
import org.apache.calcite.sql.SqlSelect;
import org.apache.calcite.sql.SqlSetOperator;
import org.apache.calcite.sql.SqlUtil;
import org.apache.calcite.sql.SqlWindow;
import org.apache.calcite.sql.fun.SqlCase;
import org.apache.calcite.sql.validate.SqlValidator;
import org.apache.calcite.sql.validate.SqlValidatorNamespace;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Where a composite value may not stand in a statement, refused by name before conversion (D291, ADR 0077).
 *
 * <p>A composite value — what a client-bodied user function returns — is carried and taken apart by field
 * access, and never compared, ordered or grouped. Calcite would accept several of those shapes: it
 * validates {@code f(x) = f(x)} and folds it to {@code TRUE} during conversion, and it converts
 * {@code ORDER BY f(x)} into a sort on a composite column that nothing can execute. So they are refused
 * here, on the validated statement, where the validator still knows every expression's type and
 * nothing has been folded away yet — and the message names the construct and the way out, which is
 * nearly always the same: use one of the composite's fields.
 *
 * <p>The refusals: a composite value as an {@code ORDER BY}, {@code GROUP BY}, {@code DISTINCT}, window
 * partition or window order key; as an operand of a comparison, {@code BETWEEN} or {@code IN}; as a
 * {@code CASE} or {@code COALESCE} result beside a scalar or beside a composite of another type — a
 * choice between composites of one type is legal (D295), which is what the group-size guard writes
 * over a composite measure; as a {@code CAST}'s operand; as a built-in aggregate's or window
 * function's argument; and as a column of a {@code UNION}, {@code INTERSECT} or {@code EXCEPT} that
 * compares rows. The {@code ROW} constructor is refused where it is lowered ({@code RexToIr}),
 * and a nested composite where it is declared.
 *
 * <p>A refusal that names an expression quotes the statement's own text for it (D296, {@link
 * StatementText}), and prints the validated form only where no span of the statement can be
 * recovered — a node the validator synthesised, or one a SQL body brought in.
 */
public final class CompositeSupport {
  private CompositeSupport() {}

  /**
   * Throws for the first misplaced composite; returns silently for a statement that has none. A
   * refusal quotes {@code text} for the expression it names.
   */
  public static void check(SqlNode statement, SqlValidator validator, StatementText text) {
    Checker checker = new Checker(validator, false, text);
    walk(statement, checker::visit);
  }

  /**
   * The same over a statement Calcite could not validate, asking only about the nodes whose types it
   * derived before it stopped. Calcite's own type checks can reach a misplaced composite first — a composite value
   * {@code IN} a subquery reads to it as a row of the composite's fields, and a comparison with a scalar,
   * a {@code CAST}, a {@code MAX} or a {@code CASE} mixing a composite value with a scalar has no signature — and
   * the refusal should not depend on which of the two got there first. Where validation stopped
   * before it recorded a node's type — a select item is typed on a copy that only replaces the
   * original once the whole list is through — a call to a function declared to return a composite value is
   * known to be one from its declaration alone. Returns silently when nothing it can see is a
   * misplaced composite, and the caller throws Calcite's own error.
   */
  public static void checkUnvalidated(
      SqlNode statement, @Nullable SqlValidator validator, StatementText text) {
    if (validator == null) {
      return;
    }
    Checker checker = new Checker(validator, true, text);
    walk(statement, checker::visit);
  }

  private static final class Checker {
    private final SqlValidator validator;

    /**
     * Whether validation stopped short. A node it never reached may have no type, and asking for a
     * query's row type can start validating it again and throw the error already being reported.
     */
    private final boolean unvalidated;

    /** The statement's own words, which every refusal naming an expression quotes (D296). */
    private final StatementText text;

    Checker(SqlValidator validator, boolean unvalidated, StatementText text) {
      this.validator = validator;
      this.unvalidated = unvalidated;
      this.text = text;
    }

    void visit(SqlNode node) {
      if (node instanceof SqlSelect select) {
        select(select);
      } else if (node instanceof SqlOrderBy orderBy) {
        orderBy(orderBy);
      } else if (node instanceof SqlWindow window) {
        window(window);
      } else if (node instanceof SqlCall call) {
        call(call);
      }
    }

    private void select(SqlSelect select) {
      RelDataType row = typeOf(select);
      if (select.isDistinct() && row != null) {
        for (RelDataTypeField field : row.getFieldList()) {
          if (field.getType().isStruct()) {
            throw refusal(
                "SELECT DISTINCT over the composite column '" + field.getName() + "'",
                "A composite value has no equality, so DISTINCT cannot compare the rows that hold one. "
                    + "Select its fields as columns of their own, or GROUP BY one of them.");
          }
        }
      }

      SqlNodeList group = select.getGroup();
      if (group != null) {
        for (SqlNode item : group) {
          groupItem(select, row, item);
        }
      }

      SqlNodeList order = select.getOrderList();
      if (order != null) {
        for (SqlNode item : order) {
          orderItem(row, select.getSelectList(), item, "ORDER BY");
        }
      }
    }

    /** A query whose {@code ORDER BY} sits outside it — a set operation's, say. */
    private void orderBy(SqlOrderBy orderBy) {
      RelDataType row = typeOf(orderBy.query);
      SqlNodeList selectList =
          orderBy.query instanceof SqlSelect select ? select.getSelectList() : null;
      for (SqlNode item : orderBy.orderList) {
        orderItem(row, selectList, item, "ORDER BY");
      }
    }

    private void window(SqlWindow window) {
      for (SqlNode key : window.getPartitionList()) {
        if (isComposite(key)) {
          throw refusal(
              "PARTITION BY a composite value (" + text.quote(key) + ")",
              "A composite value has no equality, so rows cannot be partitioned by one. Partition by one "
                  + "of its fields instead, e.g. PARTITION BY f(x).category.");
        }
      }
      for (SqlNode key : window.getOrderList()) {
        if (isComposite(unwrapOrder(key))) {
          throw refusal(
              "a window ORDER BY a composite value (" + text.quote(unwrapOrder(key)) + ")",
              "A composite value has no ordering. Order the window by one of its fields instead, e.g. "
                  + "ORDER BY f(x).confidence.");
        }
      }
    }

    private void groupItem(SqlSelect select, @Nullable RelDataType row, SqlNode item) {
      // GROUPING SETS, ROLLUP and CUBE hold the keys one level down.
      if (item instanceof SqlCall call
          && (call.getKind() == SqlKind.GROUPING_SETS
              || call.getKind() == SqlKind.ROLLUP
              || call.getKind() == SqlKind.CUBE
              || call.getKind() == SqlKind.ROW)) {
        for (SqlNode operand : call.getOperandList()) {
          if (operand != null) {
            groupItem(select, row, operand);
          }
        }
        return;
      }

      if (isComposite(resolve(row, select.getSelectList(), item), item)) {
        throw refusal(
            "GROUP BY a composite value (" + text.quote(item) + ")",
            "A composite value has no equality, so rows cannot be grouped by one. Group by one of its "
                + "fields instead, e.g. GROUP BY f(x).category.");
      }
    }

    private void orderItem(
        @Nullable RelDataType row, @Nullable SqlNodeList selectList, SqlNode item, String clause) {
      SqlNode key = unwrapOrder(item);
      if (isComposite(resolve(row, selectList, key), key)) {
        throw refusal(
            clause + " a composite value (" + text.quote(key) + ")",
            "A composite value has no ordering. Sort by one of its fields instead, e.g. ORDER BY "
                + "f(x).confidence, or ORDER BY (c).confidence over a subquery alias.");
      }
    }

    private void call(SqlCall call) {
      SqlKind kind = call.getKind();
      switch (kind) {
        case EQUALS, NOT_EQUALS, LESS_THAN, LESS_THAN_OR_EQUAL, GREATER_THAN,
            GREATER_THAN_OR_EQUAL, IS_DISTINCT_FROM, IS_NOT_DISTINCT_FROM, BETWEEN, NULLIF,
            SOME, ALL -> {
          for (SqlNode operand : call.getOperandList()) {
            if (isComposite(operand)) {
              throw refusal(
                  "a comparison of a composite value (" + text.quote(call) + ")",
                  "A composite value has no equality or ordering, so it cannot be compared. Compare one "
                      + "of its fields instead, e.g. f(x).category = 'food'.");
            }
          }
        }
        case IN, NOT_IN -> {
          SqlNode value = call.operand(0);
          if (isComposite(value)) {
            throw refusal(
                "a composite value in IN (" + text.quote(value) + ")",
                "A composite value has no equality, so it cannot be looked up in a list. Test one of its "
                    + "fields instead, e.g. f(x).category IN ('food', 'rent').");
          }
        }
        case CASE, COALESCE -> chooses(call);
        case CAST -> {
          SqlNode value = call.operand(0);
          if (isComposite(value)) {
            throw refusal(
                "CAST of a composite value (" + text.quote(value) + ")",
                "A composite value is never cast. Cast one of its fields instead, e.g. "
                    + "CAST(f(x).confidence AS DECIMAL(5, 2)).");
          }
        }
        case UNION, INTERSECT, EXCEPT -> setOperation(call);
        default -> aggregate(call);
      }
    }

    /**
     * A {@code CASE} or {@code COALESCE} that answers a composite value (D295). Choosing between
     * composite values of one type is legal — the same fields, named and typed alike, nullability
     * aside — and a {@code NULL} result is one of them. Choosing between a composite and a scalar, or
     * between composites of different types, is refused by name: Calcite validates neither, and the
     * first reaches here only when validation stopped at it.
     */
    private void chooses(SqlCall call) {
      List<SqlNode> results = results(call);
      RelDataType type = typeOf(call);
      boolean composite = isComposite(type, call);
      for (SqlNode result : results) {
        composite |= result != null && isComposite(result);
      }
      if (!composite) {
        return;
      }

      // The construct as the statement wrote it: Calcite validates a COALESCE as the CASE it expands to.
      String construct = text.construct(call);
      boolean scalarBeside = false;
      boolean different = false;
      for (SqlNode result : results) {
        if (result == null || SqlUtil.isNullLiteral(result, true)) {
          continue;
        }
        if (!isComposite(result)) {
          scalarBeside = true;
          continue;
        }
        RelDataType resultType = typeOf(result);
        if (type != null && type.isStruct() && resultType != null && !oneComposite(resultType, type)) {
          different = true;
        }
      }

      if (scalarBeside) {
        throw refusal(
            "a composite value as a " + construct + " result (" + text.quote(call) + ")",
            "A " + construct + " chooses between composite values of one type, or between scalars, "
                + "and never between the two. Take the composite apart and choose between its fields, "
                + "e.g. CASE WHEN … THEN f(x).category END.");
      }
      // Validation stopped before it typed this choice, with every result a composite value: refused
      // when their declared types differ — the one way Calcite refuses such a choice — and left to
      // Calcite's own error when they agree or cannot be told, since the failure was elsewhere.
      if (unvalidated && (type == null || !type.isStruct())) {
        different |= declaredTypesDiffer(results);
      }
      if (different) {
        throw refusal(
            "a " + construct + " between composite values of different types (" + text.quote(call) + ")",
            "A " + construct + " chooses between composite values of one type only: the same fields, "
                + "named and typed alike. Choose between their fields instead, e.g. CASE WHEN … THEN "
                + "f(x).category ELSE g(x).label END.");
      }
    }

    /**
     * Whether the composite results of a choice Calcite never typed are calls to functions declared
     * to return different composites. A result that is not such a call cannot be told, and then
     * nothing is claimed.
     */
    private static boolean declaredTypesDiffer(List<SqlNode> results) {
      chalk.ir.v1.Type first = null;
      for (SqlNode result : results) {
        if (result == null || SqlUtil.isNullLiteral(result, true)) {
          continue;
        }
        if (!(result instanceof SqlCall call)
            || !(UserOperators.declarationOf(call.getOperator()) instanceof UserFunction declaration)
            || declaration.descriptor().getReturnType().getKind() != TypeKind.TYPE_KIND_COMPOSITE) {
          return false;
        }
        chalk.ir.v1.Type declared = declaration.descriptor().getReturnType();
        if (first == null) {
          first = declared;
        } else if (!sameFields(first, declared)) {
          return true;
        }
      }
      return false;
    }

    /** Two declared composites with the same fields, named alike ignoring case, nullability aside. */
    private static boolean sameFields(chalk.ir.v1.Type left, chalk.ir.v1.Type right) {
      if (left.getFieldsCount() != right.getFieldsCount()) {
        return false;
      }
      for (int f = 0; f < left.getFieldsCount(); f++) {
        chalk.ir.v1.Field a = left.getFields(f);
        chalk.ir.v1.Field b = right.getFields(f);
        if (!a.getName().equalsIgnoreCase(b.getName())
            || !a.getType().toBuilder().setNullable(false).build()
                .equals(b.getType().toBuilder().setNullable(false).build())) {
          return false;
        }
      }
      return true;
    }

    /**
     * A {@code CASE}'s {@code THEN} and {@code ELSE} operands, or a {@code COALESCE}'s operands, each
     * as the statement wrote it: the {@code CASE} Calcite expands a {@code COALESCE} into wraps each
     * {@code THEN} in its own {@code CAST_NOT_NULL}, which is not the statement's and, where
     * validation stopped, has no type to go by.
     */
    private static List<SqlNode> results(SqlCall call) {
      List<SqlNode> results = new java.util.ArrayList<>();
      if (call instanceof SqlCase caseCall) {
        results.addAll(caseCall.getThenOperands().getList());
        results.add(caseCall.getElseOperand());
      } else {
        results.addAll(call.getOperandList());
      }
      results.replaceAll(CompositeSupport::unwrapped);
      return results;
    }

    /**
     * Whether {@code actual} is {@code expected}'s composite type: the same fields in the same order,
     * named alike ignoring case, typed alike but for nullability.
     */
    private boolean oneComposite(RelDataType actual, RelDataType expected) {
      if (!actual.isStruct() || actual.getFieldCount() != expected.getFieldCount()) {
        return false;
      }
      for (int f = 0; f < actual.getFieldCount(); f++) {
        RelDataTypeField left = actual.getFieldList().get(f);
        RelDataTypeField right = expected.getFieldList().get(f);
        if (!left.getName().equalsIgnoreCase(right.getName())
            || !org.apache.calcite.sql.type.SqlTypeUtil.equalSansNullability(
                validator.getTypeFactory(), left.getType(), right.getType())) {
          return false;
        }
      }
      return true;
    }

    /** A set operation that compares rows cannot hold a composite value; {@code UNION ALL} carries one. */
    private void setOperation(SqlCall call) {
      if (call.getOperator() instanceof SqlSetOperator operator && operator.isAll()
          && call.getKind() == SqlKind.UNION) {
        return;
      }
      RelDataType row = typeOf(call);
      if (row == null) {
        return;
      }
      for (RelDataTypeField field : row.getFieldList()) {
        if (field.getType().isStruct()) {
          throw refusal(
              call.getOperator().getName() + " over the composite column '" + field.getName() + "'",
              "A composite value has no equality, so a set operation that compares rows cannot hold one. "
                  + "UNION ALL, which compares nothing, carries a composite value.");
        }
      }
    }

    /** A built-in aggregate or window function never takes a composite value; a user aggregate never does either. */
    private void aggregate(SqlCall call) {
      if (!(call.getOperator() instanceof SqlAggFunction)
          || UserOperators.declarationOf(call.getOperator()) != null) {
        return;
      }
      for (SqlNode operand : call.getOperandList()) {
        if (operand == null
            || (operand instanceof SqlIdentifier identifier && identifier.isStar())) {
          continue;
        }
        if (isComposite(operand)) {
          throw refusal(
              call.getOperator().getName() + " of a composite value (" + text.quote(operand) + ")",
              "A built-in aggregate takes a scalar. Aggregate one of the composite's fields instead, "
                  + "e.g. MAX(f(x).confidence).");
        }
      }
    }

    /**
     * An {@code ORDER BY} or {@code GROUP BY} item's type: a select item's, when the item is an
     * ordinal or an alias of one, and the validator's own record of the expression otherwise.
     */
    private @Nullable RelDataType resolve(
        @Nullable RelDataType row, @Nullable SqlNodeList selectList, SqlNode item) {
      if (row != null && item instanceof SqlNumericLiteral literal && literal.isInteger()) {
        int ordinal = literal.intValue(true) - 1;
        List<RelDataTypeField> fields = row.getFieldList();
        return ordinal >= 0 && ordinal < fields.size() ? fields.get(ordinal).getType() : null;
      }
      if (row != null && item instanceof SqlIdentifier identifier && identifier.isSimple()) {
        RelDataTypeField field = aliased(row, selectList, identifier.getSimple());
        if (field != null) {
          return field.getType();
        }
      }
      return typeOf(item);
    }

    /** The select item a simple name is the alias of, if it is one. */
    private static @Nullable RelDataTypeField aliased(
        RelDataType row, @Nullable SqlNodeList selectList, String name) {
      if (selectList == null) {
        return null;
      }
      for (int i = 0; i < selectList.size() && i < row.getFieldCount(); i++) {
        SqlNode item = selectList.get(i);
        if (item instanceof SqlCall call && call.getKind() == SqlKind.AS) {
          SqlNode alias = call.operand(1);
          if (alias instanceof SqlIdentifier identifier
              && identifier.getSimple().equalsIgnoreCase(name)) {
            return row.getFieldList().get(i);
          }
        }
      }
      return null;
    }

    private @Nullable RelDataType typeOf(SqlNode node) {
      if (!unvalidated) {
        return validator.getValidatedNodeTypeIfKnown(node);
      }
      // A query has a namespace, and asking for the row type of one whose validation never
      // finished starts validating it again — and a select still marked as under validation
      // recurses until the stack runs out. An expression's type is only ever looked up.
      if (validator.getNamespace(node) != null) {
        return null;
      }
      try {
        return validator.getValidatedNodeTypeIfKnown(node);
      } catch (RuntimeException notReached) {
        return null;
      }
    }

    /** Whether {@code node} is a composite value, by its validated type or, failing one, its declaration. */
    private boolean isComposite(@Nullable SqlNode node) {
      return node != null && isComposite(typeOf(node), node);
    }

    /**
     * Whether a node of {@code type} is a composite value. With no type — validation stopped before it — a
     * call to a function declared to return a composite value is one all the same: the declaration is the
     * only thing its type is ever inferred from.
     */
    private boolean isComposite(@Nullable RelDataType type, SqlNode node) {
      // Row-value syntax — `(a, b) IN (…)`, `(a, b) = (c, d)`, and the CAST the validator's coercion
      // puts round the right-hand row — is Calcite's to expand into scalar comparisons, and is
      // never a composite value.
      if (isRowSyntax(node)) {
        return false;
      }
      // A query's type is its row type, which Calcite calls a struct too. A query standing where a
      // value does — the right of `= SOME (…)` or `IN (…)`, a scalar sub-query in a CASE — is a
      // composite value only when its one column is one.
      SqlValidatorNamespace namespace = unvalidated ? null : validator.getNamespace(node);
      if (node.isA(SqlKind.QUERY) || namespace != null) {
        if (namespace == null) {
          return false;
        }
        RelDataType row = namespace.getRowType();
        return row.getFieldCount() == 1 && row.getFieldList().get(0).getType().isStruct();
      }
      if (type != null) {
        return type.isStruct();
      }
      return unvalidated
          && node instanceof SqlCall call
          && UserOperators.declarationOf(call.getOperator()) instanceof UserFunction declaration
          && declaration.descriptor().getReturnType().getKind() == TypeKind.TYPE_KIND_COMPOSITE;
    }
  }

  /** {@code node} without the {@code CAST_NOT_NULL} Calcite's own rewrites put round an operand. */
  private static @Nullable SqlNode unwrapped(@Nullable SqlNode node) {
    SqlNode current = node;
    while (current instanceof SqlCall call
        && current.getKind() == SqlKind.CAST_NOT_NULL
        && call.operandCount() == 1) {
      current = call.operand(0);
    }
    return current;
  }

  /** A {@code ROW(…)} of values, under any number of casts. */
  private static boolean isRowSyntax(SqlNode node) {
    SqlNode current = node;
    while (current instanceof SqlCall call
        && call.getKind() == SqlKind.CAST
        && !call.getOperandList().isEmpty()
        && call.operand(0) != null) {
      current = call.operand(0);
    }
    return current.getKind() == SqlKind.ROW;
  }

  /** An {@code ORDER BY} item without its {@code DESC} and {@code NULLS} wrappers. */
  private static SqlNode unwrapOrder(SqlNode item) {
    SqlNode current = item;
    while (current instanceof SqlCall call
        && (call.getKind() == SqlKind.DESCENDING
            || call.getKind() == SqlKind.NULLS_FIRST
            || call.getKind() == SqlKind.NULLS_LAST)) {
      current = call.operand(0);
    }
    return current;
  }

  private static UnsupportedFeatureException refusal(String construct, String alternative) {
    return new UnsupportedFeatureException(
        construct, alternative + " (docs/design/51-structured-function-results.md §1)");
  }

  /** Every node of the statement, parents before children. */
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
}
