package chalk.planner;

import static chalk.planner.TestCatalogs.column;
import static chalk.planner.TestCatalogs.type;
import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.Association;
import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.ColumnEntitlement;
import chalk.ir.v1.Disclosure;
import chalk.ir.v1.DisclosureRule;
import chalk.ir.v1.Expr;
import chalk.ir.v1.Field;
import chalk.ir.v1.ForeignKey;
import chalk.ir.v1.InheritedVisibility;
import chalk.ir.v1.Literal;
import chalk.ir.v1.QueryLanguage;
import chalk.ir.v1.RowCountKind;
import chalk.ir.v1.RowType;
import chalk.ir.v1.Schema;
import chalk.ir.v1.SourceKind;
import chalk.ir.v1.StepDirection;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableEntitlement;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.UniqueKey;
import chalk.ir.v1.VirtualRow;
import chalk.ir.v1.VisibilityStep;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.DisclosureMap;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.JoinPolicy;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import chalk.planner.rpc.v1.ContextRelationKind;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.ContextScalar;
import chalk.planner.rpc.v1.RequestContext;
import java.util.List;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * Design 38 §8's two-source layout, planned (F84, docs/design/38-existential-visibility.md §5).
 *
 * <p>The target and the bridge are in one source and the endpoint in another: {@code orders} and
 * {@code order_items} in {@code warehouse}, a remote SQL source, and {@code items} and
 * {@code vendors} in {@code catalogue}, an in-process one. The step from the line to the item is
 * declared by a cross-source {@link Association} rather than by a foreign key, because a foreign key
 * is one source's claim about a table of its own schema (docs/design/45-typed-tenancy-surface.md §3,
 * D270).
 *
 * <p>What §5 asks for is <b>two hops</b>: the endpoint's keys that satisfy the predicate travel to
 * the bridge's source as a key set, and the key set's keys travel on to the target's source as a
 * second one — each of them one of M5's strategies at the planner's own costing. Here the target and
 * the bridge share a source, so the second hop is LOCAL and the semi-join pushes as one remote query;
 * the first hop is the exchange, and the measurement this class records is which of
 * {@code CrossSourceJoinRule}'s strategies cost chooses for it.
 */
class CrossSourcePathTest {
  private static RegisteredCatalog catalog;

  /** What a source that takes no SQL at all would make of the same layout. */
  private static RegisteredCatalog local;

  @BeforeAll
  static void register() {
    catalog = new CatalogRegistry().register(twoSources(true));
    local = new CatalogRegistry().register(twoSources(false));
  }

  private static PlannerPipeline.Result plan(String sql, RequestContext context) throws Exception {
    return plan(catalog, sql, context);
  }

