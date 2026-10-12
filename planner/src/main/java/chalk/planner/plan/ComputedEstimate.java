package chalk.planner.plan;

import chalk.ir.v1.Literal;
import java.math.BigDecimal;
import java.time.Instant;
import java.time.ZoneOffset;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexDynamicParam;
import org.apache.calcite.rex.RexExecutor;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.util.DateString;
import org.apache.calcite.util.TimestampString;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * What a bound computed once per execution is expected to be worth, for estimation only (D337,
 * F178). A bound such as {@code CURRENT_TIMESTAMP - INTERVAL '5' MINUTE} has no value at planning
 * time, so a range on it was guessed to keep half the rows, and a recent window over a long table
 * filtered a full scan. As PostgreSQL does, the planner computes such a bound with the current time
 * as it plans, and with each parameter's hint where the request gave one, and places the result on
 * the column's statistics like a literal.
 *
 * <p>Only the estimate reads this value; the plan computes the bound at every execution. A plan
 * reused later keeps the choice it was planned with, which for a window that moves with the clock
 * is the same choice.
 */
final class ComputedEstimate {
  private ComputedEstimate() {}

  /** The value, as a literal of {@code value}'s type, or null when it cannot be known. */
  static @Nullable RexLiteral of(RexNode value, ParameterHints hints, RelOptCluster cluster) {
    if (!IndexMatcher.isComputed(value)) {
      return null;
    }

    RexBuilder rexBuilder = cluster.getRexBuilder();
    Instant now = Instant.now();
    boolean[] unknown = {false};
    RexNode known =
        value.accept(
            new RexShuttle() {
              @Override
              public RexNode visitDynamicParam(RexDynamicParam parameter) {
                RexNode hinted = hinted(hints.at(parameter.getIndex()), parameter.getType(), rexBuilder);
                if (hinted == null) {
                  unknown[0] = true;
                  return parameter;
                }
                return hinted;
              }

              @Override
              public RexNode visitCall(RexCall call) {
                RexNode clock = clock(call, now, rexBuilder);
                return clock != null ? clock : super.visitCall(call);
              }
            });
    if (unknown[0]) {
      return null;
    }

    RexExecutor executor = cluster.getPlanner().getExecutor();
    List<RexNode> reduced = new ArrayList<>(1);
    try {
      (executor == null ? RexUtil.EXECUTOR : executor).reduce(rexBuilder, List.of(known), reduced);
    } catch (RuntimeException e) {
      return null;
    }
    if (reduced.size() != 1) {
      return null;
    }

    // The executor may leave a cast around the value it computed, such as CAST(9000:BIGINT):BIGINT;
    // the value inside is the estimate.
    RexNode result = reduced.get(0);
    while (result instanceof RexCall cast && cast.getKind() == org.apache.calcite.sql.SqlKind.CAST) {
      result = cast.getOperands().get(0);
    }
    return result instanceof RexLiteral literal && !literal.isNull() ? literal : null;
  }

  /** The current time as a literal of the call's type, read as a UTC wall clock; null for any other call. */
  private static @Nullable RexNode clock(RexCall call, Instant now, RexBuilder rexBuilder) {
    return switch (call.getOperator().getName()) {
      case "CURRENT_TIMESTAMP", "LOCALTIMESTAMP" ->
          rexBuilder.makeTimestampLiteral(
              TimestampString.fromMillisSinceEpoch(now.toEpochMilli()),
              call.getType().getPrecision());
      case "CURRENT_DATE" ->
          rexBuilder.makeDateLiteral(
              DateString.fromDaysSinceEpoch(
                  (int) now.atOffset(ZoneOffset.UTC).toLocalDate().toEpochDay()));
      default -> null;
    };
  }

  /** A parameter's hint as a literal of the parameter's type, for the numeric and temporal kinds. */
  private static @Nullable RexNode hinted(
      ParameterHints.@Nullable Hint hint, RelDataType type, RexBuilder rexBuilder) {
    if (hint == null || hint.isNull()) {
      return null;
    }

    Literal literal = hint.literal();
    return switch (literal.getValueCase()) {
      case I8_VALUE, I16_VALUE, I32_VALUE, I64_VALUE, DECIMAL_VALUE, FP32_VALUE, FP64_VALUE -> {
        BigDecimal number = number(literal, hint.type());
        yield number == null ? null : rexBuilder.makeExactLiteral(number, type);
      }
      case DATE_VALUE ->
          rexBuilder.makeDateLiteral(DateString.fromDaysSinceEpoch(literal.getDateValue()));
      case TIMESTAMP_VALUE ->
          rexBuilder.makeTimestampLiteral(
              TimestampString.fromMillisSinceEpoch(
                  millis(literal.getTimestampValue(), hint.type().getPrecision())),
              type.getPrecision());
      default -> null;
    };
  }

  private static @Nullable BigDecimal number(Literal literal, chalk.ir.v1.Type type) {
    return switch (literal.getValueCase()) {
      case I8_VALUE -> BigDecimal.valueOf(literal.getI8Value());
      case I16_VALUE -> BigDecimal.valueOf(literal.getI16Value());
      case I32_VALUE -> BigDecimal.valueOf(literal.getI32Value());
      case I64_VALUE -> BigDecimal.valueOf(literal.getI64Value());
      case FP32_VALUE -> BigDecimal.valueOf(literal.getFp32Value());
      case FP64_VALUE -> BigDecimal.valueOf(literal.getFp64Value());
      case DECIMAL_VALUE ->
          new BigDecimal(
              new java.math.BigInteger(literal.getDecimalValue().getUnscaled().toByteArray()),
              type.getScale());
      default -> null;
    };
  }

  /** A TIMESTAMP's units, at its precision, as milliseconds since 1970. */
  private static long millis(long units, int precision) {
    long perSecond = chalk.planner.ir.LiteralConverter.unitsPerSecond(precision);
    return perSecond >= 1000 ? units / (perSecond / 1000) : units * (1000 / perSecond);
  }
}
