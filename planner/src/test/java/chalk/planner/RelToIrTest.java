package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.IrVersion;
import chalk.ir.v1.AggregateFunctionId;
import chalk.ir.v1.Expr;
import chalk.ir.v1.FunctionId;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.ir.v1.SortDirection;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.planner.plan.PushdownPolicy;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.List;
import java.util.stream.Stream;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * The IR the planner emits for the corpus (docs/design/03-planner.md §8). The client's
 * {@code PlanValidator} is the full check of I-IR-1…10; this asserts the handful that are cheap here
 * plus the expression-level claims the corpus makes, which plan *text* cannot see.
 */
class RelToIrTest {
  private static CorpusPlanner planner;

  @BeforeAll
  static void setUp() {
    planner = new CorpusPlanner();
  }

  static Stream<CorpusQueries.Query> queries() {
    return CorpusQueries.all().stream();
  }

  @ParameterizedTest(name = "{0}")
  @MethodSource("queries")
  void every_corpus_query_produces_structurally_sound_ir(CorpusQueries.Query query) {
    for (PushdownPolicy policy : List.of(PushdownPolicy.full(), PushdownPolicy.none())) {
      Plan plan = planner.plan(query, policy);

      assertThat(plan.getIrVersion()).isEqualTo(IrVersion.CURRENT);
      assertThat(plan.getContextId()).isEqualTo(TestCatalogs.CONTEXT_ID);
      assertThat(plan.getCatalogEpoch()).isEqualTo(TestCatalogs.EPOCH);
      assertThat(plan.getPlanDigest()).isNotZero();
      assertThat(plan.hasRoot()).isTrue();
      assertThat(plan.getOutputType()).isEqualTo(plan.getRoot().getRowType());
      assertThat(plan.getOutputType().getFieldsCount()).isGreaterThan(0);

      for (Rel rel : rels(plan)) {
        assertThat(rel.getKindCase())
            .as("%s: every node has a kind", query.name())
            .isNotEqualTo(Rel.KindCase.KIND_NOT_SET);
        assertThat(rel.getRowType().getFieldsCount())
            .as("%s: %s has a row type", query.name(), rel.getKindCase())
            .isGreaterThan(0);
        assertThat(rel.getEstRowCount()).as("%s: estimates are non-negative", query.name()).isGreaterThanOrEqualTo(0);
        for (chalk.ir.v1.Field field : rel.getRowType().getFieldsList()) {
          assertTypeSound(field.getType(), query.name());
        }
        if (rel.getKindCase() == Rel.KindCase.READ) {
          assertThat(rel.getRead().getProjectionCount())
              .as("%s: Read.projection is never empty", query.name())
              .isGreaterThan(0);
          assertThat(rel.getRead().getProjectionCount()).isEqualTo(rel.getRowType().getFieldsCount());
          assertThat(rel.getRead().hasFilter())
              .as("%s: an M1 planner never pushes a filter into a Read", query.name())
              .isFalse();
        }
        for (chalk.ir.v1.Collation collation : rel.getCollationsList()) {
          for (chalk.ir.v1.SortField field : collation.getFieldsList()) {
            assertThat(field.getDirection()).isNotEqualTo(SortDirection.SORT_DIRECTION_UNSPECIFIED);
            assertThat(field.getExpr().getKindCase()).isEqualTo(Expr.KindCase.FIELD_REF);
            assertThat(field.getExpr().getFieldRef().getIndex())
                .isLessThan(rel.getRowType().getFieldsCount());
          }
        }
      }

      for (Expr expr : exprs(plan)) {
        if (expr.getKindCase() == Expr.KindCase.ENUM_ARG) {
          assertThat(expr.hasType()).as("%s: an EnumArg carries no type", query.name()).isFalse();
          continue;
        }
        assertThat(expr.hasType())
            .as("%s: every %s expression carries a type", query.name(), expr.getKindCase())
            .isTrue();
        assertTypeSound(expr.getType(), query.name());
        if (expr.getKindCase() == Expr.KindCase.PARAM) {
          assertThat(expr.getParam().getIndex()).isLessThan(plan.getParameterTypesCount());
          assertThat(plan.getParameterTypes(expr.getParam().getIndex())).isEqualTo(expr.getType());
        }
        if (expr.getKindCase() == Expr.KindCase.IF_THEN) {
          assertThat(expr.getIfThen().hasElseBranch())
              .as("%s: CASE always has an ELSE", query.name())
              .isTrue();
        }
      }
    }
  }

