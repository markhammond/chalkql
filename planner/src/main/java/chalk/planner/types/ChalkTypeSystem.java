package chalk.planner.types;

import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rel.type.RelDataTypeSystem;
import org.apache.calcite.rel.type.RelDataTypeSystemImpl;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.sql.type.SqlTypeUtil;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The type system Chalk plans under (docs/design/03-planner.md §3.3).
 *
 * <p>Note on {@code getMaxNumericPrecision}: the design says to override it, but in Calcite 1.42 it
 * is {@code final} on {@link RelDataTypeSystemImpl} and delegates to {@code
 * getMaxPrecision(DECIMAL)}; the same is true of {@code getMaxNumericScale} and {@code
 * getMaxScale(DECIMAL)}. Overriding the per-type-name methods is therefore the supported way to say
 * the same thing. See docs/adr/0001-calcite-facts.md, V5.
 */
public final class ChalkTypeSystem extends RelDataTypeSystemImpl {
  public static final RelDataTypeSystem INSTANCE = new ChalkTypeSystem();

  /** DECIMAL(p, s) with p up to 38, per the IR (docs/design/02-ir.md §3). */
  public static final int MAX_DECIMAL_PRECISION = 38;

  /** TIMESTAMP and TIMESTAMP_TZ carry up to 9 fractional digits, so DateTime ticks are lossless. */
  public static final int MAX_TIMESTAMP_PRECISION = 9;

  /** TIME is microseconds in the IR. */
  public static final int MAX_TIME_PRECISION = 6;

  private ChalkTypeSystem() {}

  /**
   * The fewest fractional digits a DECIMAL product or quotient gives up its integer digits for:
   * past 38 digits in all, the integer digits are kept and the scale gives way, though not below
   * this many unless the integer digits need them too (D338, design 63 §5).
   */
  public static final int MIN_ADJUSTED_SCALE = 6;

  /**
   * DECIMAL sums and differences, as SQL Server types them: the larger scale, and room for the
   * larger integer part and a carry. Past 38 digits the scale gives way to the integer part.
   */
  @Override
  public @Nullable RelDataType deriveDecimalPlusType(
      RelDataTypeFactory typeFactory, RelDataType type1, RelDataType type2) {
    if (!decimalArithmetic(type1, type2)) {
      return null;
    }
    int scale = Math.max(type1.getScale(), type2.getScale());
    int integral =
        Math.max(type1.getPrecision() - type1.getScale(), type2.getPrecision() - type2.getScale());
    if (integral + 1 + scale <= MAX_DECIMAL_PRECISION) {
      return decimal(typeFactory, integral + 1 + scale, scale);
    }
    return decimal(
        typeFactory, MAX_DECIMAL_PRECISION, Math.max(0, Math.min(scale, MAX_DECIMAL_PRECISION - integral)));
  }

  /**
   * DECIMAL products, as SQL Server types them: precision p1 + p2 + 1 and scale s1 + s2, and past 38
   * digits the integer digits kept and the scale reduced (D338, design 63 §5). Calcite's own rule
   * keeps every fractional digit, so four DECIMAL(28,8) factors had a scale of 30, which nothing in
   * the executor could hold.
   */
  @Override
  public @Nullable RelDataType deriveDecimalMultiplyType(
      RelDataTypeFactory typeFactory, RelDataType type1, RelDataType type2) {
    if (!decimalArithmetic(type1, type2)) {
      return null;
    }
    return adjusted(
        typeFactory,
        type1.getPrecision() + type2.getPrecision() + 1,
        type1.getScale() + type2.getScale());
  }

  /**
   * DECIMAL quotients, as SQL Server types them: scale max(6, s1 + p2 + 1) and precision p1 − s1 +
   * s2 + that scale, adjusted past 38 digits as a product is.
   */
  @Override
  public @Nullable RelDataType deriveDecimalDivideType(
      RelDataTypeFactory typeFactory, RelDataType type1, RelDataType type2) {
    if (!decimalArithmetic(type1, type2)) {
      return null;
    }
    int scale =
        Math.max(MIN_ADJUSTED_SCALE, type1.getScale() + type2.getPrecision() + 1);
    int precision = type1.getPrecision() - type1.getScale() + type2.getScale() + scale;
    return adjusted(typeFactory, precision, scale);
  }

  /** Whether Calcite asks for a DECIMAL result: both operands exact, and at least one a DECIMAL. */
  private static boolean decimalArithmetic(RelDataType type1, RelDataType type2) {
    return SqlTypeUtil.isExactNumeric(type1)
        && SqlTypeUtil.isExactNumeric(type2)
        && (SqlTypeUtil.isDecimal(type1) || SqlTypeUtil.isDecimal(type2));
  }

  /**
   * DECIMAL({@code precision}, {@code scale}), or, past 38 digits, DECIMAL(38, s) with the integer
   * digits kept: s is 38 less the integer digits where at least {@value #MIN_ADJUSTED_SCALE} remain,
   * and otherwise the smaller of the scale and {@value #MIN_ADJUSTED_SCALE}, where a value whose
   * integer part does not fit overflows when it is computed.
   */
  private static RelDataType adjusted(RelDataTypeFactory typeFactory, int precision, int scale) {
    if (precision <= MAX_DECIMAL_PRECISION) {
      return decimal(typeFactory, precision, scale);
    }
    int integral = precision - scale;
    int reduced =
        integral <= MAX_DECIMAL_PRECISION - MIN_ADJUSTED_SCALE
            ? Math.min(scale, MAX_DECIMAL_PRECISION - integral)
            : Math.min(scale, MIN_ADJUSTED_SCALE);
    return decimal(typeFactory, MAX_DECIMAL_PRECISION, reduced);
  }

  private static RelDataType decimal(RelDataTypeFactory typeFactory, int precision, int scale) {
    return typeFactory.createSqlType(SqlTypeName.DECIMAL, Math.max(1, precision), scale);
  }

  @Override
  public int getMaxPrecision(SqlTypeName typeName) {
    return switch (typeName) {
      case DECIMAL -> MAX_DECIMAL_PRECISION;
      case TIMESTAMP, TIMESTAMP_WITH_LOCAL_TIME_ZONE -> MAX_TIMESTAMP_PRECISION;
      case TIME, TIME_WITH_LOCAL_TIME_ZONE -> MAX_TIME_PRECISION;
      default -> super.getMaxPrecision(typeName);
    };
  }

  @Override
  public int getMaxScale(SqlTypeName typeName) {
    return typeName == SqlTypeName.DECIMAL ? MAX_DECIMAL_PRECISION : super.getMaxScale(typeName);
  }

  /**
   * Without this, {@code symbol IN ('BTC', 'ETHUSDT')} produces space-padded CHAR(7) literals and
   * the shorter one silently stops matching — a known Calcite trap (V6).
   */
  @Override
  public boolean shouldConvertRaggedUnionTypesToVarying() {
    return true;
  }

  /**
   * STRING columns are unbounded VARCHAR. Calcite's default of 1 would truncate anything written as
   * a bare {@code VARCHAR}.
   */
  @Override
  public int getDefaultPrecision(SqlTypeName typeName) {
    return switch (typeName) {
      case VARCHAR, VARBINARY -> RelDataType.PRECISION_NOT_SPECIFIED;
      default -> super.getDefaultPrecision(typeName);
    };
  }
}
