package chalk.planner.redact;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.CorpusPlanner;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import org.apache.calcite.sql.validate.SqlConformanceEnum;
import org.junit.jupiter.api.Test;

/**
 * The plan text with this statement's literals redacted (D262, {@code
 * docs/design/37-redacted-sql.md} §1, ADR 0043 §4).
 *
 * <p>Two properties, and the second is the one that makes the feature worth anything: the values are
 * gone <em>wherever</em> a rel prints them — an expression, an index range, a {@code VALUES} row —
 * and a value's pseudonym is the one its own statement's redacted SQL shows, so a host reading a log
 * line about a plan and a log line about a statement is reading one pseudonym for one value.
 */
class PlanTextRedactionTest {
  private static final byte[] SALT = "a fixed salt".getBytes(StandardCharsets.UTF_8);

  private static final Pattern MARKER =
      Pattern.compile("/\\*REDACTED-([0-9a-f]{8}):([A-Z0-9_]+)\\*/");

  private static final CorpusPlanner PLANNER = new CorpusPlanner();

  private static RedactionPolicy policy(boolean keepStructural) {
    return RedactionPolicy.of(SALT, RedactionPolicy.Scope.ALL, keepStructural);
  }

  /** The redacted SQL and the redacted plan text of one statement, keyed by one seed. */
  private record Redacted(String sql, String logical, String physical, String plain) {}

  private static Redacted redact(String sql, boolean keepStructural) throws Exception {
    RedactionPolicy policy = policy(keepStructural);
    SqlRedactor.Result statement = SqlRedactor.redact(sql, SqlConformanceEnum.DEFAULT, policy);
    PlanTextRedactor redactor =
        new PlanTextRedactor(SqlRedactor.pseudonyms(statement.structuralHash(), policy), policy);

    String plain;
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(PLANNER.catalog(), PushdownPolicy.full())) {
      plain = pipeline.finish(pipeline.front(sql), BoundContext.EMPTY, true).physicalPlanText();
    }
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(PLANNER.catalog(), PushdownPolicy.full())) {
      PlannerPipeline.Result result =
          pipeline.finish(pipeline.front(sql), BoundContext.EMPTY, true, null, null, redactor);
      return new Redacted(
          statement.redactedSql(), result.logicalPlanText(), result.physicalPlanText(), plain);
    }
  }

  private static List<String> pseudonyms(String text) {
    List<String> hexes = new ArrayList<>();
    Matcher matcher = MARKER.matcher(text);
    while (matcher.find()) {
      hexes.add(matcher.group(1));
    }
    return hexes;
  }

  @Test
  void a_predicates_literal_is_a_pseudonym_in_the_plan_and_the_same_one_as_in_the_statement()
      throws Exception {
    Redacted redacted = redact("SELECT symbol, ts FROM bars WHERE symbol = 'BTCUSDT'", true);

    assertThat(redacted.plain()).contains("BTCUSDT");
    assertThat(redacted.physical()).doesNotContain("BTCUSDT");
    assertThat(redacted.logical()).doesNotContain("BTCUSDT");
    assertThat(pseudonyms(redacted.physical()))
        .isNotEmpty()
        .allMatch(hex -> pseudonyms(redacted.sql()).contains(hex));
  }

  /**
   * An index range's bounds are the statement's literals too, and a rel renders them itself.
   *
   * <p>No {@code LIMIT}: with one, the planner now offers a goaled scan (D276) and takes it, because
   * fifteen rows read at a unit each beat a seek at seventeen. The statement that produces a lookup
   * is the one with no limit at all, and a lookup is what this test is about. {@code LIMIT} in the
   * plan text has its own case below.
   */
  @Test
  void an_index_lookups_ranges_are_redacted() throws Exception {
    Redacted redacted = redact("SELECT symbol, ts FROM bars WHERE symbol = 'BTCUSDT'", true);

    assertThat(redacted.plain()).contains("ranges=[[[['BTCUSDT'");
    assertThat(redacted.physical()).contains("ranges=[[[[/*REDACTED-");
    assertThat(redacted.physical()).doesNotContain("BTCUSDT");
  }

  /** So are a {@code VALUES}' rows, which Calcite renders into strings before the writer sees them. */
  @Test
  void a_values_rows_are_redacted() throws Exception {
    Redacted redacted = redact("SELECT * FROM (VALUES (1, 'a'), (2, 'b')) AS v(i, s)", true);

    assertThat(redacted.plain()).contains("tuples=[[{ 1, 'a' }, { 2, 'b' }]]");
    assertThat(redacted.physical()).doesNotContain("'a'").doesNotContain("'b'");
    assertThat(redacted.physical()).containsPattern("tuples=\\[\\[\\{ /\\*REDACTED-");
    // The same shape as the plain rendering, brackets and braces included.
    assertThat(MARKER.matcher(redacted.physical()).replaceAll("?")).contains("tuples=[[{ ?, ? }, { ?, ? }]]");
  }

  /** {@code LIMIT} is a count of rows in a plan exactly as it is in a statement. */
  @Test
  void a_fetch_is_kept_in_the_plan_text() throws Exception {
    Redacted redacted = redact("SELECT symbol FROM bars WHERE symbol = 'BTCUSDT' LIMIT 3", true);

    assertThat(redacted.physical()).contains("fetch=[3]");
  }

  @Test
  void keep_structural_off_redacts_the_plans_fetch_too() throws Exception {
    Redacted redacted = redact("SELECT symbol FROM bars WHERE symbol = 'BTCUSDT' LIMIT 3", false);

    assertThat(redacted.physical()).doesNotContain("fetch=[3]");
    assertThat(redacted.physical()).containsPattern("fetch=\\[/\\*REDACTED-");
  }

  /**
   * The unredacted rendering is the one it always was, which is what keeps every recorded plan text
   * exactly where it is: a request that asks for no redaction goes through {@code
   * RelOptUtil.dumpPlan} itself.
   */
  @Test
  void the_plain_rendering_is_untouched() throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(PLANNER.catalog(), PushdownPolicy.full())) {
      PlannerPipeline.Result result =
          pipeline.finish(
              pipeline.front("SELECT symbol, ts FROM bars WHERE symbol = 'BTCUSDT'"),
              BoundContext.EMPTY,
              true);

      // The same string RelOptUtil.dumpPlan gives, because for a run with no redactor it *is*
      // RelOptUtil.dumpPlan. (Compared within one planning: a rel's `id` is a counter across the
      // process, so two plannings of one statement differ in the ids and in nothing else.)
      assertThat(result.physicalPlanText())
          .isEqualTo(PlannerPipeline.explainWithCost(result.physical()));
      assertThat(result.logicalPlanText()).doesNotContain("REDACTED");
    }
  }
}
