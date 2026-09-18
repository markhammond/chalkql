package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.rel.ChalkAdaptiveJoin;
import chalk.planner.plan.rel.ChalkLookupJoin;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.junit.jupiter.api.Test;

/**
 * What a join charges for the calls across a source boundary (ADR 0025 V144, part 2h and part 2i).
 *
 * <p>A cross-source join's right input is a subtree whose own {@code SourceToLocalConverter} already
 * charges one {@code remote_call_cost}, and Volcano adds that to the join's <em>self</em> cost. So a
 * join whose driving side fits in one call must add no call charge of its own: charging it again
 * made a lookup lose to a full fetch of any table whose whole content cost less than one extra
 * imaginary round trip, which at the shipped costs is any table under about five hundred rows.
 *
 * <p>The two nodes reach it by different arithmetic — {@link ChalkLookupJoin} instantiates its right
 * input per call, and {@link ChalkAdaptiveJoin} holds its lookup branch as a field beside a right
 * input that is the local alternative — so both are pinned here, in the one place a reader can
 * compare them. A cross-source {@code Through} is decided by exactly this comparison
 * (docs/design/16-entitlements.md §3.13, D229).
 *
 * <p>Beside them is the one join that charges the <em>whole</em> outer row count rather than the
 * calls beyond the first: a nested loop with a remote inner side is a shape no plan may keep, so its
 * cost exists to keep the optimiser away from it rather than to price a choice (F51).
 */
class JoinCallCostTest {

  /** The shipped {@code remote_call_cost} (D38): what one round trip is worth. */
  private static final double REMOTE_CALL = 1000.0;

  /**
   * A source whose IN list is wide enough that every key of the fixture fits one call, and whose
   * round trip costs {@code remoteCall}.
   *
   * <p>Raising that one number is what makes the assertion sharp: a self cost that charged the first
   * call would move with it by a thousandfold, and one that does not is unchanged.
   */
  private static RegisteredCatalog generous(double remoteCall) {
    chalk.ir.v1.CatalogContext catalog =
        TestCatalogs.withRemote(
            "db",
            TestCatalogs.fullSqlCapabilities().setMaxInList(500_000).build(),
            TestCatalogs.duckDbProfile());
    chalk.ir.v1.CatalogContext.Builder edited = catalog.toBuilder();
    for (int s = 0; s < edited.getSchemasCount(); s++) {
      if (edited.getSchemas(s).getKind() == chalk.ir.v1.SourceKind.SOURCE_KIND_REMOTE) {
        edited.setSchemas(
            s,
            edited.getSchemas(s).toBuilder()
                .setCostProfile(
                    chalk.ir.v1.CostProfile.newBuilder()
                        .setRemoteCallCost(remoteCall)
                        .setRemoteRowCost(2.0)));
      }
    }
    return new CatalogRegistry().register(edited.build());
  }