  private static PlannerPipeline.Result plan(
      RegisteredCatalog registered, String sql, RequestContext context) throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            registered,
            PushdownPolicy.full(),
            SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            JoinPolicy.DEFAULT,
            BoundContext.of(context),
            PolicyOptions.DEFAULTS)) {
      return pipeline.plan(sql, true);
    }
  }

  private static DisclosureMap leafOf(PlannerPipeline.Result result, String table) {
    for (DisclosureMap leaf : result.entitledLeaves()) {
      if (leaf.table().equals(table)) {
        return leaf;
      }
    }
    throw new AssertionError("no entitled leaf for " + table + " in " + result.entitledLeaves());
  }

  // ------------------------------------------------------------------ the two hops

  /**
   * The chain is built across the two sources and both hops are in the plan: the endpoint's scan is
   * in {@code catalogue} and the bridge's in {@code warehouse}, and what carries the first source's
   * keys to the second is the key-set exchange M5 already had.
   */
  @Test
  void a_path_across_two_sources_builds_its_chain_and_the_exchange_carries_the_keys()
      throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, amount FROM warehouse.orders", vendor(1));

    assertThat(leafOf(result, "orders").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    String plan = result.physicalPlanText();
    // The first hop, costed: the endpoint's visible keys leave `catalogue` and arrive in
    // `warehouse` as the source's own `IN` list, which is F45's exchange under one of M5's
    // strategies — LOOKUP here, where the driving side's estimate is a measured one.
    assertThat(plan).contains("ChalkLookupJoin");
    assertThat(plan).contains("strategy=[JOIN_STRATEGY_LOOKUP]");
    assertThat(plan).contains("SourceFilter(condition=[CHALK_KEY_SET_IN($2)])");
    // Both chain scans are the mechanism's own, and each is in its own source.
    assertThat(plan).contains("ChalkTableScan(table=[[$chalk$correlation, catalogue, items]]");
    assertThat(plan).contains("SourceScan(table=[[$chalk$correlation, warehouse, order_items]]");
  }

  /**
   * And the chain's scan of a table is never the <b>statement's</b> own (F84). A {@code TableScan}'s
   * digest is its qualified name and its row type and nothing else, so an entitled leaf and a raw
   * chain scan of one table are otherwise the same expression wherever the leaf's rules widen
   * nothing — and either could then be served the other's read. The reserved segment is what keeps
   * the two apart by construction.
   */
  @Test
  void the_chains_scan_is_never_the_statements_own() throws Exception {
    PlannerPipeline.Result result =
        plan(
            local,
            "SELECT o.id, i.id FROM warehouse.orders o"
                + " JOIN warehouse.order_items i ON i.order_id = o.id",
            vendor(1));

    String plan = result.physicalPlanText();
    // The statement's own occurrence, entitled as the line's own policy says …
    assertThat(plan).contains("ChalkTableScan(table=[[warehouse, order_items]]");
    // … and the mechanism's, which discloses nothing and is not reachable from it.
    assertThat(plan).contains("ChalkTableScan(table=[[$chalk$correlation, warehouse, order_items]]");
  }

  /**
   * The second hop is LOCAL and stays so: the target and the bridge share a source, so the key set's
   * join back to the target is an ordinary join in {@code warehouse} — which is what §5 means by the
   * semi-join being pushed where the target and the bridge share a source.
   */
  @Test
  void the_second_hop_is_local_where_the_target_and_the_bridge_share_a_source() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id FROM warehouse.orders", vendor(1));

    String plan = result.physicalPlanText();
    // One exchange, not two: the key set's own keys are computed in the target's source already, so
    // the join back to the target is an ordinary local join and there is nothing for a second hop
    // to carry.
    assertThat(countOf(plan, "ChalkLookupJoin")).isEqualTo(1);
    assertThat(countOf(plan, "ChalkAdaptiveJoin")).isZero();
  }

  /** A second vendor grant leaves the key set a column rather than a pinned constant. */
  @Test
  void two_grants_leave_the_key_set_its_column() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id FROM warehouse.orders", vendor(1, 2));

    assertThat(leafOf(result, "orders").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    assertThat(result.physicalPlanText()).contains("items");
  }

  /** The endpoint decides the target's column verdict for the reaching perspective, as in one source. */
  @Test
  void the_verdict_for_the_reaching_perspective_is_decided_at_the_endpoint() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, amount FROM warehouse.orders", vendor(1));

    assertThat(leafOf(result, "orders").of(2)).isEqualTo(
        chalk.planner.entitlement.Disclosed.REDACTED);
  }

  /** FALSE drops the chain and with it the exchange: no vendor grant, no hop at all. */
  @Test
  void an_endpoint_predicate_that_folds_to_false_drops_the_chain_and_the_exchange()
      throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, amount FROM warehouse.orders", manager(1));

    assertThat(result.physicalPlanText()).doesNotContain("order_items");
    assertThat(result.physicalPlanText()).doesNotContain("items");
  }

  /** The bridge entitled along its own cross-source path: one step, up the association. */
  @Test
  void the_bridge_reads_its_own_vendor_across_the_association() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, order_id FROM warehouse.order_items", vendor(1));

    assertThat(leafOf(result, "order_items").visibility())
        .isEqualTo(DisclosureMap.Visibility.SOME);
    String plan = result.physicalPlanText();
    // §5's own sentence, in one hop: the endpoint's keys that satisfy the predicate travel to the
    // other source as a key set.
    assertThat(plan).contains("ChalkTableScan(table=[[$chalk$correlation, catalogue, items]]");
    assertThat(plan).contains("SourceFilter(condition=[CHALK_KEY_SET_IN($2)])");
  }

  /**
   * Two sources that take no query language at all: the chain is the same and every hop is LOCAL,
   * which is the POCO pair the corpus family runs first.
   */
  @Test
  void two_local_sources_build_the_same_chain_with_every_hop_local() throws Exception {
    PlannerPipeline.Result result =
        plan(local, "SELECT id, amount FROM warehouse.orders", vendor(1));

    assertThat(leafOf(result, "orders").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    String plan = result.physicalPlanText();
    assertThat(plan).contains("ChalkTableScan(table=[[$chalk$correlation, warehouse, order_items]]");
    assertThat(plan).contains("ChalkTableScan(table=[[$chalk$correlation, catalogue, items]]");
    // Nothing to ship a key set to, so every hop is LOCAL and the answer is the same.
    assertThat(plan).doesNotContain("ChalkLookupJoin");
    assertThat(plan).doesNotContain("CHALK_KEY_SET");
  }

  private static int countOf(String text, String needle) {
    int found = 0;
    for (int at = text.indexOf(needle); at >= 0; at = text.indexOf(needle, at + needle.length())) {
      found++;
    }
    return found;
  }

  // ------------------------------------------------------------------ the catalog

  private static Table vendors() {
    return Table.newBuilder()
        .setName("vendors")
        .setRowCount(20)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("name", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate("id IN (@ctx.vendor_vendor)")
                .setDescriptorHash("aaaabbbbccccddddaaaabbbbccccdddd")
                .build())
        .build();
  }

  /** The endpoint: the kind lives on the row, and it is in the other source. */
  private static Table items() {
    return Table.newBuilder()
        .setName("items")
        .setRowCount(400)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("vendor_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("name", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addForeignKeys(
            ForeignKey.newBuilder()
                .setName("item_vendor")
                .addColumns(1)
                .setParentTable("vendors")
                .addParentColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate("vendor_id IN (@ctx.vendor_vendor)")
                .setDescriptorHash("55556666777788885555666677778888")
                .build())
        .build();
  }

  /** The bridge, in the target's source, inheriting its vendor across the association. */
  private static Table orderItems() {
    return Table.newBuilder()
        .setName("order_items")
        .setRowCount(900)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("order_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("item_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("quantity", type(TypeKind.TYPE_KIND_I32)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addForeignKeys(
            ForeignKey.newBuilder()
                .setName("item_order")
                .addColumns(1)
                .setParentTable("orders")
                .addParentColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setDescriptorHash("99998888777766669999888877776666")
                .addInherited(
                    InheritedVisibility.newBuilder()
                        .setKind("vendor")
                        .addSteps(
                            VisibilityStep.newBuilder()
                                .setTable("items")
                                .setSchema("catalogue")
                                .setFromColumn(2)
                                .setToColumn(0)
                                .setDirection(StepDirection.STEP_DIRECTION_TO_PARENT))
                        .setEndpointPredicate("vendor_id IN (@ctx.vendor_vendor)")
                        .setEndpointTable("items")
                        .setEndpointSchema("catalogue"))
                .build())
        .build();
  }

  /** The target: its organisation on the row, its vendor two steps away and one source away. */
  private static Table orders() {
    return Table.newBuilder()
        .setName("orders")
        .setRowCount(200)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("amount", type(TypeKind.TYPE_KIND_I64)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate("org_id IN (@ctx.org_manager) OR @ctx.global")
                .setDescriptorHash("11112222333344441111222233334444")
                .addInherited(
                    InheritedVisibility.newBuilder()
                        .setKind("vendor")
                        .addSteps(
                            VisibilityStep.newBuilder()
                                .setTable("order_items")
                                .setFromColumn(0)
                                .setToColumn(1)
                                .setDirection(StepDirection.STEP_DIRECTION_TO_CHILD))
                        .addSteps(
                            VisibilityStep.newBuilder()
                                .setTable("items")
                                .setSchema("catalogue")
                                .setFromColumn(2)
                                .setToColumn(0)
                                .setDirection(StepDirection.STEP_DIRECTION_TO_PARENT))
                        .setEndpointPredicate("vendor_id IN (@ctx.vendor_vendor)")
                        .setEndpointTable("items")
                        .setEndpointSchema("catalogue"))
                .addColumns(
                    ColumnEntitlement.newBuilder()
                        .setColumn(2)
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen("org_id IN (@ctx.org_manager) OR @ctx.global")
                                .setThen(Disclosure.DISCLOSURE_FULL))
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen("items.vendor_id IN (@ctx.vendor_vendor)")
                                .setThen(Disclosure.DISCLOSURE_NONE))
                        .setOtherwise(Disclosure.DISCLOSURE_NONE))
                .build())
        .build();
  }

  /**
   * The layout: the target and the bridge in a remote SQL source, the endpoint and the table its own
   * kind lives on in an in-process one, and the association that states the step between them.
   *
   * @param remote whether {@code warehouse} takes SQL, which is what gives the first hop a strategy
   */
  private static CatalogContext twoSources(boolean remote) {
    Schema.Builder warehouse =
        Schema.newBuilder()
            .setSourceId("duckdb")
            .setName("warehouse")
            .addTables(orders())
            .addTables(orderItems());
    if (remote) {
      warehouse
          .setKind(SourceKind.SOURCE_KIND_REMOTE)
          .setDialect(TestCatalogs.duckDbProfile().getDialect())
          .setDialectProfile(TestCatalogs.duckDbProfile())
          .setCapabilities(TestCatalogs.fullSqlCapabilities().build());
    } else {
      warehouse
          .setKind(SourceKind.SOURCE_KIND_LOCAL)
          .setCapabilities(
              chalk.ir.v1.SourceCapabilities.newBuilder()
                  .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE));
    }

    return CatalogContext.newBuilder()
        .setContextId("f84")
        .setEpoch(1L)
        .addSchemas(warehouse)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("catalogue")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .setCapabilities(
                    chalk.ir.v1.SourceCapabilities.newBuilder()
                        .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE))
                .addTables(items())
                .addTables(vendors()))
        .addAssociations(
            Association.newBuilder()
                .setName("line_item")
                .setFromSchema("warehouse")
                .setFromTable("order_items")
                .setFromColumn("item_id")
                .setToSchema("catalogue")
                .setToTable("items")
                .setToColumn("id"))
        .build();
  }

  // ------------------------------------------------------------------ the principals

  private static RequestContext vendor(int... vendors) {
    return principal(new int[0], vendors, false);
  }

  private static RequestContext manager(int... orgs) {
    return principal(orgs, new int[0], false);
  }

  private static RequestContext principal(int[] orgs, int[] vendors, boolean everywhere) {
    return RequestContext.newBuilder()
        .addScalars(ContextScalar.newBuilder().setName("global").setValue(bool(everywhere)))
        .addRelations(list("org_manager", orgs))
        .addRelations(list("vendor_vendor", vendors))
        .build();
  }

  private static Expr bool(boolean value) {
    return Expr.newBuilder()
        .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_BOOL))
        .setLiteral(Literal.newBuilder().setBoolValue(value))
        .build();
  }

  private static Expr i32(int value) {
    return Expr.newBuilder()
        .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))
        .setLiteral(Literal.newBuilder().setI32Value(value))
        .build();
  }

  private static ContextRelationValue list(String name, int[] ids) {
    ContextRelationValue.Builder builder =
        ContextRelationValue.newBuilder()
            .setName(name)
            .setKind(ContextRelationKind.CONTEXT_RELATION_KIND_LIST)
            .setRowType(
                RowType.newBuilder()
                    .addFields(
                        Field.newBuilder()
                            .setName("id")
                            .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))));
    for (int id : ids) {
      builder.addRows(VirtualRow.newBuilder().addValues(i32(id)));
    }
    return builder.build();
  }

}
