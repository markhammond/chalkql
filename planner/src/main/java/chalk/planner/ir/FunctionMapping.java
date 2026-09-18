package chalk.planner.ir;

import chalk.ir.v1.AggregateFunctionId;
import chalk.ir.v1.FunctionId;
import chalk.ir.v1.WindowFunctionId;
import chalk.planner.UnsupportedFeatureException;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.sql.SqlAggFunction;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlOperator;

/**
 * Calcite operator → IR {@link FunctionId}. The IR names functions by a stable numeric id rather
 * than a string, so this table is the one place the two vocabularies meet (docs/design/02-ir.md §9).
 *
 * <p>Anything not here is {@link UnsupportedFeatureException} naming the operator, never a guess: a
 * function mapped to the wrong id is a wrong-answer bug the executor cannot detect.
 */
public final class FunctionMapping {
  private FunctionMapping() {}

  /** By {@link SqlKind}, which is how Calcite identifies its built-in operators. */
  private static final Map<SqlKind, FunctionId> BY_KIND = byKind();

  /** By operator name, for the many operators that share {@code SqlKind.OTHER_FUNCTION}. */
  private static final Map<String, FunctionId> BY_NAME = byName();

  private static Map<SqlKind, FunctionId> byKind() {
    Map<SqlKind, FunctionId> map = new LinkedHashMap<>();
    map.put(SqlKind.EQUALS, FunctionId.FUNCTION_ID_EQ);
    map.put(SqlKind.NOT_EQUALS, FunctionId.FUNCTION_ID_NE);
    map.put(SqlKind.LESS_THAN, FunctionId.FUNCTION_ID_LT);
    map.put(SqlKind.LESS_THAN_OR_EQUAL, FunctionId.FUNCTION_ID_LE);
    map.put(SqlKind.GREATER_THAN, FunctionId.FUNCTION_ID_GT);
    map.put(SqlKind.GREATER_THAN_OR_EQUAL, FunctionId.FUNCTION_ID_GE);
    map.put(SqlKind.IS_DISTINCT_FROM, FunctionId.FUNCTION_ID_IS_DISTINCT_FROM);
    map.put(SqlKind.IS_NOT_DISTINCT_FROM, FunctionId.FUNCTION_ID_IS_NOT_DISTINCT_FROM);

    map.put(SqlKind.AND, FunctionId.FUNCTION_ID_AND);
    map.put(SqlKind.OR, FunctionId.FUNCTION_ID_OR);
    map.put(SqlKind.NOT, FunctionId.FUNCTION_ID_NOT);
    map.put(SqlKind.IS_NULL, FunctionId.FUNCTION_ID_IS_NULL);
    map.put(SqlKind.IS_NOT_NULL, FunctionId.FUNCTION_ID_IS_NOT_NULL);
    map.put(SqlKind.IS_TRUE, FunctionId.FUNCTION_ID_IS_TRUE);
    map.put(SqlKind.IS_NOT_TRUE, FunctionId.FUNCTION_ID_IS_NOT_TRUE);
    map.put(SqlKind.IS_FALSE, FunctionId.FUNCTION_ID_IS_FALSE);
    map.put(SqlKind.IS_NOT_FALSE, FunctionId.FUNCTION_ID_IS_NOT_FALSE);

    map.put(SqlKind.PLUS, FunctionId.FUNCTION_ID_ADD);
    map.put(SqlKind.MINUS, FunctionId.FUNCTION_ID_SUBTRACT);
    map.put(SqlKind.TIMES, FunctionId.FUNCTION_ID_MULTIPLY);
    map.put(SqlKind.DIVIDE, FunctionId.FUNCTION_ID_DIVIDE);
    map.put(SqlKind.MOD, FunctionId.FUNCTION_ID_MODULUS);
    map.put(SqlKind.MINUS_PREFIX, FunctionId.FUNCTION_ID_NEGATE);

    map.put(SqlKind.LIKE, FunctionId.FUNCTION_ID_LIKE);
    map.put(SqlKind.EXTRACT, FunctionId.FUNCTION_ID_EXTRACT);
    map.put(SqlKind.COALESCE, FunctionId.FUNCTION_ID_COALESCE);
    map.put(SqlKind.NULLIF, FunctionId.FUNCTION_ID_NULLIF);
    map.put(SqlKind.POSITION, FunctionId.FUNCTION_ID_POSITION);
    map.put(SqlKind.TRIM, FunctionId.FUNCTION_ID_TRIM);

    // Lists (D58). ITEM is also how Calcite spells a MAP lookup and a ROW field access; the operand
    // types decide, and RelToIr's type mapping refuses anything but a LIST before this is reached.
    map.put(SqlKind.ITEM, FunctionId.FUNCTION_ID_ITEM);
    return Map.copyOf(map);
  }

