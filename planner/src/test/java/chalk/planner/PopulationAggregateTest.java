package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.ClientBody;
import chalk.ir.v1.ColumnEntitlement;
import chalk.ir.v1.Expr;
import chalk.ir.v1.Field;
import chalk.ir.v1.FunctionDescriptor;
import chalk.ir.v1.FunctionKind;
import chalk.ir.v1.Literal;
import chalk.ir.v1.Parameter;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.ir.v1.Schema;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableEntitlement;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.Volatility;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.InvalidCatalogException;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.Disclosed;
import chalk.planner.entitlement.PolicyException;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.ir.IrVersionGate;
import chalk.planner.ir.RelToIr;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.ReportedDisclosure;
import chalk.planner.rpc.v1.RequestContext;
import chalk.planner.types.TypeMapper;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.rel.RelNode;
import org.junit.jupiter.api.Test;

/**
 * A user aggregate its host declares {@code Population()} (D295): the flag on the wire, the
 * allow-list's catalog-aware check at registration, the taint check asking the declaration of the
 * operator a plan calls, and the group-size guard over a composite measure — a {@code CASE} of the
 * composite's own type, NULLing a small group's composite whole.
 *
 * <p>The catalog is the tenancy fixture's, with two aggregates over {@code BIGINT} answering a
 * nullable {@code COMPOSITE(Total BIGINT, Tally BIGINT)}: {@code population_summary}, declared
 * {@code Population()}, and {@code amount_summary}, which is not.
 */
final class PopulationAggregateTest {

  private static final Type SUMMARY =
      TestCatalogs.composite(
          true,
          field("Total", TestCatalogs.type(TypeKind.TYPE_KIND_I64)),
          field("Tally", TestCatalogs.type(TypeKind.TYPE_KIND_I64)));

  private static final String BOTH_FIELDS =
      "SELECT org_id, population_summary(amount).total AS total,"
          + " population_summary(amount).tally AS tally FROM orders GROUP BY org_id";

  // ------------------------------------------------------------------ registration

  @Test
  void an_aggregate_declared_population_may_be_named_in_an_allow_list() {
    RegisteredCatalog registered =
        new CatalogRegistry().register(catalog("POPULATION_SUMMARY"));
    assertThat(registered.hasEntitlements()).isTrue();
  }

  @Test
  void an_allow_list_naming_an_aggregate_not_declared_population_is_refused_saying_how() {
    assertThatThrownBy(() -> new CatalogRegistry().register(catalog("AMOUNT_SUMMARY")))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("(amount).aggregate_only_functions[4]")
        .hasMessageContaining("'AMOUNT_SUMMARY' is not a population aggregate (D190)")
        .hasMessageContaining("any user-defined aggregate not declared Population()")
        .hasMessageContaining("once its host declares it Population()");
  }

  @Test
  void an_allow_list_naming_no_declared_function_or_a_built_in_outside_the_set_is_refused() {
    assertThatThrownBy(() -> new CatalogRegistry().register(catalog("NO_SUCH_AGGREGATE")))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("'NO_SUCH_AGGREGATE' is not a population aggregate");
    assertThatThrownBy(() -> new CatalogRegistry().register(catalog("MIN")))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("'MIN' is not a population aggregate");
  }

  @Test
  void population_describes_an_aggregate_and_a_scalar_declaring_it_is_refused() {
    FunctionDescriptor scalar =
        summary("population_summary", true).toBuilder()
            .setKind(FunctionKind.FUNCTION_KIND_SCALAR)
            .build();
    CatalogContext base = catalog();
    CatalogContext catalog =
        base.toBuilder()
            .setSchemas(
                0,
                base.getSchemas(0).toBuilder()
                    .clearFunctions()
                    .addFunctions(scalar))
            .build();
    assertThatThrownBy(() -> RegisteredCatalog.of(catalog))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("`population` describe an aggregate");
  }

  // ------------------------------------------------------------------ the guard

  @Test
  void a_population_aggregate_over_a_population_only_column_is_guarded_as_one_composite()
      throws Exception {
    PlannerPipeline.Result result = plan(BOTH_FIELDS, TenancyCatalogs.auditor(1));

    assertThat(result.entitledLeaves()).hasSize(1);
    assertThat(result.entitledLeaves().get(0).of(3)).isEqualTo(Disclosed.AGGREGATE);
    assertThat(result.columnDisclosures())
        .containsExactly(
            ReportedDisclosure.REPORTED_DISCLOSURE_FULL,
            ReportedDisclosure.REPORTED_DISCLOSURE_AGGREGATE,
            ReportedDisclosure.REPORTED_DISCLOSURE_AGGREGATE);
    // COUNT(amount) beside the measure, and the guard at the column's own floor of 3.
    assertThat(result.physicalPlanText()).contains("COUNT(").contains(", 3)");

    // The guard is one CASE of the composite's own type, its ELSE a typed NULL of it, and both
    // fields are read from it: the small group's composite is NULLed whole.
    List<Expr> guards = compositeCases(ir(result));
    assertThat(guards).isNotEmpty();
    for (Expr guard : guards) {
      assertThat(guard.getType()).isEqualTo(SUMMARY);
      Expr otherwise = guard.getIfThen().getElseBranch();
      assertThat(otherwise.getLiteral().getValueCase()).isEqualTo(Literal.ValueCase.IS_NULL);
      assertThat(otherwise.getType()).isEqualTo(SUMMARY);
    }
    assertThat(
            exprs(ir(result)).stream()
                .filter(e -> e.getKindCase() == Expr.KindCase.FIELD_ACCESS)
                .filter(e -> e.getFieldAccess().getInput().getKindCase() == Expr.KindCase.IF_THEN)
                .map(e -> e.getFieldAccess().getIndex())
                .distinct()
                .sorted()
                .toList())
        .containsExactly(0, 1);
  }

