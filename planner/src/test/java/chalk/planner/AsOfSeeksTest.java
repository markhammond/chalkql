package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.diag.PlanText;
import chalk.planner.plan.AsOfSeeks;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.rel.ChalkAsOfJoin;
import chalk.planner.rpc.v1.PushdownLevel;
import java.util.ArrayList;
import java.util.List;
import java.util.function.Function;
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelNode;
import org.junit.jupiter.api.Test;

/**
 * ASOF joins priced again once the search is over (F198). Each statement is planned with index
 * lookups disallowed, so the search can only choose the plain join, as it does when it priced the
 * seek on numbers the final plan does not have. The pass then prices both on the final plan's
 * numbers. {@code bars} has 100 800 rows and an index on {@code (symbol, ts)}; {@code bars_small}
 * has 5 000 rows and the same index.
 */
class AsOfSeeksTest {
  private static final String PROBES =
      "WITH q (symbol, t0) AS (VALUES ('BTCUSDT', TIMESTAMP '2026-01-01 00:00:00'),"
          + " ('ETHUSDT', TIMESTAMP '2026-01-02 00:00:00')) ";

  /** The plan the search chose without seeks, then what the pass makes of it. */
  private record Repriced(String before, String after, int swapped, boolean sameRowType) {}

  private static Repriced reprice(String sql) {
    return reprice(sql, Function.identity());
  }

  private static Repriced reprice(String sql, Function<RelNode, RelNode> edit) {
    RegisteredCatalog catalog = new CatalogRegistry().register(TestCatalogs.corpus());
    PushdownPolicy noSeeks = new PushdownPolicy(PushdownLevel.PUSHDOWN_LEVEL_FULL, true);
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, noSeeks)) {
      RelNode searched = edit.apply(pipeline.plan(sql, false).physical());
      AsOfSeeks.Result result = AsOfSeeks.reprice(searched);
      return new Repriced(
          PlanText.withAttributes(searched),
          PlanText.withAttributes(result.plan()),
          result.swapped(),
          result.plan().getRowType().equals(searched.getRowType()));
    } catch (Exception e) {
      throw new AssertionError("planning failed for: " + sql, e);
    }
  }

  @Test
  void a_few_left_rows_seek_a_long_table_after_the_search() {
    Repriced plan =
        reprice(
            PROBES
                + "SELECT q.symbol, b.vwap FROM q ASOF JOIN bars b"
                + " MATCH_CONDITION q.t0 >= b.ts ON q.symbol = b.symbol");

    assertThat(plan.before()).contains("ChalkAsOfJoin").doesNotContain("ChalkAsOfLookupJoin");
    assertThat(plan.swapped()).isEqualTo(1);
    assertThat(plan.after())
        .contains("ChalkAsOfLookupJoin(table=[[main, bars]], index=[ix_bars_symbol_ts]")
        .doesNotContain("ChalkAsOfJoin");
    assertThat(plan.sameRowType()).isTrue();
  }

  /** Every one of 100 800 bars seeking the 5 000-row table still costs more than reading it. */
  @Test
  void many_left_rows_still_read_a_short_table() {
    Repriced plan =
        reprice(
            "SELECT b.symbol, s.vwap FROM bars b ASOF JOIN bars_small s"
                + " MATCH_CONDITION b.ts >= s.ts ON b.symbol = s.symbol");

    assertThat(plan.swapped()).isZero();
    assertThat(plan.after()).isEqualTo(plan.before());
  }

  /** A filter fixing a key column on the right is part of the seek, as it is in the search. */
  @Test
  void a_constant_key_seeks_too() {
    Repriced plan =
        reprice(
            "WITH q (t0) AS (VALUES (TIMESTAMP '2026-01-01 00:00:00'))"
                + " SELECT b.vwap FROM q LEFT ASOF JOIN bars b"
                + " MATCH_CONDITION q.t0 >= b.ts ON b.symbol = 'BTCUSDT'");

    assertThat(plan.swapped()).isEqualTo(1);
    assertThat(plan.after()).contains("ChalkAsOfLookupJoin").contains("joinType=[left_asof]");
  }

  /** From the leaves up: each join is priced over the inputs the plan will have, seeks included. */
  @Test
  void chained_joins_each_seek() {
    Repriced plan =
        reprice(
            PROBES
                + "SELECT q.symbol, c.vwap, o.vwap AS earlier FROM q"
                + " ASOF JOIN bars c MATCH_CONDITION c.ts <= q.t0 ON c.symbol = q.symbol"
                + " LEFT ASOF JOIN bars o MATCH_CONDITION o.ts < q.t0 ON o.symbol = q.symbol");

    assertThat(plan.swapped()).isEqualTo(2);
    assertThat(plan.after()).doesNotContain("ChalkAsOfJoin");
    assertThat(plan.sameRowType()).isTrue();
  }

  /** A seek claims no ordering, so a join that claims one, which a parent may rely on, stays. */
  @Test
  void a_join_that_claims_an_ordering_stays() {
    Repriced plan =
        reprice(
            PROBES
                + "SELECT q.symbol, b.vwap FROM q ASOF JOIN bars b"
                + " MATCH_CONDITION q.t0 >= b.ts ON q.symbol = b.symbol",
            AsOfSeeksTest::claimingAnOrdering);

    assertThat(plan.swapped()).isZero();
    assertThat(plan.after()).isEqualTo(plan.before());
  }

  @Test
  void the_pass_is_deterministic() {
    String sql =
        PROBES
            + "SELECT q.symbol, c.vwap, o.vwap AS earlier FROM q"
            + " ASOF JOIN bars c MATCH_CONDITION c.ts <= q.t0 ON c.symbol = q.symbol"
            + " LEFT ASOF JOIN bars o MATCH_CONDITION o.ts < q.t0 ON o.symbol = q.symbol";

    assertThat(reprice(sql).after()).isEqualTo(reprice(sql).after());
  }

  /** The pipeline names the pass only when it changed the plan; the search gets these right. */
  @Test
  void a_plan_the_pass_leaves_alone_does_not_name_it() throws Exception {
    RegisteredCatalog catalog = new CatalogRegistry().register(TestCatalogs.corpus());
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      PlannerPipeline.Result result =
          pipeline.plan(
              PROBES
                  + "SELECT q.symbol, b.vwap FROM q ASOF JOIN bars b"
                  + " MATCH_CONDITION q.t0 >= b.ts ON q.symbol = b.symbol",
              false);

      assertThat(PlanText.withAttributes(result.physical())).contains("ChalkAsOfLookupJoin");
      assertThat(result.stages())
          .noneMatch(stage -> stage.startsWith(PlannerPipeline.STAGE_ASOF_SEEKS));
    }
  }

  /** {@code rel} with each plain ASOF join made to claim an ordering on its first column. */
  private static RelNode claimingAnOrdering(RelNode rel) {
    List<RelNode> inputs = new ArrayList<>();
    for (RelNode input : rel.getInputs()) {
      inputs.add(claimingAnOrdering(input));
    }
    RelNode copied = rel.getInputs().isEmpty() ? rel : rel.copy(rel.getTraitSet(), inputs);
    if (copied instanceof ChalkAsOfJoin join) {
      return join.copy(join.getTraitSet().replace(RelCollations.of(0)), join.getInputs());
    }
    return copied;
  }
}
