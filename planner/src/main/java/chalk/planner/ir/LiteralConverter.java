package chalk.planner.ir;

import chalk.ir.v1.DecimalValue;
import chalk.ir.v1.Literal;
import chalk.ir.v1.Type;
import chalk.planner.UnsupportedFeatureException;
import com.google.protobuf.ByteString;
import java.math.BigDecimal;
import java.math.BigInteger;
import java.math.RoundingMode;
import java.time.LocalDateTime;
import java.time.ZoneOffset;
import java.time.format.DateTimeFormatter;
import java.time.format.DateTimeFormatterBuilder;
import java.time.temporal.ChronoField;
import java.util.UUID;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.util.TimestampString;

/**
 * {@code RexLiteral} → IR {@code Literal}, at the precision and scale the IR type carries
 * (docs/design/03-planner.md §5.2, docs/design/02-ir.md §3).
 *
 * <p>Calcite's own storage is lossier than the IR in two places, both handled here: a TIMESTAMP
 * literal's {@code getValueAs(Long.class)} is milliseconds, so nanosecond literals go through {@link
 * TimestampString}; and day-time intervals are milliseconds, so they are rescaled to microseconds.
 */
public final class LiteralConverter {
  private LiteralConverter() {}

  /** Timestamps print as {@code yyyy-MM-dd HH:mm:ss[.fffffffff]} with a variable-length fraction. */
  private static final DateTimeFormatter TIMESTAMP_FORMAT =
      new DateTimeFormatterBuilder()
          .appendPattern("yyyy-MM-dd HH:mm:ss")
          .optionalStart()
          .appendFraction(ChronoField.NANO_OF_SECOND, 1, 9, true)
          .optionalEnd()
          .toFormatter();

  /** Converts a literal to the IR, given the IR type the planner assigned it. */
  public static Literal convert(RexLiteral literal, Type type) {
    Literal.Builder builder = Literal.newBuilder();
    if (literal.isNull()) {
      return builder.setIsNull(true).build();
    }

    switch (type.getKind()) {
      case TYPE_KIND_BOOL -> builder.setBoolValue(value(literal, Boolean.class));
      case TYPE_KIND_I8 -> builder.setI8Value(value(literal, Integer.class));
      case TYPE_KIND_I16 -> builder.setI16Value(value(literal, Integer.class));
      case TYPE_KIND_I32 -> builder.setI32Value(value(literal, Integer.class));
      case TYPE_KIND_I64 -> builder.setI64Value(value(literal, Long.class));
      case TYPE_KIND_FP32 -> builder.setFp32Value(value(literal, Float.class));
      case TYPE_KIND_FP64 -> builder.setFp64Value(value(literal, Double.class));
      case TYPE_KIND_STRING -> builder.setStringValue(value(literal, String.class));
      case TYPE_KIND_BINARY ->
          builder.setBinaryValue(
              ByteString.copyFrom(value(literal, org.apache.calcite.avatica.util.ByteString.class).getBytes()));
      case TYPE_KIND_DATE -> builder.setDateValue(value(literal, Integer.class));
      // Calcite holds TIME literals as milliseconds since midnight; the IR is microseconds.
      case TYPE_KIND_TIME -> builder.setTimeValue(value(literal, Integer.class) * 1_000L);
      case TYPE_KIND_TIMESTAMP ->
          builder.setTimestampValue(timestampUnits(literal, type.getPrecision()));
      case TYPE_KIND_TIMESTAMP_TZ ->
          builder.setTimestampTzValue(timestampUnits(literal, type.getPrecision()));
      case TYPE_KIND_DECIMAL ->
          builder.setDecimalValue(
              DecimalValue.newBuilder()
                  .setUnscaled(unscaled(value(literal, BigDecimal.class), type.getScale())));
      case TYPE_KIND_UUID ->
          builder.setUuidValue(ByteString.copyFrom(uuidBytes(value(literal, UUID.class))));
      // Calcite carries day-time intervals as milliseconds (03-planner.md §3.3); the IR is microseconds.
      case TYPE_KIND_INTERVAL_DAY ->
          builder.setIntervalDayValue(
              value(literal, BigDecimal.class).multiply(BigDecimal.valueOf(1_000L)).longValueExact());
      case TYPE_KIND_INTERVAL_YEAR ->
          builder.setIntervalYearValue(value(literal, BigDecimal.class).intValueExact());
      default ->
          throw new UnsupportedFeatureException(
              "literal of type " + type.getKind(), "There is no IR literal encoding for it.");
    }
    return builder.build();
  }

  /**
   * The integer count of units at the type's precision. {@code getValueAs(Long.class)} would round to
   * milliseconds, which would quietly truncate a TIMESTAMP(9) literal.
   */
  private static long timestampUnits(RexLiteral literal, int precision) {
    TimestampString timestamp = literal.getValueAs(TimestampString.class);
    if (timestamp == null) {
      throw new UnsupportedFeatureException(
          "timestamp literal " + literal, "Calcite did not expose it as a TimestampString.");
    }
    LocalDateTime local = LocalDateTime.parse(timestamp.toString(), TIMESTAMP_FORMAT);
    long seconds = local.toEpochSecond(ZoneOffset.UTC);
    long nanos = local.getNano();
    long unitsPerSecond = unitsPerSecond(precision);
    return Math.multiplyExact(seconds, unitsPerSecond) + nanos / (1_000_000_000L / unitsPerSecond);
  }

  /** 0–3 → milliseconds, 4–6 → microseconds, 7–9 → nanoseconds (docs/design/02-ir.md §3). */
  public static long unitsPerSecond(int precision) {
    if (precision <= 3) {
      return 1_000L;
    }
    return precision <= 6 ? 1_000_000L : 1_000_000_000L;
  }

