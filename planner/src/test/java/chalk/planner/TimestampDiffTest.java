package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.Expr;
import chalk.ir.v1.FunctionId;
import chalk.ir.v1.TypeKind;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.ir.IrVersionGate;
import chalk.planner.ir.RelToIr;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;

/**
 * D337, F175: a count of whole units between two timestamps reaches the plan as the IR's own
 * {@code TIMESTAMP_DIFF(unit, from, to)}, with its unit. Calcite expanded {@code TIMESTAMPDIFF} into
 * an interval cast to an integer, and the IR's intervals keep no unit, so the count could not be
 * run; it counted microseconds as whole seconds multiplied up, in an INTEGER.
 */
class TimestampDiffTest {

  @ParameterizedTest(name = "{0}")
  @CsvSource({
    "MICROSECOND", "MILLISECOND", "SECOND", "MINUTE", "HOUR", "DAY", "WEEK", "MONTH", "QUARTER",
    "YEAR"
  })
  void timestampdiff_is_the_irs_own_call_in_a_bigint(String unit) throws Exception {
    Expr expr = projected("SELECT TIMESTAMPDIFF(" + unit + ", ts, TIMESTAMP '2026-03-05 00:00:00') AS a FROM bars");

    assertThat(expr.getType().getKind()).isEqualTo(TypeKind.TYPE_KIND_I64);
    assertThat(expr.getCall().getFunction()).isEqualTo(FunctionId.FUNCTION_ID_TIMESTAMP_DIFF);
    assertThat(expr.getCall().getArgs(0).getEnumArg().getValue()).isEqualTo(unit);
    assertThat(expr.getCall().getArgs(1).hasFieldRef()).as("from is the column").isTrue();
  }

  /** The standard form cast to an integer counts the qualifier's last unit, from b to a. */
  @Test
  void a_cast_interval_difference_is_the_same_call() throws Exception {
    Expr expr =
        projected("SELECT CAST((TIMESTAMP '2026-03-05 00:00:00' - ts) MINUTE AS BIGINT) AS a FROM bars");

    assertThat(expr.getCall().getFunction()).isEqualTo(FunctionId.FUNCTION_ID_TIMESTAMP_DIFF);
    assertThat(expr.getCall().getArgs(0).getEnumArg().getValue()).isEqualTo("MINUTE");
    assertThat(expr.getCall().getArgs(1).hasFieldRef()).as("from is the column").isTrue();
  }

  private static Expr projected(String sql) throws Exception {
    var registered = new CatalogRegistry().register(TestCatalogs.corpus());
    try (PlannerPipeline pipeline = PlannerPipeline.create(registered, PushdownPolicy.full())) {
      var result = pipeline.plan(sql, true);
      var plan =
          new RelToIr(
                  new chalk.planner.types.TypeMapper(result.physical().getCluster().getTypeFactory()),
                  result.physical().getCluster().getRexBuilder(),
                  org.apache.calcite.rel.metadata.RelMetadataQuery.instance(),
                  IrVersionGate.current())
              .toPlan(result.physical(), result.parameterRowType(), "test", 1L);
      return plan.getRoot().getProject().getExprs(0);
    }
  }
}
