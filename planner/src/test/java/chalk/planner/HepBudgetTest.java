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
import chalk.ir.v1.TestShape;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.UniqueKey;
import chalk.ir.v1.VirtualRow;
import chalk.ir.v1.VisibilityStep;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.diag.HepTransformations;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.ContextRelationKind;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.ContextScalar;
import chalk.planner.rpc.v1.RequestContext;
import java.util.List;
import org.junit.jupiter.api.Test;

/**
 * The Hep pre-pass reaches a fixpoint on the shape that stopped it (F66, ADR 0050).
 *
 * <p>F66 was registered as "a {@code Related} path's verdict ordinals under a second entitled join",
 * and the path is not what did it. What did it is a <b>column rule on a join key</b>. The rule makes
 * the column nullable in the row type a statement is written over — per catalog and not per
 * principal (D161, F58) — so the leaf's {@code Project_D} hands the join a {@code CAST} of the
 * constant its own {@code Filter_R} pins the column to. Calcite's metadata publishes a nullable
 * constant as {@code IS NOT DISTINCT FROM} while its join inference asks for {@code =}, so
 * {@code JOIN_PUSH_TRANSITIVE_PREDICATES} never recognises its own inference as one the side already
 * carries: it wraps the side in a {@code Filter}, {@code FILTER_REDUCE_EXPRESSIONS} proves that
 * {@code Filter} redundant and deletes it, and in one Hep rule collection the two run to a fixpoint
 * that does not exist.
 *
 * <p><b>The measurement.</b> Before the fix, the {@link #JOIN} statement over the registered catalog
 * did not finish: the two rules had produced <b>55,231</b> transformations between them after 15 s
 * ({@code ReduceExpressionsRule(Filter)}=36,817, {@code JoinPushTransitivePredicatesRule}=18,409)
 * and were still going. The corpus statement this was found through, {@code m7-tenancy} 08 as the
 * fixture's {@code u3}, stood at 58,503 after 20 s. Every assertion here is a bound on that count
 * and never on elapsed time, which is what {@code docs/handoff-m1.md} requires of a reproduction.
 */
class HepBudgetTest {

  /**
   * What the shapes below come to once the Hep phases have finished. Nine to twenty-one
   * transformations each; the bound is loose enough that ordinary rule work may move under it
   * and three orders of magnitude below a run that does not terminate, which is the only distinction
   * this test exists to make.
   */
  private static final int BUDGET = 64;

  /**
   * The registered shape: {@code orders} related to a vendor through its lines, a rule on
   * {@code member_id} reading the endpoint, and a join to a second entitled leaf on that very
   * column. This is {@code m7-tenancy} 08 over design 38 §8's fixture, in the smallest catalog that
   * carries it.
   */
  @Test
  void the_shape_that_did_not_finish_now_reaches_a_fixpoint() throws Exception {
    Planned planned = plan(REGISTERED, JOIN, PARTICIPANT);

    assertThat(planned.plan()).contains("orders", "members");
    assertThat(planned.transformations())
        .as("Hep transformations by rule: %s", planned.byRule())
        .isLessThan(BUDGET);
  }

  /**
   * The path is not what turned it on. The same catalog with the {@code Related} path removed and
   * the rule left on the join key ran exactly as long before the fix — 78,720 transformations in
   * 15 s — which is what says the finding's "distinguishing shape" was the company the defect kept
   * and not the defect.
   */
  @Test
  void the_path_is_not_what_the_fixpoint_turned_on() throws Exception {
    Planned planned =
        plan(
            catalog(
                /* path= */ false, JOIN_KEY, /* asTest= */ true, /* ownPredicate= */ true),
            JOIN,
            PARTICIPANT);

    assertThat(planned.transformations())
        .as("Hep transformations by rule: %s", planned.byRule())
        .isLessThan(BUDGET);
  }

