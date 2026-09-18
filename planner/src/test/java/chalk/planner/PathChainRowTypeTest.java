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
import chalk.ir.v1.Plan;
import chalk.ir.v1.QueryLanguage;
import chalk.ir.v1.Rel;
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
import chalk.planner.entitlement.CorrelationRelOptTable;
import chalk.planner.entitlement.DisclosureMap;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.ir.IrVersionGate;
import chalk.planner.ir.RelToIr;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.ContextRelationKind;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.RequestContext;
import chalk.planner.types.TypeMapper;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * The row type a declared path's <b>chain</b> is scanned at (F68; docs/design/38-existential-
 * visibility.md §2, §4; ADR 0054).
 *
 * <p>A path's chain is read <b>raw</b> — the bridge contributes existence and the endpoint
 * contributes the key, and neither table's own entitlement is consulted (§0, §2). "Raw" is a claim
 * about the row as well as about the policy: the row the chain reads is the one the <em>source
 * stores</em>, and the descriptor converter already types every reference in the endpoint predicate
 * by that row, because it converts through the declared catalog (F58). The chain's tables have to
 * be resolved through the same tree, or the two halves of the mechanism disagree on one column's
 * nullability and the plan is broken before it leaves the sidecar.
 *
 * <p>This catalog is the smallest that shows it: {@code orders} holds its organisation directly and
 * a rule can withhold {@code orders.org_id}, so that column is nullable in the row a statement is
 * written over (D161, F58, ADR 0038) and NOT NULL in the row the source stores; {@code order_items}
 * inherits its organisation one step up to {@code orders}, so {@code orders} is the chain's own
 * base and the endpoint predicate is over the column the rule protects.
 */
class PathChainRowTypeTest {
  private static RegisteredCatalog catalog;

  @BeforeAll
  static void register() {
    catalog = new CatalogRegistry().register(protectedChain());
  }

  // ------------------------------------------------------------------ F68

  /**
   * F68. The chain's {@code Read} carries the row the <b>source stores</b>. Before the fix it
   * carried the disclosed row, whose {@code org_id} is nullable because a rule of {@code orders} can
   * withhold it — a nullability the source does not produce.
   */
  @Test
  void the_chains_read_carries_the_row_the_source_stores() throws Exception {
    Rel read = correlationRead(irOf("SELECT id FROM order_items", manager(1)));

    assertThat(read).as("the chain's own Read is in the plan").isNotNull();
    // The key it joins back on and the column the endpoint predicate reads, and neither of them
    // nullable: that is the row `orders` stores. Before the fix `org_id` arrived here as `org_id?`.
    assertThat(nullability(read.getRowType()))
        .as("the chain reads what the source stores, not what a statement sees")
        .isEqualTo("id, org_id");
  }

  /**
   * The same claim on the tree, where it is made: the handle the chain scans through is the
   * mechanism's own, and it publishes the declared row.
   */
  @Test
  void the_chains_scan_is_typed_by_the_declared_catalog() throws Exception {
    TableScan chain = correlationScan(plan("SELECT id FROM order_items", manager(1)));

    assertThat(chain).as("the chain's raw scan is in the plan").isNotNull();
    assertThat(CorrelationRelOptTable.isCorrelation(chain.getTable())).isTrue();
    assertThat(chain.getTable().getRowType().getField("org_id", false, false).getType().isNullable())
        .as("a column a rule can withhold is still NOT NULL beneath the mechanism")
        .isFalse();
  }

  /**
   * What F68 broke, stated as the client's own invariant: I-IR-4 asks that every {@code FieldRef}
   * carry the type of the input field it reads. The endpoint predicate is converted through the
   * declared catalog, so its references are typed {@code I32}; a chain resolved through the
   * disclosed tree handed them an {@code I32?} row beneath, and the plan was refused at the client
   * (<em>I32 does not match the input field $1 type (I32?)</em>) or at the source's scan contract.
   */
  @Test
  void every_reference_in_the_plan_carries_the_type_of_the_field_it_reads() throws Exception {
    List<String> mismatches = new ArrayList<>();
    fieldRefsAgree(irOf("SELECT id FROM order_items", manager(1)).getRoot(), mismatches);

    assertThat(mismatches).isEmpty();
  }