  /**
   * The 128-bit unscaled integer at the target scale, little-endian two's complement, exactly 16
   * bytes. Rounds half-even, as {@code 02-ir.md} §6 says DECIMAL arithmetic does.
   */
  public static ByteString unscaled(BigDecimal value, int scale) {
    BigInteger unscaled = value.setScale(scale, RoundingMode.HALF_EVEN).unscaledValue();
    return ByteString.copyFrom(toLittleEndian16(unscaled));
  }

  /** Sign-extends to 16 bytes and reverses; {@code BigInteger.toByteArray} is minimal big-endian. */
  public static byte[] toLittleEndian16(BigInteger value) {
    byte[] bigEndian = value.toByteArray();
    if (bigEndian.length > 16) {
      throw new UnsupportedFeatureException(
          "decimal literal " + value,
          "It needs " + bigEndian.length + " bytes; the IR carries 128-bit unscaled values.");
    }
    byte[] littleEndian = new byte[16];
    byte fill = (byte) (value.signum() < 0 ? 0xFF : 0x00);
    java.util.Arrays.fill(littleEndian, fill);
    for (int i = 0; i < bigEndian.length; i++) {
      littleEndian[i] = bigEndian[bigEndian.length - 1 - i];
    }
    return littleEndian;
  }

  /** RFC 4122 byte order: the most significant bits first. */
  public static byte[] uuidBytes(UUID uuid) {
    byte[] bytes = new byte[16];
    long msb = uuid.getMostSignificantBits();
    long lsb = uuid.getLeastSignificantBits();
    for (int i = 0; i < 8; i++) {
      bytes[i] = (byte) (msb >>> (56 - 8 * i));
      bytes[8 + i] = (byte) (lsb >>> (56 - 8 * i));
    }
    return bytes;
  }

  /**
   * The other direction, for one purpose: a parameter's declared {@code DEFAULT}, which is a literal
   * {@code Expr} in the catalog and has to be spliced into a parse tree as a {@code SqlNode}
   * (docs/design/17-user-defined-functions.md §1). Only literals are accepted, which is what the
   * catalog validator has already required.
   */
  public static org.apache.calcite.sql.SqlNode toSqlNode(chalk.ir.v1.Expr expr) {
    org.apache.calcite.sql.parser.SqlParserPos pos =
        org.apache.calcite.sql.parser.SqlParserPos.ZERO;
    if (expr.getKindCase() != chalk.ir.v1.Expr.KindCase.LITERAL) {
      throw new UnsupportedFeatureException(
          "a parameter default that is not a literal",
          "docs/design/17-user-defined-functions.md §1: a default is a constant.");
    }

    Literal literal = expr.getLiteral();
    return switch (literal.getValueCase()) {
      case IS_NULL -> org.apache.calcite.sql.SqlLiteral.createNull(pos);
      case BOOL_VALUE -> org.apache.calcite.sql.SqlLiteral.createBoolean(literal.getBoolValue(), pos);
      case I8_VALUE -> numeric(BigDecimal.valueOf(literal.getI8Value()), pos);
      case I16_VALUE -> numeric(BigDecimal.valueOf(literal.getI16Value()), pos);
      case I32_VALUE -> numeric(BigDecimal.valueOf(literal.getI32Value()), pos);
      case I64_VALUE -> numeric(BigDecimal.valueOf(literal.getI64Value()), pos);
      case FP32_VALUE -> numeric(BigDecimal.valueOf(literal.getFp32Value()), pos);
      case FP64_VALUE -> numeric(BigDecimal.valueOf(literal.getFp64Value()), pos);
      case STRING_VALUE ->
          org.apache.calcite.sql.SqlLiteral.createCharString(literal.getStringValue(), pos);
      case DECIMAL_VALUE ->
          numeric(
              new BigDecimal(
                  fromLittleEndian16(literal.getDecimalValue().getUnscaled().toByteArray()),
                  expr.getType().getScale()),
              pos);
      default ->
          throw new UnsupportedFeatureException(
              "a parameter default of kind " + literal.getValueCase(),
              "v1 defaults are boolean, numeric, string or NULL "
                  + "(docs/design/17-user-defined-functions.md §1).");
    };
  }

  private static org.apache.calcite.sql.SqlNode numeric(
      BigDecimal value, org.apache.calcite.sql.parser.SqlParserPos pos) {
    if (value.signum() >= 0) {
      return org.apache.calcite.sql.SqlLiteral.createExactNumeric(value.toPlainString(), pos);
    }
    // A negative literal is a prefix minus applied to a positive one, which is how the parser
    // itself spells it; createExactNumeric refuses a leading '-'.
    return org.apache.calcite.sql.fun.SqlStdOperatorTable.UNARY_MINUS.createCall(
        pos, org.apache.calcite.sql.SqlLiteral.createExactNumeric(value.negate().toPlainString(), pos));
  }

  /** The inverse of {@link #toLittleEndian16}. */
  public static BigInteger fromLittleEndian16(byte[] littleEndian) {
    byte[] bigEndian = new byte[littleEndian.length];
    for (int i = 0; i < littleEndian.length; i++) {
      bigEndian[i] = littleEndian[littleEndian.length - 1 - i];
    }
    return new BigInteger(bigEndian);
  }

  private static <T> T value(RexLiteral literal, Class<T> clazz) {
    T value = literal.getValueAs(clazz);
    if (value == null) {
      throw new UnsupportedFeatureException(
          "literal " + literal,
          "Calcite could not express it as a " + clazz.getSimpleName() + ".");
    }
    return value;
  }
}
