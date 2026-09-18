package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.Schema;
import chalk.ir.v1.SortDirection;
import chalk.ir.v1.SourceKind;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableCollation;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.catalog.InvalidCatalogException;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.SqlConfigs;
import chalk.planner.types.ChalkTypeSystem;
import chalk.planner.types.TypeMapper;
import com.google.common.collect.ImmutableList;
import java.util.List;
import org.apache.calcite.plan.ConventionTraitDef;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptSchema;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.plan.RelTraitDef;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollationTraitDef;
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelFieldCollation;
import org.apache.calcite.rel.logical.LogicalTableScan;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.tools.FrameworkConfig;
import org.apache.calcite.tools.Frameworks;
import org.apache.calcite.util.ImmutableBitSet;
import org.junit.jupiter.api.Test;

/**
 * Catalog assembly and the Calcite facts the design depends on (docs/design/03-planner.md §9,
 * V1–V7). Outcomes are recorded in docs/adr/0001-calcite-facts.md; this test is what keeps them
 * true.
 */
class CatalogAssemblyTest {

  // Frameworks.ConfigBuilder.traitDefs takes a raw List<RelTraitDef>; there is no parameterised
  // overload, so the raw type is Calcite's and cannot be fixed here. PlannerPipeline.traitDefs()
  // suppresses the same warning for the same reason.
  @SuppressWarnings({"rawtypes", "unchecked"})
  private static FrameworkConfig config(RegisteredCatalog catalog) {
    return Frameworks.newConfigBuilder()
        .parserConfig(SqlConfigs.parser(SqlConfigs.DEFAULT_CONFORMANCE))
        .sqlValidatorConfig(SqlConfigs.validator(SqlConfigs.DEFAULT_CONFORMANCE))
        .sqlToRelConverterConfig(SqlConfigs.sqlToRel())
        .defaultSchema(catalog.defaultSchema())
        .typeSystem(ChalkTypeSystem.INSTANCE)
        .traitDefs(
            (List<RelTraitDef>)
                (List)
                    ImmutableList.of(ConventionTraitDef.INSTANCE, RelCollationTraitDef.INSTANCE))
        .executor(RexUtil.EXECUTOR)
        .build();
  }

  private interface ScanAction<R> {
    R apply(RelOptCluster cluster, RelOptTable table, LogicalTableScan scan);
  }

  /** Builds a LogicalTableScan over the named table and hands it to the action. */
  private static <R> R withScan(CatalogContext descriptor, String table, ScanAction<R> action) {
    RegisteredCatalog catalog = new CatalogRegistry().register(descriptor);
    return Frameworks.withPlanner(
        (RelOptCluster cluster, RelOptSchema relOptSchema, org.apache.calcite.schema.SchemaPlus root) -> {
          RelOptTable relOptTable = relOptSchema.getTableForMember(List.of("main", table));
          LogicalTableScan scan = LogicalTableScan.create(cluster, relOptTable, List.of());
          return action.apply(cluster, relOptTable, scan);
        },
        config(catalog));
  }

  // ---- V1: Statistics.of(Double, keys, referentialConstraints, collations) ----

  @Test
  void v1_statistics_carry_row_count_keys_and_collations_in_that_argument_order() {
    ChalkTable table = new ChalkTable(TestCatalogs.bars(), TestCatalogs.corpus().getSchemas(0));

    var statistic = table.getStatistic();

    assertThat(statistic.getRowCount()).isEqualTo(100_800.0);
    assertThat(statistic.getKeys()).containsExactly(ImmutableBitSet.of(1, 0));
    // Since step 20 the third argument carries the declared foreign keys (F14): bars.symbol
    // references symbols.symbol, source qualified name first, then target, then the column pairs.
    assertThat(statistic.getReferentialConstraints()).hasSize(1);
    assertThat(statistic.getReferentialConstraints().get(0).getSourceQualifiedName())
        .containsExactly("main", "bars");
    assertThat(statistic.getReferentialConstraints().get(0).getTargetQualifiedName())
        .containsExactly("main", "symbols");
    assertThat(statistic.getReferentialConstraints().get(0).getColumnPairs())
        .containsExactly(org.apache.calcite.util.mapping.IntPair.of(0, 0));
    assertThat(statistic.getCollations()).hasSize(1);
    assertThat(statistic.isKey(ImmutableBitSet.of(0, 1))).isTrue();
    assertThat(statistic.isKey(ImmutableBitSet.of(0))).isFalse();
  }

