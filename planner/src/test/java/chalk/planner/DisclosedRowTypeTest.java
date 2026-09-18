package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.PlaceholderPolicy;
import chalk.planner.rpc.v1.RequestContext;
import chalk.planner.types.ChalkTypeSystem;
import java.util.List;
import org.apache.calcite.jdbc.JavaTypeFactoryImpl;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.schema.SchemaPlus;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * The row type an entitled table publishes to the validator and the converter
 * (docs/design/16-entitlements.md §1, §3.5, §3.11; D161, D162, D224; F58).
 *
 * <p>§1's rule — "an entitled column's <em>output</em> type is nullable whenever any of its rules can
 * yield a NULL mask or a placeholder" — has to hold of the type the <em>statement</em> is written
 * over, not only of the leaf's output, or the statement's own expressions are simplified under a NOT
 * NULL the value they meet does not have. That was F58, in three shapes: {@code IS NOT NULL} folded
 * to TRUE at conversion, {@code COALESCE} collapsed to its first operand, and {@code CHAR_LENGTH}
 * typed NOT NULL and reduced to zero over the stand-in.
 *
 * <p>What is asserted here is the type and what follows from it. The rows every principal then sees
 * are the corpus's business ({@code m7-adversarial} 08, 17 and 18).
 */
class DisclosedRowTypeTest {
  private static RegisteredCatalog tenancy;
  private static RegisteredCatalog corpus;
  private static final RelDataTypeFactory TYPES = new JavaTypeFactoryImpl(ChalkTypeSystem.INSTANCE);

  @BeforeAll
  static void register() {
    tenancy = new CatalogRegistry().register(TenancyCatalogs.catalog());
    corpus = new CatalogRegistry().register(TestCatalogs.corpus());
  }

  private static RelDataType published(
      RegisteredCatalog catalog, PlaceholderPolicy policy, String table) {
    SchemaPlus schema = catalog.defaultSchema(policy);
    org.apache.calcite.schema.Table found =
        org.apache.calcite.jdbc.CalciteSchema.from(schema).getTable(table, false).getTable();
    assertThat(found).as("table %s", table).isNotNull();
    return found.getRowType(TYPES);
  }

  private static String shape(RelDataType rowType) {
    StringBuilder text = new StringBuilder();
    for (int i = 0; i < rowType.getFieldCount(); i++) {
      text.append(i == 0 ? "" : ", ")
          .append(rowType.getFieldList().get(i).getName())
          .append(rowType.getFieldList().get(i).getType().isNullable() ? "?" : "");
    }
    return text.toString();
  }

  // ------------------------------------------------------- the type the validator is handed

  @Test
  void a_withholdable_not_null_column_is_nullable_in_the_row_type_a_statement_is_written_over() {
    // first_name and last_name are MASKED for an agent and NONE outside; national_id discloses
    // nothing to anybody. All three are NOT NULL in the catalog, and all three arrive as a typed
    // NULL for some principal — so all three are nullable here (§1, D161).
    assertThat(shape(published(tenancy, PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL, "members")))
        .isEqualTo("id, org_id, first_name?, last_name?, national_id?, postcode?");
  }

  @Test
  void a_column_no_rule_can_withhold_keeps_its_declaration() {
    // orders: `amount` is FULL, AGGREGATE_ONLY or NONE and so widens; `note` is nullable already;
    // id, org_id and member_id carry no rule at all and the table's default is FULL.
    assertThat(shape(published(tenancy, PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL, "orders")))
        .isEqualTo("id, org_id, member_id, amount?, note?");
  }

  @Test
  void an_unentitled_table_is_untouched_in_an_entitled_catalog() {
    RelDataType declared =
        new ChalkTable(TenancyCatalogs.symbols(), TenancyCatalogs.catalog().getSchemas(0))
            .declaredRowType(TYPES);
    assertThat(shape(published(tenancy, PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL, "symbols")))
        .isEqualTo(shape(declared));
  }

  @Test
  void a_catalog_with_no_entitlement_publishes_one_schema_and_the_declared_types() {
    // §0's zero-cost property at this level: nothing is widened, so there is no second tree to
    // build and the schema a statement resolves through is the registered one itself.
    assertThat(corpus.defaultSchema(PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL))
        .isSameAs(corpus.defaultSchema());
    assertThat(corpus.defaultSchema(PlaceholderPolicy.PLACEHOLDER_POLICY_AS_EMPTY))
        .isSameAs(corpus.defaultSchema());
  }

  @Test
  void placeholders_as_empty_keeps_the_declared_nullability() {
    // D162: the empty value *is* a value, so nothing widens — and every statement over these
    // columns is simplified under the NOT NULL it will actually meet.
    assertThat(shape(published(tenancy, PlaceholderPolicy.PLACEHOLDER_POLICY_AS_EMPTY, "members")))
        .isEqualTo("id, org_id, first_name, last_name, national_id, postcode?");
    assertThat(tenancy.defaultSchema(PlaceholderPolicy.PLACEHOLDER_POLICY_AS_EMPTY))
        .isSameAs(tenancy.defaultSchema());
  }

