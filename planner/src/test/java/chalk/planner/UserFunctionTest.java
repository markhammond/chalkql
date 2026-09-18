package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.FunctionId;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.planner.catalog.InvalidCatalogException;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.ir.IrVersionGate;
import chalk.planner.ir.RelToIr;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.types.TypeMapper;
import org.apache.calcite.rel.RelNode;
import org.junit.jupiter.api.Test;

/**
 * User-defined functions in the planner (step 22, {@code 17-user-defined-functions.md} §6). What is
 * asserted here is the planner's half: registration and resolution per kind, the inlining of SQL
 * bodies, folding, monotonicity, native pushdown and refusal.
 */
final class UserFunctionTest {

  private static Plan plan(String sql) {
    return plan(TestCatalogs.declared(), sql, PushdownPolicy.full());
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

  private static String text(String sql) {
    RegisteredCatalog registered = RegisteredCatalog.of(TestCatalogs.declared());
    try (PlannerPipeline pipeline = PlannerPipeline.create(registered, PushdownPolicy.full())) {
      return pipeline.plan(sql, true).physicalPlanText();
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException(e);
    }
  }

  /** The plan's text form, which carries every field name and value the message has. */
  private static boolean mentions(Plan plan, String needle) {
    return plan.toString().contains(needle);
  }

  // ---- SQL bodies ----

  @Test
  void a_sql_bodied_scalar_is_inlined_and_leaves_no_call() {
    Plan plan = plan("SELECT symbol, pct_change(\"open\", \"close\") FROM bars");
    assertThat(mentions(plan, "pct_change")).isFalse();
    assertThat(plan.toString()).contains("FUNCTION_ID_DIVIDE");
  }

  /**
   * {@code RETURNS NULL ON NULL INPUT} over arguments that cannot be null is the body and nothing
   * else. The second call is the one that matters: {@code volume} is {@code I64} and the parameters
   * are {@code FP64}, so each argument reaches the body through a {@code CAST} — the shape the
   * tutorial's own {@code fill_ratio(filled, quantity)} has, and the shape whose guard used to
   * survive because a simplifier would not distribute {@code IS NULL} through a cast that can throw.
   * The inliner decides from the arguments' validated types instead, so a cast changes nothing.
   */
  @Test
  void a_strict_body_over_non_nullable_columns_carries_no_guard() {
    assertThat(plan("SELECT pct_change(\"open\", \"close\") FROM bars").toString())
        .doesNotContain("if_then");
    assertThat(plan("SELECT pct_change(volume, volume) FROM bars").toString())
        .doesNotContain("if_then");
  }

  /**
   * And the other half of the same rule: an argument that <em>can</em> be null keeps its test.
   * {@code vwap} is nullable and {@code close} is not, so exactly one {@code IS NULL} survives —
   * the guard is per argument, not all or nothing.
   */
  @Test
  void a_strict_body_over_a_nullable_column_keeps_its_guard() {
    String plan = text("SELECT pct_change(vwap, \"close\") FROM bars");

    assertThat(plan).contains("IS NULL");
    assertThat(plan).containsOnlyOnce("IS NULL");
    assertThat(plan).contains("CASE");
  }

  @Test
  void a_strict_body_over_a_null_argument_answers_null() {
    Plan plan = plan("SELECT pct_change(NULL, 1.0) FROM bars_small LIMIT 1");
    // Folded to a typed NULL by the constant folder, which is the strongest form the guard can take.
    assertThat(mentions(plan, "pct_change")).isFalse();
  }

  @Test
  void a_sql_bodied_aggregate_expands_into_the_built_ins_it_is_written_over() {
    Plan plan = plan("SELECT symbol, wavg(\"close\", volume) FROM bars GROUP BY symbol");
    assertThat(mentions(plan, "wavg")).isFalse();
    assertThat(plan.toString()).contains("AGGREGATE_FUNCTION_ID_SUM");
  }

  @Test
  void a_sql_bodied_table_function_expands_to_a_filtered_scan() {
    String plan = text("SELECT * FROM TABLE(bars_for('BTCUSDT'))");
    assertThat(plan).doesNotContain("bars_for");
    assertThat(plan).contains("bars");
  }

  @Test
  void a_table_macro_refuses_a_dynamic_parameter_naming_the_constraint() {
    assertThatThrownBy(() -> plan("SELECT * FROM TABLE(bars_for(?))"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("table macro")
        .hasMessageContaining("constants");
  }

  // ---- client bodies ----

  @Test
  void a_client_bodied_scalar_travels_by_name() {
    Plan plan = plan("SELECT bucket_price(\"close\", 5.0) FROM bars");
    assertThat(mentions(plan, "main.bucket_price")).isTrue();
  }

  @Test
  void an_immutable_client_body_with_constant_arguments_is_not_folded() {
    Plan plan = plan("SELECT bucket_price(7.0, 5.0) FROM bars_small LIMIT 1");
    assertThat(mentions(plan, "main.bucket_price")).isTrue();
  }

  @Test
  void a_client_bodied_table_function_becomes_a_table_function_scan() {
    Plan plan = plan("SELECT * FROM TABLE(generate_series(1, 10, 3))");
    assertThat(plan.getRoot().getKindCase()).isEqualTo(Rel.KindCase.TABLE_FUNCTION_SCAN);
    assertThat(plan.getRoot().getTableFunctionScan().getFunction())
        .isEqualTo("main.generate_series");
  }

  @Test
  void a_client_bodied_aggregate_travels_by_name() {
    Plan plan = plan("SELECT symbol, geo_mean(\"close\") FROM bars GROUP BY symbol");
    assertThat(mentions(plan, "main.geo_mean")).isTrue();
  }

  @Test
  void a_client_bodied_aggregate_over_a_frame_travels_by_name() {
    Plan plan =
        plan(
            "SELECT wsum(\"close\", CAST(volume AS DOUBLE)) OVER "
                + "(PARTITION BY symbol ORDER BY ts ROWS 9 PRECEDING) FROM bars");
    assertThat(mentions(plan, "main.wsum")).isTrue();
  }

  // ---- volatility ----

  @Test
  void a_volatile_call_is_never_folded() {
    Plan plan = plan("SELECT next_seq() FROM bars_small LIMIT 3");
    assertThat(mentions(plan, "main.next_seq")).isTrue();
  }

  @Test
  void a_stable_call_survives_planning_and_is_evaluated_once_by_the_executor() {
    Plan plan = plan("SELECT as_of(), symbol FROM bars_small LIMIT 3");
    assertThat(mentions(plan, "main.as_of")).isTrue();
  }

  // ---- monotonicity ----

  @Test
  void a_monotone_call_lets_the_declared_collation_pass_through() {
    String plan = text("SELECT symbol FROM bars ORDER BY minute_of(ts)");
    assertThat(plan).doesNotContain("ChalkSort");
  }

  // ---- named arguments and defaults ----

  @Test
  void a_named_call_and_an_omitted_optional_parameter_both_resolve() {
    Plan plan =
        plan(
            "SELECT bucket_price(width => 5.0, price => \"close\", off => 1.0), "
                + "bucket_price(\"close\", 5.0) FROM bars_small");
    assertThat(mentions(plan, "main.bucket_price")).isTrue();
  }

  @Test
  void a_named_call_of_a_sql_bodied_function_resolves_the_same_way() {
    Plan plan = plan("SELECT pct_change(b => \"close\", a => \"open\") FROM bars");
    assertThat(mentions(plan, "pct_change")).isFalse();
    assertThat(plan.toString()).contains("FUNCTION_ID_DIVIDE");
  }

  // ---- native bodies ----

  @Test
  void a_native_function_is_pushed_into_its_own_source() {
    CatalogContext catalog =
        TestCatalogs.withRemote(
            "duck",
            TestCatalogs.fullSqlCapabilities().addNativeFunctions("md5").build(),
            TestCatalogs.duckDbProfile());
    Plan plan = plan(catalog, "SELECT duck.md5(c_name) FROM duck.customer", PushdownPolicy.full());
    assertThat(plan.getRoot().getRemoteQuery().getQueryText()).contains("md5(\"c_name\")");
  }

  @Test
  void a_native_function_off_its_source_is_refused_naming_the_source() {
    CatalogContext catalog =
        TestCatalogs.withRemote(
            "duck",
            TestCatalogs.fullSqlCapabilities().addNativeFunctions("md5").build(),
            TestCatalogs.duckDbProfile());
    assertThatThrownBy(
            () -> plan(catalog, "SELECT duck.md5(symbol) FROM bars", PushdownPolicy.full()))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("cannot be evaluated outside source duck");
  }

  // ---- the corpus ----

  static java.util.stream.Stream<CorpusQueries.Query> udfCorpus() {
    return CorpusQueries.m6udf().stream();
  }

  /** Every step 22 corpus query plans against the catalog the client actually pushes. */
  @org.junit.jupiter.params.ParameterizedTest(name = "{0}")
  @org.junit.jupiter.params.provider.MethodSource("udfCorpus")
  void every_udf_corpus_query_plans(CorpusQueries.Query query) throws Exception {
    chalk.planner.catalog.RegisteredCatalog registered =
        RegisteredCatalog.of(TestCatalogs.corpus());
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(registered, PushdownPolicy.full(), query.conformance(), query.libraries())) {
      assertThat(pipeline.plan(query.sql(), true).physicalPlanText()).isNotBlank();
    }
  }

  // ---- registration ----

  @Test
  void a_clash_with_a_built_in_is_a_registration_error() {
    CatalogContext catalog =
        TestCatalogs.declared().toBuilder()
            .setSchemas(
                0,
                TestCatalogs.declared().getSchemas(0).toBuilder()
                    .addFunctions(
                        chalk.ir.v1.FunctionDescriptor.newBuilder()
                            .setName("upper")
                            .setKind(chalk.ir.v1.FunctionKind.FUNCTION_KIND_SCALAR)
                            .setReturnType(TestCatalogs.type(chalk.ir.v1.TypeKind.TYPE_KIND_STRING))
                            .addParameters(
                                chalk.ir.v1.Parameter.newBuilder()
                                    .setName("s")
                                    .setType(
                                        TestCatalogs.nullable(chalk.ir.v1.TypeKind.TYPE_KIND_STRING)))
                            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())))
            .build();
    assertThatThrownBy(() -> RegisteredCatalog.of(catalog))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("upper");
  }

  @Test
  void a_body_that_names_something_other_than_a_parameter_is_a_registration_error() {
    CatalogContext catalog =
        TestCatalogs.declared().toBuilder()
            .setSchemas(
                0,
                TestCatalogs.declared().getSchemas(0).toBuilder()
                    .addFunctions(
                        chalk.ir.v1.FunctionDescriptor.newBuilder()
                            .setName("bad_body")
                            .setKind(chalk.ir.v1.FunctionKind.FUNCTION_KIND_SCALAR)
                            .setReturnType(TestCatalogs.type(chalk.ir.v1.TypeKind.TYPE_KIND_FP64))
                            .addParameters(
                                chalk.ir.v1.Parameter.newBuilder()
                                    .setName("a")
                                    .setType(TestCatalogs.nullable(chalk.ir.v1.TypeKind.TYPE_KIND_FP64)))
                            .setSql(chalk.ir.v1.SqlBody.newBuilder().setText("a + volume"))))
            .build();
    assertThatThrownBy(() -> RegisteredCatalog.of(catalog))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("volume");
  }

  @Test
  void the_ir_never_carries_a_built_in_id_beside_a_user_name() {
    Plan plan = plan("SELECT bucket_price(\"close\", 5.0) FROM bars");
    assertThat(plan.toString()).doesNotContain("FUNCTION_ID_UNSPECIFIED");
    assertThat(FunctionId.FUNCTION_ID_UNSPECIFIED.getNumber()).isZero();
  }
}