  /**
   * And the rule on the join key is. The same catalog with the path kept and the rule removed
   * planned in under a second before the fix, because a column no rule names keeps the source's own
   * {@code NOT NULL} and reaches the join as a literal rather than as a cast of one.
   */
  @Test
  void a_join_key_no_rule_names_was_never_the_shape() throws Exception {
    Planned planned =
        plan(
            catalog(/* path= */ true, NO_RULE, /* asTest= */ true, /* ownPredicate= */ true),
            JOIN,
            PARTICIPANT);

    assertThat(planned.transformations())
        .as("Hep transformations by rule: %s", planned.byRule())
        .isLessThan(BUDGET);
  }

  /**
   * The inference still does its work. Moving {@code JOIN_PUSH_TRANSITIVE_PREDICATES} into a phase
   * of its own is not the same as dropping it: it still fires, and what it infers still reaches the
   * side it was inferred for, because the pass after decorrelation pushes and merges filters and
   * carries no reduce rule to undo them.
   */
  @Test
  void the_inference_still_reaches_the_side_it_was_inferred_for() throws Exception {
    Planned planned = plan(REGISTERED, JOIN, PARTICIPANT);

    assertThat(planned.byRule()).anyMatch(line -> line.startsWith("JoinPushTransitivePredicates"));
    // `members` is read only where its own row predicate pins it, and the join key the inference
    // carried over from `orders` is what leaves the scan reading one row rather than the table.
    assertThat(planned.plan()).contains("main, members");
  }

  /**
   * The owner asked whether the mechanism could emit a shape the planner handles better — an
   * {@code EXISTS}, an {@code IN (subquery)} or an {@code = ANY (subquery)} instead of the LEFT JOIN
   * with the nullable marker. It could not have helped, and this is the half of the answer a test can
   * hold: with the path as the target's <b>only</b> restriction there is no OR and no marker at all —
   * the pass emits the plain inner join a semi-join collapses to — and a <em>nullable verdict
   * ordinal</em> rides on it. A rule on a column that is not the join key is bounded here — 21
   * transformations — and was bounded before the fix too. The nullable ordinal is not the shape.
   */
  @Test
  void a_nullable_verdict_ordinal_under_a_marker_join_that_is_not_ored_is_not_the_shape()
      throws Exception {
    Planned planned =
        plan(
            catalog(
                /* path= */ true, ANOTHER_COLUMN, /* asTest= */ false, /* ownPredicate= */ false),
            JOIN_READING_BOTH,
            SUPPLIER_IN_TWO_ROLES);

    assertThat(planned.plan()).contains(chalk.planner.ReservedNames.PREFIX + "verdict0");
    assertThat(planned.transformations())
        .as("Hep transformations by rule: %s", planned.byRule())
        .isLessThan(BUDGET);
  }

  /**
   * And the other half: the same catalog, the same principal, the same plain inner marker join and
   * the same nullable ordinal, with the rule moved onto the <b>join key</b>. Twenty transformations
   * here; before the fix it did not finish at all — the same shape, run uncapped, reached 141,200 in
   * 15 s — so what F66 turns on survives every spelling of the marker, semi-join included, and no
   * choice of sub-query form could have avoided it.
   */
  @Test
  void the_same_marker_join_with_the_rule_on_the_join_key_is() throws Exception {
    Planned planned =
        plan(
            catalog(/* path= */ true, JOIN_KEY, /* asTest= */ false, /* ownPredicate= */ false),
            JOIN_READING_BOTH,
            SUPPLIER_IN_TWO_ROLES);

    assertThat(planned.plan()).contains(chalk.planner.ReservedNames.PREFIX + "verdict0");
    assertThat(planned.transformations())
        .as("Hep transformations by rule: %s", planned.byRule())
        .isLessThan(BUDGET);
  }

  // ------------------------------------------------------------------ the harness

  /** {@code orders.member_id}, the column statement 08 joins on. */
  private static final int JOIN_KEY = 1;