  @Test
  void the_whole_composite_of_a_guarded_measure_is_a_nullable_composite_column() throws Exception {
    Plan plan =
        ir(plan("SELECT population_summary(amount) AS s FROM orders", TenancyCatalogs.auditor(1)));
    assertThat(plan.getOutputType().getFields(0).getType()).isEqualTo(SUMMARY);
    assertThat(compositeCases(plan)).hasSize(1);
  }

  @Test
  void a_principal_the_column_is_full_for_is_not_guarded() throws Exception {
    PlannerPipeline.Result result = plan(BOTH_FIELDS, TenancyCatalogs.manager(1));
    assertThat(result.entitledLeaves().get(0).of(3)).isEqualTo(Disclosed.FULL);
    assertThat(compositeCases(ir(result))).isEmpty();
  }

  @Test
  void an_aggregate_not_declared_population_is_still_refused_over_the_column() {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT org_id, amount_summary(amount).total FROM orders GROUP BY org_id",
                    TenancyCatalogs.auditor(1)))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("an aggregate outside this column's allow-list")
        .hasMessageContaining("AMOUNT_SUMMARY");
  }

  // ------------------------------------------------------------------ helpers

  /** The tenancy catalog with both aggregates declared and {@code allowListed} added to amount's list. */
  private static CatalogContext catalog(String... allowListed) {
    CatalogContext base = TenancyCatalogs.catalog();
    Schema.Builder schema = base.getSchemas(0).toBuilder();
    for (int t = 0; t < schema.getTablesCount(); t++) {
      if (!schema.getTables(t).getName().equals("orders")) {
        continue;
      }
      Table.Builder orders = schema.getTables(t).toBuilder();
      TableEntitlement.Builder entitlement = orders.getEntitlement().toBuilder();
      for (int c = 0; c < entitlement.getColumnsCount(); c++) {
        ColumnEntitlement column = entitlement.getColumns(c);
        if (column.getColumn() == 3) {
          entitlement.setColumns(
              c, column.toBuilder().addAllAggregateOnlyFunctions(List.of(allowListed)));
        }
      }
      schema.setTables(t, orders.setEntitlement(entitlement));
    }
    schema.addFunctions(summary("population_summary", true));
    schema.addFunctions(summary("amount_summary", false));
    return base.toBuilder().setSchemas(0, schema).build();
  }

  private static FunctionDescriptor summary(String name, boolean population) {
    return FunctionDescriptor.newBuilder()
        .setName(name)
        .setKind(FunctionKind.FUNCTION_KIND_AGGREGATE)
        .setVolatility(Volatility.VOLATILITY_IMMUTABLE)
        .setReturnType(SUMMARY)
        .addParameters(
            Parameter.newBuilder()
                .setName("x")
                .setType(TestCatalogs.nullable(TypeKind.TYPE_KIND_I64)))
        .setPopulation(population)
        .setClient(ClientBody.getDefaultInstance())
        .build();
  }

  private static Field field(String name, Type type) {
    return Field.newBuilder().setName(name).setType(type).build();
  }

  private static PlannerPipeline.Result plan(String sql, RequestContext context) throws Exception {
    RegisteredCatalog registered = new CatalogRegistry().register(catalog("POPULATION_SUMMARY"));
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            registered,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            BoundContext.of(context),
            PolicyOptions.DEFAULTS)) {
      return pipeline.plan(sql, true);
    }
  }

  private static Plan ir(PlannerPipeline.Result result) {
    RelNode physical = result.physical();
    RelToIr converter =
        new RelToIr(
            new TypeMapper(physical.getCluster().getTypeFactory()),
            physical.getCluster().getRexBuilder(),
            physical.getCluster().getMetadataQuery(),
            IrVersionGate.current());
    return converter.toPlan(
        physical, result.parameterRowType(), TenancyCatalogs.CONTEXT_ID, TenancyCatalogs.EPOCH);
  }

  private static List<Expr> compositeCases(Plan plan) {
    return exprs(plan).stream()
        .filter(e -> e.getKindCase() == Expr.KindCase.IF_THEN)
        .filter(e -> e.getType().getKind() == TypeKind.TYPE_KIND_COMPOSITE)
        .toList();
  }

  /** Every expression in the plan, depth first. */
  private static List<Expr> exprs(Plan plan) {
    List<Expr> all = new ArrayList<>();
    walk(plan.getRoot(), all);
    return all;
  }

  private static void walk(Object value, List<Expr> into) {
    if (value instanceof Expr expr) {
      into.add(expr);
      for (Object child : expr.getAllFields().values()) {
        walk(child, into);
      }
    } else if (value instanceof Rel || value instanceof com.google.protobuf.Message) {
      for (Object child : ((com.google.protobuf.Message) value).getAllFields().values()) {
        walk(child, into);
      }
    } else if (value instanceof List<?> list) {
      for (Object item : list) {
        walk(item, into);
      }
    }
  }
}
