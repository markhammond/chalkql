package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.Disclosed;
import chalk.planner.entitlement.DisclosureMap;
import chalk.planner.entitlement.PolicyException;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.PlaceholderPolicy;
import chalk.planner.rpc.v1.ReportedDisclosure;
import chalk.planner.rpc.v1.RequestContext;
import chalk.planner.rpc.v1.StarPolicy;
import chalk.planner.rpc.v1.StarExpansion;
import java.util.List;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * The leaf rewrite (docs/design/16-entitlements.md §3.1–§3.3, D195–D197): what one entitled scan
 * becomes, what it discloses, and what the plan still says for a catalog that carries none.
 *
 * <p>Everything is asserted on the plan the pipeline produced rather than on the pass in isolation,
 * because the pass's whole contract is what the optimiser downstream then sees.
 */
class EntitlementPassTest {
  private static RegisteredCatalog tenancy;
  private static RegisteredCatalog corpus;

  @BeforeAll
  static void register() {
    tenancy = new CatalogRegistry().register(TenancyCatalogs.catalog());
    corpus = new CatalogRegistry().register(TestCatalogs.corpus());
  }

  private static PlannerPipeline.Result plan(String sql, RequestContext context) throws Exception {
    return plan(sql, context, PolicyOptions.DEFAULTS);
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

  // ------------------------------------------------------------------ the stage list

  @Test
  void the_pass_is_installed_only_for_a_catalog_that_carries_an_entitlement() throws Exception {
    assertThat(tenancy.hasEntitlements()).isTrue();
    assertThat(corpus.hasEntitlements()).isFalse();

    assertThat(plan("SELECT id FROM members", TenancyCatalogs.manager(1)).stages())
        .containsExactly("parse", "validate", "sql-to-rel", "entitlements", "hep", "volcano",
            "root-project");

    try (PlannerPipeline pipeline = PlannerPipeline.create(corpus, PushdownPolicy.full())) {
      assertThat(pipeline.plan("SELECT symbol FROM main.symbols", true).stages())
          .doesNotContain("entitlements")
          .containsExactly("parse", "validate", "sql-to-rel", "hep", "volcano", "root-project");
    }
  }

  @Test
  void a_table_with_no_entitlement_is_not_rewritten_even_in_an_entitled_catalog() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT symbol FROM symbols", TenancyCatalogs.manager(1));
    assertThat(result.entitledLeaves()).isEmpty();
    assertThat(result.physicalPlanText()).doesNotContain("entitled");
  }

  // ------------------------------------------------------------------ the leaf

  @Test
  void every_occurrence_of_an_entitled_table_is_rewritten_separately() throws Exception {
    PlannerPipeline.Result result =
        plan(
            "SELECT a.id FROM members a JOIN members b ON a.org_id = b.org_id",
            TenancyCatalogs.manager(1));

    assertThat(result.entitledLeaves()).hasSize(2);
    assertThat(result.entitledLeaves())
        .allSatisfy(leaf -> assertThat(leaf.qualifiedName()).isEqualTo("main.members"));
  }

  @Test
  void an_entitled_table_inside_a_sub_query_is_rewritten_too() throws Exception {
    PlannerPipeline.Result result =
        plan(
            "SELECT id FROM orgs WHERE EXISTS (SELECT 1 FROM members WHERE members.org_id = orgs.id)",
            TenancyCatalogs.agent(1));

    assertThat(result.entitledLeaves()).hasSize(1);
    assertThat(result.entitledLeaves().get(0).table()).isEqualTo("members");
  }

  // ------------------------------------------------------------------ the fold

  @Test
  void a_managers_own_organisation_discloses_every_column_in_full() throws Exception {
    DisclosureMap map = only(plan("SELECT * FROM members", TenancyCatalogs.manager(1)));

    // org_id is fixed to the manager's own list by Filter_R, so the FULL rule's condition
    // simplifies to TRUE under it and the sanitiser collapses to the bare column (§3.3).
    assertThat(map.of(2)).isEqualTo(Disclosed.FULL);
    assertThat(map.of(3)).isEqualTo(Disclosed.FULL);
    // national_id has no rule that can ever disclose it.
    assertThat(map.of(4)).isEqualTo(Disclosed.REDACTED);
    assertThat(map.visibility()).isEqualTo(DisclosureMap.Visibility.SOME);
    assertThat(map.withheldColumns()).containsExactly(4);
    assertThat(map.taintedColumns()).isEmpty();
  }

