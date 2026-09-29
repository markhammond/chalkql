package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import chalk.ir.v1.IndexLookup;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.planner.diag.PlanText;
import chalk.planner.ir.IrVersionGate;
import chalk.planner.ir.RelToIr;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.runtime.CalciteContextException;
import org.junit.jupiter.api.Test;

/**
 * D312–D314 against the recorded corpus catalog: a malformed escape is refused where it was
 * written, the three-operand {@code LIKE} reaches a prefix index through its escape, and a
 * parameter pattern is a prefix lookup that re-checks its own {@code LIKE}.
 *
 * <p>{@code terms} has a PREFIX index on {@code name}; {@code bars_small} an ordered one led by
 * {@code symbol}.
 */
class LikeEscapeTest {

  // ---- D312: malformed escapes, refused with their position ----

  @Test
  void a_pattern_ending_in_its_escape_is_refused_where_it_was_written() {
    assertThatThrownBy(() -> plan("SELECT name FROM terms WHERE name LIKE 'Int!' ESCAPE '!'"))
        .isInstanceOf(CalciteContextException.class)
        .hasMessageContaining("ends with its escape character '!'")
        .satisfies(
            e -> {
              CalciteContextException context = (CalciteContextException) e;
              assertThat(context.getPosLine()).isEqualTo(1);
              assertThat(context.getPosColumn()).isEqualTo(40);
            });
  }

  @Test
  void an_escape_before_an_ordinary_character_is_refused() {
    assertThatThrownBy(() -> plan("SELECT name FROM terms WHERE name LIKE 'I!nt%' ESCAPE '!'"))
        .isInstanceOf(CalciteContextException.class)
        .hasMessageContaining("before an ordinary character at position 2");
  }

  @Test
  void an_escape_that_is_not_one_character_is_refused() {
    assertThatThrownBy(() -> plan("SELECT name FROM terms WHERE name LIKE 'Int%' ESCAPE '!!'"))
        .isInstanceOf(CalciteContextException.class)
        .hasMessageContaining("exactly one character, not 2 characters");
    assertThatThrownBy(() -> plan("SELECT name FROM terms WHERE name LIKE 'Int%' ESCAPE ''"))
        .isInstanceOf(CalciteContextException.class)
        .hasMessageContaining("not the empty string");
  }

  @Test
  void an_escape_that_is_a_parameter_is_refused() {
    assertThatThrownBy(() -> plan("SELECT name FROM terms WHERE name LIKE 'Int%' ESCAPE ?"))
        .isInstanceOf(CalciteContextException.class)
        .hasMessageContaining("must be a character literal");
  }

  @Test
  void a_not_like_is_held_to_the_same_rules() {
    assertThatThrownBy(() -> plan("SELECT name FROM terms WHERE name NOT LIKE 'a!b' ESCAPE '!'"))
        .isInstanceOf(CalciteContextException.class)
        .hasMessageContaining("before an ordinary character");
  }

  @Test
  void a_backslash_without_an_escape_clause_is_an_ordinary_character() {
    assertThat(text("SELECT name FROM terms WHERE name LIKE 'Int\\'")).contains("LIKE");
  }

  @Test
  void a_pattern_that_is_malformed_only_once_folded_is_refused_where_the_plan_is_written() {
    assertThatThrownBy(() -> ir("SELECT name FROM terms WHERE name LIKE 'Int' || '!' ESCAPE '!'"))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("ends with its escape character");
  }

  // ---- D313: the three-operand form reaches the index ----

  @Test
  void an_escaped_prefix_is_a_lookup_on_a_prefix_index_with_nothing_to_recheck() {
    String text = text("SELECT name FROM terms WHERE name LIKE 'In!_%' ESCAPE '!'");
    assertThat(text).contains("ChalkIndexLookup").contains("escape '!'");
    assertThat(text).doesNotContain("ChalkFilter").doesNotContain("residual");

    IndexLookup lookup = lookupOf(ir("SELECT name FROM terms WHERE name LIKE 'In!_%' ESCAPE '!'"));
    assertThat(lookup.getRanges(0).getPrefix()).isTrue();
    assertThat(lookup.getRanges(0).getEscape()).isEqualTo("!");
    assertThat(lookup.hasResidual()).isFalse();
  }