  /** {@code orders.org_id}, a protected column the join does not read. */
  private static final int ANOTHER_COLUMN = 2;

  /** No column rule at all, so every column keeps the type the source declares. */
  private static final int NO_RULE = -1;

  /** The registered shape: the path, the rule on the join key, and the target's own predicate. */
  private static final CatalogContext REGISTERED =
      catalog(/* path= */ true, JOIN_KEY, /* asTest= */ true, /* ownPredicate= */ true);

  private static final String JOIN =
      "SELECT m.id, m.last_name, o.id FROM members m JOIN orders o ON o.member_id = m.id";

  /** The same join, reading the protected column too, so neither sanitiser is trimmed away. */
  private static final String JOIN_READING_BOTH =
      "SELECT m.id, o.id, o.org_id FROM members m JOIN orders o ON o.member_id = m.id";

  /** One planning run: its plan text and what the Hep phases did to get there. */
  private record Planned(String plan, int transformations, List<String> byRule) {}

  private static Planned plan(CatalogContext descriptor, String sql, RequestContext who)
      throws Exception {
    RegisteredCatalog catalog = new CatalogRegistry().register(descriptor);
    try (HepTransformations counter = HepTransformations.install(BUDGET);
        PlannerPipeline pipeline =
            PlannerPipeline.create(
                catalog,
                PushdownPolicy.full(),
                chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
                List.of(),
                chalk.planner.plan.JoinPolicy.DEFAULT,
                BoundContext.of(who),
                PolicyOptions.DEFAULTS)) {
      PlannerPipeline.Result result = pipeline.plan(sql, true);
      return new Planned(result.physicalPlanText(), counter.total(), counter.byRule());
    }
  }

  // ------------------------------------------------------------------ the catalog

  /** The rule the vendor perspective writes on the column it protects, which reads the endpoint. */
  private static final String VENDOR_REACHES = "vendors.id IN (@ctx.vendor_admin)";

  /**
   * Which of the endpoint's rows this principal holds the kind in, OR-ed across the roles the target
   * admits — the shape the compiler emits (design 38 §2). Two roles rather than one so that a
   * principal holding both makes a rule's role half <em>vary</em> under it, which is what puts a
   * verdict column on the path's side at all.
   */
  private static final String ENDPOINT =
      "id IN (@ctx.vendor_admin) OR id IN (@ctx.vendor_staff) OR @ctx.global";

  /** A second entitled leaf, joined to the target on the column the rule protects. */
  private static Table members() {
    return Table.newBuilder()
        .setName("members")
        .setRowCount(50)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("last_name", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate("(id, org_id) IN (@ctx.subject_pairs) OR @ctx.global")
                .setDescriptorHash("33334444555566663333444455556666")
                .build())
        .build();
  }

