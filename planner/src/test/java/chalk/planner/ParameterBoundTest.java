package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.planner.diag.PlanText;
import chalk.planner.plan.ChalkRelMetadata;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import chalk.planner.plan.rel.ChalkLimit;
import chalk.planner.plan.rel.ChalkTopN;
import java.nio.file.Files;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.junit.jupiter.api.Test;

/**
 * D285 — a {@code LIMIT} or {@code OFFSET} bound that is a parameter (design 49 §4).
 *
 * <p>Before this, {@code SELECT … LIMIT ?} failed inside the sidecar with {@code AssertionError: not
 * a literal: ?0} and reached the host as a planner internal error (F118). It is support now: the
 * bound stays a {@code RexNode}, the IR carries the parameter, and the executor reads the value that
 * is actually bound when the execution starts.
 */
class ParameterBoundTest {

  /** The failure F118 records, as support: the statement plans. */
  @Test
  void a_parameterised_limit_plans() {
    String text = text("SELECT symbol, ts FROM bars LIMIT ?");

    assertThat(text).contains("ChalkLimit");
    assertThat(text).contains("fetch=[?0]");
  }

  @Test
  void a_parameterised_limit_and_offset_both_plan() {
    String text = text("SELECT symbol, ts FROM bars LIMIT ? OFFSET ?");

    assertThat(text).contains("offset=[?1]");
    assertThat(text).contains("fetch=[?0]");
  }

  @Test
  void a_parameterised_top_n_plans() {
    String text = text("SELECT symbol, ts FROM bars ORDER BY volume DESC LIMIT ?");

    assertThat(text).contains("ChalkTopN");
    assertThat(text).contains("fetch=[?0]");
  }

  /**
   * A bound the planner cannot see bounds nothing it may rely on, so the limit estimates its input.
   * The alternative — treating an unknown bound as small — would price a plan for rows nobody
   * promised.
   */
  @Test
  void an_unhinted_parameterised_limit_estimates_its_input() {
    Planned planned = plan("SELECT symbol, ts FROM bars LIMIT ?");
    ChalkLimit limit = find(planned.physical(), ChalkLimit.class);

    assertThat(limit).isNotNull();
    assertThat(planned.mq().getRowCount(limit))
        .isEqualTo(planned.mq().getRowCount(limit.getInput()));
  }

  /** And an unhinted parameterised top-N is priced for a heap that never fills. */
  @Test
  void an_unhinted_parameterised_top_n_is_costed_as_a_full_pass() {
    Planned planned = plan("SELECT symbol, ts FROM bars ORDER BY volume DESC LIMIT ?");
    ChalkTopN topN = find(planned.physical(), ChalkTopN.class);

    assertThat(topN).isNotNull();
    double inputRows = planned.mq().getRowCount(topN.getInput());
    assertThat(planned.mq().getRowCount(topN)).isEqualTo(inputRows);

    org.apache.calcite.plan.RelOptCost cost =
        topN.computeSelfCost(topN.getCluster().getPlanner(), planned.mq());
    assertThat(cost).isNotNull();
    assertThat(cost.getRows())
        .isCloseTo(
            inputRows * (Math.log(inputRows + 1) / Math.log(2)),
            org.assertj.core.data.Offset.offset(1e-6));
  }

  /**
   * A goal is a number a leaf is costed for, so a bound the planner cannot see states none (D276,
   * design 46 §1).
   */
  @Test
  void an_unhinted_parameterised_bound_states_no_goal() {
    assertThat(text("SELECT symbol, ts FROM bars WHERE ts >= ? LIMIT ?")).doesNotContain("goal=");
    assertThat(text("SELECT symbol, ts FROM bars LIMIT 5 OFFSET ?")).doesNotContain("goal=");
  }

  /** The IR carries the parameter and not a number nobody supplied. */
  @Test
  void the_ir_carries_the_bound_as_a_parameter() {
    Rel fetch = only(ir("SELECT symbol, ts FROM bars LIMIT ? OFFSET ?"), Rel.KindCase.FETCH);

    assertThat(fetch.getFetch().hasCount()).isFalse();
    assertThat(fetch.getFetch().hasCountParam()).isTrue();
    assertThat(fetch.getFetch().getCountParam().getIndex()).isEqualTo(0);
    assertThat(fetch.getFetch().getOffset()).isEqualTo(0L);
    assertThat(fetch.getFetch().hasOffsetParam()).isTrue();
    assertThat(fetch.getFetch().getOffsetParam().getIndex()).isEqualTo(1);
  }

