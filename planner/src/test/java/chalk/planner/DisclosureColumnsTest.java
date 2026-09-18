package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.PolicyException;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.ReportedDisclosure;
import chalk.planner.rpc.v1.RequestContext;
import java.util.List;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * The sibling {@code __disclosure} columns (docs/design/16-entitlements.md §3.12, D207): what a
 * caller is handed beside each column it asked for, and what it says row by row.
 */
class DisclosureColumnsTest {
  private static RegisteredCatalog tenancy;

  private static final PolicyOptions WITH_SIBLINGS =
      PolicyOptions.DEFAULTS.withDisclosureColumns("");

  @BeforeAll
  static void register() {
    tenancy = new CatalogRegistry().register(TenancyCatalogs.catalog());
  }

  private static PlannerPipeline.Result plan(String sql, RequestContext context) throws Exception {
    return plan(sql, context, WITH_SIBLINGS);
  }

  private static PlannerPipeline.Result plan(
      String sql, RequestContext context, PolicyOptions options) throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            tenancy,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            BoundContext.of(context),
            options)) {
      return pipeline.plan(sql, true);
    }
  }

  private static List<String> names(PlannerPipeline.Result result) {
    return result.physical().getRowType().getFieldNames();
  }

  @Test
  void a_sibling_stands_beside_every_column_of_an_entitled_table() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, first_name FROM members", TenancyCatalogs.agent(1));

    assertThat(names(result))
        .containsExactly("id", "id__disclosure", "first_name", "first_name__disclosure");
    // The name is not the data: a sibling discloses in full, whatever it says about its neighbour.
    assertThat(result.columnDisclosures())
        .containsExactly(
            ReportedDisclosure.REPORTED_DISCLOSURE_FULL,
            ReportedDisclosure.REPORTED_DISCLOSURE_FULL,
            ReportedDisclosure.REPORTED_DISCLOSURE_MASKED,
            ReportedDisclosure.REPORTED_DISCLOSURE_FULL);
  }

  @Test
  void a_constant_disclosure_still_gets_its_sibling_so_a_consumer_has_one_code_path()
      throws Exception {
    // An agent in one organisation sees a constant MASKED: the fold settled it, and the sibling is
    // the constant 'masked' rather than absent.
    PlannerPipeline.Result result = plan("SELECT first_name FROM members", TenancyCatalogs.agent(1));
    assertThat(names(result)).containsExactly("first_name", "first_name__disclosure");
    assertThat(result.physicalPlanText()).contains("MASKED");
  }

  @Test
  void a_mixed_principal_gets_the_disclosure_of_the_row_rather_than_of_the_statement()
      throws Exception {
    // A manager in O1 and an agent in O2: the same column is full in one organisation's rows and
    // masked in the other's, so the sibling is a CASE over the very conditions the sanitiser uses.
    PlannerPipeline.Result result =
        plan("SELECT first_name FROM members", TenancyCatalogs.managerAndAgent(1, 2));
    assertThat(result.columnDisclosures())
        .containsExactly(
            ReportedDisclosure.REPORTED_DISCLOSURE_PER_ROW,
            ReportedDisclosure.REPORTED_DISCLOSURE_FULL);
    assertThat(result.physicalPlanText()).contains("FULL").contains("MASKED");
  }

  @Test
  void a_derived_column_takes_the_meet_of_its_origins() throws Exception {
    PlannerPipeline.Result result =
        plan(
            "SELECT first_name || last_name AS whole_name FROM members",
            TenancyCatalogs.managerAndAgent(1, 2));
    assertThat(names(result)).containsExactly("whole_name", "whole_name__disclosure");
  }

  @Test
  void a_column_of_an_unentitled_table_has_no_sibling() throws Exception {
    PlannerPipeline.Result result = plan("SELECT symbol FROM symbols", TenancyCatalogs.agent(1));
    assertThat(names(result)).containsExactly("symbol");
  }

  @Test
  void a_suffixed_name_the_statement_already_produces_is_refused() throws Exception {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT first_name, id AS \"first_name__disclosure\" FROM members",
                    TenancyCatalogs.agent(1)))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("first_name__disclosure")
        .hasMessageContaining("IncludeDisclosureColumns");
  }

  @Test
  void a_configured_suffix_is_the_one_used() throws Exception {
    PolicyOptions options = PolicyOptions.DEFAULTS.withDisclosureColumns("_d");
    assertThat(names(plan("SELECT first_name FROM members", TenancyCatalogs.agent(1), options)))
        .containsExactly("first_name", "first_name_d");
  }

  @Test
  void nothing_is_added_when_the_request_does_not_ask() throws Exception {
    assertThat(
            names(
                plan(
                    "SELECT id, first_name FROM members",
                    TenancyCatalogs.agent(1),
                    PolicyOptions.DEFAULTS)))
        .containsExactly("id", "first_name");
  }

  @Test
  void the_siblings_travel_under_execute_time_binding_too() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT id, first_name FROM members", TenancyCatalogs.shape());
    assertThat(names(result))
        .containsExactly("id", "id__disclosure", "first_name", "first_name__disclosure");
  }
}