  @Test
  void a_one_organisation_agent_gets_a_constant_masked_rather_than_a_case() throws Exception {
    PlannerPipeline.Result result = plan("SELECT * FROM members", TenancyCatalogs.agent(2));
    DisclosureMap map = only(result);

    assertThat(map.of(2)).isEqualTo(Disclosed.MASKED);
    assertThat(map.of(3)).isEqualTo(Disclosed.MASKED);
    // The sanitiser is the bare mask: no CASE survives the simplification under Filter_R.
    assertThat(result.physicalPlanText()).doesNotContain("CASE");
    assertThat(result.physicalPlanText()).contains("SUBSTRING");
  }

  @Test
  void a_mixed_principal_keeps_a_case_and_reports_per_row() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT * FROM members", TenancyCatalogs.managerAndAgent(1, 2));
    DisclosureMap map = only(result);

    assertThat(map.of(2)).isEqualTo(Disclosed.PER_ROW);
    assertThat(result.physicalPlanText()).contains("CASE");
  }

  @Test
  void a_principal_with_no_grant_folds_the_predicate_to_false() throws Exception {
    DisclosureMap map = only(plan("SELECT id FROM members", TenancyCatalogs.nobody()));
    assertThat(map.visibility()).isEqualTo(DisclosureMap.Visibility.NONE);
  }

  // ------------------------------------------------------------------ the query-field rule is gone

  @Test
  void a_predicate_on_a_masked_column_compares_the_masked_value() throws Exception {
    // D195: the earlier drafts' query-field rule would have made this zero rows. It is now a
    // predicate over SUBSTRING(last_name, 1, 1), which is what an agent's ORDER BY sorts by too.
    PlannerPipeline.Result result =
        plan("SELECT id FROM members WHERE last_name = 'T'", TenancyCatalogs.agent(2));
    assertThat(result.physicalPlanText()).contains("SUBSTRING");
    assertThat(only(result).of(3)).isEqualTo(Disclosed.MASKED);
  }

  // ------------------------------------------------------------------ the placeholder and the type

  @Test
  void a_withheld_not_null_column_becomes_a_typed_null_and_widens_the_output() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT national_id FROM members", TenancyCatalogs.manager(1));

    assertThat(result.physical().getRowType().getFieldList().get(0).getType().isNullable())
        .isTrue();
    assertThat(only(result).of(4)).isEqualTo(Disclosed.REDACTED);
  }

  // ------------------------------------------------------------------ population-only, for now

  @Test
  void an_allow_listed_aggregate_over_a_population_only_column_is_permitted_and_guarded()
      throws Exception {
    PlannerPipeline.Result result = plan("SELECT SUM(amount) FROM orders", TenancyCatalogs.auditor(1));

    DisclosureMap map = only(result);
    assertThat(map.of(3)).isEqualTo(Disclosed.AGGREGATE);
    assertThat(map.taintedColumns()).containsExactly(3);
    // COUNT(amount) beside the sum, and the guard above it: k is the column's own floor of 3.
    assertThat(result.physicalPlanText()).contains("COUNT");
    assertThat(result.physicalPlanText()).contains(">=($1, 3)");
  }

  @Test
  void a_cast_to_a_numeric_type_is_not_a_use() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT AVG(CAST(amount AS DOUBLE)) FROM orders", TenancyCatalogs.auditor(1));
    assertThat(only(result).of(3)).isEqualTo(Disclosed.AGGREGATE);
  }

  @Test
  void every_other_use_of_a_population_only_column_is_refused_naming_the_use() {
    refused("SELECT amount FROM orders", "a projection to the result");
    refused("SELECT amount - amount FROM orders", "a value use");
    refused("SELECT SUM(amount * 2) FROM orders", "a value use");
    refused("SELECT id FROM orders WHERE amount > 0", "a predicate");
    refused("SELECT amount, COUNT(*) FROM orders GROUP BY amount", "a grouping key");
    refused("SELECT id FROM orders ORDER BY amount", "a sort key");
    refused("SELECT SUM(amount) FILTER (WHERE amount > 0) FROM orders", "FILTER");
    refused("SELECT SUM(amount) OVER () FROM orders", "a window aggregate");
    refused("SELECT MIN(amount) FROM orders", "outside this column's allow-list");
    refused(
        "SELECT o.id FROM orders o JOIN orders p ON o.amount = p.amount", "a join condition");
  }

  /**
   * D198's mixed principal: the rule holds whenever {@code AGGREGATE_ONLY} is <em>possible</em> for a
   * visible row, so a manager in one organisation and an auditor in another is refused rather than
   * handed values for one and placeholders for the other.
   */
  @Test
  void a_mixed_principal_is_refused_strictly_and_a_narrowing_predicate_is_the_remedy()
      throws Exception {
    RequestContext mixed = TenancyCatalogs.managerAndAuditor(1, 2);
    assertThatThrownBy(() -> plan("SELECT amount FROM orders", mixed))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("main.orders.amount");

    // Under WHERE org_id = 1 the folded disclosure is the constant FULL and there is no taint left.
    PlannerPipeline.Result narrowed = plan("SELECT amount FROM orders WHERE org_id = 1", mixed);
    assertThat(only(narrowed).of(3)).isEqualTo(Disclosed.FULL);
  }

  @Test
  void the_guard_reuses_a_count_the_statement_already_asks_for() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT COUNT(amount), SUM(amount) FROM orders", TenancyCatalogs.auditor(1));
    // Two calls, not three: the existing COUNT(amount) is the guard's own count.
    assertThat(count(result.physicalPlanText(), "COUNT(")).isEqualTo(1);
    assertThat(result.physical().getRowType().getFieldCount()).isEqualTo(2);
  }

  // ------------------------------------------------------------------ the read's outcomes (§3.10)

  /**
   * The breadcrumbs are a verdict per column, not a pair of ordinal lists: a host reconciling the
   * planner's answer against its own policy needs to know that column 0 was disclosed in full, and
   * "not mentioned" could not tell it apart from a column the planner never considered.
   */
  @Test
  void an_entitled_read_carries_one_outcome_per_table_column_in_ordinal_order() throws Exception {
    chalk.ir.v1.Read read = onlyRead(plan("SELECT id FROM members", TenancyCatalogs.manager(1)));

    // members has six columns and the read is projected to the two the plan needs — id and the
    // org_id Filter_R stands on — while the outcomes are the table's own, all six of them.
    assertThat(read.getProjectionList()).containsExactly(0, 1);
    assertThat(read.getDisclosuresCount()).isEqualTo(6);
    for (int i = 0; i < read.getDisclosuresCount(); i++) {
      assertThat(read.getDisclosures(i).getColumn()).isEqualTo(i);
    }
    assertThat(read.getDisclosures(0).getOutcome())
        .isEqualTo(chalk.ir.v1.DisclosureOutcome.DISCLOSURE_OUTCOME_FULL);
    assertThat(read.getDisclosures(4).getOutcome())
        .isEqualTo(chalk.ir.v1.DisclosureOutcome.DISCLOSURE_OUTCOME_REDACTED);
  }

  @Test
  void the_outcomes_carry_masked_aggregate_and_per_row() throws Exception {
    assertThat(outcomes(plan("SELECT id FROM members", TenancyCatalogs.agent(2))).get(2))
        .isEqualTo(chalk.ir.v1.DisclosureOutcome.DISCLOSURE_OUTCOME_MASKED);
    assertThat(
            outcomes(plan("SELECT id FROM members", TenancyCatalogs.managerAndAgent(1, 2))).get(2))
        .isEqualTo(chalk.ir.v1.DisclosureOutcome.DISCLOSURE_OUTCOME_PER_ROW);
    assertThat(outcomes(plan("SELECT SUM(amount) FROM orders", TenancyCatalogs.auditor(1))).get(3))
        .isEqualTo(chalk.ir.v1.DisclosureOutcome.DISCLOSURE_OUTCOME_AGGREGATE);
  }

  /** A read of a table with no entitlement carries none, so an unentitled plan is the same bytes. */
  @Test
  void an_unentitled_read_carries_no_outcome_at_all() throws Exception {
    chalk.ir.v1.Read read =
        onlyRead(plan("SELECT symbol FROM symbols", TenancyCatalogs.manager(1)));
    assertThat(read.getDisclosuresList()).isEmpty();
    assertThat(read.getDescriptorHash()).isEmpty();
  }

  /**
   * And an entitled read carries the descriptor it was compiled under (D231): the digest covers the
   * whole plan, so a changed policy is a new plan whatever the folded literals came to.
   */
  @Test
  void an_entitled_read_carries_the_descriptor_it_was_compiled_under() throws Exception {
    assertThat(onlyRead(plan("SELECT id FROM members", TenancyCatalogs.manager(1))).getDescriptorHash())
        .isEqualTo("0123456789abcdef0123456789abcdef");
  }

  private static List<chalk.ir.v1.DisclosureOutcome> outcomes(PlannerPipeline.Result result) {
    return onlyRead(result).getDisclosuresList().stream()
        .map(chalk.ir.v1.ColumnDisclosure::getOutcome)
        .toList();
  }

  private static chalk.ir.v1.Read onlyRead(PlannerPipeline.Result result) {
    chalk.ir.v1.Plan plan =
        new chalk.planner.ir.RelToIr(
                new chalk.planner.types.TypeMapper(
                    result.physical().getCluster().getTypeFactory()),
                result.physical().getCluster().getRexBuilder(),
                org.apache.calcite.rel.metadata.RelMetadataQuery.instance(),
                chalk.planner.ir.IrVersionGate.current())
            .toPlan(result.physical(), result.parameterRowType(), "test", 1L);
    List<chalk.ir.v1.Read> reads = new java.util.ArrayList<>();
    collectReads(plan.getRoot(), reads);
    assertThat(reads).hasSize(1);
    return reads.get(0);
  }

  private static void collectReads(chalk.ir.v1.Rel rel, List<chalk.ir.v1.Read> into) {
    if (rel.getKindCase() == chalk.ir.v1.Rel.KindCase.READ) {
      into.add(rel.getRead());
    }
    for (chalk.ir.v1.Rel input : IrNodes.inputs(rel)) {
      collectReads(input, into);
    }
  }

  // ------------------------------------------------------------------ the floor (D211)

  /** There is no shipped floor: a column that declares none and a host that gives none pay nothing. */
  @Test
  void a_column_that_inherits_a_default_of_zero_is_not_guarded() throws Exception {
    PlannerPipeline.Result result =
        planWith(0, "SELECT SUM(amount) FROM orders", TenancyCatalogs.auditor(1), PolicyOptions.DEFAULTS);

    assertThat(only(result).of(3)).isEqualTo(Disclosed.AGGREGATE);
    assertThat(result.physicalPlanText()).doesNotContain(">=(");
    // The report still says the value is a population, which is what the caller needs to know.
    assertThat(result.columnDisclosures())
        .containsExactly(ReportedDisclosure.REPORTED_DISCLOSURE_AGGREGATE);
  }

  @Test
  void a_column_that_inherits_a_host_default_is_guarded_by_it() throws Exception {
    PlannerPipeline.Result result =
        planWith(0, "SELECT SUM(amount) FROM orders", TenancyCatalogs.auditor(1), floorOf(5));

    assertThat(result.physicalPlanText()).contains(">=($1, 5)");
  }

  @Test
  void a_columns_own_one_disables_the_guard_whatever_the_host_default_says() throws Exception {
    PlannerPipeline.Result result =
        planWith(1, "SELECT SUM(amount) FROM orders", TenancyCatalogs.auditor(1), floorOf(5));

    assertThat(only(result).of(3)).isEqualTo(Disclosed.AGGREGATE);
    assertThat(result.physicalPlanText()).doesNotContain(">=(");
  }

  @Test
  void a_columns_own_floor_guards_with_no_host_default_at_all() throws Exception {
    PlannerPipeline.Result result =
        planWith(3, "SELECT SUM(amount) FROM orders", TenancyCatalogs.auditor(1), PolicyOptions.DEFAULTS);

    assertThat(result.physicalPlanText()).contains(">=($1, 3)");
  }

  /** A floor of one or less is refused nothing: what is permitted never depends on the floor. */
  @Test
  void an_unguarded_population_only_column_is_still_refused_every_other_use() {
    assertThatThrownBy(
            () ->
                planWith(
                    1, "SELECT amount FROM orders", TenancyCatalogs.auditor(1), PolicyOptions.DEFAULTS))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("main.orders.amount");
  }

  private static PolicyOptions floorOf(int floor) {
    return new PolicyOptions(
        StarPolicy.STAR_POLICY_ALLOW,
        StarExpansion.STAR_EXPANSION_PLACEHOLDER,
        PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL,
        floor,
        false,
        false);
  }

  private static PlannerPipeline.Result planWith(
      int declaredFloor, String sql, RequestContext context, PolicyOptions options)
      throws Exception {
    RegisteredCatalog catalog =
        new CatalogRegistry().register(TenancyCatalogs.catalog(declaredFloor));
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            catalog,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            BoundContext.of(context),
            options)) {
      return pipeline.plan(sql, true);
    }
  }

  private static int count(String text, String needle) {
    int found = 0;
    for (int at = text.indexOf(needle); at >= 0; at = text.indexOf(needle, at + 1)) {
      found++;
    }
    return found;
  }

  private static void refused(String sql, String use) {
    assertThatThrownBy(() -> plan(sql, TenancyCatalogs.auditor(1)))
        .describedAs(sql)
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("main.orders.amount")
        .hasMessageContaining(use);
  }

  // ------------------------------------------------------------------ the report

  @Test
  void the_report_meets_the_disclosures_of_a_columns_origins() throws Exception {
    // Masked on a derived column of an agent's masked one; Full on a manager's derived column.
    assertThat(disclosures("SELECT UPPER(first_name) FROM members", TenancyCatalogs.agent(2)))
        .containsExactly(ReportedDisclosure.REPORTED_DISCLOSURE_MASKED);
    assertThat(disclosures("SELECT UPPER(first_name) FROM members", TenancyCatalogs.manager(1)))
        .containsExactly(ReportedDisclosure.REPORTED_DISCLOSURE_FULL);
    // PerRow for the mixed principal, whose CASE the fold could not decide.
    assertThat(disclosures("SELECT first_name FROM members", TenancyCatalogs.managerAndAgent(1, 2)))
        .containsExactly(ReportedDisclosure.REPORTED_DISCLOSURE_PER_ROW);
    // Undisclosed for a column no rule can ever reach, and Full for one of an unentitled table.
    assertThat(disclosures("SELECT national_id, id FROM members", TenancyCatalogs.manager(1)))
        .containsExactly(
            ReportedDisclosure.REPORTED_DISCLOSURE_REDACTED,
            ReportedDisclosure.REPORTED_DISCLOSURE_FULL);
    // A column of an unentitled table beside an entitled one is Full; a statement that touches no
    // entitled table has no report at all, which is what "no bytes when unused" means here.
    assertThat(
            disclosures(
                "SELECT s.symbol, m.national_id FROM symbols s, members m",
                TenancyCatalogs.manager(1)))
        .containsExactly(
            ReportedDisclosure.REPORTED_DISCLOSURE_FULL,
            ReportedDisclosure.REPORTED_DISCLOSURE_REDACTED);
    assertThat(disclosures("SELECT symbol FROM symbols", TenancyCatalogs.manager(1))).isEmpty();
  }

  @Test
  void a_guarded_population_aggregate_reports_aggregate() throws Exception {
    assertThat(disclosures("SELECT SUM(amount) FROM orders", TenancyCatalogs.auditor(1)))
        .containsExactly(ReportedDisclosure.REPORTED_DISCLOSURE_AGGREGATE);
  }

  @Test
  void the_row_predicate_is_honestly_not_pushed_over_a_local_table() throws Exception {
    // A POCO table has no source to push into, and the report says so rather than claiming it.
    assertThat(plan("SELECT id FROM members", TenancyCatalogs.manager(1)).pushedRowPredicates())
        .isEmpty();
  }

  // ------------------------------------------------------------------ stars and placeholders

  @Test
  void a_star_over_an_entitled_table_is_refused_under_the_refusing_policies() {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT * FROM members",
                    TenancyCatalogs.manager(1),
                    options(StarPolicy.STAR_POLICY_REFUSE_OVER_ENTITLED)))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("main.members");

    assertThatThrownBy(
            () ->
                plan(
                    "SELECT * FROM symbols",
                    TenancyCatalogs.manager(1),
                    options(StarPolicy.STAR_POLICY_REFUSE_WHEN_ENTITLED)))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("any star");
  }

  @Test
  void a_star_over_an_unentitled_table_passes_refuse_over_entitled() throws Exception {
    assertThat(
            plan(
                    "SELECT * FROM symbols",
                    TenancyCatalogs.manager(1),
                    options(StarPolicy.STAR_POLICY_REFUSE_OVER_ENTITLED))
                .entitledLeaves())
        .isEmpty();
  }

  @Test
  void omit_drops_a_star_column_and_never_a_named_one() throws Exception {
    PolicyOptions omit =
        new PolicyOptions(
            StarPolicy.STAR_POLICY_ALLOW,
            StarExpansion.STAR_EXPANSION_OMIT,
            PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL,
            PolicyOptions.NO_MIN_GROUP_SIZE,
            false,
            false);

    PlannerPipeline.Result starred =
        plan("SELECT * FROM members", TenancyCatalogs.manager(1), omit);
    assertThat(starred.physical().getRowType().getFieldNames()).doesNotContain("national_id");

    PlannerPipeline.Result named =
        plan("SELECT national_id FROM members", TenancyCatalogs.manager(1), omit);
    assertThat(named.physical().getRowType().getFieldNames()).containsExactly("national_id");
  }

  /**
   * Several stars in one list, read off the validator's own expansion rather than inferred by
   * subtraction (§3.11, D161): each {@code t.*} owns the expanded items carrying its alias, and a
   * bare {@code *} beside them owns what is left. Before this, two stars read as all-named and
   * {@code Omit} silently degraded to placeholders.
   */
  @Test
  void omit_reads_each_qualified_stars_own_expansion() throws Exception {
    PlannerPipeline.Result result =
        plan(
            "SELECT m.*, o.* FROM members m JOIN orders o ON o.member_id = m.id",
            TenancyCatalogs.manager(1),
            omit());

    // members.national_id is disclosed to nobody and goes; every other column of both stars stays.
    assertThat(result.physical().getRowType().getFieldNames())
        .doesNotContain("national_id")
        .contains("first_name", "last_name", "amount", "note");
  }

  /** A bare star beside a named column: the star is what remains once the named one is accounted. */
  @Test
  void omit_reads_a_bare_star_beside_a_named_column() throws Exception {
    PlannerPipeline.Result result =
        plan(
            "SELECT *, org_id + 1 AS next_org FROM members",
            TenancyCatalogs.manager(1),
            omit());

    assertThat(result.physical().getRowType().getFieldNames())
        .doesNotContain("national_id")
        .containsSequence("id", "org_id", "first_name", "last_name", "postcode", "next_org");
  }

  /**
   * Two bare stars leave two unknowns and one equation, so the reading degrades to all-named and
   * the column stays as a placeholder — which is the conservative direction, and the report says so.
   */
  @Test
  void two_bare_stars_degrade_to_placeholders() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT *, * FROM members", TenancyCatalogs.manager(1), omit());

    assertThat(result.physical().getRowType().getFieldNames()).contains("national_id");
    assertThat(result.omitDegraded()).isTrue();
  }

  /**
   * And the coalesced column of a {@code JOIN … USING} expands to an expression rather than to a
   * qualified identifier, so no star owns it and the reading degrades for the same reason.
   */
  @Test
  void a_coalesced_join_column_degrades_to_placeholders() throws Exception {
    PlannerPipeline.Result result =
        plan(
            "SELECT * FROM members m JOIN orders o USING (org_id)",
            TenancyCatalogs.manager(1),
            omit());

    assertThat(result.physical().getRowType().getFieldNames()).contains("national_id");
    assertThat(result.omitDegraded()).isTrue();
  }

  private static PolicyOptions omit() {
    return new PolicyOptions(
        StarPolicy.STAR_POLICY_ALLOW,
        StarExpansion.STAR_EXPANSION_OMIT,
        PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL,
        PolicyOptions.NO_MIN_GROUP_SIZE,
        false,
        false);
  }

  /** {@code RedactedColumns.Refuse} is about the columns a star surfaced (D217). */
  @Test
  void refuse_names_the_column_the_star_surfaced() {
    assertThatThrownBy(() -> plan("SELECT * FROM members", TenancyCatalogs.manager(1), refuse()))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("national_id")
        .hasMessageContaining("star");
  }

  /**
   * And it leaves a column the statement <em>named</em> alone: §3.11's answer there is a
   * placeholder, since naming the column hides nothing (D217).
   */
  @Test
  void refuse_leaves_a_named_column_its_placeholder() throws Exception {
    PlannerPipeline.Result result =
        plan("SELECT national_id FROM members", TenancyCatalogs.manager(1), refuse());

    assertThat(result.physical().getRowType().getFieldNames()).containsExactly("national_id");
  }

  /** The second knob is what refuses a named one (D217). */
  @Test
  void the_named_knob_refuses_a_named_column() {
    PolicyOptions refuse =
        new PolicyOptions(
            StarPolicy.STAR_POLICY_ALLOW,
            StarExpansion.STAR_EXPANSION_PLACEHOLDER,
            PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL,
            PolicyOptions.NO_MIN_GROUP_SIZE,
            false,
            false,
            false,
            PolicyOptions.DEFAULT_DISCLOSURE_SUFFIX,
            chalk.planner.rpc.v1.NamedColumns.NAMED_COLUMNS_REFUSE);
    assertThatThrownBy(() -> plan("SELECT national_id FROM members", TenancyCatalogs.manager(1), refuse))
        .isInstanceOf(PolicyException.class)
        .hasMessageContaining("national_id")
        .hasMessageContaining("named by this statement");
  }

  private static PolicyOptions refuse() {
    return new PolicyOptions(
        StarPolicy.STAR_POLICY_ALLOW,
        StarExpansion.STAR_EXPANSION_REFUSE,
        PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL,
        PolicyOptions.NO_MIN_GROUP_SIZE,
        false,
        false);
  }

  @Test
  void placeholders_as_empty_keeps_the_declared_nullability() throws Exception {
    PolicyOptions empty =
        new PolicyOptions(
            StarPolicy.STAR_POLICY_ALLOW,
            StarExpansion.STAR_EXPANSION_PLACEHOLDER,
            PlaceholderPolicy.PLACEHOLDER_POLICY_AS_EMPTY,
            PolicyOptions.NO_MIN_GROUP_SIZE,
            false,
            false);
    PlannerPipeline.Result result =
        plan("SELECT national_id FROM members", TenancyCatalogs.manager(1), empty);
    assertThat(result.physical().getRowType().getFieldList().get(0).getType().isNullable())
        .isFalse();
  }

  private static java.util.List<ReportedDisclosure> disclosures(String sql, RequestContext context)
      throws Exception {
    return plan(sql, context).columnDisclosures();
  }

  private static PolicyOptions options(StarPolicy star) {
    return new PolicyOptions(
        star,
        StarExpansion.STAR_EXPANSION_PLACEHOLDER,
        PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL,
        PolicyOptions.NO_MIN_GROUP_SIZE,
        false,
        false);
  }

  private static DisclosureMap only(PlannerPipeline.Result result) {
    assertThat(result.entitledLeaves()).hasSize(1);
    return result.entitledLeaves().get(0);
  }
}
