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
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.List;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.type.SqlTypeName;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * A column entitled along <b>several</b> paths at once (F73, D269 (a), design 43).
 *
 * <p>The catalog is design 38 §8's in miniature: an order line derives its organisation and its
 * member from its order and its vendor from its item, so it is the first table whose column rules
 * are decided at <em>two</em> endpoints and whose sanitiser must therefore switch on two verdict
 * ordinals. §4's rule for a row reached along several routes is the <b>least</b> ordinal — first
 * match wins across the routes, which is D222's precedence applied across routes — and the target
 * switches on the one ordinal D228 built.
 *
 * <p>The reproduction is {@code m7-tenancy} 39's shape: the join of the orders to their lines, read
 * by a principal holding a per-row grant along either route.
 */
class PathVerdictMeetTest {
  private static RegisteredCatalog catalog;

  /** {@code m7-tenancy} 39: the join of the orders to their lines. */
  private static final String THE_JOIN =
      "SELECT o.id, i.id, i.quantity, i.unit_price"
          + " FROM orders o JOIN order_items i ON i.order_id = o.id";

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

  // ------------------------------------------------------------------ the reproduction

  /**
   * The reproduction. Nine of the fifteen principals of §8's corpus are per-row along a route, and
   * for them the two-path sanitiser was a {@code CASE} with an ordinal where a condition belongs;
   * the client's validator refuses that plan with {@code I-IR-2: expected Bool but found I32}. This
   * is the same claim read on the planner's side of the wire, for every principal.
   */
  @Test
  void a_two_path_column_builds_no_case_that_switches_on_an_ordinal() throws Exception {
    for (String who : PRINCIPALS) {
      PlannerPipeline.Result result = plan(THE_JOIN, principal(who));
      assertThat(nonBooleanCaseConditions(result.physical()))
          .as("%s: a CASE condition is a boolean, whatever route the verdict came along", who)
          .isEmpty();
    }
  }

  /** The same over the lines alone, which is the shape without the target's own path beside it. */
  @Test
  void the_lines_alone_build_no_case_that_switches_on_an_ordinal() throws Exception {
    for (String who : PRINCIPALS) {
      PlannerPipeline.Result result =
          plan("SELECT id, quantity, unit_price FROM order_items", principal(who));
      assertThat(nonBooleanCaseConditions(result.physical())).as("%s", who).isEmpty();
    }
  }

  // ------------------------------------------------------------------ the meet

  /**
   * One route alone: the verdict is that route's, and a route whose endpoint predicate folded to
   * FALSE is not in the plan at all — it reaches no row, so it takes no part in the meet and has no
   * position for the child to read (which is what the reproduction above was about).
   */
  @Test
  void a_route_that_reaches_nothing_takes_no_part_in_the_meet() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, unit_price FROM order_items", principal("manager"));

    assertThat(leafOf(result, "order_items").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    assertThat(leafOf(result, "order_items").of(4)).isEqualTo(Disclosed.PER_ROW);
    assertThat(result.physicalPlanText())
        .as("the vendor route grants nothing and its chain is dropped")
        .doesNotContain("main, items");
    assertThat(verdictProjections(result.physicalPlanText()))
        .as("one route reaches the row, so one ordinal")
        .isEqualTo(1);
  }

  /**
   * Several routes reach the row, so every one of them <b>projects</b> its ordinal: a constant that
   * is the verdict of the rows a route reached says nothing about a row it did not reach, and the
   * LEFT JOIN's NULL is what says so (D269 (a)).
   */
  @Test
  void every_route_that_reaches_the_row_projects_its_own_ordinal() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, unit_price FROM order_items", principal("manager-and-vendor"));

