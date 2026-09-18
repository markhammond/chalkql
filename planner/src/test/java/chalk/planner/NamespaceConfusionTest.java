package chalk.planner;

import static chalk.planner.TestCatalogs.column;
import static chalk.planner.TestCatalogs.type;
import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.ColumnEntitlement;
import chalk.ir.v1.Disclosure;
import chalk.ir.v1.DisclosureRule;
import chalk.ir.v1.Expr;
import chalk.ir.v1.Literal;
import chalk.ir.v1.QueryLanguage;
import chalk.ir.v1.RowCountKind;
import chalk.ir.v1.Schema;
import chalk.ir.v1.SourceCapabilities;
import chalk.ir.v1.SourceKind;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableEntitlement;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.UniqueKey;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.DisclosureMap;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.ContextRelationKind;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.ContextScalar;
import chalk.planner.rpc.v1.RequestContext;
import chalk.ir.v1.Field;
import chalk.ir.v1.RowType;
import chalk.ir.v1.VirtualRow;
import java.util.List;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * Namespace confusion: two schemas of one catalog holding a table of the same name, entitled
 * differently, in one statement (D270, docs/design/45-typed-tenancy-surface.md §1 as amended
 * 2026-09-16).
 *
 * <p>The client's side of this is that a policy obtains a {@code Table} handle from a {@code Source}
 * and never from a bare name. The planner's side is what this asserts: a leaf is rewritten under
 * <em>its own</em> table's descriptor, so two occurrences of {@code orders} in one plan carry two
 * descriptor hashes and two folded predicates, and an unqualified name reads the default schema's
 * leaf alone. A pass that keyed anything by the bare table name would hand one source's rules to the
 * other's rows, which is the shape a leak has.
 */
class NamespaceConfusionTest {
  private static RegisteredCatalog catalog;

  @BeforeAll
  static void register() {
    catalog = new CatalogRegistry().register(twoSchemas());
  }

  // ------------------------------------------------------------------ the catalog

  /** The ledger's hash, which no archive leaf may ever carry. */
  private static final String LEDGER_HASH = "1111111122222222111111112222222";

  /** The archive's, which no ledger leaf may ever carry. */
  private static final String ARCHIVE_HASH = "3333333344444444333333334444444";

  /**
   * Two sources, each holding {@code orders} with the same columns and different rules: the ledger's
   * rows resolve by organisation and disclose {@code amount} to a manager, the archive's resolve by
   * region and disclose it to nobody. The two are told apart by nothing but their schema.
   */
  private static CatalogContext twoSchemas() {
    return CatalogContext.newBuilder()
        .setContextId("namespace")
        .setEpoch(1L)
        .addSchemas(schema("ledger-mem", "ledger", ledgerOrders()))
        .addSchemas(schema("archive-mem", "archive", archiveOrders()))
        .build();
  }

