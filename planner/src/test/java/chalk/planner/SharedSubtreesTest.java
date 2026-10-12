package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import java.util.List;
import org.junit.jupiter.api.Test;

/**
 * A repeated subtree written once (D338, design 63 §2): equal subtrees, estimates aside, go into
 * {@code Plan.shared} and a {@code SharedRef} stands at each use. A scan, a filter of one and
 * {@code VALUES} are cheaper to read again than to hold, so they are not shared.
 */
class SharedSubtreesTest {
  private static final CorpusPlanner PLANNER = new CorpusPlanner();

  private static long refs(Plan plan) {
    long refs = IrNodes.of(plan, Rel.KindCase.SHARED_REF).size();
    for (chalk.ir.v1.SharedPlan shared : plan.getSharedList()) {
      refs += IrNodes.of(Plan.newBuilder().setRoot(shared.getRel()).build(), Rel.KindCase.SHARED_REF).size();
    }
    return refs;
  }

  /** The three branches of a UNION ALL over one aggregated block: one shared plan, three uses. */
  @Test
  void a_block_read_by_three_branches_is_written_once() {
    Plan plan =
        PLANNER.plan(
            "WITH p AS (SELECT symbol, SUM(volume) AS v FROM bars GROUP BY symbol)"
                + " SELECT symbol, v FROM p WHERE v > 0"
                + " UNION ALL SELECT symbol, v FROM p WHERE v < 0"
                + " UNION ALL SELECT 'all', SUM(v) FROM p");

    assertThat(plan.getSharedCount()).isEqualTo(1);
    assertThat(refs(plan)).isEqualTo(3);
    assertThat(IrNodes.of(Plan.newBuilder().setRoot(plan.getShared(0).getRel()).build(), Rel.KindCase.HASH_AGGREGATE))
        .hasSize(1);
  }

  /** A self-join of a derived table shares the derived table. */
  @Test
  void a_self_join_of_a_derived_table_shares_it() {
    Plan plan =
        PLANNER.plan(
            "WITH t AS (SELECT symbol, COUNT(*) AS n FROM bars GROUP BY symbol)"
                + " SELECT a.symbol, a.n, b.n FROM t a JOIN t b ON a.symbol = b.symbol");

    assertThat(plan.getSharedCount()).isEqualTo(1);
    assertThat(refs(plan)).isEqualTo(2);
  }

  /** Two reads of a table are two scans: nothing is held. */
  @Test
  void a_scan_is_not_shared() {
    Plan plan =
        PLANNER.plan(
            "SELECT a.symbol FROM bars a JOIN bars b ON a.symbol = b.symbol AND a.ts = b.ts");

    assertThat(plan.getSharedList()).isEmpty();
    assertThat(IrNodes.of(plan, Rel.KindCase.SHARED_REF)).isEmpty();
  }

  /** Nor is a filter of one. */
  @Test
  void a_filtered_scan_is_not_shared() {
    Plan plan =
        PLANNER.plan(
            "WITH t AS (SELECT symbol, ts FROM bars WHERE volume > 10)"
                + " SELECT a.symbol FROM t a JOIN t b ON a.symbol = b.symbol AND a.ts = b.ts");

    assertThat(plan.getSharedList()).isEmpty();
  }

  /**
   * A remote query a plan reads twice is computed once — unless its pushed plan carries a node an
   * access policy put there, which stays at each use, as everything a policy put in the plan does.
   */
  @Test
  void a_policy_inside_a_pushed_plan_is_not_shared() {
    assertThat(
            chalk.planner.ir.SharedSubtrees.share(twoUsesOf(remoteQuery(false))).getSharedCount())
        .isEqualTo(1);
    assertThat(
            chalk.planner.ir.SharedSubtrees.share(twoUsesOf(remoteQuery(true))).getSharedCount())
        .isZero();
  }

  private static chalk.ir.v1.RowType oneColumn() {
    return chalk.ir.v1.RowType.newBuilder()
        .addFields(
            chalk.ir.v1.Field.newBuilder()
                .setName("n")
                .setType(chalk.ir.v1.Type.newBuilder().setKind(chalk.ir.v1.TypeKind.TYPE_KIND_I64)))
        .build();
  }

  private static Rel remoteQuery(boolean policed) {
    Rel pushed =
        Rel.newBuilder()
            .setRowType(oneColumn())
            .setPolicyInjected(policed)
            .setRead(
                chalk.ir.v1.Read.newBuilder()
                    .setTable(
                        chalk.ir.v1.TableRef.newBuilder()
                            .setSourceId("db")
                            .setSchema("main")
                            .setTable("orders"))
                    .addProjection(0))
            .build();
    return Rel.newBuilder()
        .setRowType(oneColumn())
        .setRemoteQuery(
            chalk.ir.v1.RemoteQuery.newBuilder()
                .setSourceId("db")
                .setQueryText("SELECT n FROM orders")
                .setPushedPlan(pushed))
        .build();
  }

  private static Plan twoUsesOf(Rel rel) {
    return Plan.newBuilder()
        .setRoot(
            Rel.newBuilder()
                .setRowType(oneColumn())
                .setSetOp(
                    chalk.ir.v1.SetOp.newBuilder()
                        .setKind(chalk.ir.v1.SetOpKind.SET_OP_KIND_UNION_ALL)
                        .addInputs(rel)
                        .addInputs(rel)))
        .build();
  }

  /** A plan that repeats nothing is unchanged, its digest included. */
  @Test
  void a_plan_that_repeats_nothing_has_no_shared_list() {
    Plan plan = PLANNER.plan("SELECT symbol, SUM(volume) FROM bars GROUP BY symbol");

    assertThat(plan.getSharedList()).isEmpty();
    assertThat(List.of(plan.getRoot().getKindCase())).doesNotContain(Rel.KindCase.SHARED_REF);
  }
}
