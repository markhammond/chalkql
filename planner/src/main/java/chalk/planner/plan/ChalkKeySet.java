package chalk.planner.plan;

import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlOperandCountRange;
import org.apache.calcite.sql.SqlOperator;
import org.apache.calcite.sql.SqlOperatorBinding;
import org.apache.calcite.sql.SqlSpecialOperator;
import org.apache.calcite.sql.SqlSyntax;
import org.apache.calcite.sql.SqlWriter;
import org.apache.calcite.sql.type.SqlOperandCountRanges;
import org.apache.calcite.sql.type.SqlTypeName;

/**
 * The predicate a lookup join binds its keys into (M5, D105).
 *
 * <p>{@code KEY_SET_IN(column)} means "column is one of the keys this call carries" and unparses as
 * {@code "column" IN (?)}; {@code KEY_SET_ROWS(column)} means the same and unparses as
 * {@code "column" IN (VALUES (?))}, which is the shape a broadcast ships its rows in. Both convert
 * to the same IR — {@code InList(column, [KeySetParam])} — and which one was used is recorded on the
 * {@code LookupJoin} so the executor knows how to expand the single placeholder into a call's worth
 * of them.
 *
 * <p><b>A key set carries its columns (F50).</b> Either operator takes one column or several, and
 * with several it unparses the tuple: {@code ("member_id", "org_id") IN (?)}, whose one placeholder
 * the executor expands into {@code (?, ?), (?, ?), …} — a row-constructor IN list, which is what
 * lets a bound list of pairs drive a lookup instead of being joined locally against the whole
 * table. The IR spelling of a composite key set is {@code KeySetMatch}, because the value being
 * matched is a tuple and the IR has no tuple. A source must declare
 * {@code supports_row_value_in_list} before one is pushed into it.
 *
 * <p>Neither is in any operator table. No SQL text can name them and the validator never sees one:
 * they exist so that a rule can put a bindable key set into a {@code RexNode}, which is the only
 * vocabulary the pushdown machinery and {@code RelToSql} understand. Everything downstream —
 * {@code PushdownGate}, {@code SourceSql}, {@code RexToIr} — treats them as ordinary calls.
 */
public final class ChalkKeySet {
  private ChalkKeySet() {}

  /** {@code column IN (?)} — one placeholder standing for the whole key set. */
  public static final SqlOperator KEY_SET_IN = new KeySetOperator("CHALK_KEY_SET_IN", false);

  /** {@code column IN (VALUES (?))} — the same set shipped as rows (JOIN_STRATEGY_BROADCAST). */
  public static final SqlOperator KEY_SET_ROWS = new KeySetOperator("CHALK_KEY_SET_ROWS", true);

  /** Whether {@code node} is one of the two key-set predicates. */
  public static boolean is(RexNode node) {
    return node instanceof RexCall call && call.getOperator() instanceof KeySetOperator;
  }

  /** Whether {@code node} is the rows-shaped one. Only meaningful when {@link #is} says yes. */
  public static boolean isRows(RexNode node) {
    return node instanceof RexCall call
        && call.getOperator() instanceof KeySetOperator operator
        && operator.rows;
  }

  /**
   * How many key columns this key set matches — one for the {@code InList} shape, more for a
   * row-constructor one (F50). Zero when {@code node} is not a key set at all.
   */
  public static int columns(RexNode node) {
    return is(node) ? ((RexCall) node).getOperands().size() : 0;
  }

  /** Whether the subtree contains a key-set predicate anywhere. */
  public static boolean contains(RexNode node) {
    if (is(node)) {
      return true;
    }
    if (node instanceof RexCall call) {
      for (RexNode operand : call.getOperands()) {
        if (contains(operand)) {
          return true;
        }
      }
    }
    return false;
  }

  /**
   * A key-set operator. A {@link SqlSpecialOperator} rather than a {@code SqlFunction} because it
   * writes itself as an infix {@code IN} rather than as {@code NAME(args)}, and {@code
   * SqlSpecialOperator} is what lets {@link #unparse} say so.
   */
  private static final class KeySetOperator extends SqlSpecialOperator {
    private final boolean rows;

    KeySetOperator(String name, boolean rows) {
      super(name, SqlKind.OTHER_FUNCTION, 32, true, null, null, null);
      this.rows = rows;
    }

    @Override
    public SqlSyntax getSyntax() {
      return SqlSyntax.SPECIAL;
    }

    @Override
    public SqlOperandCountRange getOperandCountRange() {
      // One column, or the several a row-constructor key set matches (F50).
      return SqlOperandCountRanges.from(1);
    }

    @Override
    public RelDataType inferReturnType(SqlOperatorBinding binding) {
      RelDataTypeFactory factory = binding.getTypeFactory();

      // Never NULL: a NULL key matches nothing, which is FALSE, not UNKNOWN. The executor drops
      // NULL keys before it builds a call, so the source never sees one either.
      return factory.createSqlType(SqlTypeName.BOOLEAN);
    }

    @Override
    public void unparse(SqlWriter writer, SqlCall call, int leftPrec, int rightPrec) {
      if (call.operandCount() == 1) {
        call.operand(0).unparse(writer, getLeftPrec(), getRightPrec());
      } else {
        // `(a, b) IN (…)` — a row constructor on the left, which is how both DuckDB and
        // PostgreSQL spell a membership over a tuple (F50).
        SqlWriter.Frame tuple = writer.startList("(", ")");
        for (SqlNode column : call.getOperandList()) {
          writer.sep(",");
          column.unparse(writer, 0, 0);
        }
        writer.endList(tuple);
      }
      writer.keyword("IN");
      SqlWriter.Frame outer = writer.startList("(", ")");
      if (rows) {
        // `x IN (VALUES (1), (2))` is a join against an inline relation in every dialect Chalk
        // ships a profile for, and is what makes the source do the work rather than filter for it.
        writer.keyword("VALUES");
        SqlWriter.Frame row = writer.startList("(", ")");
        writer.dynamicParam(0);
        writer.endList(row);
      } else {
        writer.dynamicParam(0);
      }
      writer.endList(outer);
    }
  }
}