  @Test
  void an_unknown_row_count_is_null_not_minus_one() {
    Table unknown = TestCatalogs.bars().toBuilder().setRowCount(-1).build();

    ChalkTable table = new ChalkTable(unknown, TestCatalogs.corpus().getSchemas(0));

    assertThat(table.getStatistic().getRowCount()).isNull();
  }

  // ---- V2: LogicalTableScan derives the collation trait from the statistic ----

  @Test
  void v2_mq_collations_on_a_scan_returns_the_declared_collation() {
    List<RelCollation> collations =
        withScan(
            TestCatalogs.corpus(),
            "bars",
            (cluster, relOptTable, scan) -> RelMetadataQuery.instance().collations(scan));

    assertThat(collations)
        .containsExactly(
            RelCollations.of(
                new RelFieldCollation(
                    1,
                    RelFieldCollation.Direction.ASCENDING,
                    RelFieldCollation.NullDirection.LAST),
                new RelFieldCollation(
                    0,
                    RelFieldCollation.Direction.ASCENDING,
                    RelFieldCollation.NullDirection.LAST)));
  }

  @Test
  void the_scan_also_reports_the_declared_row_count_and_unique_key() {
    Boolean ok =
        withScan(
            TestCatalogs.corpus(),
            "bars",
            (cluster, relOptTable, scan) -> {
              RelMetadataQuery mq = RelMetadataQuery.instance();
              assertThat(mq.getRowCount(scan)).isEqualTo(100_800.0);
              assertThat(mq.areColumnsUnique(scan, ImmutableBitSet.of(0, 1))).isTrue();
              assertThat(mq.areColumnsUnique(scan, ImmutableBitSet.of(0))).isFalse();
              return true;
            });

    assertThat(ok).isTrue();
  }

  // ---- V3: RelCollation.satisfies includes the null direction ----

  @Test
  void v3_collation_satisfaction_compares_the_null_direction_too() {
    RelCollation declared =
        RelCollations.of(
            new RelFieldCollation(
                1, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.LAST));
    RelCollation wantedSame =
        RelCollations.of(
            new RelFieldCollation(
                1, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.LAST));
    RelCollation wantedNullsFirst =
        RelCollations.of(
            new RelFieldCollation(
                1, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.FIRST));

    assertThat(declared.satisfies(wantedSame)).isTrue();
    assertThat(declared.satisfies(wantedNullsFirst)).isFalse();
  }

  // ---- V4, V5, V7: the type system's limits ----

  @Test
  void v4_timestamp_precision_9_is_accepted_under_this_type_system() {
    Boolean ok =
        withScan(
            TestCatalogs.allTypes(),
            "all_types",
            (cluster, relOptTable, scan) -> {
              RelDataTypeFactory factory = cluster.getTypeFactory();
              RelDataType ts = factory.createSqlType(SqlTypeName.TIMESTAMP, 9);
              assertThat(ts.getPrecision()).isEqualTo(9);
              RelDataType tstz =
                  factory.createSqlType(SqlTypeName.TIMESTAMP_WITH_LOCAL_TIME_ZONE, 9);
              assertThat(tstz.getPrecision()).isEqualTo(9);
              return true;
            });

    assertThat(ok).isTrue();
  }

  // getMaxNumericPrecision and getMaxNumericScale are deprecated in Calcite 1.42 and are exactly
  // what V5 is about: ChalkTypeSystem overrides the per-type-name methods, and this asserts the
  // deprecated pair delegates to them, so a Calcite path that still calls the old accessors sees 38
  // too. Asserting a deprecated method is the assertion; there is nothing to fix.
  @SuppressWarnings("deprecation")
  @Test
  void v5_decimal_28_10_survives_the_type_system() {
    assertThat(ChalkTypeSystem.INSTANCE.getMaxPrecision(SqlTypeName.DECIMAL)).isEqualTo(38);
    assertThat(ChalkTypeSystem.INSTANCE.getMaxNumericPrecision()).isEqualTo(38);
    assertThat(ChalkTypeSystem.INSTANCE.getMaxNumericScale()).isEqualTo(38);

    Boolean ok =
        withScan(
            TestCatalogs.allTypes(),
            "all_types",
            (cluster, relOptTable, scan) -> {
              RelDataType decimal =
                  cluster.getTypeFactory().createSqlType(SqlTypeName.DECIMAL, 28, 10);
              assertThat(decimal.getPrecision()).isEqualTo(28);
              assertThat(decimal.getScale()).isEqualTo(10);
              return true;
            });

    assertThat(ok).isTrue();
  }

