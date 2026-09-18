package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.Expr;
import chalk.ir.v1.FunctionId;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.planner.UnsupportedFeatureException;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.Test;

/**
 * D52 — the tumbling window. {@code TUMBLE} is rewritten into the projection that computes the same
 * thing row by row, so it plans exactly like the {@code TIME_BUCKET} query a host would write by
 * hand; {@code HOP} and {@code SESSION} are refused, naming §9.
 */
class TumbleRewriteTest {
  private static final CorpusPlanner PLANNER = new CorpusPlanner();

  private static List<Expr> calls(Plan plan) {
    List<Expr> found = new ArrayList<>();
    collect(plan.getRoot(), found);
    return found;
  }

  private static void collect(Rel rel, List<Expr> found) {
    if (rel.getKindCase() == Rel.KindCase.PROJECT) {
      for (Expr expr : rel.getProject().getExprsList()) {
        walk(expr, found);
      }

      collect(rel.getProject().getInput(), found);
      return;
    }

    switch (rel.getKindCase()) {
      case FILTER -> collect(rel.getFilter().getInput(), found);
      case SORT -> collect(rel.getSort().getInput(), found);
      case HASH_AGGREGATE -> collect(rel.getHashAggregate().getAggregate().getInput(), found);
      case AGGREGATE -> collect(rel.getAggregate().getInput(), found);
      default -> {
        // A leaf, or a node the rewrite cannot appear under.
      }
    }
  }

  private static void walk(Expr expr, List<Expr> found) {
    if (expr.hasCall()) {
      found.add(expr);
      for (Expr argument : expr.getCall().getArgsList()) {
        walk(argument, found);
      }
    }
  }

  @Test
  void tumble_becomes_a_time_bucket_projection() {
    Plan plan =
        PLANNER.plan(
            "SELECT symbol, window_start, SUM(volume) FROM "
                + "TABLE(TUMBLE(TABLE bars_small, DESCRIPTOR(ts), INTERVAL '5' MINUTE)) "
                + "GROUP BY symbol, window_start");

    assertThat(calls(plan))
        .as("the rewrite computes the bucket with TIME_BUCKET")
        .anyMatch(e -> e.getCall().getFunction() == FunctionId.FUNCTION_ID_TIME_BUCKET);

    // The interval reaches the IR in microseconds: five minutes is 300 000 000 of them.
    Expr bucket =
        calls(plan).stream()
            .filter(e -> e.getCall().getFunction() == FunctionId.FUNCTION_ID_TIME_BUCKET)
            .findFirst()
            .orElseThrow();
    assertThat(bucket.getCall().getArgs(0).getLiteral().getIntervalDayValue())
        .isEqualTo(300_000_000L);
    assertThat(bucket.getCall().getArgs(1).hasFieldRef()).isTrue();
  }

  /**
   * The point of the rewrite: {@code TUMBLE} and the hand-written {@code TIME_BUCKET} are the same
   * query, so they plan into the same shape. The digests differ only because the column the query
   * names is called {@code window_start} in one and {@code bucket} in the other.
   */
  @Test
  void tumble_and_time_bucket_plan_alike() {
    Plan tumble =
        PLANNER.plan(
            "SELECT symbol, window_start, SUM(volume) FROM "
                + "TABLE(TUMBLE(TABLE bars_small, DESCRIPTOR(ts), INTERVAL '5' MINUTE)) "
                + "GROUP BY symbol, window_start");
    Plan manual =
        PLANNER.plan(
            "SELECT symbol, TIME_BUCKET(INTERVAL '5' MINUTE, ts) AS window_start, SUM(volume) "
                + "FROM bars_small GROUP BY symbol, TIME_BUCKET(INTERVAL '5' MINUTE, ts)");

    assertThat(tumble.getRoot().getKindCase()).isEqualTo(manual.getRoot().getKindCase());
    assertThat(tumble.getPlanDigest()).isEqualTo(manual.getPlanDigest());
  }

  /** {@code SELECT *} keeps both bucket bounds, and the end is the start plus the width. */
  @Test
  void select_star_carries_window_start_and_window_end() {
    Plan plan =
        PLANNER.plan(
            "SELECT * FROM TABLE(TUMBLE(TABLE bars_small, DESCRIPTOR(ts), INTERVAL '5' MINUTE))");

    List<String> names =
        plan.getOutputType().getFieldsList().stream().map(f -> f.getName()).toList();
    assertThat(names).containsSubsequence("window_start", "window_end");
    assertThat(calls(plan))
        .anyMatch(e -> e.getCall().getFunction() == FunctionId.FUNCTION_ID_ADD);
  }

  /**
   * A table function that is neither {@code TUMBLE} nor one of step 19's two is still refused on the
   * logical tree, naming itself.
   */
  @Test
  void an_unknown_table_function_is_refused_naming_itself() {
    assertThatThrownBy(
            () -> PLANNER.plan(
                "SELECT * FROM TABLE(TUMBLE(TABLE bars_small, DESCRIPTOR(ts), "
                    + "INTERVAL '5' MINUTE, INTERVAL '1' MINUTE))"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("TUMBLE with an alignment offset");
  }
}