  private static Map<String, FunctionId> byName() {
    Map<String, FunctionId> map = new LinkedHashMap<>();
    map.put("ABS", FunctionId.FUNCTION_ID_ABS);
    map.put("ROUND", FunctionId.FUNCTION_ID_ROUND);
    map.put("POWER", FunctionId.FUNCTION_ID_POWER);
    map.put("SQRT", FunctionId.FUNCTION_ID_SQRT);
    map.put("LN", FunctionId.FUNCTION_ID_LN);
    map.put("LOG10", FunctionId.FUNCTION_ID_LOG10);
    map.put("EXP", FunctionId.FUNCTION_ID_EXP);
    map.put("SIGN", FunctionId.FUNCTION_ID_SIGN);

    map.put("||", FunctionId.FUNCTION_ID_CONCAT);
    map.put("CONCAT", FunctionId.FUNCTION_ID_CONCAT);
    map.put("UPPER", FunctionId.FUNCTION_ID_UPPER);
    map.put("LOWER", FunctionId.FUNCTION_ID_LOWER);
    map.put("SUBSTRING", FunctionId.FUNCTION_ID_SUBSTRING);
    map.put("CHAR_LENGTH", FunctionId.FUNCTION_ID_CHAR_LENGTH);
    map.put("CHARACTER_LENGTH", FunctionId.FUNCTION_ID_CHAR_LENGTH);
    map.put("LTRIM", FunctionId.FUNCTION_ID_LTRIM);
    map.put("RTRIM", FunctionId.FUNCTION_ID_RTRIM);
    map.put("REPLACE", FunctionId.FUNCTION_ID_REPLACE);
    map.put("STARTS_WITH", FunctionId.FUNCTION_ID_STARTS_WITH);
    map.put("ENDS_WITH", FunctionId.FUNCTION_ID_ENDS_WITH);

    map.put("CURRENT_TIMESTAMP", FunctionId.FUNCTION_ID_CURRENT_TIMESTAMP);
    map.put("CURRENT_DATE", FunctionId.FUNCTION_ID_CURRENT_DATE);
    map.put("TIMESTAMPDIFF", FunctionId.FUNCTION_ID_TIMESTAMP_DIFF);
    map.put("TIMESTAMPADD", FunctionId.FUNCTION_ID_TIMESTAMP_ADD);
    // Chalk's own, registered in the planner's operator table (D52).
    map.put("TIME_BUCKET", FunctionId.FUNCTION_ID_TIME_BUCKET);

    // Lists (D58). CARDINALITY is standard; ARRAY_TO_STRING lives in the BIG_QUERY library, which a
    // request asks for through PlannerOptions.libraries (D60).
    map.put("CARDINALITY", FunctionId.FUNCTION_ID_CARDINALITY);
    map.put("ARRAY_TO_STRING", FunctionId.FUNCTION_ID_ARRAY_TO_STRING);

    // The entitlement layer's two (step 26, 16-entitlements.md §4), Chalk's own like TIME_BUCKET.
    map.put("FINGERPRINT", FunctionId.FUNCTION_ID_FINGERPRINT);
    map.put("PRESENT", FunctionId.FUNCTION_ID_PRESENT);
    return Map.copyOf(map);
  }

  /**
   * The IR id for a scalar operator.
   *
   * @param temporalUnitArgument true when the call carries a {@code FLAG(unit)} argument, which is
   *     what distinguishes {@code FLOOR(ts TO HOUR)} from numeric {@code FLOOR(x)}.
   */
  public static FunctionId scalar(SqlOperator operator, boolean temporalUnitArgument) {
    if (operator.getKind() == SqlKind.FLOOR) {
      return temporalUnitArgument
          ? FunctionId.FUNCTION_ID_FLOOR_TEMPORAL
          : FunctionId.FUNCTION_ID_FLOOR;
    }
    if (operator.getKind() == SqlKind.CEIL) {
      return temporalUnitArgument
          ? FunctionId.FUNCTION_ID_CEIL_TEMPORAL
          : FunctionId.FUNCTION_ID_CEIL;
    }

    FunctionId byKind = BY_KIND.get(operator.getKind());
    if (byKind != null) {
      return byKind;
    }
    FunctionId byName = BY_NAME.get(operator.getName().toUpperCase(Locale.ROOT));
    if (byName != null) {
      return byName;
    }
    throw new UnsupportedFeatureException(
        "function " + operator.getName() + " (" + operator.getKind() + ")",
        "docs/design/02-ir.md §6 lists the functions the IR carries; this is not one of them.");
  }

  /**
   * The IR id for a scalar call, or null when the IR has none for it. The question the pushdown gate
   * asks (D82): "is this a function the source could be told about at all?" — where an unmapped
   * function is a plain no rather than a planning failure, because the call is perfectly runnable
   * locally.
   */
  public static @org.checkerframework.checker.nullness.qual.Nullable FunctionId tryFunctionOf(
      RexCall call) {
    boolean temporalUnit =
        call.getOperands().size() > 1
            && call.getOperands().get(call.getOperands().size() - 1).getKind() == SqlKind.LITERAL
            && call.getOperands().get(call.getOperands().size() - 1).getType().getSqlTypeName()
                == org.apache.calcite.sql.type.SqlTypeName.SYMBOL;
    try {
      return scalar(call.getOperator(), temporalUnit);
    } catch (UnsupportedFeatureException unmapped) {
      return null;
    }
  }

