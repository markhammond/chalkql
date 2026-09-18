package chalk.planner;

import static chalk.planner.TestCatalogs.column;
import static chalk.planner.TestCatalogs.type;
import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.Expr;
import chalk.ir.v1.Field;
import chalk.ir.v1.ForeignKey;
import chalk.ir.v1.Literal;
import chalk.ir.v1.QueryLanguage;
import chalk.ir.v1.RowCountKind;
import chalk.ir.v1.RowType;
import chalk.ir.v1.Schema;
import chalk.ir.v1.SourceCapabilities;
import chalk.ir.v1.SourceKind;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableEntitlement;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.UniqueKey;
import chalk.ir.v1.VirtualRow;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.diag.HepTransformations;
import chalk.planner.entitlement.BoundContext;
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
 * A correlate whose input folded to nothing (F70; ADR 0054, ADR 0050 §7 (b)).
 *
 * <p>{@code RuleSets.hep}'s pruning collection — its comment 5, "WHERE 1 = 0 becomes an empty
 * Values" — listed every {@link org.apache.calcite.rel.rules.PruneEmptyRules} instance the joins run
 * of 2026-09-08 had a shape for, and that run's milestone had no correlates. So a sub-query the
 * optimiser could not turn into a semi-join, over a leaf an entitlement folded to nothing, left an
 * {@code INNER} correlate that nothing pruned and that this milestone decorrelates nothing out of:
 * planning refused with <em>correlated subquery could not be decorrelated</em> rather than answering
 * the no rows that are certainly right.
 *
 * <p>{@code items} is entitled to the vendors the principal's grants name, so for a principal
 * holding no vendor grant its leaf is empty and the body of the sub-query is empty with it; the
 * target is an ordinary organisation-entitled table, so the <em>outer</em> side is not.
 */
class CorrelateEmptyInputTest {
  private static RegisteredCatalog catalog;

  /** ADR 0048 §10's own statement for F70. */
  private static final String EXISTS =
      "SELECT o.id FROM orders o WHERE EXISTS ("
          + "SELECT 1 FROM order_items i JOIN items p ON p.id = i.item_id WHERE i.order_id = o.id)";

  @BeforeAll
  static void register() {
    catalog = new CatalogRegistry().register(marketplace());
  }

  /** The {@code INNER} half: no endpoint row is reachable, so no order is, and the answer is none. */
  @Test
  void a_correlate_whose_input_folded_to_nothing_plans_to_no_rows() throws Exception {
    PlannerPipeline.Result result = plan(EXISTS, manager(1));

    assertThat(result.physicalPlanText()).contains("ChalkValues(tuples=[[]])");
  }

  /**
   * The {@code LEFT} half. A {@code LEFT JOIN LATERAL} whose body reads a table that folded to
   * nothing keeps every left row with nulls where the body would have been, which is what an outer
   * join means and what {@code CORRELATE_LEFT_INSTANCE} answers.
   */
  @Test
  void a_left_correlate_whose_input_folded_to_nothing_keeps_the_left_side() throws Exception {
    PlannerPipeline.Result result =
        plan(
            "SELECT o.id, c.id FROM orders o LEFT JOIN LATERAL ("
                + "SELECT p.id FROM order_items i JOIN items p ON p.id = i.item_id"
                + " WHERE i.order_id = o.id) c ON TRUE",
            manager(1));

    assertThat(result.physicalPlanText()).contains("main, orders");
    assertThat(result.physicalPlanText()).doesNotContain("Correlate");
    // The right side is gone and its column is a typed NULL, which is the left join's own answer.
    assertThat(result.physicalPlanText()).doesNotContain("main, items");
  }

  /** And a principal who does reach the endpoint still gets the join, not an empty relation. */
  @Test
  void a_principal_who_reaches_the_endpoint_still_reads_it() throws Exception {
    PlannerPipeline.Result result = plan(EXISTS, vendor(1));

    assertThat(result.physicalPlanText()).contains("main, items");
    assertThat(result.physicalPlanText()).doesNotContain("ChalkValues(tuples=[[]])");
  }

