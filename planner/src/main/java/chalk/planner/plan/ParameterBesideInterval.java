package chalk.planner.plan;

import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Function;
import java.util.function.Predicate;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlDynamicParam;
import org.apache.calcite.sql.SqlIntervalLiteral;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.type.InferTypes;
import org.apache.calcite.sql.type.SqlTypeUtil;
import org.apache.calcite.sql.util.SqlBasicVisitor;
import org.apache.calcite.util.Util;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A parameter beside an interval in datetime arithmetic takes the datetime type the arithmetic
 * stands beside (F192). In {@code ts >= ? - INTERVAL '6' HOUR} Calcite types the parameter as the
 * first known operand next to it, the interval, which makes the arithmetic an interval and the
 * comparison one between a timestamp and an interval, refused. The parameter is a timestamp: the
 * type of {@code ts}.
 *
 * <p>Only where that type is known. Either the context infers a datetime for the arithmetic itself,
 * or the arithmetic is an operand of a call whose operands all take the first known one's type — a
 * comparison, {@code BETWEEN}, {@code LEAST}, {@code GREATEST} — and another operand of that call is
 * a datetime. Alone, as in {@code SELECT ? - INTERVAL '1' HOUR}, the parameter could as well be an
 * interval, and is typed as before; a declared parameter type (D337) or a {@code CAST} says which.
 * The arithmetic may be nested, as {@code (? - INTERVAL '1' DAY) + INTERVAL '2' HOUR}, and an
 * interval added may stand on either side of the {@code +}.
 *
 * <p>The plan is then the one {@code CAST(? AS TIMESTAMP(…))} gives, so where the arithmetic runs is
 * decided as it is for that: a source evaluates it only when its dialect can, and Chalk otherwise.
 */
final class ParameterBesideInterval {
  private final Map<Integer, RelDataType> types = new HashMap<>();

  /** The type this rule gave {@code node}, a parameter, or null. */
  @Nullable RelDataType typeOf(SqlNode node) {
    return node instanceof SqlDynamicParam parameter ? types.get(parameter.getIndex()) : null;
  }

  /**
   * Types the parameters of the datetime arithmetic in {@code call} where its context says what they
   * are, before the validator infers them.
   *
   * @param inferred the type the validator infers for {@code call} from its context
   * @param derive the type of a node that holds no parameter, or null where it has none
   * @param declared whether a parameter's type was declared, which no inference overrides
   */
  void infer(
      SqlCall call,
      RelDataType inferred,
      Function<SqlNode, @Nullable RelDataType> derive,
      Predicate<SqlDynamicParam> declared) {
    if (isArithmetic(call)) {
      if (SqlTypeUtil.isDatetime(inferred)) {
        assign(call, inferred, derive, declared);
      }
      return;
    }

    if (call.getOperator().getOperandTypeInference() != InferTypes.FIRST_KNOWN) {
      return;
    }

    // Nothing is derived unless an operand is arithmetic holding a parameter: a statement without
    // one is validated exactly as it was.
    List<@Nullable SqlNode> operands = call.getOperandList();
    boolean candidate = false;
    for (SqlNode operand : operands) {
      if (operand instanceof SqlCall arithmetic && isArithmetic(arithmetic) && holdsParameter(arithmetic)) {
        candidate = true;
        break;
      }
    }

    if (!candidate) {
      return;
    }

    RelDataType datetime = null;
    for (SqlNode operand : operands) {
      if (isExpression(operand) && !holdsParameter(operand)) {
        RelDataType type = derive.apply(operand);
        if (type != null && SqlTypeUtil.isDatetime(type)) {
          datetime = type;
          break;
        }
      }
    }

    if (datetime == null) {
      return;
    }

    for (SqlNode operand : operands) {
      if (operand instanceof SqlCall arithmetic && isArithmetic(arithmetic)) {
        assign(arithmetic, datetime, derive, declared);
      }
    }
  }

  /** A scalar expression, which has a type of its own: not an {@code IN} list, not a subquery. */
  private static boolean isExpression(@Nullable SqlNode node) {
    return node != null
        && !(node instanceof org.apache.calcite.sql.SqlNodeList)
        && !node.isA(SqlKind.QUERY);
  }

  /** Follows the datetime side of nested arithmetic to a parameter there, and types it. */
  private void assign(
      SqlCall arithmetic,
      RelDataType datetime,
      Function<SqlNode, @Nullable RelDataType> derive,
      Predicate<SqlDynamicParam> declared) {
    SqlNode node = arithmetic;
    while (node instanceof SqlCall call && isArithmetic(call)) {
      SqlNode left = call.operand(0);
      SqlNode right = call.operand(1);
      if (isInterval(right, derive)) {
        node = left;
      } else if (call.getKind() == SqlKind.PLUS && isInterval(left, derive)) {
        node = right;
      } else {
        return;
      }
    }

    if (node instanceof SqlDynamicParam parameter && !declared.test(parameter)) {
      types.putIfAbsent(parameter.getIndex(), datetime);
    }
  }

  private static boolean isArithmetic(SqlCall call) {
    return (call.getKind() == SqlKind.MINUS || call.getKind() == SqlKind.PLUS)
        && call.operandCount() == 2;
  }

  private static boolean isInterval(SqlNode node, Function<SqlNode, @Nullable RelDataType> derive) {
    if (node instanceof SqlIntervalLiteral) {
      return true;
    }
    if (!isExpression(node) || holdsParameter(node)) {
      return false;
    }
    RelDataType type = derive.apply(node);
    return type != null && SqlTypeUtil.isInterval(type);
  }

  private static boolean holdsParameter(SqlNode node) {
    try {
      node.accept(
          new SqlBasicVisitor<Void>() {
            @Override
            public Void visit(SqlDynamicParam parameter) {
              throw Util.FoundOne.NULL;
            }
          });
      return false;
    } catch (Util.FoundOne found) {
      return true;
    }
  }
}
