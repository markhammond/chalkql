package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.entitlement.Leakproof;
import chalk.planner.plan.ChalkOperatorTable;
import java.math.BigDecimal;
import org.apache.calcite.avatica.util.TimeUnitRange;
import org.apache.calcite.jdbc.JavaTypeFactoryImpl;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.type.SqlTypeName;
import org.junit.jupiter.api.Test;

/**
 * What cannot raise for a row (F288, F292, F300): the functions masks are written with that cannot,
 * and the ones that can, which a policy's condition, mask or placeholder is refused for and the
 * entitlement barrier keeps above itself.
 */
class LeakproofTest {
  private final RelDataTypeFactory types = new JavaTypeFactoryImpl();
  private final RexBuilder rex = new RexBuilder(types);

  private final RelDataType string = types.createTypeWithNullability(
      types.createSqlType(SqlTypeName.VARCHAR), true);
  private final RelDataType integer = types.createSqlType(SqlTypeName.INTEGER);
  private final RelDataType real = types.createSqlType(SqlTypeName.DOUBLE);
  private final RelDataType decimal = types.createSqlType(SqlTypeName.DECIMAL, 10, 2);
  private final RelDataType date = types.createSqlType(SqlTypeName.DATE);

  private RexNode column(RelDataType type, int index) {
    return rex.makeInputRef(type, index);
  }

  private RexNode number(long value) {
    return rex.makeExactLiteral(BigDecimal.valueOf(value), integer);
  }

  @Test
  void the_functions_masks_are_written_with_cannot_raise() {
    RexNode name = column(string, 0);
    assertThat(Leakproof.is(rex.makeCall(ChalkOperatorTable.FINGERPRINT, name, rex.makeLiteral("k"))))
        .isTrue();
    assertThat(Leakproof.is(rex.makeCall(ChalkOperatorTable.PRESENT, name))).isTrue();
    assertThat(Leakproof.is(rex.makeCall(SqlStdOperatorTable.CHAR_LENGTH, name))).isTrue();
    assertThat(Leakproof.is(rex.makeCall(SqlStdOperatorTable.CONCAT, name, rex.makeLiteral("-"))))
        .isTrue();
    assertThat(Leakproof.is(rex.makeCall(SqlStdOperatorTable.SUBSTRING, name, number(1), number(3))))
        .isTrue();
    assertThat(Leakproof.is(rex.makeCall(SqlStdOperatorTable.SUBSTRING, name, number(2)))).isTrue();
    assertThat(Leakproof.is(rex.makeCall(SqlStdOperatorTable.ROUND, column(real, 1)))).isTrue();
    assertThat(
            Leakproof.is(
                rex.makeCall(
                    SqlStdOperatorTable.FLOOR, column(date, 2), rex.makeFlag(TimeUnitRange.YEAR))))
        .isTrue();
  }

  @Test
  void what_can_raise_is_not_leakproof() {
    RexNode name = column(string, 0);
    // A length that is not a literal, or a negative one, is what SUBSTRING refuses.
    assertThat(
            Leakproof.is(
                rex.makeCall(SqlStdOperatorTable.SUBSTRING, name, number(1), column(integer, 3))))
        .isFalse();
    assertThat(Leakproof.is(rex.makeCall(SqlStdOperatorTable.SUBSTRING, name, number(1), number(-1))))
        .isFalse();
    // A decimal rounds up past its precision; a double does not.
    assertThat(Leakproof.is(rex.makeCall(SqlStdOperatorTable.ROUND, column(decimal, 4)))).isFalse();
    assertThat(Leakproof.is(rex.makeCast(integer, name))).isFalse();
    assertThat(Leakproof.is(rex.makeCall(SqlStdOperatorTable.DIVIDE, number(10), column(integer, 3))))
        .isFalse();
  }

  @Test
  void the_wrapper_of_a_mask_is_as_leakproof_as_the_mask() {
    RexNode prefix = rex.makeCall(SqlStdOperatorTable.SUBSTRING, column(string, 0), number(1), number(1));
    assertThat(Leakproof.is(rex.makeCall(ChalkOperatorTable.MASKED, prefix, rex.makeLiteral("s.t.c"))))
        .isTrue();
    assertThat(
            Leakproof.is(
                rex.makeCall(
                    ChalkOperatorTable.MASKED, rex.makeCast(integer, column(string, 0)),
                    rex.makeLiteral("s.t.c"))))
        .isFalse();
  }

  @Test
  void a_refusal_names_the_outermost_call_that_can_raise() {
    RexNode divisor = rex.makeCall(SqlStdOperatorTable.MINUS, column(integer, 3), number(2));
    RexNode quotient = rex.makeCall(SqlStdOperatorTable.DIVIDE, number(10), divisor);
    RexNode condition = rex.makeCall(SqlStdOperatorTable.GREATER_THAN, quotient, number(1));

    assertThat(Leakproof.offending(condition)).isEqualTo(quotient);
    assertThat(Leakproof.offending(rex.makeCall(SqlStdOperatorTable.EQUALS, column(integer, 3), number(1))))
        .isNull();
  }
}