  /** SEARCH is a Calcite-internal shorthand; the IR never carries it (§5.2). */
  @ParameterizedTest(name = "{0}")
  @MethodSource("queries")
  void no_plan_contains_an_unmapped_function(CorpusQueries.Query query) {
    Plan plan = planner.plan(query);

    for (Expr expr : exprs(plan)) {
      if (expr.getKindCase() == Expr.KindCase.CALL) {
        assertThat(expr.getCall().getFunction())
            .as("%s: every call names a known function", query.name())
            .isNotEqualTo(FunctionId.FUNCTION_ID_UNSPECIFIED);
      }
    }
  }

  @Test
  void query_03_between_becomes_and_of_ge_and_le_with_no_search() {
    // At NONE, where the predicate stays a Filter: this is a claim about RexToIr, and from M2 the
    // same query at FULL turns the BETWEEN into index range bounds instead.
    Plan plan = planner.plan(sql("03_between_timestamps"), PushdownPolicy.none());

    Expr condition = find(plan, Rel.KindCase.FILTER).getFilter().getCondition();

    assertThat(condition.getCall().getFunction()).isEqualTo(FunctionId.FUNCTION_ID_AND);
    assertThat(functionsIn(condition))
        .contains(FunctionId.FUNCTION_ID_GE, FunctionId.FUNCTION_ID_LE);
  }

  @Test
  void query_15_in_list_keeps_both_options_unpadded() {
    // At NONE: at FULL the IN list becomes two index ranges, and the InList expression this is
    // about only exists where the predicate is evaluated per row (V6).
    Plan plan = planner.plan(sql("15_in_list_is_not_null"), PushdownPolicy.none());

    Expr inList =
        exprs(plan).stream()
            .filter(e -> e.getKindCase() == Expr.KindCase.IN_LIST)
            .findFirst()
            .orElseThrow(() -> new AssertionError("no InList in the plan"));

    assertThat(inList.getInList().getOptionsCount()).isEqualTo(2);
    assertThat(
            inList.getInList().getOptionsList().stream()
                .map(o -> o.getLiteral().getStringValue())
                .toList())
        .containsExactly("BTCUSDT", "ETHUSDT");
    assertThat(functionsIn(find(plan, Rel.KindCase.FILTER).getFilter().getCondition()))
        .contains(FunctionId.FUNCTION_ID_IS_NOT_NULL);
  }

  @Test
  void query_19_infers_the_parameter_types() {
    // At NONE, so the two parameters appear once each; at FULL they are index range bounds and the
    // equality's value appears on both sides of the range.
    Plan plan = planner.plan(sql("19_params"), PushdownPolicy.none());

    assertThat(plan.getParameterTypesCount()).isEqualTo(2);
    assertThat(plan.getParameterTypes(0).getKind()).isEqualTo(TypeKind.TYPE_KIND_STRING);
    assertThat(plan.getParameterTypes(0).getNullable()).isTrue();
    assertThat(plan.getParameterTypes(1).getKind()).isEqualTo(TypeKind.TYPE_KIND_TIMESTAMP);
    assertThat(plan.getParameterTypes(1).getPrecision()).isEqualTo(9);
    assertThat(exprs(plan).stream().filter(e -> e.getKindCase() == Expr.KindCase.PARAM).count())
        .isEqualTo(2);
  }

