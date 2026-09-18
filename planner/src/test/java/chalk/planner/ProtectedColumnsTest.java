package chalk.planner;

import static chalk.planner.TestCatalogs.column;
import static chalk.planner.TestCatalogs.type;
import static org.assertj.core.api.Assertions.assertThatCode;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.ColumnEntitlement;
import chalk.ir.v1.Disclosure;
import chalk.ir.v1.DisclosureRule;
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
import org.junit.jupiter.api.Test;

/**
 * Which columns count as <em>protected</em> under {@code default_disclosure = NONE}, and therefore
 * which a mask may read (docs/design/16-entitlements.md §1, D220; ADR 0025 V127 as refined).
 *
 * <p>Under a NONE default a column the descriptor does not list is withheld, so "protected" has to
 * mean every column rather than only the listed ones. But a listed column whose disclosure is
 * <em>unconditionally</em> FULL is withheld from nobody, so a mask may read it exactly as it may
 * read a column of a table with no default at all. A column FULL for some roles only stays
 * protected: to a principal without those roles it is a placeholder, and a mask reading it would
 * disclose what the report says it did not.
 */
class ProtectedColumnsTest {

  private static Table notes(ColumnEntitlement... columns) {
    TableEntitlement.Builder entitlement =
        TableEntitlement.newBuilder()
            .setRowPredicate("org_id IN (@ctx.manager_orgs)")
            .setDefaultDisclosure(Disclosure.DISCLOSURE_NONE)
            .setDescriptorHash("00000000000000000000000000000000");
    for (ColumnEntitlement column : columns) {
      entitlement.addColumns(column);
    }
    return Table.newBuilder()
        .setName("notes")
        .setRowCount(9)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("kind", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("body", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(entitlement)
        .build();
  }

  private static CatalogContext catalog(Table notes) {
    return CatalogContext.newBuilder()
        .setContextId("protected")
        .setEpoch(1L)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("main")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .setCapabilities(
                    SourceCapabilities.newBuilder()
                        .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE))
                .addTables(notes))
        .build();
  }

  /** {@code kind} is FULL for every row and every principal, so a mask of {@code body} may read it. */
  @Test
  void a_listed_column_that_is_unconditionally_full_is_not_protected() {
    assertThatCode(
            () ->
                new CatalogRegistry()
                    .register(
                        catalog(
                            notes(
                                ColumnEntitlement.newBuilder()
                                    .setColumn(2)
                                    .addRules(
                                        DisclosureRule.newBuilder()
                                            .setWhen("TRUE")
                                            .setThen(Disclosure.DISCLOSURE_FULL))
                                    .setOtherwise(Disclosure.DISCLOSURE_NONE)
                                    .build(),
                                ColumnEntitlement.newBuilder()
                                    .setColumn(3)
                                    .setMask("kind || ': withheld'")
                                    .addRules(
                                        DisclosureRule.newBuilder()
                                            .setWhen("org_id IN (@ctx.manager_orgs)")
                                            .setThen(Disclosure.DISCLOSURE_MASKED))
                                    .setOtherwise(Disclosure.DISCLOSURE_NONE)
                                    .build()))))
        .doesNotThrowAnyException();
  }

  /** The same column FULL for some roles only stays protected, and the mask is refused. */
  @Test
  void a_listed_column_that_is_full_for_some_roles_stays_protected() {
    assertThatThrownBy(
            () ->
                new CatalogRegistry()
                    .register(
                        catalog(
                            notes(
                                ColumnEntitlement.newBuilder()
                                    .setColumn(2)
                                    .addRules(
                                        DisclosureRule.newBuilder()
                                            .setWhen("org_id IN (@ctx.manager_orgs)")
                                            .setThen(Disclosure.DISCLOSURE_FULL))
                                    .setOtherwise(Disclosure.DISCLOSURE_NONE)
                                    .build(),
                                ColumnEntitlement.newBuilder()
                                    .setColumn(3)
                                    .setMask("kind || ': withheld'")
                                    .addRules(
                                        DisclosureRule.newBuilder()
                                            .setWhen("org_id IN (@ctx.manager_orgs)")
                                            .setThen(Disclosure.DISCLOSURE_MASKED))
                                    .setOtherwise(Disclosure.DISCLOSURE_NONE)
                                    .build()))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("mask for 'body' reads 'kind'")
        .hasMessageContaining("which is itself a protected column");
  }

  /** And a column the descriptor does not list at all is protected, as V127 says. */
  @Test
  void an_unlisted_column_under_a_none_default_stays_protected() {
    assertThatThrownBy(
            () ->
                new CatalogRegistry()
                    .register(
                        catalog(
                            notes(
                                ColumnEntitlement.newBuilder()
                                    .setColumn(3)
                                    .setMask("kind || ': withheld'")
                                    .addRules(
                                        DisclosureRule.newBuilder()
                                            .setWhen("org_id IN (@ctx.manager_orgs)")
                                            .setThen(Disclosure.DISCLOSURE_MASKED))
                                    .setOtherwise(Disclosure.DISCLOSURE_NONE)
                                    .build()))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("mask for 'body' reads 'kind'");
  }
}
