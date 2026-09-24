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
 * and a composite-valued return type through the user operators. The corpus functions are {@code
 * price_move(open_price, close_price)}, a strict scalar answering {@code COMPOSITE<Direction:STRING,
 * Change:FP64>}, and {@code close_range(x)}, a window-capable aggregate answering a nullable {@code
 * COMPOSITE(Low FP64, High FP64)}.
 */
final class CompositeResultsTest {

  private static final Type MOVE =
      TestCatalogs.composite(
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

  private static List<Expr> ifThens(Plan plan) {
    return exprs(plan).stream().filter(e -> e.getKindCase() == Expr.KindCase.IF_THEN).toList();
  }

  /**
   * The corpus with two more client scalars over price_move's arguments: {@code price_band}, answering
   * {@code COMPOSITE(Low FP64, High FP64)}, and {@code price_turn}, answering price_move's type with
   * its first field renamed {@code Way}.
   */
  private static CatalogContext withOtherComposites() {
    CatalogContext base = TestCatalogs.corpus();
    CatalogContext.Builder builder = base.toBuilder();
    for (int s = 0; s < base.getSchemasCount(); s++) {
      chalk.ir.v1.FunctionDescriptor move =
          base.getSchemas(s).getFunctionsList().stream()
              .filter(f -> f.getName().equals("price_move"))
              .findFirst()
              .orElse(null);
      if (move == null) {
        continue;
      }
      builder.setSchemas(
          s,
          base.getSchemas(s).toBuilder()
              .addFunctions(
                  move.toBuilder()
                      .setName("price_band")
                      .setReturnType(
                          TestCatalogs.composite(
                              false,
                              field("Low", TestCatalogs.type(TypeKind.TYPE_KIND_FP64)),
                              field("High", TestCatalogs.type(TypeKind.TYPE_KIND_FP64)))))
              .addFunctions(
                  move.toBuilder()
                      .setName("price_turn")
                      .setReturnType(
                          TestCatalogs.composite(
                              false,
                              field("Way", TestCatalogs.type(TypeKind.TYPE_KIND_STRING)),
                              field("Change", TestCatalogs.type(TypeKind.TYPE_KIND_FP64))))));
    }
    return builder.build();
  }

  private static List<Expr> fieldAccesses(Plan plan) {
    return exprs(plan).stream()
        .filter(e -> e.getKindCase() == Expr.KindCase.FIELD_ACCESS)
        .toList();
  }

  // ---- (a) TypeMapper, both ways ----

  @Test
  void a_composite_maps_to_a_record_and_back_keeping_every_nullability() {
    TypeMapper types = new TypeMapper(new JavaTypeFactoryImpl());
    Type nullableOfNonNull =
        TestCatalogs.composite(
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
    Type nested = TestCatalogs.composite(false, field("Inner", MOVE));

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
  void a_field_of_a_nullable_composite_is_nullable() {
    // STRICT over a NULL argument makes the whole result nullable, and the field with it.
    Plan plan = plan("SELECT price_move(NULL, \"close\").change AS c FROM bars");

    Expr access = fieldAccesses(plan).get(0);
    Type composite = access.getFieldAccess().getInput().getType();
    assertThat(composite.getNullable()).isTrue();
    assertThat(access.getType().getKind()).isEqualTo(TypeKind.TYPE_KIND_FP64);
    assertThat(access.getType().getNullable()).isTrue();
  }

  @Test
  void the_whole_value_is_a_composite_column_and_one_call() {
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
  void a_call_taken_apart_and_the_same_call_selected_whole_are_one_expression() {
    // The validator casts the arguments to the declared parameter types; the converter drops the
    // casts that change nothing for a call standing alone and kept them under a field access, so
    // the two spellings were two expressions and the executor could not share them (D293).
    Plan plan =
        plan(
            "SELECT price_move(\"open\", \"close\").change AS change,"
                + " price_move(\"open\", \"close\") AS m FROM bars");

    List<Expr> calls =
        exprs(plan).stream()
            .filter(e -> e.getKindCase() == Expr.KindCase.CALL)
            .filter(e -> e.getCall().getUserFunction().equals("main.price_move"))
            .toList();
    assertThat(calls).hasSize(2);
    assertThat(calls.get(0)).isEqualTo(calls.get(1));
    assertThat(calls.get(0).getCall().getArgsList())
        .allSatisfy(arg -> assertThat(arg.getKindCase()).isEqualTo(Expr.KindCase.FIELD_REF));
  }

  @Test
  void a_row_type_is_not_a_composite_value() {
    // Calcite calls a query's row type and a row constructor structs too. None of these compares,
    // chooses between or casts a composite value, and each plans as it did before composites.
    String[] statements = {
      "SELECT symbol FROM bars WHERE \"close\" = SOME (SELECT \"close\" FROM bars_small)",
      "SELECT symbol FROM bars WHERE (symbol, ts) IN (SELECT symbol, ts FROM bars_small)",
      "SELECT symbol FROM bars WHERE (symbol, \"close\") = ('a', 1.0)",
      "SELECT CASE WHEN volume > 0 THEN (SELECT MAX(\"close\") FROM bars_small) END AS m FROM bars",
      "SELECT COALESCE((SELECT MAX(\"close\") FROM bars_small), 0.0) AS m FROM bars",
      "SELECT CAST((SELECT MAX(\"close\") FROM bars_small) AS DECIMAL(10, 2)) AS m FROM bars",
    };
    for (String sql : statements) {
      assertThat(plan(sql).getOutputType().getFieldsCount()).as(sql).isOne();
    }

    // A scalar sub-query whose one column is a composite value is one.
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT symbol FROM bars WHERE price_move(\"open\", \"close\") ="
                        + " (SELECT price_move(\"close\", \"open\") FROM bars_small LIMIT 1)"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("a comparison of a composite value");
  }

  @Test
  void a_derived_ordering_stops_short_of_a_composite_column() {
    // Proved empty, the relation is a VALUES of no row, which satisfies every ordering, the
    // composite column included. The claim is made up to that column and no further, rather than
    // refused as an ordering on it (ADR 0077).
    Plan plan =
        plan(
            "SELECT symbol, price_move(\"open\", \"close\") AS m FROM bars WHERE 1 = 0"
                + " ORDER BY symbol");

    assertThat(plan.getOutputType().getFields(1).getType().getKind())
        .isEqualTo(TypeKind.TYPE_KIND_COMPOSITE);
    List<chalk.ir.v1.Collation> claimed = new ArrayList<>(plan.getRoot().getCollationsList());
    assertThat(claimed)
        .allSatisfy(
            collation ->
                assertThat(collation.getFieldsList())
                    .noneSatisfy(
                        field ->
                            assertThat(field.getExpr().getType().getKind())
                                .isEqualTo(TypeKind.TYPE_KIND_COMPOSITE)));
  }

  @Test
  void the_alias_spellings_work_and_the_two_that_look_right_do_not() {
    String inner = "(SELECT symbol, price_move(\"open\", \"close\") AS m FROM bars) AS s";

    // Through the alias: parenthesised, or qualified by the sub-query's name.
    assertThat(fieldAccesses(plan("SELECT (m).change FROM " + inner))).hasSize(1);
    assertThat(fieldAccesses(plan("SELECT s.m.change FROM " + inner))).hasSize(1);
    assertThat(fieldAccesses(plan("SELECT s.m.* FROM " + inner))).hasSize(2);

    // A bare `m.change` reads `m` as a table.
    assertThatThrownBy(() -> plan("SELECT m.change FROM " + inner))
        .hasMessageContaining("Table 'm' not found");
    // And `(f(x)).*` does not parse.
    assertThatThrownBy(() -> plan("SELECT (price_move(\"open\", \"close\")).* FROM bars"))
        .hasCauseInstanceOf(org.apache.calcite.sql.parser.SqlParseException.class)
        .hasMessageContaining("Encountered \". *\"");
  }

  @Test
  void a_composite_valued_aggregate_is_one_measure_and_two_field_accesses_over_its_column() {
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
                  .isEqualTo(TypeKind.TYPE_KIND_COMPOSITE);
              // The aggregate's composite is nullable, so each field read through it is too.
              assertThat(a.getType().getNullable()).isTrue();
            });
    assertThat(plan.toString()).contains("main.close_range");
  }

  @Test
  void the_row_constructor_stays_refused_because_a_composite_comes_from_a_function() {
    assertThatThrownBy(() -> plan("SELECT symbol, ROW(symbol, \"close\") AS r FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("the ROW constructor")
        .hasMessageContaining("A composite comes from a function");
  }

  // ---- (c) the refusals, by name ----

  @Test
  void order_by_a_composite_is_refused_naming_the_field_alternative() {
    assertThatThrownBy(
            () -> plan("SELECT symbol FROM bars ORDER BY price_move(\"open\", \"close\")"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("ORDER BY a composite value (price_move(\"open\", \"close\"))")
        .hasMessageContaining("Sort by one of its fields");
  }

  @Test
  void order_by_the_alias_of_a_composite_is_refused_too() {
    assertThatThrownBy(
            () -> plan("SELECT symbol, price_move(\"open\", \"close\") AS m FROM bars ORDER BY m"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("ORDER BY a composite value (m)");
    assertThatThrownBy(
            () -> plan("SELECT symbol, price_move(\"open\", \"close\") AS m FROM bars ORDER BY 2"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("ORDER BY a composite value (2)");
  }

  @Test
  void distinct_over_a_composite_is_refused() {
    assertThatThrownBy(() -> plan("SELECT DISTINCT price_move(\"open\", \"close\") AS m FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("SELECT DISTINCT over the composite column 'm'");
  }

  @Test
  void group_by_a_composite_is_refused() {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT COUNT(*) FROM bars GROUP BY price_move(\"open\", \"close\")"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("GROUP BY a composite value (price_move(\"open\", \"close\"))")
        .hasMessageContaining("Group by one of its fields");
  }

  @Test
  void a_comparison_of_composites_is_refused_even_where_calcite_would_fold_it() {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT symbol FROM bars WHERE price_move(\"open\", \"close\")"
                        + " = price_move(\"open\", \"close\")"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining(
            "a comparison of a composite value"
                + " (price_move(\"open\", \"close\") = price_move(\"open\", \"close\"))")
        .hasMessageContaining("Compare one of its fields");
  }

  @Test
  void a_composite_in_in_is_refused() {
    // A list of composite values validates, and the refusal comes after validation.
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT symbol FROM bars WHERE price_move(\"open\", \"close\") IN"
                        + " (price_move(\"close\", \"open\"), price_move(\"open\", \"open\"))"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("a composite value in IN (price_move(\"open\", \"close\"))")
        .hasMessageContaining("Test one of its fields");
    // Against a subquery Calcite reads the composite as a row of its two fields and refuses the shape
    // itself; the refusal by name still wins.
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT symbol FROM bars WHERE price_move(\"open\", \"close\") IN"
                        + " (SELECT price_move(\"close\", \"open\") FROM bars)"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("a composite value in IN (price_move(\"open\", \"close\"))");
  }

  @Test
  void where_calcite_s_own_type_checks_stop_first_the_refusal_is_still_by_name() {
    // A comparison with a scalar, a CAST, MAX and a CASE that mixes a composite value with a scalar have no
    // signature, so validation fails before the checks that run on a validated statement.
    assertThatThrownBy(
            () -> plan("SELECT symbol FROM bars WHERE price_move(\"open\", \"close\") = 1"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("a comparison of a composite value (price_move(\"open\", \"close\") = 1)");
    assertThatThrownBy(
            () -> plan("SELECT CAST(price_move(\"open\", \"close\") AS VARCHAR) FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("CAST of a composite value (price_move(\"open\", \"close\"))")
        .hasMessageContaining("Cast one of its fields");
    assertThatThrownBy(() -> plan("SELECT MAX(price_move(\"open\", \"close\")) FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("MAX of a composite value (price_move(\"open\", \"close\"))");
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT CASE WHEN volume > 0 THEN price_move(\"open\", \"close\") ELSE 1 END"
                        + " FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining(
            "a composite value as a CASE result"
                + " (CASE WHEN volume > 0 THEN price_move(\"open\", \"close\") ELSE 1 END)");
  }

  @Test
  void a_window_partitioned_or_ordered_by_a_composite_is_refused() {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT COUNT(*) OVER (PARTITION BY price_move(\"open\", \"close\")) FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("PARTITION BY a composite value (price_move(\"open\", \"close\"))");
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT COUNT(*) OVER (ORDER BY price_move(\"open\", \"close\")) FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining(
            "a window ORDER BY a composite value (price_move(\"open\", \"close\"))");
  }

  @Test
  void a_set_operation_that_compares_rows_is_refused_and_union_all_carries_a_composite() {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT price_move(\"open\", \"close\") AS m FROM bars UNION"
                        + " SELECT price_move(\"close\", \"open\") FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("UNION over the composite column 'm'");

    Plan plan =
        plan(
            "SELECT price_move(\"open\", \"close\") AS m FROM bars UNION ALL"
                + " SELECT price_move(\"close\", \"open\") FROM bars");
    assertThat(plan.getOutputType().getFields(0).getType().getKind())
        .isEqualTo(TypeKind.TYPE_KIND_COMPOSITE);
  }

  @Test
  void a_null_test_reads_a_composite_s_validity_and_is_allowed() {
    Plan plan =
        plan(
            "SELECT symbol FROM bars WHERE price_move(\"open\", \"close\") IS NOT NULL");
    assertThat(
            exprs(plan).stream()
                .filter(e -> e.getKindCase() == Expr.KindCase.CALL)
                .anyMatch(e -> e.getCall().getUserFunction().equals("main.price_move")))
        .isTrue();
  }

  // ---- (D295) a choice between composite values of one type ----

  @Test
  void a_case_between_composites_of_one_type_is_a_composite_if_then() {
    Plan plan =
        plan(
            "SELECT CASE WHEN volume > 0 THEN price_move(\"open\", \"close\") END AS m"
                + " FROM bars");
    Type m = plan.getOutputType().getFields(0).getType();
    assertThat(m.getKind()).isEqualTo(TypeKind.TYPE_KIND_COMPOSITE);
    assertThat(m.getNullable()).isTrue();

    // One IfThen of the composite type, whose ELSE is the typed NULL SQL's missing ELSE means.
    List<Expr> cases = ifThens(plan);
    assertThat(cases).hasSize(1);
    assertThat(cases.get(0).getType().getKind()).isEqualTo(TypeKind.TYPE_KIND_COMPOSITE);
    Expr otherwise = cases.get(0).getIfThen().getElseBranch();
    assertThat(otherwise.getLiteral().getValueCase())
        .isEqualTo(chalk.ir.v1.Literal.ValueCase.IS_NULL);
    assertThat(otherwise.getType().getKind()).isEqualTo(TypeKind.TYPE_KIND_COMPOSITE);
  }

  @Test
  void a_case_and_a_coalesce_choose_between_two_calls_of_one_composite_type() {
    Plan chosen =
        plan(
            "SELECT CASE WHEN volume > 0 THEN price_move(\"open\", \"close\")"
                + " ELSE price_move(\"close\", \"open\") END AS m FROM bars");
    assertThat(ifThens(chosen)).hasSize(1);
    assertThat(chosen.getOutputType().getFields(0).getType().getFieldsList())
        .extracting(Field::getName)
        .containsExactly("Direction", "Change");

    // Calcite validates a COALESCE as the CASE it expands to, so it plans as the same IfThen.
    Plan coalesced =
        plan(
            "SELECT COALESCE(price_move(\"open\", \"close\"), price_move(\"close\", \"open\"))"
                + " AS m FROM bars");
    assertThat(ifThens(coalesced))
        .extracting(e -> e.getType().getKind())
        .containsExactly(TypeKind.TYPE_KIND_COMPOSITE);
    // A field of the choice is a field access over it, as over any composite value.
    Plan field =
        plan(
            "SELECT COALESCE(price_move(\"open\", \"close\"), price_move(\"close\", \"open\"))"
                + ".change AS c FROM bars");
    assertThat(fieldAccesses(field)).hasSize(1);
  }

  @Test
  void a_choice_between_composites_of_different_types_is_refused_by_name() {
    // Types Calcite cannot reconcile: validation stops, and the pre-check names the choice.
    assertThatThrownBy(
            () ->
                plan(
                    withOtherComposites(),
                    "SELECT CASE WHEN volume > 0 THEN price_move(\"open\", \"close\")"
                        + " ELSE price_band(\"open\", \"close\") END AS m FROM bars",
                    PushdownPolicy.full()))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining(
            "a CASE between composite values of different types (CASE WHEN volume > 0 THEN"
                + " price_move(\"open\", \"close\") ELSE price_band(\"open\", \"close\") END)")
        .hasMessageContaining("the same fields, named and typed alike");
    // Types Calcite would reconcile by taking the first one's field names: refused, not renamed.
    assertThatThrownBy(
            () ->
                plan(
                    withOtherComposites(),
                    "SELECT COALESCE(price_move(\"open\", \"close\"), price_turn(\"open\", \"close\"))"
                        + " AS m FROM bars",
                    PushdownPolicy.full()))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining(
            "a COALESCE between composite values of different types (COALESCE(price_move(\"open\","
                + " \"close\"), price_turn(\"open\", \"close\")))")
        .hasMessageContaining("A COALESCE chooses between composite values of one type only");
    assertThatThrownBy(
            () ->
                plan(
                    withOtherComposites(),
                    "SELECT COALESCE(price_move(\"open\", \"close\"), price_band(\"open\", \"close\"))"
                        + " AS m FROM bars",
                    PushdownPolicy.full()))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining(
            "a COALESCE between composite values of different types (COALESCE(price_move(\"open\","
                + " \"close\"), price_band(\"open\", \"close\")))");
  }

  @Test
  void a_composite_as_a_built_in_aggregate_s_argument_is_refused() {
    assertThatThrownBy(() -> plan("SELECT COUNT(price_move(\"open\", \"close\")) FROM bars"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("COUNT of a composite value (price_move(\"open\", \"close\"))");
  }

  @Test
  void an_unknown_field_is_calcite_s_own_validation_error() {
    assertThatThrownBy(() -> plan("SELECT price_move(\"open\", \"close\").nope FROM bars"))
        .hasMessageContaining("nope");
  }

  @Test
  void a_nested_composite_declared_is_refused_at_registration() {
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
                            .setReturnType(TestCatalogs.composite(false, field("inner", MOVE)))
                            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())))
            .build();

    assertThatThrownBy(() -> RegisteredCatalog.of(catalog))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("one level deep");
  }

  @Test
  void a_composite_returned_by_a_sql_bodied_function_is_refused_at_registration() {
    CatalogContext catalog =
        TestCatalogs.declared().toBuilder()
            .setSchemas(
                0,
                TestCatalogs.declared().getSchemas(0).toBuilder()
                    .addFunctions(
                        chalk.ir.v1.FunctionDescriptor.newBuilder()
                            .setName("inlined_composite")
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
        .hasMessageContaining("returns a COMPOSITE and is SQL-bodied");
  }

  @Test
  void the_catalog_refuses_a_composite_wherever_one_would_be_stored_bound_or_nested() {
    chalk.ir.v1.FunctionDescriptor client =
        chalk.ir.v1.FunctionDescriptor.newBuilder()
            .setName("mover")
            .setKind(chalk.ir.v1.FunctionKind.FUNCTION_KIND_SCALAR)
            .setVolatility(chalk.ir.v1.Volatility.VOLATILITY_IMMUTABLE)
            .setReturnType(MOVE)
            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())
            .build();

    // A native body runs in a source, which has no way to return a composite value.
    assertThatThrownBy(
            () ->
                RegisteredCatalog.of(
                    withFunction(
                        client.toBuilder()
                            .setNative(chalk.ir.v1.NativeBody.newBuilder().setDialectName("m"))
                            .build())))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("returns a COMPOSITE and is native");
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
        .hasMessageContaining(
            "a COMPOSITE is a function's result or an in-process table's column and never a"
                + " parameter");
    // Two fields SQL could not tell apart.
    assertThatThrownBy(
            () ->
                RegisteredCatalog.of(
                    withFunction(
                        client.toBuilder()
                            .setReturnType(
                                TestCatalogs.composite(
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
        .hasMessageContaining("a LIST's element is a COMPOSITE");
    // A table stores one on an in-process source only (D302): the corpus's LOCAL schema takes one,
    // and a REMOTE schema declaring one is refused, naming the table and the column.
    CatalogContext local =
        TestCatalogs.declared().toBuilder()
            .setSchemas(
                0,
                TestCatalogs.declared().getSchemas(0).toBuilder()
                    .setTables(
                        0,
                        TestCatalogs.declared().getSchemas(0).getTables(0).toBuilder()
                            .addColumns(TestCatalogs.column("m", MOVE))))
            .build();
    assertThat(RegisteredCatalog.of(local)).isNotNull();

    CatalogContext withRemote =
        TestCatalogs.withRemote(
            "duck", TestCatalogs.fullSqlCapabilities().build(), TestCatalogs.duckDbProfile());
    CatalogContext remote =
        withRemote.toBuilder()
            .setSchemas(
                1,
                withRemote.getSchemas(1).toBuilder()
                    .setTables(
                        0,
                        withRemote.getSchemas(1).getTables(0).toBuilder()
                            .addColumns(TestCatalogs.column("m", MOVE))))
            .build();
    assertThatThrownBy(() -> RegisteredCatalog.of(remote))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining(
            "column 'm' of table 'lineitem' is a COMPOSITE, and schema 'duck' is not an in-process"
                + " source; a composite column is read only from an in-process (LOCAL) source");
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
  void a_composite_return_type_flows_through_and_strict_widens_the_whole_result() {
    Plan nonNull = plan("SELECT price_move(\"open\", \"close\") AS m FROM bars");
    Plan widened = plan("SELECT price_move(NULL, \"close\") AS m FROM bars");

    assertThat(nonNull.getOutputType().getFields(0).getType()).isEqualTo(MOVE);
    // Widened as a whole: the composite is nullable and its fields are the record's, as declared.
    assertThat(widened.getOutputType().getFields(0).getType())
        .isEqualTo(MOVE.toBuilder().setNullable(true).build());
  }

  @Test
  void an_aggregate_s_declared_nullable_composite_keeps_its_fields_as_declared() {
    Plan plan = plan("SELECT symbol, close_range(\"close\") AS r FROM bars GROUP BY symbol");

    Type range = plan.getOutputType().getFields(1).getType();
    assertThat(range.getKind()).isEqualTo(TypeKind.TYPE_KIND_COMPOSITE);
    assertThat(range.getNullable()).isTrue();
    assertThat(range.getFieldsList().stream().map(Field::getName)).containsExactly("Low", "High");
  }
}
