package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.Expr;
import chalk.ir.v1.Field;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.planner.catalog.InvalidCatalogException;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.ir.IrVersionGate;
import chalk.planner.ir.RelToIr;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.types.TypeMapper;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.jdbc.JavaTypeFactoryImpl;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.sql.type.SqlTypeName;
import org.junit.jupiter.api.Test;

/**
 * Keys nothing can compare, refused by the planner by name (D58, F128): a LIST as a grouping key, as a
 * {@code SELECT DISTINCT} column, or as the argument of a {@code DISTINCT} aggregate, which the
 * executor used to count as one value per group. The refusal names the clause and the column, which
 * is what the planner can say and the executor's own backstop at prepare cannot.
 */
final class ListKeyRefusalsTest {

  private static Plan plan(String sql) {
    return plan(TestCatalogs.corpus(), sql, PushdownPolicy.full());
  }

  private static Plan plan(CatalogContext catalog, String sql, PushdownPolicy policy) {
    RegisteredCatalog registered = RegisteredCatalog.of(catalog);
    try (PlannerPipeline pipeline = PlannerPipeline.create(registered, policy)) {
      PlannerPipeline.Result result = pipeline.plan(sql, true);
      RelNode physical = result.physical();
      TypeMapper types = new TypeMapper(physical.getCluster().getTypeFactory());
      RelToIr converter =
          new RelToIr(
              types,
              physical.getCluster().getRexBuilder(),
              physical.getCluster().getMetadataQuery(),
              IrVersionGate.current());
      return converter.toPlan(
          physical, result.parameterRowType(), registered.contextId(), registered.epoch());
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException(e);
    }
  }


  @Test
  void count_distinct_over_a_list_is_refused_naming_the_column() {
    assertThatThrownBy(() -> plan("SELECT COUNT(DISTINCT tags) FROM symbols"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("COUNT(DISTINCT) on the LIST column 'tags'")
        .hasMessageContaining("no ordering or equality");
  }

  @Test
  void group_by_a_list_is_refused_naming_the_column() {
    assertThatThrownBy(() -> plan("SELECT tags, COUNT(*) FROM symbols GROUP BY tags"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("GROUP BY or SELECT DISTINCT on the LIST column 'tags'");
  }

  @Test
  void select_distinct_over_a_list_is_refused_naming_the_column() {
    assertThatThrownBy(() -> plan("SELECT DISTINCT tags FROM symbols"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("GROUP BY or SELECT DISTINCT on the LIST column 'tags'");
  }

  @Test
  void order_by_a_list_stays_refused_as_before() {
    assertThatThrownBy(() -> plan("SELECT symbol, tags FROM symbols ORDER BY tags"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("ORDER BY on the LIST column 'tags'");
  }

  /** A distinct count of a comparable column beside the list is untouched. */
  @Test
  void count_distinct_of_a_scalar_beside_the_list_plans() {
    assertThat(plan("SELECT COUNT(DISTINCT symbol) FROM symbols")).isNotNull();
  }
}
