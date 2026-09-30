package chalk.planner;

import static chalk.planner.TestCatalogs.column;
import static chalk.planner.TestCatalogs.type;
import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.catchThrowableOfType;

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
import chalk.planner.entitlement.PolicyException;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.PlanErrors;
import chalk.planner.rpc.v1.ContextRelationKind;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.EntitlementsOptions;
import chalk.planner.rpc.v1.NamedColumns;
import chalk.planner.rpc.v1.PlanError;
import chalk.planner.rpc.v1.PlanErrorKind;
import chalk.planner.rpc.v1.PolicyRefusalReason;
import chalk.planner.rpc.v1.Redaction;
import chalk.planner.rpc.v1.ReportedDisclosure;
import chalk.planner.rpc.v1.RequestContext;
import chalk.planner.rpc.v1.StarPolicy;
import io.grpc.StatusRuntimeException;
import java.util.List;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * A refusal says what it refused, and carries the result the statement would have had (D329).
 *
 * <p>{@code members} admits the rows of the principal's organisations, and discloses {@code
 * national_id} only to be counted where the principal counts, withholding it elsewhere. A star
 * returns it as a value, so the star is refused, and the refusal names the table, the column, the
 * use and the aggregate the column allows, beside the three output columns as the policy shapes
 * them. A principal who counts nowhere is withheld the column, which is nullable for them where the
 * catalog says NOT NULL. A star under a refusing star policy is refused before the statement is
 * validated, so it has no columns to carry.
 */
