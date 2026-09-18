package chalk.planner;

import static chalk.planner.TestCatalogs.column;
import static chalk.planner.TestCatalogs.type;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.ColumnEntitlement;
import chalk.ir.v1.Disclosure;
import chalk.ir.v1.DisclosureRule;
import chalk.ir.v1.Expr;
import chalk.ir.v1.Field;
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
import chalk.planner.rpc.v1.ContextRelationKind;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.ContextScalar;
import chalk.planner.rpc.v1.RequestContext;

/**
 * One table whose policy reads a context <em>scalar</em>, for the half of execute-time binding the
 * tenancy fixture does not exercise (D209).
 *
 * <p>{@code notes} is visible to a manager of its organisation and, whatever their tenancy, to the
 * principal who created it — which is the resource-owner fail-safe of the shipped package written
 * directly in layer A, and the smallest descriptor that binds both a list and a scalar.
 */
public final class ExecuteTimeBindingCatalogs {
  private ExecuteTimeBindingCatalogs() {}

  public static CatalogContext catalog() {
    return CatalogContext.newBuilder()
        .setContextId("notes")
        .setEpoch(1L)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("main")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .setCapabilities(
                    SourceCapabilities.newBuilder()
                        .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE)
                        .build())
                .addTables(notes()))
        .build();
  }

  private static Table notes() {
    return Table.newBuilder()
        .setName("notes")
        .setRowCount(6)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("created_by", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("body", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate("org_id IN (@ctx.manager_orgs) OR created_by = @ctx.user")
                .setDescriptorHash("00112233445566770011223344556677")
                .addColumns(
                    ColumnEntitlement.newBuilder()
                        .setColumn(3)
                        .setMask("'********'")
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen("created_by = @ctx.user")
                                .setThen(Disclosure.DISCLOSURE_FULL))
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen("org_id IN (@ctx.manager_orgs)")
                                .setThen(Disclosure.DISCLOSURE_MASKED))
                        .setOtherwise(Disclosure.DISCLOSURE_NONE))
                .build())
        .build();
  }

  /**
   * A <b>partial</b> binding: the list's values folded, the scalar left open (D232). One leaf then
   * carries a literal membership beside a parameter.
   *
   * @param userType the type the host declares for the open scalar — its own, or a wrong one
   */
  public static RequestContext partial(TypeKind userType, int... orgs) {
    ContextRelationValue.Builder list =
        ContextRelationValue.newBuilder()
            .setName("manager_orgs")
            .setKind(ContextRelationKind.CONTEXT_RELATION_KIND_LIST)
            .setRowType(
                RowType.newBuilder()
                    .addFields(
                        Field.newBuilder()
                            .setName("id")
                            .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))));
    for (int org : orgs) {
      list.addRows(
          chalk.ir.v1.VirtualRow.newBuilder()
              .addValues(
                  Expr.newBuilder()
                      .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))
                      .setLiteral(chalk.ir.v1.Literal.newBuilder().setI32Value(org))));
    }
    return RequestContext.newBuilder()
        .addScalars(
            ContextScalar.newBuilder()
                .setName("user")
                .setShape(true)
                .setValue(Expr.newBuilder().setType(Type.newBuilder().setKind(userType))))
        .addRelations(list)
        .build();
  }

  /** The shape: one list and one scalar, named and typed, with no value at all. */
  public static RequestContext shape() {
    return RequestContext.newBuilder()
        .setShapeOnly(true)
        .addScalars(
            ContextScalar.newBuilder()
                .setName("user")
                .setValue(
                    Expr.newBuilder().setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))))
        .addRelations(
            ContextRelationValue.newBuilder()
                .setName("manager_orgs")
                .setKind(ContextRelationKind.CONTEXT_RELATION_KIND_LIST)
                .setRowType(
                    RowType.newBuilder()
                        .addFields(
                            Field.newBuilder()
                                .setName("id")
                                .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32)))))
        .build();
  }
}
