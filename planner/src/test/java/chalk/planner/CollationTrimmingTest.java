package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.diag.PlanText;
import chalk.planner.plan.ChalkFieldTrimmer;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import com.google.common.collect.ImmutableList;
import com.google.common.collect.ImmutableSet;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.core.RelFactories;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.logical.LogicalJoin;
import org.apache.calcite.rex.RexCorrelVariable;
import org.apache.calcite.sql2rel.RelFieldTrimmer;
import org.apache.calcite.tools.RelBuilder;
import org.apache.calcite.util.Holder;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * What {@link chalk.planner.plan.ChalkFieldTrimmer} changes about Calcite's trimming (F114).
 *
 * <p>Calcite's {@code RelFieldTrimmer} never discards a column that defines an input's collation.
 * For a catalog whose tables declare one that is the ordering key of every table in every scan,
 * however little of it the query reads. Chalk's trimmer drops the rule; what is asserted here is
 * that the column goes when nothing reads it and stays when something does — which is the whole of
 * why Calcite kept it.
 */
class CollationTrimmingTest {
  private static RegisteredCatalog corpus;

  @BeforeAll
  static void registerCatalog() {
    corpus = new CatalogRegistry().register(TestCatalogs.corpus());
  }

  private static String plan(String sql) {
    try (PlannerPipeline pipeline = PlannerPipeline.create(corpus, PushdownPolicy.full())) {
      return PlanText.withAttributes(pipeline.plan(sql, false).physical());
    } catch (RuntimeException failure) {
      throw failure;
    } catch (Exception failure) {
      throw new AssertionError("planning failed for: " + sql, failure);
    }
  }

  /** {@code bars} declares {@code (ts, symbol)}; this query names neither {@code ts} nor an order. */
  @Test
  void a_filter_does_not_read_an_ordering_column_the_query_never_names() {
    String plan = plan("SELECT symbol, volume FROM bars WHERE volume < 0");

    assertThat(plan).contains("ChalkTableScan(table=[[main, bars]], projection=[[0, 6]])");
  }

  /** The same query without the filter already pruned, and still does. */
  @Test
  void a_plain_projection_is_unchanged() {
    String plan = plan("SELECT symbol, volume FROM bars");

    assertThat(plan).contains("ChalkTableScan(table=[[main, bars]], projection=[[0, 6]])");
  }

  /**
   * An ordering column the query orders by is read, and the declared collation still removes the
   * sort — which is the claim corpus query 42 makes and the reason the columns were kept at all.
   */
  @Test
  void an_ordering_column_the_query_orders_by_is_still_read() {
    String plan = plan("SELECT symbol FROM bars ORDER BY ts");

    assertThat(plan)
        .contains("ChalkTableScan(table=[[main, bars]], projection=[[0, 1]])")
        .doesNotContain("ChalkSort");
  }

  /** And an index is still matched against a narrower scan than the ordering key spans. */
  @Test
  void an_index_still_answers_a_predicate_on_a_narrowed_scan() {
    String plan = plan("SELECT COUNT(*) FROM bars WHERE ts >= TIMESTAMP '2026-01-14 00:00:00'");

    assertThat(plan).contains("ChalkIndexLookup(table=[[main, bars]]");
  }

  /**
   * A join trims each of its sides through a {@code trimChild} overload of its own, which repeats
   * the collation union rather than delegating to the single-input one — so the override has to
   * cover both. It does; what these two pin is the end state, that a scan under a join reads what
   * the query names and no more.
   *
   * <p>Neither moves today if only the single-input overload is overridden, and the reason is worth
   * writing down: {@code PROJECT_JOIN_TRANSPOSE} runs in the Hep pre-pass, so by the time the
   * trimmer reaches a join both sides are already a {@code Project}, and a projection that has
   * dropped the ordering column reports no collation for the union to find. The join overload's
   * union is therefore a hole rather than a leak — one a side that is a bare scan would fall
   * through, which is a shape this rule list does not produce and another one might.
   */
  @Test
  void a_scan_under_a_join_does_not_read_an_ordering_column_either() {
    String plan =
        plan(
            "SELECT b.symbol, s.base FROM bars b JOIN symbols s ON b.symbol = s.symbol"
                + " WHERE b.volume > 5000");

    assertThat(plan).contains("ChalkTableScan(table=[[main, bars]], projection=[[0, 6]])");
  }

