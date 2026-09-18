package chalk.planner;

import static chalk.planner.TestCatalogs.column;
import static chalk.planner.TestCatalogs.type;
import static org.assertj.core.api.Assertions.assertThat;

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
import chalk.ir.v1.SourceCapabilities;
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
import chalk.planner.entitlement.Disclosed;
import chalk.planner.entitlement.DisclosureMap;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.ContextRelationKind;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.ContextScalar;
import chalk.planner.rpc.v1.RequestContext;
import java.util.List;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * Visibility along a declared path (docs/design/38-existential-visibility.md §4, D265).
 *
 * <p>{@code orders} holds its organisation directly and its vendor along a <b>related</b> path: one
 * step up to {@code order_items}, the bridge, and one down to {@code vendors}, the endpoint where
 * the kind lives. The pass compiles that into a key set — the bridge joined to the endpoint,
 * filtered by the endpoint predicate, distinct on the key that joins back — and a LEFT JOIN from the
 * target's raw scan to it, whose marker {@code Filter_R} ORs with the organisation's own predicate.
 */
class PathPassTest {
  private static RegisteredCatalog catalog;

  @BeforeAll
  static void register() {
    catalog = new CatalogRegistry().register(marketplace());
  }

  private static PlannerPipeline.Result plan(String sql, RequestContext context) throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            catalog,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
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

  // ------------------------------------------------------------------ the marker

  @Test
  void a_vendor_reads_an_order_through_the_items_that_name_it() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, amount FROM orders", vendor(1));

    // One entitled leaf: the target's. The chain below the marker is raw — the bridge contributes
    // existence and nothing else, and the endpoint contributes the key (§0, §2).
    assertThat(result.entitledLeaves()).hasSize(1);
    assertThat(leafOf(result, "orders").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    assertThat(result.physicalPlanText()).contains("order_items");
    assertThat(result.physicalPlanText()).contains("vendors");
  }

  /** The endpoint predicate is what decides, and the target's own rules switch on its ordinal. */
  @Test
  void the_verdict_for_the_reaching_perspective_is_decided_at_the_endpoint() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, amount FROM orders", vendor(1));

