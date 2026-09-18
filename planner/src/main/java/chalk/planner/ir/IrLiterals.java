package chalk.planner.ir;

import chalk.ir.v1.Expr;
import chalk.ir.v1.Literal;
import chalk.planner.UnsupportedFeatureException;
import java.math.BigDecimal;
import java.math.BigInteger;
import java.nio.ByteOrder;
import org.apache.calcite.avatica.util.DateTimeUtils;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.util.DateString;
import org.apache.calcite.util.NlsString;
import org.apache.calcite.util.TimestampString;

/**
 * The one direction the planner does not otherwise travel: an IR {@code Literal} back into a
 * {@code RexNode}.
 *
 * <p>Only the catalog needs it, and only for a partition's value (D106): a partitioning descriptor
 * says "this partition holds the rows whose key is 'BTCUSDT'", and that has to become a
 * {@code RexLiteral} the pruning rule can compare a predicate against. Everything else in the
 * planner reads Rex and writes IR, which is why this is a small, deliberately narrow class rather
 * than the mirror of {@link RexToIr}.
 */
public final class IrLiterals {
  private IrLiterals() {}

  /** The literal as a {@code RexNode} of {@code type}. */
  public static RexNode literal(RexBuilder rex, Expr expr, RelDataType type) {
    if (expr.getKindCase() != Expr.KindCase.LITERAL) {
      throw new UnsupportedFeatureException(
          "a partition value that is not a literal",
          "A partition's value must be a constant of the partition column's type "
              + "(docs/design/20-m5-federation.md §3).");
    }

    Literal value = expr.getLiteral();
    return switch (value.getValueCase()) {
      case IS_NULL -> rex.makeNullLiteral(type);
      case BOOL_VALUE -> rex.makeLiteral(value.getBoolValue());
      case I8_VALUE -> exact(rex, value.getI8Value(), type);
      case I16_VALUE -> exact(rex, value.getI16Value(), type);
      case I32_VALUE -> exact(rex, value.getI32Value(), type);
      case I64_VALUE -> exact(rex, value.getI64Value(), type);
      case FP32_VALUE -> rex.makeApproxLiteral(BigDecimal.valueOf(value.getFp32Value()), type);
      case FP64_VALUE -> rex.makeApproxLiteral(BigDecimal.valueOf(value.getFp64Value()), type);
      case STRING_VALUE ->
          rex.makeLiteral(
              new NlsString(value.getStringValue(), type.getCharset() == null
                      ? null
                      : type.getCharset().name(), type.getCollation()),
              type);
      case DATE_VALUE ->
          rex.makeDateLiteral(
              new DateString(
                  DateTimeUtils.unixDateToString(value.getDateValue())));
      case TIMESTAMP_VALUE ->
          rex.makeTimestampLiteral(
              TimestampString.fromMillisSinceEpoch(
                  micros(value.getTimestampValue(), type) / 1_000L),
              type.getPrecision());
      case DECIMAL_VALUE -> rex.makeExactLiteral(decimal(value, type), type);
      default ->
          throw new UnsupportedFeatureException(
              "a partition value of kind " + value.getValueCase(),
              "The partition column's type has no literal form the planner can compare against.");
    };
  }

  private static RexNode exact(RexBuilder rex, long value, RelDataType type) {
    return rex.makeExactLiteral(BigDecimal.valueOf(value), type);
  }

  private static long micros(long raw, RelDataType type) {
    // The IR carries a timestamp in the units the type's precision names; Calcite's literal wants
    // microseconds. Precision 9 is nanoseconds, 6 microseconds, 3 milliseconds, 0 seconds.
    return switch (type.getPrecision()) {
      case 9 -> raw / 1_000L;
      case 6 -> raw;
      case 3 -> raw * 1_000L;
      default -> raw * 1_000_000L;
    };
  }

  private static BigDecimal decimal(Literal value, RelDataType type) {
    byte[] unscaled = value.getDecimalValue().getUnscaled().toByteArray();
    byte[] bigEndian = new byte[unscaled.length];
    for (int i = 0; i < unscaled.length; i++) {
      bigEndian[i] = unscaled[unscaled.length - 1 - i];
    }
    if (ByteOrder.nativeOrder() == null) {
      throw new IllegalStateException("unreachable");
    }
    return new BigDecimal(new BigInteger(bigEndian), type.getScale());
  }
}