  // ---------------------------------------------------------------- a count of nothing (F79)

  /**
   * F79, and the shape {@code CORRELATE_LEFT_INSTANCE} has no answer for. {@code COUNT} over
   * nothing is {@code 0} and not nothing, so the aggregate stands over the empty {@code Values}, the
   * correlate's right side is one row rather than none, and neither prune rule matches. What is left
   * is a correlate whose right side no longer reads the left row at all — the correlated predicate
   * went with the pruning — and that is a join on TRUE
   * ({@link chalk.planner.plan.rules.UncorrelatedCorrelateRule}). It is {@code m7-adversarial} 54's
   * shape: a count of the lines of each visible order, read by a principal who reaches no line.
   */
  @Test
  void a_correlated_count_over_a_leaf_that_folded_to_nothing_counts_zero() throws Exception {
    PlannerPipeline.Result result = plan(COUNT_OF_THE_LINES, manager(1));

    assertThat(result.physicalPlanText()).contains("main, orders");
    assertThat(result.physicalPlanText()).doesNotContain("Correlate");
    // Every visible order keeps its row, and the count beside it is the aggregate's own answer for
    // no rows, which is what the LEFT join to a one-row right side preserves.
    assertThat(result.physicalPlanText()).containsPattern("joinType=\\[left\\]");
    assertThat(result.physicalPlanText()).contains("COUNT()");
  }

  /** And a principal who reaches the lines still counts them, through the join it always was. */
  @Test
  void a_principal_who_reaches_the_endpoint_still_counts_its_lines() throws Exception {
    PlannerPipeline.Result result = plan(COUNT_OF_THE_LINES, vendor(1));

    assertThat(result.physicalPlanText()).contains("main, items");
    assertThat(result.physicalPlanText()).doesNotContain("Correlate");
  }

  /**
   * The rule reaches its fixpoint in the Hep pre-pass rather than trading with another (F66's
   * lesson, {@code HepBudgetTest}): the bound is on applications and never on elapsed time.
   */
  @Test
  void the_rule_reaches_a_fixpoint() throws Exception {
    try (HepTransformations counter = HepTransformations.install(BUDGET);
        PlannerPipeline pipeline =
            PlannerPipeline.create(
                catalog,
                PushdownPolicy.full(),
                chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
                List.of(),
                chalk.planner.plan.JoinPolicy.DEFAULT,
                BoundContext.of(manager(1)),
                PolicyOptions.DEFAULTS)) {
      pipeline.plan(COUNT_OF_THE_LINES, true);
      assertThat(counter.total())
          .as("Hep transformations by rule: %s", counter.byRule())
          .isLessThan(BUDGET);
    }
  }

  /** {@code m7-adversarial} 54's shape: a correlated count of the lines of each order. */
  private static final String COUNT_OF_THE_LINES =
      "SELECT o.id, (SELECT COUNT(*) FROM order_items i JOIN items p ON p.id = i.item_id"
          + " WHERE i.order_id = o.id) AS n FROM orders o";

  /** The same loose bound {@code HepBudgetTest} uses, and for the same reason. */
  private static final int BUDGET = 64;

  // ------------------------------------------------------------------ the plan

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

  // ------------------------------------------------------------------ the catalog

  private static Table orders() {
    return Table.newBuilder()
        .setName("orders")
        .setRowCount(200)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate("org_id IN (@ctx.org_manager)")
                .setDescriptorHash("aaaa1111bbbb2222aaaa1111bbbb2222")
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
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate("vendor_id IN (@ctx.vendor_vendor)")
                .setDescriptorHash("cccc3333dddd4444cccc3333dddd4444")
                .build())
        .build();
  }

  private static CatalogContext marketplace() {
    return CatalogContext.newBuilder()
        .setContextId("correlate")
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

  private static RequestContext manager(int... orgs) {
    return principal(orgs, new int[0]);
  }

  private static RequestContext vendor(int... vendors) {
    return principal(new int[] {1}, vendors);
  }

  private static RequestContext principal(int[] orgs, int[] vendors) {
    return RequestContext.newBuilder()
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
