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
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.Disclosed;
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
 * A statement's own equality settles the rules it decides, beside a list read by lookup (F162).
 *
 * <p>{@code members} admits the rows of the principal's organisations and of the people it was
 * given, and masks {@code first_name} to its initial in those organisations and withholds it
 * elsewhere. Bound partially, as a host binds the subject lists at execution, the organisations fold
 * to a literal list and the people stay a lookup, so the leaf's filter is {@code org_id IN (1, 2) OR
 * CHALK_CONTEXT_MEMBERSHIP('people', id)}. Beside it Calcite takes no constant from {@code WHERE
 * org_id = 3}, since it leaves out any column a predicate other than an equality mentions; the pass
 * takes it from the statement's equalities alone, and settles {@code first_name}'s rule with it.
 *
 * <p>{@code accounts} discloses {@code org_id} itself only in the principal's organisations and
 * withholds it as NULL elsewhere. The constant settles that rule too, but never stands in for the
 * value: the statement's filter reads what is disclosed, so {@code WHERE org_id = 3} keeps no row.
 */
class StatementEqualityTest {
  private static RegisteredCatalog catalog;

  @BeforeAll
  static void register() {
    Table members =
        Table.newBuilder()
            .setName("members")
            .setRowCount(40)
            .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
            .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
            .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
            .addColumns(column("first_name", nullable(TypeKind.TYPE_KIND_STRING)))
            .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
            .setEntitlement(
                TableEntitlement.newBuilder()
                    .setDescriptorHash("5eed5eed5eed5eed5eed5eed5eed5eed")
                    .setRowPredicate("org_id IN (@ctx.orgs) OR id IN (@ctx.people)")
                    .addColumns(
                        ColumnEntitlement.newBuilder()
                            .setColumn(2)
                            .setMask("SUBSTRING(first_name, 1, 1)")
                            .addRules(
                                DisclosureRule.newBuilder()
                                    .setWhen("org_id IN (@ctx.orgs)")
                                    .setThen(Disclosure.DISCLOSURE_MASKED))
                            .setOtherwise(Disclosure.DISCLOSURE_NONE)))
            .build();
    Table accounts =
        Table.newBuilder()
            .setName("accounts")
            .setRowCount(40)
            .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
            .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
            .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
            .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
            .setEntitlement(
                TableEntitlement.newBuilder()
                    .setDescriptorHash("acc0acc0acc0acc0acc0acc0acc0acc0")
                    .setRowPredicate("org_id IN (@ctx.orgs) OR id IN (@ctx.people)")
                    .addColumns(
                        ColumnEntitlement.newBuilder()
                            .setColumn(1)
                            .addRules(
                                DisclosureRule.newBuilder()
                                    .setWhen("org_id IN (@ctx.orgs)")
                                    .setThen(Disclosure.DISCLOSURE_FULL))
                            .setOtherwise(Disclosure.DISCLOSURE_NONE)))
            .build();
    catalog =
        new CatalogRegistry()
            .register(
                CatalogContext.newBuilder()
                    .setContextId("statement-equalities")
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
                            .addTables(members)
                            .addTables(accounts))
                    .build());
  }

  /** Organisations 1 and 2 bound, the people left to execution. */
  private static RequestContext partial() {
    return RequestContext.newBuilder()
        .addRelations(ids("orgs", false, 1, 2))
        .addRelations(ids("people", true))
        .build();
  }

  /** Both lists left to execution. */
  private static RequestContext shape() {
    return RequestContext.newBuilder()
        .setShapeOnly(true)
        .addRelations(ids("orgs", true))
        .addRelations(ids("people", true))
        .build();
  }

  private static ContextRelationValue ids(String name, boolean shape, int... ids) {
    ContextRelationValue.Builder list =
        ContextRelationValue.newBuilder()
            .setName(name)
            .setKind(ContextRelationKind.CONTEXT_RELATION_KIND_LIST)
            .setShape(shape)
            .setRowType(
                RowType.newBuilder()
                    .addFields(
                        Field.newBuilder()
                            .setName("id")
                            .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))));
    for (int id : ids) {
      list.addRows(
          VirtualRow.newBuilder()
              .addValues(
                  Expr.newBuilder()
                      .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))
                      .setLiteral(Literal.newBuilder().setI32Value(id))));
    }
    return list.build();
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

  @Test
  void without_an_equality_the_rule_is_decided_per_row() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, first_name FROM members", partial());

    assertThat(result.entitledLeaves().get(0).of(2)).isEqualTo(Disclosed.PER_ROW);
    assertThat(result.physicalPlanText())
        .contains("CHALK_CONTEXT_MEMBERSHIP('people'")
        .contains("CASE");
  }

  @Test
  void an_equality_outside_the_organisations_withholds_the_column_on_every_row() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, first_name FROM members WHERE org_id = 3", partial());

    // Organisation 3's rows are admitted only as people, and none of them is in 1 or 2: the name is
    // a NULL, not a CASE over the organisation, and the statement's own filter stays.
    assertThat(result.entitledLeaves().get(0).of(2)).isEqualTo(Disclosed.REDACTED);
    assertThat(result.physicalPlanText())
        .doesNotContain("CASE")
        .contains("first_name=[null:VARCHAR")
        .contains("=($1, 3)");
  }

  @Test
  void an_equality_inside_the_organisations_masks_the_column_on_every_row() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, first_name FROM members WHERE org_id = 1", partial());

    assertThat(result.entitledLeaves().get(0).of(2)).isEqualTo(Disclosed.MASKED);
    assertThat(result.physicalPlanText()).doesNotContain("CASE").contains("SUBSTRING");
  }

  @Test
  void bound_at_execution_the_organisations_are_not_known_and_nothing_is_settled() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, first_name FROM members WHERE org_id = 3", shape());

    // The rule is left as it was: a lookup of the constant is no more settled than one of the column.
    assertThat(result.entitledLeaves().get(0).of(2)).isEqualTo(Disclosed.PER_ROW);
    assertThat(result.physicalPlanText())
        .contains("CASE(CHALK_CONTEXT_MEMBERSHIP('orgs', $1)")
        .doesNotContain("CHALK_CONTEXT_MEMBERSHIP('orgs', 3)");
  }

  @Test
  void the_statements_filter_reads_the_value_disclosed_not_the_constant() throws Exception {
    // Where org_id is 3 the rule withholds it, as NULL, and a NULL fails org_id = 3: no row is kept.
    PlannerPipeline.Result outside =
        plan("SELECT id, org_id FROM accounts WHERE org_id = 3", partial());
    assertThat(outside.entitledLeaves().get(0).of(1)).isEqualTo(Disclosed.REDACTED);
    assertThat(outside.physicalPlanText()).contains("Values(tuples=[[]])");

    // Where it is 1 the rule discloses it, and the filter reads the raw value.
    PlannerPipeline.Result inside =
        plan("SELECT id, org_id FROM accounts WHERE org_id = 1", partial());
    assertThat(inside.entitledLeaves().get(0).of(1)).isEqualTo(Disclosed.FULL);
    assertThat(inside.physicalPlanText()).doesNotContain("CASE").contains("=($1, 1)");
  }
}
