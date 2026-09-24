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
import org.apache.calcite.sql.SqlWindow;
import org.apache.calcite.sql.fun.SqlCase;
import org.apache.calcite.sql.validate.SqlValidator;
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
 * {@code CASE} or {@code COALESCE} result; as a {@code CAST}'s operand; as a built-in aggregate's or
 * window function's argument; and as a column of a {@code UNION}, {@code INTERSECT} or {@code EXCEPT}
 * that compares rows. The {@code ROW} constructor is refused where it is lowered ({@code RexToIr}),
 * and a nested composite where it is declared.
 */
public final class CompositeSupport {
  private CompositeSupport() {}

  /** Throws for the first misplaced composite; returns silently for a statement that has none. */
  public static void check(SqlNode statement, SqlValidator validator) {
    Checker checker = new Checker(validator, false);
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
  public static void checkUnvalidated(SqlNode statement, @Nullable SqlValidator validator) {
    if (validator == null) {
      return;
    }
    Checker checker = new Checker(validator, true);
    walk(statement, checker::visit);
  }

  private static final class Checker {
    private final SqlValidator validator;

    /**
     * Whether validation stopped short. A node it never reached may have no type, and asking for a
     * query's row type can start validating it again and throw the error already being reported.
     */
    private final boolean unvalidated;

    Checker(SqlValidator validator, boolean unvalidated) {
      this.validator = validator;
      this.unvalidated = unvalidated;
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
              "PARTITION BY a composite value (" + key + ")",
              "A composite value has no equality, so rows cannot be partitioned by one. Partition by one "
                  + "of its fields instead, e.g. PARTITION BY f(x).category.");
        }
      }
      for (SqlNode key : window.getOrderList()) {
        if (isComposite(unwrapOrder(key))) {
          throw refusal(
              "a window ORDER BY a composite value (" + unwrapOrder(key) + ")",
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
            "GROUP BY a composite value (" + item + ")",
            "A composite value has no equality, so rows cannot be grouped by one. Group by one of its "
                + "fields instead, e.g. GROUP BY f(x).category.");
      }
    }

    private void orderItem(
        @Nullable RelDataType row, @Nullable SqlNodeList selectList, SqlNode item, String clause) {
      SqlNode key = unwrapOrder(item);
      if (isComposite(resolve(row, selectList, key), key)) {
        throw refusal(
            clause + " a composite value (" + key + ")",
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
                  "a comparison of a composite value (" + call + ")",
                  "A composite value has no equality or ordering, so it cannot be compared. Compare one "
                      + "of its fields instead, e.g. f(x).category = 'food'.");
            }
          }
        }
        case IN, NOT_IN -> {
          SqlNode value = call.operand(0);
          if (isComposite(value)) {
            throw refusal(
                "a composite value in IN (" + value + ")",
                "A composite value has no equality, so it cannot be looked up in a list. Test one of its "
                    + "fields instead, e.g. f(x).category IN ('food', 'rent').");
          }
        }
        case CASE, COALESCE -> {
          if (isComposite(typeOf(call), call) || resultIsComposite(call)) {
            throw refusal(
                "a composite value as a " + kind.name().toUpperCase(Locale.ROOT) + " result",
                "A composite value is carried as the function returned it and is never chosen between. "
                    + "Take it apart first and choose between its fields, e.g. CASE WHEN … THEN "
                    + "f(x).category END.");
          }
        }
        case CAST -> {
          SqlNode value = call.operand(0);
          if (isComposite(value)) {
            throw refusal(
                "CAST of a composite value (" + value + ")",
                "A composite value is never cast. Cast one of its fields instead, e.g. "
                    + "CAST(f(x).confidence AS DECIMAL(5, 2)).");
          }
        }
        case UNION, INTERSECT, EXCEPT -> setOperation(call);
        default -> aggregate(call);
      }
    }

    /**
     * Whether one of a {@code CASE}'s or {@code COALESCE}'s results is a composite value: what a statement Calcite
     * refused for mixing a composite value with a scalar still has, when the call itself has no type.
     */
    private boolean resultIsComposite(SqlCall call) {
      List<SqlNode> results = new java.util.ArrayList<>();
      if (call instanceof SqlCase caseCall) {
        results.addAll(caseCall.getThenOperands().getList());
        results.add(caseCall.getElseOperand());
      } else {
        results.addAll(call.getOperandList());
      }
      for (SqlNode result : results) {
        if (isComposite(result)) {
          return true;
        }
      }
      return false;
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
              call.getOperator().getName() + " of a composite value (" + operand + ")",
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
      if (type != null) {
        return type.isStruct();
      }
      return unvalidated
          && node instanceof SqlCall call
          && UserOperators.declarationOf(call.getOperator()) instanceof UserFunction declaration
          && declaration.descriptor().getReturnType().getKind() == TypeKind.TYPE_KIND_COMPOSITE;
    }
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