  @Test
  void query_08_reduces_avg_to_sum0_over_count() {
    Plan plan = planner.plan(sql("08_group_by_avg"));

    Rel aggregate = find(plan, Rel.KindCase.HASH_AGGREGATE);
    List<AggregateFunctionId> measures =
        aggregate.getHashAggregate().getAggregate().getMeasuresList().stream()
            .map(chalk.ir.v1.Measure::getFunction)
            .toList();

    assertThat(measures)
        .containsExactly(
            AggregateFunctionId.AGGREGATE_FUNCTION_ID_SUM0,
            AggregateFunctionId.AGGREGATE_FUNCTION_ID_COUNT);
    assertThat(measures).doesNotContain(AggregateFunctionId.AGGREGATE_FUNCTION_ID_AVG);
  }

  @Test
  void query_09_floors_the_timestamp_with_an_enum_argument() {
    Plan plan = planner.plan(sql("09_group_by_hour"));

    Expr floor =
        exprs(plan).stream()
            .filter(
                e ->
                    e.getKindCase() == Expr.KindCase.CALL
                        && e.getCall().getFunction() == FunctionId.FUNCTION_ID_FLOOR_TEMPORAL)
            .findFirst()
            .orElseThrow(() -> new AssertionError("no FLOOR_TEMPORAL in the plan"));

    assertThat(floor.getCall().getArgsCount()).isEqualTo(2);
    assertThat(floor.getCall().getArgs(1).getKindCase()).isEqualTo(Expr.KindCase.ENUM_ARG);
    assertThat(floor.getCall().getArgs(1).getEnumArg().getValue()).isEqualTo("HOUR");
  }

  @Test
  void query_24_extracts_with_an_enum_argument_first() {
    Plan plan = planner.plan(sql("24_extract_hour"));

    Expr extract =
        exprs(plan).stream()
            .filter(
                e ->
                    e.getKindCase() == Expr.KindCase.CALL
                        && e.getCall().getFunction() == FunctionId.FUNCTION_ID_EXTRACT)
            .findFirst()
            .orElseThrow(() -> new AssertionError("no EXTRACT in the plan"));

    assertThat(extract.getCall().getArgs(0).getKindCase()).isEqualTo(Expr.KindCase.ENUM_ARG);
    assertThat(extract.getCall().getArgs(0).getEnumArg().getValue()).isEqualTo("HOUR");
  }

  @Test
  void query_01_carries_the_declared_collation_onto_the_read() {
    Plan plan = planner.plan(sql("01_scan_all"));

    Rel read = find(plan, Rel.KindCase.READ);

    assertThat(read.getCollationsCount()).isEqualTo(1);
    assertThat(read.getCollations(0).getFieldsList())
        .extracting(f -> f.getExpr().getFieldRef().getIndex(), chalk.ir.v1.SortField::getDirection)
        .containsExactly(
            org.assertj.core.groups.Tuple.tuple(1, SortDirection.SORT_DIRECTION_ASC_NULLS_LAST),
            org.assertj.core.groups.Tuple.tuple(0, SortDirection.SORT_DIRECTION_ASC_NULLS_LAST));
  }

  @Test
  void query_02_prunes_the_leaf_to_three_columns_at_full_but_not_at_none() {
    // At FULL the leaf is an index lookup since M2; pruning is the property under test, and both
    // leaf kinds carry a projection.
    assertThat(find(planner.plan(sql("02_filter_eq_project"), PushdownPolicy.full()), Rel.KindCase.INDEX_LOOKUP)
            .getIndexLookup()
            .getProjectionCount())
        .isEqualTo(3);
    assertThat(find(planner.plan(sql("02_filter_eq_project"), PushdownPolicy.none()), Rel.KindCase.READ)
            .getRead()
            .getProjectionCount())
        .isEqualTo(9);
  }

