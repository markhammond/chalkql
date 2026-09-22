package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import chalk.ir.v1.DynamicParam;
import chalk.ir.v1.Literal;
import chalk.ir.v1.Plan;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.diag.PlanText;
import chalk.ir.IrVersion;
import chalk.planner.plan.ChalkRelMetadata;
import chalk.planner.plan.ParameterHintCheck;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import chalk.planner.plan.rel.ChalkLimit;
import chalk.planner.plan.rel.ChalkTopN;
import chalk.planner.rpc.PlannerServiceImpl;
import chalk.planner.rpc.v1.ContextScalar;
import chalk.planner.rpc.v1.ParameterHint;
import chalk.planner.rpc.v1.PlanRequest;
import chalk.planner.rpc.v1.PlanResponse;
import chalk.planner.rpc.v1.PlannerOptions;
import chalk.planner.rpc.v1.RequestContext;
import com.google.protobuf.Descriptors;
import com.google.protobuf.Message;
import java.nio.file.Files;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;
import org.apache.calcite.rel.RelNode;
import org.junit.jupiter.api.Test;

/**
 * D284 — what the caller expects a parameter to be worth, and what the planner does with it
 * (design 49 §3, §5, §6).
 *
 * <p>Planned against the recorded corpus catalog, whose {@code bars} is 100 800 rows with
 * {@code volume} declared between 1 000 and 9 999 and an ordered index on {@code (symbol, ts)}, so
 * the numbers here are the ones the optimiser actually compared.
 */
class ParameterHintsTest {

  /** {@code volume >= ?}: a guess at a half without a hint, a measurement with one. */
  private static final String FILTERED = "SELECT symbol, ts FROM bars WHERE volume >= ? LIMIT ?";

  /** The shape design 49 §6 works through: an ordering only the index serves, bounded. */
  private static final String ORDERED =
      "SELECT symbol, ts FROM bars WHERE symbol >= ? ORDER BY symbol, ts LIMIT ?";

  // ---- the estimate ----

  /**
   * A predicate against a parameter is a guess; the same predicate against a hinted parameter is
   * read off the statistics, exactly as a literal would be.
   */
  @Test
  void a_hint_turns_a_guess_into_a_measurement() {
    assertThat(text(FILTERED, List.of())).contains("sel=[guess(0.5000)]");

    // (9000 - 1000) / (9999 - 1000) of the rows are below, so a ninth of them are at or above.
    assertThat(text(FILTERED, List.of(i64(0, 9000), i32(1, 1)))).contains("sel=[hint(0.1110)]");
  }

  /** The estimate says where it came from, so a reader of a bad plan can see which it was. */
  @Test
  void the_plan_text_distinguishes_measured_guessed_and_hinted() {
    String measured =
        text("SELECT symbol, ts FROM bars WHERE volume >= 9000 LIMIT 1", List.of());

    assertThat(measured).contains("sel=[0.1110]");
    assertThat(measured).doesNotContain("hint(");
    assertThat(text(FILTERED, List.of())).contains("guess(");
    assertThat(text(FILTERED, List.of(i64(0, 9000), i32(1, 1)))).contains("hint(");
  }

  /**
   * A hint of SQL NULL is a statement about the value: no row satisfies a comparison against NULL,
   * so the estimate is the floor every estimate is clamped to.
   *
   * <p>It is still an <em>estimate</em>. The predicate is the predicate the statement wrote and the
   * filter is still there; what must never happen is the one thing a truth would license — folding
   * the branch away into an empty relation. A cost of nearly nothing may well reorder two row-wise
   * operators, which is a hint doing its job, so what is pinned is the operators the plan is made of
   * and not the order cost put them in.
   */
  @Test
  void a_null_hint_selects_no_row_and_never_empties_the_relation() {
    String hinted = text(FILTERED, List.of(nul(0), i32(1, 1)));
    String unhinted = text(FILTERED, List.of());

    assertThat(hinted).contains("hint(0.0000)");
    assertThat(shapeOf(hinted)).containsExactlyInAnyOrderElementsOf(shapeOf(unhinted));
    assertThat(hinted).doesNotContain("ChalkValues");
    assertThat(hinted).contains("ChalkFilter");
    assertThat(hinted).contains("ChalkTableScan");
  }

  // ---- the two bounded nodes ----

