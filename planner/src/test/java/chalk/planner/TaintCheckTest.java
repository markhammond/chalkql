package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.Disclosed;
import chalk.planner.entitlement.DisclosureMap;
import chalk.planner.entitlement.EntitledRelOptTable;
import chalk.planner.entitlement.PolicyException;
import chalk.planner.entitlement.TaintCheck;
import chalk.planner.plan.ChalkRelMetadata;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.rel.ChalkTableScan;
import com.google.common.collect.ImmutableList;
import java.math.BigDecimal;
import java.util.List;
import java.util.Map;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.CorrelationId;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.hint.RelHint;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rel.logical.LogicalUnion;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.junit.jupiter.api.Test;

/**
 * The taint check's three physical clauses, one deliberate breakage each
 * (docs/design/16-entitlements.md §3.10 and §8).
 *
 * <p>Each test builds a tree the pass would never produce and hands it to the check, which is the
 * only way to test a check whose whole job is to catch what the rewrite got wrong: a passing plan
 * proves nothing about it. Every failure is a {@code POLICY} error <em>and</em> a bug report, and
 * the message says so.
 */
class TaintCheckTest {
  private static final RegisteredCatalog TENANCY =
      new CatalogRegistry().register(TenancyCatalogs.catalog());

  /** orders(id, org_id, member_id, amount, note): amount population-only, note withheld. */
  private static final DisclosureMap ORDERS =
      new DisclosureMap(
          "main",
          "orders",
          "fedcba9876543210fedcba9876543210",
          List.of(
              Disclosed.FULL,
              Disclosed.FULL,
              Disclosed.FULL,
              Disclosed.AGGREGATE,
              Disclosed.REDACTED),
          DisclosureMap.Visibility.SOME);

  /** A scan of the entitled table, as the pass would have wrapped it, and nothing above it yet. */
  private record Leaf(RelNode scan, RelOptCluster cluster, RexBuilder rexBuilder) {}

