package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.AggregateFunctionId;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.diag.PlanText;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import java.util.List;
import org.apache.calcite.sql.fun.SqlLibrary;
import org.apache.calcite.sql.validate.SqlConformanceEnum;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * Step 19's planner rules ({@code 14-windows-ii.md} §1, §6 and §7): {@code HOP} and {@code SESSION}
 * as operators, the dialect libraries as a request option, and {@code UNNEST} in every spelling.
 */
class WindowsIiRulesTest {
  private static RegisteredCatalog catalog;
  private static CorpusPlanner planner;

  @BeforeAll
  static void registerCatalog() {
    catalog = new CatalogRegistry().register(TestCatalogs.corpus());
    planner = new CorpusPlanner();
  }

  private static String planText(String sql) {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      return PlanText.withAttributes(pipeline.plan(sql, false).physical());
    } catch (Exception e) {
      throw new IllegalStateException("planning failed for: " + sql, e);
    }
  }

  private static List<Rel> nodes(Plan plan, Rel.KindCase kind) {
    return IrNodes.of(plan, kind);
  }

  // ---- HOP and SESSION (D55) -------------------------------------------------------------------

  @Test
  void hop_becomes_an_operator_with_the_intervals_in_microseconds() {
    Plan plan =
        planner.plan(
            "SELECT * FROM TABLE(HOP(TABLE bars_small, DESCRIPTOR(ts), "
                + "INTERVAL '5' MINUTE, INTERVAL '15' MINUTE))");

    List<Rel> hops = nodes(plan, Rel.KindCase.HOP);
    assertThat(hops).hasSize(1);
    assertThat(hops.get(0).getHop().getTimeColumn()).isEqualTo(1);
    assertThat(hops.get(0).getHop().getSlide().getLiteral().getIntervalDayValue())
        .isEqualTo(300_000_000L);
    assertThat(hops.get(0).getHop().getSize().getLiteral().getIntervalDayValue())
        .isEqualTo(900_000_000L);
  }

  /** The point of D55's trait: the declared collation already delivers (symbol, ts). */
  @Test
  void session_over_a_collated_table_needs_no_sort() {
    String text =
        planText(
            "SELECT * FROM TABLE(SESSION(TABLE trades_sparse, DESCRIPTOR(ts), "
                + "DESCRIPTOR(symbol), INTERVAL '2' HOUR))");

    assertThat(text).contains("ChalkSession");
    assertThat(text).doesNotContain("ChalkSort");
  }

  @Test
  void session_carries_its_partition_key_and_gap() {
    Plan plan =
        planner.plan(
            "SELECT * FROM TABLE(SESSION(TABLE trades_sparse, DESCRIPTOR(ts), "
                + "DESCRIPTOR(symbol), INTERVAL '2' HOUR))");

    List<Rel> sessions = nodes(plan, Rel.KindCase.SESSION);
    assertThat(sessions).hasSize(1);
    assertThat(sessions.get(0).getSession().getPartitionKeysList()).containsExactly(0);
    assertThat(sessions.get(0).getSession().getTimeColumn()).isEqualTo(1);
    assertThat(sessions.get(0).getSession().getGap().getLiteral().getIntervalDayValue())
        .isEqualTo(7_200_000_000L);
  }

  /**
   * A hop appends two columns and reorders nothing, so an ordering its input can deliver passes
   * straight through it: {@code ORDER BY ts, symbol} over the declared collation needs no sort, and
   * the node says so in its claimed collations.
   */
  @Test
  void a_hop_passes_an_ordering_through_to_its_input() {
    String sql =
        "SELECT * FROM TABLE(HOP(TABLE bars_small, DESCRIPTOR(ts), "
            + "INTERVAL '5' MINUTE, INTERVAL '15' MINUTE)) ORDER BY ts, symbol";

    assertThat(planText(sql)).contains("ChalkHop").doesNotContain("ChalkSort");
    assertThat(nodes(planner.plan(sql), Rel.KindCase.HOP).get(0).getCollationsCount())
        .isGreaterThan(0);
  }

  /** V24: {@code PARTITION BY} on a window table function is a Calcite validation error. */
  @Test
  void session_with_a_partition_by_clause_is_a_validation_error() {
    assertThatThrownBy(
            () -> planner.plan(
                "SELECT * FROM TABLE(SESSION(TABLE trades_sparse PARTITION BY symbol, "
                    + "DESCRIPTOR(ts), INTERVAL '2' HOUR))"))
        .hasStackTraceContaining("set semantics");
  }

  @Test
  void a_hop_whose_shape_the_rule_cannot_read_is_unsupported() {
    // Two operands: HOP needs a slide and a size.
    assertThatThrownBy(
            () -> planner.plan(
                "SELECT * FROM TABLE(HOP(TABLE bars_small, DESCRIPTOR(ts), INTERVAL '5' MINUTE))"))
        .hasMessageContaining("HOP");
  }

  // ---- the holistic aggregates (D57) ------------------------------------------------------------

  @Test
  void percentile_within_group_carries_its_ordering_to_the_ir() {
    Plan plan =
        planner.plan(
            "SELECT symbol, PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY \"close\") "
                + "FROM bars_small GROUP BY symbol");

    var aggregate = nodes(plan, Rel.KindCase.HASH_AGGREGATE).get(0).getHashAggregate().getAggregate();
    assertThat(aggregate.getMeasures(0).getFunction())
        .isEqualTo(AggregateFunctionId.AGGREGATE_FUNCTION_ID_PERCENTILE_CONT);
    assertThat(aggregate.getMeasures(0).getOrderByCount()).isEqualTo(1);
  }

  @Test
  void mode_reaches_the_ir_in_both_contexts() {
    Plan grouped = planner.plan("SELECT symbol, MODE(trade_count) FROM bars_small GROUP BY symbol");
    assertThat(
            nodes(grouped, Rel.KindCase.HASH_AGGREGATE)
                .get(0)
                .getHashAggregate()
                .getAggregate()
                .getMeasures(0)
                .getFunction())
        .isEqualTo(AggregateFunctionId.AGGREGATE_FUNCTION_ID_MODE);

    Plan windowed =
        planner.plan(
            "SELECT MODE(trade_count) OVER (PARTITION BY symbol ORDER BY ts) FROM bars_small");
    assertThat(nodes(windowed, Rel.KindCase.WINDOW).get(0).getWindow().getCalls(0).getAggregate())
        .isEqualTo(AggregateFunctionId.AGGREGATE_FUNCTION_ID_MODE);
  }

  // ---- the libraries option (D60) ---------------------------------------------------------------

  @Test
  void a_library_function_needs_its_library() {
    assertThatThrownBy(
            () -> planner.plan(
                "SELECT quote, STRING_AGG(symbol, ', ') FROM symbols GROUP BY quote"))
        .hasMessageContaining("STRING_AGG");

    Plan plan =
        planner.plan(
            "SELECT quote, STRING_AGG(symbol, ', ' ORDER BY symbol) FROM symbols GROUP BY quote",
            PushdownPolicy.full(),
            SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(SqlLibrary.POSTGRESQL));

    // Calcite maps PostgreSQL's STRING_AGG onto LISTAGG during validation (ADR 0018).
    assertThat(
            nodes(plan, Rel.KindCase.HASH_AGGREGATE)
                .get(0)
                .getHashAggregate()
                .getAggregate()
                .getMeasures(0)
                .getFunction())
        .isEqualTo(AggregateFunctionId.AGGREGATE_FUNCTION_ID_LISTAGG);
  }

  @Test
  void array_agg_needs_postgresql_and_produces_a_list() {
    Plan plan =
        planner.plan(
            "SELECT symbol, ARRAY_AGG(\"close\" ORDER BY ts) AS closes "
                + "FROM bars_small GROUP BY symbol",
            PushdownPolicy.full(),
            SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(SqlLibrary.POSTGRESQL));

    assertThat(plan.getOutputType().getFields(1).getType().getKind())
        .isEqualTo(chalk.ir.v1.TypeKind.TYPE_KIND_LIST);
    assertThat(plan.getOutputType().getFields(1).getType().getElement().getKind())
        .isEqualTo(chalk.ir.v1.TypeKind.TYPE_KIND_FP64);
  }

  // ---- UNNEST (D66) -----------------------------------------------------------------------------

  @Test
  void unnest_of_a_constant_array_is_an_unnest_over_a_virtual_table() {
    Plan plan = planner.plan("SELECT * FROM UNNEST(ARRAY[1, 2, 3]) AS t(x)");

    assertThat(nodes(plan, Rel.KindCase.UNNEST)).hasSize(1);
    assertThat(nodes(plan, Rel.KindCase.VIRTUAL_TABLE)).hasSize(1);
  }

  @Test
  void unnest_with_ordinality_appends_a_position() {
    Plan plan = planner.plan("SELECT * FROM UNNEST(ARRAY[1, 2, 3]) WITH ORDINALITY AS t(x, n)");

    assertThat(nodes(plan, Rel.KindCase.UNNEST).get(0).getUnnest().getWithOrdinality()).isTrue();
    assertThat(plan.getOutputType().getFieldsCount()).isEqualTo(2);
  }

  @Test
  void unnest_of_two_arrays_is_unsupported() {
    assertThatThrownBy(
            () -> planner.plan(
                "SELECT * FROM (VALUES (ARRAY[1,2], ARRAY[3,4])) AS t(a, b), "
                    + "UNNEST(t.a, t.b) AS u(x, y)"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("could not be decorrelated");
  }

  // ---- LATERAL (D67, D68) -----------------------------------------------------------------------

  @Test
  void cross_apply_needs_a_lenient_conformance() {
    assertThatThrownBy(
            () -> planner.plan(
                "SELECT s.symbol, b.ts FROM symbols s CROSS APPLY "
                    + "(SELECT ts FROM bars_small b WHERE b.symbol = s.symbol) b"))
        .hasMessageContaining("APPLY");

    Plan plan =
        planner.plan(
            "SELECT s.symbol, b.ts FROM symbols s OUTER APPLY "
                + "(SELECT ts FROM bars_small b WHERE b.symbol = s.symbol AND b.volume > 999999 "
                + "LIMIT 1) b",
            PushdownPolicy.full(),
            SqlConformanceEnum.LENIENT);

    assertThat(nodes(plan, Rel.KindCase.WINDOW)).isNotEmpty();
  }

  /**
   * V26's residual shape: a non-{@code COUNT} aggregate on the inner side of a
   * {@code CROSS JOIN LATERAL}. Calcite's general decorrelator widens the measure to nullable over
   * an inner join that cannot produce a NULL, and the first rule that merges the projection back
   * together disagrees with it. D67 turns that into {@code UNSUPPORTED} naming the shape.
   */
  @Test
  void the_one_lateral_shape_that_cannot_be_decorrelated_names_itself() {
    assertThatThrownBy(
            () -> planner.plan(
                "SELECT s.symbol, x.m FROM symbols s, LATERAL "
                    + "(SELECT MAX(b.\"close\") AS m FROM bars_small b WHERE b.symbol = s.symbol) x"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("could not be decorrelated")
        .hasMessageContaining("LATERAL");
  }

  /** The same query as {@code LEFT JOIN LATERAL … ON TRUE} decorrelates into a grouped join. */
  @Test
  void a_left_lateral_aggregate_decorrelates_into_a_join() {
    String text =
        planText(
            "SELECT s.symbol, x.m FROM symbols s LEFT JOIN LATERAL "
                + "(SELECT MAX(b.\"close\") AS m FROM bars_small b WHERE b.symbol = s.symbol) x "
                + "ON TRUE");

    assertThat(text).doesNotContain("Correlate");
    assertThat(text).contains("Join");
  }
}