  private static Schema schema(String sourceId, String name, Table orders) {
    return Schema.newBuilder()
        .setSourceId(sourceId)
        .setName(name)
        .setKind(SourceKind.SOURCE_KIND_LOCAL)
        .setCapabilities(
            SourceCapabilities.newBuilder()
                .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE)
                .build())
        .addTables(orders)
        .build();
  }

  private static Table.Builder orders() {
    return Table.newBuilder()
        .setName("orders")
        .setRowCount(40)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("region_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("amount", type(TypeKind.TYPE_KIND_I64)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT);
  }

  private static Table ledgerOrders() {
    return orders()
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate("org_id IN (@ctx.manager_orgs)")
                .setDescriptorHash(LEDGER_HASH)
                .addColumns(
                    ColumnEntitlement.newBuilder()
                        .setColumn(3)
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen("org_id IN (@ctx.manager_orgs)")
                                .setThen(Disclosure.DISCLOSURE_FULL))
                        .setOtherwise(Disclosure.DISCLOSURE_NONE))
                .build())
        .build();
  }

  private static Table archiveOrders() {
    return orders()
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate("region_id IN (@ctx.keeper_regions)")
                .setDescriptorHash(ARCHIVE_HASH)
                .addColumns(
                    ColumnEntitlement.newBuilder()
                        .setColumn(3)
                        .setOtherwise(Disclosure.DISCLOSURE_NONE))
                .build())
        .build();
  }

  // ------------------------------------------------------------------ the principal

  /** A manager in one organisation and a keeper of one region: entitled in both, differently. */
  private static RequestContext principal(int org, int region) {
    return RequestContext.newBuilder()
        .addScalars(ContextScalar.newBuilder().setName("user").setValue(i32(1)))
        .addRelations(list("manager_orgs", org))
        .addRelations(list("keeper_regions", region))
        .build();
  }

  private static Expr i32(int value) {
    return Expr.newBuilder()
        .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))
        .setLiteral(Literal.newBuilder().setI32Value(value))
        .build();
  }

  private static ContextRelationValue list(String name, int... values) {
    ContextRelationValue.Builder builder =
        ContextRelationValue.newBuilder()
            .setName(name)
            .setKind(ContextRelationKind.CONTEXT_RELATION_KIND_LIST)
            .setRowType(
                RowType.newBuilder()
                    .addFields(
                        Field.newBuilder()
                            .setName("id")
                            .setType(type(TypeKind.TYPE_KIND_I32))));
    for (int value : values) {
      builder.addRows(VirtualRow.newBuilder().addValues(i32(value)));
    }
    return builder.build();
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

  private static DisclosureMap leaf(PlannerPipeline.Result result, String qualified) {
    return result.entitledLeaves().stream()
        .filter(map -> map.qualifiedName().equals(qualified))
        .findFirst()
        .orElseThrow(() -> new AssertionError("no entitled leaf for " + qualified));
  }

  // ------------------------------------------------------------------ the join

  /**
   * Each leaf carries its own table's descriptor and its own table's folded predicate. The two
   * hashes are the proof: the digest covers them, so a plan whose archive leaf carried the ledger's
   * hash would be a plan compiled under a policy nobody wrote.
   */
  @Test
  void each_leaf_of_a_join_across_two_schemas_carries_its_own_descriptor() throws Exception {
    PlannerPipeline.Result result =
        plan(
            "SELECT l.id, l.amount, a.amount FROM ledger.orders l"
                + " JOIN archive.orders a ON a.id = l.id",
            principal(1, 7));

    assertThat(result.entitledLeaves()).hasSize(2);
    assertThat(result.entitledLeaves())
        .extracting(DisclosureMap::qualifiedName)
        .containsExactlyInAnyOrder("ledger.orders", "archive.orders");

    assertThat(leaf(result, "ledger.orders").descriptorHash()).isEqualTo(LEDGER_HASH);
    assertThat(leaf(result, "archive.orders").descriptorHash()).isEqualTo(ARCHIVE_HASH);

    // Each source's own row predicate folded against its own column, in one plan, neither standing
    // in for the other: the ledger's leaf filters on `org_id` (column 1) against the manager's
    // organisation, and the archive's on `region_id` (column 2) against the keeper's region.
    String text = result.physicalPlanText();
    assertThat(text).contains("table=[[ledger, orders]], projection=[[0, 1, 3]]");
    assertThat(text).contains("table=[[archive, orders]], projection=[[0, 2]]");
    assertThat(text).contains("entitled=[" + LEDGER_HASH + "]");
    assertThat(text).contains("entitled=[" + ARCHIVE_HASH + "]");
  }

  /**
   * And the column rules are each table's own: {@code amount} is the manager's to read in the ledger
   * and nobody's in the archive, for one principal in one statement.
   */
  @Test
  void the_column_rules_of_a_join_across_two_schemas_are_each_tables_own() throws Exception {
    PlannerPipeline.Result result =
        plan(
            "SELECT l.id, l.amount, a.amount FROM ledger.orders l"
                + " JOIN archive.orders a ON a.id = l.id",
            principal(1, 7));

    assertThat(leaf(result, "ledger.orders").of(3))
        .isEqualTo(chalk.planner.entitlement.Disclosed.FULL);
    assertThat(leaf(result, "archive.orders").of(3))
        .isEqualTo(chalk.planner.entitlement.Disclosed.REDACTED);
  }

  // ------------------------------------------------------------------ the unqualified name

  /**
   * An unqualified name is the default schema's — the catalog's first — and reads that leaf alone.
   * Nothing of the other source's descriptor reaches the plan.
   */
  @Test
  void an_unqualified_name_reads_the_default_schemas_leaf_alone() throws Exception {
    PlannerPipeline.Result result = plan("SELECT id, amount FROM orders", principal(1, 7));

    DisclosureMap only = result.entitledLeaves().stream().findFirst().orElseThrow();
    assertThat(result.entitledLeaves()).hasSize(1);
    assertThat(only.qualifiedName()).isEqualTo("ledger.orders");
    assertThat(only.descriptorHash()).isEqualTo(LEDGER_HASH);
    assertThat(result.physicalPlanText()).doesNotContain(ARCHIVE_HASH);
  }

  /**
   * The same statement qualified the other way reads the archive's, which is the other half of the
   * same claim: the schema is what decides, and it decides every time.
   */
  @Test
  void the_qualified_name_reads_that_schemas_leaf_alone() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, amount FROM archive.orders", principal(1, 7));

    DisclosureMap only = result.entitledLeaves().stream().findFirst().orElseThrow();
    assertThat(result.entitledLeaves()).hasSize(1);
    assertThat(only.qualifiedName()).isEqualTo("archive.orders");
    assertThat(only.descriptorHash()).isEqualTo(ARCHIVE_HASH);
    assertThat(result.physicalPlanText()).doesNotContain(LEDGER_HASH);
  }
}
