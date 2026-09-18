package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.Column;
import chalk.ir.v1.RowCountKind;
import chalk.ir.v1.Schema;
import chalk.ir.v1.Table;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.rpc.PlanErrors;
import chalk.planner.rpc.v1.ColumnStatisticsEntry;
import chalk.planner.rpc.v1.RegisterCatalogRequest;
import chalk.planner.rpc.v1.RegisterStatisticsRequest;
import chalk.planner.rpc.v1.TableStatistics;
import java.time.Duration;
import java.util.concurrent.atomic.AtomicLong;
import org.junit.jupiter.api.Test;

/**
 * The bounded, versioned registry of D271 (d) and (e): deltas applied atomically, a bound on the
 * predecessors one instance keeps, idle eviction of a whole instance, and the statistics version
 * joined with whichever shape a plan names.
 */
final class CatalogVersionRegistryTest {
  private static final String INSTANCE = "01JENGINEINSTANCEXXXXXXXXX";

  @Test
  void a_version_is_addressable_and_the_newest_is_the_default() {
    CatalogRegistry registry = new CatalogRegistry();
    registry.register(request("v1", catalog(1, table("orders", "id", "total"))));
    registry.register(request("v2", catalog(2, table("orders", "id", "total", "region"))));

    assertThat(columns(registry.find(INSTANCE, "v1").orElseThrow(), "orders")).isEqualTo(2);
    assertThat(columns(registry.find(INSTANCE, "v2").orElseThrow(), "orders")).isEqualTo(3);
    // A request that names no version — a client written before D271 — gets the newest.
    assertThat(columns(registry.find(INSTANCE).orElseThrow(), "orders")).isEqualTo(3);
    assertThat(registry.versions(INSTANCE)).isEqualTo(2);
    assertThat(registry.size()).isEqualTo(1);
  }

  @Test
  void a_version_already_held_is_installed_once() {
    CatalogRegistry registry = new CatalogRegistry();
    CatalogRegistry.Registration first =
        registry.register(request("v1", catalog(1, table("orders", "id"))));
    CatalogRegistry.Registration again =
        registry.register(request("v1", catalog(1, table("orders", "id"))));

    assertThat(first.installed()).isTrue();
    assertThat(again.installed()).isFalse();
    assertThat(again.catalog()).isSameAs(first.catalog());
  }

  @Test
  void a_delta_names_only_the_changed_table_and_is_applied_atomically() {
    CatalogRegistry registry = new CatalogRegistry();
    registry.register(
        request("v1", catalog(1, table("orders", "id", "total"), table("notes", "id"))));

    // Only `orders` travels, carrying its whole descriptor; `notes` is the base's.
    CatalogContext delta =
        CatalogContext.newBuilder()
            .setContextId(INSTANCE)
            .setEpoch(2)
            .addSchemas(
                Schema.newBuilder()
                    .setSourceId("mem")
                    .setName("main")
                    .setKind(chalk.ir.v1.SourceKind.SOURCE_KIND_LOCAL)
                    .addTables(table("orders", "id", "total", "region")))
            .build();
    registry.register(
        RegisterCatalogRequest.newBuilder()
            .setInstanceId(INSTANCE)
            .setShapeVersion("v2")
            .setBaseShapeVersion("v1")
            .setCatalog(delta)
            .build());

    RegisteredCatalog v2 = registry.find(INSTANCE, "v2").orElseThrow();
    assertThat(columns(v2, "orders")).isEqualTo(3);
    assertThat(columns(v2, "notes")).isEqualTo(1);
    assertThat(v2.epoch()).isEqualTo(2L);
    // The base is untouched, which is what a plan already made against it still needs.
    assertThat(columns(registry.find(INSTANCE, "v1").orElseThrow(), "orders")).isEqualTo(2);
  }

