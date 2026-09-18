package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.Disclosed;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.RequestContext;
import java.util.List;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * Execute-time binding (docs/design/16-entitlements.md §2, D209): one plan for every principal whose
 * bindings have the same shape, and the values bound when it runs.
 *
 * <p>What is asserted here is the plan the pipeline produces from a shape-only context — that every
 * scalar is a parameter carrying its own name, every list a bound relation, and every entitled column
 * decided per row — and that the same shape gives the same plan whoever is asking. Whether the rows
 * agree with prepare-time binding is the corpus's question, and it asks it as every principal.
 */
class ExecuteTimeBindingTest {
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

  @Test
  void a_shape_binds_every_list_as_a_relation_and_every_scalar_as_a_named_parameter()
      throws Exception {
    PlannerPipeline.Result result = plan("SELECT * FROM members", TenancyCatalogs.shape());

    assertThat(result.requiredRelations())
        .containsExactlyInAnyOrder("manager_orgs", "agent_orgs");
    assertThat(result.entitledLeaves()).hasSize(1);
  }

  @Test
  void every_entitled_column_is_decided_per_row_because_nothing_folded() throws Exception {
    PlannerPipeline.Result result = plan("SELECT * FROM members", TenancyCatalogs.shape());

    // first_name and last_name have two live rules and a default under a shape, so no name is
    // constant; national_id has no rule at all and is constant nothing whatever is bound.
    assertThat(result.entitledLeaves().get(0).of(2)).isEqualTo(Disclosed.PER_ROW);
    assertThat(result.entitledLeaves().get(0).of(3)).isEqualTo(Disclosed.PER_ROW);
    assertThat(result.entitledLeaves().get(0).of(4)).isEqualTo(Disclosed.REDACTED);
  }

  @Test
  void one_shape_is_one_plan_whoever_is_asking() throws Exception {
    String first = shapeOf(plan("SELECT * FROM members", TenancyCatalogs.shape()));
    String second = shapeOf(plan("SELECT * FROM members", TenancyCatalogs.shape()));
    assertThat(second).isEqualTo(first);

    // And it is not the folded plan: a manager's has literals where this has bound relations.
    String folded = shapeOf(plan("SELECT * FROM members", TenancyCatalogs.manager(1)));
    assertThat(folded).isNotEqualTo(first);
  }

  @Test
  void a_membership_is_one_marker_per_list_and_not_one_per_column() throws Exception {
    // members reads manager_orgs and agent_orgs in its row predicate and in both column rules —
    // eight uses, two lists, and therefore two scans of a bound relation and no more (§3.2).
    String text = plan("SELECT * FROM members", TenancyCatalogs.shape()).physicalPlanText();
    assertThat(count(text, "ChalkContextScan")).isEqualTo(2);
  }

  @Test
  void a_bound_scalar_is_a_parameter_carrying_its_own_name() throws Exception {
    RegisteredCatalog notes = new CatalogRegistry().register(ExecuteTimeBindingCatalogs.catalog());
    PlannerPipeline.Result result;
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            notes,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            BoundContext.of(ExecuteTimeBindingCatalogs.shape()),
            PolicyOptions.DEFAULTS)) {
      result = pipeline.plan("SELECT id, body FROM notes", true);
    }

    assertThat(result.requiredScalars()).containsExactly("user");
    assertThat(result.requiredRelations()).containsExactly("manager_orgs");
    // The parameter reads as the policy wrote it, in plan text and in the explain oracle alike.
    assertThat(result.physicalPlanText()).contains("@ctx.user");
    assertThat(result.parameterRowType().getFieldCount()).isZero();
  }

  // ------------------------------------------------------------------ partial binding (§2.1, D232)

  @Test
  void a_partly_bound_leaf_carries_a_folded_list_and_a_marker_at_once() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT * FROM members", TenancyCatalogs.managerWithAgentShape(1));

    // The open half is the only thing execution still has to bind; the folded half is in the plan.
    assertThat(result.requiredRelations()).containsExactly("agent_orgs");
    assertThat(result.requiredScalars()).isEmpty();

    // One bound relation scanned — the open one — and the folded one is a literal in the leaf's own
    // filter rather than a scan of anything.
    String text = result.physicalPlanText();
    assertThat(count(text, "ChalkContextScan")).isEqualTo(1);
    assertThat(text).contains("agent_orgs");
    assertThat(text).doesNotContain("manager_orgs");
  }

  @Test
  void what_the_fold_settles_is_constant_and_what_it_does_not_is_per_row() throws Exception {
    // A manager everywhere they can see, with the agent half open: first_name can still be masked
    // by an agent grant nobody has bound yet, so it stays per-row; national_id is nothing to
    // anybody and stays constant whatever is bound.
    PlannerPipeline.Result result =
        plan("SELECT * FROM members", TenancyCatalogs.managerWithAgentShape(1));
    assertThat(result.entitledLeaves().get(0).of(2)).isEqualTo(Disclosed.PER_ROW);
    assertThat(result.entitledLeaves().get(0).of(4)).isEqualTo(Disclosed.REDACTED);
  }

  @Test
  void one_folded_half_is_one_plan_whatever_the_open_half_would_hold() throws Exception {
    String first = shapeOf(plan("SELECT * FROM members", TenancyCatalogs.managerWithAgentShape(1)));
    String second = shapeOf(plan("SELECT * FROM members", TenancyCatalogs.managerWithAgentShape(1)));
    assertThat(second).isEqualTo(first);

    // Another tenant is another plan, which is what the folded half being in the plan means.
    assertThat(shapeOf(plan("SELECT * FROM members", TenancyCatalogs.managerWithAgentShape(2))))
        .isNotEqualTo(first);
  }

  /**
   * A shape is a <em>type</em>, and the descriptor's own is what it has to agree with: a scalar
   * declared as the wrong one is refused at prepare rather than at execution, where there would be a
   * plan built for it (§2.1, D232).
   */
  @Test
  void a_shape_whose_type_the_descriptor_disagrees_with_is_refused_at_prepare() throws Exception {
    RegisteredCatalog notes = new CatalogRegistry().register(ExecuteTimeBindingCatalogs.catalog());
    // `created_by = @ctx.user` over an I32 column: declared as one, this plans.
    assertThat(planNotes(notes, ExecuteTimeBindingCatalogs.partial(
            chalk.ir.v1.TypeKind.TYPE_KIND_I32, 1)).requiredScalars())
        .containsExactly("user");

    org.assertj.core.api.Assertions.assertThatThrownBy(
            () ->
                planNotes(
                    notes,
                    ExecuteTimeBindingCatalogs.partial(chalk.ir.v1.TypeKind.TYPE_KIND_DATE, 1)))
        .hasMessageContaining("main.notes does not type-check against the table")
        .hasMessageContaining("<INTEGER> = <DATE>");
  }

  private static PlannerPipeline.Result planNotes(RegisteredCatalog notes, RequestContext context)
      throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            notes,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            BoundContext.of(context),
            PolicyOptions.DEFAULTS)) {
      return pipeline.plan("SELECT id, body FROM notes", true);
    }
  }

  /** The plan without the node identifiers, which are a counter and not a property of the plan. */
  private static String shapeOf(PlannerPipeline.Result result) {
    return result.physicalPlanText().replaceAll("id = \\d+", "id = n");
  }

  private static int count(String text, String needle) {
    int found = 0;
    int at = text.indexOf(needle);
    while (at >= 0) {
      found++;
      at = text.indexOf(needle, at + needle.length());
    }
    return found;
  }
}
