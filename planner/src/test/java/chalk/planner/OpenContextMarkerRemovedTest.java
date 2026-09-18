package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.DisclosureMap;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.RequestContext;
import java.util.List;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * F77: the membership marker over a context relation the request left <b>open</b>, which the
 * optimiser removed, and what clause 3 accepts in its place (ADR 0058;
 * docs/design/16-entitlements.md §2.1, §3.2, §3.10).
 *
 * <p>This is the whole route, end to end, rather than the check alone. Under partial binding (D232)
 * {@code members} carries {@code org_id IN (1)} from the folded half of its row predicate beside a
 * membership over the half the request left open, and the pass writes the second as a marker join
 * (§3.2). Where the statement's own predicate implies the folded half, the disjunction is true of
 * every row the statement can reach whatever the open list comes to hold; the Hep pre-pass proves it
 * and deletes the join, and with it the scan clause 3 was reading as its evidence.
 *
 * <p>{@code m7-tenancy} 04 is the statement F77 was found on and its predicate is over the
 * <em>disclosed</em> column, whose sanitiser for this principal carries the same organisation test —
 * so both spellings are here, the direct one and 04's own.
 */
class OpenContextMarkerRemovedTest {
  private static RegisteredCatalog catalog;

  /** A manager in organisation 1 with the agent half left open: F77's {@code u15} in miniature. */
  private static final RequestContext PARTIALLY_BOUND =
      TenancyCatalogs.managerWithAgentShape(1);

  @BeforeAll
  static void register() {
    catalog = new CatalogRegistry().register(TenancyCatalogs.catalog());
  }

  // ------------------------------------------------------------------ the acceptance

  /**
   * The statement's own predicate <em>is</em> the folded half of {@code Filter_R}. The optimiser
   * removes the marker join over the open relation, soundly, and the plan is accepted because the
   * plan's own predicates guarantee the conjunct whatever the open list holds.
   */
  @Test
  void a_statement_whose_predicate_implies_the_folded_half_plans() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, first_name FROM members WHERE org_id = 1", PARTIALLY_BOUND);

    assertThat(leafOf(result, "members").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    assertThat(result.physicalPlanText()).contains("members");
    assertThat(result.physicalPlanText()).doesNotContain("agent_orgs");
  }

  /**
   * {@code m7-tenancy} 04's own shape, which is where F77 was found: a statement over the
   * <em>disclosed</em> column (D195) beside the organisation test its sanitiser carries for this
   * principal. The whole projection is sanitised and the whole row predicate is still implied, so
   * the join goes and the plan is accepted.
   */
  @Test
  void the_corpus_statement_that_found_it_plans() throws Exception {
    PlannerPipeline.Result result =
        plan(
            "SELECT * FROM members WHERE org_id = 1 AND first_name LIKE 'T%' ORDER BY id",
            PARTIALLY_BOUND);

    assertThat(leafOf(result, "members").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    assertThat(result.physicalPlanText()).doesNotContain("agent_orgs");
  }

  // ------------------------------------------------------------------ and not a licence

  /**
   * The acceptance is the optimiser's own soundness and nothing more. A statement that restricts
   * nothing implies nothing, the marker over the open relation is what decides the row, and the
   * plan carries the scan clause 3 asks for.
   */
  @Test
  void a_statement_that_implies_nothing_keeps_the_scan() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, first_name FROM members", PARTIALLY_BOUND);

    assertThat(result.physicalPlanText()).contains("agent_orgs");
  }

  /**
   * And a statement whose predicate names <em>another</em> organisation keeps it too: the folded
   * half is false of every row it can reach, so the open membership is the only way in.
   */
  @Test
  void a_statement_over_another_organisation_keeps_the_scan() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, first_name FROM members WHERE org_id = 2", PARTIALLY_BOUND);

    assertThat(result.physicalPlanText()).contains("agent_orgs");
  }

  // ------------------------------------------------------------------ the plan

  private static PlannerPipeline.Result plan(String sql, RequestContext context) throws Exception {
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

  private static DisclosureMap leafOf(PlannerPipeline.Result result, String table) {
    for (DisclosureMap leaf : result.entitledLeaves()) {
      if (leaf.table().equals(table)) {
        return leaf;
      }
    }
    throw new AssertionError("no entitled leaf for " + table + " in " + result.entitledLeaves());
  }
}
