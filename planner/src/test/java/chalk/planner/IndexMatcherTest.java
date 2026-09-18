package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.plan.IndexMatcher;
import com.google.common.collect.ImmutableList;
import java.math.BigDecimal;
import java.util.Arrays;
import java.util.List;
import java.util.stream.Stream;
import org.apache.calcite.jdbc.JavaTypeFactoryImpl;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.util.DateString;
import org.apache.calcite.util.TimestampString;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * The matcher's table (D40, {@code 11-m2-index-support.md} §6). Every shape states both halves:
 * what the ranges are and what is left residual.
 *
 * <p>The residual is the half that matters. A conjunct the matcher consumes disappears from the
 * filter above the lookup, so consuming one the ranges do not actually enforce is a silent
 * wrong-answer bug — which is why every case here asserts the residual exactly, including the cases
 * where the answer is "the matcher understood nothing and everything is residual".
 *
 * <p>The row is {@code bars}' first three columns — {@code symbol} STRING, {@code ts} TIMESTAMP(9),
 * {@code close} FP64 — and the index under test is {@code (symbol, ts)} unless a case says
 * otherwise.
 */
class IndexMatcherTest {
  private static final RelDataTypeFactory TYPES = new JavaTypeFactoryImpl(ChalkTypeSystemHolder.INSTANCE);
  private static final RexBuilder REX = new RexBuilder(TYPES);

  private static final RelDataType ROW =
      TYPES.builder()
          .add("symbol", TYPES.createSqlType(SqlTypeName.VARCHAR))
          .add("ts", TYPES.createSqlType(SqlTypeName.TIMESTAMP, 9))
          .add("close", TYPES.createSqlType(SqlTypeName.DOUBLE))
          .add("vwap", TYPES.createTypeWithNullability(TYPES.createSqlType(SqlTypeName.DOUBLE), true))
          .build();

  /** {@code (symbol, ts)} — both key columns are in the row, at positions 0 and 1. */
  private static final List<Integer> SYMBOL_TS = List.of(0, 1);

  /** {@code (ts, symbol)} — the collation-backed index, key columns the other way round. */
  private static final List<Integer> TS_SYMBOL = List.of(1, 0);

  /** An index on a column the scan does not project: unusable. */
  private static final List<Integer> UNPROJECTED = List.of(-1);

  /** One case: a condition, the index key, and what the matcher must make of it. */
  private record Case(String name, RexNode condition, List<Integer> key, boolean hash,
      String ranges, String residual) {
    @Override
    public String toString() {
      return name;
    }
  }

