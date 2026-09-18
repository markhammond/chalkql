package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.stream.Stream;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.JoinRelType;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.Arguments;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * D67 — <b>decorrelate first, always</b> ({@code 14-windows-ii.md} §8, §10). Every lateral query of
 * §9 plans without a {@code Correlate}, and the one shape Calcite 1.42 cannot decorrelate (V26) is
 * reported as {@code UNSUPPORTED} naming itself rather than planned as a per-row re-execution.
 *
 * <p>This extends step 17's {@link CorrelateFreeTest}, which asserts the same thing over the whole
 * corpus: the IR has no correlated node, so a surviving {@code Correlate} would leave {@code RelToIr}
 * throwing. What this adds is the lateral queries by name, and the message of the residual case.
 */
class LateralDecorrelationTest {
  private static final CorpusPlanner PLANNER = new CorpusPlanner();

  /** The corpus queries that write a lateral join, in either spelling. */
  static Stream<CorpusQueries.Query> queries() {
    return CorpusQueries.m5().stream()
        .filter(
            q -> {
              String sql = q.sql().toUpperCase(Locale.ROOT);
              return sql.contains("LATERAL") || sql.contains("APPLY");
            });
  }

  @ParameterizedTest(name = "{0}")
  @MethodSource("queries")
  void every_lateral_query_plans_without_a_correlate(CorpusQueries.Query query) {
    // The physical plan is where a surviving correlate would still be visible; the IR has no
    // correlated node at all, so reaching it is the second half of the same assertion.
    assertThat(PLANNER.planText(query)).as("plan of %s", query.name()).doesNotContain("Correlate");

    Plan plan = PLANNER.plan(query);
    assertThat(plan.getRoot().getKindCase()).isNotEqualTo(Rel.KindCase.KIND_NOT_SET);
  }

  /** The queries §9 names are all present, so the sweep above is not vacuous. */
  @Test
  void the_corpus_covers_every_lateral_shape() {
    List<String> names = queries().map(CorpusQueries.Query::name).toList();

    assertThat(names)
        .contains(
            "15_left_lateral_unnest_ordinality",
            "18_lateral_top_n",
            "19_lateral_aggregate",
            "20_left_lateral_limit",
            "21_outer_apply_lenient",
            "22_lateral_projection");
  }

  // ---- one outer column tied to two of the sub-query's own tables (ADR 0026) ----

  /**
   * The trial's statement (docs/design/calcite-143-trial.md §6): {@code c.c_custkey} constrains both
   * {@code o1} and {@code o2}, and {@code o_orderstatus} is deliberately not unique, so no
   * functional dependency can stand in for a lost equality.
   */
  private static final String SHARED_OUTER_FIELD =
      """
      SELECT c.c_name, x.n FROM customer c LEFT JOIN LATERAL (
        SELECT COUNT(*) AS n FROM orders o1 JOIN orders o2
          ON o1.o_orderstatus = o2.o_orderstatus
        WHERE o1.o_custkey = c.c_custkey AND o2.o_custkey = c.c_custkey) x ON TRUE
      """;

  /**
   * <b>The defect itself</b>, pinned where it lives: the general (top-down) decorrelator's
   * rewrite. {@code PlannerPipeline.logical} converts and decorrelates and does nothing else — the
   * shape check below is part of {@code plan}, not of it — so this is the only place the wrong tree
   * is still reachable, which is why the check was left out of that entry point.
   *
   * <p>The inner join must carry <em>two</em> equalities: the status one the statement wrote, and
   * the one between the two copies of {@code o_custkey} that decorrelation owes it. It carries one.
   * {@code o1.o_custkey} is neither projected nor constrained, so the count includes pairs whose
   * first row belongs to a different customer.
   *
   * <p><b>When this test fails, Calcite has fixed it</b> — that is the signal to delete the refusal
   * and the F38 row with it. Identical on 1.42 and on the pinned 1.43 snapshot.
   */
  @Test
  void the_general_decorrelator_keeps_only_one_of_the_two_equalities() {
    RelNode logical = PLANNER.logical(SHARED_OUTER_FIELD);

    List<Join> inner =
        joins(logical).stream().filter(join -> join.getJoinType() == JoinRelType.INNER).toList();
    assertThat(inner).as("the join the sub-query wrote:%n%s", RelOptUtil.toString(logical)).hasSize(1);

    assertThat(RelOptUtil.conjunctions(inner.get(0).getCondition()))
        .as(
            "the defect: decorrelation owes this join the equality between the two copies of "
                + "o_custkey and does not add it:%n%s",
            RelOptUtil.toString(logical))
        .hasSize(1);
  }

