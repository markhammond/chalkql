package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.DialectProfile;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.ir.v1.RemoteQuery;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.ir.IrVersionGate;
import chalk.planner.ir.RelToIr;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SourceDialects;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.avatica.util.TimeUnit;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.sql.SqlIntervalQualifier;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.type.SqlTypeFactoryImpl;
import org.junit.jupiter.api.Test;

/**
 * F238: interval arithmetic is sent only to a source whose dialect has an interval type. SQLite has
 * none, and {@code ts + INTERVAL '6' HOUR} sent there was a syntax error wherever the timestamps'
 * precision let the comparison be pushed; it now stays with Chalk, and DuckDB and PostgreSQL are
 * sent it as before.
 *
 * <p>Planned against {@code bars} in a remote schema {@code db} of each dialect.
 */
class IntervalPushdownTest {

  /**
   * At a precision every dialect here can hold, the comparison goes to DuckDB and PostgreSQL as
   * written, and never to SQLite, which has no interval and stays with Chalk evaluating it.
   */
  @Test
  void interval_arithmetic_is_sent_only_where_the_dialect_has_intervals() {
    String sql = "SELECT symbol FROM db.bars WHERE CAST(ts AS TIMESTAMP(0)) + INTERVAL '6' HOUR >= ?";

    assertThat(remoteSql(TestCatalogs.sqliteProfile(), sql)).doesNotContain("INTERVAL");
    assertThat(remoteSql(TestCatalogs.duckDbProfile(), sql)).contains("INTERVAL '6' HOUR");
    assertThat(remoteSql(TestCatalogs.postgresProfile(), sql)).contains("INTERVAL '6' HOUR");
  }

  /** Only the SQLite dialect says it has no interval type; the others keep Calcite's answer. */
  @Test
  void only_the_sqlite_dialect_has_no_interval_type() {
    RelDataType interval =
        new SqlTypeFactoryImpl(org.apache.calcite.rel.type.RelDataTypeSystem.DEFAULT)
            .createSqlIntervalType(new SqlIntervalQualifier(TimeUnit.HOUR, null, SqlParserPos.ZERO));

    assertThat(SourceDialects.of(TestCatalogs.sqliteProfile()).supportsDataType(interval)).isFalse();
    assertThat(SourceDialects.of(TestCatalogs.duckDbProfile()).supportsDataType(interval)).isTrue();
    assertThat(SourceDialects.of(TestCatalogs.postgresProfile()).supportsDataType(interval)).isTrue();
  }

  // ---- the scaffolding ----

  /** The one query sent to the remote schema {@code db} of this dialect. */
  private static String remoteSql(DialectProfile profile, String sql) {
    RegisteredCatalog catalog =
        new CatalogRegistry()
            .register(
                TestCatalogs.withRemote("db", TestCatalogs.fullSqlCapabilities().build(), profile));
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      PlannerPipeline.Result result = pipeline.plan(sql, false);
      Plan plan =
          new RelToIr(
                  new chalk.planner.types.TypeMapper(result.physical().getCluster().getTypeFactory()),
                  result.physical().getCluster().getRexBuilder(),
                  org.apache.calcite.rel.metadata.RelMetadataQuery.instance(),
                  IrVersionGate.current())
              .toPlan(result.physical(), result.parameterRowType(), "test", 1L);
      List<RemoteQuery> found = new ArrayList<>();
      collect(plan.getRoot(), found);
      assertThat(found).as("a RemoteQuery for: " + sql).hasSize(1);
      return found.get(0).getQueryText();
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