  private static Stream<Case> cases() {
    RexNode symbol = ref(0);
    RexNode ts = ref(1);
    RexNode close = ref(2);
    RexNode btc = str("BTCUSDT");
    RexNode eth = str("ETHUSDT");
    RexNode day3 = timestamp("2026-01-03 00:00:00");
    RexNode day4 = timestamp("2026-01-04 00:00:00");
    RexNode p0 = param(0, TYPES.createSqlType(SqlTypeName.VARCHAR));
    RexNode p1 = param(1, TYPES.createSqlType(SqlTypeName.TIMESTAMP, 9));

    return Stream.of(
        // ---- the shapes the matcher consumes ----
        new Case("equality on the first key column",
            eq(symbol, btc), SYMBOL_TS, false, "[['BTCUSDT'], ['BTCUSDT']]", null),
        new Case("equality with the literal on the left",
            eq(btc, symbol), SYMBOL_TS, false, "[['BTCUSDT'], ['BTCUSDT']]", null),
        new Case("equality against a parameter",
            eq(symbol, p0), SYMBOL_TS, false, "[[?0], [?0]]", null),
        new Case("equality on the whole key",
            and(eq(symbol, btc), eq(ts, day3)), SYMBOL_TS, false,
            "[['BTCUSDT', 2026-01-03 00:00:00:TIMESTAMP(9)], ['BTCUSDT', 2026-01-03 00:00:00:TIMESTAMP(9)]]", null),
        new Case("equality prefix and a lower bound",
            and(eq(symbol, btc), ge(ts, day3)), SYMBOL_TS, false,
            "[['BTCUSDT', 2026-01-03 00:00:00:TIMESTAMP(9)], ['BTCUSDT']]", null),
        new Case("equality prefix and an exclusive lower bound",
            and(eq(symbol, btc), gt(ts, day3)), SYMBOL_TS, false,
            "(['BTCUSDT', 2026-01-03 00:00:00:TIMESTAMP(9)], ['BTCUSDT']]", null),
        new Case("equality prefix and an upper bound",
            and(eq(symbol, btc), lt(ts, day4)), SYMBOL_TS, false,
            "[['BTCUSDT'], ['BTCUSDT', 2026-01-04 00:00:00:TIMESTAMP(9)])", null),
        new Case("equality prefix and both bounds",
            and(eq(symbol, btc), ge(ts, day3), lt(ts, day4)), SYMBOL_TS, false,
            "[['BTCUSDT', 2026-01-03 00:00:00:TIMESTAMP(9)], ['BTCUSDT', 2026-01-04 00:00:00:TIMESTAMP(9)])", null),
        new Case("a range on the leading key column",
            and(ge(ts, day3), le(ts, day4)), TS_SYMBOL, false,
            "[[2026-01-03 00:00:00:TIMESTAMP(9)], [2026-01-04 00:00:00:TIMESTAMP(9)]]", null),
        new Case("a range with the literals on the left",
            and(le(day3, ts), ge(day4, ts)), TS_SYMBOL, false,
            "[[2026-01-03 00:00:00:TIMESTAMP(9)], [2026-01-04 00:00:00:TIMESTAMP(9)]]", null),
        new Case("BETWEEN, which arrives as a Sarg",
            search(ts, day3, day4), TS_SYMBOL, false,
            "[[2026-01-03 00:00:00:TIMESTAMP(9)], [2026-01-04 00:00:00:TIMESTAMP(9)]]", null),
        new Case("an OR of equalities on the first column is an IN list",
            or(eq(symbol, btc), eq(symbol, eth)), SYMBOL_TS, false,
            "[['BTCUSDT'], ['BTCUSDT']] [['ETHUSDT'], ['ETHUSDT']]", null),
        new Case("an IN list is sorted, whatever order the query gave",
            or(eq(symbol, eth), eq(symbol, btc)), SYMBOL_TS, false,
            "[['BTCUSDT'], ['BTCUSDT']] [['ETHUSDT'], ['ETHUSDT']]", null),
        new Case("a duplicate IN value yields one range",
            or(eq(symbol, btc), eq(symbol, btc)), SYMBOL_TS, false,
            "[['BTCUSDT'], ['BTCUSDT']]", null),
        new Case("an IN list and a bound on the next column",
            and(or(eq(symbol, btc), eq(symbol, eth)), lt(ts, day4)), SYMBOL_TS, false,
            "[['BTCUSDT'], ['BTCUSDT', 2026-01-04 00:00:00:TIMESTAMP(9)]) "
                + "[['ETHUSDT'], ['ETHUSDT', 2026-01-04 00:00:00:TIMESTAMP(9)])",
            null),
        new Case("a hash index answers the whole key",
            and(eq(symbol, btc), eq(ts, day3)), SYMBOL_TS, true,
            "[['BTCUSDT', 2026-01-03 00:00:00:TIMESTAMP(9)], ['BTCUSDT', 2026-01-03 00:00:00:TIMESTAMP(9)]]", null),

        // ---- consumed, with something left over ----
        new Case("an unindexed conjunct is residual",
            and(eq(symbol, btc), gt(close, real(100))), SYMBOL_TS, false,
            "[['BTCUSDT'], ['BTCUSDT']]", ">($2, 100.0E0)"),
        new Case("two unindexed conjuncts are both residual",
            and(eq(symbol, btc), gt(close, real(100)), lt(close, real(200))), SYMBOL_TS, false,
            "[['BTCUSDT'], ['BTCUSDT']]",
            "AND(>($2, 100.0E0), <($2, 200.0E0))"),
        new Case("IS NOT NULL is never consumed",
            and(eq(symbol, btc), isNotNull(ref(3))), SYMBOL_TS, false,
            "[['BTCUSDT'], ['BTCUSDT']]", "IS NOT NULL($3)"),
        new Case("a second equality on the same column is residual",
            and(eq(symbol, btc), eq(symbol, eth)), SYMBOL_TS, false,
            "[['BTCUSDT'], ['BTCUSDT']]", "=($0, 'ETHUSDT')"),
        new Case("a second lower bound on the same column is residual",
            and(eq(symbol, btc), ge(ts, day3), gt(ts, day4)), SYMBOL_TS, false,
            "[['BTCUSDT', 2026-01-03 00:00:00:TIMESTAMP(9)], ['BTCUSDT']]",
            ">($1, 2026-01-04 00:00:00)"),
        new Case("a bound past the first unpinned column is residual",
            and(ge(ts, day3), eq(symbol, btc)), TS_SYMBOL, false,
            "[[2026-01-03 00:00:00:TIMESTAMP(9)], [-inf]]", "=($0, 'BTCUSDT')"),
        new Case("an IN list on the second key column is residual",
            and(eq(symbol, btc), or(eq(ts, day3), eq(ts, day4))), SYMBOL_TS, false,
            "[['BTCUSDT'], ['BTCUSDT']]",
            "OR(=($1, 2026-01-03 00:00:00), =($1, 2026-01-04 00:00:00))"),
        new Case("an IN list beside an equality on the same column is residual",
            and(eq(symbol, btc), or(eq(symbol, btc), eq(symbol, eth))), SYMBOL_TS, false,
            "[['BTCUSDT'], ['BTCUSDT']]", "OR(=($0, 'BTCUSDT'), =($0, 'ETHUSDT'))"),

        // ---- nothing consumed ----
        new Case("a predicate on no key column",
            gt(close, real(100)), SYMBOL_TS, false, null, null),
        new Case("an equality-bound prefix with a NULL-able residual column",
            and(eq(symbol, btc), isNull(ref(3))), SYMBOL_TS, false,
            "[['BTCUSDT'], ['BTCUSDT']]", "IS NULL($3)"),
        new Case("an equality on the second key column alone",
            eq(ts, day3), SYMBOL_TS, false, null, null),
        new Case("a range on the second key column alone",
            ge(ts, day3), SYMBOL_TS, false, null, null),
        new Case("IS NULL alone",
            isNull(ref(3)), SYMBOL_TS, false, null, null),
        new Case("IS NOT NULL alone",
            isNotNull(ref(3)), SYMBOL_TS, false, null, null),
        new Case("LIKE",
            like(symbol, str("BTC%")), SYMBOL_TS, false, null, null),
        new Case("a comparison between two columns",
            eq(symbol, ref(2)), SYMBOL_TS, false, null, null),
        new Case("a cast on the column side",
            eq(cast(symbol, SqlTypeName.VARCHAR, 4), str("BTCU")), SYMBOL_TS, false, null, null),
        new Case("a cast on the value side",
            eq(ts, cast(p1, SqlTypeName.TIMESTAMP, 9)), SYMBOL_TS, false, null, null),
        new Case("a NOT over an equality",
            not(eq(symbol, btc)), SYMBOL_TS, false, null, null),
        new Case("a not-equals",
            ne(symbol, btc), SYMBOL_TS, false, null, null),
        new Case("a top-level OR that is not an IN list",
            or(eq(symbol, btc), gt(close, real(100))), SYMBOL_TS, false, null, null),
        new Case("an OR of equalities on two different columns",
            or(eq(symbol, btc), eq(ts, day3)), SYMBOL_TS, false, null, null),
        new Case("a key column the scan does not project",
            eq(symbol, btc), UNPROJECTED, false, null, null),
        new Case("a hash index refuses a range",
            and(eq(symbol, btc), ge(ts, day3)), SYMBOL_TS, true, null, null),
        new Case("a hash index refuses a key prefix",
            eq(symbol, btc), SYMBOL_TS, true, null, null),
        new Case("a parameter of the wrong type is not a bound",
            eq(ts, p0), TS_SYMBOL, false, null, null),
        new Case("a parameter of the wrong precision is not a bound",
            eq(ts, param(0, TYPES.createSqlType(SqlTypeName.TIMESTAMP, 3))), TS_SYMBOL, false,
            null, null),
        new Case("a literal of the wrong family is not a bound",
            eq(ts, date("2026-01-03")), TS_SYMBOL, false, null, null),
        new Case("a coarser literal of the right family is a bound",
            eq(ts, timestamp0("2026-01-03 00:00:00")), TS_SYMBOL, false,
            "[[2026-01-03 00:00:00], [2026-01-03 00:00:00]]", null),
        new Case("an always-true condition",
            REX.makeLiteral(true), SYMBOL_TS, false, null, null),
        new Case("a parameter as a lower bound",
            and(eq(symbol, p0), ge(ts, p1)), SYMBOL_TS, false, "[[?0, ?1], [?0]]", null));
  }

