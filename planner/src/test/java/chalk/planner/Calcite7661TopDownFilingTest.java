package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.plan.PlannerPipeline;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.JoinRelType;
import org.junit.jupiter.api.Test;

/**
 * The reproduction to file upstream, minimal and in one piece (F38, ADR 0026).
 *
 * <p><b>Issue.</b> {@code TopDownGeneralDecorrelator} loses the equality between the two copies of an
 * outer field when both inputs of a join inside a {@code LATERAL} body are constrained by it.
 * CALCITE-7661 fixed exactly this in {@code RelDecorrelator}, for every join type; the same shape
 * reaches the general (top-down) decorrelator, which that fix did not touch. The consequence is
 * wrong rows with nothing in the plan text to say so: one input of the join ranges over its whole
 * table, so the body answers about rows belonging to another outer row. Under an entitlement it
 * crosses tenancies.
 *
 * <p><b>Schema.</b> Two tables of TPC-H shape; the join key {@code o_orderstatus} is deliberately
 * <em>not</em> unique, so no functional dependency can stand in for the lost equality.
 *
 * <pre>{@code
 * customer(c_custkey INTEGER, c_name VARCHAR, c_nationkey INTEGER, …)
 * orders(o_orderkey INTEGER, o_custkey INTEGER, o_orderstatus VARCHAR, …)
 * }</pre>
 *
 * <p><b>Statement.</b>
 *
 * <pre>{@code
 * SELECT c.c_name, x.n FROM customer c LEFT JOIN LATERAL (
 *   SELECT COUNT(*) AS n FROM orders o1 JOIN orders o2
 *     ON o1.o_orderstatus = o2.o_orderstatus
 *   WHERE o1.o_custkey = c.c_custkey AND o2.o_custkey = c.c_custkey) x ON TRUE
 * }</pre>
 *
 * <p><b>Expected.</b> The decorrelated inner join carries two conjuncts: the status equality the
 * statement wrote, and {@code o1.o_custkey = o2.o_custkey}, which decorrelation owes it because both
 * sides were tied to one outer value. <b>Actual:</b> one conjunct, the status equality.
 *
 * <p><b>Configuration.</b> The general decorrelator, enabled through
 * {@code CalciteConnectionProperty.TOPDOWN_GENERAL_DECORRELATION_ENABLED}, and
 * {@code SqlToRelConverter.Config.withExpand(false)}. Reproduces on 1.42 and on the pinned 1.43
 * snapshot alike.
 *
 * <p><b>What this test is for.</b> {@link PlannerPipeline#logical} converts and decorrelates and
 * runs none of {@code plan}'s checks, so it is the only entry point on which the wrong tree is still
 * reachable — the refusal of ADR 0026 rejects the statement everywhere else. <b>The day Calcite
 * fixes this, the assertion below fails</b>, which is the signal to delete the refusal, this class
 * and the F38 row together. Its twin in {@link LateralDecorrelationTest} says the same thing in
 * Chalk's own vocabulary; this one is written to be read by somebody who has never seen Chalk.
 */
class Calcite7661TopDownFilingTest {
  private static final CorpusPlanner PLANNER = new CorpusPlanner();

  private static final String SQL =
      """
      SELECT c.c_name, x.n FROM customer c LEFT JOIN LATERAL (
        SELECT COUNT(*) AS n FROM orders o1 JOIN orders o2
          ON o1.o_orderstatus = o2.o_orderstatus
        WHERE o1.o_custkey = c.c_custkey AND o2.o_custkey = c.c_custkey) x ON TRUE
      """;

  @Test
  void top_down_decorrelation_loses_the_shared_outer_field_equality() {
    RelNode logical = PLANNER.logical(SQL);

    List<Join> inner = new ArrayList<>();
    collect(logical, inner);

    assertThat(inner)
        .as("the one inner join the body wrote:%n%s", RelOptUtil.toString(logical))
        .hasSize(1);
    assertThat(RelOptUtil.conjunctions(inner.get(0).getCondition()))
        .as(
            "expected two conjuncts — o1.o_orderstatus = o2.o_orderstatus and the "
                + "o1.o_custkey = o2.o_custkey decorrelation owes this join — and found one:%n%s",
            RelOptUtil.toString(logical))
        .hasSize(1);
  }

  private static void collect(RelNode rel, List<Join> into) {
    if (rel instanceof Join join && join.getJoinType() == JoinRelType.INNER) {
      into.add(join);
    }
    for (RelNode input : rel.getInputs()) {
      collect(input, into);
    }
  }
}
