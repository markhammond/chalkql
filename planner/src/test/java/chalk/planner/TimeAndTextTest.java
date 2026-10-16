package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.DialectProfile;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.ir.v1.RemoteQuery;
import chalk.ir.v1.Type;
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
 * F191: {@code CURRENT_TIME} and {@code LOCALTIME} are the time of day of the execution's instant,
 * written as the time of day of {@code CURRENT_TIMESTAMP}. F193: a date or a time cast to text is
 * Chalk's text, so the cast is never sent to a source, which writes its own.
 *
 * <p>Planned against the declared catalog, whose {@code bars.ts} is a {@code TIMESTAMP(9)}, and the
 * same {@code bars} in a remote schema {@code db} of each dialect.
 */
class TimeAndTextTest {

  // ---- F191: the time of day ----

  /** A time of precision 6 when none is written, as a timestamp is, and the precision written when one is. */
  @Test
  void current_time_and_localtime_are_times_of_precision_six_unless_written() {
    Plan plan =
        plan(
            TestCatalogs.declared(),
            "SELECT CURRENT_TIME AS t, LOCALTIME AS l, CURRENT_TIME(0) AS c FROM bars");

    assertThat(outputTypes(plan)).containsExactly("TIME(6)", "TIME(6)", "TIME(0)");
  }

  /** TIME holds microseconds, so a seventh digit is refused, naming the function. */
  @Test
  void a_time_finer_than_a_microsecond_is_refused() {
    assertThatThrownBy(() -> plan(TestCatalogs.declared(), "SELECT CURRENT_TIME(7) AS t FROM bars"))
        .rootCause()
        .hasMessageContaining("CURRENT_TIME");
  }

  /** The plan reads the execution's instant once, as the timestamp, and takes its time of day. */
  @Test
  void current_time_is_the_time_of_day_of_the_current_timestamp() {
    String plan = plan(TestCatalogs.declared(), "SELECT CURRENT_TIME AS t FROM bars").getRoot().toString();

    assertThat(plan).contains("FUNCTION_ID_CURRENT_TIMESTAMP").contains("cast");
  }

  // ---- F193: text, and where it is written ----

  /**
   * A timestamp cast to text is written by Chalk for every dialect, and a number cast to text is
   * still sent as before; so is neither half of a comparison with the current time.
   */
  @Test
  void a_source_is_sent_neither_a_date_as_text_nor_the_current_time() {
    for (DialectProfile profile :
        List.of(TestCatalogs.sqliteProfile(), TestCatalogs.duckDbProfile(), TestCatalogs.postgresProfile())) {
      assertThat(remoteSql(profile, "SELECT CAST(ts AS VARCHAR) AS t FROM db.bars"))
          .as(profile.getDialect())
          .doesNotContain("CAST");
      assertThat(remoteSql(profile, "SELECT CAST(volume AS VARCHAR) AS v FROM db.bars"))
          .as(profile.getDialect())
          .contains("CAST(\"volume\"");
      assertThat(remoteSql(profile, "SELECT symbol FROM db.bars WHERE CURRENT_TIME > TIME '12:00:00'"))
          .as(profile.getDialect())
          .doesNotContain("CURRENT");
    }
  }

  // ---- the scaffolding ----

  /** Each output column's type, as {@code KIND(precision)} where the kind carries one. */
  private static List<String> outputTypes(Plan plan) {
    List<String> types = new ArrayList<>();
    for (var field : plan.getOutputType().getFieldsList()) {
      Type type = field.getType();
      types.add(type.getKind().name().substring("TYPE_KIND_".length()) + "(" + type.getPrecision() + ")");
    }
    return types;
  }

  /** The one query sent to the remote schema {@code db} of this dialect. */
  private static String remoteSql(DialectProfile profile, String sql) {
    Plan plan =
        plan(TestCatalogs.withRemote("db", TestCatalogs.fullSqlCapabilities().build(), profile), sql);
    List<RemoteQuery> found = new ArrayList<>();
    collect(plan.getRoot(), found);
    assertThat(found).as("a RemoteQuery for: " + sql).hasSize(1);
    return found.get(0).getQueryText();
  }

  private static Plan plan(CatalogContext context, String sql) {
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
