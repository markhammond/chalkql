package chalk.planner;

import static chalk.planner.TestCatalogs.column;
import static chalk.planner.TestCatalogs.type;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.ColumnEntitlement;
import chalk.ir.v1.Disclosure;
import chalk.ir.v1.ForeignKey;
import chalk.ir.v1.InheritedVisibility;
import chalk.ir.v1.QueryLanguage;
import chalk.ir.v1.RowCountKind;
import chalk.ir.v1.Schema;
import chalk.ir.v1.SourceCapabilities;
import chalk.ir.v1.SourceKind;
import chalk.ir.v1.StepDirection;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableEntitlement;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.UniqueKey;
import chalk.ir.v1.VisibilityStep;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.InvalidCatalogException;
import java.util.function.UnaryOperator;
import org.junit.jupiter.api.Test;

/**
 * What registration refuses of a declared path (docs/design/38-existential-visibility.md §3, D265).
 *
 * <p>Three tables play three parts: {@code orders} is the target, {@code order_items} the bridge
 * whose rows carry the relationship and contribute existence and nothing else, and {@code vendors}
 * the endpoint where the kind is held directly. The shape checks and the vocabulary check are two
 * different things and both live here: every step goes over a declared foreign key in the direction
 * it claims, the shape is one §1 admits, the bridge's key columns carry no rule; and the endpoint
 * predicate is boolean over the <em>endpoint's</em> own row, with the target out of scope.
 *
 * <p>The catalog is this test's own and not the shared fixture, because what is under test is
 * registration alone: none of these catalogs is ever planned.
 */
class PathRegistrationTest {

  private static void register(CatalogContext catalog) {
    new CatalogRegistry().register(catalog);
  }

  // ------------------------------------------------------------------ the catalog

  /** The endpoint: the kind is held here, and its own predicate is what the path folds. */
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

