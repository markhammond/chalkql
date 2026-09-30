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
 * A context value that {@code AND}, {@code OR} or {@code NOT} combines is a truth value when the
 * catalog is registered (docs/design/16-entitlements.md §1, F164).
 *
 * <p>A policy that declares its combinations writes no membership on a table's own row for a role
 * whose combinations are all answered along a path: a tasking that holds its mission and inherits
 * its asset's classification and releasability, under one combination of all three, is reached
 * through the asset alone. Its row predicate is then the role flags and nothing else, and
 * registration typed each flag as {@code ANY}, reduced {@code NULL OR NULL} to a NULL of that type,
 * and refused the descriptor as not boolean — so the engine could not be created at all.
 */
class ContextConnectiveRegistrationTest {

  private static Table tasking(String rowPredicate, ColumnEntitlement... columns) {
    TableEntitlement.Builder entitlement =
        TableEntitlement.newBuilder()
            .setRowPredicate(rowPredicate)
            .setDescriptorHash("00000000000000000000000000000000");
    for (ColumnEntitlement column : columns) {
      entitlement.addColumns(column);
    }
    return Table.newBuilder()
        .setName("tasking")
        .setRowCount(3)
        .addColumns(column("tasking_id", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("asset_id", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("mission_id", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(entitlement)
        .build();
  }

  private static CatalogContext catalog(Table table) {
    return CatalogContext.newBuilder()
        .setContextId("connectives")
        .setEpoch(1L)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("coalition")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .setCapabilities(
                    SourceCapabilities.newBuilder()
                        .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE))
                .addTables(table))
        .build();
  }

  /** The row predicate a declaring policy writes for a table its combinations reach by a path. */
  @Test
  void a_row_predicate_of_role_flags_alone_registers() {
    assertThatCode(
            () ->
                new CatalogRegistry()
                    .register(catalog(tasking("(@ctx.global_tasking_reader) OR @ctx.global"))))
        .doesNotThrowAnyException();
  }

  /** Every connective, nested, and a rule condition as well as a row predicate. */
  @Test
  void flags_combined_by_every_connective_register_wherever_a_truth_value_is_wanted() {
    assertThatCode(
            () ->
                new CatalogRegistry()
                    .register(
                        catalog(
                            tasking(
                                "NOT @ctx.suspended AND (@ctx.global_tasking_reader OR @ctx.global)",
                                ColumnEntitlement.newBuilder()
                                    .setColumn(2)
                                    .addRules(
                                        DisclosureRule.newBuilder()
                                            .setWhen("@ctx.auditor AND NOT @ctx.suspended")
                                            .setThen(Disclosure.DISCLOSURE_FULL))
                                    .setOtherwise(Disclosure.DISCLOSURE_NONE)
                                    .build()))))
        .doesNotThrowAnyException();
  }

  /** A row predicate that is not a truth value is refused as before. */
  @Test
  void a_row_predicate_that_is_not_boolean_is_still_refused() {
    assertThatThrownBy(
            () -> new CatalogRegistry().register(catalog(tasking("mission_id"))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("the row predicate is not boolean");
  }

  /** And a flag under a connective is still a context value: it cannot stand for a column. */
  @Test
  void a_flag_combined_with_a_value_that_is_not_boolean_is_still_refused() {
    assertThatThrownBy(
            () ->
                new CatalogRegistry()
                    .register(catalog(tasking("@ctx.global OR mission_id"))))
        .isInstanceOf(InvalidCatalogException.class);
  }
}