  /** And the statement's own view of the protected column is unchanged: still nullable (F58). */
  @Test
  void the_statements_own_row_type_still_widens_the_protected_column() throws Exception {
    RelNode physical = plan("SELECT id, org_id FROM orders", manager(1));

    assertThat(physical.getRowType().getField("org_id", false, false).getType().isNullable())
        .as("what the statement is handed")
        .isTrue();
  }

  /** And the bridge is readable: the chain restricts it, and the leaf says so. */
  @Test
  void a_statement_over_the_bridge_plans_along_the_path() throws Exception {
    PlannerPipeline.Result result = planned("SELECT id, order_id FROM order_items", manager(1));

    assertThat(leafOf(result, "order_items").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    assertThat(result.physicalPlanText()).contains("orders");
  }

  // ------------------------------------------------------------------ the plan, and the IR

  private static PlannerPipeline.Result planned(String sql, RequestContext context)
      throws Exception {
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

  private static RelNode plan(String sql, RequestContext context) throws Exception {
    return planned(sql, context).physical();
  }

  /** The plan as the client receives it, which is where I-IR-4 is decided. */
  private static Plan irOf(String sql, RequestContext context) throws Exception {
    PlannerPipeline.Result result = planned(sql, context);
    RelToIr toIr =
        new RelToIr(
            new TypeMapper(result.physical().getCluster().getTypeFactory()),
            result.physical().getCluster().getRexBuilder(),
            RelMetadataQuery.instance(),
            IrVersionGate.current());
    return toIr.toPlan(
        result.physical(), result.parameterRowType(), catalog.contextId(), catalog.epoch());
  }

  private static DisclosureMap leafOf(PlannerPipeline.Result result, String table) {
    for (DisclosureMap leaf : result.entitledLeaves()) {
      if (leaf.table().equals(table)) {
        return leaf;
      }
    }
    throw new AssertionError("no entitled leaf for " + table + " in " + result.entitledLeaves());
  }

  /** The first {@code Read} the mechanism marked as its own correlation (D265 §2, §7). */
  private static @org.checkerframework.checker.nullness.qual.Nullable Rel correlationRead(
      Plan plan) {
    return correlationRead(plan.getRoot());
  }

  private static @org.checkerframework.checker.nullness.qual.Nullable Rel correlationRead(Rel rel) {
    if (rel.getKindCase() == Rel.KindCase.READ && rel.getRead().getCorrelation()) {
      return rel;
    }
    for (Rel input : inputs(rel)) {
      Rel found = correlationRead(input);
      if (found != null) {
        return found;
      }
    }
    return null;
  }

  private static @org.checkerframework.checker.nullness.qual.Nullable TableScan correlationScan(
      RelNode rel) {
    if (rel instanceof TableScan scan && CorrelationRelOptTable.isCorrelation(scan.getTable())) {
      return scan;
    }
    for (RelNode input : rel.getInputs()) {
      TableScan found = correlationScan(input);
      if (found != null) {
        return found;
      }
    }
    return null;
  }

  /** Every field of {@code rowType}, with a {@code ?} on the ones that are nullable. */
  private static String nullability(RowType rowType) {
    StringBuilder text = new StringBuilder();
    for (int i = 0; i < rowType.getFieldsCount(); i++) {
      Field field = rowType.getFields(i);
      text.append(i == 0 ? "" : ", ")
          .append(field.getName())
          .append(field.getType().getNullable() ? "?" : "");
    }
    return text.toString();
  }

  // ------------------------------------------------------------------ I-IR-4, as the client reads it

  /**
   * The client's I-IR-4 over the two node kinds a chain produces: a {@code FieldRef} carries the
   * type of the input field it reads. Anything it disagrees with is collected rather than thrown, so
   * a failure names every place the two row types parted company.
   */
  private static void fieldRefsAgree(Rel rel, List<String> mismatches) {
    switch (rel.getKindCase()) {
      case FILTER -> {
        RowType input = rel.getFilter().getInput().getRowType();
        refs(rel.getFilter().getCondition(), input, "Filter", mismatches);
      }
      case PROJECT -> {
        RowType input = rel.getProject().getInput().getRowType();
        for (Expr expression : rel.getProject().getExprsList()) {
          refs(expression, input, "Project", mismatches);
        }
      }
      default -> {}
    }
    for (Rel input : inputs(rel)) {
      fieldRefsAgree(input, mismatches);
    }
  }

  private static void refs(Expr expression, RowType input, String where, List<String> mismatches) {
    if (expression.hasFieldRef()) {
      int index = expression.getFieldRef().getIndex();
      if (index < input.getFieldsCount()
          && !expression.getType().equals(input.getFields(index).getType())) {
        mismatches.add(
            where
                + " $"
                + index
                + ": the reference is "
                + expression.getType()
                + " and the input field is "
                + input.getFields(index).getType());
      }
      return;
    }
    for (Expr child : nested(expression, Expr.class)) {
      refs(child, input, where, mismatches);
    }
  }

  /** Every child relation of {@code rel}, whatever kind it is. */
  private static List<Rel> inputs(Rel rel) {
    return nested(rel, Rel.class);
  }

  /**
   * The {@code type} messages directly under {@code message} — one level of them, so a nested node
   * is found once and its own children are left to the walk that visits it.
   */
  private static <T extends com.google.protobuf.Message> List<T> nested(
      com.google.protobuf.Message message, Class<T> type) {
    List<T> found = new ArrayList<>();
    collect(message, type, found);
    return found;
  }

  private static <T extends com.google.protobuf.Message> void collect(
      com.google.protobuf.Message message, Class<T> type, List<T> found) {
    for (Object value : message.getAllFields().values()) {
      for (Object one : value instanceof List<?> many ? many : List.of(value)) {
        if (type.isInstance(one)) {
          found.add(type.cast(one));
        } else if (one instanceof com.google.protobuf.Message child) {
          collect(child, type, found);
        }
      }
    }
  }

  // ------------------------------------------------------------------ the catalog

  /**
   * {@code orders} with its organisation held directly and {@code org_id} protected by a rule, so
   * the disclosed row widens it; {@code order_items} inheriting that organisation one step up.
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
                .setDescriptorHash("55556666777788885555666677778888")
                .addColumns(
                    ColumnEntitlement.newBuilder()
                        .setColumn(1)
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen("org_id IN (@ctx.org_auditor)")
                                .setThen(Disclosure.DISCLOSURE_FULL))
                        .setOtherwise(Disclosure.DISCLOSURE_NONE))
                .build())
        .build();
  }

  private static Table orderItems() {
    return Table.newBuilder()
        .setName("order_items")
        .setRowCount(900)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("order_id", type(TypeKind.TYPE_KIND_I32)))
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
                .setDescriptorHash("11112222333344441111222233334444")
                .addInherited(
                    InheritedVisibility.newBuilder()
                        .setKind("org")
                        .addSteps(
                            VisibilityStep.newBuilder()
                                .setTable("orders")
                                .setFromColumn(1)
                                .setToColumn(0)
                                .setDirection(StepDirection.STEP_DIRECTION_TO_PARENT))
                        .setEndpointPredicate("org_id IN (@ctx.org_manager)")
                        .setEndpointTable("orders"))
                .build())
        .build();
  }

  private static CatalogContext protectedChain() {
    return CatalogContext.newBuilder()
        .setContextId("chain")
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
                .addTables(orderItems()))
        .build();
  }

  // ------------------------------------------------------------------ the principals

  private static RequestContext manager(int... orgs) {
    return RequestContext.newBuilder()
        .addRelations(list("org_manager", orgs))
        .addRelations(list("org_auditor", new int[0]))
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