  @ParameterizedTest(name = "{0}")
  @MethodSource("cases")
  void the_matcher_consumes_exactly_what_it_can_enforce(Case testCase) {
    IndexMatcher.Result result =
        IndexMatcher.split(testCase.condition(), testCase.key(), ROW, testCase.hash(), REX);

    if (testCase.ranges() == null) {
      assertThat(result.matched()).as("ranges").isFalse();
      assertThat(result.residual()).as("residual").isNull();
      return;
    }

    assertThat(render(result.ranges())).as("ranges").isEqualTo(testCase.ranges());
    assertThat(result.residual() == null ? null : result.residual().toString())
        .as("residual")
        .isEqualTo(testCase.residual());
  }

  /**
   * Whatever the matcher leaves behind, together with the ranges, has to select the same rows as the
   * original condition. This is the shape of that claim the table above cannot state: every
   * consumed conjunct must be one of the original conjuncts, never something invented.
   */
  @ParameterizedTest(name = "{0}")
  @MethodSource("cases")
  void the_residual_is_made_of_conjuncts_the_condition_had(Case testCase) {
    IndexMatcher.Result result =
        IndexMatcher.split(testCase.condition(), testCase.key(), ROW, testCase.hash(), REX);
    if (result.residual() == null) {
      return;
    }

    List<RexNode> original =
        org.apache.calcite.plan.RelOptUtil.conjunctions(
            org.apache.calcite.rex.RexUtil.expandSearch(REX, null, testCase.condition()));
    assertThat(org.apache.calcite.plan.RelOptUtil.conjunctions(result.residual()))
        .allSatisfy(conjunct -> assertThat(original).contains(conjunct));
  }