  /** The aggregate twin of {@link #tryFunctionOf}: null rather than a throw for an unmapped one. */
  public static @org.checkerframework.checker.nullness.qual.Nullable AggregateFunctionId
      tryAggregateOf(org.apache.calcite.rel.core.AggregateCall call) {
    try {
      return aggregate(call.getAggregation());
    } catch (UnsupportedFeatureException unmapped) {
      return null;
    }
  }

  /** The IR id for an aggregate function. */
  public static AggregateFunctionId aggregate(SqlAggFunction function) {
    // $SUM0 shares SqlKind.SUM0; COUNT, MIN, MAX and SUM are unambiguous by kind.
    return switch (function.getKind()) {
      case COUNT -> AggregateFunctionId.AGGREGATE_FUNCTION_ID_COUNT;
      case SUM -> AggregateFunctionId.AGGREGATE_FUNCTION_ID_SUM;
      case SUM0 -> AggregateFunctionId.AGGREGATE_FUNCTION_ID_SUM0;
      case MIN -> AggregateFunctionId.AGGREGATE_FUNCTION_ID_MIN;
      case MAX -> AggregateFunctionId.AGGREGATE_FUNCTION_ID_MAX;
      case AVG -> AggregateFunctionId.AGGREGATE_FUNCTION_ID_AVG;
      case ANY_VALUE -> AggregateFunctionId.AGGREGATE_FUNCTION_ID_ANY_VALUE;
      case BIT_AND -> throw unsupportedAggregate(function);
      // The holistic family (D57). Calcite maps PostgreSQL's STRING_AGG onto LISTAGG during
      // validation of a GROUP BY, but not over a window, where the kind survives; the two mean the
      // same thing, so both arrive as LISTAGG and AGGREGATE_FUNCTION_ID_STRING_AGG stays reserved.
      case PERCENTILE_CONT -> AggregateFunctionId.AGGREGATE_FUNCTION_ID_PERCENTILE_CONT;
      case PERCENTILE_DISC -> AggregateFunctionId.AGGREGATE_FUNCTION_ID_PERCENTILE_DISC;
      case LISTAGG, STRING_AGG -> AggregateFunctionId.AGGREGATE_FUNCTION_ID_LISTAGG;
      case ARRAY_AGG -> AggregateFunctionId.AGGREGATE_FUNCTION_ID_ARRAY_AGG;
      case MODE -> AggregateFunctionId.AGGREGATE_FUNCTION_ID_MODE;
      default ->
          switch (function.getName().toUpperCase(Locale.ROOT)) {
            case "BOOL_AND", "EVERY" -> AggregateFunctionId.AGGREGATE_FUNCTION_ID_BOOL_AND;
            case "BOOL_OR", "SOME" -> AggregateFunctionId.AGGREGATE_FUNCTION_ID_BOOL_OR;
            case "APPROX_COUNT_DISTINCT" ->
                AggregateFunctionId.AGGREGATE_FUNCTION_ID_APPROX_COUNT_DISTINCT;
            default -> throw unsupportedAggregate(function);
          };
    };
  }

  /**
   * The IR id for a function that only exists over a window — the ranking family and the navigation
   * family (D48). Returns null for an ordinary aggregate, which {@link #aggregate} then maps: a
   * window's {@code SUM} is the same function as a {@code GROUP BY}'s, and the IR says so by putting
   * both behind {@code WindowCall.function}.
   */
  public static WindowFunctionId windowFunction(SqlAggFunction function) {
    return switch (function.getKind()) {
      case ROW_NUMBER -> WindowFunctionId.WINDOW_FUNCTION_ID_ROW_NUMBER;
      case RANK -> WindowFunctionId.WINDOW_FUNCTION_ID_RANK;
      case DENSE_RANK -> WindowFunctionId.WINDOW_FUNCTION_ID_DENSE_RANK;
      case NTILE -> WindowFunctionId.WINDOW_FUNCTION_ID_NTILE;
      case PERCENT_RANK -> WindowFunctionId.WINDOW_FUNCTION_ID_PERCENT_RANK;
      case CUME_DIST -> WindowFunctionId.WINDOW_FUNCTION_ID_CUME_DIST;
      case LAG -> WindowFunctionId.WINDOW_FUNCTION_ID_LAG;
      case LEAD -> WindowFunctionId.WINDOW_FUNCTION_ID_LEAD;
      case FIRST_VALUE -> WindowFunctionId.WINDOW_FUNCTION_ID_FIRST_VALUE;
      case LAST_VALUE -> WindowFunctionId.WINDOW_FUNCTION_ID_LAST_VALUE;
      case NTH_VALUE -> WindowFunctionId.WINDOW_FUNCTION_ID_NTH_VALUE;
      default -> null;
    };
  }

  private static UnsupportedFeatureException unsupportedAggregate(SqlAggFunction function) {
    return new UnsupportedFeatureException(
        "aggregate " + function.getName(),
        "docs/design/02-ir.md §6 lists the aggregates the IR carries; this is not one of them.");
  }
}