  @Test
  void an_escaped_prefix_is_a_lookup_on_an_ordered_index() {
    String text = text("SELECT symbol FROM bars_small WHERE symbol LIKE 'BTC\\%%' ESCAPE '\\'");
    assertThat(text).contains("ChalkIndexLookup").contains("escape '\\'");
    assertThat(text).doesNotContain("ChalkFilter");
  }

  @Test
  void an_escaped_equality_is_not_a_prefix() {
    assertThat(text("SELECT name FROM terms WHERE name LIKE 'Int!%' ESCAPE '!'"))
        .doesNotContain("ChalkIndexLookup");
  }

  @Test
  void a_two_operand_prefix_is_written_as_it_was() {
    IndexLookup lookup = lookupOf(ir("SELECT name FROM terms WHERE name LIKE 'Int%'"));
    assertThat(lookup.getRanges(0).getEscape()).isEmpty();
    assertThat(lookup.hasResidual()).isFalse();
  }

  // ---- D314: a parameter pattern re-checks its own LIKE ----

  @Test
  void a_parameter_pattern_is_a_lookup_that_rechecks_its_like() {
    String text = text("SELECT name FROM terms WHERE name LIKE ?");
    assertThat(text).contains("ChalkIndexLookup").contains("residual=[LIKE(");
    assertThat(text).doesNotContain("ChalkFilter");

    IndexLookup lookup = lookupOf(ir("SELECT name FROM terms WHERE name LIKE ?"));
    assertThat(lookup.getRanges(0).getPrefix()).isTrue();
    assertThat(lookup.hasResidual()).isTrue();
    assertThat(lookup.getResidual().getCall().getFunction())
        .isEqualTo(chalk.ir.v1.FunctionId.FUNCTION_ID_LIKE);
  }

  @Test
  void a_parameter_pattern_under_an_escape_carries_it_on_the_range_and_the_residual() {
    IndexLookup lookup = lookupOf(ir("SELECT name FROM terms WHERE name LIKE ? ESCAPE '!'"));
    assertThat(lookup.getRanges(0).getEscape()).isEqualTo("!");
    assertThat(lookup.getResidual().getCall().getArgsCount()).isEqualTo(3);
  }

  @Test
  void a_parameter_pattern_elsewhere_is_an_ordinary_filter() {
    // text has no index; the LIKE is a Filter over the scan, and plans at all (D314).
    String text = text("SELECT name FROM terms WHERE text LIKE ?");
    assertThat(text).contains("ChalkFilter").doesNotContain("ChalkIndexLookup");
  }

  // ---- D316: ILIKE is its own function, never a lookup ----

  /** ILIKE is Chalk's own operator (D316): no library needs naming, and naming PostgreSQL's is harmless. */
  @Test
  void ilike_needs_no_library() {
    assertThat(functions(ir("SELECT name FROM terms WHERE name ILIKE 'INT%'")))
        .contains(chalk.ir.v1.FunctionId.FUNCTION_ID_ILIKE);
    assertThat(functions(ir("SELECT name FROM terms WHERE name NOT ILIKE 'INT%'")))
        .contains(chalk.ir.v1.FunctionId.FUNCTION_ID_ILIKE);
  }

  @Test
  void ilike_is_its_own_function_and_never_an_index_lookup() {
    Plan plan = ir("SELECT name FROM terms WHERE name ILIKE 'INT%'", POSTGRESQL);

    assertThat(IrNodes.of(plan, Rel.KindCase.INDEX_LOOKUP)).isEmpty();
    assertThat(functions(plan))
        .contains(chalk.ir.v1.FunctionId.FUNCTION_ID_ILIKE)
        .doesNotContain(chalk.ir.v1.FunctionId.FUNCTION_ID_LIKE);
  }

  @Test
  void a_not_ilike_is_the_negation_of_ilike() {
    Plan plan = ir("SELECT name FROM terms WHERE name NOT ILIKE ?", POSTGRESQL);

    assertThat(functions(plan))
        .contains(chalk.ir.v1.FunctionId.FUNCTION_ID_NOT, chalk.ir.v1.FunctionId.FUNCTION_ID_ILIKE)
        .doesNotContain(chalk.ir.v1.FunctionId.FUNCTION_ID_LIKE);
  }

