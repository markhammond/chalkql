package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.JoinPolicy;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import chalk.planner.redact.Labels;
import chalk.planner.redact.PlanTextRedactor;
import chalk.planner.redact.RedactionPolicy;
import chalk.planner.redact.SqlRedactor;
import chalk.planner.rpc.v1.RequestContext;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import org.apache.calcite.sql.validate.SqlConformanceEnum;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * A folded context value's marker names the entry it came from (D286, {@code
 * docs/design/37-redacted-sql.md} §5), in the plan text of a statement over an entitled table.
 *
 * <p>The tenancy fixture folds the principal's organisation lists into {@code members}' row
 * predicate, so what reaches the plan is a literal for a list of one, and a {@code Sarg} for a list
 * of several — the two shapes the label has to survive. Everything is asserted with the pseudonyms
 * masked; which eight hex characters they are is the business of the tests that compare two of them.
 */
class RedactionLabelsTest {
  private static final byte[] SALT = "a fixed salt".getBytes(StandardCharsets.UTF_8);

  /** A marker, with the label it may carry after its type. */
  private static final Pattern MARKER =
      Pattern.compile("/\\*REDACTED-([0-9a-f]{8}):([A-Z0-9_]+)((?: [^*]+)?)\\*/");

  private static RegisteredCatalog tenancy;

  @BeforeAll
  static void register() {
    tenancy = new CatalogRegistry().register(TenancyCatalogs.catalog());
  }

  /** One marker of a plan text: its pseudonym, its type and its label ("" for none). */
  private record Marker(String hex, String type, String label) {}

  private record Rendered(String physical, List<Marker> markers) {
    List<String> hexes() {
      return markers.stream().map(Marker::hex).toList();
    }

    List<Marker> ofType(String type) {
      return markers.stream().filter(m -> m.type().equals(type)).toList();
    }

    /** The text with the pseudonyms masked and Calcite's per-run node ids dropped. */
    String masked() {
      return MARKER.matcher(physical)
          .replaceAll("/*REDACTED-xxxxxxxx:$2$3*/")
          .replaceAll(", id = \\d+", "");
    }
  }

  private static Rendered plan(String sql, RequestContext context, boolean labelled)
      throws Exception {
    BoundContext bound = BoundContext.of(context);
    RedactionPolicy policy = RedactionPolicy.of(SALT, RedactionPolicy.Scope.ALL, true);
    SqlRedactor.Result statement = SqlRedactor.redact(sql, SqlConformanceEnum.DEFAULT, policy);
    PlanTextRedactor redactor =
        new PlanTextRedactor(
            SqlRedactor.pseudonyms(statement.structuralHash(), policy),
            policy,
            labelled ? Labels.of(bound) : Labels.NONE);
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            tenancy,
            PushdownPolicy.full(),
            SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            JoinPolicy.DEFAULT,
            bound,
            PolicyOptions.DEFAULTS)) {
      String physical =
          pipeline.finish(pipeline.front(sql), bound, true, null, null, redactor).physicalPlanText();
      List<Marker> markers = new ArrayList<>();
      Matcher matcher = MARKER.matcher(physical);
      while (matcher.find()) {
        markers.add(
            new Marker(
                matcher.group(1),
                matcher.group(2),
                matcher.group(3).strip()));
      }
      return new Rendered(physical, markers);
    }
  }

  /**
   * A list of one folds to a single comparison, and its literal is labelled with the list's name.
   *
   * <p>Organisation 2 rather than 1 throughout these tests: the fixture's principal is user 1, and
   * a value bound under two names carries both — which is right, and is not what these tests are
   * about.
   */
  @Test
  void a_folded_list_of_one_is_labelled_with_the_lists_name() throws Exception {
    Rendered rendered = plan("SELECT id FROM members", TenancyCatalogs.manager(2), true);

    assertThat(rendered.markers()).isNotEmpty();
    assertThat(rendered.markers())
        .anySatisfy(marker -> assertThat(marker.label()).isEqualTo("@ctx.manager_orgs"));
    assertThat(rendered.physical()).doesNotContain("agent_orgs");
  }

  /**
   * A list of several folds into one {@code Sarg}, which is labelled as one set — the name every
   * one of its points carries — and never expanded into its elements.
   */
  @Test
  void a_folded_list_is_labelled_as_one_set() throws Exception {
    Rendered rendered = plan("SELECT id FROM members", TenancyCatalogs.manager(2, 3, 4), true);

    assertThat(rendered.ofType("SARG")).isNotEmpty();
    assertThat(rendered.ofType("SARG"))
        .anySatisfy(marker -> assertThat(marker.label()).isEqualTo("@ctx.manager_orgs"));
    assertThat(rendered.physical()).doesNotContain("Sarg[");
  }

  /**
   * A set the optimiser merged out of two lists is nobody's list: the manager's and the agent's
   * organisations meet in one {@code Sarg} for the row predicate, and that one carries no name,
   * while each list's own set — in the disclosure rules — still carries its own.
   */
  @Test
  void a_set_merged_from_two_lists_carries_no_name() throws Exception {
    Rendered rendered =
        plan(
            "SELECT id FROM members",
            TenancyCatalogs.managerAndAgentLists(new int[] {2, 3}, new int[] {3, 4}),
            true);

    List<String> labels = rendered.ofType("SARG").stream().map(Marker::label).distinct().toList();
    assertThat(labels).contains("");
    assertThat(labels).doesNotContain("@ctx.manager_orgs,@ctx.agent_orgs");
    assertThat(labels)
        .allSatisfy(
            label ->
                assertThat(label).isIn("", "@ctx.manager_orgs", "@ctx.agent_orgs"));
  }

  /** Under a shape-only context nothing folds, so there is nothing to label. */
  @Test
  void a_shape_only_context_labels_nothing() throws Exception {
    Rendered rendered = plan("SELECT id FROM members", TenancyCatalogs.shape(), true);

    assertThat(rendered.markers()).allSatisfy(marker -> assertThat(marker.label()).isEmpty());
  }

  /** A label is a rendering: the same request without one carries the very same pseudonyms. */
  @Test
  void a_label_moves_no_pseudonym() throws Exception {
    RequestContext context = TenancyCatalogs.manager(2, 3, 4);
    Rendered labelled = plan("SELECT id FROM members", context, true);
    Rendered plain = plan("SELECT id FROM members", context, false);

    assertThat(labelled.hexes()).isEqualTo(plain.hexes());
    assertThat(plain.markers()).allSatisfy(marker -> assertThat(marker.label()).isEmpty());
    assertThat(labelled.masked().replace(" @ctx.manager_orgs", "")).isEqualTo(plain.masked());
  }
}
