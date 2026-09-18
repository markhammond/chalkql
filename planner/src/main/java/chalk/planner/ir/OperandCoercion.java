package chalk.planner.ir;

import chalk.ir.v1.Cast;
import chalk.ir.v1.CastFailure;
import chalk.ir.v1.Expr;
import chalk.ir.v1.FunctionId;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import java.util.ArrayList;
import java.util.EnumSet;
import java.util.List;
import java.util.Set;

/**
 * Operand homogeneity, I-IR-2 (docs/design/03-planner.md §5.3).
 *
 * <p>Calcite's validator already coerces most cases; this pass makes the guarantee unconditional, so
 * the executor's kernels can be closed over a single type at plan compilation and never need a
 * mixed-type path. {@code PlanValidator} on the client checks the same rule, so a gap here fails
 * loudly at plan receipt rather than quietly at run time.
 */
public final class OperandCoercion {
  private OperandCoercion() {}

  /** Calls whose value operands must share a kind. */
  private static final Set<FunctionId> HOMOGENEOUS =
      EnumSet.of(
          FunctionId.FUNCTION_ID_EQ,
          FunctionId.FUNCTION_ID_NE,
          FunctionId.FUNCTION_ID_LT,
          FunctionId.FUNCTION_ID_LE,
          FunctionId.FUNCTION_ID_GT,
          FunctionId.FUNCTION_ID_GE,
          FunctionId.FUNCTION_ID_IS_DISTINCT_FROM,
          FunctionId.FUNCTION_ID_IS_NOT_DISTINCT_FROM,
          FunctionId.FUNCTION_ID_ADD,
          FunctionId.FUNCTION_ID_SUBTRACT,
          FunctionId.FUNCTION_ID_MULTIPLY,
          FunctionId.FUNCTION_ID_DIVIDE,
          FunctionId.FUNCTION_ID_MODULUS,
          FunctionId.FUNCTION_ID_POWER,
          FunctionId.FUNCTION_ID_NULLIF,
          FunctionId.FUNCTION_ID_COALESCE);

  /**
   * Widening order for the numeric kinds. Anything outside this list is only compatible with itself,
   * which is what makes a STRING/I64 comparison an error rather than a silent conversion.
   */
  private static final List<TypeKind> NUMERIC_WIDTH =
      List.of(
          TypeKind.TYPE_KIND_I8,
          TypeKind.TYPE_KIND_I16,
          TypeKind.TYPE_KIND_I32,
          TypeKind.TYPE_KIND_I64,
          TypeKind.TYPE_KIND_DECIMAL,
          TypeKind.TYPE_KIND_FP32,
          TypeKind.TYPE_KIND_FP64);

  /** Coerces a call's arguments if the function requires homogeneous operands. */
  public static List<Expr> harmonise(FunctionId function, List<Expr> args) {
    if (!HOMOGENEOUS.contains(function) || args.size() < 2) {
      return args;
    }
    // Temporal arithmetic against an interval is heterogeneous by definition
    // (TIMESTAMP + INTERVAL_DAY -> TIMESTAMP), so it is exempt (docs/design/02-ir.md §6).
    if (args.stream().anyMatch(a -> isInterval(kindOf(a)))) {
      return args;
    }
    Type target = leastRestrictive(args);
    return target == null ? args : harmoniseTo(target, args);
  }

  /** Arithmetic, whose result must be the kind its harmonised operands share. */
  private static final Set<FunctionId> ARITHMETIC =
      EnumSet.of(
          FunctionId.FUNCTION_ID_ADD,
          FunctionId.FUNCTION_ID_SUBTRACT,
          FunctionId.FUNCTION_ID_MULTIPLY,
          FunctionId.FUNCTION_ID_DIVIDE,
          FunctionId.FUNCTION_ID_MODULUS);

  /**
   * The type an arithmetic call produces once its operands have been harmonised.
   *
   * <p><b>V52, ADR 0024.</b> Calcite infers {@code MOD}'s type from its <em>second</em> argument —
   * {@code SqlStdOperatorTable.MOD} returns {@code ARG1_NULLABLE} — so {@code MOD(BIGINT, INTEGER)}
   * is an INTEGER while {@link #harmonise} widens both operands to BIGINT. The executor closes its
   * kernel over one type at compilation (I-IR-2's whole purpose) and takes it from the result, so it
   * read a 64-bit column as 32-bit lanes: wrong values, and a zero divisor out of the high half.
   * Every other arithmetic operator already gets the wider type from Calcite, which is why nothing
   * but MOD ever showed it.
   *
   * <p>Widening the result rather than narrowing an operand is the only safe direction: {@code a % b}
   * is bounded by {@code |b|} and always fits the wider type, where a cast of {@code a} down to the
   * narrower one could overflow. The call is then cast back to what the row type declares, which is
   * exact for the same reason — see {@link #narrowed}.
   */
  public static Type resultOf(FunctionId function, Type declared, List<Expr> args) {
    if (!ARITHMETIC.contains(function) || args.size() < 2) {
      return declared;
    }

    TypeKind operands = kindOf(args.get(0));
    if (operands != kindOf(args.get(1))) {
      return declared;
    }

    int declaredWidth = NUMERIC_WIDTH.indexOf(declared.getKind());
    int operandWidth = NUMERIC_WIDTH.indexOf(operands);
    if (declaredWidth < 0 || operandWidth <= declaredWidth) {
      return declared;
    }

    return declared.toBuilder()
        .setKind(operands)
        .setPrecision(args.get(0).getType().getPrecision())
        .setScale(args.get(0).getType().getScale())
        .build();
  }