  @Test
  void the_ir_carries_a_top_ns_bound_as_a_parameter() {
    Rel topN = only(ir("SELECT symbol, ts FROM bars ORDER BY volume DESC LIMIT ?"), Rel.KindCase.TOP_N);

    assertThat(topN.getTopN().getCount()).isEqualTo(0L);
    assertThat(topN.getTopN().hasCountParam()).isTrue();
    assertThat(topN.getTopN().getCountParam().getIndex()).isEqualTo(0);
    assertThat(topN.getTopN().hasOffsetParam()).isFalse();
  }

  /** A literal bound is written exactly as it always was, which is what keeps every plan stable. */
  @Test
  void a_literal_bound_carries_no_parameter() {
    Rel fetch = only(ir("SELECT symbol, ts FROM bars LIMIT 5 OFFSET 3"), Rel.KindCase.FETCH);

    assertThat(fetch.getFetch().hasCount()).isTrue();
    assertThat(fetch.getFetch().getCount()).isEqualTo(5L);
    assertThat(fetch.getFetch().getOffset()).isEqualTo(3L);
    assertThat(fetch.getFetch().hasCountParam()).isFalse();
    assertThat(fetch.getFetch().hasOffsetParam()).isFalse();
  }

  // ---- support ----

  private record Planned(RelNode physical, RelMetadataQuery mq) {}

  private static String text(String sql) {
    return PlanText.withAttributes(plan(sql).physical());
  }

  private static Planned plan(String sql) {
    assumeTrue(
        Files.exists(CorpusQueries.corpusDir().resolve("schemas/corpus.binpb")),
        "the recorded corpus catalog carries the statistics the cost model reads");

    CorpusPlanner planner = new CorpusPlanner();
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            planner.catalog(), PushdownPolicy.full(), SqlConfigs.DEFAULT_CONFORMANCE)) {
      RelNode physical = pipeline.plan(sql, true).physical();
      physical.getCluster().setMetadataProvider(ChalkRelMetadata.SOURCE);
      physical.getCluster().invalidateMetadataQuery();
      return new Planned(physical, RelMetadataQuery.instance());
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException("planning failed for: " + sql, e);
    }
  }

  private static Plan ir(String sql) {
    assumeTrue(
        Files.exists(CorpusQueries.corpusDir().resolve("schemas/corpus.binpb")),
        "the recorded corpus catalog carries the statistics the cost model reads");
    return new CorpusPlanner().plan(sql);
  }

  /** The one node of this kind in the plan. */
  private static Rel only(Plan plan, Rel.KindCase kind) {
    Rel found = find(plan.getRoot(), kind);
    assertThat(found).as("a %s in the plan", kind).isNotNull();
    return found;
  }

  private static Rel find(Rel rel, Rel.KindCase kind) {
    if (rel.getKindCase() == kind) {
      return rel;
    }

    for (Rel input : inputs(rel)) {
      Rel found = find(input, kind);
      if (found != null) {
        return found;
      }
    }

    return null;
  }

  private static java.util.List<Rel> inputs(Rel rel) {
    return switch (rel.getKindCase()) {
      case FILTER -> java.util.List.of(rel.getFilter().getInput());
      case PROJECT -> java.util.List.of(rel.getProject().getInput());
      case SORT -> java.util.List.of(rel.getSort().getInput());
      case FETCH -> java.util.List.of(rel.getFetch().getInput());
      case TOP_N -> java.util.List.of(rel.getTopN().getInput());
      default -> java.util.List.of();
    };
  }

  private static <T extends RelNode> T find(RelNode root, Class<T> type) {
    if (type.isInstance(root)) {
      return type.cast(root);
    }

    for (RelNode input : root.getInputs()) {
      T found = find(input, type);
      if (found != null) {
        return found;
      }
    }

    return null;
  }
}