  /** The endpoint of the vendor perspective: the kind is on its own row. */
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
                .setRowPredicate(ENDPOINT)
                .setDescriptorHash("aaaabbbbccccddddaaaabbbbccccdddd")
                .build())
        .build();
  }

  /** The bridge: it carries the relationship and contributes existence and nothing else. */
  private static Table orderItems() {
    return Table.newBuilder()
        .setName("order_items")
        .setRowCount(900)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("order_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("vendor_id", type(TypeKind.TYPE_KIND_I32)))
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

  /**
   * The target. Its row predicate is the fixture's own: a subject grant confined within an
   * organisation, which folds for this principal to {@code member_id = 3 AND org_id = 2}, and the
   * resource-owner escape beside it. That conjunction is what pins the join key to a constant.
   */
  private static Table orders(boolean path, int ruleColumn, boolean asTest, boolean ownPredicate) {
    TableEntitlement.Builder entitlement =
        TableEntitlement.newBuilder().setDescriptorHash("11112222333344441111222233334444");
    if (ownPredicate) {
      entitlement.setRowPredicate(
          "(member_id, org_id) IN (@ctx.subject_pairs) OR created_by = @ctx.user OR @ctx.global");
    }
    if (path) {
      entitlement.addInherited(
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
              .setEndpointPredicate(ENDPOINT)
              .setEndpointTable("vendors"));
    }
    if (ruleColumn >= 0) {
      // §8's order, and D216: the moment the vendor's rule names a column it is protected for every
      // role, so the rule the other roles reach says so after it. What the vendor is granted there
      // is §8's own pair — a comparison of the member (D261), a placeholder for the organisation.
      DisclosureRule.Builder vendor =
          DisclosureRule.newBuilder().setWhen(path ? VENDOR_REACHES : "created_by = 0");
      if (asTest) {
        vendor.setThen(Disclosure.DISCLOSURE_TEST).addTests(TestShape.TEST_SHAPE_EQUALS);
      } else {
        vendor.setThen(Disclosure.DISCLOSURE_NONE).setPlaceholder("0");
      }
      entitlement.addColumns(
          ColumnEntitlement.newBuilder()
              .setColumn(ruleColumn)
              .addRules(vendor)
              .addRules(
                  DisclosureRule.newBuilder().setWhen("TRUE").setThen(Disclosure.DISCLOSURE_FULL))
              .setOtherwise(Disclosure.DISCLOSURE_NONE));
    }
    return Table.newBuilder()
        .setName("orders")
        .setRowCount(200)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("member_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("created_by", type(TypeKind.TYPE_KIND_I32)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(entitlement.build())
        .build();
  }

  private static CatalogContext catalog(
      boolean path, int ruleColumn, boolean asTest, boolean ownPredicate) {
    return CatalogContext.newBuilder()
        .setContextId("hep-budget")
        .setEpoch(1L)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("main")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .setCapabilities(
                    SourceCapabilities.newBuilder()
                        .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE))
                .addTables(orders(path, ruleColumn, asTest, ownPredicate))
                .addTables(orderItems())
                .addTables(vendors())
                .addTables(members()))
        .build();
  }

  // ------------------------------------------------------------------ the principal

  /** The fixture's {@code u3}: one subject grant, confined within an organisation, and no other. */
  private static final RequestContext PARTICIPANT = subject(3, 2);

  /**
   * The same subject grant and two vendor grants in two different roles, which is what makes the
   * endpoint's role halves differ from the endpoint predicate and so puts a verdict column on the
   * path's side rather than folding it to a constant.
   */
  private static final RequestContext SUPPLIER_IN_TWO_ROLES = subject(3, 2, 1, 2);

  private static RequestContext subject(int member, int org, int... vendors) {
    return RequestContext.newBuilder()
        .addScalars(ContextScalar.newBuilder().setName("global").setValue(bool(false)))
        .addScalars(ContextScalar.newBuilder().setName("user").setValue(i32(member)))
        .addRelations(pairs("subject_pairs", member, org))
        .addRelations(list("vendor_admin", vendors.length > 0 ? vendors[0] : null))
        .addRelations(list("vendor_staff", vendors.length > 1 ? vendors[1] : null))
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

  private static ContextRelationValue list(String name, Integer... ids) {
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
    for (Integer id : ids) {
      if (id != null) {
        builder.addRows(VirtualRow.newBuilder().addValues(i32(id)));
      }
    }
    return builder.build();
  }

  private static ContextRelationValue pairs(String name, int member, int org) {
    return ContextRelationValue.newBuilder()
        .setName(name)
        .setKind(ContextRelationKind.CONTEXT_RELATION_KIND_LIST)
        .setRowType(
            RowType.newBuilder()
                .addFields(
                    Field.newBuilder()
                        .setName("member_id")
                        .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32)))
                .addFields(
                    Field.newBuilder()
                        .setName("org_id")
                        .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))))
        .addRows(VirtualRow.newBuilder().addValues(i32(member)).addValues(i32(org)))
        .build();
  }
}
