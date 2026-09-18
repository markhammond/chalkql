package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.Expr;
import chalk.ir.v1.Field;
import chalk.ir.v1.Literal;
import chalk.ir.v1.RowType;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.VirtualRow;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.ContextFold;
import chalk.planner.entitlement.ContextSql;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import chalk.planner.rpc.v1.ContextRelationKind;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.ContextScalar;
import chalk.planner.rpc.v1.RequestContext;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.parser.SqlParser;
import org.junit.jupiter.api.Test;

/**
 * Context binding and the fold (docs/design/16-entitlements.md §2, D152, D197).
 *
 * <p>Three levels, because three things can be wrong independently: the token rewrite, which must
 * not touch a string, a quoted identifier or a comment; the parse-tree substitution, which must
 * produce SQL that means what the binding means; and the conversion, which must produce the shapes
 * the pushdown gate and the executor already know — an OR chain or a {@code SEARCH} for a small
 * list, a semi-join over a {@code ContextTable} for a large one.
 */
class ContextBindingTest {

  // ------------------------------------------------------------------ the vocabulary

  private static Type i32() {
    return Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32).build();
  }

  private static Type string() {
    return Type.newBuilder().setKind(TypeKind.TYPE_KIND_STRING).build();
  }

  private static Expr literal(int value) {
    return Expr.newBuilder().setType(i32()).setLiteral(Literal.newBuilder().setI32Value(value)).build();
  }

  private static Expr literal(String value) {
    return Expr.newBuilder()
        .setType(string())
        .setLiteral(Literal.newBuilder().setStringValue(value))
        .build();
  }

  private static Expr nullLiteral() {
    return Expr.newBuilder()
        .setType(i32().toBuilder().setNullable(true).build())
        .setLiteral(Literal.newBuilder().setIsNull(true))
        .build();
  }

  private static ContextScalar scalar(String name, Expr value) {
    return ContextScalar.newBuilder().setName(name).setValue(value).build();
  }

  /** A single-column list of integers. */
  private static ContextRelationValue list(String name, int... values) {
    ContextRelationValue.Builder builder =
        ContextRelationValue.newBuilder()
            .setName(name)
            .setKind(ContextRelationKind.CONTEXT_RELATION_KIND_LIST)
            .setRowType(RowType.newBuilder().addFields(Field.newBuilder().setName("id").setType(i32())));
    for (int value : values) {
      builder.addRows(VirtualRow.newBuilder().addValues(literal(value)));
    }
    return builder.build();
  }

  /** A two-column list of integer pairs. */
  private static ContextRelationValue pairs(String name, int... flat) {
    ContextRelationValue.Builder builder =
        ContextRelationValue.newBuilder()
            .setName(name)
            .setKind(ContextRelationKind.CONTEXT_RELATION_KIND_LIST)
            .setRowType(
                RowType.newBuilder()
                    .addFields(Field.newBuilder().setName("a").setType(i32()))
                    .addFields(Field.newBuilder().setName("b").setType(i32())));
    for (int i = 0; i < flat.length; i += 2) {
      builder.addRows(
          VirtualRow.newBuilder().addValues(literal(flat[i])).addValues(literal(flat[i + 1])));
    }
    return builder.build();
  }

  /** A type factory for the fold's own use; a shape-only context is the only thing that needs it. */
  private static org.apache.calcite.rel.type.RelDataTypeFactory typeFactory() {
    return new org.apache.calcite.jdbc.JavaTypeFactoryImpl(
        chalk.planner.types.ChalkTypeSystem.INSTANCE);
  }

  private static BoundContext bind(RequestContext.Builder builder) {
    return BoundContext.of(builder.build());
  }

  private static String fold(String sql, BoundContext context) {
    SqlParser.Config parserConfig = SqlConfigs.parser(SqlConfigs.DEFAULT_CONFORMANCE);
    String rewritten = ContextSql.rewrite(sql, context);
    try {
      SqlNode parsed = SqlParser.create(rewritten, parserConfig).parseQuery();
      return unparse(ContextFold.fold(parsed, context, parserConfig, typeFactory()).node())
          .replaceAll("\\s+", " ");
    } catch (org.apache.calcite.sql.parser.SqlParseException failure) {
      throw new AssertionError("parsing " + rewritten, failure);
    }
  }

  /**
   * Back to text in the dialect the parser reads (double quotes quote, case preserved), which is
   * what makes the fold's output re-parseable. The pass itself never unparses — it hands the folded
   * node straight to {@code SqlToRelConverter} — so this is the test harness's step and not the
   * mechanism's.
   */
  private static String unparse(SqlNode node) {
    return node.toSqlString(org.apache.calcite.sql.dialect.CalciteSqlDialect.DEFAULT).getSql();
  }

  // ------------------------------------------------------------------ the token rewrite

  @Test
  public void a_scalar_becomes_a_call_and_a_list_becomes_a_ctx_identifier() {
    BoundContext context =
        bind(RequestContext.newBuilder().addScalars(scalar("user", literal(7))).addRelations(list("orgs", 1)));

    assertThat(ContextSql.rewrite("created_by = @ctx.user", context))
        .isEqualTo("created_by = \"$chalk$ctx\"('user')");
    assertThat(ContextSql.rewrite("org_id IN (@ctx.orgs)", context))
        .isEqualTo("org_id IN (\"$chalk$ctx\".\"orgs\")");
  }

  /**
   * The reason this is a scanner and not a pattern: a mask is exactly the kind of expression that
   * holds a string, and a policy is exactly the kind of text somebody comments.
   */
  @Test
  public void nothing_inside_a_string_a_quoted_identifier_or_a_comment_is_rewritten() {
    BoundContext context = bind(RequestContext.newBuilder().addScalars(scalar("user", literal(7))));

    assertThat(ContextSql.rewrite("'@ctx.user'", context)).isEqualTo("'@ctx.user'");
    assertThat(ContextSql.rewrite("'it''s @ctx.user'", context)).isEqualTo("'it''s @ctx.user'");
    assertThat(ContextSql.rewrite("\"@ctx.user\"", context)).isEqualTo("\"@ctx.user\"");
    assertThat(ContextSql.rewrite("x -- @ctx.user\n", context)).isEqualTo("x -- @ctx.user\n");
    assertThat(ContextSql.rewrite("x /* @ctx.user */ y", context)).isEqualTo("x /* @ctx.user */ y");
    assertThat(ContextSql.rewrite("a = '@ctx.user' OR b = @ctx.user", context))
        .isEqualTo("a = '@ctx.user' OR b = \"$chalk$ctx\"('user')");
  }

  @Test
  public void an_unbound_name_is_refused_naming_what_is_bound() {
    BoundContext context = bind(RequestContext.newBuilder().addScalars(scalar("user", literal(7))));

    assertThatThrownBy(() -> ContextSql.rewrite("x = @ctx.usr", context))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("'@ctx.usr' is not bound")
        .hasMessageContaining("user (scalar)");
  }

  @Test
  public void a_context_reference_that_names_nothing_is_refused() {
    assertThatThrownBy(() -> ContextSql.rewrite("x = @ctx.", BoundContext.EMPTY))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("names nothing");
  }

  // ------------------------------------------------------------------ bind-time checks

  @Test
  public void a_null_member_of_a_list_is_refused_at_bind() {
    RequestContext proto =
        RequestContext.newBuilder()
            .addRelations(
                ContextRelationValue.newBuilder()
                    .setName("orgs")
                    .setKind(ContextRelationKind.CONTEXT_RELATION_KIND_LIST)
                    .setRowType(
                        RowType.newBuilder()
                            .addFields(Field.newBuilder().setName("id").setType(i32())))
                    .addRows(VirtualRow.newBuilder().addValues(nullLiteral())))
            .build();

    assertThatThrownBy(() -> BoundContext.of(proto))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("has a NULL in row 0");
  }

  /** A relation is a set the host handed over; a NULL in one is the host's business. */
  @Test
  public void a_null_in_a_relation_is_accepted() {
    BoundContext context =
        bind(
            RequestContext.newBuilder()
                .addRelations(
                    ContextRelationValue.newBuilder()
                        .setName("windows")
                        .setKind(ContextRelationKind.CONTEXT_RELATION_KIND_RELATION)
                        .setRowType(
                            RowType.newBuilder()
                                .addFields(Field.newBuilder().setName("id").setType(i32())))
                        .addRows(VirtualRow.newBuilder().addValues(nullLiteral()))));

    assertThat(context.relation("windows").rows()).hasSize(1);
  }

  @Test
  public void one_binding_per_name() {
    assertThatThrownBy(
            () ->
                bind(
                    RequestContext.newBuilder()
                        .addScalars(scalar("x", literal(1)))
                        .addRelations(list("x", 1))))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("bound twice");
  }

  @Test
  public void a_scalar_that_is_not_a_literal_is_refused() {
    assertThatThrownBy(
            () ->
                bind(
                    RequestContext.newBuilder()
                        .addScalars(
                            ContextScalar.newBuilder()
                                .setName("x")
                                .setValue(
                                    Expr.newBuilder()
                                        .setFieldRef(
                                            chalk.ir.v1.FieldRef.newBuilder().setIndex(0))
                                        .build()))))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("is not a literal");
  }

  // ------------------------------------------------------------------ the fold

  @Test
  public void a_scalar_folds_to_a_typed_literal() {
    BoundContext context =
        bind(
            RequestContext.newBuilder()
                .addScalars(scalar("user", literal(7)))
                .addScalars(scalar("role", literal("manager"))));

    assertThat(fold("SELECT * FROM t WHERE created_by = @ctx.user AND r = @ctx.role", context))
        .contains("\"created_by\" = 7")
        .contains("\"r\" = 'manager'");
  }

  @Test
  public void a_single_column_list_folds_to_a_literal_in_list() {
    BoundContext context = bind(RequestContext.newBuilder().addRelations(list("orgs", 1, 2, 5)));

    assertThat(fold("SELECT * FROM t WHERE org_id IN (@ctx.orgs)", context))
        .contains("\"org_id\" IN (1, 2, 5)");
  }

  @Test
  public void a_composite_list_folds_to_row_constructors() {
    BoundContext context = bind(RequestContext.newBuilder().addRelations(pairs("subjects", 7, 1, 9, 2)));

    assertThat(fold("SELECT * FROM t WHERE (member_id, org_id) IN (@ctx.subjects)", context))
        .contains("IN (ROW(7, 1), ROW(9, 2))");
  }

  /** Nothing is in the empty set, and everything is not in it — three-valued-correct either way. */
  @Test
  public void an_empty_list_folds_to_false_and_its_negation_to_true() {
    BoundContext context = bind(RequestContext.newBuilder().addRelations(list("orgs")));

    assertThat(fold("SELECT * FROM t WHERE org_id IN (@ctx.orgs)", context)).contains("WHERE FALSE");
    assertThat(fold("SELECT * FROM t WHERE org_id NOT IN (@ctx.orgs)", context))
        .contains("WHERE TRUE");
  }

  @Test
  public void a_list_above_the_ceiling_stays_a_relation() {
    int[] many = new int[10];
    for (int i = 0; i < many.length; i++) {
      many[i] = i;
    }
    BoundContext context =
        bind(RequestContext.newBuilder().setFoldMaxRows(4).addRelations(list("orgs", many)));

    String folded = fold("SELECT * FROM t WHERE org_id IN (@ctx.orgs)", context);
    assertThat(folded).contains("SELECT \"id\"").contains("FROM \"$chalk$ctx\".\"orgs\"");
    assertThat(folded).doesNotContain("(0, 1, 2");
    assertThat(context.unfolded()).hasSize(1);
  }

  @Test
  public void a_list_at_the_ceiling_is_still_folded() {
    BoundContext context =
        bind(RequestContext.newBuilder().setFoldMaxRows(3).addRelations(list("orgs", 1, 2, 3)));

    assertThat(fold("SELECT * FROM t WHERE org_id IN (@ctx.orgs)", context))
        .contains("IN (1, 2, 3)");
    assertThat(context.unfolded()).isEmpty();
  }

  @Test
  public void the_default_ceiling_is_sixty_four() {
    assertThat(BoundContext.DEFAULT_FOLD_MAX_ROWS).isEqualTo(64);
    assertThat(bind(RequestContext.newBuilder()).foldMaxRows()).isEqualTo(64);
  }

  // ------------------------------------------------------------------ the two kind refusals

  @Test
  public void a_relation_in_an_in_list_is_refused() {
    BoundContext context =
        bind(
            RequestContext.newBuilder()
                .addRelations(
                    ContextRelationValue.newBuilder()
                        .setName("windows")
                        .setKind(ContextRelationKind.CONTEXT_RELATION_KIND_RELATION)
                        .setRowType(
                            RowType.newBuilder()
                                .addFields(Field.newBuilder().setName("id").setType(i32())))
                        .addRows(VirtualRow.newBuilder().addValues(literal(1)))));

    assertThatThrownBy(() -> fold("SELECT * FROM t WHERE org_id IN (@ctx.windows)", context))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("A relation is a table");
  }

  @Test
  public void a_list_in_a_from_is_refused() {
    BoundContext context = bind(RequestContext.newBuilder().addRelations(list("orgs", 1, 2)));

    assertThatThrownBy(() -> fold("SELECT * FROM @ctx.orgs", context))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("A list is a membership set");
  }

  @Test
  public void a_list_used_as_a_value_is_refused() {
    BoundContext context = bind(RequestContext.newBuilder().addRelations(list("orgs", 1, 2)));

    assertThatThrownBy(() -> fold("SELECT * FROM t WHERE org_id = @ctx.orgs", context))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("is used as a value");
  }

  @Test
  public void a_membership_test_of_the_wrong_arity_is_refused() {
    BoundContext context = bind(RequestContext.newBuilder().addRelations(pairs("subjects", 7, 1)));

    assertThatThrownBy(() -> fold("SELECT * FROM t WHERE member_id IN (@ctx.subjects)", context))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("2 column(s) and the membership test compares 1");
  }

  // ------------------------------------------------------------------ through the planner

  /**
   * The folded shape as the optimiser sees it: a small list is the {@code SEARCH} the pushdown gate
   * already judges, not a semi-join, because the parser configuration keeps every literal list off
   * the sub-query path (D29) and the fold produces exactly that.
   */
  @Test
  public void a_folded_list_plans_as_a_search_over_the_scan() {
    BoundContext context = bind(RequestContext.newBuilder().addRelations(list("syms", 1, 2, 3)));
    String plan =
        planFolded("SELECT symbol FROM main.bars WHERE volume IN (@ctx.syms)", context);

    assertThat(plan).contains("SEARCH");
    assertThat(plan).doesNotContain("ChalkContextScan");
  }

  /** And a large one is the semi-join over a {@code ContextTable}, whose rows are not in the plan. */
  @Test
  public void an_unfolded_list_plans_as_a_semi_join_over_a_context_table() {
    int[] many = new int[10];
    for (int i = 0; i < many.length; i++) {
      many[i] = i;
    }
    BoundContext context =
        bind(RequestContext.newBuilder().setFoldMaxRows(4).addRelations(list("syms", many)));
    String plan = planFolded("SELECT symbol FROM main.bars WHERE volume IN (@ctx.syms)", context);

    assertThat(plan).contains("ChalkContextScan");
    assertThat(plan).contains("context=[syms]");
  }

  /** An empty context installs no schema and leaves the plan exactly as it was. */
  @Test
  public void an_empty_context_changes_nothing() {
    assertThat(BoundContext.EMPTY.isEmpty()).isTrue();
    assertThat(chalk.planner.entitlement.ContextSchema.of(BoundContext.EMPTY)).isNull();

    // A context that binds a value nothing refers to still installs no schema and folds nothing, so
    // the plan is the one the same statement gets with no context at all. Rel ids are per planner
    // instance and are not part of a plan's identity.
    String withNothing = planFolded("SELECT symbol FROM main.bars", BoundContext.EMPTY);
    String withUnused =
        planFolded(
            "SELECT symbol FROM main.bars",
            bind(RequestContext.newBuilder().addScalars(scalar("user", literal(7)))));
    assertThat(withoutIds(withUnused)).isEqualTo(withoutIds(withNothing));
  }

  private static String withoutIds(String plan) {
    return plan.replaceAll("id = \\d+", "id = n");
  }

  /**
   * Folds the statement, unparses it and plans the result — which is what the enforcement pass will
   * do to a descriptor, one step at a time and with each step visible.
   */
  private static String planFolded(String sql, BoundContext context) {
    RegisteredCatalog catalog = new CatalogRegistry().register(TestCatalogs.declared());
    SqlParser.Config parserConfig = SqlConfigs.parser(SqlConfigs.DEFAULT_CONFORMANCE);
    try {
      String rewritten = ContextSql.rewrite(sql, context);
      SqlNode folded =
          ContextFold.fold(
                  SqlParser.create(rewritten, parserConfig).parseQuery(),
                  context,
                  parserConfig,
                  typeFactory())
              .node();
      try (PlannerPipeline pipeline =
          PlannerPipeline.create(
              catalog,
              PushdownPolicy.none(),
              SqlConfigs.DEFAULT_CONFORMANCE,
              com.google.common.collect.ImmutableList.of(),
              chalk.planner.plan.JoinPolicy.DEFAULT,
              context)) {
        return pipeline.plan(unparse(folded), true).physicalPlanText();
      }
    } catch (Exception failure) {
      throw new AssertionError("planning " + sql, failure);
    }
  }
}
