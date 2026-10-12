package chalk.planner.types;

import static org.assertj.core.api.Assertions.assertThat;

import org.apache.calcite.jdbc.JavaTypeFactoryImpl;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.sql.type.SqlTypeName;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;

/**
 * D338, design 63 §5: DECIMAL arithmetic typed as SQL Server types it, keeping the integer digits
 * and giving up scale past 38 digits, never below six fractional digits unless the integer digits
 * need them. Calcite's own rule kept every fractional digit, so a product of DECIMAL(28,8) values
 * soon had a scale nothing could hold.
 */
class DecimalArithmeticTypeTest {

  private static final RelDataTypeFactory TYPES = new JavaTypeFactoryImpl(ChalkTypeSystem.INSTANCE);

  @ParameterizedTest(name = "{0}: DECIMAL({1},{2}) and DECIMAL({3},{4}) is DECIMAL({5},{6})")
  @CsvSource({
    // Within 38 digits, SQL Server's rule as it stands.
    "*, 10, 2, 5, 3, 16, 5",
    "/, 10, 2, 5, 3, 19, 8",
    "+, 10, 2, 5, 3, 12, 3",
    // The owner's notional: the integer digits kept, six places.
    "*, 28, 8, 28, 8, 38, 6",
    "/, 38, 6, 28, 8, 38, 6",
    "+, 38, 6, 38, 6, 38, 6",
    // Past 38 with few integer digits: the scale gives way only as far as it must.
    "*, 10, 8, 30, 20, 38, 25",
    "/, 18, 2, 28, 8, 38, 14",
  })
  void decimal_arithmetic_is_typed_as_sql_server_types_it(
      String operator, int p1, int s1, int p2, int s2, int precision, int scale) {
    RelDataType left = TYPES.createSqlType(SqlTypeName.DECIMAL, p1, s1);
    RelDataType right = TYPES.createSqlType(SqlTypeName.DECIMAL, p2, s2);

    RelDataType result =
        switch (operator) {
          case "*" -> ChalkTypeSystem.INSTANCE.deriveDecimalMultiplyType(TYPES, left, right);
          case "/" -> ChalkTypeSystem.INSTANCE.deriveDecimalDivideType(TYPES, left, right);
          default -> ChalkTypeSystem.INSTANCE.deriveDecimalPlusType(TYPES, left, right);
        };

    assertThat(result).isNotNull();
    assertThat(result.getPrecision()).as("precision").isEqualTo(precision);
    assertThat(result.getScale()).as("scale").isEqualTo(scale);
  }
}
