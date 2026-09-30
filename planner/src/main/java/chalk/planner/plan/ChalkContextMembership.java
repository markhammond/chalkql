package chalk.planner.plan;

import chalk.planner.entitlement.ContextTable;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexSubQuery;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlOperandCountRange;
import org.apache.calcite.sql.SqlOperator;
import org.apache.calcite.sql.SqlOperatorBinding;
import org.apache.calcite.sql.SqlSpecialOperator;
import org.apache.calcite.sql.SqlSyntax;
import org.apache.calcite.sql.type.SqlOperandCountRanges;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.sql.type.SqlTypeUtil;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A membership over a context list the executor holds (F161): {@code CHALK_CONTEXT_MEMBERSHIP('list',
 * k1, …, kn)} means the tuple {@code (k1, …, kn)} is one of the rows of the list the host bound as
 * {@code @ctx.list}, compared in the list's own column order. It converts to the IR's
 * {@code ContextMembership}, which the executor answers from a hash set of the list's rows built once
 * per execution.
 *
 * <p>It stands where a membership over a whole context list would otherwise become a join: under
 * execute-time binding, where each list was a {@code LEFT} join of its own (§3.2, D209), and in a
 * folded leaf past what the split of F36 carries, where Calcite's rewrite of an {@code IN} inside an
 * {@code OR} joins each list in turn. One join per list nested the plan one level deeper per list,
 * which is what F161 measured; an expression nests nothing.
 *
 * <p>Its answer is SQL's {@code IN} exactly, three-valued included, so unlike the marker it replaces
 * it may stand under a negation. It is in no operator table, so no SQL text can name it, and a
 * source is never asked to evaluate one: the list is the executor's, and {@link PushdownGate} keeps
 * every expression holding one local.
 */
public final class ChalkContextMembership {
  private ChalkContextMembership() {}

  /** The one operator. The list's name is its first operand, a character literal. */
  public static final SqlOperator OPERATOR = new MembershipOperator();

  /** Whether {@code node} is a membership over a context list the executor holds. */
  public static boolean is(RexNode node) {
    return node instanceof RexCall call && call.getOperator() == OPERATOR;
  }

  /** The list's name. Only meaningful when {@link #is} says yes. */
  public static String list(RexCall call) {
    return ((RexLiteral) call.getOperands().get(0)).getValueAs(String.class);
  }

  /** The tuple's columns, in the list's column order. Only meaningful when {@link #is} says yes. */
  public static List<RexNode> columns(RexCall call) {
    return call.getOperands().subList(1, call.getOperands().size());
  }

  /**
   * The call standing for {@code membership}, or null where the sub-query is anything but a whole
   * context list — the rows of one relation the host bound, every column of it — which is the one
   * shape the context fold writes ({@code x IN (@ctx.list)}) and the one the executor can answer
   * from a set. Anything else keeps the join it had.
   */
  public static @Nullable RexNode of(RexSubQuery membership, RexBuilder rexBuilder) {
    if (membership.getKind() != SqlKind.IN) {
      return null;
    }
    RelNode rel = membership.rel.stripped();

    // Which list column each of the sub-query's columns is: the identity where the fold wrote
    // `SELECT "a", "b" FROM ctx.list` and Calcite kept the projection, and a permutation of every
    // column where it was written in another order. Anything else — an expression, a column read
    // twice, a column left out — is not a whole list.
    int width = membership.rel.getRowType().getFieldCount();
    int[] listColumn = new int[width];
    for (int i = 0; i < width; i++) {
      listColumn[i] = i;
    }
    if (rel instanceof Project project) {
      boolean[] seen = new boolean[project.getInput().getRowType().getFieldCount()];
      if (seen.length != width) {
        return null;
      }
      for (int i = 0; i < width; i++) {
        if (!(project.getProjects().get(i) instanceof RexInputRef ref) || seen[ref.getIndex()]) {
          return null;
        }
        seen[ref.getIndex()] = true;
        listColumn[i] = ref.getIndex();
      }
      rel = project.getInput().stripped();
    }
    if (!(rel instanceof TableScan scan)) {
      return null;
    }
    ContextTable table = scan.getTable().unwrap(ContextTable.class);
    if (table == null
        || scan.getRowType().getFieldCount() != width
        || membership.getOperands().size() != width) {
      return null;
    }

    // Each key in the list column it is compared with, at the type the comparison is made in: the
    // two are the same type wherever the tenancy compiler wrote the list, and where a host's are not
    // the key is cast to the wider one — the list's values are bound into it at execution, which
    // D317 holds exact.
    RelDataTypeFactory types = rexBuilder.getTypeFactory();
    RexNode[] keys = new RexNode[width];
    for (int i = 0; i < width; i++) {
      RexNode key = membership.getOperands().get(i);
      RelDataTypeField field = scan.getRowType().getFieldList().get(listColumn[i]);
      RelDataType compared = comparisonType(types, key.getType(), field.getType());
      if (compared == null) {
        return null;
      }
      keys[listColumn[i]] =
          SqlTypeUtil.equalSansNullability(types, key.getType(), compared)
              ? key
              : rexBuilder.makeCast(
                  types.createTypeWithNullability(compared, key.getType().isNullable()), key);
    }

    List<RexNode> operands = new ArrayList<>(width + 1);
    operands.add(rexBuilder.makeLiteral(table.contextName()));
    for (RexNode key : keys) {
      operands.add(key);
    }
    return rexBuilder.makeCall(membership.getType(), OPERATOR, operands);
  }

  /** The type a key and a list column compare in, or null where the two do not compare at all. */
  private static @Nullable RelDataType comparisonType(
      RelDataTypeFactory types, RelDataType key, RelDataType column) {
    if (SqlTypeUtil.equalSansNullability(types, key, column)) {
      return types.createTypeWithNullability(column, false);
    }
    RelDataType wider = types.leastRestrictive(List.of(key, column));
    return wider == null ? null : types.createTypeWithNullability(wider, false);
  }

  /**
   * The operator. A {@link SqlSpecialOperator} for the same reason the key set's is — it is written
   * by a rewrite, never parsed — and <b>dynamic</b>: its answer is the execution's binding, so
   * nothing may fold a call of it into a constant at planning, whatever its operands are.
   */
  private static final class MembershipOperator extends SqlSpecialOperator {
    MembershipOperator() {
      super("CHALK_CONTEXT_MEMBERSHIP", SqlKind.OTHER_FUNCTION, 32, true, null, null, null);
    }

    @Override
    public SqlSyntax getSyntax() {
      return SqlSyntax.FUNCTION;
    }

    @Override
    public SqlOperandCountRange getOperandCountRange() {
      // The list's name and at least one column.
      return SqlOperandCountRanges.from(2);
    }

    @Override
    public boolean isDynamicFunction() {
      return true;
    }

    @Override
    public RelDataType inferReturnType(SqlOperatorBinding binding) {
      // Every call is made with the membership's own type; this is only asked of a call rebuilt
      // without one, and UNKNOWN is a possible answer wherever anything is nullable.
      RelDataTypeFactory factory = binding.getTypeFactory();
      return factory.createTypeWithNullability(
          factory.createSqlType(SqlTypeName.BOOLEAN), true);
    }
  }
}
