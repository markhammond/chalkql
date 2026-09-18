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
 * A bound list above the fold ceiling standing inside a disjunction (F36,
 * docs/design/16-entitlements.md §2).
 *
 * <p>Such a membership test used to be refused naming {@code LITERAL_AGG}, the aggregate Calcite's
 * three-valued rewrite needs and the IR does not carry. The pass now splits the leaf into a
 * {@code UNION ALL} of a semi-join branch and an anti-join branch, so each membership stands where
 * it may answer two-valued and every row appears exactly once.
 *
 * <p>The rows themselves are asserted on the client side, against the oracle; what is asserted here
 * is the shape — that the split happened, that it did not happen where Calcite manages on its own,
 * and that the two branches are two leaves of one table.
 */
class MembershipSplitTest {
  private static RegisteredCatalog tenancy;

  @BeforeAll
  static void register() {
    tenancy = new CatalogRegistry().register(TenancyCatalogs.catalog());
  }

  /** A ceiling of one leaves any list of two or more rows unfolded, which is the whole point. */
  private static RequestContext unfolded(RequestContext context) {
    return context.toBuilder().setFoldMaxRows(1).build();
  }

  private static PlannerPipeline.Result plan(String sql, RequestContext context) throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            tenancy,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            BoundContext.of(context),
            PolicyOptions.DEFAULTS)) {
      return pipeline.plan(sql, true);
    }
  }

  @Test
  void one_unfolded_membership_in_a_top_level_conjunct_still_takes_the_plain_semi_join()
      throws Exception {
    // A manager and nothing else: the other two disjuncts fold to FALSE, so the membership is the
    // whole predicate and Calcite's own rewrite gives it a semi-join.
    PlannerPipeline.Result result =
        plan("SELECT id FROM orders", unfolded(TenancyCatalogs.manager(1, 2)));

    assertThat(result.entitledLeaves()).hasSize(1);
    assertThat(result.physicalPlanText()).doesNotContain("Union");
  }

  @Test
  void two_unfolded_memberships_in_a_disjunction_become_a_union_of_two_leaves() throws Exception {
    // A manager in two organisations and an agent in two others: both lists are above the ceiling
    // and the row predicate ORs them, which is the shape that used to be refused.
    PlannerPipeline.Result result =
        plan(
            "SELECT id FROM orders",
            unfolded(TenancyCatalogs.managerAndAgentLists(new int[] {1, 2}, new int[] {3, 4})));

    assertThat(result.physicalPlanText()).contains("Union");

    List<DisclosureMap> leaves = result.entitledLeaves();
    assertThat(leaves).hasSize(2);
    assertThat(leaves.get(0).qualifiedName()).isEqualTo("main.orders");
    assertThat(leaves.get(1).qualifiedName()).isEqualTo("main.orders");
    assertThat(leaves.get(0).visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    assertThat(leaves.get(1).visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
  }

  @Test
  void the_branches_disclose_what_their_own_rows_disclose() throws Exception {
    // `note` is FULL where the principal manages and MASKED where they act as an agent. Split by
    // membership, each branch knows which it is, so neither is PER_ROW and the caller is told the
    // union of the two rather than a shrug.
    PlannerPipeline.Result result =
        plan(
            "SELECT note FROM orders",
            unfolded(TenancyCatalogs.managerAndAgentLists(new int[] {1, 2}, new int[] {3, 4})));

    List<DisclosureMap> leaves = result.entitledLeaves();
    assertThat(leaves).hasSize(2);
    assertThat(leaves.get(0).of(4)).isEqualTo(chalk.planner.entitlement.Disclosed.FULL);
    assertThat(leaves.get(1).of(4)).isEqualTo(chalk.planner.entitlement.Disclosed.MASKED);

    // And the caller, who gets one column out of two alternative rows, is told so.
    assertThat(result.columnDisclosures())
        .containsExactly(chalk.planner.rpc.v1.ReportedDisclosure.REPORTED_DISCLOSURE_PER_ROW);
  }

  @Test
  void the_anti_join_branch_reads_the_same_context_relation_as_the_semi_join_branch()
      throws Exception {
    PlannerPipeline.Result result =
        plan(
            "SELECT id FROM orders",
            unfolded(TenancyCatalogs.managerAndAgentLists(new int[] {1, 2}, new int[] {3, 4})));

    // Both halves are joins against the bound lists, which is what keeps the rows out of the plan:
    // the semi-join branch takes the rows the first list holds, the anti-join branch the ones it
    // does not, and the second list decides those.
    assertThat(result.requiredRelations()).contains("manager_orgs", "agent_orgs");
    assertThat(result.physicalPlanText())
        .contains("joinType=[anti]")
        .contains("joinType=[semi]")
        .contains("ChalkContextScan(table=[[$chalk$ctx, manager_orgs]]")
        .contains("ChalkContextScan(table=[[$chalk$ctx, agent_orgs]]");
  }
}
