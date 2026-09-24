package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.Expr;
import chalk.ir.v1.Field;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.planner.catalog.InvalidCatalogException;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.ir.IrVersionGate;
import chalk.planner.ir.RelToIr;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.types.TypeMapper;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.jdbc.JavaTypeFactoryImpl;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.sql.type.SqlTypeName;
import org.junit.jupiter.api.Test;

/**
 * Structured function results in the planner (D291, D292; design 51 §4, ADR 0077): the type mapping
 * both ways, the lowering of a field access, the refusals by name, pushdown declining a field access,
 * and a struct-valued return type through the user operators. The corpus functions are {@code
 * price_move(open_price, close_price)}, a strict scalar answering {@code STRUCT<Direction:STRING,
 * Change:FP64>}, and {@code close_range(x)}, a window-capable aggregate answering a nullable {@code
 * STRUCT<Low:FP64, High:FP64>}.
 */
final class StructResultsTest {

  private static final Type MOVE =
      TestCatalogs.struct(
          false,
          field("Direction", TestCatalogs.type(TypeKind.TYPE_KIND_STRING)),
          field("Change", TestCatalogs.type(TypeKind.TYPE_KIND_FP64)));

  private static Plan plan(String sql) {
    return plan(TestCatalogs.corpus(), sql, PushdownPolicy.full());
  }

  private static Plan plan(CatalogContext catalog, String sql, PushdownPolicy policy) {
    RegisteredCatalog registered = RegisteredCatalog.of(catalog);
    try (PlannerPipeline pipeline = PlannerPipeline.create(registered, policy)) {
      PlannerPipeline.Result result = pipeline.plan(sql, true);
      RelNode physical = result.physical();
      TypeMapper types = new TypeMapper(physical.getCluster().getTypeFactory());
      RelToIr converter =
          new RelToIr(
              types,
              physical.getCluster().getRexBuilder(),
              physical.getCluster().getMetadataQuery(),
              IrVersionGate.current());
      return converter.toPlan(
          physical, result.parameterRowType(), registered.contextId(), registered.epoch());
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException(e);
    }
  }

  private static String physicalText(CatalogContext catalog, String sql) {
    RegisteredCatalog registered = RegisteredCatalog.of(catalog);
    try (PlannerPipeline pipeline = PlannerPipeline.create(registered, PushdownPolicy.full())) {
      return pipeline.plan(sql, true).physicalPlanText();
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException(e);
    }
  }

  private static Field field(String name, Type type) {
    return Field.newBuilder().setName(name).setType(type).build();
  }

  /** Every expression in the plan, the executed walk's, depth first. */
  private static List<Expr> exprs(Plan plan) {
    List<Expr> all = new ArrayList<>();
    collect(plan.getRoot(), all);
    return all;
  }

  private static void collect(Rel rel, List<Expr> into) {
    for (com.google.protobuf.Descriptors.FieldDescriptor descriptor :
        rel.getAllFields().keySet()) {
      Object value = rel.getField(descriptor);
      walk(value, into);
    }
  }

  private static void walk(Object value, List<Expr> into) {
    if (value instanceof Expr expr) {
      into.add(expr);
      for (Object child : expr.getAllFields().values()) {
        walk(child, into);
      }
    } else if (value instanceof com.google.protobuf.Message message) {
      for (Object child : message.getAllFields().values()) {
        walk(child, into);
      }
    } else if (value instanceof List<?> list) {
      for (Object item : list) {
        walk(item, into);
      }
    }
  }

  private static List<Expr> fieldAccesses(Plan plan) {
    return exprs(plan).stream()
        .filter(e -> e.getKindCase() == Expr.KindCase.FIELD_ACCESS)
        .toList();
  }

  // ---- (a) TypeMapper, both ways ----

  @Test
  void a_struct_maps_to_a_record_and_back_keeping_every_nullability() {
    TypeMapper types = new TypeMapper(new JavaTypeFactoryImpl());
    Type nullableOfNonNull =
        TestCatalogs.struct(
            true,
            field("Direction", TestCatalogs.type(TypeKind.TYPE_KIND_STRING)),
            field("Change", TestCatalogs.nullable(TypeKind.TYPE_KIND_FP64)));

    RelDataType record = types.toCalcite(nullableOfNonNull);

    assertThat(record.isStruct()).isTrue();
    assertThat(record.isNullable()).isTrue();
    assertThat(record.getFieldNames()).containsExactly("Direction", "Change");
    assertThat(record.getFieldList().get(0).getType().isNullable()).isFalse();
    assertThat(record.getFieldList().get(1).getType().isNullable()).isTrue();
    assertThat(types.toIr(record)).isEqualTo(nullableOfNonNull);
    assertThat(types.toIr(types.toCalcite(MOVE))).isEqualTo(MOVE);
  }