class PolicyRefusalTest {
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
            .addColumns(column("national_id", type(TypeKind.TYPE_KIND_STRING)))
            .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
            .setEntitlement(
                TableEntitlement.newBuilder()
                    .setDescriptorHash("7efa5e7efa5e7efa5e7efa5e7efa5e7e")
                    .setRowPredicate("org_id IN (@ctx.orgs)")
                    .addColumns(
                        ColumnEntitlement.newBuilder()
                            .setColumn(2)
                            .addAggregateOnlyFunctions("COUNT")
                            .setMinGroupSize(3)
                            .addRules(
                                DisclosureRule.newBuilder()
                                    .setWhen("org_id IN (@ctx.counting)")
                                    .setThen(Disclosure.DISCLOSURE_AGGREGATE_ONLY))
                            .setOtherwise(Disclosure.DISCLOSURE_NONE)))
            .build();
    // The same shape with a group-size floor of one, which is no guard at all (D211).
    Table badges =
        members.toBuilder()
            .setName("badges")
            .setColumns(2, column("badge", type(TypeKind.TYPE_KIND_STRING)))
            .setEntitlement(
                members.getEntitlement().toBuilder()
                    .setDescriptorHash("badbadbadbadbadbadbadbadbadbadba")
                    .setColumns(
                        0,
                        members.getEntitlement().getColumns(0).toBuilder().setMinGroupSize(1)))
            .build();
    catalog =
        new CatalogRegistry()
            .register(
                CatalogContext.newBuilder()
                    .setContextId("policy-refusals")
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
                            .addTables(badges))
                    .build());
  }

  /** Organisations 1 and 2, counting in both. */
  private static RequestContext counter() {
    return RequestContext.newBuilder()
        .addRelations(ids("orgs", false, 1, 2))
        .addRelations(ids("counting", false, 1, 2))
        .build();
  }

  /** Organisations 1 and 2, counting in neither. */
  private static RequestContext reader() {
    return RequestContext.newBuilder()
        .addRelations(ids("orgs", false, 1, 2))
        .addRelations(ids("counting", false))
        .build();
  }

  /** Both lists left to execution. */
  private static RequestContext shape() {
    return RequestContext.newBuilder()
        .setShapeOnly(true)
        .addRelations(ids("orgs", true))
        .addRelations(ids("counting", true))
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

  private static PolicyException refusal(String sql, RequestContext context, PolicyOptions options) {
    PolicyException refused =
        catchThrowableOfType(
            PolicyException.class,
            () -> {
              try (PlannerPipeline pipeline =
                  PlannerPipeline.create(
                      catalog,
                      PushdownPolicy.full(),
                      chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
                      List.of(),
                      chalk.planner.plan.JoinPolicy.DEFAULT,
                      BoundContext.of(context),
                      options)) {
                pipeline.plan(sql, true);
              }
            });
    assertThat(refused).as(sql).isNotNull();
    return refused;
  }

  private static PlannerPipeline.Result planned(String sql, RequestContext context)
      throws Exception {
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

  private static List<String> names(RowType row) {
    return row.getFieldsList().stream().map(Field::getName).toList();
  }

  @Test
  void a_value_use_of_a_population_only_column_says_what_was_refused_and_carries_the_result() {
    PolicyException refused =
        refusal("SELECT * FROM members", counter(), PolicyOptions.DEFAULTS);

    assertThat(refused.reason()).isEqualTo(PolicyRefusalReason.POLICY_REFUSAL_REASON_POPULATION_ONLY);
    assertThat(refused.table()).isEqualTo("main.members");
    assertThat(refused.column()).isEqualTo("national_id");
    assertThat(refused.use()).isEqualTo("a projection to the result");
    assertThat(refused.permitted()).containsExactly("COUNT");

    RowType output = refused.output();
    assertThat(output).isNotNull();
    assertThat(names(output)).containsExactly("id", "org_id", "national_id");
    // Counted on every row this principal sees, so never withheld: NOT NULL, as in the catalog.
    assertThat(output.getFields(2).getType().getKind()).isEqualTo(TypeKind.TYPE_KIND_STRING);
    assertThat(output.getFields(2).getType().getNullable()).isFalse();
    assertThat(refused.disclosures())
        .containsExactly(
            ReportedDisclosure.REPORTED_DISCLOSURE_FULL,
            ReportedDisclosure.REPORTED_DISCLOSURE_FULL,
            ReportedDisclosure.REPORTED_DISCLOSURE_AGGREGATE);
  }

  @Test
  void bound_at_execution_the_refusal_carries_the_result_too() {
    PolicyException refused = refusal("SELECT * FROM members", shape(), PolicyOptions.DEFAULTS);

    assertThat(refused.reason()).isEqualTo(PolicyRefusalReason.POLICY_REFUSAL_REASON_POPULATION_ONLY);
    assertThat(refused.column()).isEqualTo("national_id");
    assertThat(names(refused.output())).containsExactly("id", "org_id", "national_id");
    assertThat(refused.disclosures()).hasSize(3);
  }

  @Test
  void a_named_column_refused_after_optimisation_carries_the_result() {
    // A principal who counts nowhere is withheld national_id outright; named, under
    // NamedColumns.Refuse, it is refused by the redaction policy on the optimised tree.
    PolicyOptions refuseNamed =
        PolicyOptions.of(
            EntitlementsOptions.newBuilder()
                .setRedaction(
                    Redaction.newBuilder().setNamedColumns(NamedColumns.NAMED_COLUMNS_REFUSE))
                .build());
    PolicyException refused = refusal("SELECT id, national_id FROM members", reader(), refuseNamed);

    assertThat(refused.reason()).isEqualTo(PolicyRefusalReason.POLICY_REFUSAL_REASON_REDACTED);
    assertThat(refused.column()).isEqualTo("national_id");
    RowType output = refused.output();
    assertThat(names(output)).containsExactly("id", "national_id");
    // NOT NULL in the catalog, and nullable here: withheld, its placeholder is NULL.
    assertThat(output.getFields(1).getType().getNullable()).isTrue();
    assertThat(output.getFields(0).getType().getNullable()).isFalse();
    assertThat(refused.disclosures())
        .containsExactly(
            ReportedDisclosure.REPORTED_DISCLOSURE_FULL,
            ReportedDisclosure.REPORTED_DISCLOSURE_REDACTED);
  }

  @Test
  void a_refusal_partway_describes_a_guarded_aggregate_as_nullable() throws Exception {
    // Refused at the predicate, which the trace meets before the aggregate above it.
    PolicyException refused =
        refusal(
            "SELECT org_id, COUNT(national_id) AS n FROM members WHERE national_id <> 'x'"
                + " GROUP BY org_id",
            counter(),
            PolicyOptions.DEFAULTS);
    assertThat(refused.use()).isEqualTo("a predicate");
    RowType output = refused.output();
    assertThat(names(output)).containsExactly("org_id", "n");
    // COUNT is NOT NULL, and a guard with a floor of three makes it NULL below the floor.
    assertThat(output.getFields(1).getType().getNullable()).isTrue();
    // As in the same statement without the predicate, which runs.
    assertThat(
            planned("SELECT org_id, COUNT(national_id) AS n FROM members GROUP BY org_id", counter())
                .physical()
                .getRowType()
                .getFieldList()
                .get(1)
                .getType()
                .isNullable())
        .isTrue();

    // A floor of one is no guard, so COUNT stays NOT NULL.
    PolicyException unguarded =
        refusal(
            "SELECT org_id, COUNT(badge) AS n FROM badges WHERE badge <> 'x' GROUP BY org_id",
            counter(),
            PolicyOptions.DEFAULTS);
    assertThat(unguarded.output().getFields(1).getType().getNullable()).isFalse();
  }

  @Test
  void a_star_refused_before_validation_carries_no_columns() {
    PolicyOptions refuseStars =
        PolicyOptions.of(
            EntitlementsOptions.newBuilder()
                .setStarPolicy(StarPolicy.STAR_POLICY_REFUSE_WHEN_ENTITLED)
                .build());
    PolicyException refused = refusal("SELECT * FROM members", counter(), refuseStars);

    assertThat(refused.reason()).isEqualTo(PolicyRefusalReason.POLICY_REFUSAL_REASON_STAR);
    assertThat(refused.output()).isNull();
    assertThat(refused.disclosures()).isEmpty();
  }

  @Test
  void the_refusal_travels_in_the_trailer() throws Exception {
    StatusRuntimeException status =
        PlanErrors.toStatus(refusal("SELECT * FROM members", counter(), PolicyOptions.DEFAULTS));
    PlanError error = PlanError.parseFrom(status.getTrailers().get(PlanErrors.TRAILER));

    assertThat(error.getKind()).isEqualTo(PlanErrorKind.PLAN_ERROR_KIND_POLICY);
    assertThat(error.getMessage()).contains("national_id is population-only");
    assertThat(error.getPolicyRefusal().getReason())
        .isEqualTo(PolicyRefusalReason.POLICY_REFUSAL_REASON_POPULATION_ONLY);
    assertThat(error.getPolicyRefusal().getTable()).isEqualTo("main.members");
    assertThat(error.getPolicyRefusal().getColumn()).isEqualTo("national_id");
    assertThat(error.getPolicyRefusal().getPermittedList()).containsExactly("COUNT");
    assertThat(names(error.getPolicyRefusal().getOutput()))
        .containsExactly("id", "org_id", "national_id");
    assertThat(error.getPolicyRefusal().getDisclosuresList())
        .containsExactly("FULL", "FULL", "AGGREGATE");
  }
}