  @Test
  void the_table_covers_at_least_thirty_shapes() {
    // Rev 3 asks for exhaustiveness here by name: a dropped residual is a silent wrong answer, and
    // the differential suite is the backstop rather than the front line.
    assertThat(cases().count()).isGreaterThanOrEqualTo(30);
  }

  private static String render(ImmutableList<IndexMatcher.Range> ranges) {
    return String.join(" ", ranges.stream().map(IndexMatcherTest::render).toList());
  }

  private static String render(IndexMatcher.Range range) {
    String lower = range.lower().isEmpty() ? "[-inf]" : range.lower().toString();
    String upper = range.upper().isEmpty() ? "[-inf]" : range.upper().toString();
    return (range.lowerInclusive() ? "[" : "(") + lower + ", " + upper
        + (range.upperInclusive() ? "]" : ")");
  }

  private static RexNode ref(int index) {
    return REX.makeInputRef(ROW.getFieldList().get(index).getType(), index);
  }

  private static RexNode param(int index, RelDataType type) {
    return REX.makeDynamicParam(TYPES.createTypeWithNullability(type, true), index);
  }

  private static RexNode str(String value) {
    return REX.makeLiteral(value);
  }

  private static RexNode real(double value) {
    return REX.makeLiteral(
        BigDecimal.valueOf(value), TYPES.createSqlType(SqlTypeName.DOUBLE), false);
  }

