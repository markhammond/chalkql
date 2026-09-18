package chalk.planner;

import static chalk.planner.TestCatalogs.column;
import static chalk.planner.TestCatalogs.nullable;
import static chalk.planner.TestCatalogs.type;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.ColumnEntitlement;
import chalk.ir.v1.Disclosure;
import chalk.ir.v1.DisclosureRule;
import chalk.ir.v1.ForeignKey;
import chalk.ir.v1.ParentVisibility;
import chalk.ir.v1.QueryLanguage;
import chalk.ir.v1.RowCountKind;
import chalk.ir.v1.Schema;
import chalk.ir.v1.SourceCapabilities;
import chalk.ir.v1.SourceKind;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableEntitlement;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.UniqueKey;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.InvalidCatalogException;
import java.util.function.UnaryOperator;
import org.junit.jupiter.api.Test;

/**
 * What registration refuses of a {@code through} relationship
 * (docs/design/16-entitlements.md §3.13, D225).
 *
 * <p>The shape checks and the vocabulary check are two different things and both live here: the
 * parent has to be a table of this catalog, entitled and itself restricted, keyed by a declared
 * unique key and reached by a column of the key's type, with no chain returning to a table; and the
 * child's own expressions have to stay inside §2's vocabulary — this table's row and the context —
 * with the parent's columns admissible in a rule <em>condition</em> and nowhere else.
 *
 * <p>The catalog is this test's own and not the shared fixture, because what is under test is
 * registration alone: none of these catalogs is ever planned.
 */
class ThroughRegistrationTest {

  private static void register(CatalogContext catalog) {
    new CatalogRegistry().register(catalog);
  }

  // ------------------------------------------------------------------ the catalog

  /** Organisations: a plain table with a unique key and no entitlement at all. */
  private static Table orgs() {
    return Table.newBuilder()
        .setName("orgs")
        .setRowCount(3)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("name", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  /** The parent: restricted by the tenancy, as a parent must be. */
  private static Table threads() {
    return Table.newBuilder()
        .setName("threads")
        .setRowCount(20)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate("org_id IN (@ctx.manager_orgs)")
                .setDescriptorHash("aaaabbbbccccddddaaaabbbbccccdddd")
                .build())
        .build();
  }

  /** The child: no tenancy column of its own, and its {@code content} decided by the thread's. */
  private static Table messages() {
    return Table.newBuilder()
        .setName("messages")
        .setRowCount(200)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("thread_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("content", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("first_viewed_at", nullable(TypeKind.TYPE_KIND_TIMESTAMP)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addForeignKeys(
            ForeignKey.newBuilder()
                .setName("messages_thread")
                .addColumns(1)
                .setParentTable("threads")
                .addParentColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setDescriptorHash("1111222233334444111122223333444")
                .addThrough(
                    ParentVisibility.newBuilder()
                        .setColumn(1)
                        .setParentTable("threads")
                        .setParentColumn(0))
                .addColumns(
                    ColumnEntitlement.newBuilder()
                        .setColumn(2)
                        .setMask("SUBSTRING(content, 1, 8)")
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen("threads.org_id IN (@ctx.manager_orgs)")
                                .setThen(Disclosure.DISCLOSURE_FULL))
                        .setOtherwise(Disclosure.DISCLOSURE_NONE))
                .build())
        .build();
  }