  @Test
  void a_nested_composite_is_refused_as_one_level_deep_in_both_directions() {
    TypeMapper types = new TypeMapper(new JavaTypeFactoryImpl());
    Type nested = TestCatalogs.struct(false, field("Inner", MOVE));

    assertThatThrownBy(() -> types.toCalcite(nested))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("a nested composite type")
        .hasMessageContaining("one level deep");

    RelDataTypeFactory factory = new JavaTypeFactoryImpl();
    RelDataType inner = factory.builder().add("a", SqlTypeName.INTEGER).build();
    RelDataType outer = factory.builder().add("inner", inner).build();
    assertThatThrownBy(() -> types.toIr(outer))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("a nested composite type");
  }

  // ---- (b) RexToIr ----

  @Test
  void the_owner_s_two_fields_are_two_field_accesses_over_one_call() {
    Plan plan =
        plan(
            "SELECT price_move(\"open\", \"close\").direction,"
                + " price_move(\"open\", \"close\").change FROM bars");

    List<Expr> accesses = fieldAccesses(plan);
    assertThat(accesses).hasSize(2);
    for (Expr access : accesses) {
      Expr input = access.getFieldAccess().getInput();
      assertThat(input.getKindCase()).isEqualTo(Expr.KindCase.CALL);
      assertThat(input.getCall().getUserFunction()).isEqualTo("main.price_move");
      assertThat(input.getType()).isEqualTo(MOVE);
    }
    assertThat(accesses.get(0).getFieldAccess().getIndex()).isZero();
    assertThat(accesses.get(0).getType()).isEqualTo(TestCatalogs.type(TypeKind.TYPE_KIND_STRING));
    assertThat(accesses.get(1).getFieldAccess().getIndex()).isEqualTo(1);
    // The output columns take the query's spelling, not the declaration's.
    assertThat(plan.getOutputType().getFields(0).getName()).isEqualTo("direction");
    assertThat(plan.getOutputType().getFields(1).getName()).isEqualTo("change");
  }

  @Test
  void a_field_of_a_nullable_struct_is_nullable() {
    // STRICT over a NULL argument makes the whole result nullable, and the field with it.
    Plan plan = plan("SELECT price_move(NULL, \"close\").change AS c FROM bars");

    Expr access = fieldAccesses(plan).get(0);
    Type struct = access.getFieldAccess().getInput().getType();
    assertThat(struct.getNullable()).isTrue();
    assertThat(access.getType().getKind()).isEqualTo(TypeKind.TYPE_KIND_FP64);
    assertThat(access.getType().getNullable()).isTrue();
  }

  @Test
  void the_whole_value_is_a_struct_column_and_one_call() {
    Plan plan = plan("SELECT symbol, price_move(\"open\", \"close\") AS m FROM bars");

    assertThat(plan.getOutputType().getFields(1).getType()).isEqualTo(MOVE);
    assertThat(fieldAccesses(plan)).isEmpty();
    assertThat(
            exprs(plan).stream()
                .filter(e -> e.getKindCase() == Expr.KindCase.CALL)
                .filter(e -> e.getCall().getUserFunction().equals("main.price_move"))
                .count())
        .isEqualTo(1);
  }

  @Test
  void the_subquery_spellings_are_field_accesses_over_the_call_the_subquery_computes() {
    Plan plan =
        plan(
            "SELECT (m).change AS move, s.m.* FROM (SELECT symbol, price_move(\"open\", \"close\")"
                + " AS m FROM bars) AS s");

    List<Expr> accesses = fieldAccesses(plan);
    assertThat(accesses).hasSize(3);
    assertThat(accesses)
        .allSatisfy(
            a ->
                assertThat(a.getFieldAccess().getInput().getCall().getUserFunction())
                    .isEqualTo("main.price_move"));
    assertThat(plan.getOutputType().getFieldsList().stream().map(Field::getName))
        .containsExactly("move", "Direction", "Change");
  }