  @Test
  void v7_sql_type_name_uuid_exists_so_guid_columns_stay_uuid() {
    assertThat(SqlTypeName.UUID).isNotNull();

    Boolean ok =
        withScan(
            TestCatalogs.allTypes(),
            "all_types",
            (cluster, relOptTable, scan) -> {
              RelDataType row = scan.getRowType();
              assertThat(row.getField("c_uuid", false, false)).isNotNull();
              assertThat(row.getField("c_uuid", false, false).getType().getSqlTypeName())
                  .isEqualTo(SqlTypeName.UUID);
              return true;
            });

    assertThat(ok).isTrue();
  }

  // ---- type mapping round trip ----

  @Test
  void every_type_kind_maps_to_calcite_and_back() {
    Boolean ok =
        withScan(
            TestCatalogs.allTypes(),
            "all_types",
            (cluster, relOptTable, scan) -> {
              TypeMapper mapper = new TypeMapper(cluster.getTypeFactory());
              for (chalk.ir.v1.Column column :
                  TestCatalogs.allTypes()
                      .getSchemas(0)
                      .getTables(0)
                      .getColumnsList()) {
                Type original = column.getType();
                RelDataType calcite = mapper.toCalcite(original);
                Type back = mapper.toIr(calcite);
                assertThat(back)
                    .as("round trip of %s (%s)", column.getName(), original.getKind())
                    .isEqualTo(original);
              }
              return true;
            });

    assertThat(ok).isTrue();
  }

  @Test
  void a_string_column_is_unbounded_varchar_not_varchar_1() {
    Boolean ok =
        withScan(
            TestCatalogs.corpus(),
            "bars",
            (cluster, relOptTable, scan) -> {
              RelDataType symbol = scan.getRowType().getFieldList().get(0).getType();
              assertThat(symbol.getSqlTypeName()).isEqualTo(SqlTypeName.VARCHAR);
              assertThat(symbol.getPrecision())
                  .as("unbounded, so a 7-character symbol is not truncated")
                  .isEqualTo(RelDataType.PRECISION_NOT_SPECIFIED);
              return true;
            });

    assertThat(ok).isTrue();
  }

  @Test
  void nullability_survives_the_round_trip() {
    Boolean ok =
        withScan(
            TestCatalogs.corpus(),
            "bars",
            (cluster, relOptTable, scan) -> {
              RelDataType row = scan.getRowType();
              assertThat(row.getFieldList().get(0).getType().isNullable()).isFalse();
              assertThat(row.getFieldList().get(7).getType().isNullable()).isTrue();
              assertThat(row.getFieldList().get(8).getType().isNullable()).isTrue();
              return true;
            });

    assertThat(ok).isTrue();
  }

  // ---- scan() is load-bearing ----

  @Test
  void scan_throws_so_nobody_executes_through_calcite() {
    ChalkTable table = new ChalkTable(TestCatalogs.bars(), TestCatalogs.corpus().getSchemas(0));

    assertThatThrownBy(() -> table.scan(null))
        .isInstanceOf(UnsupportedOperationException.class)
        .hasMessageContaining("execution is client-side");
  }

  // ---- registry and validation ----

  @Test
  void registering_replaces_the_previous_catalog_under_the_same_id() {
    CatalogRegistry registry = new CatalogRegistry();

    registry.register(TestCatalogs.corpus());
    registry.register(TestCatalogs.corpus().toBuilder().setEpoch(2).build());

    assertThat(registry.size()).isEqualTo(1);
    assertThat(registry.find(TestCatalogs.CONTEXT_ID)).isPresent();
    assertThat(registry.find(TestCatalogs.CONTEXT_ID).orElseThrow().epoch()).isEqualTo(2);
    assertThat(registry.find("nope")).isEmpty();
  }

