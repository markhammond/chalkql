package chalk.planner;

import static chalk.planner.TestCatalogs.column;
import static chalk.planner.TestCatalogs.decimal;
import static chalk.planner.TestCatalogs.list;
import static chalk.planner.TestCatalogs.type;
import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.ColumnEntitlement;
import chalk.ir.v1.Disclosure;
import chalk.ir.v1.DisclosureRule;
import chalk.ir.v1.QueryLanguage;
import chalk.ir.v1.RowCountKind;
import chalk.ir.v1.Schema;
import chalk.ir.v1.SourceCapabilities;
import chalk.ir.v1.SourceKind;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableEntitlement;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.InvalidCatalogException;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.PlaceholderPolicy;
import chalk.planner.rpc.v1.RequestContext;
import chalk.planner.rpc.v1.StarExpansion;
import chalk.planner.rpc.v1.StarPolicy;
import java.util.List;
import java.util.function.UnaryOperator;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rex.RexNode;
import org.junit.jupiter.api.Test;

/**
 * What stands in for a value nothing discloses (docs/design/16-entitlements.md §3.11, D162, D224,
 * F47): the empty policy's table type by type, and a rule's own placeholder over the column's.
 *
 * <p>The empty policy is the half that had never been exercised past a string and a DATE. Its value
 * is Calcite's {@code RexBuilder.makeZeroLiteral}, whose table stops well short of Chalk's own type
 * vocabulary, and a column it has no value for used to be an error thrown inside the sidecar at
 * planning time. So there is one test per type kind here, and each of them says both halves — the
 * value and the nullability — because the two are what the policy is a choice between.
 */
class PlaceholderTest {
  /** A table of one column per type kind, every one of them redacted for every principal. */
  private static final String ONE_OF_EACH = "widths";

  // ------------------------------------------------------------------ F47: the empty policy

