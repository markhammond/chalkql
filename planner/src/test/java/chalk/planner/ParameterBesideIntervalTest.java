package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.DialectProfile;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.ir.v1.RemoteQuery;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.ir.IrVersionGate;
import chalk.planner.ir.RelToIr;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.Test;

/**
 * F192: a parameter beside an interval in datetime arithmetic takes the datetime type the
 * arithmetic stands beside, where Calcite gave it the interval's and the statement was refused.
 * The arithmetic is then sent to a source only as F238 lets interval arithmetic go, so the
 * statement answers the same over every source.
 *
 * <p>Planned against the declared catalog, whose {@code bars.ts} is a {@code TIMESTAMP(9)}, and
 * the same {@code bars} in a remote schema {@code db} of each dialect.
 */
class ParameterBesideIntervalTest {

  // ---- F192: the parameter's type ----

  /** The statement F192 names: the parameter is the timestamp it is compared with, not the interval. */
  @Test
  void a_parameter_minus_an_interval_compared_with_a_timestamp_is_a_timestamp() {
    assertThat(parameterTypes("SELECT symbol FROM bars WHERE ts >= ? - INTERVAL '6' HOUR"))
        .containsExactly("TIMESTAMP(9)");
  }

  /** Either way round: the comparison's first operand is derived first, and is the arithmetic here. */
  @Test
  void the_comparison_may_stand_either_way_round() {
    assertThat(parameterTypes("SELECT symbol FROM bars WHERE ? - INTERVAL '6' HOUR <= ts"))
        .containsExactly("TIMESTAMP(9)");
  }

  /** A BETWEEN's bound, an interval added before the parameter, and arithmetic nested in arithmetic. */
  @Test
  void a_bound_an_interval_added_first_and_nested_arithmetic_type_alike() {
    assertThat(parameterTypes("SELECT symbol FROM bars WHERE ts BETWEEN ? - INTERVAL '1' DAY AND ?"))
        .containsExactly("TIMESTAMP(9)", "TIMESTAMP(9)");
    assertThat(parameterTypes("SELECT symbol FROM bars WHERE ts < INTERVAL '1' HOUR + ?"))
        .containsExactly("TIMESTAMP(9)");
    assertThat(
            parameterTypes(
                "SELECT symbol FROM bars WHERE ts >= (? - INTERVAL '1' DAY) + INTERVAL '2' HOUR"))
        .containsExactly("TIMESTAMP(9)");
  }

  /** The type is the datetime's, whatever it is: a date compared with a date takes a date. */
  @Test
  void a_parameter_beside_an_interval_takes_the_datetime_its_arithmetic_meets() {
    assertThat(
            parameterTypes(
                "SELECT symbol FROM bars WHERE CAST(ts AS DATE) >= ? - INTERVAL '7' DAY"))
        .containsExactly("DATE");
  }

  /**
   * Alone, a parameter beside an interval could as well be an interval, and is typed as before: a
   * declared parameter type or a {@code CAST} says which.
   */
  @Test
  void alone_a_parameter_beside_an_interval_is_typed_as_before() {
    assertThat(parameterTypes("SELECT ? - INTERVAL '1' HOUR AS t FROM bars"))
        .singleElement()
        .satisfies(type -> assertThat(type).startsWith("INTERVAL"));
  }

  /** Arithmetic with no datetime beside it is refused as before, rather than guessed. */
  @Test
  void parameters_on_both_sides_still_say_nothing_of_their_type() {
    assertThatThrownBy(
            () -> parameterTypes("SELECT symbol FROM bars WHERE ? - INTERVAL '1' HOUR >= ?"))
        .hasMessageContaining("planning failed");
  }

  // ---- where the arithmetic runs ----

  /** The statement F192 now admits, over SQLite: the parameter's arithmetic stays with Chalk. */
  @Test
  void a_parameter_beside_an_interval_is_never_sent_to_sqlite() {
    String sql = "SELECT symbol FROM db.bars WHERE CAST(ts AS TIMESTAMP(3)) >= ? - INTERVAL '6' HOUR";

    assertThat(remoteSql(TestCatalogs.sqliteProfile(), sql)).doesNotContain("INTERVAL");
    assertThat(remoteSql(TestCatalogs.duckDbProfile(), sql)).contains("? - INTERVAL '6' HOUR");
  }

  // ---- the scaffolding ----

  /** Each parameter's type, as {@code KIND(precision)} where the kind carries one. */
  private static List<String> parameterTypes(String sql) {
    Plan plan = plan(TestCatalogs.declared(), sql);
    List<String> types = new ArrayList<>();
    for (Type type : plan.getParameterTypesList()) {
      String kind = type.getKind().name().substring("TYPE_KIND_".length());
      types.add(
          type.getKind() == TypeKind.TYPE_KIND_TIMESTAMP ? kind + "(" + type.getPrecision() + ")" : kind);
    }
    return types;
  }

  /** The one query sent to the remote schema {@code db} of this dialect. */
  private static String remoteSql(DialectProfile profile, String sql) {
    Plan plan =
        plan(
            TestCatalogs.withRemote("db", TestCatalogs.fullSqlCapabilities().build(), profile),
            sql);
    List<RemoteQuery> found = new ArrayList<>();
    collect(plan.getRoot(), found);
    assertThat(found).as("a RemoteQuery for: " + sql).hasSize(1);
    return found.get(0).getQueryText();
  }

  private static Plan plan(chalk.ir.v1.CatalogContext context, String sql) {
    RegisteredCatalog catalog = new CatalogRegistry().register(context);
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      PlannerPipeline.Result result = pipeline.plan(sql, false);
      return new RelToIr(
              new chalk.planner.types.TypeMapper(result.physical().getCluster().getTypeFactory()),
              result.physical().getCluster().getRexBuilder(),
              org.apache.calcite.rel.metadata.RelMetadataQuery.instance(),
              IrVersionGate.current())
          .toPlan(result.physical(), result.parameterRowType(), "test", 1L);
    } catch (Exception failure) {
      throw new AssertionError("planning failed for: " + sql, failure);
    }
  }

  private static void collect(Rel rel, List<RemoteQuery> into) {
    if (rel.getKindCase() == Rel.KindCase.REMOTE_QUERY) {
      into.add(rel.getRemoteQuery());
      return;
    }
    for (Rel input : IrNodes.inputs(rel)) {
      collect(input, into);
    }
  }
}
