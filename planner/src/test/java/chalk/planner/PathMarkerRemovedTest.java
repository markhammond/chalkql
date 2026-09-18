package chalk.planner;

import static chalk.planner.TestCatalogs.column;
import static chalk.planner.TestCatalogs.type;
import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.CatalogContext;
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
import chalk.planner.entitlement.DisclosureMap;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.ContextRelationKind;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.RequestContext;
import java.util.List;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * F69: the key-set join the optimiser <b>removed</b>, and what clause 3 accepts in its place
 * (ADR 0054; docs/design/38-existential-visibility.md §4, §7).
 *
 * <p>ADR 0052 taught clause 3 the <em>folded</em> form of its own join — a key set of one row, whose
 * key is projected as the constant the endpoint predicate pinned it to. This is one step further on:
 * the join is not folded, it is <b>gone</b>. A target that holds a tenancy directly <em>and</em>
 * along a path has {@code Filter_R = own OR marker} (§4); where the statement's own predicate
 * implies {@code own}, that disjunction is true of every row the statement can reach whatever the
 * marker says, the optimiser proves it and deletes the key set and its join, and clause 3 asked for
 * a join no correct plan has any reason to hold.
 *
 * <p>This catalog is clause (h)'s {@code orders} in miniature: the organisation on the row, the
 * vendor along a {@code Related} path of two steps, and the statement of {@code m7-adversarial} 50.
 */
class PathMarkerRemovedTest {
  private static RegisteredCatalog catalog;

  /** {@code m7-adversarial} 50, which is where F69 was found. */
  private static final String STATEMENT =
      "SELECT id, org_id, region_id FROM orders WHERE org_id = 1 AND region_id <> 2";

  @BeforeAll
  static void register() {
    catalog = new CatalogRegistry().register(marketplace());
  }

  // ------------------------------------------------------------------ the acceptance

  /**
   * F69's own statement, for a principal holding both grants. The statement's predicate implies the
   * organisation disjunct of {@code Filter_R}, the optimiser removes the marker's join, and the
   * occurrence is accepted because the plan's own predicates say the marker could not have changed
   * the answer.
   */
  @Test
  void a_statement_whose_predicate_implies_the_leafs_own_restriction_plans() throws Exception {
    PlannerPipeline.Result result = plan(STATEMENT, vendorAndOrg(1, 1));

    assertThat(leafOf(result, "orders").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    // The mechanism is gone because the optimiser proved it redundant, not because the leaf is gone.
    assertThat(result.physicalPlanText()).contains("orders");
    assertThat(result.physicalPlanText()).doesNotContain("items");
  }

  /**
   * The same statement for the vendor-only principal, whose organisation membership folds to FALSE
   * so that the marker is the whole of {@code Filter_R}. Nothing implies anything here, and the join
   * has to be — and is — in the plan.
   */
  @Test
  void the_vendor_only_principal_keeps_the_join() throws Exception {
    PlannerPipeline.Result result = plan(STATEMENT, vendorOnly(1));

    assertThat(leafOf(result, "orders").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    assertThat(result.physicalPlanText()).contains("order_items");
    assertThat(result.physicalPlanText()).contains("items");
  }

  /**
   * And the acceptance is not a licence: a statement that restricts nothing keeps the join, because
   * nothing implies the organisation disjunct and the marker is what decides the row.
   */
  @Test
  void a_statement_that_implies_nothing_keeps_the_join() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, org_id FROM orders", vendorAndOrg(1, 1));

    assertThat(result.physicalPlanText()).contains("order_items");
  }

  // ------------------------------------------------------------------ the plan, the principals

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

  // ------------------------------------------------------------------ the catalog

  private static Table orders() {
    return Table.newBuilder()
        .setName("orders")
        .setRowCount(200)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("member_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("amount", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("created_by", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("region_id", type(TypeKind.TYPE_KIND_I32)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                // The organisation on the row, and the resource owner beside it: the shape clause
                // (h)'s `orders` has before the path is added to it.
                .setRowPredicate(
                    "org_id IN (@ctx.org_manager) OR created_by IN (@ctx.owner_self)")
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
                                .setFromColumn(2)
                                .setToColumn(0)
                                .setDirection(StepDirection.STEP_DIRECTION_TO_PARENT))
                        // `items.vendor_id` is not unique, so nothing is pinned and F67's folded
                        // form does not apply — this is the join removed outright.
                        .setEndpointPredicate("vendor_id IN (@ctx.vendor_vendor)")
                        .setEndpointTable("items"))
                .build())
        .build();
  }

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
                .setName("line_order")
                .addColumns(1)
                .setParentTable("orders")
                .addParentColumns(0))
        .addForeignKeys(
            ForeignKey.newBuilder()
                .setName("line_item")
                .addColumns(2)
                .setParentTable("items")
                .addParentColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  private static Table items() {
    return Table.newBuilder()
        .setName("items")
        .setRowCount(300)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("vendor_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("name", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  private static CatalogContext marketplace() {
    return CatalogContext.newBuilder()
        .setContextId("marker")
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
                .addTables(items()))
        .build();
  }

  // ------------------------------------------------------------------ the principals

  private static RequestContext vendorOnly(int vendor) {
    return principal(new int[0], new int[] {vendor});
  }

  private static RequestContext vendorAndOrg(int org, int vendor) {
    return principal(new int[] {org}, new int[] {vendor});
  }

  private static RequestContext principal(int[] orgs, int[] vendors) {
    return RequestContext.newBuilder()
        .addRelations(list("owner_self", new int[0]))
        .addRelations(list("org_manager", orgs))
        .addRelations(list("vendor_vendor", vendors))
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