  @Test
  void an_empty_string_is_the_empty_string_and_stays_not_null() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_STRING, "'':VARCHAR CHARACTER SET \"UTF-8\"", false);
  }

  @Test
  void an_empty_binary_is_empty_bytes_and_stays_not_null() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_BINARY, "X'':VARBINARY", false);
  }

  @Test
  void an_empty_i8_is_zero_and_stays_not_null() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_I8, "0:TINYINT", false);
  }

  @Test
  void an_empty_i16_is_zero_and_stays_not_null() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_I16, "0:SMALLINT", false);
  }

  @Test
  void an_empty_i32_is_zero_and_stays_not_null() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_I32, "0", false);
  }

  @Test
  void an_empty_i64_is_zero_and_stays_not_null() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_I64, "0:BIGINT", false);
  }

  @Test
  void an_empty_fp32_is_zero_and_stays_not_null() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_FP32, "0.0E0:REAL", false);
  }

  @Test
  void an_empty_fp64_is_zero_and_stays_not_null() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_FP64, "0.0E0:DOUBLE", false);
  }

  @Test
  void an_empty_decimal_is_zero_and_stays_not_null() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_DECIMAL, "0.00:DECIMAL(10, 2)", false);
  }

  @Test
  void an_empty_bool_is_false_and_stays_not_null() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_BOOL, "false", false);
  }

  @Test
  void an_empty_date_is_the_epoch_and_stays_not_null() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_DATE, "1970-01-01", false);
  }

  @Test
  void an_empty_time_is_midnight_and_stays_not_null() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_TIME, "00:00:00", false);
  }

  @Test
  void an_empty_timestamp_is_the_epoch_and_stays_not_null() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_TIMESTAMP, "1970-01-01 00:00:00", false);
  }

  /**
   * Year one, and not the epoch. Calcite's zero for a zoned timestamp is {@code 0001-01-01} while
   * its zero for an unzoned one is the epoch; the documentation says so because the value is Calcite's
   * and not Chalk's, and a reader who assumed the two agreed would be wrong about this one.
   */
  @Test
  void an_empty_timestamp_tz_is_year_one_and_stays_not_null() throws Exception {
    assertEmpty(
        TypeKind.TYPE_KIND_TIMESTAMP_TZ,
        "0001-01-01 00:00:00:TIMESTAMP_WITH_LOCAL_TIME_ZONE(0)",
        false);
  }

  @Test
  void an_empty_uuid_is_a_typed_null_and_widens() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_UUID, "null:UUID", true);
  }

  @Test
  void an_empty_list_is_a_typed_null_and_widens() throws Exception {
    // A list column is nullable in the catalog already (D58), so the widening is a no-op here and
    // what the test is about is that the plan exists at all.
    assertEmpty(TypeKind.TYPE_KIND_LIST, "null:INTEGER NOT NULL ARRAY", true);
  }

  @Test
  void an_empty_interval_year_is_a_typed_null_and_widens() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_INTERVAL_YEAR, "null:INTERVAL YEAR TO MONTH", true);
  }

  @Test
  void an_empty_interval_day_is_a_typed_null_and_widens() throws Exception {
    assertEmpty(TypeKind.TYPE_KIND_INTERVAL_DAY, "null:INTERVAL DAY TO SECOND", true);
  }

  /**
   * The same column under the default policy, so that the two rows of the table are read side by
   * side: a type Calcite has an empty value for takes NULL here and keeps its declared nullability
   * there, and a type it has none for takes NULL either way.
   */
  @Test
  void the_null_policy_widens_every_type() throws Exception {
    for (TypeKind kind :
        List.of(
            TypeKind.TYPE_KIND_STRING,
            TypeKind.TYPE_KIND_I32,
            TypeKind.TYPE_KIND_TIMESTAMP_TZ,
            TypeKind.TYPE_KIND_UUID,
            TypeKind.TYPE_KIND_INTERVAL_DAY)) {
      RelNode plan = planOne(kind, PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL);
      assertThat(placeholderOf(plan).toString())
          .as("%s under PlaceholdersAsNull", kind)
          .startsWith("null:");
      assertThat(plan.getRowType().getFieldList().get(0).getType().isNullable())
          .as("%s widens under PlaceholdersAsNull", kind)
          .isTrue();
    }
  }

  // ------------------------------------------------------------------ D224: a rule's placeholder

  @Test
  void a_rules_own_placeholder_wins_over_the_columns() throws Exception {
    RelNode plan =
        plan(
            rulePlaceholderCatalog("'withheld by rule'", "'the column stand-in'"),
            PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL);
    assertThat(placeholderOf(plan).toString()).isEqualTo("'withheld by rule':VARCHAR CHARACTER SET \"UTF-8\"");
  }

  @Test
  void the_columns_placeholder_answers_where_the_rule_states_none() throws Exception {
    RelNode plan =
        plan(rulePlaceholderCatalog("", "'the column stand-in'"),
            PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL);
    assertThat(placeholderOf(plan).toString()).isEqualTo("'the column stand-in':VARCHAR CHARACTER SET \"UTF-8\"");
  }

  @Test
  void the_policy_answers_where_neither_states_one() throws Exception {
    RelNode plan =
        plan(rulePlaceholderCatalog("", ""), PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL);
    assertThat(placeholderOf(plan).toString()).startsWith("null:");
  }

  /** A rule placeholder is a value and never a query use: the outcome stays REDACTED. */
  @Test
  void a_rule_placeholder_leaves_the_outcome_redacted() throws Exception {
    RegisteredCatalog catalog =
        new CatalogRegistry()
            .register(rulePlaceholderCatalog("'withheld by rule'", "'the column stand-in'"));
    try (PlannerPipeline pipeline = pipeline(catalog, PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL)) {
      PlannerPipeline.Result result = pipeline.plan("SELECT secret FROM main.notes", true);
      assertThat(result.columnDisclosures())
          .containsExactly(chalk.planner.rpc.v1.ReportedDisclosure.REPORTED_DISCLOSURE_REDACTED);
    }
  }

  // ------------------------------------------------------------------ D224: the misuse refusals

  @Test
  void a_placeholder_on_a_rule_that_discloses_is_refused() {
    assertThatThrownBy(
            () ->
                new CatalogRegistry()
                    .register(
                        misuse(
                            rule ->
                                rule.setThen(Disclosure.DISCLOSURE_MASKED)
                                    .setMask("'****'")
                                    .setPlaceholder("'withheld'"))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("rules[0].placeholder")
        .hasMessageContaining("discloses MASKED and states a placeholder")
        .hasMessageContaining("meaningful only with NONE");
  }

  @Test
  void a_rule_placeholder_of_the_wrong_type_is_refused() {
    assertThatThrownBy(
            () -> new CatalogRegistry().register(misuse(rule -> rule.setPlaceholder("42"))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("rules[0].placeholder")
        .hasMessageContaining("does not match column type STRING");
  }

  @Test
  void a_rule_placeholder_reading_another_protected_column_is_refused() {
    assertThatThrownBy(
            () -> new CatalogRegistry().register(misuse(rule -> rule.setPlaceholder("other"))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("rules[0].placeholder")
        .hasMessageContaining("reads 'other', which is itself a protected column");
  }

  @Test
  void a_rule_placeholder_reading_the_column_itself_is_refused() {
    assertThatThrownBy(
            () -> new CatalogRegistry().register(misuse(rule -> rule.setPlaceholder("secret"))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("rules[0].placeholder")
        .hasMessageContaining("reads 'secret' itself")
        .hasMessageContaining("disclose exactly what it claims to have withheld");
  }

  /** The same rule applied to the column-level placeholder, which had the same hole. */
  @Test
  void a_column_placeholder_reading_the_column_itself_is_refused() {
    assertThatThrownBy(
            () ->
                new CatalogRegistry()
                    .register(
                        notes(
                            column ->
                                column
                                    .setPlaceholder("UPPER(secret)")
                                    .setOtherwise(Disclosure.DISCLOSURE_NONE))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("columns[0] (secret).placeholder")
        .hasMessageContaining("reads 'secret' itself");
  }

  // ------------------------------------------------------------------ the fixtures

  private static void assertEmpty(TypeKind kind, String value, boolean widens) throws Exception {
    RelNode plan = planOne(kind, PlaceholderPolicy.PLACEHOLDER_POLICY_AS_EMPTY);
    assertThat(placeholderOf(plan).toString()).as("%s under PlaceholdersAsEmpty", kind).isEqualTo(value);
    assertThat(plan.getRowType().getFieldList().get(0).getType().isNullable())
        .as("%s nullability under PlaceholdersAsEmpty", kind)
        .isEqualTo(widens || declared(kind).getNullable());
  }

  private static RelNode planOne(TypeKind kind, PlaceholderPolicy policy) throws Exception {
    RegisteredCatalog catalog = new CatalogRegistry().register(widths());
    try (PlannerPipeline pipeline = pipeline(catalog, policy)) {
      return pipeline.plan("SELECT " + columnName(kind) + " FROM main." + ONE_OF_EACH, true)
          .physical();
    }
  }

  private static RelNode plan(CatalogContext descriptor, PlaceholderPolicy policy) throws Exception {
    RegisteredCatalog catalog = new CatalogRegistry().register(descriptor);
    try (PlannerPipeline pipeline = pipeline(catalog, policy)) {
      return pipeline.plan("SELECT secret FROM main.notes", true).physical();
    }
  }

  private static PlannerPipeline pipeline(RegisteredCatalog catalog, PlaceholderPolicy policy) {
    return PlannerPipeline.create(
        catalog,
        PushdownPolicy.full(),
        chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
        List.of(),
        chalk.planner.plan.JoinPolicy.DEFAULT,
        BoundContext.of(principal()),
        new PolicyOptions(
            StarPolicy.STAR_POLICY_ALLOW,
            StarExpansion.STAR_EXPANSION_PLACEHOLDER,
            policy,
            PolicyOptions.NO_MIN_GROUP_SIZE,
            false,
            false));
  }

  /**
   * A principal binding one scalar nothing reads: an entitled catalog refuses a request that binds
   * nothing at all, and these descriptors mention no context name of their own.
   */
  private static RequestContext principal() {
    return RequestContext.newBuilder()
        .addScalars(
            chalk.planner.rpc.v1.ContextScalar.newBuilder()
                .setName("user")
                .setValue(
                    chalk.ir.v1.Expr.newBuilder()
                        .setType(type(TypeKind.TYPE_KIND_I32))
                        .setLiteral(chalk.ir.v1.Literal.newBuilder().setI32Value(7))))
        .build();
  }

  /** The single projected expression, which for a redacted column is the placeholder itself. */
  private static RexNode placeholderOf(RelNode plan) {
    RelNode node = plan;
    while (!(node instanceof Project) && !node.getInputs().isEmpty()) {
      node = node.getInput(0);
    }
    assertThat(node).isInstanceOf(Project.class);
    return ((Project) node).getProjects().get(0);
  }

  /** Every type kind Chalk's IR has, one column each, and none of them ever disclosed. */
  private static CatalogContext widths() {
    Table.Builder table =
        Table.newBuilder().setName(ONE_OF_EACH).setRowCount(4)
            .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT);
    for (TypeKind kind : kinds()) {
      table.addColumns(column(columnName(kind), declared(kind)));
    }
    return oneTable(
        table
            .setEntitlement(
                TableEntitlement.newBuilder()
                    .setDescriptorHash("00000000000000000000000000000001")
                    .setDefaultDisclosure(Disclosure.DISCLOSURE_NONE))
            .build());
  }

  /** {@code v_string}, {@code v_date}: prefixed, because half the type names are reserved words. */
  private static String columnName(TypeKind kind) {
    return "v_" + kind.name().replace("TYPE_KIND_", "").toLowerCase(java.util.Locale.ROOT);
  }

  private static List<TypeKind> kinds() {
    return List.of(
        TypeKind.TYPE_KIND_BOOL,
        TypeKind.TYPE_KIND_I8,
        TypeKind.TYPE_KIND_I16,
        TypeKind.TYPE_KIND_I32,
        TypeKind.TYPE_KIND_I64,
        TypeKind.TYPE_KIND_FP32,
        TypeKind.TYPE_KIND_FP64,
        TypeKind.TYPE_KIND_STRING,
        TypeKind.TYPE_KIND_BINARY,
        TypeKind.TYPE_KIND_DATE,
        TypeKind.TYPE_KIND_TIME,
        TypeKind.TYPE_KIND_TIMESTAMP,
        TypeKind.TYPE_KIND_TIMESTAMP_TZ,
        TypeKind.TYPE_KIND_DECIMAL,
        TypeKind.TYPE_KIND_UUID,
        TypeKind.TYPE_KIND_INTERVAL_DAY,
        TypeKind.TYPE_KIND_INTERVAL_YEAR,
        TypeKind.TYPE_KIND_LIST);
  }

  private static Type declared(TypeKind kind) {
    return switch (kind) {
      case TYPE_KIND_DECIMAL -> decimal(10, 2, false);
      case TYPE_KIND_LIST -> list(type(TypeKind.TYPE_KIND_I32));
      default -> type(kind);
    };
  }

  /**
   * {@code notes(secret, other)}, both protected, with one rule on {@code secret} the caller shapes
   * — which is what makes one refusal per misuse a one-line test.
   */
  private static CatalogContext misuse(UnaryOperator<DisclosureRule.Builder> rule) {
    return notes(
        column ->
            column
                .addRules(rule.apply(
                    DisclosureRule.newBuilder()
                        .setWhen("1 = 1")
                        .setThen(Disclosure.DISCLOSURE_NONE)))
                .setOtherwise(Disclosure.DISCLOSURE_NONE));
  }

  private static CatalogContext notes(UnaryOperator<ColumnEntitlement.Builder> secret) {
    return oneTable(
        Table.newBuilder()
            .setName("notes")
            .setRowCount(6)
            .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
            .addColumns(column("secret", type(TypeKind.TYPE_KIND_STRING)))
            .addColumns(column("other", type(TypeKind.TYPE_KIND_STRING)))
            .setEntitlement(
                TableEntitlement.newBuilder()
                    .setDescriptorHash("00000000000000000000000000000002")
                    .addColumns(secret.apply(ColumnEntitlement.newBuilder().setColumn(0)))
                    .addColumns(
                        ColumnEntitlement.newBuilder()
                            .setColumn(1)
                            .setOtherwise(Disclosure.DISCLOSURE_NONE)))
            .build());
  }

  /** {@code notes} with one NONE rule that carries {@code rulePlaceholder}, or none. */
  private static CatalogContext rulePlaceholderCatalog(
      String rulePlaceholder, String columnPlaceholder) {
    return notes(
        column ->
            column
                .setPlaceholder(columnPlaceholder)
                .addRules(
                    DisclosureRule.newBuilder()
                        .setWhen("1 = 1")
                        .setThen(Disclosure.DISCLOSURE_NONE)
                        .setPlaceholder(rulePlaceholder))
                .setOtherwise(Disclosure.DISCLOSURE_NONE));
  }

  private static CatalogContext oneTable(Table table) {
    return CatalogContext.newBuilder()
        .setContextId("placeholders")
        .setEpoch(1)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("main")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .setCapabilities(
                    SourceCapabilities.newBuilder()
                        .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE)
                        .build())
                .addTables(table))
        .build();
  }
}