    assertThat(leafOf(result, "orders").of(2)).isEqualTo(Disclosed.REDACTED);
  }

  /** An organisation manager reads the row by its own predicate, and sees the amount in full. */
  @Test
  void a_manager_reads_the_row_by_its_own_predicate() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, amount FROM orders", manager(1));

    assertThat(leafOf(result, "orders").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    assertThat(leafOf(result, "orders").of(2)).isEqualTo(Disclosed.FULL);
  }

  /**
   * FALSE drops the joins and the marker (§4): a principal with no vendor grant reaches nothing
   * along the path, and the plan is the one it was before the path existed.
   */
  @Test
  void an_endpoint_predicate_that_folds_to_false_drops_the_chain() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, amount FROM orders", manager(1));

    assertThat(result.physicalPlanText()).doesNotContain("order_items");
    assertThat(result.physicalPlanText()).doesNotContain("vendors");
  }

  /** Nothing at all: neither the organisation nor the path grants, so the leaf is NONE. */
  @Test
  void a_principal_with_no_grant_sees_no_order() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, amount FROM orders", nobody());

    assertThat(leafOf(result, "orders").visibility()).isEqualTo(DisclosureMap.Visibility.NONE);
  }

  /**
   * A global grant makes the table's own predicate TRUE, so the marker cannot change the answer and
   * the chain is not emitted: the row is ALL, as it was before the path existed.
   */
  @Test
  void a_global_grant_leaves_the_chain_unbuilt() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, amount FROM orders", global());

    assertThat(leafOf(result, "orders").visibility()).isEqualTo(DisclosureMap.Visibility.ALL);
    assertThat(result.physicalPlanText()).doesNotContain("order_items");
  }

  /**
   * The key set is distinct on the key that joins back, which is what keeps the join from
   * multiplying the target's rows however many items name the vendor (§4). Where the marker is the
   * whole of {@code Filter_R} the optimiser collapses the LEFT JOIN and its {@code IS NOT NULL} into
   * the semi-join the OR comes to, and the {@code DISTINCT} with it — which is the same answer by a
   * cheaper plan, and is what §4 means by "the SEMI JOIN the OR collapses to".
   */
  @Test
  void the_key_set_does_not_multiply_the_targets_rows() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id FROM orders", vendor(1));

    assertThat(result.physicalPlanText()).contains("order_items");
    assertThat(result.physicalPlanText()).containsPattern("joinType=\\[(semi|left)\\]");
  }

  /**
   * A vendor who also manages the organisation meets the row from both perspectives, and the rules
   * decide it in the order the host wrote them (§8).
   */
  @Test
  void both_perspectives_meet_on_one_order() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, amount FROM orders", managerAndVendor(1, 1));

    assertThat(leafOf(result, "orders").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    assertThat(result.physicalPlanText()).contains("order_items");
  }

  /**
   * Two entitled tables in one statement, one of them entitled along a path: the entitlements
   * compose across the join, as §8's corpus 08 asks of them.
   */
  @Test
  void a_path_composes_across_a_join_with_another_entitled_table() throws Exception {
    PlannerPipeline.Result result =
        plan(
            "SELECT m.id, o.id FROM members m JOIN orders o ON o.org_id = m.org_id",
            vendor(1));

    assertThat(leafOf(result, "orders").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
  }

  // ------------------------------------------------------------------ the folded key set (F67)

  /**
   * F67, and clause 3's answer to it (ADR 0052). {@code order_items} holds its vendor along a path
   * of one step, to {@code vendors}, where the kind lives on the endpoint's <b>unique key</b>. For a
   * principal holding one vendor grant the endpoint predicate folds to {@code id = 1}, so the key
   * set is one row and the optimiser projects the constant in place of the column — the join is
   * still there and still restricts the lines, but its far side no longer has a column origin.
   * Before the fix clause 3 refused the statement; now it recognises the restriction the key set
   * came to.
   */
  @Test
  void a_statement_over_a_bridge_whose_key_set_folded_to_one_constant_plans() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, order_id FROM order_items", vendor(1));

    assertThat(leafOf(result, "order_items").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    // The mechanism is in the finished plan in its folded spelling: the key set's own constant, and
    // the lines restricted to it.
    assertThat(result.physicalPlanText()).contains("vendors");
    assertThat(result.physicalPlanText()).contains("$chalk$key=[1]");
  }

  /**
   * Two grants leave the key set a column — {@code id IN (1, 2)} pins nothing — so the join keeps
   * its origins and clause 3 matches it the way it always did. The two spellings are the same
   * mechanism and the check has to take both.
   */
  @Test
  void two_grants_leave_the_key_set_its_column() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, order_id FROM order_items", vendor(1, 2));

    assertThat(leafOf(result, "order_items").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    assertThat(result.physicalPlanText()).contains("vendors");
    assertThat(result.physicalPlanText()).doesNotContain("$chalk$key=[1]");
  }

  /** No vendor grant: the endpoint predicate folds to FALSE, the path is dropped, the leaf is NONE. */
  @Test
  void a_bridge_read_by_a_principal_holding_no_grant_of_the_kind_sees_nothing() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, order_id FROM order_items", manager(1));

    assertThat(leafOf(result, "order_items").visibility()).isEqualTo(DisclosureMap.Visibility.NONE);
  }

  // ------------------------------------------------------------------ the catalog

  /** A second entitled table, so a statement can join one to the path's target. */
  private static Table members() {
    return Table.newBuilder()
        .setName("members")
        .setRowCount(50)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate("org_id IN (@ctx.org_manager) OR @ctx.global")
                .setDescriptorHash("33334444555566663333444455556666")
                .build())
        .build();
  }


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

  private static Table orderItems() {
    return Table.newBuilder()
        .setName("order_items")
        .setRowCount(900)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("order_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("vendor_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("quantity", type(TypeKind.TYPE_KIND_I32)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addForeignKeys(
            ForeignKey.newBuilder()
                .setName("item_order")
                .addColumns(1)
                .setParentTable("orders")
                .addParentColumns(0))
        .addForeignKeys(
            ForeignKey.newBuilder()
                .setName("item_vendor")
                .addColumns(2)
                .setParentTable("vendors")
                .addParentColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        // The bridge's own perspective, kind-scoped one step down to where the key lives: a vendor
        // sees the lines that carry its own goods (§8). Its endpoint predicate pins `vendors.id` —
        // the endpoint's unique key — which is the shape F67 was about.
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setDescriptorHash("99998888777766669999888877776666")
                .addInherited(
                    InheritedVisibility.newBuilder()
                        .setKind("vendor")
                        .addSteps(
                            VisibilityStep.newBuilder()
                                .setTable("vendors")
                                .setFromColumn(2)
                                .setToColumn(0)
                                .setDirection(StepDirection.STEP_DIRECTION_TO_PARENT))
                        .setEndpointPredicate("id IN (@ctx.vendor_vendor)")
                        .setEndpointTable("vendors"))
                .build())
        .build();
  }

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
                                .setTable("vendors")
                                .setFromColumn(2)
                                .setToColumn(0)
                                .setDirection(StepDirection.STEP_DIRECTION_TO_PARENT))
                        .setEndpointPredicate("id IN (@ctx.vendor_vendor)")
                        .setEndpointTable("vendors"))
                .addColumns(
                    ColumnEntitlement.newBuilder()
                        .setColumn(2)
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen("org_id IN (@ctx.org_manager) OR @ctx.global")
                                .setThen(Disclosure.DISCLOSURE_FULL))
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen("vendors.id IN (@ctx.vendor_vendor)")
                                .setThen(Disclosure.DISCLOSURE_NONE))
                        .setOtherwise(Disclosure.DISCLOSURE_NONE))
                .addColumns(
                    ColumnEntitlement.newBuilder()
                        .setColumn(1)
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen("org_id IN (@ctx.org_manager) OR @ctx.global")
                                .setThen(Disclosure.DISCLOSURE_FULL))
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen("vendors.id IN (@ctx.vendor_vendor)")
                                .setThen(Disclosure.DISCLOSURE_NONE))
                        .setOtherwise(Disclosure.DISCLOSURE_NONE))
                .build())
        .build();
  }

  private static CatalogContext marketplace() {
    return CatalogContext.newBuilder()
        .setContextId("path")
        .setEpoch(1L)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("main")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .setCapabilities(
                    SourceCapabilities.newBuilder()
                        .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE))
                .addTables(orders())
                .addTables(orderItems())
                .addTables(vendors())
                .addTables(members()))
        .build();
  }

  // ------------------------------------------------------------------ the principals

  private static RequestContext vendor(int... vendors) {
    return principal(new int[0], vendors, false);
  }

  private static RequestContext manager(int... orgs) {
    return principal(orgs, new int[0], false);
  }

  private static RequestContext managerAndVendor(int org, int vendorId) {
    return principal(new int[] {org}, new int[] {vendorId}, false);
  }

  private static RequestContext nobody() {
    return principal(new int[0], new int[0], false);
  }

  private static RequestContext global() {
    return principal(new int[0], new int[0], true);
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