  /**
   * A limit whose bound is a hinted parameter estimates the hint, where an unhinted one estimates
   * its input. This is what a goal from a hinted bound is built on (D285, design 49 §4).
   */
  @Test
  void a_hinted_bound_is_the_limits_own_estimate() {
    ChalkLimit plain = find(plan("SELECT symbol, ts FROM bars LIMIT ?", List.of()), ChalkLimit.class);
    assertThat(plain).isNotNull();
    assertThat(rows(plain)).isEqualTo(rows(plain.getInput()));

    ChalkLimit bounded =
        find(plan("SELECT symbol, ts FROM bars LIMIT ?", List.of(i32(0, 7))), ChalkLimit.class);
    assertThat(bounded).isNotNull();
    assertThat(rows(bounded)).isEqualTo(7.0);
  }

  /** And a top-N's heap is the hint's size rather than its whole input's. */
  @Test
  void a_hinted_bound_is_the_top_ns_own_estimate() {
    String sql = "SELECT symbol, ts FROM bars ORDER BY volume DESC LIMIT ?";
    ChalkTopN plain = find(plan(sql, List.of()), ChalkTopN.class);
    assertThat(plain).isNotNull();
    assertThat(rows(plain)).isEqualTo(rows(plain.getInput()));

    ChalkTopN bounded = find(plan(sql, List.of(i32(0, 3))), ChalkTopN.class);
    assertThat(bounded).isNotNull();
    assertThat(rows(bounded)).isEqualTo(3.0);

    // And it is costed for that heap: input rows × log2(3 + 1), which is two.
    assertThat(selfCost(bounded))
        .isCloseTo(rows(bounded.getInput()) * 2.0, org.assertj.core.data.Offset.offset(1e-6));
  }

  // ---- what the estimate is for ----

  /**
   * The row goal of D276, from a hinted bound. The goal is a number the leaf is costed for and
   * never a bound it enforces (design 46 §1), which is what makes taking it from a hint safe.
   */
  @Test
  void a_hinted_bound_goals_the_leaf() {
    assertThat(text(ORDERED, List.of())).doesNotContain("goal=");

    String hinted = text(ORDERED, List.of(str(0, "BTCUSDT"), i32(1, 1)));
    assertThat(hinted).contains("ChalkIndexLookup");
    assertThat(hinted).contains("goal=[1]");
  }

  /** And the goal a filter inflates is inflated by the hinted selectivity, not by a guess. */
  @Test
  void a_hinted_selectivity_inflates_the_goal_it_passes_through() {
    // One row above the filter, one row in nine below it: the leaf expects to be read for ten.
    assertThat(text(FILTERED, List.of(i64(0, 9000), i32(1, 1)))).contains("goal=[10]");
  }

  /**
   * What a hinted bound actually buys, against the two plans it has to beat: the same statement with
   * no bound at all, and the same statement whose bound the planner cannot see.
   *
   * <p>The probe is written as one test so the four plans are compared under one catalog and one
   * cost model. The assertions are on the operators each plan is made of and on the costs relative
   * to each other; nothing here is timed.
   */
  @Test
  void a_hinted_bound_beats_both_no_bound_and_an_unseeable_one() {
    String unbounded = "SELECT symbol, ts FROM bars WHERE symbol >= ? ORDER BY symbol, ts";

    PlannerPipeline.Result noLimit = plan(unbounded, List.of());
    PlannerPipeline.Result unhinted = plan(ORDERED, List.of());
    PlannerPipeline.Result hintedOne = plan(ORDERED, List.of(str(0, "AAA"), i32(1, 1)));
    PlannerPipeline.Result hintedTen = plan(ORDERED, List.of(str(0, "AAA"), i32(1, 10)));

    // (a) and (b) read the whole range: an unseeable bound bounds nothing the planner may rely on,
    // so the leaf under it is the leaf the unbounded statement gets.
    assertThat(PlanText.withAttributes(noLimit.physical())).doesNotContain("goal=");
    assertThat(PlanText.withAttributes(unhinted.physical())).doesNotContain("goal=");

    // (c) and (d) goal the leaf at what the caller said it wants, and take the index that serves
    // the ordering.
    String one = PlanText.withAttributes(hintedOne.physical());
    String ten = PlanText.withAttributes(hintedTen.physical());
    assertThat(one).contains("ChalkIndexLookup").contains("goal=[1]");
    assertThat(ten).contains("ChalkIndexLookup").contains("goal=[10]");

    // And they cost less than either plan that reads the range whole — which is the whole point.
    assertThat(cumulativeCost(hintedOne)).isLessThan(cumulativeCost(noLimit));
    assertThat(cumulativeCost(hintedOne)).isLessThan(cumulativeCost(unhinted));
    assertThat(cumulativeCost(hintedTen)).isLessThan(cumulativeCost(noLimit));
    assertThat(cumulativeCost(hintedTen)).isLessThan(cumulativeCost(unhinted));

    // Ten rows cost more than one and both cost far less than the range: the goal is a number and
    // not a switch.
    assertThat(cumulativeCost(hintedOne)).isLessThan(cumulativeCost(hintedTen));
  }