  /** And so the statement is refused, naming the column, the two tables and the way out. */
  @Test
  void a_lateral_that_ties_one_outer_column_to_two_tables_is_refused() {
    assertThatThrownBy(() -> PLANNER.plan(SHARED_OUTER_FIELD))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("LATERAL sub-query that constrains o1 and o2")
        .hasMessageContaining("same outer column c.c_custkey")
        .hasMessageContaining("o2 against o1");
  }

  /** The correlation written into the {@code ON} clause is the same shape and the same refusal. */
  @Test
  void the_same_shape_written_in_the_on_clause_is_refused_too() {
    assertThatThrownBy(
            () ->
                PLANNER.plan(
                    """
                    SELECT c.c_name, x.n FROM customer c LEFT JOIN LATERAL (
                      SELECT COUNT(*) AS n FROM orders o1 JOIN orders o2
                        ON o1.o_orderstatus = o2.o_orderstatus
                       AND o1.o_custkey = c.c_custkey AND o2.o_custkey = c.c_custkey) x ON TRUE
                    """))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("same outer column c.c_custkey");
  }

  /**
   * The way out the message names, and the proof it is one: constraining {@code o2} through
   * {@code o1} plans, and the decorrelated join keeps both equalities.
   */
  @Test
  void the_rewritten_form_plans_and_keeps_both_equalities() {
    String sql =
        """
        SELECT c.c_name, x.n FROM customer c LEFT JOIN LATERAL (
          SELECT COUNT(*) AS n FROM orders o1 JOIN orders o2
            ON o1.o_orderstatus = o2.o_orderstatus AND o2.o_custkey = o1.o_custkey
          WHERE o1.o_custkey = c.c_custkey) x ON TRUE
        """;

    RelNode logical = PLANNER.logical(sql);
    List<Join> inner =
        joins(logical).stream().filter(join -> join.getJoinType() == JoinRelType.INNER).toList();

    assertThat(inner).hasSize(1);
    assertThat(RelOptUtil.conjunctions(inner.get(0).getCondition()))
        .as("both equalities:%n%s", RelOptUtil.toString(logical))
        .hasSize(2);
    assertThat(PLANNER.planText(sql)).doesNotContain("Correlate");
  }

  /**
   * The same body under {@code CROSS JOIN LATERAL} rather than {@code LEFT JOIN LATERAL}. The
   * decorrelator's mistake is inside the body, so the join type outside it changes nothing, and the
   * check is on the body — the refusal is the same one.
   */
  @Test
  void the_same_body_under_a_cross_join_lateral_is_refused() {
    assertThatThrownBy(
            () ->
                PLANNER.plan(
                    """
                    SELECT c.c_name, x.n FROM customer c CROSS JOIN LATERAL (
                      SELECT COUNT(*) AS n FROM orders o1 JOIN orders o2
                        ON o1.o_orderstatus = o2.o_orderstatus
                      WHERE o1.o_custkey = c.c_custkey AND o2.o_custkey = c.c_custkey) x
                    """))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("same outer column c.c_custkey");
  }

  /**
   * And under {@code OUTER APPLY}, which needs a {@code LENIENT} conformance to parse at all. The
   * check walks the parsed statement, and {@code APPLY} is a {@code LATERAL} by another spelling, so
   * it must reach this one too — a shape that parses only in one dialect is exactly where a check
   * written against one spelling goes wrong.
   */
  @Test
  void the_same_body_under_an_outer_apply_is_refused_under_a_lenient_conformance() {
    assertThatThrownBy(
            () ->
                PLANNER.plan(
                    """
                    SELECT c.c_name, x.n FROM customer c OUTER APPLY (
                      SELECT COUNT(*) AS n FROM orders o1 JOIN orders o2
                        ON o1.o_orderstatus = o2.o_orderstatus
                      WHERE o1.o_custkey = c.c_custkey AND o2.o_custkey = c.c_custkey) x
                    """,
                    chalk.planner.plan.PushdownPolicy.full(),
                    org.apache.calcite.sql.validate.SqlConformanceEnum.LENIENT))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("same outer column c.c_custkey");
  }

