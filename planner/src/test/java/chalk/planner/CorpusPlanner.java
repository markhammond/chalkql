package chalk.planner;

import chalk.ir.v1.Plan;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.ir.IrVersionGate;
import chalk.planner.ir.RelToIr;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import chalk.planner.types.TypeMapper;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.sql.validate.SqlConformanceEnum;

/** Plans a corpus query straight to IR, without a channel. */
public final class CorpusPlanner {
  private final RegisteredCatalog catalog;

  public CorpusPlanner() {
    this.catalog = new CatalogRegistry().register(TestCatalogs.corpus());
  }

  public RegisteredCatalog catalog() {
    return catalog;
  }

  public Plan plan(String sql) {
    return plan(sql, PushdownPolicy.full());
  }

  /** A corpus query in the dialect its {@code -- conformance:} header asks for (D34). */
  public Plan plan(CorpusQueries.Query query) {
    return plan(query, PushdownPolicy.full());
  }

  public Plan plan(CorpusQueries.Query query, PushdownPolicy policy) {
    return plan(query.sql(), policy, query.conformance(), query.libraries());
  }

  /** The physical plan text of a corpus query, for the tests that assert on the rels it contains. */
  public String planText(CorpusQueries.Query query) {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(catalog, PushdownPolicy.full(), query.conformance(), query.libraries())) {
      return chalk.planner.diag.PlanText.withAttributes(pipeline.plan(query.sql(), false).physical());
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException("planning failed for: " + query.sql(), e);
    }
  }

  public Plan plan(String sql, PushdownPolicy policy) {
    return plan(sql, policy, SqlConfigs.DEFAULT_CONFORMANCE);
  }

  public Plan plan(String sql, PushdownPolicy policy, SqlConformanceEnum conformance) {
    return plan(sql, policy, conformance, java.util.List.of());
  }

  /** The same, with the dialect function libraries this statement asked for (D60). */
  public Plan plan(
      String sql,
      PushdownPolicy policy,
      SqlConformanceEnum conformance,
      java.util.List<org.apache.calcite.sql.fun.SqlLibrary> libraries) {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(catalog, policy, conformance, libraries)) {
      PlannerPipeline.Result result = pipeline.plan(sql, false);
      RelToIr toIr =
          new RelToIr(
              new TypeMapper(result.physical().getCluster().getTypeFactory()),
              result.physical().getCluster().getRexBuilder(),
              RelMetadataQuery.instance(),
              IrVersionGate.current());
      return toIr.toPlan(
          result.physical(), result.parameterRowType(), catalog.contextId(), catalog.epoch());
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException("planning failed for: " + sql, e);
    }
  }

  /** The physical plan text of a statement, for a test that asserts on the rels it contains. */
  public String planText(String sql) {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      return chalk.planner.diag.PlanText.withAttributes(pipeline.plan(sql, false).physical());
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException("planning failed for: " + sql, e);
    }
  }

  /**
   * The tree after conversion and decorrelation and before every rule — {@code PlannerPipeline}'s
   * own {@code logical}, which runs none of {@code plan}'s checks. That is what makes it the place
   * to observe a decorrelator's output for what it is.
   */
  public org.apache.calcite.rel.RelNode logical(String sql) {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      return pipeline.logical(sql);
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException("planning failed for: " + sql, e);
    }
  }
}