  // ---- the invariants ----

  /**
   * Invariant 1: the tree is the same with and without hints, and so is the set of parameters the
   * finished plan carries. A hint moves a cost; it does not rewrite anything.
   */
  @Test
  void the_tree_and_the_parameters_are_the_same_with_and_without_hints() throws Exception {
    PlannerPipeline.Result unhinted = plan(FILTERED, List.of());
    PlannerPipeline.Result hinted = plan(FILTERED, List.of(i64(0, 9000), i32(1, 1)));

    assertThat(hinted.logicalPlanText()).isEqualTo(unhinted.logicalPlanText());
    assertThat(parametersOf(ir(FILTERED, List.of(i64(0, 9000), i32(1, 1)))))
        .isEqualTo(parametersOf(ir(FILTERED, List.of())));
  }

  /** Invariant 3: the ordinals reach the diagnostics and the values reach nothing. */
  @Test
  void the_stage_list_names_the_hinted_ordinals_and_no_value() throws Exception {
    PlannerServiceImpl service = service();
    PlanResponse response =
        service.planOrThrow(
            request(FILTERED).addParameterHints(i64(0, 9000)).addParameterHints(i32(1, 7)).build());

    assertThat(response.getStats().getStagesList()).contains("parameter hints 0, 1");
    assertThat(response.getStats().getStagesList().toString()).doesNotContain("9000");
    assertThat(response.getPlanText()).doesNotContain("9000");
  }

  /** Invariant 5: a hint the statement cannot be about is refused by name. */
  @Test
  void a_hint_of_the_wrong_family_is_refused_by_name() {
    assertThatThrownBy(() -> plan(FILTERED, List.of(str(0, "9000"), i32(1, 1))))
        .isInstanceOf(ParameterHintCheck.InvalidHintException.class)
        .hasMessageContaining("the value hint for parameter 0 is a string")
        .hasMessageContaining("which is an exact number")
        .hasMessageNotContaining("9000");
  }

  /** A hint of a floating value for a bound is the wrong family too, never a percentage. */
  @Test
  void a_floating_hint_for_a_bound_is_refused_and_never_read_as_a_percentage() {
    assertThatThrownBy(() -> plan(FILTERED, List.of(i64(0, 9000), fp64(1, 0.1))))
        .isInstanceOf(ParameterHintCheck.InvalidHintException.class)
        .hasMessageContaining("the value hint for parameter 1 is a floating-point number")
        .hasMessageContaining("which is an exact number");
  }

  /** And a hint for a parameter the statement does not have is a mistake rather than nothing said. */
  @Test
  void a_hint_for_a_parameter_the_statement_does_not_have_is_refused_by_name() {
    assertThatThrownBy(() -> plan(FILTERED, List.of(i64(4, 9000))))
        .isInstanceOf(ParameterHintCheck.InvalidHintException.class)
        .hasMessageContaining("names parameter 4 and this statement has 2 parameters");
  }

  /** A hint for a parameter the caller said nothing about leaves that parameter estimated as ever. */
  @Test
  void hints_are_sparse() {
    assertThat(text(FILTERED, List.of(i32(1, 1)))).contains("guess(0.5000)");
  }

  // ---- narrowing (ADR 0074) ----

  /**
   * A narrowing under hints is the plan the same request gives from SQL, byte for byte. The
   * retained half is the validated statement and the converted tree, and neither reads a hint, so
   * re-entering it under any hints is sound by construction.
   */
  @Test
  void a_narrowed_hinted_request_plans_as_the_same_request_from_sql() throws Exception {
    PlannerServiceImpl service = service();
    PlanResponse base = service.planOrThrow(narrowable(FILTERED).build());

    PlanResponse narrowed =
        service.planOrThrow(
            narrowable(FILTERED)
                .setNarrowFrom(base.getPlan().getPlanDigest())
                .addParameterHints(i64(0, 9000))
                .addParameterHints(i32(1, 1))
                .build());
    PlanResponse fromSql =
        service.planOrThrow(
            narrowable(FILTERED)
                .addParameterHints(i64(0, 9000))
                .addParameterHints(i32(1, 1))
                .build());

    assertThat(narrowed.getStats().getStagesList())
        .first(org.assertj.core.api.InstanceOfAssertFactories.STRING)
        .startsWith(PlannerPipeline.STAGE_NARROWED_FROM);
    assertThat(narrowed.getPlan()).isEqualTo(fromSql.getPlan());
    assertThat(narrowed.getPlan().getPlanDigest()).isEqualTo(fromSql.getPlan().getPlanDigest());
  }