  /**
   * Three tables tied to one outer column, which is the same defect with more of it: the message
   * names two of them, because two is what makes the shape wrong and listing every table would say
   * no more.
   */
  @Test
  void three_references_to_one_outer_column_across_three_tables_are_refused() {
    assertThatThrownBy(
            () ->
                PLANNER.plan(
                    """
                    SELECT c.c_name, x.n FROM customer c LEFT JOIN LATERAL (
                      SELECT COUNT(*) AS n FROM orders o1 JOIN orders o2
                        ON o1.o_orderstatus = o2.o_orderstatus
                        JOIN orders o3 ON o3.o_orderstatus = o1.o_orderstatus
                      WHERE o1.o_custkey = c.c_custkey AND o2.o_custkey = c.c_custkey
                        AND o3.o_custkey = c.c_custkey) x ON TRUE
                    """))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("same outer column c.c_custkey");
  }

  /**
   * The neighbours the check must <em>not</em> refuse, each measured to decorrelate correctly: two
   * different outer columns one per table; one outer column used twice against one table; and the
   * ordinary one-table lateral the corpus is full of.
   */
  @ParameterizedTest(name = "{0}")
  @MethodSource("shapesThatStillPlan")
  void a_lateral_the_decorrelator_gets_right_still_plans(String name, String sql) {
    assertThat(PLANNER.planText(sql)).as(name).doesNotContain("Correlate");
  }

  static Stream<Arguments> shapesThatStillPlan() {
    return Stream.of(
        Arguments.of(
            "two different outer columns, one per table",
            """
            SELECT c.c_name, x.n FROM customer c LEFT JOIN LATERAL (
              SELECT COUNT(*) AS n FROM orders o1 JOIN orders o2
                ON o1.o_orderstatus = o2.o_orderstatus
              WHERE o1.o_custkey = c.c_custkey AND o2.o_custkey = c.c_nationkey) x ON TRUE
            """),
        Arguments.of(
            "one outer column, twice against one table",
            """
            SELECT c.c_name, x.n FROM customer c LEFT JOIN LATERAL (
              SELECT COUNT(*) AS n FROM orders o1 JOIN orders o2
                ON o1.o_orderstatus = o2.o_orderstatus
              WHERE o1.o_custkey = c.c_custkey AND o1.o_orderkey > c.c_custkey) x ON TRUE
            """),
        Arguments.of(
            "the ordinary one-table lateral",
            """
            SELECT c.c_name, x.n FROM customer c, LATERAL (
              SELECT COUNT(*) AS n FROM orders o WHERE o.o_custkey = c.c_custkey) x
            """));
  }

  /** Every {@code Join} in the tree, root first. */
  private static List<Join> joins(RelNode rel) {
    List<Join> found = new ArrayList<>();
    collectJoins(rel, found);
    return found;
  }

  private static void collectJoins(RelNode rel, List<Join> found) {
    if (rel instanceof Join join) {
      found.add(join);
    }

    for (RelNode input : rel.getInputs()) {
      collectJoins(input, found);
    }
  }

  /**
   * V26's residual shape: a non-COUNT aggregate on the inner side of a {@code CROSS JOIN LATERAL}.
   * Calcite's general decorrelator produces a tree its own rules then reject, so the correlate
   * survives — and D67 turns that into a message a reader of the query can act on.
   */
  @Test
  void the_shape_that_survives_decorrelation_names_itself() {
    String sql =
        """
        SELECT s.symbol, x.m
        FROM symbols s, LATERAL (SELECT MAX(b."close") AS m FROM bars_small b
                                 WHERE b.symbol = s.symbol) x
        """;

    assertThatThrownBy(() -> PLANNER.plan(sql))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("could not be decorrelated")
        .hasMessageContaining("LATERAL")
        .hasMessageContaining("CROSS JOIN LATERAL")
        .hasMessageContaining("LEFT JOIN LATERAL");
  }
}
