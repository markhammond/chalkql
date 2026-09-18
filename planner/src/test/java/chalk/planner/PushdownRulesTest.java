package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.DialectProfile;
import chalk.ir.v1.PredicateShape;
import chalk.ir.v1.SourceCapabilities;
import chalk.ir.v1.StringCollation;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.DisabledCapability;
import chalk.planner.rpc.v1.PushdownLevel;
import org.junit.jupiter.api.Test;

/**
 * Rule by rule over descriptor variants (§6): each capability flag flips exactly the rules it
 * should, and every semantics-drift rule of D89 refuses what it must whatever the descriptor claims.
 *
 * <p>No database is involved. The catalog carries a REMOTE schema with a declared descriptor, and
 * what is asserted is the physical plan the rules produce — which is where the reason is legible.
 */
class PushdownRulesTest {

  private static String plan(SourceCapabilities capabilities, DialectProfile profile, String sql) {
    return plan(capabilities, profile, sql, PushdownPolicy.full());
  }

  private static String plan(
      SourceCapabilities capabilities, DialectProfile profile, String sql, PushdownPolicy policy) {
    RegisteredCatalog catalog =
        new CatalogRegistry().register(TestCatalogs.withRemote("db", capabilities, profile));
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, policy)) {
      return pipeline.plan(sql, true).physicalPlanText();
    } catch (Exception failure) {
      throw new AssertionError("planning failed for: " + sql, failure);
    }
  }

  private static SourceCapabilities full() {
    return TestCatalogs.fullSqlCapabilities().build();
  }

  private static DialectProfile duck() {
    return TestCatalogs.duckDbProfile();
  }

  /** The full descriptor minus the two LIKE shapes, which no non-binary collation may declare. */
  private static SourceCapabilities withoutLikeShapes() {
    SourceCapabilities.Builder builder = TestCatalogs.fullSqlCapabilities();
    java.util.List<PredicateShape> kept = new java.util.ArrayList<>(builder.getPushablePredicatesList());
    kept.remove(PredicateShape.PREDICATE_SHAPE_LIKE);
    kept.remove(PredicateShape.PREDICATE_SHAPE_LIKE_PREFIX);
    return builder.clearPushablePredicates().addAllPushablePredicates(kept).build();
  }

  // ------------------------------------------------------------------ the basics

  @Test
  public void a_full_descriptor_pushes_the_whole_filter_and_projection() {
    String plan =
        plan(
            full(),
            duck(),
            "SELECT l_orderkey, l_quantity FROM db.lineitem"
                + " WHERE l_shipdate >= DATE '1995-01-01' AND l_discount BETWEEN 0.05 AND 0.07");

    assertThat(plan).contains("SourceToLocalConverter");
    assertThat(plan).contains("SourceFilter");
    assertThat(plan).doesNotContain("ChalkFilter");
  }

  @Test
  public void a_source_that_declares_nothing_is_only_scanned() {
    String plan =
        plan(
            SourceCapabilities.getDefaultInstance(),
            DialectProfile.newBuilder().setDialect("ansi").build(),
            "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > 10");

    assertThat(plan).doesNotContain("SourceToLocalConverter");
    assertThat(plan).contains("ChalkTableScan");
  }

  @Test
  public void a_predicate_shape_that_is_not_declared_stays_local() {
    SourceCapabilities noRanges =
        TestCatalogs.fullSqlCapabilities()
            .clearPushablePredicates()
            .addPushablePredicates(PredicateShape.PREDICATE_SHAPE_EQ)
            .addPushablePredicates(PredicateShape.PREDICATE_SHAPE_AND)
            .setMaxInList(0)
            .build();

    assertThat(plan(noRanges, duck(), "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > 10"))
        .contains("ChalkFilter");
    assertThat(plan(noRanges, duck(), "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey = 10"))
        .doesNotContain("ChalkFilter");
  }

  // ------------------------------------------------------------------ the conditional (D273, F104)

  /**
   * A {@code CASE} is pushed by a descriptor that says nothing about one, which is the whole of
   * D273: {@code supports_case} carries explicit presence and absent means true, so a catalog
   * written before the field existed pushes a conditional from now on. {@link
   * TestCatalogs#fullSqlCapabilities} sets no such field, so this descriptor is that catalog.
   */
  @Test
  public void a_conditional_is_pushed_by_a_descriptor_that_says_nothing_about_one() {
    SourceCapabilities silent = full();
    assertThat(silent.hasSupportsCase()).isFalse();

    String plan =
        plan(
            silent,
            duck(),
            "SELECT CASE WHEN l_orderkey > 10 THEN 1 ELSE 0 END FROM db.lineitem");

    assertThat(plan).contains("SourceToLocalConverter");
    assertThat(plan).doesNotContain("ChalkProject");
  }

  /** And a source that cannot spell one says so, after which the projection is the client's. */
  @Test
  public void a_source_that_cannot_take_a_conditional_keeps_it_local() {
    SourceCapabilities noCase = TestCatalogs.fullSqlCapabilities().setSupportsCase(false).build();

    String plan =
        plan(
            noCase,
            duck(),
            "SELECT CASE WHEN l_orderkey > 10 THEN 1 ELSE 0 END FROM db.lineitem");

    assertThat(plan).contains("ChalkProject");
  }

  /**
   * The flag admits the shape and nothing else. {@code CONCAT} is not in this descriptor's
   * {@code pushable_functions}, so the arm that calls it is not pushable, so the conditional is not
   * — which is the rule every other expression is already judged by, reached through a new node
   * kind.
   */
  @Test
  public void a_conditional_over_an_undeclared_function_stays_local() {
    String plan =
        plan(
            full(),
            duck(),
            "SELECT CASE WHEN l_orderkey > 10 THEN l_comment || 'x' ELSE l_comment END"
                + " FROM db.lineitem");

    assertThat(plan).contains("ChalkProject");
  }

  /**
   * And a conditional in a predicate, which is the half that fails silently: a projection the source
   * computed differently is a wrong column, while a predicate it evaluated differently is a
   * plausible set of rows.
   */
  @Test
  public void a_conditional_inside_a_predicate_is_pushed_with_it() {
    String plan =
        plan(
            full(),
            duck(),
            "SELECT l_orderkey FROM db.lineitem"
                + " WHERE CASE WHEN l_linenumber > 1 THEN l_orderkey ELSE 0 END > 10");

    assertThat(plan).contains("SourceFilter");
    assertThat(plan).doesNotContain("ChalkFilter");
  }

  @Test
  public void a_conjunction_needs_the_and_shape() {
    SourceCapabilities noAnd =
        TestCatalogs.fullSqlCapabilities()
            .clearPushablePredicates()
            .addPushablePredicates(PredicateShape.PREDICATE_SHAPE_EQ)
            .addPushablePredicates(PredicateShape.PREDICATE_SHAPE_RANGE)
            .setMaxInList(0)
            .build();

    assertThat(
            plan(
                noAnd,
                duck(),
                "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey = 10 AND l_linenumber > 1"))
        .contains("ChalkFilter");
  }

  // ------------------------------------------------------------------ D89, the drift rules

  /** A string comparison under a case-insensitive collation matches different rows. */
  @Test
  public void a_string_predicate_is_not_pushed_under_a_case_insensitive_collation() {
    DialectProfile insensitive =
        duck().toBuilder()
            .setStringCollation(StringCollation.STRING_COLLATION_CASE_INSENSITIVE)
            .build();

    assertThat(
            plan(
                withoutLikeShapes(),
                insensitive,
                "SELECT c_name FROM db.customer WHERE c_mktsegment = 'BUILDING'"))
        .contains("ChalkFilter");
  }

  /** The same predicate on a numeric column is unaffected by the collation. */
  @Test
  public void a_numeric_predicate_is_pushed_under_any_collation() {
    DialectProfile insensitive =
        duck().toBuilder()
            .setStringCollation(StringCollation.STRING_COLLATION_CASE_INSENSITIVE)
            .build();

    assertThat(
            plan(withoutLikeShapes(), insensitive, "SELECT c_name FROM db.customer WHERE c_custkey = 42"))
        .doesNotContain("ChalkFilter");
  }

  /** A sort whose null placement the source will not honour stays local. */
  @Test
  public void a_sort_is_not_pushed_when_the_null_placement_is_not_honoured() {
    // SQLite: no NULLS FIRST/LAST clause, and NULLs first ascending. `ORDER BY x` under D15 wants
    // NULLS LAST, which SQLite would not give.
    String plan =
        plan(
            full(),
            TestCatalogs.sqliteProfile(),
            "SELECT l_orderkey, l_comment FROM db.lineitem ORDER BY l_comment");

    assertThat(plan).contains("ChalkSort");
  }

  @Test
  public void the_same_sort_is_pushed_where_the_clause_exists() {
    String plan =
        plan(full(), duck(), "SELECT l_orderkey, l_comment FROM db.lineitem ORDER BY l_comment");
    assertThat(plan).doesNotContain("ChalkSort");
    assertThat(plan).contains("SourceSort");
  }

  /** A DECIMAL wider than the source's own precision would be compared after truncation. */
  @Test
  public void a_comparison_the_source_would_truncate_stays_local() {
    // SQLite's decimals are doubles: 15 digits. `l_extendedprice` is DECIMAL(15,2), which fits, but
    // a comparison against a wider literal does not.
    String plan =
        plan(
            full(),
            TestCatalogs.sqliteProfile(),
            "SELECT l_orderkey FROM db.lineitem"
                + " WHERE l_extendedprice > CAST(1 AS DECIMAL(30, 10))");

    assertThat(plan).contains("ChalkFilter");
  }

  @Test
  public void a_session_function_is_never_pushed() {
    String plan =
        plan(
            full(),
            duck(),
            "SELECT l_orderkey FROM db.lineitem WHERE l_shipdate > CURRENT_DATE");

    assertThat(plan).contains("ChalkFilter");
  }

  // ------------------------------------------------- disabled_capabilities (D87)

  @Test
  public void a_disabled_capability_is_planned_as_if_no_source_declared_it() {
    // The request's switch and the descriptor's claim reach the same gate, so the plan with FILTER
    // disabled is the plan a source that never claimed a predicate would have got.
    String sql = "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > 10";

    assertThat(plan(full(), duck(), sql)).doesNotContain("ChalkFilter");
    assertThat(plan(full(), duck(), sql, disabling(DisabledCapability.DISABLED_CAPABILITY_FILTER)))
        .contains("ChalkFilter");
  }

  @Test
  public void each_switch_turns_off_exactly_its_own_operator() {
    String aggregate = "SELECT l_returnflag, SUM(l_quantity) FROM db.lineitem GROUP BY l_returnflag";
    assertThat(plan(full(), duck(), aggregate)).contains("SourceAggregate");
    assertThat(
            plan(
                full(),
                duck(),
                aggregate,
                disabling(DisabledCapability.DISABLED_CAPABILITY_AGGREGATE)))
        .doesNotContain("SourceAggregate");

    // Turning the aggregate off leaves the filter alone.
    String both = "SELECT l_returnflag, SUM(l_quantity) FROM db.lineitem"
        + " WHERE l_orderkey > 10 GROUP BY l_returnflag";
    String plan =
        plan(full(), duck(), both, disabling(DisabledCapability.DISABLED_CAPABILITY_AGGREGATE));
    assertThat(plan).doesNotContain("SourceAggregate");
    assertThat(plan).doesNotContain("ChalkFilter");
    assertThat(plan).contains("SourceFilter");
  }

  @Test
  public void every_capability_disabled_leaves_a_bare_read_inside_the_boundary() {
    // Not the same as PUSHDOWN_LEVEL_NONE: the level decides whether a query is used at all, the
    // switches decide what may be in one. A source that only takes queries still gets a query --
    // an unfiltered, unaggregated read of the table.
    String plan =
        plan(
            full(),
            duck(),
            "SELECT l_returnflag, SUM(l_quantity) FROM db.lineitem"
                + " WHERE l_orderkey > 10 GROUP BY l_returnflag",
            disabling(
                DisabledCapability.DISABLED_CAPABILITY_FILTER,
                DisabledCapability.DISABLED_CAPABILITY_PROJECT,
                DisabledCapability.DISABLED_CAPABILITY_AGGREGATE,
                DisabledCapability.DISABLED_CAPABILITY_SORT,
                DisabledCapability.DISABLED_CAPABILITY_LIMIT,
                DisabledCapability.DISABLED_CAPABILITY_JOIN,
                DisabledCapability.DISABLED_CAPABILITY_DISTINCT,
                DisabledCapability.DISABLED_CAPABILITY_IN_LIST,
                DisabledCapability.DISABLED_CAPABILITY_PARAMETERS));

    assertThat(plan).contains("SourceScan");
    assertThat(plan).doesNotContain("SourceFilter");
    assertThat(plan).doesNotContain("SourceAggregate");
    assertThat(plan).contains("ChalkFilter");
    assertThat(plan).contains("ChalkHashAggregate");
  }

  private static PushdownPolicy disabling(DisabledCapability... capabilities) {
    return new PushdownPolicy(
        PushdownLevel.PUSHDOWN_LEVEL_FULL, false, java.util.List.of(capabilities));
  }

  // ------------------------------------------------------- declared capabilities

  @Test
  public void a_profile_without_group_by_gets_the_scan_and_aggregates_locally() {
    // Corpus 04. The aggregate stays here and the source is read with a projection, which is the
    // whole of what a level would have given us — reached instead through what the source declared.
    // HAVING has to go with it: the validator refuses a descriptor that keeps one without the
    // other, because a HAVING with no GROUP BY has nothing to filter.
    SourceCapabilities noGroupBy =
        TestCatalogs.fullSqlCapabilities().setSupportsGroupBy(false).setSupportsHaving(false).build();
    String sql = "SELECT l_returnflag, SUM(l_quantity) FROM db.lineitem GROUP BY l_returnflag";

    String plan = plan(noGroupBy, duck(), sql);
    assertThat(plan).contains("ChalkHashAggregate");
    assertThat(plan).contains("SourceScan");
    assertThat(plan).doesNotContain("SourceAggregate");

    assertThat(plan(full(), duck(), sql)).contains("SourceAggregate");
  }

  @Test
  public void an_approximate_distinct_count_is_never_pushed_without_permission() {
    // Corpus 09. APPROX_COUNT_DISTINCT is the one aggregate whose answer is allowed to be wrong,
    // so pushing it into a source that did not say it may answer approximately would turn a
    // deliberate choice by the host into an accident.
    String sql = "SELECT APPROX_COUNT_DISTINCT(l_suppkey) FROM db.lineitem";

    assertThat(plan(full(), duck().toBuilder().setApproximateDistinctCount(false).build(), sql))
        .doesNotContain("SourceAggregate");
    assertThat(plan(full(), duck().toBuilder().setApproximateDistinctCount(true).build(), sql))
        .contains("SourceAggregate");

    // The exact count is pushed either way: it is not an approximation.
    assertThat(
            plan(
                full(),
                duck().toBuilder().setApproximateDistinctCount(false).build(),
                "SELECT COUNT(DISTINCT l_suppkey) FROM db.lineitem"))
        .contains("SourceAggregate");
  }

  // ------------------------------------------------------------------ ceilings

  @Test
  public void max_pushdown_rows_stops_the_aggregate_and_keeps_the_filter() {
    // A ceiling of 10 is below every subtree's estimate, so nothing above the scan is pushed — but
    // the scan itself still is, because a bare scan is the only way to read the table at all.
    SourceCapabilities tiny = TestCatalogs.fullSqlCapabilities().setMaxPushdownRows(10).build();
    String plan =
        plan(
            tiny,
            duck(),
            "SELECT l_returnflag, SUM(l_quantity) FROM db.lineitem GROUP BY l_returnflag");

    assertThat(plan).contains("ChalkHashAggregate");
    assertThat(plan).contains("SourceScan");
    assertThat(plan).doesNotContain("SourceAggregate");

    // With no ceiling the same query pushes the aggregate.
    assertThat(
            plan(
                full(),
                duck(),
                "SELECT l_returnflag, SUM(l_quantity) FROM db.lineitem GROUP BY l_returnflag"))
        .contains("SourceAggregate");
  }

  @Test
  public void an_in_list_longer_than_the_ceiling_stays_local() {
    StringBuilder list = new StringBuilder();
    for (int i = 0; i < 120; i++) {
      list.append(i == 0 ? "" : ", ").append(i);
    }
    SourceCapabilities small = TestCatalogs.fullSqlCapabilities().setMaxInList(100).build();
    SourceCapabilities large = TestCatalogs.fullSqlCapabilities().setMaxInList(200).build();
    String sql = "SELECT l_orderkey FROM db.lineitem WHERE l_partkey IN (" + list + ")";

    assertThat(plan(small, duck(), sql)).contains("ChalkFilter");
    assertThat(plan(large, duck(), sql)).doesNotContain("ChalkFilter");
  }

  // ------------------------------------------------------------------ levels

  @Test
  public void the_levels_push_exactly_what_the_design_says() {
    String sql =
        "SELECT l_returnflag, SUM(l_quantity) FROM db.lineitem"
            + " WHERE l_orderkey > 10 GROUP BY l_returnflag";

    String full = plan(full(), duck(), sql, PushdownPolicy.full());
    assertThat(full).contains("SourceAggregate");

    String filters =
        plan(
            full(),
            duck(),
            sql,
            new PushdownPolicy(PushdownLevel.PUSHDOWN_LEVEL_FILTERS_ONLY, false));
    assertThat(filters).contains("SourceFilter");
    assertThat(filters).doesNotContain("SourceAggregate");
    assertThat(filters).contains("ChalkHashAggregate");

    String projection =
        plan(
            full(),
            duck(),
            sql,
            new PushdownPolicy(PushdownLevel.PUSHDOWN_LEVEL_PROJECTION_ONLY, false));
    assertThat(projection).doesNotContain("SourceFilter");
    assertThat(projection).contains("ChalkFilter");

    String none = plan(full(), duck(), sql, PushdownPolicy.none());
    assertThat(none).doesNotContain("SourceToLocalConverter");
    assertThat(none).contains("ChalkTableScan");
  }

  // ------------------------------------------------------------------ joins

  @Test
  public void a_same_source_join_is_pushed_as_one_query() {
    String plan =
        plan(
            full(),
            duck(),
            "SELECT o.o_orderkey, c.c_name FROM db.orders o"
                + " JOIN db.customer c ON o.o_custkey = c.c_custkey"
                + " WHERE c.c_mktsegment = 'BUILDING'");

    assertThat(plan).contains("SourceJoin");
    assertThat(plan).doesNotContain("ChalkHashJoin");
  }

  /**
   * M4 refused to push a join whose sides are in different sources and left it a local hash join
   * over two full fetches; M5 gives the same shape a strategy (D103). Neither source can hold the
   * join — that part has not changed, and {@code SourceJoin} is still absent — but the smaller side
   * now drives a lookup into the larger one instead of both being fetched whole.
   */
  @Test
  public void a_cross_source_join_becomes_a_lookup_rather_than_two_full_fetches() {
    String plan =
        plan(
            full(),
            duck(),
            "SELECT o.o_orderkey, c.c_name FROM db.orders o"
                + " JOIN main.customer c ON o.o_custkey = c.c_custkey");

    assertThat(plan).doesNotContain("SourceJoin");
    assertThat(plan).contains("ChalkLookupJoin");
    assertThat(plan).contains("CHALK_KEY_SET_IN");
  }

  // ------------------------------------------------- the filter residual (§3.7, D199)

  /**
   * One conjunct the source cannot take no longer holds back the ones it can. Before this rule the
   * whole {@code WHERE} stayed local and the source was handed the table.
   */
  @Test
  public void a_mixed_filter_pushes_the_conjuncts_the_source_takes() {
    String plan =
        plan(
            full(),
            duck(),
            "SELECT l_orderkey FROM db.lineitem"
                + " WHERE l_orderkey > 10 AND l_shipdate > CURRENT_DATE");

    assertThat(plan).contains("SourceFilter");
    assertThat(plan).contains("ChalkFilter");
  }

  /** The split is by conjunct: the pushed half is the range, the residual is the session function. */
  @Test
  public void the_residual_is_what_the_source_refused_and_nothing_else() {
    String plan =
        plan(
            full(),
            duck(),
            "SELECT l_orderkey FROM db.lineitem"
                + " WHERE l_orderkey > 10 AND l_shipdate > CURRENT_DATE");

    String pushed = lineContaining(plan, "SourceFilter");
    String local = lineContaining(plan, "ChalkFilter");
    assertThat(pushed).contains("$0").doesNotContain("CURRENT_DATE");
    assertThat(local).contains("CURRENT_DATE");
  }

  /** Nothing pushable, nothing pushed: the filter stays whole and local, as it always did. */
  @Test
  public void a_filter_with_no_pushable_conjunct_is_not_split() {
    String plan =
        plan(
            full(),
            duck(),
            "SELECT l_orderkey FROM db.lineitem"
                + " WHERE l_shipdate > CURRENT_DATE AND l_receiptdate > CURRENT_DATE");

    assertThat(plan).contains("ChalkFilter");
    assertThat(plan).doesNotContain("SourceFilter");
  }

  /**
   * The row ceiling judges the <em>pushed</em> part. A source that will not answer a query over
   * more rows than the ceiling refuses the split too, and the whole filter stays local.
   */
  @Test
  public void the_row_ceiling_applies_to_the_pushed_part_of_a_split() {
    SourceCapabilities tinyCeiling =
        TestCatalogs.fullSqlCapabilities().setMaxPushdownRows(1).build();

    String plan =
        plan(
            tinyCeiling,
            duck(),
            "SELECT l_orderkey FROM db.lineitem"
                + " WHERE l_orderkey > 10 AND l_shipdate > CURRENT_DATE");

    assertThat(plan).doesNotContain("SourceFilter");
    assertThat(plan).contains("ChalkFilter");
  }

  private static String lineContaining(String plan, String needle) {
    for (String line : plan.split("\n")) {
      if (line.contains(needle)) {
        return line;
      }
    }
    throw new AssertionError("no line containing " + needle + " in:\n" + plan);
  }
}