  private static Leaf leaf() throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            TENANCY,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            BoundContext.of(TenancyCatalogs.auditor(1)))) {
      RelNode logical = pipeline.logical("SELECT * FROM orders");
      TableScan scan = firstScan(logical);
      RelOptCluster cluster = scan.getCluster();
      cluster.setMetadataProvider(ChalkRelMetadata.SOURCE);
      cluster.invalidateMetadataQuery();
      RelOptTable entitled = EntitledRelOptTable.of(scan.getTable(), ORDERS);
      return new Leaf(
          ChalkTableScan.create(cluster, entitled), cluster, cluster.getRexBuilder());
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

  private static void check(RelNode physical, Map<String, RexNode> rowPredicates) {
    RelOptCluster cluster = physical.getCluster();
    cluster.invalidateMetadataQuery();
    TaintCheck.physical(
        physical,
        ImmutableList.of(ORDERS),
        rowPredicates,
        RelMetadataQuery.instance(),
        cluster.getRexBuilder(),
        new chalk.planner.plan.ChalkRexExecutor(RexUtil.EXECUTOR));
  }

  // ------------------------------------------------------- clause 3, through a parent (§3.13)

  /**
   * A {@code through} child has no row predicate a walk can read — its {@code Filter_R} is an OR of
   * join markers, and a marker does not survive above {@code Project_D} — so what clause 3 checks is
   * the <em>shape</em>: the join is still there, on the declared key. Take it away and the plan is
   * refused, which is the whole of the guarantee for a table with no tenancy column of its own.
   */
  @Test
  void a_through_join_the_plan_no_longer_holds_is_refused() throws Exception {
    Leaf leaf = leaf();
    List<TaintCheck.ThroughEvidence> joins =
        List.of(new TaintCheck.ThroughEvidence("main.orders", 2, "main.members", 0));

    RelNode alone =
        LogicalProject.create(
            leaf.scan(),
            ImmutableList.<RelHint>of(),
            List.of(leaf.rexBuilder().makeInputRef(leaf.scan(), 0)),
            List.of("id"),
            java.util.Set.<CorrelationId>of());

    RelOptCluster cluster = alone.getCluster();
    cluster.invalidateMetadataQuery();
    assertThatThrownBy(
            () ->
                TaintCheck.physical(
                    alone,
                    ImmutableList.of(ORDERS),
                    Map.of(),
                    joins,
                    RelMetadataQuery.instance(),
                    cluster.getRexBuilder(),
                    new chalk.planner.plan.ChalkRexExecutor(RexUtil.EXECUTOR)))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("derives through main.members")
        .hasMessageContaining("is not in the finished plan")
        .hasMessageContaining("also a bug");
  }

  // ------------------------------------------- clause 3, a path whose key set folded (F67)

  /**
   * The folded form of a path's own key-set join (ADR 0052). Where the endpoint predicate pins the
   * endpoint's unique key to one constant the key set is a single row, so the optimiser writes the
   * constant where the column was and the join's far side has no column origin left to match. What
   * the check asks for instead is the <em>restriction</em> the key set came to: the target's own
   * correlation key pinned to that constant, which the plan here carries as a filter.
   */
  @Test
  void a_key_set_that_folded_to_one_constant_is_matched_in_its_collapsed_form() throws Exception {
    Leaf leaf = leaf();
    RexBuilder rex = leaf.rexBuilder();
    RelNode restricted =
        org.apache.calcite.rel.logical.LogicalFilter.create(
            leaf.scan(),
            rex.makeCall(
                SqlStdOperatorTable.EQUALS,
                rex.makeInputRef(leaf.scan(), 2),
                rex.makeExactLiteral(BigDecimal.ONE)));
    RelNode kept =
        LogicalProject.create(
            restricted,
            ImmutableList.<RelHint>of(),
            List.of(rex.makeInputRef(restricted, 0)),
            List.of("id"),
            java.util.Set.<CorrelationId>of());

    checkWith(kept, folded(rex, BigDecimal.ONE));
  }

  /**
   * The negative half, which is what makes the acceptance mean anything: take the restriction away
   * and the same evidence is refused. A statement that lacks the mechanism fails closed whether the
   * key set folded or not.
   */
  @Test
  void the_folded_form_is_still_refused_where_the_plan_restricts_nothing() throws Exception {
    Leaf leaf = leaf();
    RexBuilder rex = leaf.rexBuilder();
    RelNode broken =
        LogicalProject.create(
            leaf.scan(),
            ImmutableList.<RelHint>of(),
            List.of(rex.makeInputRef(leaf.scan(), 0)),
            List.of("id"),
            java.util.Set.<CorrelationId>of());

    assertThatThrownBy(() -> checkWith(broken, folded(rex, BigDecimal.ONE)))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("derives along a path through main.members")
        .hasMessageContaining("is not in the finished plan")
        .hasMessageContaining("also a bug");
  }

  /**
   * And the constant has to be the key set's own: a plan that pins the correlation key to some
   * other value restricts the rows to a set the endpoint predicate never named.
   */
  @Test
  void the_folded_form_is_refused_where_the_plan_pins_another_constant() throws Exception {
    Leaf leaf = leaf();
    RexBuilder rex = leaf.rexBuilder();
    RelNode restricted =
        org.apache.calcite.rel.logical.LogicalFilter.create(
            leaf.scan(),
            rex.makeCall(
                SqlStdOperatorTable.EQUALS,
                rex.makeInputRef(leaf.scan(), 2),
                rex.makeExactLiteral(BigDecimal.valueOf(7))));
    RelNode broken =
        LogicalProject.create(
            restricted,
            ImmutableList.<RelHint>of(),
            List.of(rex.makeInputRef(restricted, 0)),
            List.of("id"),
            java.util.Set.<CorrelationId>of());

    assertThatThrownBy(() -> checkWith(broken, folded(rex, BigDecimal.ONE)))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("is not in the finished plan");
  }

  // ------------------------------------- clause 3, a marker the optimiser removed (F69)

  /**
   * F69, and clause 3's answer to it (ADR 0054). A target that holds a tenancy directly
   * <em>and</em> along a path has {@code Filter_R = own OR marker}; where the plan's own predicates
   * guarantee {@code own} of every row of the leaf, the marker could not have decided one, and the
   * key set and its join are a thing no correct plan has to hold. Here the plan restricts
   * {@code org_id} to 1 and the leaf's own restriction is {@code org_id IN (1, 2)}, which that
   * implies.
   */
  @Test
  void a_marker_whose_disjunct_the_plans_own_predicates_imply_is_not_asked_for() throws Exception {
    Leaf leaf = leaf();
    RexBuilder rex = leaf.rexBuilder();
    RelNode restricted =
        org.apache.calcite.rel.logical.LogicalFilter.create(
            leaf.scan(),
            rex.makeCall(
                SqlStdOperatorTable.EQUALS,
                rex.makeInputRef(leaf.scan(), 1),
                rex.makeExactLiteral(BigDecimal.ONE)));
    RelNode kept =
        LogicalProject.create(
            restricted,
            ImmutableList.<RelHint>of(),
            List.of(rex.makeInputRef(restricted, 0)),
            List.of("id"),
            java.util.Set.<CorrelationId>of());

    checkWith(kept, removed(orgIn(rex, 1, 2)));
  }

  /**
   * The negative half. The same plan and the same shape of evidence, but what the leaf restricts by
   * itself is over another organisation: the plan guarantees nothing about it, the marker is still
   * what decides the row, and a plan without the join is refused.
   */
  @Test
  void a_marker_the_plan_implies_nothing_about_is_still_refused() throws Exception {
    Leaf leaf = leaf();
    RexBuilder rex = leaf.rexBuilder();
    RelNode restricted =
        org.apache.calcite.rel.logical.LogicalFilter.create(
            leaf.scan(),
            rex.makeCall(
                SqlStdOperatorTable.EQUALS,
                rex.makeInputRef(leaf.scan(), 1),
                rex.makeExactLiteral(BigDecimal.ONE)));
    RelNode broken =
        LogicalProject.create(
            restricted,
            ImmutableList.<RelHint>of(),
            List.of(rex.makeInputRef(restricted, 0)),
            List.of("id"),
            java.util.Set.<CorrelationId>of());

    assertThatThrownBy(() -> checkWith(broken, removed(orgIn(rex, 3, 4))))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("is not in the finished plan")
        .hasMessageContaining("also a bug");
  }

  /**
   * And the principal for whom the marker is the <b>whole</b> of {@code Filter_R} — every other
   * restriction folded to FALSE, so the evidence carries no own restriction at all. There is nothing
   * to imply, and the join's absence is the refusal it always was. This is the vendor holding one
   * grant of the kind and nothing else, which is the case the acceptance must not reach.
   */
  @Test
  void a_marker_that_is_the_whole_row_predicate_is_refused_wherever_the_join_is_gone()
      throws Exception {
    Leaf leaf = leaf();
    RexBuilder rex = leaf.rexBuilder();
    RelNode restricted =
        org.apache.calcite.rel.logical.LogicalFilter.create(
            leaf.scan(),
            rex.makeCall(
                SqlStdOperatorTable.EQUALS,
                rex.makeInputRef(leaf.scan(), 1),
                rex.makeExactLiteral(BigDecimal.ONE)));
    RelNode broken =
        LogicalProject.create(
            restricted,
            ImmutableList.<RelHint>of(),
            List.of(rex.makeInputRef(restricted, 0)),
            List.of("id"),
            java.util.Set.<CorrelationId>of());

    assertThatThrownBy(() -> checkWith(broken, removed(null)))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("is not in the finished plan");
  }

  // --------------------------- clause 3, a conjunct over a context relation (F77)

  /**
   * F77, and clause 3's answer to it (ADR 0058). A membership over a name the request left
   * <b>open</b> is not made literal: it stays a sub-query over a context relation, and by the time
   * there is a physical tree it is a semi-join rather than a predicate, so clause 3 checks it by its
   * evidence — the relation is still scanned somewhere. But where the plan's own pulled-up
   * predicates <em>guarantee</em> the conjunct outright, the optimiser may drop the marker join
   * soundly and take the scan with it, and the evidence test reads that absence as the failure it
   * exists to catch.
   *
   * <p>Here the leaf's restriction is {@code org_id = 1 OR org_id IN (ctx.agent_orgs)} and the plan
   * restricts {@code org_id} to 1, which implies the whole disjunction whatever the open list comes
   * to hold. So the conjunct is established and the scan is a thing no correct plan has to carry.
   */
  @Test
  void a_conjunct_over_a_context_relation_the_plan_guarantees_is_not_asked_for() throws Exception {
    Leaf leaf = leaf();
    RexBuilder rex = leaf.rexBuilder();
    RelNode restricted =
        org.apache.calcite.rel.logical.LogicalFilter.create(
            leaf.scan(),
            rex.makeCall(
                SqlStdOperatorTable.EQUALS,
                rex.makeInputRef(leaf.scan(), 1),
                rex.makeExactLiteral(BigDecimal.ONE)));
    RelNode kept =
        LogicalProject.create(
            restricted,
            ImmutableList.<RelHint>of(),
            List.of(rex.makeInputRef(restricted, 0)),
            List.of("id"),
            java.util.Set.<CorrelationId>of());

    check(kept, Map.of("main.orders", orgOneOrOpenList(leaf, rex)));
  }

  /**
   * The negative half, which is what makes the acceptance mean anything. The same conjunct and the
   * same missing scan, over a plan that restricts nothing at all: nothing establishes the
   * disjunction, the open list is still what decides the row, and the refusal is the one clause 3
   * has always given, naming the relation the plan no longer scans.
   */
  @Test
  void a_conjunct_over_a_context_relation_the_plan_implies_nothing_about_is_still_refused()
      throws Exception {
    Leaf leaf = leaf();
    RexBuilder rex = leaf.rexBuilder();
    RelNode broken =
        LogicalProject.create(
            leaf.scan(),
            ImmutableList.<RelHint>of(),
            List.of(rex.makeInputRef(leaf.scan(), 0)),
            List.of("id"),
            java.util.Set.<CorrelationId>of());

    assertThatThrownBy(() -> check(broken, Map.of("main.orders", orgOneOrOpenList(leaf, rex))))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("does not survive to its consumer")
        .hasMessageContaining("the conjunct reads the context relation(s) [agent_orgs]")
        .hasMessageContaining("which the plan no longer scans")
        .hasMessageContaining("also a bug");
  }

  /**
   * {@code org_id = 1 OR org_id IN (ctx.agent_orgs)}: the folded half of a partially bound tenancy
   * beside the membership over the half the request left open, which is the row predicate the pass
   * records for such a leaf (§2.1, D232).
   */
  private static RexNode orgOneOrOpenList(Leaf leaf, RexBuilder rex) {
    RelOptTable relation =
        org.apache.calcite.prepare.RelOptTableImpl.create(
            null,
            openList(leaf).getRowType(leaf.cluster().getTypeFactory()),
            List.of(BoundContext.SCHEMA, "agent_orgs"),
            openList(leaf),
            (org.apache.calcite.linq4j.tree.TableExpressionFactory) null);
    RelNode scan =
        org.apache.calcite.rel.logical.LogicalTableScan.create(
            leaf.cluster(), relation, ImmutableList.<RelHint>of());
    return RexUtil.composeDisjunction(
        rex,
        List.of(
            rex.makeCall(
                SqlStdOperatorTable.EQUALS,
                rex.makeInputRef(leaf.scan(), 1),
                rex.makeExactLiteral(BigDecimal.ONE)),
            org.apache.calcite.rex.RexSubQuery.in(
                scan, ImmutableList.of(rex.makeInputRef(leaf.scan(), 1)))));
  }

  /** The {@code agent_orgs} relation of a partially bound context, as a table a scan can name. */
  private static org.apache.calcite.schema.Table openList(Leaf leaf) {
    chalk.planner.entitlement.ContextSchema schema =
        chalk.planner.entitlement.ContextSchema.of(
            BoundContext.of(TenancyCatalogs.managerWithAgentShape(1)));
    org.apache.calcite.schema.Table relation = schema.tables().get("agent_orgs");
    if (relation == null) {
      throw new AssertionError("agent_orgs is not an open relation of this context");
    }
    return relation;
  }

  /** {@code org_id IN (…)} over the leaf's own column, as a folded row restriction reads. */
  private static RexNode orgIn(RexBuilder rex, int... orgs) {
    List<RexNode> terms = new java.util.ArrayList<>(orgs.length);
    for (int id : orgs) {
      terms.add(
          rex.makeCall(
              SqlStdOperatorTable.EQUALS,
              rex.makeInputRef(
                  rex.getTypeFactory()
                      .createSqlType(org.apache.calcite.sql.type.SqlTypeName.INTEGER),
                  1),
              rex.makeExactLiteral(BigDecimal.valueOf(id))));
    }
    return RexUtil.composeDisjunction(rex, terms);
  }

  /** A path's key-set evidence with nothing pinned, carrying what the leaf restricts by itself. */
  private static List<TaintCheck.ThroughEvidence> removed(
      org.apache.calcite.rex.@org.checkerframework.checker.nullness.qual.Nullable RexNode own) {
    return List.of(
        TaintCheck.ThroughEvidence.raw("main.orders", 2, "main.members", 0, null, own));
  }

  /** The key-set evidence of a path whose endpoint predicate pinned its key to {@code value}. */
  private static List<TaintCheck.ThroughEvidence> folded(RexBuilder rex, BigDecimal value) {
    return List.of(
        TaintCheck.ThroughEvidence.raw(
            "main.orders", 2, "main.members", 0, rex.makeExactLiteral(value)));
  }

  private static void checkWith(RelNode physical, List<TaintCheck.ThroughEvidence> joins) {
    RelOptCluster cluster = physical.getCluster();
    cluster.invalidateMetadataQuery();
    TaintCheck.physical(
        physical,
        ImmutableList.of(ORDERS),
        Map.of(),
        joins,
        RelMetadataQuery.instance(),
        cluster.getRexBuilder(),
        new chalk.planner.plan.ChalkRexExecutor(RexUtil.EXECUTOR));
  }

  // ---------------------------------------------------------------- clause 2

  @Test
  void a_rule_that_re_introduces_a_raw_reference_is_refused() throws Exception {
    Leaf leaf = leaf();
    RexBuilder rex = leaf.rexBuilder();
    // The breakage: an expression over the raw population-only column, which is exactly what a
    // rewrite that forgot to sanitise would leave behind.
    // Above the leaf's own projection, which is the only place a raw column may be read (§3.1).
    RelNode sanitised =
        LogicalProject.create(
            leaf.scan(),
            ImmutableList.<RelHint>of(),
            List.of(rex.makeInputRef(leaf.scan(), 3)),
            List.of("amount"),
            java.util.Set.<CorrelationId>of());
    RelNode broken =
        LogicalProject.create(
            sanitised,
            ImmutableList.<RelHint>of(),
            List.of(
                rex.makeCall(
                    SqlStdOperatorTable.MULTIPLY,
                    rex.makeInputRef(sanitised, 0),
                    rex.makeExactLiteral(BigDecimal.valueOf(2)))),
            List.of("doubled"),
            java.util.Set.<CorrelationId>of());

    assertThatThrownBy(() -> check(broken, Map.of()))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("population-only")
        .hasMessageContaining("also a bug");
  }

  /**
   * F86. A declared path computes its verdict in marker joins and projections of its own between
   * the leaf and the sanitiser (D265, §3.14), so the projection holding the sanitiser is no longer
   * the one "directly over the entitled leaf" the position rule of §3.10 clause 2 looks for. Here
   * that is one pass-through projection in the way, which is the smallest tree with the same
   * property. The sanitiser is still a sanitiser and the clause reads it by shape.
   */
  @Test
  void a_sanitiser_the_position_rule_cannot_see_is_read_as_one() throws Exception {
    Leaf leaf = leaf();
    RexBuilder rex = leaf.rexBuilder();
    RelNode between = passThrough(leaf, rex);
    RelNode sanitiser =
        LogicalProject.create(
            between,
            ImmutableList.<RelHint>of(),
            List.of(
                rex.makeCall(
                    SqlStdOperatorTable.CASE,
                    rex.makeCall(
                        SqlStdOperatorTable.EQUALS,
                        rex.makeInputRef(between, 0),
                        rex.makeExactLiteral(BigDecimal.ONE)),
                    rex.makeInputRef(between, 1),
                    rex.makeNullLiteral(between.getRowType().getFieldList().get(1).getType()))),
            List.of("amount"),
            java.util.Set.<CorrelationId>of());

    check(sanitiser, Map.of());
  }

  /**
   * The other half of the same shape, and what keeps it a check: a {@code CASE} whose
   * <em>condition</em> reads the raw value selects rows by it, which is the oracle clause 2 exists
   * to refuse, wherever the projection stands.
   */
  @Test
  void a_case_whose_condition_reads_the_raw_value_is_still_refused() throws Exception {
    Leaf leaf = leaf();
    RexBuilder rex = leaf.rexBuilder();
    RelNode between = passThrough(leaf, rex);
    RelNode broken =
        LogicalProject.create(
            between,
            ImmutableList.<RelHint>of(),
            List.of(
                rex.makeCall(
                    SqlStdOperatorTable.CASE,
                    rex.makeCall(
                        SqlStdOperatorTable.GREATER_THAN,
                        rex.makeInputRef(between, 1),
                        rex.makeExactLiteral(BigDecimal.TEN)),
                    rex.makeInputRef(between, 1),
                    rex.makeNullLiteral(between.getRowType().getFieldList().get(1).getType()))),
            List.of("amount"),
            java.util.Set.<CorrelationId>of());

    assertThatThrownBy(() -> check(broken, Map.of()))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("population-only")
        .hasMessageContaining("also a bug");
  }

  /** {@code org_id}, then the raw {@code amount}: the leaf's own projection, by position. */
  private static RelNode passThrough(Leaf leaf, RexBuilder rex) {
    return LogicalProject.create(
        leaf.scan(),
        ImmutableList.<RelHint>of(),
        List.of(rex.makeInputRef(leaf.scan(), 1), rex.makeInputRef(leaf.scan(), 3)),
        List.of("org_id", "amount"),
        java.util.Set.<CorrelationId>of());
  }

  // ---------------------------------------------------------------- clause 3

  @Test
  void a_dropped_row_predicate_conjunct_is_refused() throws Exception {
    Leaf leaf = leaf();
    RexBuilder rex = leaf.rexBuilder();
    // The breakage: the plan carries no filter at all, and the policy said org_id = 1.
    RexNode predicate =
        rex.makeCall(
            SqlStdOperatorTable.EQUALS,
            rex.makeInputRef(leaf.scan(), 1),
            rex.makeExactLiteral(BigDecimal.ONE));
    RelNode broken =
        LogicalProject.create(
            leaf.scan(),
            ImmutableList.<RelHint>of(),
            List.of(rex.makeInputRef(leaf.scan(), 0)),
            List.of("id"),
            java.util.Set.<CorrelationId>of());

    assertThatThrownBy(() -> check(broken, Map.of("main.orders", predicate)))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("does not survive to its consumer")
        .hasMessageContaining("also a bug");
  }

  @Test
  void a_row_predicate_the_plan_still_enforces_passes() throws Exception {
    Leaf leaf = leaf();
    RexBuilder rex = leaf.rexBuilder();
    RexNode predicate =
        rex.makeCall(
            SqlStdOperatorTable.EQUALS,
            rex.makeInputRef(leaf.scan(), 1),
            rex.makeExactLiteral(BigDecimal.ONE));
    RelNode filtered = org.apache.calcite.rel.logical.LogicalFilter.create(leaf.scan(), predicate);
    RelNode kept =
        LogicalProject.create(
            filtered,
            ImmutableList.<RelHint>of(),
            List.of(rex.makeInputRef(filtered, 0)),
            List.of("id"),
            java.util.Set.<CorrelationId>of());

    check(kept, Map.of("main.orders", predicate));
  }

  // ---------------------------------------------------------------- clause 4

  @Test
  void a_raw_redacted_column_smuggled_to_the_root_through_a_set_operation_is_refused()
      throws Exception {
    Leaf leaf = leaf();
    RexBuilder rex = leaf.rexBuilder();
    // The breakage: `note` is withheld, and here it reaches the root as itself — through a UNION,
    // which is the shape that would defeat a check that only looked at projections.
    RelNode branch =
        LogicalProject.create(
            leaf.scan(),
            ImmutableList.<RelHint>of(),
            List.of(rex.makeInputRef(leaf.scan(), 4)),
            List.of("note"),
            java.util.Set.<CorrelationId>of());
    RelNode broken = LogicalUnion.create(List.of(branch, branch), true);

    assertThatThrownBy(() -> check(broken, Map.of()))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("redacted column")
        .hasMessageContaining("also a bug");
  }

  // ---------------------------------------------------------------- clause 1

  @Test
  void an_entitled_scan_the_pass_did_not_wrap_is_refused() throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            TENANCY,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            BoundContext.of(TenancyCatalogs.auditor(1)))) {
      // The tree as SqlToRelConverter leaves it: an entitled scan nothing has wrapped.
      RelNode unrewritten = pipeline.logical("SELECT id FROM orders");
      assertThatThrownBy(() -> TaintCheck.logical(unrewritten))
          .isInstanceOf(PolicyException.class)
          .hasMessageContaining("survived the entitlement rewrite unwrapped")
          .hasMessageContaining("also a bug");
    }
  }

  @Test
  void the_check_passes_on_every_plan_the_pass_produced() throws Exception {
    // The other half of the theory: the clauses must not refuse a plan the rewrite built, which is
    // what every other test in this package exercises by planning at all.
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            TENANCY,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            BoundContext.of(TenancyCatalogs.auditor(1)))) {
      assertThat(pipeline.plan("SELECT org_id, SUM(amount) FROM orders GROUP BY org_id", true)
              .entitledLeaves())
          .hasSize(1);
    }
  }
}
