package chalk.planner.types;

import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeSystem;
import org.apache.calcite.rel.type.RelDataTypeSystemImpl;
import org.apache.calcite.sql.type.SqlTypeName;

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