  /**
   * Two casts the SQL asks for, plus one the IR requires: {@code CAST(volume AS DOUBLE) / 1000}
   * divides an FP64 by an I32 literal, and I-IR-2 says a call's value operands share a kind.
   */
  @Test
  void query_20_emits_the_two_written_casts_plus_one_for_operand_homogeneity() {
    Plan plan = planner.plan(sql("20_casts"));

    List<Expr> casts =
        exprs(plan).stream().filter(e -> e.getKindCase() == Expr.KindCase.CAST).toList();

    assertThat(casts).hasSize(3);
    assertThat(casts).extracting(e -> e.getType().getKind())
        .containsExactlyInAnyOrder(
            TypeKind.TYPE_KIND_FP64, TypeKind.TYPE_KIND_DATE, TypeKind.TYPE_KIND_FP64);
  }

  /**
   * V52, ADR 0024: {@code MOD} takes its type from its <em>second</em> argument, so
   * {@code MOD(BIGINT, INTEGER)} is declared INTEGER while I-IR-2 widens both operands to BIGINT.
   * The executor closes its kernel over the call's declared type, so the plan as Calcite typed it
   * made the kernel read a 64-bit column — and a 64-bit literal — as 32-bit lanes: wrong values, and
   * a zero divisor out of the literal's high half.
   *
   * <p>The call must therefore be computed at the operands' width and cast back to what the row type
   * declares. Two claims, because either alone would pass a broken plan: the operands, the call and
   * the cast's input are all I64, and the projection the row type sees is still I32.
   */
  @Test
  void a_modulus_over_mixed_widths_computes_at_the_wider_one_and_casts_back() {
    Plan plan = planner.plan("SELECT MOD(volume, 7) AS m FROM bars", PushdownPolicy.none());

    assertThat(plan.getOutputType().getFields(0).getType().getKind())
        .as("the row type is what Calcite declared: MOD's second argument is an INTEGER")
        .isEqualTo(TypeKind.TYPE_KIND_I32);

    Expr projected = find(plan, Rel.KindCase.PROJECT).getProject().getExprs(0);

    assertThat(projected.getKindCase())
        .as("the widened call is cast back to the declared width")
        .isEqualTo(Expr.KindCase.CAST);
    assertThat(projected.getType().getKind()).isEqualTo(TypeKind.TYPE_KIND_I32);

    Expr call = projected.getCast().getInput();

    assertThat(call.getCall().getFunction()).isEqualTo(FunctionId.FUNCTION_ID_MODULUS);
    assertThat(call.getType().getKind())
        .as("the kernel's width is the call's type, and the operands are I64")
        .isEqualTo(TypeKind.TYPE_KIND_I64);
    assertThat(call.getCall().getArgsList())
        .extracting(a -> a.getType().getKind())
        .containsExactly(TypeKind.TYPE_KIND_I64, TypeKind.TYPE_KIND_I64);
  }

  /** The other half of V52: where nothing was widened, nothing is cast. */
  @Test
  void a_modulus_over_one_width_is_not_cast_at_all() {
    Plan plan =
        planner.plan("SELECT MOD(volume, CAST(7 AS BIGINT)) AS m FROM bars", PushdownPolicy.none());

    Expr projected = find(plan, Rel.KindCase.PROJECT).getProject().getExprs(0);

    assertThat(projected.getKindCase()).isEqualTo(Expr.KindCase.CALL);
    assertThat(projected.getCall().getFunction()).isEqualTo(FunctionId.FUNCTION_ID_MODULUS);
    assertThat(projected.getType().getKind()).isEqualTo(TypeKind.TYPE_KIND_I64);
    assertThat(exprs(plan).stream().filter(e -> e.getKindCase() == Expr.KindCase.CAST).toList())
        .isEmpty();
  }