  /** The same with the predicate under the join rather than in it, which is a longer chain. */
  @Test
  void a_scan_under_a_join_over_a_filter_does_not_read_one_either() {
    String plan =
        plan(
            "SELECT b.symbol, s.base FROM (SELECT symbol, volume FROM bars WHERE volume > 5000) b"
                + " JOIN symbols s ON b.symbol = s.symbol");

    assertThat(plan).contains("ChalkTableScan(table=[[main, bars]], projection=[[0, 6]])");
  }

  /**
   * The one thing the collation union was mixed in with: a correlated reference out of a join's
   * side is a reference into that side's row that no {@code RexInputRef} above it names. The
   * override reproduces Calcite's arithmetic for it — the window {@code [startIndex, endIndex)}
   * that says which side a reference belongs to, and the shift into that side's own numbering —
   * and this pins the two together.
   *
   * <p>Built by hand because the pipeline decorrelates before it trims, so no statement reaches the
   * trimmer with a correlation left in it: this is the one part of the override that has no
   * statement to reach it, which is exactly why it is compared with the original rather than with
   * an expectation somebody wrote down.
   */
  @Test
  void a_correlated_reference_out_of_a_joins_side_is_still_kept() throws Exception {
    RelBuilder builder = builder();
    Holder<RexCorrelVariable> variable = Holder.empty();

    // symbols is (symbol, base, quote, tick_size, tags), so `quote` is its field 2 and the join's
    // field 2 as well; bars occupies the join's fields 5 upward.
    builder.scan("main", "symbols").variable(variable::set);
    RelNode left = builder.build();
    RelNode right =
        builder
            .scan("main", "bars")
            .filter(
                builder.equals(builder.field("symbol"), builder.field(variable.get(), "quote")))
            .build();
    RelNode join =
        LogicalJoin.create(
            left,
            right,
            ImmutableList.of(),
            builder.literal(true),
            ImmutableSet.of(variable.get().id),
            JoinRelType.INNER);

    RelNode rel = builder.push(join).project(builder.field(0)).build();

    // Calcite's own trimmer is the oracle: the override changes what the collation union did and
    // nothing about the correlation, so the two must say exactly the same thing about this tree —
    // whether that is a trimmed tree or a refusal to trim one.
    assertThat(outcome(new ChalkFieldTrimmer(builder), rel))
        .isEqualTo(outcome(new RelFieldTrimmer(null, builder), rel));
  }

  /** What a trimmer makes of a tree: the tree it returns, or the failure it raises.  */
  private static String outcome(RelFieldTrimmer trimmer, RelNode rel) {
    try {
      return RelOptUtil.toString(trimmer.trim(rel));
    } catch (RuntimeException | AssertionError failure) {
      return failure.getClass().getName() + ": " + failure.getMessage();
    }
  }

  /** A {@code RelBuilder} over the corpus catalog, for the case no statement can reach. */
  private static RelBuilder builder() throws Exception {
    try (PlannerPipeline pipeline = PlannerPipeline.create(corpus, PushdownPolicy.full())) {
      RelNode logical = pipeline.logical("SELECT symbol FROM symbols");
      TableScan scan = firstScan(logical);
      return RelFactories.LOGICAL_BUILDER.create(
          logical.getCluster(), scan.getTable().getRelOptSchema());
    }
  }

  private static TableScan firstScan(RelNode rel) {
    if (rel instanceof TableScan scan) {
      return scan;
    }

    for (RelNode input : rel.getInputs()) {
      TableScan found = firstScan(input);
      if (found != null) {
        return found;
      }
    }

    return null;
  }
}
