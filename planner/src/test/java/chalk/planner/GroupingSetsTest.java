package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.Aggregate;
import chalk.ir.v1.AggregateFunctionId;
import chalk.ir.v1.Grouping;
import chalk.ir.v1.Measure;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import java.util.List;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * {@code GROUPING SETS}, {@code ROLLUP} and {@code CUBE} as one aggregate with a grouping for each
 * set (D338, design 63 §1). Each was "cannot plan": the hash aggregate's rule declined grouping
 * sets, and the IR writer refused more than one grouping.
 */
class GroupingSetsTest {
  private static CorpusPlanner planner;

  @BeforeAll
  static void createPlanner() {
    planner = new CorpusPlanner();
  }

  private static Aggregate onlyAggregate(Plan plan) {
    List<Rel> aggregates = IrNodes.of(plan, Rel.KindCase.HASH_AGGREGATE);
    assertThat(aggregates).hasSize(1);
    return aggregates.get(0).getHashAggregate().getAggregate();
  }

  private static List<List<Integer>> sets(Aggregate aggregate) {
    return aggregate.getGroupingsList().stream().map(Grouping::getKeysList).toList();
  }

  /** Two rollups side by side are their product, one aggregate of four sets: a cube. */
  @Test
  void two_rollups_side_by_side_are_one_aggregate_of_four_sets() {
    Aggregate aggregate =
        onlyAggregate(
            planner.plan(
                "SELECT region, label, COUNT(*) AS n FROM sales GROUP BY ROLLUP(region), ROLLUP(label)"));

    assertThat(sets(aggregate))
        .containsExactly(List.of(0, 1), List.of(0), List.of(1), List.of());
  }

  /**
   * The output's keys are the union of the sets, and a key some set leaves out is nullable even
   * where its column is not: {@code region} is NOT NULL in {@code sales}.
   */
  @Test
  void a_rolled_up_key_is_nullable() {
    Plan plan = planner.plan("SELECT region, COUNT(*) AS n FROM sales GROUP BY ROLLUP(region)");
    Rel aggregate = IrNodes.of(plan, Rel.KindCase.HASH_AGGREGATE).get(0);

    assertThat(sets(aggregate.getHashAggregate().getAggregate()))
        .containsExactly(List.of(0), List.of());
    assertThat(aggregate.getRowType().getFields(0).getType().getNullable()).isTrue();
  }

  /**
   * {@code GROUPING} and {@code GROUPING_ID} are one function, a measure over the keys it names,
   * a non-null BIGINT.
   */
  @Test
  void grouping_and_grouping_id_are_one_measure() {
    Aggregate aggregate =
        onlyAggregate(
            planner.plan(
                "SELECT region, label, GROUPING(region, label) AS g, GROUPING_ID(label) AS gi"
                    + " FROM sales GROUP BY CUBE(region, label)"));

    List<Measure> measures = aggregate.getMeasuresList();
    assertThat(measures)
        .extracting(Measure::getFunction)
        .containsExactly(
            AggregateFunctionId.AGGREGATE_FUNCTION_ID_GROUPING,
            AggregateFunctionId.AGGREGATE_FUNCTION_ID_GROUPING);
    assertThat(measures.get(0).getArgsList())
        .extracting(arg -> arg.getFieldRef().getIndex())
        .containsExactly(0, 1);
    assertThat(measures.get(1).getArgsList())
        .extracting(arg -> arg.getFieldRef().getIndex())
        .containsExactly(1);
    assertThat(measures.get(0).getType().getNullable()).isFalse();
  }

  /**
   * {@code GROUP_ID()} plans: Calcite numbers a repeated set itself, as a literal in each branch
   * of a union of aggregates, so no aggregate carries it.
   */
  @Test
  void group_id_numbers_a_repeated_set_through_a_union() {
    Plan plan =
        planner.plan(
            "SELECT region, GROUP_ID() AS gid, COUNT(*) AS n FROM sales"
                + " GROUP BY GROUPING SETS ((region), (region), ())");

    assertThat(IrNodes.of(plan, Rel.KindCase.SET_OP)).hasSize(1);
    assertThat(IrNodes.of(plan, Rel.KindCase.HASH_AGGREGATE)).hasSize(2);
  }

  /**
   * A DISTINCT aggregate under grouping sets is refused by name on a small table and on a large
   * one alike: the join expansion would rewrite it over {@code bars}, and leave it over {@code
   * sales}, so the refusal comes first.
   */
  @Test
  void a_distinct_aggregate_with_grouping_sets_is_refused_by_name_whatever_the_table() {
    for (String sql :
        List.of(
            "SELECT region, COUNT(DISTINCT amount) AS n FROM sales GROUP BY ROLLUP(region)",
            "SELECT symbol, COUNT(DISTINCT trade_count) AS n FROM bars GROUP BY ROLLUP(symbol)")) {
      assertThatThrownBy(() -> planner.plan(sql))
          .isInstanceOf(UnsupportedFeatureException.class)
          .hasMessageContaining("COUNT(DISTINCT …) with GROUPING SETS, ROLLUP or CUBE")
          .hasMessageContaining("UNION ALL");
    }
  }

  /** One set is a plain GROUP BY, and a DISTINCT aggregate in it stays as it was. */
  @Test
  void a_single_grouping_set_keeps_its_distinct_aggregate() {
    Aggregate aggregate =
        onlyAggregate(
            planner.plan(
                "SELECT region, COUNT(DISTINCT amount) AS n FROM sales GROUP BY GROUPING SETS ((region))"));

    assertThat(sets(aggregate)).containsExactly(List.of(0));
    assertThat(aggregate.getMeasures(0).getDistinct()).isTrue();
  }

  /** No source is asked for grouping sets: the aggregate runs in the host over the pushed scan. */
  @Test
  void grouping_sets_are_never_pushed_to_a_source() {
    RegisteredCatalog catalog =
        new CatalogRegistry()
            .register(
                TestCatalogs.withRemote(
                    "db", TestCatalogs.fullSqlCapabilities().build(), TestCatalogs.duckDbProfile()));
    String text;
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      text =
          pipeline
              .plan(
                  "SELECT l_returnflag, l_linestatus, SUM(l_quantity) FROM db.lineitem"
                      + " GROUP BY ROLLUP(l_returnflag, l_linestatus)",
                  true)
              .physicalPlanText();
    } catch (Exception e) {
      throw new AssertionError(e);
    }

    assertThat(text).contains("ChalkHashAggregate").doesNotContain("SourceAggregate");
  }
}