  @Test
  void query_22_is_an_empty_virtual_table_with_the_full_schema() {
    Plan plan = planner.plan(sql("22_empty_result"));

    Rel values = find(plan, Rel.KindCase.VIRTUAL_TABLE);

    assertThat(values.getVirtualTable().getRowsCount()).isZero();
    assertThat(values.getRowType().getFieldsCount()).isEqualTo(9);
  }

  @Test
  void query_12_is_a_top_n_with_the_fetch_fused_in() {
    Plan plan = planner.plan(sql("12_topn"));

    Rel topN = find(plan, Rel.KindCase.TOP_N);

    assertThat(topN.getTopN().getCount()).isEqualTo(10);
    assertThat(topN.getTopN().getOffset()).isZero();
    assertThat(topN.getTopN().getFields(0).getDirection())
        .isEqualTo(SortDirection.SORT_DIRECTION_DESC_NULLS_FIRST);
  }

  @Test
  void query_13_is_a_fetch_that_keeps_the_input_collation() {
    Plan plan = planner.plan(sql("13_fetch_offset_collated"));

    Rel fetch = find(plan, Rel.KindCase.FETCH);

    assertThat(fetch.getFetch().getOffset()).isEqualTo(100);
    assertThat(fetch.getFetch().hasCount()).isTrue();
    assertThat(fetch.getFetch().getCount()).isEqualTo(50);
    assertThat(fetch.getCollationsCount())
        .as("Fetch preserves the input's ordering (02-ir.md §4)")
        .isEqualTo(1);
  }

  @Test
  void the_root_row_type_carries_the_select_list_names() {
    Plan plan = planner.plan(sql("07_group_by_basic"));

    assertThat(plan.getOutputType().getFieldsList())
        .extracting(chalk.ir.v1.Field::getName)
        .containsExactly("symbol", "n", "vol", "lo", "hi");
  }

  // ---- helpers ----

  private static String sql(String name) {
    return CorpusQueries.all().stream()
        .filter(q -> q.name().equals(name))
        .findFirst()
        .orElseThrow(() -> new AssertionError("no corpus query named " + name))
        .sql();
  }

  private static void assertTypeSound(Type type, String query) {
    assertThat(type.getKind()).as("%s: no unspecified types", query).isNotEqualTo(TypeKind.TYPE_KIND_UNSPECIFIED);
    if (type.getKind() == TypeKind.TYPE_KIND_DECIMAL) {
      assertThat(type.getPrecision()).as("%s: DECIMAL precision", query).isBetween(1, 38);
      assertThat(type.getScale()).isLessThanOrEqualTo(type.getPrecision());
    } else if (type.getKind() == TypeKind.TYPE_KIND_TIMESTAMP
        || type.getKind() == TypeKind.TYPE_KIND_TIMESTAMP_TZ) {
      assertThat(type.getPrecision()).isBetween(0, 9);
      assertThat(type.getScale()).isZero();
    } else if (type.getKind() == TypeKind.TYPE_KIND_TIME) {
      assertThat(type.getPrecision()).isBetween(0, 6);
    } else {
      assertThat(type.getPrecision()).as("%s: %s carries no precision", query, type.getKind()).isZero();
      assertThat(type.getScale()).isZero();
    }
  }

  private static Rel find(Plan plan, Rel.KindCase kind) {
    return rels(plan).stream()
        .filter(r -> r.getKindCase() == kind)
        .findFirst()
        .orElseThrow(() -> new AssertionError("no " + kind + " in the plan"));
  }

  private static List<Rel> rels(Plan plan) {
    List<Rel> all = new ArrayList<>();
    Deque<Rel> stack = new ArrayDeque<>();
    stack.push(plan.getRoot());
    while (!stack.isEmpty()) {
      Rel rel = stack.pop();
      all.add(rel);
      inputs(rel).forEach(stack::push);
    }
    return all;
  }