  /**
   * Two narrowings of one base under different hints get their own plans, and a third that hints
   * nothing gets the base's. The hints belong to a request and not to a pipeline.
   */
  @Test
  void narrowings_under_different_hints_get_their_own_plans() throws Exception {
    PlannerServiceImpl service = service();
    PlanResponse base = service.planOrThrow(narrowable(ORDERED).build());
    long from = base.getPlan().getPlanDigest();

    PlanResponse goaled =
        service.planOrThrow(
            narrowable(ORDERED)
                .setNarrowFrom(from)
                .addParameterHints(str(0, "BTCUSDT"))
                .addParameterHints(i32(1, 1))
                .build());
    PlanResponse unhinted = service.planOrThrow(narrowable(ORDERED).setNarrowFrom(from).build());

    assertThat(goaled.getPlan().getPlanDigest()).isNotEqualTo(base.getPlan().getPlanDigest());
    assertThat(unhinted.getPlan()).isEqualTo(base.getPlan());
  }

  /**
   * The estimate one request's hints produced must not be answered to the next. A metadata query is
   * cached on the cluster a narrowing re-enters, so the hints are set only after it is dropped; the
   * two {@code sel=} values below are what would be equal if it were not.
   */
  @Test
  void an_estimate_from_one_requests_hints_is_not_answered_to_another() throws Exception {
    PlannerServiceImpl service = service();
    PlanResponse base = service.planOrThrow(narrowable(FILTERED).build());
    long from = base.getPlan().getPlanDigest();

    String first =
        service
            .planOrThrow(
                narrowable(FILTERED)
                    
                    .setNarrowFrom(from)
                    .addParameterHints(i64(0, 9000))
                    .build())
            .getPlanText();
    String second =
        service
            .planOrThrow(
                narrowable(FILTERED)
                    
                    .setNarrowFrom(from)
                    .addParameterHints(i64(0, 2000))
                    .build())
            .getPlanText();

    // A ninth of the rows are at or above 9 000; seven eighths of them are at or above 2 000.
    assertThat(first).contains("sel=[hint(0.1110)]");
    assertThat(second).contains("sel=[hint(0.8889)]");
  }

  // ---- support ----

  private static ParameterHint i32(int ordinal, int value) {
    return hint(ordinal, Literal.newBuilder().setI32Value(value).build());
  }

  private static ParameterHint i64(int ordinal, long value) {
    return hint(ordinal, Literal.newBuilder().setI64Value(value).build());
  }

  private static ParameterHint fp64(int ordinal, double value) {
    return hint(ordinal, Literal.newBuilder().setFp64Value(value).build());
  }

  private static ParameterHint str(int ordinal, String value) {
    return hint(ordinal, Literal.newBuilder().setStringValue(value).build());
  }

  private static ParameterHint nul(int ordinal) {
    return ParameterHint.newBuilder().setOrdinal(ordinal).setIsNull(true).build();
  }

  private static ParameterHint hint(int ordinal, Literal literal) {
    return ParameterHint.newBuilder().setOrdinal(ordinal).setLiteral(literal).build();
  }

  private static String text(String sql, List<ParameterHint> hints) {
    return PlanText.withCosts(plan(sql, hints).physical());
  }

  /** The node names of a plan text, which is its shape with every estimate taken out. */
  private static List<String> shapeOf(String text) {
    return text.lines()
        .map(line -> line.strip().split("\\(", 2)[0])
        .filter(name -> !name.isEmpty())
        .toList();
  }