  private static RelNode physical(String sql, double remoteCall) throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(generous(remoteCall), PushdownPolicy.full())) {
      return pipeline.plan(sql, true).physical();
    }
  }

  private static @org.checkerframework.checker.nullness.qual.Nullable RelNode find(
      RelNode rel, Class<?> kind) {
    if (kind.isInstance(rel)) {
      return rel;
    }
    for (RelNode input : rel.getInputs()) {
      RelNode found = find(input, kind);
      if (found != null) {
        return found;
      }
    }
    return null;
  }

  private static double selfCost(RelNode rel) {
    RelMetadataQuery mq = rel.getCluster().getMetadataQuery();
    RelOptCost cost = mq.getNonCumulativeCost(rel);
    return cost == null ? Double.NaN : cost.getRows();
  }

  private static final String LOOKUP_SHAPE =
      "SELECT c.c_name, o.o_orderkey FROM main.customer c"
          + " JOIN db.orders o ON o.o_custkey = c.c_custkey";

  /** A guessed small side is what makes the planner choose the adaptive node instead (D97). */
  private static final String ADAPTIVE_SHAPE =
      "SELECT c.c_name, o.o_orderkey FROM"
          + " (SELECT c_custkey, c_name FROM main.customer WHERE c_mktsegment = 'BUILDING') c"
          + " JOIN db.orders o ON o.o_custkey = c.c_custkey";

  @Test
  void a_lookup_join_that_makes_one_call_charges_no_call_at_all() throws Exception {
    RelNode cheap = find(physical(LOOKUP_SHAPE, REMOTE_CALL), ChalkLookupJoin.class);
    RelNode dear = find(physical(LOOKUP_SHAPE, REMOTE_CALL * 1000), ChalkLookupJoin.class);

    assertThat(cheap).isNotNull();
    assertThat(dear).isNotNull();
    // The one call this join makes is the one its right input already charged, so a round trip
    // worth a thousand times more leaves this node's own cost exactly where it was.
    assertThat(selfCost(dear)).isEqualTo(selfCost(cheap));
  }

  @Test
  void an_adaptive_join_that_makes_one_call_charges_no_call_at_all() throws Exception {
    RelNode cheap = find(physical(ADAPTIVE_SHAPE, REMOTE_CALL), ChalkAdaptiveJoin.class);
    RelNode dear = find(physical(ADAPTIVE_SHAPE, REMOTE_CALL * 1000), ChalkAdaptiveJoin.class);

    assertThat(cheap).isNotNull();
    assertThat(dear).isNotNull();
    assertThat(selfCost(dear)).isEqualTo(selfCost(cheap));
  }

  // --------------------------------------------------- a nested loop's remote inner side (F51)

  /**
   * A {@link ChalkNestedLoopJoin} instantiates its inner side per outer row, so a remote query there
   * is a round trip per outer row — and charging nothing for it made this join win against a hash
   * join that would have been valid, leaving {@code CrossSourceSupport} to refuse the plan.
   *
   * <p>The node is built by hand because the shape it costs is one the planner refuses: what is
   * being pinned is the arithmetic that stops it from being chosen. And it is the <em>whole</em>
   * outer row count rather than the two joins above's {@code calls - 1}: a correction that leaves
   * the shape free at a one-row estimate leaves the refusal where it was, and D104 refuses this
   * shape at every size.
   */
  @Test
  void a_nested_loop_join_charges_a_remote_inner_side_at_every_outer_row() throws Exception {
    Nested cheap = nestedLoop(REMOTE_CALL, /* remoteInner= */ true);
    Nested dear = nestedLoop(REMOTE_CALL * 1000, /* remoteInner= */ true);

    assertThat(cheap.outerRows()).isGreaterThan(0);
    assertThat(dear.selfCost() - cheap.selfCost())
        .isEqualTo(cheap.outerRows() * ((REMOTE_CALL * 1000) - REMOTE_CALL));
  }

  /** And an inner side that is local costs no round trip, however dear one is. */
  @Test
  void a_nested_loop_join_over_a_local_inner_side_charges_no_call() throws Exception {
    assertThat(nestedLoop(REMOTE_CALL * 1000, /* remoteInner= */ false).selfCost())
        .isEqualTo(nestedLoop(REMOTE_CALL, /* remoteInner= */ false).selfCost());
  }

  /**
   * And the charge reaches a fetch the inner side <b>hides under a join</b> (F55).
   *
   * <p>The first cut read the boundary through single-input nodes alone — a projection or a filter
   * above a {@code SourceToLocalConverter} is the same fetch, and a join's rows are not one
   * boundary's. That is the right reading for what a fetch costs and the wrong one for what a
   * nested loop's inner side is: whatever is in there is re-read per outer row, so a remote query
   * anywhere under it is a round trip per outer row. A context with an open half is where it
   * showed — the parent's entitled leaf becomes a join to the membership tables, with the source's
   * own scan inside it — and the plan was costed as free, passed the planner's own backstop, and
   * was refused by the client's I-IR-20 instead.
   */
  @Test
  void a_nested_loop_join_charges_a_fetch_under_a_join_on_its_inner_side() throws Exception {
    Nested cheap = nestedLoopOverJoin(REMOTE_CALL);
    Nested dear = nestedLoopOverJoin(REMOTE_CALL * 1000);

    assertThat(cheap.outerRows()).isGreaterThan(0);
    assertThat(dear.selfCost() - cheap.selfCost())
        .isEqualTo(cheap.outerRows() * ((REMOTE_CALL * 1000) - REMOTE_CALL));
  }

  /** And the backstop reads the inner side the same way, so the two never disagree (D104). */
  @Test
  void the_backstop_refuses_a_fetch_under_a_join_on_an_inner_side() throws Exception {
    RelNode physical = physical(LOOKUP_SHAPE, REMOTE_CALL);
    org.assertj.core.api.Assertions.assertThatThrownBy(
            () ->
                chalk.planner.plan.CrossSourceSupport.check(
                    hidden(physical), chalk.planner.plan.JoinPolicy.DEFAULT))
        .isInstanceOf(chalk.planner.UnsupportedFeatureException.class)
        .hasMessageContaining("nested-loop join's inner side");
  }

  /** One nested-loop join's self cost, and how many outer rows it was costed over. */
  private record Nested(double selfCost, double outerRows) {}

  private static Nested nestedLoopOverJoin(double remoteCall) throws Exception {
    RelNode physical = physical(LOOKUP_SHAPE, remoteCall);
    RelNode join = hidden(physical);
    return new Nested(
        selfCost(join), physical.getCluster().getMetadataQuery().getRowCount(join.getInput(0)));
  }

  /**
   * A nested-loop join whose inner side is a <em>join</em> with the remote fetch inside it, which is
   * the shape the boundary reading walked straight past.
   */
  private static RelNode hidden(RelNode physical) {
    RelNode remote = find(physical, chalk.planner.plan.rel.SourceToLocalConverter.class);
    RelNode local = find(physical, chalk.planner.plan.rel.ChalkTableScan.class);
    assertThat(remote).isNotNull();
    assertThat(local).isNotNull();

    org.apache.calcite.rex.RexNode always =
        physical.getCluster().getRexBuilder().makeLiteral(true);
    RelNode inner =
        chalk.planner.plan.rel.ChalkHashJoin.create(
            remote, local, always, java.util.Set.of(),
            org.apache.calcite.rel.core.JoinRelType.INNER);
    return chalk.planner.plan.rel.ChalkNestedLoopJoin.create(
        local, inner, always, java.util.Set.of(),
        org.apache.calcite.rel.core.JoinRelType.INNER);
  }

  private static Nested nestedLoop(double remoteCall, boolean remoteInner) throws Exception {
    RelNode physical = physical(LOOKUP_SHAPE, remoteCall);
    RelNode remote = find(physical, chalk.planner.plan.rel.SourceToLocalConverter.class);
    RelNode local = find(physical, chalk.planner.plan.rel.ChalkTableScan.class);
    assertThat(remote).isNotNull();
    assertThat(local).isNotNull();

    RelNode outer = remoteInner ? local : remote;
    RelNode inner = remoteInner ? remote : local;
    // A cross join: this node's cost does not read the condition, and a literal keeps the two
    // inputs' row types out of it.
    RelNode join =
        chalk.planner.plan.rel.ChalkNestedLoopJoin.create(
            outer,
            inner,
            physical.getCluster().getRexBuilder().makeLiteral(true),
            java.util.Set.of(),
            org.apache.calcite.rel.core.JoinRelType.INNER);
    return new Nested(selfCost(join), physical.getCluster().getMetadataQuery().getRowCount(outer));
  }
}