  // ------------------------------------------------------- and what follows from it

  @Test
  void the_scan_beneath_the_leaf_still_reads_the_row_the_source_stores() throws Exception {
    // The disclosed row is the statement's; the leaf's own scan — and therefore the Read that
    // reaches the source — is the catalog's declaration, or the source's schema would not match it.
    RelNode physical = plan("SELECT id, first_name FROM members", TenancyCatalogs.manager(1));
    TableScan scan = firstScan(physical);
    assertThat(scan).isNotNull();
    assertThat(scan.getTable().getRowType().getField("first_name", false, false).getType()
            .isNullable())
        .as("the scan under Project_D reads the stored column")
        .isFalse();
  }

  /**
   * What each principal is handed, which is not the same question (F91, ADR 0062 §4). The type a
   * statement is <b>written over</b> is nullable for every principal, and that is the claim F58 is
   * about — the four tests below read it back off folded expressions. The type a <b>plan</b>
   * produces is the sanitiser's own: where the fold came to the column itself the projection is the
   * column, with the column's declared type, and where it did not it is the disclosed one.
   */
  @Test
  void a_plan_hands_back_the_sanitisers_own_type() throws Exception {
    // The manager's rules fold to FULL under her own row predicate, so the sanitiser *is* the
    // column and the plan says so. A cast here would only re-state a nullability the value cannot
    // have, over a column the report calls Full.
    RelNode manager = plan("SELECT id, first_name FROM members", TenancyCatalogs.manager(1));
    assertThat(manager.getRowType().getField("first_name", false, false).getType().isNullable())
        .as("a column this principal holds in full")
        .isFalse();
    assertThat(explain(manager)).doesNotContain("CAST");

    // The agent's do not: the sanitiser is a CASE over the mask and the catch-all, and its value
    // can be the typed NULL D161 widened the row type for.
    RelNode agent = plan("SELECT id, first_name FROM members", TenancyCatalogs.agent(1));
    assertThat(agent.getRowType().getField("first_name", false, false).getType().isNullable())
        .as("a column this principal holds under a rule that can withhold it")
        .isTrue();
  }

  @Test
  void is_not_null_over_a_withheld_column_is_no_longer_true_for_every_row() throws Exception {
    // F58's first shape. `national_id` discloses nothing to anybody, so the answer is no rows for
    // everybody — which is what the oracle says and what the converter used to fold away.
    assertThat(
            plan(
                    "SELECT id FROM members WHERE national_id IS NOT NULL",
                    TenancyCatalogs.manager(1))
                .getRowType()
                .getFieldCount())
        .isEqualTo(1);
    assertThat(
            explain(
                plan(
                    "SELECT id FROM members WHERE national_id IS NOT NULL",
                    TenancyCatalogs.manager(1))))
        .contains("ChalkValues(tuples=[[]])");
  }

  @Test
  void coalesce_over_a_withheld_column_answers_its_second_operand() throws Exception {
    // F58's second shape: the validator rewrites COALESCE into a CASE over IS NOT NULL, and that
    // test folded to TRUE under the base column's declaration.
    assertThat(
            explain(
                plan(
                    "SELECT id, COALESCE(national_id, 'absent') AS filled FROM members",
                    TenancyCatalogs.manager(1))))
        .contains("'absent'");
  }

  @Test
  void a_call_over_a_withheld_column_is_typed_nullable() throws Exception {
    // F58's third shape: CHAR_LENGTH took its nullability from its operand and was reduced to the
    // primitive zero over a stand-in it was told could not be NULL.
    RelNode physical =
        plan("SELECT CHAR_LENGTH(first_name) AS len FROM members", TenancyCatalogs.manager(1));
    assertThat(physical.getRowType().getFieldList().get(0).getType().isNullable()).isTrue();
    assertThat(explain(physical)).contains("CHAR_LENGTH");
  }

  private static String explain(RelNode rel) {
    return org.apache.calcite.plan.RelOptUtil.toString(rel);
  }

  private static org.apache.calcite.rel.core.@org.checkerframework.checker.nullness.qual.Nullable
          TableScan
      firstScan(RelNode rel) {
    if (rel instanceof TableScan scan) {
      return scan;
    }
    for (RelNode input : rel.getInputs()) {
      TableScan found = firstScan(input);
      if (found != null) {
        return found;
      }
    }
    return null;
  }

  private static RelNode plan(String sql, RequestContext context) throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            tenancy,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            BoundContext.of(context),
            PolicyOptions.DEFAULTS)) {
      return pipeline.plan(sql, true).physical();
    }
  }
}
