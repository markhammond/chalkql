package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.diag.PlanText;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import chalk.ir.v1.SqlConformance;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.stream.Stream;
import org.apache.calcite.sql.parser.SqlParseException;
import org.apache.calcite.sql.validate.SqlConformanceEnum;
import org.apache.calcite.tools.ValidationException;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.EnumSource;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * The Java-side golden plan: for each corpus query the physical plan text must match the checked-in
 * expectation (docs/design/03-planner.md §8). It complements the IR digest golden on the .NET side —
 * a rule or cost change that alters plan shape shows up here first, without needing both toolchains.
 *
 * <p>Regenerate with {@code CHALK_WRITE_FIXTURES=1} and review the diff.
 */
class PipelineTest {
  private static RegisteredCatalog catalog;

  private static final Path EXPECTED =
      Path.of(System.getProperty("chalk.projectDir", ".")).resolve("src/test/resources/expected");

  @BeforeAll
  static void registerCatalog() {
    catalog = new CatalogRegistry().register(TestCatalogs.corpus());
  }

  static Stream<CorpusQueries.Query> queries() {
    return CorpusQueries.all().stream();
  }

  @ParameterizedTest(name = "{0}")
  @MethodSource("queries")
  void physical_plan_matches_the_checked_in_expectation(CorpusQueries.Query query) throws Exception {
    String full = plan(query, PushdownPolicy.full());
    String none = plan(query, PushdownPolicy.none());
    String text = "-- FULL\n" + full + "-- NONE\n" + none;

    Path file = EXPECTED.resolve(query.name() + ".plan.txt");
    if ("1".equals(System.getenv("CHALK_WRITE_FIXTURES"))) {
      Files.createDirectories(EXPECTED);
      Files.writeString(file, text, StandardCharsets.UTF_8);
      return;
    }

    assertThat(file)
        .as("expected plan for %s; regenerate with CHALK_WRITE_FIXTURES=1", query.name())
        .exists();
    assertThat(text)
        .as("physical plan for %s", query.name())
        .isEqualTo(Files.readString(file, StandardCharsets.UTF_8));
  }

  /** The four plan-shape claims rev 3 and the design call out by name. */
  @ParameterizedTest(name = "{0}")
  @MethodSource("queries")
  void plan_shape_expectations_hold(CorpusQueries.Query query) throws Exception {
    String full = plan(query, PushdownPolicy.full());
    for (String expectation : query.expectations()) {
      switch (expectation) {
        case "not(Sort)" ->
            assertThat(full).as("%s must not sort", query.name()).doesNotContain("ChalkSort");
        case "not(TopN)" -> assertThat(full).doesNotContain("ChalkTopN");
        case "not(Fetch)" -> assertThat(full).doesNotContain("ChalkLimit");
        case "not(Read)" -> assertThat(full).doesNotContain("ChalkTableScan");
        case "not(Project)" -> assertThat(full).doesNotContain("ChalkProject");
        case "has(Sort)" -> assertThat(full).contains("ChalkSort");
        case "has(TopN)" -> assertThat(full).contains("ChalkTopN");
        case "has(Fetch)" -> assertThat(full).contains("ChalkLimit");
        case "has(Filter)" -> assertThat(full).contains("ChalkFilter");
        case "has(Project)" -> assertThat(full).contains("ChalkProject");
        case "has(HashAggregate)" -> assertThat(full).contains("ChalkHashAggregate");
        case "has(Window)" -> assertThat(full).contains("ChalkWindow");
        case "not(Window)" -> assertThat(full).doesNotContain("ChalkWindow");
        case "has(IndexLookup)" -> assertThat(full).contains("ChalkIndexLookup");
        case "not(IndexLookup)" -> assertThat(full).doesNotContain("ChalkIndexLookup");
        case "has(VirtualTable)" -> assertThat(full).contains("ChalkValues");
        case "not(HashAggregate)", "not(Aggregate)" ->
            assertThat(full)
                .as("%s: the declared unique key should have removed the aggregate", query.name())
                .doesNotContain("ChalkHashAggregate");
        default -> {
          // The remaining expectations are IR-level and are checked by RelToIrTest and the .NET
          // GoldenPlanTests, which can see expressions rather than plan text.
        }
      }
    }
  }

  /**
   * D34: the request's conformance reaches both halves of the front end.
   *
   * <p>One case per value of the proto enum, and the expectation is asked of Calcite rather than
   * written down here — {@code isOffsetLimitAllowed} is a parser rule and {@code isGroupByAlias} is
   * a validator rule, so a level that reached only one of the two configs would fail on the other.
   */
  @ParameterizedTest(name = "{0}")
  @EnumSource(
      value = SqlConformance.class,
      names = "UNRECOGNIZED",
      mode = EnumSource.Mode.EXCLUDE)
  void the_requested_conformance_reaches_the_parser_and_the_validator(SqlConformance requested) {
    SqlConformanceEnum conformance = SqlConfigs.conformance(requested.getNumber());

    assertThat(plans("SELECT symbol, ts FROM bars ORDER BY ts OFFSET 3 LIMIT 5", conformance))
        .as("%s: OFFSET before LIMIT is a parser rule", requested)
        .isEqualTo(conformance.isOffsetLimitAllowed());

    assertThat(plans("SELECT symbol AS s, COUNT(*) FROM bars GROUP BY s", conformance))
        .as("%s: GROUP BY on a SELECT alias is a validator rule", requested)
        .isEqualTo(conformance.isGroupByAlias());
  }

  /** UNSPECIFIED means DEFAULT, which is the standard dialect and not D15's pinned LENIENT. */
  @Test
  void an_unset_conformance_is_the_default_dialect() {
    assertThat(SqlConfigs.conformance(SqlConformance.SQL_CONFORMANCE_UNSPECIFIED.getNumber()))
        .isEqualTo(SqlConformanceEnum.DEFAULT)
        .isEqualTo(SqlConfigs.DEFAULT_CONFORMANCE);
    assertThat(SqlConfigs.summary()).contains("conformance=DEFAULT");
  }

  /** A value from a newer client is named, not silently planned as something else. */
  @Test
  void an_unknown_conformance_value_is_rejected_by_number() {
    assertThatThrownBy(() -> SqlConfigs.conformance(99))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("99");
  }

  private static boolean plans(String sql, SqlConformanceEnum conformance) {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(catalog, PushdownPolicy.full(), conformance)) {
      pipeline.plan(sql, false);
      return true;
    } catch (SqlParseException | ValidationException e) {
      return false;
    } catch (Exception e) {
      throw new IllegalStateException("planning failed for: " + sql, e);
    }
  }

  private static String plan(CorpusQueries.Query query, PushdownPolicy policy) throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(catalog, policy, query.conformance())) {
      PlannerPipeline.Result result = pipeline.plan(query.sql(), false);
      return PlanText.withAttributes(result.physical());
    }
  }
}
