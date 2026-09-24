package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.Schema;
import chalk.ir.v1.TypeKind;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import org.junit.jupiter.api.Test;

/**
 * Chalk's own planner driver (D292, ADR 0077): Calcite's {@code PlannerImpl} less the structured-type
 * flattener. That the driver changes nothing else is what the recorded corpus proves, plan for plan;
 * what is here is the one shape the change could have broken that the corpus does not hold.
 */
class ChalkPlannerTest {

  /**
   * A SQL-bodied table function whose body reads a partitioned table. Its expansion is converted by
   * the driver's {@code expandView}, from inside the statement's own conversion — which in {@code
   * PlannerImpl} ran the flattener too, and was the one place a partitioned scan still met it once the
   * statement's own conversion stopped doing so. The driver flattens neither, so the scan needs no
   * self-flattening hook, and the expansion plans and prunes as the table would on its own.
   */
  @Test
  public void a_sql_table_function_over_a_partitioned_table_expands_and_prunes() throws Exception {
    CatalogContext partitioned = TestCatalogs.withPartitions(TestCatalogs.duckDbProfile());
    CatalogContext.Builder catalog = partitioned.toBuilder();
    for (int i = 0; i < catalog.getSchemasCount(); i++) {
      Schema schema = catalog.getSchemas(i);
      if (schema.getName().equals("federated")) {
        catalog.setSchemas(i, schema.toBuilder().addFunctions(barsOf()));
      }
    }

    RegisteredCatalog registered = new CatalogRegistry().register(catalog.build());
    try (PlannerPipeline pipeline = PlannerPipeline.create(registered, PushdownPolicy.full())) {
      String plan =
          pipeline
              .plan("SELECT symbol, ts FROM TABLE(federated.bars_of('BTCUSDT'))", true)
              .physicalPlanText();

      assertThat(plan).contains("ChalkPartitionedScan");
      assertThat(plan).contains("bars_btcusdt");
      assertThat(plan).doesNotContain("bars_ethusdt");
    }
  }

  /** {@code bars_of(sym)}: the rows of one symbol, read from the partitioned view. */
  private static chalk.ir.v1.FunctionDescriptor barsOf() {
    return chalk.ir.v1.FunctionDescriptor.newBuilder()
        .setName("bars_of")
        .setKind(chalk.ir.v1.FunctionKind.FUNCTION_KIND_TABLE)
        .setVolatility(chalk.ir.v1.Volatility.VOLATILITY_IMMUTABLE)
        .setReturnsTable(
            chalk.ir.v1.RowType.newBuilder()
                .addFields(field("symbol", TestCatalogs.type(TypeKind.TYPE_KIND_STRING)))
                .addFields(field("ts", TestCatalogs.precise(TypeKind.TYPE_KIND_TIMESTAMP, 9)))
                .build())
        .addParameters(
            chalk.ir.v1.Parameter.newBuilder()
                .setName("sym")
                .setType(TestCatalogs.nullable(TypeKind.TYPE_KIND_STRING)))
        .setSql(
            chalk.ir.v1.SqlBody.newBuilder()
                .setText("SELECT symbol, ts FROM bars_by_symbol WHERE symbol = sym"))
        .build();
  }

  private static chalk.ir.v1.Field field(String name, chalk.ir.v1.Type type) {
    return chalk.ir.v1.Field.newBuilder().setName(name).setType(type).build();
  }
}