    assertThat(verdictProjections(result.physicalPlanText())).isEqualTo(2);
    assertThat(leafOf(result, "order_items").of(4)).isEqualTo(Disclosed.PER_ROW);
  }

  /**
   * The combination itself: each route's ordinal read through the sentinel where it matched nothing,
   * and the least of them taken with the {@code CASE} on {@code >=} the report's own meet uses.
   * {@code LEAST} is a library function the IR does not carry and none is emitted.
   */
  @Test
  void the_meet_is_the_least_ordinal_across_the_routes() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, unit_price FROM order_items", principal("manager-and-vendor"));

    String text = result.physicalPlanText();
    assertThat(text).contains("COALESCE(");
    assertThat(text).containsPattern(">=\\(COALESCE\\(");
    assertThat(text).doesNotContain("LEAST");
  }

  /**
   * A principal whose grant along one route is stronger than along the other. The organisation's
   * rule is written first, so on a line both routes reach it wins; on a line only the vendor's route
   * reaches, the organisation's ordinal is the sentinel and the vendor's rule decides. That is
   * D222's precedence applied across routes, and it is why the verdict is a column rather than the
   * least of two constants.
   */
  @Test
  void a_stronger_grant_along_one_route_decides_the_rows_both_reach() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, unit_price FROM order_items", principal("vendor"));

    // Two routes reach a row of this table for this principal — the creator's fail-safe on the
    // order, and the vendor's own item — and each projects its own ordinal.
    assertThat(verdictProjections(result.physicalPlanText())).isEqualTo(2);
    assertThat(result.physicalPlanText()).contains("$chalk$verdict0=[CAST(1):INTEGER]");
    assertThat(result.physicalPlanText()).contains("$chalk$verdict0=[CAST(2):INTEGER]");
    assertThat(leafOf(result, "order_items").of(4)).isEqualTo(Disclosed.PER_ROW);
  }

  /**
   * The sentinel is one past the last rule, and no branch of the sanitiser tests for it: a row no
   * route matched a rule on falls through to the {@code ELSE} arm, which is the placeholder.
   */
  @Test
  void the_sentinel_lands_on_the_else_arm() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, unit_price FROM order_items", principal("manager-and-vendor"));

    // Two rules, so the sentinel is 3, and it is what a route that matched neither projects.
    assertThat(result.physicalPlanText()).contains("$chalk$verdict0=[CAST(CASE(");
    assertThat(result.physicalPlanText()).containsPattern(", 1, 3\\)\\):INTEGER\\]");
    // And the sanitiser has no arm for it: a row no route matched a rule on takes the ELSE, which
    // is the placeholder the column's `Otherwise` names.
    assertThat(sanitiserOf(result.physicalPlanText(), "unit_price")).endsWith("null:BIGINT)");
  }

  /** The expression one output column is computed by, as the plan text writes it. */
  private static String sanitiserOf(String text, String column) {
    int at = text.indexOf(column + "=[");
    assertThat(at).as("%s is projected", column).isNotNegative();
    int from = at + column.length() + 2;
    int depth = 0;
    for (int i = from; i < text.length(); i++) {
      char c = text.charAt(i);
      if (c == '[' || c == '(') {
        depth++;
      } else if (c == ')') {
        depth--;
      } else if (c == ']' && depth == 0) {
        return text.substring(from, i);
      } else if (c == ']') {
        depth--;
      }
    }
    throw new AssertionError("unterminated expression for " + column);
  }

  /** A global grant reaches every row along every route, and the column is nobody's placeholder. */
  @Test
  void a_global_grant_folds_the_meet_away() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, unit_price FROM order_items", principal("global"));

    assertThat(leafOf(result, "order_items").visibility()).isEqualTo(DisclosureMap.Visibility.ALL);
    assertThat(leafOf(result, "order_items").of(4)).isEqualTo(Disclosed.FULL);
    assertThat(result.physicalPlanText()).doesNotContain("$chalk$verdict");
  }

  /**
   * The report's label and the sanitiser agree branch for branch: a per-row verdict is reported
   * per-row, and a route that folded to one rule is reported as that rule's disclosure.
   */
  @Test
  void the_reports_label_agrees_with_the_branch_the_sanitiser_takes() throws Exception {
    for (String who : PRINCIPALS) {
      PlannerPipeline.Result result =
          plan("SELECT id, unit_price FROM order_items", principal(who));
      Disclosed price = leafOf(result, "order_items").of(4);
      assertThat(result.columnDisclosures().get(1))
          .as("%s: the leaf says %s", who, price)
          .isEqualTo(label(price));
    }
  }

  private static chalk.planner.rpc.v1.ReportedDisclosure label(Disclosed disclosed) {
    return switch (disclosed) {
      case FULL -> chalk.planner.rpc.v1.ReportedDisclosure.REPORTED_DISCLOSURE_FULL;
      case MASKED -> chalk.planner.rpc.v1.ReportedDisclosure.REPORTED_DISCLOSURE_MASKED;
      case REDACTED -> chalk.planner.rpc.v1.ReportedDisclosure.REPORTED_DISCLOSURE_REDACTED;
      case PER_ROW -> chalk.planner.rpc.v1.ReportedDisclosure.REPORTED_DISCLOSURE_PER_ROW;
      case AGGREGATE -> chalk.planner.rpc.v1.ReportedDisclosure.REPORTED_DISCLOSURE_AGGREGATE;
      case TESTED -> chalk.planner.rpc.v1.ReportedDisclosure.REPORTED_DISCLOSURE_TESTED;
    };
  }

  /** How many routes project an ordinal of their own in this plan. */
  private static int verdictProjections(String text) {
    int count = 0;
    int at = text.indexOf("$chalk$verdict0=[");
    while (at >= 0) {
      count++;
      at = text.indexOf("$chalk$verdict0=[", at + 1);
    }
    return count;
  }

  // ------------------------------------------------------------------ the walk

  /** Every {@code CASE} operand that stands in a condition position and is not a boolean. */
  private static List<String> nonBooleanCaseConditions(RelNode root) {
    List<String> bad = new ArrayList<>();
    Deque<RelNode> queue = new ArrayDeque<>();
    queue.add(root);
    while (!queue.isEmpty()) {
      RelNode rel = queue.removeFirst();
      queue.addAll(rel.getInputs());
      rel.accept(
          new org.apache.calcite.rex.RexShuttle() {
            @Override
            public RexNode visitCall(RexCall call) {
              if (call.getKind() == SqlKind.CASE) {
                List<RexNode> operands = call.getOperands();
                for (int i = 0; i + 1 < operands.size(); i += 2) {
                  if (operands.get(i).getType().getSqlTypeName() != SqlTypeName.BOOLEAN) {
                    bad.add(
                        rel.getRelTypeName()
                            + ": CASE condition "
                            + operands.get(i)
                            + " is "
                            + operands.get(i).getType()
                            + ", not BOOLEAN — "
                            + call);
                  }
                }
              }
              return super.visitCall(call);
            }
          });
    }
    return bad;
  }

  // ------------------------------------------------------------------ the catalog

  /**
   * The organisation's half of an order's rows: every way in that is not the subject's and not the
   * path's, which is what an order line inherits by kind for {@code org} (design 38 §8).
   */
  private static final String ORDER_ORG_ROWS =
      "org_id IN (@ctx.manager_orgs) OR org_id IN (@ctx.agent_orgs)"
          + " OR @ctx.global OR created_by = @ctx.user";

  /** The subject's half, which is the endpoint predicate of the line's {@code member} path. */
  private static final String ORDER_SUBJECT = "(member_id, org_id) IN (@ctx.subject_pairs)";

  private static final String ORDER_ROWS = ORDER_ORG_ROWS + " OR " + ORDER_SUBJECT;

  /** Where the vendor kind lives {@code Direct}ly, and every vendor path's endpoint predicate. */
  private static final String ITEM_ROWS = "vendor_id IN (@ctx.vendor_vendor) OR @ctx.global";

  /** A vendor's reach on the target, decided at the endpoint of the path (§2, §4). */
  private static final String THE_VENDORS_ITEM = "items.vendor_id IN (@ctx.vendor_vendor)";

  /** The other route into a line: through its order, whose columns the rule may name (§2). */
  private static final String LINE_THROUGH_ITS_ORDER =
      "orders.org_id IN (@ctx.manager_orgs) OR orders.org_id IN (@ctx.agent_orgs)"
          + " OR @ctx.global OR orders.created_by = @ctx.user"
          + " OR (orders.member_id, orders.org_id) IN (@ctx.subject_pairs)";

  private static Table orders() {
    return Table.newBuilder()
        .setName("orders")
        .setRowCount(200)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("member_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("created_by", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("amount", type(TypeKind.TYPE_KIND_I64)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate(ORDER_ROWS)
                .setDescriptorHash("11112222333344441111222233334444")
                // The target of §8's Related path: up to the lines, down to the items.
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
                        .setEndpointPredicate(ITEM_ROWS)
                        .setEndpointTable("items"))
                // §8's rule order: the organisation's own reach first, the vendor's second.
                .addColumns(
                    ColumnEntitlement.newBuilder()
                        .setColumn(1)
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen(ORDER_ROWS)
                                .setThen(Disclosure.DISCLOSURE_FULL))
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen(THE_VENDORS_ITEM)
                                .setThen(Disclosure.DISCLOSURE_NONE))
                        .setOtherwise(Disclosure.DISCLOSURE_NONE))
                .addColumns(
                    ColumnEntitlement.newBuilder()
                        .setColumn(2)
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen(ORDER_ROWS)
                                .setThen(Disclosure.DISCLOSURE_FULL))
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen(THE_VENDORS_ITEM)
                                .setThen(Disclosure.DISCLOSURE_TEST)
                                .addTests(TestShape.TEST_SHAPE_EQUALS))
                        .setOtherwise(Disclosure.DISCLOSURE_NONE))
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
                .setRowPredicate("id IN (@ctx.vendor_vendor) OR @ctx.global")
                .setDescriptorHash("aaaabbbbccccddddaaaabbbbccccdddd")
                .build())
        .build();
  }

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
                .setRowPredicate(ITEM_ROWS)
                .setDescriptorHash("55556666777788885555666677778888")
                .build())
        .build();
  }

  /**
   * The bridge, entitled by kind (design 38 §8): its organisation and its member from its order,
   * its vendor from its item — three restrictions over two endpoints, which is what makes
   * {@code unit_price} the first column decided along several paths at once.
   */
  private static Table orderItems() {
    return Table.newBuilder()
        .setName("order_items")
        .setRowCount(900)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("order_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("item_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("quantity", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("unit_price", type(TypeKind.TYPE_KIND_I64)))
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
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setDescriptorHash("99998888777766669999888877776666")
                .addInherited(
                    InheritedVisibility.newBuilder()
                        .setKind("org")
                        .addSteps(toTheOrder())
                        .setEndpointPredicate(ORDER_ORG_ROWS)
                        .setEndpointTable("orders"))
                .addInherited(
                    InheritedVisibility.newBuilder()
                        .setKind("member")
                        .addSteps(toTheOrder())
                        .setEndpointPredicate(ORDER_SUBJECT)
                        .setEndpointTable("orders"))
                .addInherited(
                    InheritedVisibility.newBuilder()
                        .setKind("vendor")
                        .addSteps(toTheItem())
                        .setEndpointPredicate(ITEM_ROWS)
                        .setEndpointTable("items"))
                .addColumns(
                    ColumnEntitlement.newBuilder()
                        .setColumn(4)
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen(LINE_THROUGH_ITS_ORDER)
                                .setThen(Disclosure.DISCLOSURE_FULL))
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen(THE_VENDORS_ITEM)
                                .setThen(Disclosure.DISCLOSURE_NONE))
                        .setOtherwise(Disclosure.DISCLOSURE_NONE))
                .build())
        .build();
  }

  private static VisibilityStep.Builder toTheOrder() {
    return VisibilityStep.newBuilder()
        .setTable("orders")
        .setFromColumn(1)
        .setToColumn(0)
        .setDirection(StepDirection.STEP_DIRECTION_TO_PARENT);
  }

  private static VisibilityStep.Builder toTheItem() {
    return VisibilityStep.newBuilder()
        .setTable("items")
        .setFromColumn(2)
        .setToColumn(0)
        .setDirection(StepDirection.STEP_DIRECTION_TO_PARENT);
  }

  private static CatalogContext marketplace() {
    return CatalogContext.newBuilder()
        .setContextId("meet")
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
                .addTables(items())
                .addTables(vendors()))
        .build();
  }

  // ------------------------------------------------------------------ the principals

  /** §8's shapes: each route alone, both at once, a subject grant, a global one, and nothing. */
  private static final List<String> PRINCIPALS =
      List.of("manager", "vendor", "manager-and-vendor", "subject", "subject-and-vendor",
          "global", "nobody");

  private static RequestContext principal(String who) {
    return switch (who) {
      case "manager" -> bindings(new int[] {1}, new int[0], new int[0][], false);
      case "vendor" -> bindings(new int[0], new int[] {1}, new int[0][], false);
      case "manager-and-vendor" -> bindings(new int[] {1}, new int[] {1}, new int[0][], false);
      case "subject" -> bindings(new int[0], new int[0], new int[][] {{7, 1}}, false);
      case "subject-and-vendor" ->
          bindings(new int[0], new int[] {1}, new int[][] {{7, 1}}, false);
      case "global" -> bindings(new int[0], new int[0], new int[0][], true);
      case "nobody" -> bindings(new int[0], new int[0], new int[0][], false);
      default -> throw new AssertionError(who);
    };
  }

  private static RequestContext bindings(
      int[] managerOrgs, int[] vendors, int[][] pairs, boolean everywhere) {
    return RequestContext.newBuilder()
        .addScalars(ContextScalar.newBuilder().setName("global").setValue(bool(everywhere)))
        .addScalars(ContextScalar.newBuilder().setName("user").setValue(i32(15)))
        .addRelations(list("manager_orgs", managerOrgs))
        .addRelations(list("agent_orgs", new int[0]))
        .addRelations(list("vendor_vendor", vendors))
        .addRelations(pairList("subject_pairs", pairs))
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

  private static ContextRelationValue pairList(String name, int[][] pairs) {
    ContextRelationValue.Builder builder =
        ContextRelationValue.newBuilder()
            .setName(name)
            .setKind(ContextRelationKind.CONTEXT_RELATION_KIND_LIST)
            .setRowType(
                RowType.newBuilder()
                    .addFields(
                        Field.newBuilder()
                            .setName("subject")
                            .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32)))
                    .addFields(
                        Field.newBuilder()
                            .setName("within")
                            .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))));
    for (int[] pair : pairs) {
      builder.addRows(VirtualRow.newBuilder().addValues(i32(pair[0])).addValues(i32(pair[1])));
    }
    return builder.build();
  }
}
