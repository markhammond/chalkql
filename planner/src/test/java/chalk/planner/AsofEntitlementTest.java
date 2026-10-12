package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import java.util.List;
import org.junit.jupiter.api.Test;

/**
 * An ASOF join over entitled tables (D337). The entitlement pass rebuilt every join it carried a
 * column through or retyped as a plain join of the same type, which dropped an ASOF join's match
 * condition, and Calcite then failed reading it as an ASOF join: a planner internal error for any
 * ASOF join under a policy.
 */
class AsofEntitlementTest {

  @Test
  void an_asof_join_over_entitled_tables_plans() throws Exception {
    String plan =
        plan(
            "SELECT m.id, x.org_id FROM members m ASOF JOIN members x"
                + " MATCH_CONDITION m.id >= x.id ON TRUE");

    assertThat(plan).contains("ChalkAsOfJoin").contains("joinType=[asof]");
  }

  /** And with a condition on one input, which filters that input under the join. */
  @Test
  void an_asof_join_over_entitled_tables_with_a_condition_on_one_input_plans() throws Exception {
    String plan =
        plan(
            "SELECT m.id, x.org_id FROM members m LEFT ASOF JOIN members x"
                + " MATCH_CONDITION m.id >= x.id ON x.org_id = 1");

    assertThat(plan).contains("ChalkAsOfJoin").contains("left_asof");
  }

  private static String plan(String sql) throws Exception {
    RegisteredCatalog registered = new CatalogRegistry().register(TenancyCatalogs.catalog());
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            registered,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            BoundContext.of(TenancyCatalogs.manager(1, 2)),
            PolicyOptions.DEFAULTS)) {
      return pipeline.plan(sql, true).physicalPlanText();
    }
  }
}