  private static CatalogContext catalog(Table threads, Table messages) {
    return CatalogContext.newBuilder()
        .setContextId("through")
        .setEpoch(1L)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("main")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .setCapabilities(
                    SourceCapabilities.newBuilder()
                        .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE))
                .addTables(orgs())
                .addTables(threads)
                .addTables(messages))
        .build();
  }

  /** The example, with the child edited. */
  private static CatalogContext child(UnaryOperator<Table.Builder> edit) {
    return catalog(threads(), edit.apply(messages().toBuilder()).build());
  }

  private static Table.Builder through(Table.Builder table, ParentVisibility parent) {
    return table.setEntitlement(
        table.getEntitlement().toBuilder().clearThrough().addThrough(parent).build());
  }

  // ------------------------------------------------------------------ the shape

  @Test
  void the_example_registers() {
    register(catalog(threads(), messages()));
  }

  @Test
  void a_parent_the_catalog_does_not_hold_is_refused() {
    assertThatThrownBy(
            () ->
                register(
                    child(
                        table ->
                            through(
                                table,
                                ParentVisibility.newBuilder()
                                    .setColumn(1)
                                    .setParentTable("chat_threads")
                                    .setParentColumn(0)
                                    .build()))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("main.chat_threads")
        .hasMessageContaining("which this catalog does not hold");
  }

  @Test
  void a_parent_that_carries_no_entitlement_is_refused() {
    assertThatThrownBy(
            () ->
                register(
                    child(
                        table ->
                            through(
                                table,
                                ParentVisibility.newBuilder()
                                    .setColumn(1)
                                    .setParentTable("orgs")
                                    .setParentColumn(0)
                                    .build()))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("carries no entitlement")
        .hasMessageContaining("Through an unrestricted parent restricts nothing");
  }

  @Test
  void a_parent_that_restricts_no_row_is_refused() {
    Table.Builder threads = threads().toBuilder();
    Table unrestricted =
        threads
            .setEntitlement(threads.getEntitlement().toBuilder().clearRowPredicate())
            .build();

    assertThatThrownBy(() -> register(catalog(unrestricted, messages())))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("restricts no row")
        .hasMessageContaining("declare the child unrestricted or restrict the parent");
  }

  @Test
  void a_parent_key_that_is_not_a_declared_unique_key_is_refused() {
    assertThatThrownBy(
            () ->
                register(
                    child(
                        table ->
                            through(
                                table,
                                ParentVisibility.newBuilder()
                                    .setColumn(1)
                                    .setParentTable("threads")
                                    .setParentColumn(1)
                                    .build()))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("is not a declared unique key of the parent");
  }

  @Test
  void a_correlation_key_of_another_type_kind_is_refused() {
    assertThatThrownBy(
            () ->
                register(
                    child(
                        table ->
                            through(
                                table,
                                ParentVisibility.newBuilder()
                                    .setColumn(2)
                                    .setParentTable("threads")
                                    .setParentColumn(0)
                                    .build()))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("must have the same type kind");
  }

  @Test
  void a_chain_of_parents_that_returns_to_a_table_is_refused() {
    Table.Builder threads = threads().toBuilder();
    Table cyclic =
        threads
            .addForeignKeys(
                ForeignKey.newBuilder()
                    .addColumns(0)
                    .setParentTable("messages")
                    .addParentColumns(0))
            .setEntitlement(
                threads.getEntitlement().toBuilder()
                    .addThrough(
                        ParentVisibility.newBuilder()
                            .setColumn(0)
                            .setParentTable("messages")
                            .setParentColumn(0)))
            .build();

    assertThatThrownBy(() -> register(catalog(cyclic, messages())))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("the chain of derived visibility returns to");
  }

  // ------------------------------------------------------------------ the vocabulary

  @Test
  void a_rule_condition_over_another_catalog_table_is_refused_naming_through_as_the_way() {
    assertThatThrownBy(
            () ->
                register(
                    child(
                        table ->
                            table.setEntitlement(
                                table.getEntitlement().toBuilder()
                                    .setColumns(
                                        0,
                                        ColumnEntitlement.newBuilder()
                                            .setColumn(2)
                                            .setMask("'********'")
                                            .addRules(
                                                DisclosureRule.newBuilder()
                                                    .setWhen(
                                                        "EXISTS (SELECT 1 FROM orgs o WHERE"
                                                            + " o.id = thread_id)")
                                                    .setThen(Disclosure.DISCLOSURE_FULL))
                                            .setOtherwise(Disclosure.DISCLOSURE_NONE))
                                    .build()))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("reads the catalog table")
        .hasMessageContaining("declared as a `through` parent");
  }

  @Test
  void a_row_predicate_over_another_catalog_table_is_refused_naming_through_as_the_way() {
    Table.Builder threads = threads().toBuilder();
    Table subquery =
        threads
            .setEntitlement(
                threads.getEntitlement().toBuilder()
                    .setRowPredicate("org_id IN (SELECT id FROM orgs)"))
            .build();

    assertThatThrownBy(() -> register(catalog(subquery, messages())))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("reads the catalog table")
        .hasMessageContaining("declared as a `through` parent");
  }

  @Test
  void a_mask_that_reads_a_parents_column_is_refused() {
    assertThatThrownBy(
            () ->
                register(
                    child(
                        table ->
                            table.setEntitlement(
                                table.getEntitlement().toBuilder()
                                    .setColumns(
                                        0,
                                        ColumnEntitlement.newBuilder()
                                            .setColumn(2)
                                            .setMask("CAST(threads.org_id AS VARCHAR)")
                                            .addRules(
                                                DisclosureRule.newBuilder()
                                                    .setWhen(
                                                        "threads.org_id IN (@ctx.manager_orgs)")
                                                    .setThen(Disclosure.DISCLOSURE_MASKED))
                                            .setOtherwise(Disclosure.DISCLOSURE_NONE))
                                    .build()))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("mask reads 'threads'")
        .hasMessageContaining("a parent this table is entitled through");
  }

  @Test
  void a_row_predicate_that_reads_a_parents_column_is_refused() {
    assertThatThrownBy(
            () ->
                register(
                    child(
                        table ->
                            table.setEntitlement(
                                table.getEntitlement().toBuilder()
                                    .setRowPredicate("threads.org_id IN (@ctx.manager_orgs)")
                                    .build()))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("the row predicate reads 'threads'")
        .hasMessageContaining("the parent's rows are reached by the `through` entry itself");
  }
}