  @Test
  void a_delta_removes_a_table_by_name() {
    CatalogRegistry registry = new CatalogRegistry();
    registry.register(
        request("v1", catalog(1, table("orders", "id"), table("notes", "id"))));
    registry.register(
        RegisterCatalogRequest.newBuilder()
            .setInstanceId(INSTANCE)
            .setShapeVersion("v2")
            .setBaseShapeVersion("v1")
            .addRemovedTables("main.notes")
            .setCatalog(CatalogContext.newBuilder().setContextId(INSTANCE).setEpoch(2).build())
            .build());

    RegisteredCatalog v2 = registry.find(INSTANCE, "v2").orElseThrow();
    assertThat(v2.descriptor().getSchemas(0).getTablesList())
        .extracting(Table::getName)
        .containsExactly("orders");
  }

  @Test
  void a_delta_whose_base_is_gone_is_refused_by_name() {
    CatalogRegistry registry = new CatalogRegistry();
    registry.register(request("v1", catalog(1, table("orders", "id"))));

    assertThatThrownBy(
            () ->
                registry.register(
                    RegisterCatalogRequest.newBuilder()
                        .setInstanceId(INSTANCE)
                        .setShapeVersion("v9")
                        .setBaseShapeVersion("v8")
                        .setCatalog(
                            CatalogContext.newBuilder().setContextId(INSTANCE).setEpoch(9).build())
                        .build()))
        .isInstanceOf(PlanErrors.UnknownCatalogVersionException.class)
        .hasMessageContaining("v8");
  }

  @Test
  void beyond_the_bound_the_least_recently_used_version_goes() {
    CatalogRegistry registry = new CatalogRegistry(2, Duration.ofMinutes(30));
    registry.register(request("v1", catalog(1, table("orders", "id"))));
    registry.register(request("v2", catalog(2, table("orders", "id"))));
    // Reading v1 makes v2 the least recently used one.
    assertThat(registry.find(INSTANCE, "v1")).isPresent();
    registry.register(request("v3", catalog(3, table("orders", "id"))));

    assertThat(registry.versions(INSTANCE)).isEqualTo(2);
    assertThat(registry.find(INSTANCE, "v2")).isEmpty();
    assertThat(registry.find(INSTANCE, "v1")).isPresent();
    assertThat(registry.find(INSTANCE, "v3")).isPresent();
  }

  @Test
  void an_idle_instance_is_evicted_whole() {
    AtomicLong now = new AtomicLong();
    CatalogRegistry registry = new CatalogRegistry(4, Duration.ofMinutes(5), now::get);
    registry.register(request("v1", catalog(1, table("orders", "id"))));
    assertThat(registry.find(INSTANCE, "v1")).isPresent();

    now.addAndGet(Duration.ofMinutes(6).toNanos());
    assertThat(registry.find(INSTANCE, "v1")).isEmpty();
    assertThat(registry.size()).isZero();
  }

  @Test
  void statistics_join_the_shape_a_plan_names() {
    CatalogRegistry registry = new CatalogRegistry();
    registry.register(request("v1", catalog(1, table("orders", "id", "total"))));
    assertThat(rowCount(registry.find(INSTANCE, "v1").orElseThrow(), "orders")).isEqualTo(0L);

    registry.registerStatistics(
        RegisterStatisticsRequest.newBuilder()
            .setInstanceId(INSTANCE)
            .setStatisticsVersion("s1")
            .addTables(
                TableStatistics.newBuilder()
                    .setSchema("main")
                    .setTable("orders")
                    .setRowCount(4242)
                    .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
                    .addColumns(
                        ColumnStatisticsEntry.newBuilder()
                            .setColumn("total")
                            .setStatistics(
                                chalk.ir.v1.ColumnStatistics.newBuilder().setDistinctCount(7))))
            .build());

    RegisteredCatalog joined = registry.find(INSTANCE, "v1").orElseThrow();
    assertThat(rowCount(joined, "orders")).isEqualTo(4242L);
    assertThat(statistics(joined, "orders", "total").getDistinctCount()).isEqualTo(7L);
    assertThat(registry.statisticsVersion(INSTANCE)).isEqualTo("s1");

    // And a shape registered afterwards is joined with the same newest statistics.
    registry.register(request("v2", catalog(2, table("orders", "id", "total", "region"))));
    assertThat(rowCount(registry.find(INSTANCE, "v2").orElseThrow(), "orders")).isEqualTo(4242L);
  }