  private static RexNode timestamp(String value) {
    return REX.makeTimestampLiteral(new TimestampString(value), 9);
  }

  private static RexNode timestamp0(String value) {
    return REX.makeTimestampLiteral(new TimestampString(value), 0);
  }

  private static RexNode date(String value) {
    return REX.makeDateLiteral(new DateString(value));
  }

  private static RexNode cast(RexNode node, SqlTypeName type, int precision) {
    return REX.makeCast(TYPES.createSqlType(type, precision), node, true, false);
  }

  private static RexNode eq(RexNode left, RexNode right) {
    return REX.makeCall(SqlStdOperatorTable.EQUALS, left, right);
  }

  private static RexNode ne(RexNode left, RexNode right) {
    return REX.makeCall(SqlStdOperatorTable.NOT_EQUALS, left, right);
  }

  private static RexNode gt(RexNode left, RexNode right) {
    return REX.makeCall(SqlStdOperatorTable.GREATER_THAN, left, right);
  }

  private static RexNode ge(RexNode left, RexNode right) {
    return REX.makeCall(SqlStdOperatorTable.GREATER_THAN_OR_EQUAL, left, right);
  }

  private static RexNode lt(RexNode left, RexNode right) {
    return REX.makeCall(SqlStdOperatorTable.LESS_THAN, left, right);
  }

  private static RexNode le(RexNode left, RexNode right) {
    return REX.makeCall(SqlStdOperatorTable.LESS_THAN_OR_EQUAL, left, right);
  }

  private static RexNode like(RexNode left, RexNode right) {
    return REX.makeCall(SqlStdOperatorTable.LIKE, left, right);
  }

  private static RexNode isNull(RexNode node) {
    return REX.makeCall(SqlStdOperatorTable.IS_NULL, node);
  }

  private static RexNode isNotNull(RexNode node) {
    return REX.makeCall(SqlStdOperatorTable.IS_NOT_NULL, node);
  }

  private static RexNode not(RexNode node) {
    return REX.makeCall(SqlStdOperatorTable.NOT, node);
  }

  private static RexNode and(RexNode... nodes) {
    return REX.makeCall(SqlStdOperatorTable.AND, Arrays.asList(nodes));
  }

  private static RexNode or(RexNode... nodes) {
    return REX.makeCall(SqlStdOperatorTable.OR, Arrays.asList(nodes));
  }

  /** {@code BETWEEN}, as the simplifier leaves it: a SEARCH over a one-range Sarg. */
  private static RexNode search(RexNode column, RexNode low, RexNode high) {
    return REX.makeCall(
        SqlStdOperatorTable.AND,
        REX.makeCall(SqlStdOperatorTable.GREATER_THAN_OR_EQUAL, column, low),
        REX.makeCall(SqlStdOperatorTable.LESS_THAN_OR_EQUAL, column, high));
  }

  /** The type system the planner uses, so DECIMAL and TIMESTAMP precisions match production. */
  private static final class ChalkTypeSystemHolder {
    static final org.apache.calcite.rel.type.RelDataTypeSystem INSTANCE =
        chalk.planner.types.ChalkTypeSystem.INSTANCE;
  }
}