  private static List<Rel> inputs(Rel rel) {
    return switch (rel.getKindCase()) {
      case INDEX_LOOKUP -> List.of();
      case FILTER -> List.of(rel.getFilter().getInput());
      case PROJECT -> List.of(rel.getProject().getInput());
      case AGGREGATE -> List.of(rel.getAggregate().getInput());
      case HASH_AGGREGATE -> List.of(rel.getHashAggregate().getAggregate().getInput());
      case STREAM_AGGREGATE -> List.of(rel.getStreamAggregate().getAggregate().getInput());
      case SORT -> List.of(rel.getSort().getInput());
      case FETCH -> List.of(rel.getFetch().getInput());
      case TOP_N -> List.of(rel.getTopN().getInput());
      default -> List.of();
    };
  }

  private static List<Expr> exprs(Plan plan) {
    List<Expr> all = new ArrayList<>();
    for (Rel rel : rels(plan)) {
      switch (rel.getKindCase()) {
        case FILTER -> collect(rel.getFilter().getCondition(), all);
        case PROJECT -> rel.getProject().getExprsList().forEach(e -> collect(e, all));
        case HASH_AGGREGATE -> {
          for (chalk.ir.v1.Measure measure : rel.getHashAggregate().getAggregate().getMeasuresList()) {
            measure.getArgsList().forEach(e -> collect(e, all));
            if (measure.hasFilter()) {
              collect(measure.getFilter(), all);
            }
          }
        }
        case INDEX_LOOKUP -> {
          for (chalk.ir.v1.IndexRange range : rel.getIndexLookup().getRangesList()) {
            range.getLowerList().forEach(e -> collect(e, all));
            range.getUpperList().forEach(e -> collect(e, all));
          }
          if (rel.getIndexLookup().hasResidual()) {
            collect(rel.getIndexLookup().getResidual(), all);
          }
        }
        case SORT -> rel.getSort().getFieldsList().forEach(f -> collect(f.getExpr(), all));
        case TOP_N -> rel.getTopN().getFieldsList().forEach(f -> collect(f.getExpr(), all));
        case VIRTUAL_TABLE ->
            rel.getVirtualTable().getRowsList().forEach(r -> r.getValuesList().forEach(e -> collect(e, all)));
        default -> {
          // no expressions of its own
        }
      }
      rel.getCollationsList()
          .forEach(c -> c.getFieldsList().forEach(f -> collect(f.getExpr(), all)));
    }
    return all;
  }

  private static void collect(Expr expr, List<Expr> into) {
    into.add(expr);
    switch (expr.getKindCase()) {
      case CALL -> expr.getCall().getArgsList().forEach(a -> collect(a, into));
      case CAST -> collect(expr.getCast().getInput(), into);
      case IF_THEN -> {
        for (chalk.ir.v1.IfClause clause : expr.getIfThen().getClausesList()) {
          collect(clause.getCondition(), into);
          collect(clause.getResult(), into);
        }
        collect(expr.getIfThen().getElseBranch(), into);
      }
      case IN_LIST -> {
        collect(expr.getInList().getValue(), into);
        expr.getInList().getOptionsList().forEach(o -> collect(o, into));
      }
      default -> {
        // leaf
      }
    }
  }

  private static List<FunctionId> functionsIn(Expr expr) {
    List<Expr> all = new ArrayList<>();
    collect(expr, all);
    return all.stream()
        .filter(e -> e.getKindCase() == Expr.KindCase.CALL)
        .map(e -> e.getCall().getFunction())
        .toList();
  }

  // ---- the row goal on the wire (D276, 46-row-goals.md §1 and §6) ----

  /**
   * A goaled scan writes {@code Read.row_goal}. No rule states a goal yet, so the goal is put on the
   * leaf here and what is under test is the conversion.
   */
  @Test
  void a_scan_with_a_row_goal_writes_the_field() {
    Rel leaf = leafOf(convert("SELECT symbol, ts FROM bars", 7L), Rel.KindCase.READ);

    assertThat(leaf.getRead().getRowGoal()).isEqualTo(7L);
  }