  /** The bridge: a key to the target, a key to the endpoint, and one value column. */
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
        .build();
  }

  private static VisibilityStep up(String table, int from, int to) {
    return VisibilityStep.newBuilder()
        .setTable(table)
        .setFromColumn(from)
        .setToColumn(to)
        .setDirection(StepDirection.STEP_DIRECTION_TO_CHILD)
        .build();
  }

  private static VisibilityStep down(String table, int from, int to) {
    return VisibilityStep.newBuilder()
        .setTable(table)
        .setFromColumn(from)
        .setToColumn(to)
        .setDirection(StepDirection.STEP_DIRECTION_TO_PARENT)
        .build();
  }

  private static InheritedVisibility path() {
    return InheritedVisibility.newBuilder()
        .setKind("vendor")
        .addSteps(up("order_items", 0, 1))
        .addSteps(down("vendors", 2, 0))
        .setEndpointPredicate("id IN (@ctx.vendor_vendor)")
        .setEndpointTable("vendors")
        .build();
  }

  /**
   * The target: direct in one kind, related in the other, and a column rule for the related
   * perspective written over the endpoint's own column, as §2 lets it be.
   */
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
                .setRowPredicate("org_id IN (@ctx.org_manager)")
                .setDescriptorHash("11112222333344441111222233334444")
                .addInherited(path())
                .addColumns(
                    ColumnEntitlement.newBuilder()
                        .setColumn(2)
                        .addRules(
                            chalk.ir.v1.DisclosureRule.newBuilder()
                                .setWhen("vendors.id IN (@ctx.vendor_vendor)")
                                .setThen(Disclosure.DISCLOSURE_NONE))
                        .setOtherwise(Disclosure.DISCLOSURE_NONE))
                .build())
        .build();
  }

  private static CatalogContext catalog(Table orders, Table orderItems, Table vendors) {
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
                .addTables(orders)
                .addTables(orderItems)
                .addTables(vendors))
        .build();
  }

  /** The example, with the target's path edited. */
  private static CatalogContext target(UnaryOperator<InheritedVisibility.Builder> edit) {
    Table.Builder table = orders().toBuilder();
    table.setEntitlement(
        table
            .getEntitlement()
            .toBuilder()
            .clearInherited()
            .addInherited(edit.apply(path().toBuilder()).build())
            .build());
    return catalog(table.build(), orderItems(), vendors());
  }

  // ------------------------------------------------------------------ the shape

  @Test
  void the_example_registers() {
    register(catalog(orders(), orderItems(), vendors()));
  }

  @Test
  void a_path_with_no_step_is_refused() {
    assertThatThrownBy(() -> register(target(InheritedVisibility.Builder::clearSteps)))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("has no step");
  }

  @Test
  void a_step_to_a_table_the_catalog_does_not_hold_is_refused() {
    assertThatThrownBy(
            () ->
                register(
                    target(
                        p ->
                            p.clearSteps()
                                .addSteps(up("order_lines", 0, 1))
                                .setEndpointTable("order_lines"))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("main.order_lines")
        .hasMessageContaining("which this catalog does not hold");
  }

  /** The direction is checked, not inferred (§1, §3). */
  @Test
  void a_step_whose_direction_the_foreign_keys_do_not_support_is_refused() {
    assertThatThrownBy(
            () ->
                register(
                    target(
                        p ->
                            p.clearSteps()
                                .addSteps(down("order_items", 0, 0))
                                .addSteps(down("vendors", 2, 0)))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("goes down from 'orders' to 'order_items'")
        .hasMessageContaining("declares no foreign key");
  }

  @Test
  void a_join_onto_something_that_is_not_a_declared_unique_key_is_refused() {
    assertThatThrownBy(
            () ->
                register(
                    target(
                        p ->
                            p.clearSteps()
                                .addSteps(up("order_items", 1, 1))
                                .addSteps(down("vendors", 2, 0)))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("is not a declared unique key");
  }

  /**
   * This run admits an inherited path of any length and a related path of one up-step followed by
   * down-steps; every other shape is refused rather than built (§1, §9).
   */
  @Test
  void a_second_up_step_is_refused() {
    assertThatThrownBy(
            () ->
                register(
                    target(
                        p ->
                            p.clearSteps()
                                .addSteps(up("order_items", 0, 1))
                                .addSteps(up("order_items", 0, 1))
                                .setEndpointTable("order_items"))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("goes up")
        .hasMessageContaining("refused rather than built");
  }

  @Test
  void an_endpoint_the_last_step_does_not_reach_is_refused() {
    assertThatThrownBy(
            () ->
                register(target(p -> p.clearSteps().addSteps(up("order_items", 0, 1)))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("says its endpoint is 'vendors'")
        .hasMessageContaining("arrives at 'order_items'");
  }

  @Test
  void a_path_with_no_endpoint_predicate_is_refused() {
    assertThatThrownBy(() -> register(target(p -> p.setEndpointPredicate(""))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("carries no endpoint predicate");
  }

  @Test
  void a_step_back_to_the_target_is_refused() {
    assertThatThrownBy(
            () ->
                register(
                    target(
                        p ->
                            p.clearSteps()
                                .addSteps(up("order_items", 0, 1))
                                .addSteps(down("orders", 1, 0))
                                .setEndpointTable("orders"))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("returns to this table itself");
  }

  /**
   * The bridge's two key columns are read raw and disclosed to nobody, exactly as a parent's key is
   * left FULL under D227 — so a rule over one is a contradiction (§3).
   */
  @Test
  void a_protected_bridge_key_is_refused() {
    Table bridge =
        orderItems().toBuilder()
            .setEntitlement(
                TableEntitlement.newBuilder()
                    .setRowPredicate("order_id IN (@ctx.order_ids)")
                    .setDescriptorHash("2222333344445555222233334444555")
                    .addColumns(
                        ColumnEntitlement.newBuilder()
                            .setColumn(2)
                            .setOtherwise(Disclosure.DISCLOSURE_NONE))
                    .build())
            .build();

    assertThatThrownBy(() -> register(catalog(orders(), bridge, vendors())))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("main.order_items.vendor_id")
        .hasMessageContaining("is protected by a rule");
  }

  /**
   * A bridge that is itself entitled is ordinary — its own policy governs a statement's own
   * occurrence of it — as long as the two key columns are left alone (§0).
   */
  @Test
  void an_entitled_bridge_whose_keys_are_full_registers() {
    Table bridge =
        orderItems().toBuilder()
            .setEntitlement(
                TableEntitlement.newBuilder()
                    .setRowPredicate("order_id IN (@ctx.order_ids)")
                    .setDescriptorHash("2222333344445555222233334444555")
                    .addColumns(
                        ColumnEntitlement.newBuilder()
                            .setColumn(3)
                            .setOtherwise(Disclosure.DISCLOSURE_NONE))
                    .build())
            .build();

    register(catalog(orders(), bridge, vendors()));
  }

  // ------------------------------------------------------------------ the vocabulary

  /**
   * The endpoint predicate is over the <em>endpoint's</em> own row and the context (§2): the target
   * is out of scope, because the pass folds the predicate at the far end of the chain, where the
   * target's columns are not.
   */
  @Test
  void an_endpoint_predicate_over_the_targets_columns_is_refused() {
    assertThatThrownBy(() -> register(target(p -> p.setEndpointPredicate("org_id > 0"))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("inherited[0].endpoint_predicate")
        .hasMessageContaining("Column 'org_id' not found");
  }

  @Test
  void an_endpoint_predicate_that_is_not_boolean_is_refused() {
    assertThatThrownBy(() -> register(target(p -> p.setEndpointPredicate("id"))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("is not boolean");
  }

  /**
   * The target's rule conditions may name the declared endpoint's columns and nothing else, exactly
   * as a child's may name its parent's (§2, §3): a table that is not on any declared path does not
   * resolve.
   */
  @Test
  void a_rule_condition_naming_a_table_that_is_not_the_endpoint_is_refused() {
    Table.Builder table = orders().toBuilder();
    table.setEntitlement(
        table
            .getEntitlement()
            .toBuilder()
            .clearColumns()
            .addColumns(
                ColumnEntitlement.newBuilder()
                    .setColumn(2)
                    .addRules(
                        chalk.ir.v1.DisclosureRule.newBuilder()
                            .setWhen("order_items.quantity > 0")
                            .setThen(Disclosure.DISCLOSURE_NONE))
                    .setOtherwise(Disclosure.DISCLOSURE_NONE))
            .build());

    assertThatThrownBy(() -> register(catalog(table.build(), orderItems(), vendors())))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("order_items");
  }

  /** The cycle rule is over the visibility-dependency graph: target → endpoint (§3). */
  @Test
  void a_chain_of_derived_visibility_that_returns_to_a_table_is_refused() {
    Table endpoint =
        vendors().toBuilder()
            .setEntitlement(
                TableEntitlement.newBuilder()
                    .setDescriptorHash("aaaabbbbccccddddaaaabbbbccccdddd")
                    .addThrough(
                        chalk.ir.v1.ParentVisibility.newBuilder()
                            .setColumn(0)
                            .setParentTable("orders")
                            .setParentColumn(0)
                            .build())
                    .build())
            .build();

    assertThatThrownBy(() -> register(catalog(orders(), orderItems(), endpoint)))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("the chain of derived visibility returns to");
  }
}
