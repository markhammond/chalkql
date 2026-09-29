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
 * A statement's own conjunct decides a disclosure rule only where it speaks of the raw row (F163).
 *
 * <p>The statement's filter reads the leaf's sanitised row. {@code last_name} is masked to its
 * initial for everyone but a manager, and {@code note} is disclosed only where the last name is
 * exactly S: {@code WHERE last_name = 'S'} holds for Smith, whose initial is S, and must not settle
 * {@code note}'s rule as though his name were. {@code org_id} is disclosed raw, so a conjunct over it
 * still settles {@code memo}'s rule. So does a conjunct no NULL satisfies over a column withheld as
 * NULL — every row it keeps carries the raw value — but not one a NULL satisfies, nor one over a
 * column withheld as {@code 0}.
 */
class StatementConjunctTest {
  private static RegisteredCatalog catalog;

  @BeforeAll
  static void register() {
    Table table =
        Table.newBuilder()
            .setName("cards")
            .setRowCount(40)
            .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
            .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
            .addColumns(column("last_name", type(TypeKind.TYPE_KIND_STRING)))
            .addColumns(column("note", nullable(TypeKind.TYPE_KIND_STRING)))
            .addColumns(column("memo", nullable(TypeKind.TYPE_KIND_STRING)))
            .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
            .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
            .setEntitlement(
                TableEntitlement.newBuilder()
                    .setDescriptorHash("5c5c5c5c5c5c5c5c5c5c5c5c5c5c5c5c")
                    .addColumns(
                        ColumnEntitlement.newBuilder()
                            .setColumn(1)
                            .addRules(
                                DisclosureRule.newBuilder()
                                    .setWhen("org_id IN (@ctx.managers)")
                                    .setThen(Disclosure.DISCLOSURE_FULL))
                            .setOtherwise(Disclosure.DISCLOSURE_MASKED)
                            .setMask("SUBSTRING(last_name, 1, 1)"))
                    .addColumns(
                        ColumnEntitlement.newBuilder()
                            .setColumn(2)
                            .addRules(
                                DisclosureRule.newBuilder()
                                    .setWhen("last_name = 'S'")
                                    .setThen(Disclosure.DISCLOSURE_FULL))
                            .setOtherwise(Disclosure.DISCLOSURE_NONE))
                    .addColumns(
                        ColumnEntitlement.newBuilder()
                            .setColumn(3)
                            .addRules(
                                DisclosureRule.newBuilder()
                                    .setWhen("org_id = 1")
                                    .setThen(Disclosure.DISCLOSURE_FULL))
                            .setOtherwise(Disclosure.DISCLOSURE_NONE)))
            .build();
    catalog =
        new CatalogRegistry()
            .register(
                CatalogContext.newBuilder()
                    .setContextId("statement-conjuncts")
                    .setEpoch(1)
                    .addSchemas(
                        Schema.newBuilder()
                            .setSourceId("mem")
                            .setName("main")
                            .setKind(SourceKind.SOURCE_KIND_LOCAL)
                            .setCapabilities(
                                SourceCapabilities.newBuilder()
                                    .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE))
                            .addTables(table)
                            .addTables(accounts("accounts", null))
                            .addTables(accounts("zeroed", "0")))
                    .build());
  }

  /**
   * An account's {@code org_id} disclosed whole in the principal's organisations and withheld
   * elsewhere — as NULL, or as {@code placeholder} — with {@code note} disclosed where the row is
   * organisation 1's, {@code memo} where it is not organisation 2's, and {@code aside} where its
   * organisation is 1 or unknown.
   */
  private static Table accounts(String name, String placeholder) {
    ColumnEntitlement.Builder org =
        ColumnEntitlement.newBuilder()
            .setColumn(1)
            .addRules(
                DisclosureRule.newBuilder()
                    .setWhen("org_id IN (@ctx.orgs)")
                    .setThen(Disclosure.DISCLOSURE_FULL))
            .setOtherwise(Disclosure.DISCLOSURE_NONE);
    if (placeholder != null) {
      org.setPlaceholder(placeholder);
    }
    return Table.newBuilder()
        .setName(name)
        .setRowCount(40)
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        // Nullable, so a COALESCE over it is not simplified away before the pass sees it.
        .addColumns(column("org_id", nullable(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("note", nullable(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("memo", nullable(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("aside", nullable(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setDescriptorHash(placeholder == null
                    ? "6d6d6d6d6d6d6d6d6d6d6d6d6d6d6d6d"
                    : "7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e")
                .addColumns(org)
                .addColumns(disclosedWhen(2, "org_id = 1"))
                .addColumns(disclosedWhen(3, "org_id <> 2"))
                .addColumns(disclosedWhen(4, "COALESCE(org_id, 1) = 1")))
        .build();
  }

  private static ColumnEntitlement disclosedWhen(int column, String when) {
    return ColumnEntitlement.newBuilder()
        .setColumn(column)
        .addRules(DisclosureRule.newBuilder().setWhen(when).setThen(Disclosure.DISCLOSURE_FULL))
        .setOtherwise(Disclosure.DISCLOSURE_NONE)
        .build();
  }

  /** A manager of no organisation, in organisation 1: every last name is its initial. */
  private static RequestContext agent() {
    return RequestContext.newBuilder()
        .addRelations(ids("managers"))
        .addRelations(ids("orgs", 1))
        .build();
  }

  private static ContextRelationValue ids(String name, int... ids) {
    ContextRelationValue.Builder list =
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
      list.addRows(
          VirtualRow.newBuilder()
              .addValues(
                  Expr.newBuilder()
                      .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))
                      .setLiteral(Literal.newBuilder().setI32Value(id))));
    }
    return list.build();
  }

  private static PlannerPipeline.Result plan(String sql) throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            catalog,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            BoundContext.of(agent()),
            PolicyOptions.DEFAULTS)) {
      return pipeline.plan(sql, true);
    }
  }

  @Test
  void a_filter_on_a_masked_column_decides_no_rule_over_it() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, note FROM cards WHERE last_name = 'S'");

    // The note stays per row, decided on the raw name, beside a filter that reads the initial.
    assertThat(result.entitledLeaves().get(0).of(2)).isEqualTo(Disclosed.PER_ROW);
    assertThat(result.physicalPlanText())
        .contains("CASE(=($1, 'S'), $2")
        .contains("=(SUBSTRING($1, 1, 1), 'S')");
  }

  @Test
  void a_filter_on_a_raw_column_still_settles_a_rule_over_it() throws Exception {
    assertThat(plan("SELECT id, memo FROM cards").entitledLeaves().get(0).of(3))
        .isEqualTo(Disclosed.PER_ROW);

    PlannerPipeline.Result result = plan("SELECT id, memo FROM cards WHERE org_id = 1");
    assertThat(result.entitledLeaves().get(0).of(3)).isEqualTo(Disclosed.FULL);
    assertThat(result.physicalPlanText()).doesNotContain("CASE");
  }

  @Test
  void a_filter_no_null_passes_on_a_column_withheld_as_null_settles_a_rule_over_it() throws Exception {
    // A row the rules withhold org_id from carries a NULL there, which fails org_id = 1: every row
    // kept is organisation 1's, and says so raw.
    assertThat(plan("SELECT id, org_id FROM accounts").entitledLeaves().get(0).of(1))
        .isEqualTo(Disclosed.PER_ROW);
    assertThat(plan("SELECT id, org_id FROM accounts WHERE org_id = 1").entitledLeaves().get(0).of(1))
        .isEqualTo(Disclosed.FULL);
    assertThat(plan("SELECT id, note FROM accounts WHERE org_id = 1").entitledLeaves().get(0).of(2))
        .isEqualTo(Disclosed.FULL);
  }

  @Test
  void a_filter_a_null_passes_decides_no_rule() throws Exception {
    // A withheld org_id is NULL, so COALESCE(org_id, 1) = 1 keeps organisation 2's rows too, where
    // the rule over the raw value withholds `aside`: taken as a fact, the conjunct would settle it.
    assertThat(
            plan("SELECT id, aside FROM accounts WHERE COALESCE(org_id, 1) = 1")
                .entitledLeaves()
                .get(0)
                .of(4))
        .isEqualTo(Disclosed.PER_ROW);
  }

  @Test
  void a_filter_a_placeholder_passes_decides_no_rule() throws Exception {
    // Withheld as 0, organisation 2's org_id passes org_id <> 2: its memo stays withheld.
    assertThat(plan("SELECT id, memo FROM zeroed WHERE org_id <> 2").entitledLeaves().get(0).of(3))
        .isEqualTo(Disclosed.PER_ROW);
  }
}