  /**
   * The call, cast back to what the row type declares if {@link #resultOf} widened it (V52). The
   * narrowing is exact: the widening only ever happens for {@code MOD}, whose value is bounded by
   * its second operand's original width.
   */
  public static Expr narrowed(Expr call, Type declared) {
    return compatible(call.getType(), declared) ? call : coerce(call, declared);
  }

  /** Coerces every expression to the target's kind, scale and precision. */
  public static List<Expr> harmoniseTo(Type target, List<Expr> exprs) {
    List<Expr> result = new ArrayList<>(exprs.size());
    for (Expr expr : exprs) {
      result.add(coerce(expr, target));
    }
    return result;
  }

  private static Expr coerce(Expr expr, Type target) {
    if (!expr.hasType() || compatible(expr.getType(), target)) {
      return expr;
    }
    Type castType =
        target.toBuilder().setNullable(expr.getType().getNullable() || target.getNullable()).build();
    return Expr.newBuilder()
        .setType(castType)
        .setCast(Cast.newBuilder().setInput(expr).setOnFailure(CastFailure.CAST_FAILURE_ERROR))
        .build();
  }

  /** Nullability may differ; kind, DECIMAL scale and temporal precision may not (I-IR-2). */
  private static boolean compatible(Type left, Type right) {
    if (left.getKind() != right.getKind()) {
      return false;
    }
    if (left.getKind() == TypeKind.TYPE_KIND_DECIMAL) {
      return left.getScale() == right.getScale() && left.getPrecision() == right.getPrecision();
    }
    if (isTemporal(left.getKind())) {
      return left.getPrecision() == right.getPrecision();
    }
    return true;
  }

  /**
   * The type every operand can widen to, or null when they already agree or cannot be reconciled —
   * in which case the operands are left alone and {@code PlanValidator} reports the real problem
   * with the expression's path, rather than this pass inventing a conversion.
   */
  private static Type leastRestrictive(List<Expr> args) {
    Type widest = args.get(0).getType();
    for (Expr arg : args) {
      Type type = arg.getType();
      if (compatible(widest, type)) {
        continue;
      }
      if (type.getKind() == widest.getKind()) {
        // Same kind, different scale or precision: take the wider of each.
        widest =
            widest.toBuilder()
                .setPrecision(Math.max(widest.getPrecision(), type.getPrecision()))
                .setScale(Math.max(widest.getScale(), type.getScale()))
                .build();
        continue;
      }
      int left = NUMERIC_WIDTH.indexOf(widest.getKind());
      int right = NUMERIC_WIDTH.indexOf(type.getKind());
      if (left < 0 || right < 0) {
        return null;
      }
      widest = right > left ? merge(type, widest) : merge(widest, type);
    }
    return widest;
  }

  /** Widens {@code wider} enough to hold {@code narrower}; only DECIMAL carries a scale. */
  private static Type merge(Type wider, Type narrower) {
    if (wider.getKind() != TypeKind.TYPE_KIND_DECIMAL) {
      return wider;
    }
    int scale = Math.max(wider.getScale(), narrower.getScale());
    int precision = Math.max(wider.getPrecision(), narrower.getPrecision());
    return wider.toBuilder().setPrecision(Math.max(precision, scale)).setScale(scale).build();
  }

  private static TypeKind kindOf(Expr expr) {
    return expr.hasType() ? expr.getType().getKind() : TypeKind.TYPE_KIND_UNSPECIFIED;
  }

  private static boolean isInterval(TypeKind kind) {
    return kind == TypeKind.TYPE_KIND_INTERVAL_DAY || kind == TypeKind.TYPE_KIND_INTERVAL_YEAR;
  }

  private static boolean isTemporal(TypeKind kind) {
    return kind == TypeKind.TYPE_KIND_DATE
        || kind == TypeKind.TYPE_KIND_TIME
        || kind == TypeKind.TYPE_KIND_TIMESTAMP
        || kind == TypeKind.TYPE_KIND_TIMESTAMP_TZ;
  }
}