  @Test
  void a_later_statistics_message_merges_over_the_one_before_it() {
    CatalogRegistry registry = new CatalogRegistry();
    registry.register(
        request("v1", catalog(1, table("orders", "id"), table("notes", "id"))));
    registry.registerStatistics(statistics("s1", "orders", 10));
    registry.registerStatistics(statistics("s2", "notes", 20));

    RegisteredCatalog joined = registry.find(INSTANCE, "v1").orElseThrow();
    assertThat(rowCount(joined, "orders")).isEqualTo(10L);
    assertThat(rowCount(joined, "notes")).isEqualTo(20L);
  }

  @Test
  void a_statistics_message_that_arrives_out_of_order_is_ignored() {
    CatalogRegistry registry = new CatalogRegistry();
    registry.register(request("v1", catalog(1, table("orders", "id"))));
    registry.registerStatistics(statistics("s2", "orders", 20));
    registry.registerStatistics(statistics("s1", "orders", 10));

    assertThat(rowCount(registry.find(INSTANCE, "v1").orElseThrow(), "orders")).isEqualTo(20L);
    assertThat(registry.statisticsVersion(INSTANCE)).isEqualTo("s2");
  }

  // ---------------------------------------------------------------------- fixtures

  private static RegisterStatisticsRequest statistics(String version, String table, long rows) {
    return RegisterStatisticsRequest.newBuilder()
        .setInstanceId(INSTANCE)
        .setStatisticsVersion(version)
        .addTables(
            TableStatistics.newBuilder()
                .setSchema("main")
                .setTable(table)
                .setRowCount(rows)
                .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT))
        .build();
  }

  private static RegisterCatalogRequest request(String version, CatalogContext catalog) {
    return RegisterCatalogRequest.newBuilder()
        .setInstanceId(INSTANCE)
        .setShapeVersion(version)
        .setCatalog(catalog)
        .build();
  }

  private static CatalogContext catalog(long epoch, Table... tables) {
    Schema.Builder schema =
        Schema.newBuilder()
            .setSourceId("mem")
            .setName("main")
            .setKind(chalk.ir.v1.SourceKind.SOURCE_KIND_LOCAL);
    for (Table table : tables) {
      schema.addTables(table);
    }
    return CatalogContext.newBuilder()
        .setContextId(INSTANCE)
        .setEpoch(epoch)
        .addSchemas(schema)
        .build();
  }

  private static Table table(String name, String... columns) {
    Table.Builder table = Table.newBuilder().setName(name);
    for (String column : columns) {
      table.addColumns(
          Column.newBuilder()
              .setName(column)
              .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I64).setNullable(false)));
    }
    return table.build();
  }

  private static int columns(RegisteredCatalog catalog, String table) {
    return find(catalog, table).getColumnsCount();
  }

  private static long rowCount(RegisteredCatalog catalog, String table) {
    return find(catalog, table).getRowCount();
  }

  private static chalk.ir.v1.ColumnStatistics statistics(
      RegisteredCatalog catalog, String table, String column) {
    for (Column declared : find(catalog, table).getColumnsList()) {
      if (declared.getName().equals(column)) {
        return declared.getStatistics();
      }
    }
    throw new AssertionError("no column " + column + " on " + table);
  }

  private static Table find(RegisteredCatalog catalog, String table) {
    for (Schema schema : catalog.descriptor().getSchemasList()) {
      for (Table declared : schema.getTablesList()) {
        if (declared.getName().equals(table)) {
          return declared;
        }
      }
    }
    throw new AssertionError("no table " + table);
  }
}