  @Test
  void an_ilike_escape_is_held_to_the_same_rules() {
    assertThatThrownBy(
            () -> plan("SELECT name FROM terms WHERE name ILIKE 'Int!' ESCAPE '!'", POSTGRESQL))
        .isInstanceOf(CalciteContextException.class)
        .hasMessageContaining("ends with its escape character");
  }

  /** F154: a LIKE-kind operator that is neither LIKE nor ILIKE is refused, not planned as LIKE. */
  @Test
  void another_like_kind_operator_is_refused_by_name() {
    org.apache.calcite.sql.SqlOperator stranger =
        new org.apache.calcite.sql.SqlBinaryOperator(
            "SOUNDS_LIKE",
            org.apache.calcite.sql.SqlKind.LIKE,
            32,
            false,
            org.apache.calcite.sql.type.ReturnTypes.BOOLEAN_NULLABLE,
            org.apache.calcite.sql.type.InferTypes.FIRST_KNOWN,
            org.apache.calcite.sql.type.OperandTypes.STRING_SAME_SAME);

    assertThatThrownBy(() -> chalk.planner.ir.FunctionMapping.scalar(stranger, false))
        .isInstanceOf(chalk.planner.UnsupportedFeatureException.class)
        .hasMessageContaining("SOUNDS_LIKE");
    assertThat(
            chalk.planner.ir.FunctionMapping.scalar(
                org.apache.calcite.sql.fun.SqlLibraryOperators.ILIKE, false))
        .isEqualTo(chalk.ir.v1.FunctionId.FUNCTION_ID_ILIKE);
  }

  private static final List<org.apache.calcite.sql.fun.SqlLibrary> POSTGRESQL =
      List.of(org.apache.calcite.sql.fun.SqlLibrary.POSTGRESQL);

  private static List<chalk.ir.v1.FunctionId> functions(Plan plan) {
    List<chalk.ir.v1.FunctionId> found = new ArrayList<>();
    collectFunctions(plan.getRoot(), found);
    return found;
  }

  private static void collectFunctions(Rel rel, List<chalk.ir.v1.FunctionId> found) {
    String text = rel.toString();
    for (chalk.ir.v1.FunctionId id : chalk.ir.v1.FunctionId.values()) {
      if (id != chalk.ir.v1.FunctionId.UNRECOGNIZED
          && java.util.regex.Pattern.compile("\\bfunction: " + id.name() + "\\b").matcher(text).find()) {
        found.add(id);
      }
    }
  }

  private static IndexLookup lookupOf(Plan plan) {
    List<IndexLookup> found = new ArrayList<>();
    collect(plan.getRoot(), found);
    assertThat(found).hasSize(1);
    return found.get(0);
  }

  private static void collect(Rel rel, List<IndexLookup> found) {
    if (rel.hasIndexLookup()) {
      found.add(rel.getIndexLookup());
    }
    for (Rel input : IrNodes.inputs(rel)) {
      collect(input, found);
    }
  }

  private static String text(String sql) {
    return PlanText.withAttributes(plan(sql).physical());
  }

  private static PlannerPipeline.Result plan(String sql) {
    return plan(sql, List.of());
  }

  private static PlannerPipeline.Result plan(
      String sql, List<org.apache.calcite.sql.fun.SqlLibrary> libraries) {
    assumeTrue(
        Files.exists(CorpusQueries.corpusDir().resolve("schemas/corpus.binpb")),
        "the recorded corpus catalog carries the statistics the cost model reads");
    CorpusPlanner planner = new CorpusPlanner();
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            planner.catalog(), PushdownPolicy.full(), SqlConfigs.DEFAULT_CONFORMANCE, libraries)) {
      return pipeline.plan(sql, true);
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new AssertionError("planning failed for: " + sql, e);
    }
  }

  private static Plan ir(String sql) {
    return ir(sql, List.of());
  }

  private static Plan ir(String sql, List<org.apache.calcite.sql.fun.SqlLibrary> libraries) {
    PlannerPipeline.Result result = plan(sql, libraries);
    return new RelToIr(
            new chalk.planner.types.TypeMapper(result.physical().getCluster().getTypeFactory()),
            result.physical().getCluster().getRexBuilder(),
            RelMetadataQuery.instance(),
            IrVersionGate.current())
        .toPlan(result.physical(), result.parameterRowType(), "test", 1L);
  }
}
