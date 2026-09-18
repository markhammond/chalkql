package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.Disclosed;
import chalk.planner.entitlement.DisclosureMap;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.RequestContext;
import java.util.List;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * Visibility derived through a parent (docs/design/16-entitlements.md §3.13, D225–D231).
 *
 * <p>{@code messages} carries no tenancy column: a row is visible when its thread is, and what its
 * {@code content} discloses is decided by the roles held in the thread's organisation — on the
 * parent's side of the join, once per thread rather than once per message (D228).
 */
class ThroughTest {
  private static RegisteredCatalog tenancy;

  @BeforeAll
  static void register() {
    tenancy = new CatalogRegistry().register(TenancyCatalogs.catalog());
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

  private static DisclosureMap leafOf(PlannerPipeline.Result result, String table) {
    for (DisclosureMap leaf : result.entitledLeaves()) {
      if (leaf.table().equals(table)) {
        return leaf;
      }
    }
    throw new AssertionError("no entitled leaf for " + table + " in " + result.entitledLeaves());
  }

  // ------------------------------------------------------------------ the join

  @Test
  void a_manager_reads_a_message_through_its_thread() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, content FROM messages", TenancyCatalogs.manager(1));

    // Both leaves are the pass's: the child, and the parent's own entitled scan inside the join.
    assertThat(result.entitledLeaves()).hasSize(2);
    assertThat(leafOf(result, "messages").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    assertThat(leafOf(result, "threads").visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    assertThat(leafOf(result, "messages").of(2)).isEqualTo(Disclosed.FULL);
    assertThat(result.physicalPlanText()).contains("threads");
  }

  @Test
  void an_agent_reads_the_excerpt_the_threads_organisation_grants() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, content FROM messages", TenancyCatalogs.agent(1));

    assertThat(leafOf(result, "messages").of(2)).isEqualTo(Disclosed.MASKED);
  }

  @Test
  void a_principal_with_no_grant_sees_no_message() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, content FROM messages", TenancyCatalogs.nobody());

    assertThat(leafOf(result, "messages").visibility()).isEqualTo(DisclosureMap.Visibility.NONE);
  }

  @Test
  void a_global_grant_elides_the_join_and_sees_every_message() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, content FROM messages", TenancyCatalogs.global());

    assertThat(result.entitledLeaves()).hasSize(1);
    assertThat(leafOf(result, "messages").visibility()).isEqualTo(DisclosureMap.Visibility.ALL);
    assertThat(leafOf(result, "messages").of(2)).isEqualTo(Disclosed.FULL);
    assertThat(result.physicalPlanText()).doesNotContain("threads");
  }

  // ------------------------------------------------------------------ the oracle (§3.12, D230)

  @Test
  void explain_names_the_parent_the_key_and_what_the_principal_sees_of_it() throws Exception {
    chalk.planner.entitlement.PolicyExplain.Parent parent =
        parentOf(plan("SELECT id, content FROM messages", TenancyCatalogs.manager(1)));

    assertThat(parent.schema()).isEqualTo("main");
    assertThat(parent.table()).isEqualTo("threads");
    assertThat(parent.column()).isEqualTo("thread_id");
    assertThat(parent.parentColumn()).isEqualTo("id");
    assertThat(parent.elided()).isFalse();
    // The correlation key stays an ordinary column, so the statement's own join can use it (D227).
    assertThat(parent.keyDisclosure()).isEqualTo("FULL");
  }

  @Test
  void explain_says_when_the_join_was_elided() throws Exception {
    assertThat(parentOf(plan("SELECT id FROM messages", TenancyCatalogs.global())).elided())
        .isTrue();
  }

  /** The report's hashes cover the parent's descriptor too, because its leaf is one of the pass's. */
  @Test
  void the_report_names_the_parents_descriptor_as_well_as_the_childs() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id FROM messages", TenancyCatalogs.manager(1));

    assertThat(
            chalk.planner.entitlement.DisclosureReport.tables(result.entitledLeaves()).stream()
                .map(DisclosureMap::descriptorHash))
        .containsExactlyInAnyOrder(
            "1111222233334444111122223333444", "aaaabbbbccccddddaaaabbbbccccdddd");
  }

  private static chalk.planner.entitlement.PolicyExplain.Parent parentOf(
      PlannerPipeline.Result result) {
    for (chalk.planner.entitlement.PolicyExplain.Table table : result.explained()) {
      if (table.table().equals("messages")) {
        return table.parents().get(0);
      }
    }
    throw new AssertionError("no explanation for messages");
  }

  @Test
  void a_chain_of_parents_brings_both_joins() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, name FROM attachments", TenancyCatalogs.manager(1));

    assertThat(result.entitledLeaves()).hasSize(3);
    assertThat(leafOf(result, "attachments").visibility())
        .isEqualTo(DisclosureMap.Visibility.SOME);
  }
}