  private static PlannerPipeline.Result plan(String sql, List<ParameterHint> hints) {
    assumeTrue(
        Files.exists(CorpusQueries.corpusDir().resolve("schemas/corpus.binpb")),
        "the recorded corpus catalog carries the statistics the cost model reads");

    CorpusPlanner planner = new CorpusPlanner();
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            planner.catalog(), PushdownPolicy.full(), SqlConfigs.DEFAULT_CONFORMANCE)) {
      PlannerPipeline.Result result = pipeline.plan(sql, true, hints);
      RelNode physical = result.physical();
      physical.getCluster().setMetadataProvider(ChalkRelMetadata.SOURCE);
      physical.getCluster().invalidateMetadataQuery();
      return result;
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException("planning failed for: " + sql, e);
    }
  }

  private static Plan ir(String sql, List<ParameterHint> hints) throws Exception {
    PlanResponse response =
        service()
            .planOrThrow(request(sql).addAllParameterHints(hints).build());
    return response.getPlan();
  }

  private static PlannerServiceImpl service() {
    assumeTrue(
        Files.exists(CorpusQueries.corpusDir().resolve("schemas/corpus.binpb")),
        "the recorded corpus catalog carries the statistics the cost model reads");
    CatalogRegistry catalogs = new CatalogRegistry();
    catalogs.register(TestCatalogs.corpus());
    return new PlannerServiceImpl(catalogs);
  }

  private static PlanRequest.Builder request(String sql) {
    return PlanRequest.newBuilder()
        .setSql(sql)
        .setContextId(TestCatalogs.CONTEXT_ID)
        .setCatalogEpoch(TestCatalogs.EPOCH)
        .setClientIrVersion(IrVersion.CURRENT)
        .setOptions(
            PlannerOptions.newBuilder()
                .setPushdown(chalk.planner.rpc.v1.PushdownLevel.PUSHDOWN_LEVEL_FULL)
                .setIncludePlanText(true));
  }

  /**
   * The same request with a shape-only context, which is what makes the sidecar retain the
   * converted half and therefore what makes a narrowing possible at all (D233).
   */
  private static PlanRequest.Builder narrowable(String sql) {
    return request(sql)
        .setContext(
            RequestContext.newBuilder()
                .setShapeOnly(true)
                .addScalars(
                    ContextScalar.newBuilder()
                        .setName("tenant")
                        .setValue(
                            chalk.ir.v1.Expr.newBuilder()
                                .setType(
                                    chalk.ir.v1.Type.newBuilder()
                                        .setKind(chalk.ir.v1.TypeKind.TYPE_KIND_I32)))));
  }

  /** The first rel of this type in a planned result, or null. */
  private static <T extends RelNode> T find(PlannerPipeline.Result result, Class<T> type) {
    return find(result.physical(), type);
  }

  /** This node's estimated row count, under the metadata provider {@link #plan} installed. */
  private static double rows(RelNode node) {
    return org.apache.calcite.rel.metadata.RelMetadataQuery.instance().getRowCount(node);
  }

  /** This node's own cost, in the slot Volcano compares. */
  private static double selfCost(RelNode node) {
    org.apache.calcite.plan.RelOptCost cost =
        node.computeSelfCost(
            node.getCluster().getPlanner(),
            org.apache.calcite.rel.metadata.RelMetadataQuery.instance());
    assertThat(cost).isNotNull();
    return cost.getRows();
  }

  /** What the whole plan costs, which is the number two plans are compared by. */
  private static double cumulativeCost(PlannerPipeline.Result result) {
    org.apache.calcite.plan.RelOptCost cost =
        org.apache.calcite.rel.metadata.RelMetadataQuery.instance()
            .getCumulativeCost(result.physical());
    assertThat(cost).isNotNull();
    return cost.getRows();
  }

  /** The first rel of this type in a physical plan, or null. */
  private static <T extends RelNode> T find(RelNode root, Class<T> type) {
    if (type.isInstance(root)) {
      return type.cast(root);
    }

    for (RelNode input : root.getInputs()) {
      T found = find(input, type);
      if (found != null) {
        return found;
      }
    }

    return null;
  }

  /** Every {@code DynamicParam} the plan carries, by ordinal, wherever in the message it sits. */
  private static Set<Integer> parametersOf(Plan plan) {
    Set<Integer> found = new TreeSet<>();
    collect(plan, found);
    return found;
  }

  private static void collect(Message message, Set<Integer> found) {
    if (message instanceof DynamicParam parameter) {
      found.add(parameter.getIndex());
      return;
    }

    for (Map.Entry<Descriptors.FieldDescriptor, Object> field : message.getAllFields().entrySet()) {
      Object value = field.getValue();
      if (value instanceof List<?> repeated) {
        for (Object element : repeated) {
          if (element instanceof Message sub) {
            collect(sub, found);
          }
        }
      } else if (value instanceof Message sub) {
        collect(sub, found);
      }
    }
  }
}