  /** And a goaled lookup writes {@code IndexLookup.row_goal}. */
  @Test
  void a_lookup_with_a_row_goal_writes_the_field() {
    Rel leaf =
        leafOf(
            convert("SELECT symbol, ts FROM bars WHERE symbol = 'BTCUSDT'", 3L),
            Rel.KindCase.INDEX_LOOKUP);

    assertThat(leaf.getIndexLookup().getRowGoal()).isEqualTo(3L);
  }

  /**
   * The zero-cost half of the contract: a leaf with no goal leaves the field unset, and an unset
   * {@code int64} emits no bytes, so a plan from before this step is byte-identical to the same plan
   * after it. (The corpus's recorded plans are the other, wider, half of that claim.)
   */
  @Test
  void a_leaf_without_a_row_goal_writes_no_bytes_at_all() {
    String sql = "SELECT symbol, ts FROM bars WHERE symbol = 'BTCUSDT'";
    Rel goaled = leafOf(convert(sql, 5L), Rel.KindCase.INDEX_LOOKUP);
    Rel plain = leafOf(convert(sql, 0L), Rel.KindCase.INDEX_LOOKUP);

    assertThat(plain.getIndexLookup().getRowGoal()).isZero();
    assertThat(plain.getIndexLookup().toByteArray())
        .isEqualTo(plain.getIndexLookup().toBuilder().clearRowGoal().build().toByteArray());
    assertThat(goaled.getIndexLookup().toByteArray())
        .isNotEqualTo(plain.getIndexLookup().toByteArray());
  }

  /** Plans {@code sql} against the declared catalog and states {@code goal} on every leaf. */
  private static Plan convert(String sql, long goal) {
    chalk.planner.catalog.RegisteredCatalog catalog =
        new chalk.planner.catalog.CatalogRegistry().register(TestCatalogs.declared());
    try (chalk.planner.plan.PlannerPipeline pipeline =
        chalk.planner.plan.PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      chalk.planner.plan.PlannerPipeline.Result result = pipeline.plan(sql, false);
      org.apache.calcite.rel.RelNode physical = withRowGoal(result.physical(), goal);
      return new chalk.planner.ir.RelToIr(
              new chalk.planner.types.TypeMapper(physical.getCluster().getTypeFactory()),
              physical.getCluster().getRexBuilder(),
              org.apache.calcite.rel.metadata.RelMetadataQuery.instance(),
              chalk.planner.ir.IrVersionGate.current())
          .toPlan(physical, result.parameterRowType(), TestCatalogs.CONTEXT_ID, TestCatalogs.EPOCH);
    } catch (Exception failure) {
      throw new AssertionError("planning failed for: " + sql, failure);
    }
  }

  /** The same tree with {@code goal} on every {@code ChalkTableScan} and {@code ChalkIndexLookup}. */
  private static org.apache.calcite.rel.RelNode withRowGoal(
      org.apache.calcite.rel.RelNode node, long goal) {
    if (node instanceof chalk.planner.plan.rel.ChalkTableScan scan) {
      return scan.withRowGoal(goal);
    }
    if (node instanceof chalk.planner.plan.rel.ChalkIndexLookup lookup) {
      return lookup.withRowGoal(goal);
    }

    List<org.apache.calcite.rel.RelNode> inputs = new ArrayList<>(node.getInputs().size());
    boolean changed = false;
    for (org.apache.calcite.rel.RelNode input : node.getInputs()) {
      org.apache.calcite.rel.RelNode replaced = withRowGoal(input, goal);
      changed |= replaced != input;
      inputs.add(replaced);
    }

    return changed ? node.copy(node.getTraitSet(), inputs) : node;
  }

  private static Rel leafOf(Plan plan, Rel.KindCase kind) {
    for (Rel rel : rels(plan)) {
      if (rel.getKindCase() == kind) {
        return rel;
      }
    }

    throw new AssertionError("no " + kind + " in " + plan);
  }
}
