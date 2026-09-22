package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * D276 §3 — the limit a set operation stands in the way of is copied into every branch.
 *
 * <p>The assertions read the <em>logical</em> plan, because that is where the copy is made and what
 * it looks like: one {@code LogicalSort} per branch carrying {@code offset + fetch} and no offset,
 * with the original left in place as the global bound. What each copy then becomes — a
 * {@code ChalkTopN}, a pushed {@code SourceSort}, a {@code ChalkLimit} over a goaled leaf — is the
 * cost model's business and is covered where those rules are.
 */
class LimitCopyRulesTest {
  private static RegisteredCatalog corpus;
  private static RegisteredCatalog partitioned;

  @BeforeAll
  static void registerCatalogs() {
    corpus = new CatalogRegistry().register(TestCatalogs.corpus());
    partitioned =
        new CatalogRegistry().register(TestCatalogs.withPartitions(TestCatalogs.duckDbProfile()));
  }

  private static String logical(RegisteredCatalog catalog, String sql) {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      return pipeline.plan(sql, true).logicalPlanText();
    } catch (Exception failure) {
      throw new AssertionError("planning failed for: " + sql, failure);
    }
  }

  private static long sorts(String plan) {
    return plan.lines().filter(line -> line.contains("LogicalSort(")).count();
  }

  private static long bounded(String plan, String bound) {
    return plan.lines().filter(line -> line.contains(bound)).count();
  }

  /** A pure {@code LIMIT}: every branch contributes its first n rows and the union takes n of them. */
  @Test
  void a_union_all_under_a_limit_bounds_every_branch() {
    String plan =
        logical(
            corpus, "SELECT symbol FROM bars_small UNION ALL SELECT symbol FROM symbols LIMIT 3");

    assertThat(plan).contains("LogicalUnion(all=[true])");
    // The global bound and one copy per branch, and no more: the rule matches its own output and
    // declines every branch of it, which is what makes the Hep pass finite.
    assertThat(sorts(plan)).isEqualTo(3);
    assertThat(bounded(plan, "LogicalSort(fetch=[3])")).isEqualTo(3);
  }

  /** With an {@code ORDER BY}, each copy carries the collation as well as the bound. */
  @Test
  void a_union_all_under_an_order_by_and_a_limit_copies_the_collation_too() {
    String plan =
        logical(
            corpus,
            "SELECT symbol, ts FROM bars_small WHERE symbol = 'BTCUSDT'"
                + " UNION ALL SELECT symbol, ts FROM bars_small WHERE symbol = 'ETHUSDT'"
                + " ORDER BY ts, symbol LIMIT 10");

    assertThat(sorts(plan)).isEqualTo(3);
    assertThat(
            bounded(
                plan,
                "LogicalSort(sort0=[$1], sort1=[$0], dir0=[ASC], dir1=[ASC], fetch=[10])"))
        .isEqualTo(3);
  }

  /**
   * An offset is folded into the copies' bound and is <b>not</b> copied: each branch must offer its
   * first {@code o + n} candidates, and the global sort applies the offset once to what they produce
   * between them. Skipping the first two of every branch would skip rows the answer needs.
   */
  @Test
  void an_offset_is_folded_into_the_bound_and_never_copied() {
    String plan =
        logical(
            corpus,
            "SELECT symbol, ts FROM bars_small WHERE symbol = 'BTCUSDT'"
                + " UNION ALL SELECT symbol, ts FROM bars_small WHERE symbol = 'ETHUSDT'"
                + " ORDER BY ts, symbol OFFSET 2 ROWS FETCH NEXT 4 ROWS ONLY");

    assertThat(plan)
        .contains("LogicalSort(sort0=[$1], sort1=[$0], dir0=[ASC], dir1=[ASC], offset=[2], fetch=[4])");
    assertThat(
            bounded(
                plan, "LogicalSort(sort0=[$1], sort1=[$0], dir0=[ASC], dir1=[ASC], fetch=[6])"))
        .isEqualTo(2);
    assertThat(sorts(plan)).isEqualTo(3);
  }

  /**
   * A distinct {@code UNION}, an {@code INTERSECT} and a {@code MINUS} reshape their inputs — a row
   * a branch would have dropped under its own bound may be the one that survives de-duplication — so
   * they are left alone.
   */
  @Test
  void a_distinct_set_operation_is_left_alone() {
    for (String operator : new String[] {"UNION", "INTERSECT", "EXCEPT"}) {
      String plan =
          logical(
              corpus,
              "SELECT symbol FROM bars_small " + operator + " SELECT symbol FROM symbols"
                  + " ORDER BY symbol LIMIT 3");

      assertThat(sorts(plan)).as("%s keeps one sort", operator).isEqualTo(1);
    }
  }

  /** {@code ChalkPartitionedScan} means {@code UNION ALL} and needs the rule of its own (D106). */
  @Test
  void a_partitioned_scan_bounds_every_partition() {
    String plan =
        logical(
            partitioned,
            "SELECT symbol, ts FROM federated.bars_by_symbol ORDER BY symbol, ts LIMIT 4");

    assertThat(plan).contains("ChalkPartitionedScan");
    // Five partitions and the global bound above them.
    assertThat(sorts(plan)).isEqualTo(6);
    assertThat(
            bounded(
                plan, "LogicalSort(sort0=[$0], sort1=[$1], dir0=[ASC], dir1=[ASC], fetch=[4])"))
        .isEqualTo(6);
  }

  /** And folds an offset the same way. */
  @Test
  void a_partitions_bound_folds_the_offset_in_as_well() {
    String plan =
        logical(
            partitioned,
            "SELECT symbol, ts FROM federated.bars_by_symbol ORDER BY symbol, ts"
                + " OFFSET 2 ROWS FETCH NEXT 4 ROWS ONLY");

    assertThat(plan)
        .contains("LogicalSort(sort0=[$0], sort1=[$1], dir0=[ASC], dir1=[ASC], offset=[2], fetch=[4])");
    assertThat(
            bounded(
                plan, "LogicalSort(sort0=[$0], sort1=[$1], dir0=[ASC], dir1=[ASC], fetch=[6])"))
        .isEqualTo(5);
    assertThat(sorts(plan)).isEqualTo(6);
  }

  /** A sort with no fetch bounds nothing, so there is nothing to copy. */
  @Test
  void an_order_by_without_a_fetch_copies_nothing() {
    String plan =
        logical(
            partitioned, "SELECT symbol, ts FROM federated.bars_by_symbol ORDER BY symbol, ts");

    assertThat(plan).contains("ChalkPartitionedScan");
    assertThat(sorts(plan)).isEqualTo(1);
  }

  // ------------------------------------------ a bound that is a parameter (D288, F122)

  /**
   * A parameterised fetch with no offset is copied the way a literal one is: the copy's bound is
   * {@code offset + fetch}, which with no offset is the parameter itself, so every branch carries
   * the same {@code RexNode} and renders the same number when the execution starts.
   */
  @Test
  void a_parameterised_fetch_is_copied_into_every_branch() {
    String plan =
        logical(
            corpus, "SELECT symbol FROM bars_small UNION ALL SELECT symbol FROM symbols LIMIT ?");

    assertThat(plan).contains("LogicalUnion(all=[true])");
    assertThat(sorts(plan)).isEqualTo(3);
    assertThat(bounded(plan, "LogicalSort(fetch=[?0])")).isEqualTo(3);
  }

  /** And into every partition, which is the same rewrite over the node D106 gave partitions. */
  @Test
  void a_parameterised_fetch_is_copied_into_every_partition() {
    String plan =
        logical(
            partitioned,
            "SELECT symbol, ts FROM federated.bars_by_symbol ORDER BY symbol, ts LIMIT ?");

    assertThat(plan).contains("ChalkPartitionedScan");
    assertThat(sorts(plan)).isEqualTo(6);
    assertThat(
            bounded(
                plan, "LogicalSort(sort0=[$0], sort1=[$1], dir0=[ASC], dir1=[ASC], fetch=[?0])"))
        .isEqualTo(6);
  }

  /**
   * With an offset beside it the copy is declined, at both spellings of {@code UNION ALL}. The
   * copy's bound would be {@code offset + fetch} — an expression, which is neither a literal the
   * planner can read nor a parameter the executor can render — and before this run Calcite's own
   * "deterministic fetch" guard let one through and the plan failed several nodes later (F122).
   * The statement still plans: the bound stays where the statement put it.
   */
  @Test
  void a_parameterised_fetch_with_an_offset_is_not_copied() {
    String union =
        logical(
            corpus,
            "SELECT symbol FROM bars_small UNION ALL SELECT symbol FROM symbols"
                + " ORDER BY symbol OFFSET 2 ROWS FETCH NEXT ? ROWS ONLY");

    assertThat(union).contains("LogicalUnion(all=[true])");
    assertThat(sorts(union)).isEqualTo(1);
    assertThat(union).contains("offset=[2]").contains("fetch=[?0]");

    String scan =
        logical(
            partitioned,
            "SELECT symbol, ts FROM federated.bars_by_symbol ORDER BY symbol, ts"
                + " OFFSET 2 ROWS FETCH NEXT ? ROWS ONLY");

    assertThat(scan).contains("ChalkPartitionedScan");
    assertThat(sorts(scan)).isEqualTo(1);
  }

  /** A parameterised offset over a literal fetch is the same shape and is declined too. */
  @Test
  void a_parameterised_offset_is_not_copied_either() {
    String plan =
        logical(
            corpus,
            "SELECT symbol FROM bars_small UNION ALL SELECT symbol FROM symbols"
                + " ORDER BY symbol OFFSET ? ROWS FETCH NEXT 4 ROWS ONLY");

    assertThat(sorts(plan)).isEqualTo(1);
    assertThat(plan).contains("offset=[?0]").contains("fetch=[4]");
  }

  /**
   * A branch that already delivers the collation within the bound is left as it is rather than
   * wrapped a second time — the guard that lets the rule match its own output and stop.
   */
  @Test
  void a_branch_that_is_already_bounded_is_not_wrapped_twice() {
    String plan =
        logical(
            corpus,
            "SELECT symbol, ts FROM"
                + " (SELECT symbol, ts FROM bars_small ORDER BY ts, symbol LIMIT 10)"
                + " UNION ALL SELECT symbol, ts FROM bars_small WHERE symbol = 'ETHUSDT'"
                + " ORDER BY ts, symbol LIMIT 10");

    // Three sorts, not four: the global one, the sub-query's own, and one copy for the branch that
    // had none.
    assertThat(sorts(plan)).isEqualTo(3);
  }
}