  @Test
  void a_collation_with_an_unspecified_direction_is_rejected_at_registration() {
    Table broken =
        TestCatalogs.bars().toBuilder()
            .clearCollations()
            .addCollations(
                TableCollation.newBuilder()
                    .addKeys(
                        chalk.ir.v1.KeyOrder.newBuilder()
                            .setColumn(1)
                            .setDirection(SortDirection.SORT_DIRECTION_UNSPECIFIED)))
            .build();
    CatalogContext catalog = withOnlyTable(broken);

    assertThatThrownBy(() -> new CatalogRegistry().register(catalog))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("never satisfies an ORDER BY");
  }

  @Test
  void an_out_of_range_unique_key_column_is_rejected_at_registration() {
    Table broken =
        TestCatalogs.bars().toBuilder()
            .clearUniqueKeys()
            .addUniqueKeys(chalk.ir.v1.UniqueKey.newBuilder().addColumns(99))
            .build();

    assertThatThrownBy(() -> new CatalogRegistry().register(withOnlyTable(broken)))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("out of range");
  }

  @Test
  void a_malformed_decimal_is_rejected_at_registration() {
    Table broken =
        TestCatalogs.bars().toBuilder()
            .setColumns(
                7,
                TestCatalogs.column(
                    "vwap",
                    Type.newBuilder()
                        .setKind(TypeKind.TYPE_KIND_DECIMAL)
                        .setPrecision(10)
                        .setScale(20)
                        .build()))
            .build();

    assertThatThrownBy(() -> new CatalogRegistry().register(withOnlyTable(broken)))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("exceeds precision");
  }

  // ---- D257: the clustered kind and its covering set ----

  /** {@code bars} with one clustered index over (symbol, ts) and the given covering ordinals. */
  private static Table clustered(chalk.ir.v1.IndexKind kind, int... covering) {
    chalk.ir.v1.Index.Builder index =
        chalk.ir.v1.Index.newBuilder()
            .setName("cx_bars_symbol_ts")
            .setKind(kind)
            .setUnique(true)
            .addColumns(0)
            .addColumns(1);
    for (int column : covering) {
      index.addCovering(column);
    }

    // The foreign key names `symbols`, which withOnlyTable does not register.
    return TestCatalogs.bars().toBuilder()
        .clearIndexes()
        .clearForeignKeys()
        .addIndexes(index)
        .build();
  }

  @Test
  void a_clustered_index_over_its_key_and_a_covered_column_registers() {
    assertThat(
            new CatalogRegistry()
                .register(
                    withOnlyTable(
                        clustered(chalk.ir.v1.IndexKind.INDEX_KIND_CLUSTERED, 0, 1, 5)))
                .epoch())
        .isEqualTo(1);
  }

  @Test
  void a_clustered_index_that_covers_everything_declares_no_covering_set() {
    assertThat(
            new CatalogRegistry()
                .register(withOnlyTable(clustered(chalk.ir.v1.IndexKind.INDEX_KIND_CLUSTERED)))
                .epoch())
        .isEqualTo(1);
  }

  @Test
  void a_covering_set_that_leaves_out_a_key_column_is_rejected_at_registration() {
    assertThatThrownBy(
            () ->
                new CatalogRegistry()
                    .register(
                        withOnlyTable(
                            clustered(chalk.ir.v1.IndexKind.INDEX_KIND_CLUSTERED, 0, 5))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("leaves out key column");
  }

  @Test
  void a_covering_column_that_is_not_a_column_is_rejected_at_registration() {
    assertThatThrownBy(
            () ->
                new CatalogRegistry()
                    .register(
                        withOnlyTable(
                            clustered(chalk.ir.v1.IndexKind.INDEX_KIND_CLUSTERED, 0, 1, 99))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("out of range");
  }

  @Test
  void a_covering_set_on_an_ordered_index_is_rejected_at_registration() {
    assertThatThrownBy(
            () ->
                new CatalogRegistry()
                    .register(
                        withOnlyTable(
                            clustered(chalk.ir.v1.IndexKind.INDEX_KIND_ORDERED, 0, 1, 5))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("only a CLUSTERED index holds a copy");
  }

  private static CatalogContext withOnlyTable(Table table) {
    return CatalogContext.newBuilder()
        .setContextId("broken")
        .setEpoch(1)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("main")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .addTables(table))
        .build();
  }
}
