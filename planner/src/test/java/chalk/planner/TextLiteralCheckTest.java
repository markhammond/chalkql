package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatCode;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import java.nio.file.Files;
import java.util.List;
import org.apache.calcite.runtime.CalciteContextException;
import org.junit.jupiter.api.Test;

/**
 * F278 against the recorded corpus catalog: text compared with a number that it cannot be read as
 * is refused where it was written, naming the type and the column, as the statement's mistake; text
 * that reads as the number is a value written in quotes and plans. Text compared with a time plans
 * whatever it holds, the offsets ChalkQL's cast reads among it: only that cast knows what it takes,
 * and it reads the text when the plan runs.
 *
 * <p>{@code bars_small} has {@code high}, a {@code DOUBLE}; {@code volume}, a {@code BIGINT};
 * {@code ts}, a {@code TIMESTAMP}; and {@code symbol}, text.
 */
class TextLiteralCheckTest {

  @Test
  void text_that_is_not_a_number_compared_with_a_double_is_refused_where_it_was_written() {
    assertThatThrownBy(() -> plan("SELECT symbol FROM bars_small WHERE high = 'x'"))
        .isInstanceOf(CalciteContextException.class)
        .hasMessageContaining("the text 'x' cannot be read as a DOUBLE, which high is")
        .hasMessageContaining("compare high with a number, such as 1.5")
        .satisfies(
            e -> {
              CalciteContextException context = (CalciteContextException) e;
              assertThat(context.getPosLine()).isEqualTo(1);
              assertThat(context.getPosColumn()).isEqualTo(44);
              assertThat(context.getCause()).isInstanceOf(InvalidArgumentException.class);
              assertThat(((InvalidArgumentException) context.getCause()).code()).isEqualTo(ErrorCode.SQL_VALIDATION);
            });
  }

  @Test
  void every_comparison_is_held_to_it_and_every_side() {
    for (String sql : List.of(
        "SELECT symbol FROM bars_small WHERE high > 'x'",
        "SELECT symbol FROM bars_small WHERE 'x' <= high",
        "SELECT symbol FROM bars_small WHERE high <> 'x'",
        "SELECT symbol FROM bars_small WHERE high IN (1.5, 'x')",
        "SELECT symbol FROM bars_small WHERE high NOT IN ('x')",
        "SELECT symbol FROM bars_small WHERE high BETWEEN 1 AND 'x'")) {
      assertThatThrownBy(() -> plan(sql), sql)
          .isInstanceOf(CalciteContextException.class)
          .hasMessageContaining("the text 'x' cannot be read as a DOUBLE");
    }
  }

  @Test
  void a_whole_number_type_takes_whole_numbers_only() {
    assertThatThrownBy(() -> plan("SELECT symbol FROM bars_small WHERE volume = '1.5'"))
        .isInstanceOf(CalciteContextException.class)
        .hasMessageContaining("the text '1.5' cannot be read as a BIGINT, which volume is")
        .hasMessageContaining("a whole number, such as 42");
  }

  @Test
  void text_compared_with_a_time_plans_and_is_read_when_the_plan_runs() {
    for (String sql : List.of(
        "SELECT symbol FROM bars_small WHERE ts = '2026-01-08 12:00:00+02:00'",
        "SELECT symbol FROM bars_small WHERE ts > CAST('2026-01-08 12:00:00+02:00' AS TIMESTAMP)",
        "SELECT symbol FROM bars_small WHERE ts = 'tomorrow'")) {
      assertThatCode(() -> plan(sql)).as(sql).doesNotThrowAnyException();
    }
  }

  @Test
  void text_that_reads_as_the_type_is_a_value_in_quotes_and_plans() {
    for (String sql : List.of(
        "SELECT symbol FROM bars_small WHERE high = '1.5'",
        "SELECT symbol FROM bars_small WHERE high = ' 1e3 '",
        "SELECT symbol FROM bars_small WHERE volume IN ('42', '7')",
        "SELECT symbol FROM bars_small WHERE ts = '2026-01-01 00:00:00'",
        "SELECT symbol FROM bars_small WHERE symbol = 'x'",
        "SELECT symbol FROM bars_small WHERE symbol IN ('x', 'y')")) {
      assertThatCode(() -> plan(sql)).as(sql).doesNotThrowAnyException();
    }
  }

  private static PlannerPipeline.Result plan(String sql) {
    assumeTrue(
        Files.exists(CorpusQueries.corpusDir().resolve("schemas/corpus.binpb")),
        "the recorded corpus catalog has bars_small");
    CorpusPlanner planner = new CorpusPlanner();
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(planner.catalog(), PushdownPolicy.full(), SqlConfigs.DEFAULT_CONFORMANCE, List.of())) {
      return pipeline.plan(sql, true);
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new AssertionError("planning failed for: " + sql, e);
    }
  }
}
