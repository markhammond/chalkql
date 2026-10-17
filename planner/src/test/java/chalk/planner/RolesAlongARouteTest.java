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
import chalk.planner.rpc.v1.RequestContext;
import java.util.List;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * A rule for a role held along a declared path decides only the rows the path reaches (D279 §4).
 *
 * <p>The reader holds the role in exactly the endpoint rows the path admits — one grant, on its own
 * id — so the rule's role half folds to TRUE on the path's side. That is the verdict of the rows the
 * path reached. Where the table makes a row visible by itself as well, by a row predicate, the
 * verdict is read where the path matched, and the column's disclosure varies by row. Where the path
 * is the only way to a row, every row it shows was reached by it, and the verdict stays a constant.
 */
class RolesAlongARouteTest {
  private static RegisteredCatalog catalog;

  @BeforeAll
  static void register() {
    catalog = new CatalogRegistry().register(route());
  }

  /** A member of organisation 1 and reader 7, who has consented to what a grant row shares with it. */
  private static RequestContext memberAndReader() {
    return RequestContext.newBuilder()
        .addRelations(list("org_member", 1))
        .addRelations(list("reader_consented", 7))
        .build();
  }

  /**
   * Notes held by organisation and along their grants, with rules over both: one boolean per rule.
   * A note shared with the reader is read in full and one it reaches only as a member is not.
   */
  @Test
  void a_path_role_beside_a_row_predicate_is_decided_per_row() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, body FROM notes", memberAndReader());

    assertThat(leafOf(result, "notes").of(2)).isEqualTo(Disclosed.PER_ROW);
  }

  /** Documents with one rule, for the path's role: every rule decided on the path's side, an ordinal. */
  @Test
  void a_path_role_decided_by_ordinal_beside_a_row_predicate_is_decided_per_row() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, body FROM docs", memberAndReader());

    assertThat(leafOf(result, "docs").of(2)).isEqualTo(Disclosed.PER_ROW);
  }

  /** Cards held along their grants alone: every card the reader reads, the path reached. */
  @Test
  void a_path_role_on_a_table_the_path_alone_reaches_stays_constant() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, body FROM cards", memberAndReader());

    assertThat(leafOf(result, "cards").of(1)).isEqualTo(Disclosed.FULL);
    assertThat(leafOf(result, "cards").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
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

  // ------------------------------------------------------------------ the catalog

  private static CatalogContext route() {
    return CatalogContext.newBuilder()
        .setContextId("route")
        .setEpoch(1L)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("main")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .setCapabilities(
                    SourceCapabilities.newBuilder()
                        .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE))
                .addTables(readers())
                .addTables(target("notes", "11110000111100001111000011110000", true, true))
                .addTables(grants("note_grants", "notes"))
                .addTables(target("docs", "22220000222200002222000022220000", true, false))
                .addTables(grants("doc_grants", "docs"))
                .addTables(target("cards", "33330000333300003333000033330000", false, false))
                .addTables(grants("card_grants", "cards")))
        .build();
  }

  /** The readers: the path's endpoint, where the reader's own grant lives. */
  private static Table readers() {
    return Table.newBuilder()
        .setName("readers")
        .setRowCount(20)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate("id IN (@ctx.reader_consented)")
                .setDescriptorHash("aaaa0000aaaa0000aaaa0000aaaa0000")
                .build())
        .build();
  }

  /**
   * A table reached along its grants: down to them, up to the reader they name. With {@code byOrg}
   * it holds its organisation by itself too, and with {@code memberRule} its member's rule sits
   * beside the path's, so the rules read both the row and the path.
   */
  private static Table target(String name, String hash, boolean byOrg, boolean memberRule) {
    String grants = name.substring(0, name.length() - 1) + "_grants";
    TableEntitlement.Builder entitlement =
        TableEntitlement.newBuilder()
            .setDescriptorHash(hash)
            .addInherited(
                InheritedVisibility.newBuilder()
                    .setKind("reader")
                    .addSteps(
                        VisibilityStep.newBuilder()
                            .setTable(grants)
                            .setFromColumn(0)
                            .setToColumn(1)
                            .setDirection(StepDirection.STEP_DIRECTION_TO_CHILD))
                    .addSteps(
                        VisibilityStep.newBuilder()
                            .setTable("readers")
                            .setFromColumn(2)
                            .setToColumn(0)
                            .setDirection(StepDirection.STEP_DIRECTION_TO_PARENT))
                    .setEndpointPredicate("id IN (@ctx.reader_consented)")
                    .setEndpointTable("readers"));
    if (byOrg) {
      entitlement.setRowPredicate("org_id IN (@ctx.org_member)");
    }

    int body = byOrg ? 2 : 1;
    ColumnEntitlement.Builder rules =
        ColumnEntitlement.newBuilder()
            .setColumn(body)
            .addRules(
                DisclosureRule.newBuilder()
                    .setWhen("readers.id IN (@ctx.reader_consented)")
                    .setThen(Disclosure.DISCLOSURE_FULL))
            .setOtherwise(Disclosure.DISCLOSURE_NONE);
    if (memberRule) {
      rules.addRules(
          DisclosureRule.newBuilder()
              .setWhen("org_id IN (@ctx.org_member)")
              .setThen(Disclosure.DISCLOSURE_NONE));
    }
    entitlement.addColumns(rules);

    Table.Builder table =
        Table.newBuilder()
            .setName(name)
            .setRowCount(200)
            .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)));
    if (byOrg) {
      table.addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)));
    }
    return table
        .addColumns(column("body", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(entitlement)
        .build();
  }

  /** The bridge: which reader a row of {@code target} is shared with. */
  private static Table grants(String name, String target) {
    return Table.newBuilder()
        .setName(name)
        .setRowCount(600)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("target_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("reader_id", type(TypeKind.TYPE_KIND_I32)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addForeignKeys(
            ForeignKey.newBuilder()
                .setName(name + "_target")
                .addColumns(1)
                .setParentTable(target)
                .addParentColumns(0))
        .addForeignKeys(
            ForeignKey.newBuilder()
                .setName(name + "_reader")
                .addColumns(2)
                .setParentTable("readers")
                .addParentColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  // ------------------------------------------------------------------ the context

  private static ContextRelationValue list(String name, int... ids) {
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

  private static Expr i32(int value) {
    return Expr.newBuilder()
        .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))
        .setLiteral(Literal.newBuilder().setI32Value(value))
        .build();
  }
}
