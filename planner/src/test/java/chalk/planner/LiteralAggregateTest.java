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
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.LiteralAggregates;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.ContextRelationKind;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.RequestContext;
import java.util.List;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.core.RelFactories;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.tools.RelBuilder;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * {@code LITERAL_AGG} written as the projected literal it means (F71, ADR 0054).
 *
 * <p>Calcite's three-valued rewrite of a membership emits {@code Aggregate(g, LITERAL_AGG(v))}. The
 * IR has no such call, so a statement whose {@code Filter_R} put an unfolded membership beside a
 * path's nullable marker — a list above the fold ceiling (D209), or a name left open by partial
 * binding (D232) — was refused naming an aggregate the <em>policy</em> had produced. The pass's own
 * {@link chalk.planner.entitlement.MembershipSplit} answers the case where the leaf has no path; a
 * leaf that derives its visibility along one takes the {@code through} route instead, where there is
 * no split, and there the two markers meet.
 *
 * <p>{@code LITERAL_AGG(v)} over a group is {@code v} once per group, which is {@code Project(g, v)}
 * over {@code Aggregate(g)} — the shape {@code MembershipMarkers} builds by hand and the executor
 * already runs.
 */
class LiteralAggregateTest {
  private static RegisteredCatalog catalog;

  @BeforeAll
  static void register() {
    catalog = new CatalogRegistry().register(marketplace());
  }

  // ------------------------------------------------------------------ F71, end to end

  /**
   * F71's own shape: a membership above the fold ceiling beside a path's marker. It plans, and the
   * plan holds the projected literal rather than an aggregate call the IR cannot carry.
   */
  @Test
  void a_membership_above_the_ceiling_beside_a_path_marker_plans() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, amount FROM orders", unfolded(managerAndVendor(new int[] {1, 2}, 1)));

