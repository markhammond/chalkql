package chalk.planner.entitlement;

import static org.assertj.core.api.Assertions.assertThat;

import java.util.List;
import java.util.Map;
import org.apache.calcite.jdbc.JavaTypeFactoryImpl;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.type.SqlTypeName;
import org.junit.jupiter.api.Test;

/**
 * The oracle's expressions said as SQL (F286): column names for positions, and the grouping a
 * reader needs where precedence alone would carry it.
 */
class ExplainedSqlTest {

  private final JavaTypeFactoryImpl types = new JavaTypeFactoryImpl();
  private final RexBuilder rex = new RexBuilder(types);
  private final RelDataType integer = types.createSqlType(SqlTypeName.INTEGER);

  private final ExplainedSql.Row row =
      new ExplainedSql.Row(
          List.<SqlNode>of(ExplainedSql.name("site_id"), ExplainedSql.name("Open"), ExplainedSql.name("User")),
          Map.of());

  private RexNode equals(int column, int value) {
    return rex.makeCall(
        SqlStdOperatorTable.EQUALS, rex.makeInputRef(integer, column), rex.makeExactLiteral(java.math.BigDecimal.valueOf(value)));
  }

  @Test
  void a_conjunction_under_a_disjunction_is_parenthesised() {
    RexNode either =
        rex.makeCall(
            SqlStdOperatorTable.OR,
            rex.makeCall(SqlStdOperatorTable.AND, equals(0, 1), equals(1, 2)),
            rex.makeCall(SqlStdOperatorTable.AND, equals(0, 3), equals(2, 4)));

    assertThat(ExplainedSql.render(either, row))
        .isEqualTo("(site_id = 1 AND \"Open\" = 2) OR (site_id = 3 AND \"User\" = 4)");
  }

  @Test
  void a_disjunction_under_a_conjunction_is_parenthesised_by_precedence_alone() {
    RexNode both =
        rex.makeCall(
            SqlStdOperatorTable.AND,
            equals(0, 1),
            rex.makeCall(SqlStdOperatorTable.OR, equals(1, 2), equals(2, 3)));

    assertThat(ExplainedSql.render(both, row))
        .isEqualTo("site_id = 1 AND (\"Open\" = 2 OR \"User\" = 3)");
  }

  @Test
  void a_plain_disjunction_takes_no_parentheses() {
    RexNode either = rex.makeCall(SqlStdOperatorTable.OR, equals(0, 1), equals(0, 2));

    assertThat(ExplainedSql.render(either, row)).isEqualTo("site_id = 1 OR site_id = 2");
  }
}
