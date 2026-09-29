package chalk.planner;

import static chalk.planner.TestCatalogs.column;
import static chalk.planner.TestCatalogs.nullable;
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
import chalk.ir.v1.ParentVisibility;
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
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.ContextRelationKind;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.RequestContext;
import java.util.List;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * A membership over a whole context list is a lookup the executor answers, not a join (F161).
 *
 * <p>One table reads six lists in its row predicate, and the first again in a column rule. Bound at
 * execution, or folded with every list above the ceiling, each list is one {@code
 * CHALK_CONTEXT_MEMBERSHIP} and nothing is joined, so the plan's depth does not grow with the lists.
 * Where the split of F36 serves the leaf — four lists or fewer — it still does, and a membership
 * standing alone as the predicate keeps the semi-join a remote source takes as a key set.
 */
class ContextMembershipTest {
  private static final String[] LISTS = {"l1", "l2", "l3", "l4", "l5", "l6"};

  /** A membership of each list, over a column of its own. */
  private static final String SIX =
      "k1 IN (@ctx.l1) OR k2 IN (@ctx.l2) OR k3 IN (@ctx.l3) OR k4 IN (@ctx.l4)"
          + " OR k5 IN (@ctx.l5) OR k6 IN (@ctx.l6)";

  private static RegisteredCatalog wide;

  @BeforeAll
  static void register() {
    wide = new CatalogRegistry().register(catalog());
  }