  @Test
  void a_struct_valued_aggregate_is_one_measure_and_two_field_accesses_over_its_column() {
    Plan plan =
        plan(
            "SELECT symbol, close_range(\"close\").low AS low, close_range(\"close\").high AS high"
                + " FROM bars GROUP BY symbol");

    List<Expr> accesses = fieldAccesses(plan);
    assertThat(accesses).hasSize(2);
    assertThat(accesses)
        .allSatisfy(
            a -> {
              assertThat(a.getFieldAccess().getInput().getKindCase())
                  .isEqualTo(Expr.KindCase.FIELD_REF);
              assertThat(a.getFieldAccess().getInput().getType().getKind())
                  .isEqualTo(TypeKind.TYPE_KIND_STRUCT);
              // The aggregate's struct is nullable, so each field read through it is too.
              assertThat(a.getType().getNullable()).isTrue();
            });
    assertThat(plan.toString()).contains("main.close_range");
  }

  @Test
  void the_row_constructor_stays_refused_because_a_struct_comes_from_a_function() {
    assertThatThrownBy(() -> plan("SELECT symbol, ROW(symbol, \"close\") AS r FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("the ROW constructor")
        .hasMessageContaining("A struct comes from a function");
  }

  // ---- (c) the refusals, by name ----

  @Test
  void order_by_a_struct_is_refused_naming_the_field_alternative() {
    assertThatThrownBy(
            () -> plan("SELECT symbol FROM bars ORDER BY price_move(\"open\", \"close\")"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("ORDER BY a struct")
        .hasMessageContaining("Sort by one of its fields");
  }

  @Test
  void order_by_the_alias_of_a_struct_is_refused_too() {
    assertThatThrownBy(
            () -> plan("SELECT symbol, price_move(\"open\", \"close\") AS m FROM bars ORDER BY m"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("ORDER BY a struct");
    assertThatThrownBy(
            () -> plan("SELECT symbol, price_move(\"open\", \"close\") AS m FROM bars ORDER BY 2"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("ORDER BY a struct");
  }

  @Test
  void distinct_over_a_struct_is_refused() {
    assertThatThrownBy(() -> plan("SELECT DISTINCT price_move(\"open\", \"close\") AS m FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("SELECT DISTINCT over the struct column 'm'");
  }

  @Test
  void group_by_a_struct_is_refused() {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT COUNT(*) FROM bars GROUP BY price_move(\"open\", \"close\")"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("GROUP BY a struct")
        .hasMessageContaining("Group by one of its fields");
  }

  @Test
  void a_comparison_of_structs_is_refused_even_where_calcite_would_fold_it() {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT symbol FROM bars WHERE price_move(\"open\", \"close\")"
                        + " = price_move(\"open\", \"close\")"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("a comparison of a struct")
        .hasMessageContaining("Compare one of its fields");
  }

  @Test
  void a_struct_in_in_is_refused() {
    // A list of structs validates, and the refusal comes after validation.
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT symbol FROM bars WHERE price_move(\"open\", \"close\") IN"
                        + " (price_move(\"close\", \"open\"), price_move(\"open\", \"open\"))"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("a struct in IN")
        .hasMessageContaining("Test one of its fields");
    // Against a subquery Calcite reads the struct as a row of its two fields and refuses the shape
    // itself; the refusal by name still wins.
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT symbol FROM bars WHERE price_move(\"open\", \"close\") IN"
                        + " (SELECT price_move(\"close\", \"open\") FROM bars)"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("a struct in IN");
  }

  @Test
  void where_calcite_s_own_type_checks_stop_first_the_refusal_is_still_by_name() {
    // A comparison with a scalar, a CAST, MAX and a CASE that mixes a struct with a scalar have no
    // signature, so validation fails before the checks that run on a validated statement.
    assertThatThrownBy(
            () -> plan("SELECT symbol FROM bars WHERE price_move(\"open\", \"close\") = 1"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("a comparison of a struct");
    assertThatThrownBy(
            () -> plan("SELECT CAST(price_move(\"open\", \"close\") AS VARCHAR) FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("CAST of a struct")
        .hasMessageContaining("Cast one of its fields");
    assertThatThrownBy(() -> plan("SELECT MAX(price_move(\"open\", \"close\")) FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("MAX of a struct");
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT CASE WHEN volume > 0 THEN price_move(\"open\", \"close\") ELSE 1 END"
                        + " FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("a struct as a CASE result");
  }

  @Test
  void a_window_partitioned_or_ordered_by_a_struct_is_refused() {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT COUNT(*) OVER (PARTITION BY price_move(\"open\", \"close\")) FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("PARTITION BY a struct");
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT COUNT(*) OVER (ORDER BY price_move(\"open\", \"close\")) FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("a window ORDER BY a struct");
  }

  @Test
  void a_set_operation_that_compares_rows_is_refused_and_union_all_carries_a_struct() {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT price_move(\"open\", \"close\") AS m FROM bars UNION"
                        + " SELECT price_move(\"close\", \"open\") FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("UNION over the struct column 'm'");

    Plan plan =
        plan(
            "SELECT price_move(\"open\", \"close\") AS m FROM bars UNION ALL"
                + " SELECT price_move(\"close\", \"open\") FROM bars");
    assertThat(plan.getOutputType().getFields(0).getType().getKind())
        .isEqualTo(TypeKind.TYPE_KIND_STRUCT);
  }

  @Test
  void a_null_test_reads_a_struct_s_validity_and_is_allowed() {
    Plan plan =
        plan(
            "SELECT symbol FROM bars WHERE price_move(\"open\", \"close\") IS NOT NULL");
    assertThat(
            exprs(plan).stream()
                .filter(e -> e.getKindCase() == Expr.KindCase.CALL)
                .anyMatch(e -> e.getCall().getUserFunction().equals("main.price_move")))
        .isTrue();
  }

  @Test
  void a_struct_as_a_case_result_is_refused() {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT CASE WHEN volume > 0 THEN price_move(\"open\", \"close\") END AS m"
                        + " FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("a struct as a CASE result");
  }

  @Test
  void a_struct_as_a_built_in_aggregate_s_argument_is_refused() {
    assertThatThrownBy(() -> plan("SELECT COUNT(price_move(\"open\", \"close\")) FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("COUNT of a struct");
  }

  @Test
  void an_unknown_field_is_calcite_s_own_validation_error() {
    assertThatThrownBy(() -> plan("SELECT price_move(\"open\", \"close\").nope FROM bars"))
        .hasMessageContaining("nope");
  }

  @Test
  void a_nested_struct_declared_is_refused_at_registration() {
    CatalogContext catalog =
        TestCatalogs.declared().toBuilder()
            .setSchemas(
                0,
                TestCatalogs.declared().getSchemas(0).toBuilder()
                    .addFunctions(
                        chalk.ir.v1.FunctionDescriptor.newBuilder()
                            .setName("nested")
                            .setKind(chalk.ir.v1.FunctionKind.FUNCTION_KIND_SCALAR)
                            .setVolatility(chalk.ir.v1.Volatility.VOLATILITY_IMMUTABLE)
                            .setReturnType(TestCatalogs.struct(false, field("inner", MOVE)))
                            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())))
            .build();

    assertThatThrownBy(() -> RegisteredCatalog.of(catalog))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("one level deep");
  }

  @Test
  void a_struct_returned_by_a_sql_bodied_function_is_refused_at_registration() {
    CatalogContext catalog =
        TestCatalogs.declared().toBuilder()
            .setSchemas(
                0,
                TestCatalogs.declared().getSchemas(0).toBuilder()
                    .addFunctions(
                        chalk.ir.v1.FunctionDescriptor.newBuilder()
                            .setName("inlined_struct")
                            .setKind(chalk.ir.v1.FunctionKind.FUNCTION_KIND_SCALAR)
                            .setVolatility(chalk.ir.v1.Volatility.VOLATILITY_IMMUTABLE)
                            .setReturnType(MOVE)
                            .addParameters(
                                chalk.ir.v1.Parameter.newBuilder()
                                    .setName("a")
                                    .setType(TestCatalogs.nullable(TypeKind.TYPE_KIND_FP64)))
                            .setSql(chalk.ir.v1.SqlBody.newBuilder().setText("a"))))
            .build();

    assertThatThrownBy(() -> RegisteredCatalog.of(catalog))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("returns a STRUCT and is SQL-bodied");
  }

  @Test
  void the_catalog_refuses_a_struct_wherever_one_would_be_stored_bound_or_nested() {
    chalk.ir.v1.FunctionDescriptor client =
        chalk.ir.v1.FunctionDescriptor.newBuilder()
            .setName("mover")
            .setKind(chalk.ir.v1.FunctionKind.FUNCTION_KIND_SCALAR)
            .setVolatility(chalk.ir.v1.Volatility.VOLATILITY_IMMUTABLE)
            .setReturnType(MOVE)
            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())
            .build();

    // A native body runs in a source, which has no way to return a struct.
    assertThatThrownBy(
            () ->
                RegisteredCatalog.of(
                    withFunction(
                        client.toBuilder()
                            .setNative(chalk.ir.v1.NativeBody.newBuilder().setDialectName("m"))
                            .build())))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("returns a STRUCT and is native");
    // A parameter is a scalar.
    assertThatThrownBy(
            () ->
                RegisteredCatalog.of(
                    withFunction(
                        client.toBuilder()
                            .setReturnType(TestCatalogs.type(TypeKind.TYPE_KIND_FP64))
                            .addParameters(
                                chalk.ir.v1.Parameter.newBuilder().setName("m").setType(MOVE))
                            .build())))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("a STRUCT is only ever a function's result");
    // Two fields SQL could not tell apart.
    assertThatThrownBy(
            () ->
                RegisteredCatalog.of(
                    withFunction(
                        client.toBuilder()
                            .setReturnType(
                                TestCatalogs.struct(
                                    false,
                                    field("Change", TestCatalogs.type(TypeKind.TYPE_KIND_FP64)),
                                    field("change", TestCatalogs.type(TypeKind.TYPE_KIND_FP64))))
                            .build())))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("used twice ignoring case");
    // A list holds scalars.
    assertThatThrownBy(
            () ->
                RegisteredCatalog.of(
                    withFunction(
                        client.toBuilder()
                            .setReturnType(
                                Type.newBuilder()
                                    .setKind(TypeKind.TYPE_KIND_LIST)
                                    .setElement(MOVE)
                                    .build())
                            .build())))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("a LIST's element is a STRUCT");
    // And no table stores one.
    CatalogContext column =
        TestCatalogs.declared().toBuilder()
            .setSchemas(
                0,
                TestCatalogs.declared().getSchemas(0).toBuilder()
                    .setTables(
                        0,
                        TestCatalogs.declared().getSchemas(0).getTables(0).toBuilder()
                            .addColumns(TestCatalogs.column("m", MOVE))))
            .build();
    assertThatThrownBy(() -> RegisteredCatalog.of(column))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("a STRUCT cannot be a table column");
  }

  private static CatalogContext withFunction(chalk.ir.v1.FunctionDescriptor function) {
    return TestCatalogs.declared().toBuilder()
        .setSchemas(
            0, TestCatalogs.declared().getSchemas(0).toBuilder().addFunctions(function))
        .build();
  }

  // ---- (d) pushdown ----

  @Test
  void a_field_access_and_the_client_call_under_it_stay_local_above_a_pushed_scan() {
    CatalogContext catalog =
        TestCatalogs.withRemote(
            "duck", TestCatalogs.fullSqlCapabilities().build(), TestCatalogs.duckDbProfile());
    String text =
        physicalText(
            catalog,
            "SELECT symbol, main.price_move(\"open\", \"close\").direction AS d FROM duck.bars");

    assertThat(text).contains("SourceScan");
    // The projection holding the call and its field sits above the source's boundary: neither
    // appears in the source's own subtree.
    int boundary = text.indexOf("SourceToLocalConverter");
    int call = text.indexOf("price_move");
    assertThat(boundary).isPositive();
    assertThat(call).isPositive().isLessThan(boundary);
  }

  // ---- (f) the user operators ----

  @Test
  void a_struct_return_type_flows_through_and_strict_widens_the_whole_result() {
    Plan nonNull = plan("SELECT price_move(\"open\", \"close\") AS m FROM bars");
    Plan widened = plan("SELECT price_move(NULL, \"close\") AS m FROM bars");

    assertThat(nonNull.getOutputType().getFields(0).getType()).isEqualTo(MOVE);
    // Widened as a whole: the struct is nullable and its fields are the record's, as declared.
    assertThat(widened.getOutputType().getFields(0).getType())
        .isEqualTo(MOVE.toBuilder().setNullable(true).build());
  }

  @Test
  void an_aggregate_s_declared_nullable_struct_keeps_its_fields_as_declared() {
    Plan plan = plan("SELECT symbol, close_range(\"close\") AS r FROM bars GROUP BY symbol");

    Type range = plan.getOutputType().getFields(1).getType();
    assertThat(range.getKind()).isEqualTo(TypeKind.TYPE_KIND_STRUCT);
    assertThat(range.getNullable()).isTrue();
    assertThat(range.getFieldsList().stream().map(Field::getName)).containsExactly("Low", "High");
  }
}
