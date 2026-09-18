package chalk.planner.entitlement;

import chalk.ir.v1.Literal;
import chalk.ir.v1.Type;
import java.math.BigDecimal;
import java.math.BigInteger;
import java.util.concurrent.TimeUnit;
import org.apache.calcite.avatica.util.DateTimeUtils;
import org.apache.calcite.sql.SqlLiteral;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.util.DateString;
import org.apache.calcite.util.TimeString;
import org.apache.calcite.util.TimestampString;

/**
 * An IR literal as a SQL parse-tree literal, for the context fold (docs/design/16-entitlements.md
 * §2).
 *
 * <p>The substitution happens on the parse tree rather than on Rex, so what a bound value has to
 * become is a {@code SqlLiteral}: the validator then types it in the expression it sits in, and
 * coercion against the column it is compared with is the ordinary one. Going through Rex instead
 * would mean building expressions over a type factory before the statement that will hold them
 * exists, which is the mistake ADR 0025's second finding is about.
 */
public final class ContextLiterals {
  private ContextLiterals() {}

  /**
   * The literal as a parse-tree node of the type the IR gives it.
   *
   * @throws IllegalArgumentException for a kind that has no SQL literal spelling
   */
  public static SqlNode toSqlNode(Literal literal, Type type, String what, SqlParserPos pos) {
    if (literal.getValueCase() == Literal.ValueCase.IS_NULL && literal.getIsNull()) {
      return SqlLiteral.createNull(pos);
    }

    return switch (literal.getValueCase()) {
      case BOOL_VALUE -> SqlLiteral.createBoolean(literal.getBoolValue(), pos);
      case I8_VALUE -> exact(literal.getI8Value(), pos);
      case I16_VALUE -> exact(literal.getI16Value(), pos);
      case I32_VALUE -> exact(literal.getI32Value(), pos);
      case I64_VALUE -> exact(literal.getI64Value(), pos);
      case FP32_VALUE -> approx(literal.getFp32Value(), pos);
      case FP64_VALUE -> approx(literal.getFp64Value(), pos);
      case STRING_VALUE -> SqlLiteral.createCharString(literal.getStringValue(), pos);
      case BINARY_VALUE ->
          SqlLiteral.createBinaryString(literal.getBinaryValue().toByteArray(), pos);
      case DATE_VALUE ->
          SqlLiteral.createDate(
              new DateString(DateTimeUtils.unixDateToString(literal.getDateValue())), pos);
      case TIME_VALUE -> time(literal.getTimeValue(), type, pos);
      case TIMESTAMP_VALUE, TIMESTAMP_TZ_VALUE -> timestamp(literal, type, pos);
      case DECIMAL_VALUE ->
          SqlLiteral.createExactNumeric(
              new BigDecimal(
                      new BigInteger(
                          reversed(literal.getDecimalValue().getUnscaled().toByteArray())),
                      type.getScale())
                  .toPlainString(),
              pos);
      default ->
          throw new IllegalArgumentException(
              what + " has type " + literal.getValueCase()
                  + ", which has no SQL literal spelling. A context binds BOOL, the integer and "
                  + "floating kinds, DECIMAL, STRING, BINARY, DATE, TIME and the two TIMESTAMP "
                  + "kinds; UUID, the intervals and LIST are not bindable as context values in this "
                  + "step.");
    };
  }

  private static SqlNode exact(long value, SqlParserPos pos) {
    return SqlLiteral.createExactNumeric(Long.toString(value), pos);
  }

  private static SqlNode approx(double value, SqlParserPos pos) {
    if (Double.isNaN(value) || Double.isInfinite(value)) {
      throw new IllegalArgumentException(
          "a context scalar is " + value + ", which SQL has no literal for");
    }
    return SqlLiteral.createApproxNumeric(new BigDecimal(value).toString(), pos);
  }

  private static SqlNode time(long micros, Type type, SqlParserPos pos) {
    long millis = TimeUnit.MICROSECONDS.toMillis(micros);
    TimeString text =
        new TimeString(DateTimeUtils.unixTimeToString((int) millis))
            .withMillis((int) (millis % DateTimeUtils.MILLIS_PER_SECOND));
    return SqlLiteral.createTime(text, precision(type, 6), pos);
  }

  /**
   * The IR's timestamp unit comes from the type's precision — milliseconds up to 3, microseconds up
   * to 6, nanoseconds above — so the count is scaled to nanoseconds once and {@link TimestampString}
   * renders whatever fraction the precision asks for.
   */
  private static SqlNode timestamp(Literal literal, Type type, SqlParserPos pos) {
    long value =
        literal.getValueCase() == Literal.ValueCase.TIMESTAMP_VALUE
            ? literal.getTimestampValue()
            : literal.getTimestampTzValue();
    int precision = precision(type, 6);
    long nanosPerUnit =
        precision <= 3 ? 1_000_000L : precision <= 6 ? 1_000L : 1L;
    long nanos = value * nanosPerUnit;
    long seconds = Math.floorDiv(nanos, 1_000_000_000L);
    int fraction = (int) Math.floorMod(nanos, 1_000_000_000L);
    TimestampString text =
        new TimestampString(DateTimeUtils.unixTimestampToString(seconds * 1000L))
            .withNanos(fraction);
    return SqlLiteral.createTimestamp(
        literal.getValueCase() == Literal.ValueCase.TIMESTAMP_VALUE
            ? org.apache.calcite.sql.type.SqlTypeName.TIMESTAMP
            : org.apache.calcite.sql.type.SqlTypeName.TIMESTAMP_WITH_LOCAL_TIME_ZONE,
        text,
        precision,
        pos);
  }

  private static int precision(Type type, int max) {
    int precision = type.getPrecision();
    return precision < 0 ? 0 : Math.min(precision, max);
  }

  /** The IR's decimals are little-endian; {@link BigInteger} is big-endian. */
  private static byte[] reversed(byte[] bytes) {
    byte[] out = new byte[bytes.length];
    for (int i = 0; i < bytes.length; i++) {
      out[i] = bytes[bytes.length - 1 - i];
    }
    return out;
  }
}