  private static CatalogContext catalog() {
    Table.Builder table =
        Table.newBuilder()
            .setName("wide")
            .setRowCount(40)
            .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
            .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)));
    for (int k = 1; k <= 5; k++) {
      table.addColumns(column("k" + k, type(TypeKind.TYPE_KIND_I32)));
    }
    table
        .addColumns(column("k6", nullable(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("note", nullable(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0));
    Table.Builder negated = table.clone().setName("negated");
    // With statistics on its key, so the estimator reaches for them: a lookup is no comparison with
    // a literal, whatever its operand count says.
    Table.Builder single = table.clone().setName("single");
    single.setColumns(
        1,
        single.getColumns(1).toBuilder()
            .setStatistics(
                chalk.ir.v1.ColumnStatistics.newBuilder()
                    .setLevel(chalk.ir.v1.StatisticsLevel.STATISTICS_LEVEL_BASIC)
                    .setDistinctCount(10)));
    table.setEntitlement(entitlement(SIX));
    // The nullable key under a negation: the shape a marker join could not answer, since it answered
    // NULL where the membership answers FALSE, and so was refused under execute-time binding.
    negated.setEntitlement(entitlement("k1 IN (@ctx.l1) OR NOT (k6 IN (@ctx.l6))"));
    // One membership and nothing else: the filter is the lookup alone.
    single.setEntitlement(entitlement("k1 IN (@ctx.l1)"));
    // Two children of `wide` that hold no kind of their own: one reaches it along a declared path,
    // whose endpoint predicate reads the six lists, and one derives its visibility through it.
    Table.Builder lines =
        child("lines")
            .setEntitlement(
                TableEntitlement.newBuilder()
                    .setDescriptorHash("fedcba9876543210fedcba9876543210")
                    .addInherited(
                        InheritedVisibility.newBuilder()
                            .setKind("k")
                            .addSteps(
                                VisibilityStep.newBuilder()
                                    .setTable("wide")
                                    .setFromColumn(1)
                                    .setToColumn(0)
                                    .setDirection(StepDirection.STEP_DIRECTION_TO_PARENT))
                            .setEndpointPredicate(SIX)
                            .setEndpointTable("wide")));
    // A table that restricts by itself as well as along the path, and discloses a column by a
    // membership of its own: the `through` leaf, whose own memberships nothing split either.
    Table.Builder twofold =
        child("twofold")
            .addColumns(column("k", type(TypeKind.TYPE_KIND_I32)))
            .addColumns(column("note", nullable(TypeKind.TYPE_KIND_STRING)))
            .setEntitlement(
                TableEntitlement.newBuilder()
                    .setDescriptorHash("0f1e2d3c4b5a69780f1e2d3c4b5a6978")
                    .setRowPredicate("k IN (@ctx.l1) OR k IN (@ctx.l2)")
                    .addInherited(lines.getEntitlement().getInherited(0))
                    .addColumns(
                        ColumnEntitlement.newBuilder()
                            .setColumn(3)
                            .addRules(
                                DisclosureRule.newBuilder()
                                    .setWhen("k IN (@ctx.l3)")
                                    .setThen(Disclosure.DISCLOSURE_FULL))
                            .setOtherwise(Disclosure.DISCLOSURE_NONE)));
    Table.Builder notes =
        child("notes")
            .setEntitlement(
                TableEntitlement.newBuilder()
                    .setDescriptorHash("00112233445566778899aabbccddeeff")
                    .addThrough(
                        ParentVisibility.newBuilder()
                            .setColumn(1)
                            .setParentTable("wide")
                            .setParentColumn(0)));
    return CatalogContext.newBuilder()
        .setContextId("wide")
        .setEpoch(1)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("main")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .setCapabilities(
                    SourceCapabilities.newBuilder()
                        .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE)
                        .build())
                .addTables(table)
                .addTables(negated)
                .addTables(single)
                .addTables(lines)
                .addTables(twofold)
                .addTables(notes))
        .build();
  }

  /** A table whose second column is a declared foreign key onto {@code wide}'s unique key. */
  private static Table.Builder child(String name) {
    return Table.newBuilder()
        .setName(name)
        .setRowCount(80)
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("wide_id", type(TypeKind.TYPE_KIND_I32)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addForeignKeys(
            ForeignKey.newBuilder()
                .setName(name + "_wide")
                .addColumns(1)
                .setParentTable("wide")
                .addParentColumns(0));
  }

  /** The row predicate given, and {@code note} disclosed where the first list holds the row. */
  private static TableEntitlement entitlement(String rowPredicate) {
    return TableEntitlement.newBuilder()
        .setRowPredicate(rowPredicate)
        .setDescriptorHash("0123456789abcdef0123456789abcdef")
        .addColumns(
            ColumnEntitlement.newBuilder()
                .setColumn(7)
                .addRules(
                    DisclosureRule.newBuilder()
                        .setWhen("k1 IN (@ctx.l1)")
                        .setThen(Disclosure.DISCLOSURE_FULL))
                .setOtherwise(Disclosure.DISCLOSURE_NONE))
        .build();
  }

  /** Each named list with {@code rows} rows, every other list empty, and a ceiling of one. */
  private static RequestContext folded(int rows, String... large) {
    RequestContext.Builder context = RequestContext.newBuilder().setFoldMaxRows(1);
    for (String name : LISTS) {
      int count = List.of(large).contains(name) ? rows : 0;
      context.addRelations(list(name, count, false));
    }
    return context.build();
  }

  /** Every list as a shape: names and types, no rows (D209). */
  private static RequestContext shape() {
    RequestContext.Builder context = RequestContext.newBuilder().setShapeOnly(true);
    for (String name : LISTS) {
      context.addRelations(list(name, 0, true));
    }
    return context.build();
  }

  private static ContextRelationValue list(String name, int rows, boolean shape) {
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
    for (int r = 0; r < rows; r++) {
      builder.addRows(
          VirtualRow.newBuilder()
              .addValues(
                  Expr.newBuilder()
                      .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))
                      .setLiteral(Literal.newBuilder().setI32Value(r + 1))));
    }
    if (shape) {
      builder.setShape(true);
    }
    return builder.build();
  }

  private static PlannerPipeline.Result plan(RequestContext context) throws Exception {
    return plan("SELECT id, note FROM wide", context);
  }

  private static PlannerPipeline.Result plan(String sql, RequestContext context) throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            wide,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            BoundContext.of(context),
            PolicyOptions.DEFAULTS)) {
      return pipeline.plan(sql, true);
    }
  }

  @Test
  void a_shape_reads_every_list_by_a_lookup_and_joins_nothing() throws Exception {
    PlannerPipeline.Result result = plan(shape());

    String text = result.physicalPlanText();
    for (String name : LISTS) {
      assertThat(text).contains("CHALK_CONTEXT_MEMBERSHIP('" + name + "'");
    }
    assertThat(text).doesNotContain("Join").doesNotContain("ChalkContextScan");
    assertThat(result.requiredRelations()).containsExactlyInAnyOrder(LISTS);
  }

  @Test
  void a_negated_membership_under_a_shape_is_a_lookup_rather_than_a_refusal() throws Exception {
    // A marker answered NULL where the membership answers FALSE, so a negation of one over a
    // nullable key was refused; the lookup is the membership, so NOT of it is exact.
    assertThat(plan("SELECT id, note FROM negated", shape()).physicalPlanText())
        .contains("NOT(CHALK_CONTEXT_MEMBERSHIP('l6'");
  }

  @Test
  void a_predicate_that_is_one_membership_is_one_lookup() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, k1 FROM single WHERE id > 3 ORDER BY id", shape());

    assertThat(result.physicalPlanText()).contains("CHALK_CONTEXT_MEMBERSHIP('l1'");
    assertThat(result.requiredRelations()).containsExactly("l1");
  }

  @Test
  void past_four_lists_above_the_ceiling_each_is_a_lookup_and_nothing_is_joined() throws Exception {
    PlannerPipeline.Result result = plan(folded(2, LISTS));

    String text = result.physicalPlanText();
    for (String name : LISTS) {
      assertThat(text).contains("CHALK_CONTEXT_MEMBERSHIP('" + name + "'");
    }
    assertThat(text).doesNotContain("Join").doesNotContain("Union");
    assertThat(result.requiredRelations()).containsExactlyInAnyOrder(LISTS);
  }

  @Test
  void up_to_four_lists_the_split_still_serves_the_leaf() throws Exception {
    // Three lists above the ceiling and the rest empty, which folds them away: F36's union of a
    // semi-join branch and an anti-join branch, whose semi-joins a remote source takes as key sets.
    String text = plan(folded(2, "l1", "l2", "l3")).physicalPlanText();

    assertThat(text).contains("Union").contains("joinType=[semi]");
    assertThat(text).doesNotContain("CHALK_CONTEXT_MEMBERSHIP");
  }

  @Test
  void a_membership_standing_alone_keeps_its_semi_join() throws Exception {
    // One list above the ceiling and the rest empty: the predicate is the one membership, which
    // Calcite makes the semi-join `ContextKeySetRule` ships to a remote source (F45). Its column
    // rule reads the same list, and a projection cannot hold a join: there it is a lookup.
    String text = plan(folded(2, "l1")).physicalPlanText();

    assertThat(text).contains("joinType=[semi]").contains("ChalkContextScan");
    assertThat(text).contains("CHALK_CONTEXT_MEMBERSHIP('l1'");
    assertThat(text).doesNotContain("Union");
  }

  @Test
  void a_paths_endpoint_reads_every_list_by_a_lookup() throws Exception {
    // The endpoint's filter is no leaf's, so nothing splits it, and Calcite joined each list against
    // the chain in turn: tutorial chapter 13's base plan was fifteen such joins deep.
    for (RequestContext context : List.of(shape(), folded(2, LISTS))) {
      PlannerPipeline.Result result = plan("SELECT id FROM lines", context);

      String text = result.physicalPlanText();
      for (String name : LISTS) {
        assertThat(text).contains("CHALK_CONTEXT_MEMBERSHIP('" + name + "'");
      }
      assertThat(text).doesNotContain("ChalkContextScan");
      assertThat(result.requiredRelations()).containsExactlyInAnyOrder(LISTS);
    }
  }

  @Test
  void a_table_held_along_a_path_reads_its_own_lists_by_a_lookup_too() throws Exception {
    // Its restriction is `own OR marker`, and its rule is its own: neither is a leaf the split sees,
    // and Calcite joined each list twice over, under the filter and under the sanitisers.
    for (RequestContext context : List.of(shape(), folded(2, LISTS))) {
      PlannerPipeline.Result result = plan("SELECT id, note FROM twofold", context);

      String text = result.physicalPlanText();
      for (String name : LISTS) {
        assertThat(text).contains("CHALK_CONTEXT_MEMBERSHIP('" + name + "'");
      }
      assertThat(text).doesNotContain("ChalkContextScan");
      assertThat(result.requiredRelations()).containsExactlyInAnyOrder(LISTS);
    }
  }

  @Test
  void a_parent_side_reads_every_list_by_a_lookup() throws Exception {
    // The parent's own entitled scan, beside the child: a leaf the split of F36 never sees.
    for (RequestContext context : List.of(shape(), folded(2, LISTS))) {
      PlannerPipeline.Result result = plan("SELECT id FROM notes", context);

      String text = result.physicalPlanText();
      for (String name : LISTS) {
        assertThat(text).contains("CHALK_CONTEXT_MEMBERSHIP('" + name + "'");
      }
      assertThat(text).doesNotContain("ChalkContextScan");
      assertThat(result.requiredRelations()).containsExactlyInAnyOrder(LISTS);
    }
  }
}