    assertThat(result.physicalPlanText()).contains("main, orders");
    assertThat(result.physicalPlanText()).doesNotContain("LITERAL_AGG");
    // And it reaches the IR, which is where it was refused: `aggregate LITERAL_AGG is not supported
    // by this planner` (02-ir.md §6).
    assertThat(irOf(result).getRoot().getKindCase())
        .isNotEqualTo(chalk.ir.v1.Rel.KindCase.KIND_NOT_SET);
  }

  /** The plan as the client receives it, which is where an aggregate the IR lacks is refused. */
  private static chalk.ir.v1.Plan irOf(PlannerPipeline.Result result) {
    chalk.planner.ir.RelToIr toIr =
        new chalk.planner.ir.RelToIr(
            new chalk.planner.types.TypeMapper(result.physical().getCluster().getTypeFactory()),
            result.physical().getCluster().getRexBuilder(),
            org.apache.calcite.rel.metadata.RelMetadataQuery.instance(),
            chalk.planner.ir.IrVersionGate.current());
    return toIr.toPlan(
        result.physical(), result.parameterRowType(), catalog.contextId(), catalog.epoch());
  }

  /** And the same statement for a principal whose list folds is unchanged: no aggregate at all. */
  @Test
  void a_membership_that_folds_is_left_exactly_as_it_was() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, amount FROM orders", managerAndVendor(new int[] {1}, 1));

    assertThat(result.physicalPlanText()).doesNotContain("LITERAL_AGG");
    assertThat(result.physicalPlanText()).contains("main, orders");
  }

  // ------------------------------------------------------------------ the rewrite itself

  /** A grouped aggregate: the group set and the other call survive, the literal is projected. */
  @Test
  void a_grouped_literal_agg_becomes_a_projected_literal() throws Exception {
    RelNode input = builder().scan("main", "orders").build();
    RelNode aggregate =
        org.apache.calcite.rel.logical.LogicalAggregate.create(
            input,
            com.google.common.collect.ImmutableList.of(),
            org.apache.calcite.util.ImmutableBitSet.of(1),
            null,
            List.of(count(input, 1), literalAgg(input, 1)));
    assertThat(literalAggs(aggregate)).isEqualTo(1);

    RelNode rewritten = LiteralAggregates.rewrite(aggregate);

    assertThat(literalAggs(rewritten)).isZero();
    // The row type is field-for-field what it was, so nothing above it can tell the difference.
    assertThat(rewritten.getRowType().getFullTypeString())
        .isEqualTo(aggregate.getRowType().getFullTypeString());
    assertThat(rewritten).isInstanceOf(org.apache.calcite.rel.core.Project.class);
    Aggregate reduced = (Aggregate) rewritten.getInput(0);
    assertThat(reduced.getGroupSet().toString())
        .isEqualTo(((Aggregate) aggregate).getGroupSet().toString());
    assertThat(reduced.getAggCallList()).hasSize(1);
    assertThat(reduced.getAggCallList().get(0).getAggregation().getKind())
        .isEqualTo(SqlKind.COUNT);
  }

  /**
   * The empty global group, which is the case a naive strip gets wrong: {@code LITERAL_AGG} reports
   * its value once even over an input with no rows, and {@code Aggregate(∅, [])} would report none.
   * A {@code COUNT(*)} keeps the report and the projection drops it.
   */
  @Test
  void an_empty_global_group_still_reports_its_literal() throws Exception {
    RelNode input = builder().scan("main", "orders").build();
    RelNode aggregate =
        org.apache.calcite.rel.logical.LogicalAggregate.create(
            input,
            com.google.common.collect.ImmutableList.of(),
            org.apache.calcite.util.ImmutableBitSet.of(),
            null,
            List.of(literalAgg(input, 0)));
    assertThat(literalAggs(aggregate)).isEqualTo(1);

    RelNode rewritten = LiteralAggregates.rewrite(aggregate);

    assertThat(literalAggs(rewritten)).isZero();
    assertThat(rewritten.getRowType().getFullTypeString())
        .isEqualTo(aggregate.getRowType().getFullTypeString());
    Aggregate reduced = (Aggregate) rewritten.getInput(0);
    assertThat(reduced.getGroupSet().isEmpty()).isTrue();
    assertThat(reduced.getAggCallList()).hasSize(1);
    assertThat(reduced.getAggCallList().get(0).getAggregation().getKind())
        .as("the global group must keep reporting over an empty input")
        .isEqualTo(SqlKind.COUNT);
  }

  /** A tree that holds none comes back as the same object, which is what costs nothing. */
  @Test
  void a_tree_without_one_is_returned_unchanged() throws Exception {
    RelNode input = builder().scan("main", "orders").build();
    RelNode plain =
        org.apache.calcite.rel.logical.LogicalAggregate.create(
            input,
            com.google.common.collect.ImmutableList.of(),
            org.apache.calcite.util.ImmutableBitSet.of(1),
            null,
            List.of(count(input, 1)));

    assertThat(LiteralAggregates.rewrite(plain)).isSameAs(plain);
  }

  /** {@code LITERAL_AGG(TRUE)}, exactly as Calcite's three-valued sub-query rewrite emits it. */
  @SuppressWarnings("deprecation")
  private static org.apache.calcite.rel.core.AggregateCall literalAgg(RelNode input, int groups) {
    return org.apache.calcite.rel.core.AggregateCall.create(
        org.apache.calcite.sql.fun.SqlLiteralAggFunction.INSTANCE,
        /* distinct= */ false,
        /* approximate= */ false,
        /* ignoreNulls= */ false,
        com.google.common.collect.ImmutableList.of(
            input.getCluster().getRexBuilder().makeLiteral(true)),
        com.google.common.collect.ImmutableList.of(),
        /* filterArg= */ -1,
        /* distinctKeys= */ null,
        org.apache.calcite.rel.RelCollations.EMPTY,
        groups,
        input,
        /* type= */ null,
        "lit");
  }

  @SuppressWarnings("deprecation")
  private static org.apache.calcite.rel.core.AggregateCall count(RelNode input, int groups) {
    return org.apache.calcite.rel.core.AggregateCall.create(
        org.apache.calcite.sql.fun.SqlStdOperatorTable.COUNT,
        /* distinct= */ false,
        /* approximate= */ false,
        /* ignoreNulls= */ false,
        com.google.common.collect.ImmutableList.of(),
        com.google.common.collect.ImmutableList.of(),
        /* filterArg= */ -1,
        /* distinctKeys= */ null,
        org.apache.calcite.rel.RelCollations.EMPTY,
        groups,
        input,
        /* type= */ null,
        "n");
  }

  private static int literalAggs(RelNode rel) {
    int found = 0;
    if (rel instanceof Aggregate aggregate) {
      for (org.apache.calcite.rel.core.AggregateCall call : aggregate.getAggCallList()) {
        if (call.getAggregation().getKind() == SqlKind.LITERAL_AGG) {
          found++;
        }
      }
    }
    for (RelNode input : rel.getInputs()) {
      found += literalAggs(input);
    }
    return found;
  }

  // ------------------------------------------------------------------ the plan and the builder

  private static PlannerPipeline.Result plan(String sql, RequestContext context) throws Exception {
    try (PlannerPipeline pipeline = pipeline(context)) {
      return pipeline.plan(sql, true);
    }
  }

  /** A {@code RelBuilder} over the same catalog, for the unit cases. */
  private static RelBuilder builder() throws Exception {
    try (PlannerPipeline pipeline = pipeline(managerAndVendor(new int[] {1}, 1))) {
      RelNode logical = pipeline.logical("SELECT id FROM orders");
      org.apache.calcite.rel.core.TableScan scan = firstScan(logical);
      return RelFactories.LOGICAL_BUILDER.create(
          logical.getCluster(), scan.getTable().getRelOptSchema());
    }
  }

  private static org.apache.calcite.rel.core.TableScan firstScan(RelNode rel) {
    if (rel instanceof org.apache.calcite.rel.core.TableScan scan) {
      return scan;
    }
    for (RelNode input : rel.getInputs()) {
      org.apache.calcite.rel.core.TableScan found = firstScan(input);
      if (found != null) {
        return found;
      }
    }
    throw new AssertionError("no scan in " + rel);
  }

  private static PlannerPipeline pipeline(RequestContext context) {
    return PlannerPipeline.create(
        catalog,
        PushdownPolicy.full(),
        chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
        List.of(),
        chalk.planner.plan.JoinPolicy.DEFAULT,
        BoundContext.of(context),
        PolicyOptions.DEFAULTS);
  }

  /** A ceiling of one leaves any list of two or more rows a sub-query, which is the whole point. */
  private static RequestContext unfolded(RequestContext context) {
    return context.toBuilder().setFoldMaxRows(1).build();
  }

  // ------------------------------------------------------------------ the catalog

  private static Table orders() {
    return Table.newBuilder()
        .setName("orders")
        .setRowCount(200)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("org_id", TestCatalogs.nullable(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("amount", type(TypeKind.TYPE_KIND_I64)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate("org_id IN (@ctx.org_manager) OR org_id IN (@ctx.org_agent)")
                .setDescriptorHash("77778888999900007777888899990000")
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
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  private static CatalogContext marketplace() {
    return CatalogContext.newBuilder()
        .setContextId("literal-agg")
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

  private static RequestContext managerAndVendor(int[] orgs, int vendor) {
    return RequestContext.newBuilder()
        .addRelations(list("org_manager", orgs))
        .addRelations(list("org_agent", orgs))
        .addRelations(list("vendor_vendor", new int[] {vendor}))
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
