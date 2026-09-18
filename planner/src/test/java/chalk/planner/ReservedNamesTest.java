package chalk.planner;

import static chalk.planner.TestCatalogs.column;
import static chalk.planner.TestCatalogs.type;
import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.ClientBody;
import chalk.ir.v1.ColumnEntitlement;
import chalk.ir.v1.Disclosure;
import chalk.ir.v1.DisclosureRule;
import chalk.ir.v1.FunctionDescriptor;
import chalk.ir.v1.FunctionKind;
import chalk.ir.v1.Parameter;
import chalk.ir.v1.QueryLanguage;
import chalk.ir.v1.RowCountKind;
import chalk.ir.v1.Schema;
import chalk.ir.v1.SourceCapabilities;
import chalk.ir.v1.SourceKind;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableEntitlement;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.UniqueKey;
import chalk.ir.v1.Volatility;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.InvalidCatalogException;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.PolicyOptions;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.RequestContext;
import java.util.List;
import org.junit.jupiter.api.Test;

/**
 * The names the planner keeps for itself (docs/design/16-entitlements.md §2, D223).
 *
 * <p>Two halves. The markers are unwritable, so a host may use the words the planner used to use —
 * a function called {@code CTX}, a schema called {@code ctx} — and nothing about the fold changes.
 * And a host that reaches for the reserved spelling <em>quoted</em>, which is the one way the
 * grammar would let it, is refused before any rewrite reads the text.
 */
class ReservedNamesTest {
  // ------------------------------------------------------------------ the collisions that were

  /**
   * A host function named {@code CTX}, called from a descriptor's own mask, where the fold used to
   * eat it: {@code isCtxCall} matched any single-argument call of that name, so the mask became a
   * context scalar that names nothing and the registration failed — or, worse for a host that had
   * bound a scalar of the same name, quietly became that scalar's value.
   */
  @Test
  void a_host_function_named_ctx_is_left_alone_by_the_fold() throws Exception {
    RegisteredCatalog catalog = new CatalogRegistry().register(catalog());
    try (PlannerPipeline pipeline = pipeline(catalog)) {
      String plan = pipeline.plan("SELECT first_name FROM main.members", true).logicalPlanText();

      // The mask survived as the host's own function over the raw column …
      assertThat(plan).contains("ctx($1)");
      // … and the descriptor's `@ctx.agent_orgs` still folded to this principal's one organisation,
      // which is what says the marker and the host function are two different things now.
      assertThat(plan).contains("=($0, 1)");
    }
  }

  /** A host schema named {@code ctx}: the context's own relations live in {@code $chalk$ctx}. */
  @Test
  void a_host_schema_named_ctx_resolves_to_the_hosts_tables() throws Exception {
    RegisteredCatalog catalog = new CatalogRegistry().register(catalog());
    try (PlannerPipeline pipeline = pipeline(catalog)) {
      String plan = pipeline.plan("SELECT note FROM ctx.notes", true).logicalPlanText();
      assertThat(plan).contains("[ctx, notes]").doesNotContain("$chalk$ctx");
    }
  }

  /**
   * And the two together, in one statement: the host's schema in the FROM, the host's function in
   * the select list, and an entitled table whose descriptor uses the real marker.
   */
  @Test
  void both_at_once_leave_the_entitled_leaf_exactly_as_it_was() throws Exception {
    RegisteredCatalog catalog = new CatalogRegistry().register(catalog());
    try (PlannerPipeline pipeline = pipeline(catalog)) {
      String plan =
          pipeline
              .plan(
                  "SELECT CTX(n.note), m.first_name FROM ctx.notes n, main.members m", true)
              .logicalPlanText();
      assertThat(plan).contains("[ctx, notes]").contains("[main, members]");
      assertThat(plan).doesNotContain("$chalk$ctx");
    }
  }

  // ------------------------------------------------------------------ the refusals

  @Test
  void a_quoted_reserved_identifier_in_a_statement_is_refused() {
    assertThatThrownBy(
            () -> ReservedNames.check("SELECT x FROM \"$chalk$ctx\".\"orgs\"", "this statement"))
        .isInstanceOf(ReservedNames.ReservedNameException.class)
        .hasMessageContaining("the identifier \"$chalk$ctx\" in this statement begins with $chalk$")
        .hasMessageContaining("Rename it.");
  }

  /** Whatever the case, since the parser's quoted identifiers are matched case-insensitively. */
  @Test
  void the_refusal_does_not_depend_on_case() {
    assertThatThrownBy(() -> ReservedNames.check("SELECT \"$CHALK$Strict_Guard\"(1)", "this statement"))
        .isInstanceOf(ReservedNames.ReservedNameException.class)
        .hasMessageContaining("$CHALK$Strict_Guard");
  }

  /** {@code INVALID_REQUEST}: well-formed text the planner declines to plan (D223). */
  @Test
  void the_statement_refusal_is_an_invalid_request() {
    io.grpc.StatusRuntimeException status =
        chalk.planner.rpc.PlanErrors.toStatus(
            new ReservedNames.ReservedNameException("the identifier \"$chalk$ctx\" …"));
    assertThat(status.getStatus().getCode()).isEqualTo(io.grpc.Status.Code.INVALID_ARGUMENT);
    assertThat(status.getMessage()).contains("$chalk$ctx");
  }

  /** A descriptor's is {@code INVALID_CATALOG}, naming the field it is in. */
  @Test
  void a_quoted_reserved_identifier_in_a_descriptor_is_refused() {
    assertThatThrownBy(
            () ->
                new CatalogRegistry()
                    .register(catalog("org_id IN (@ctx.agent_orgs)", "\"$chalk$ctx\"(first_name)")))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("columns[0] (first_name).mask")
        .hasMessageContaining("begins with $chalk$");
  }

  /** And so is one in a row predicate, which is the field a host writes first. */
  @Test
  void a_quoted_reserved_identifier_in_a_row_predicate_is_refused() {
    assertThatThrownBy(
            () ->
                new CatalogRegistry()
                    .register(catalog("\"$chalk$ctx\".\"orgs\" IS NOT NULL", "UPPER(first_name)")))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("row_predicate")
        .hasMessageContaining("begins with $chalk$");
  }

  /** Nothing inside a string literal or a comment is a name, which is why this is a scanner. */
  @Test
  void a_reserved_spelling_inside_a_literal_or_a_comment_is_not_a_name() {
    ReservedNames.check("SELECT '\"$chalk$ctx\"' AS t FROM x", "this statement");
    ReservedNames.check("SELECT 1 -- \"$chalk$ctx\"\n FROM x", "this statement");
    ReservedNames.check("SELECT 1 /* \"$chalk$ctx\" */ FROM x", "this statement");
    ReservedNames.check("SELECT \"ctx\", \"CTX\" FROM x", "this statement");
  }

  // ------------------------------------------------------------------ the fixture

  private static PlannerPipeline pipeline(RegisteredCatalog catalog) {
    return PlannerPipeline.create(
        catalog,
        PushdownPolicy.none(),
        chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
        List.of(),
        chalk.planner.plan.JoinPolicy.DEFAULT,
        BoundContext.of(principal()),
        PolicyOptions.DEFAULTS);
  }

  /** An agent in organisation 1, which is what makes the descriptor's rule the matching one. */
  private static RequestContext principal() {
    return RequestContext.newBuilder()
        .addRelations(
            ContextRelationValue.newBuilder()
                .setName("agent_orgs")
                .setRowType(
                    chalk.ir.v1.RowType.newBuilder()
                        .addFields(
                            chalk.ir.v1.Field.newBuilder()
                                .setName("org_id")
                                .setType(type(TypeKind.TYPE_KIND_I32))))
                .addRows(
                    chalk.ir.v1.VirtualRow.newBuilder()
                        .addValues(
                            chalk.ir.v1.Expr.newBuilder()
                                .setType(type(TypeKind.TYPE_KIND_I32))
                                .setLiteral(chalk.ir.v1.Literal.newBuilder().setI32Value(1)))))
        .build();
  }

  private static CatalogContext catalog() {
    return catalog("org_id IN (@ctx.agent_orgs)", "CTX(first_name)");
  }

  /**
   * Two schemas — {@code main}, with an entitled {@code members}, and one the host called
   * {@code ctx} — and a scalar function the host called {@code CTX}. Both were reserved words to the
   * planner before D223 and neither is now.
   */
  private static CatalogContext catalog(String rowPredicate, String mask) {
    return CatalogContext.newBuilder()
        .setContextId("reserved")
        .setEpoch(1)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("main")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .setCapabilities(
                    SourceCapabilities.newBuilder()
                        .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE))
                .addFunctions(
                    FunctionDescriptor.newBuilder()
                        .setName("ctx")
                        .setKind(FunctionKind.FUNCTION_KIND_SCALAR)
                        .setVolatility(Volatility.VOLATILITY_IMMUTABLE)
                        .setReturnType(type(TypeKind.TYPE_KIND_STRING))
                        .addParameters(
                            Parameter.newBuilder()
                                .setName("s")
                                .setType(type(TypeKind.TYPE_KIND_STRING)))
                        .setClient(ClientBody.getDefaultInstance()))
                .addTables(
                    Table.newBuilder()
                        .setName("members")
                        .setRowCount(12)
                        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
                        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
                        .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
                        .addColumns(column("first_name", type(TypeKind.TYPE_KIND_STRING)))
                        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
                        .setEntitlement(
                            TableEntitlement.newBuilder()
                                .setRowPredicate(rowPredicate)
                                .setDescriptorHash("0000000000000000000000000000000a")
                                .addColumns(
                                    ColumnEntitlement.newBuilder()
                                        .setColumn(2)
                                        .setMask(mask)
                                        .addRules(
                                            DisclosureRule.newBuilder()
                                                .setWhen("org_id IN (@ctx.agent_orgs)")
                                                .setThen(Disclosure.DISCLOSURE_MASKED))
                                        .setOtherwise(Disclosure.DISCLOSURE_NONE)))))
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("ctx")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .setCapabilities(
                    SourceCapabilities.newBuilder()
                        .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE))
                .addTables(
                    Table.newBuilder()
                        .setName("notes")
                        .setRowCount(6)
                        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
                        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
                        .addColumns(column("note", type(TypeKind.TYPE_KIND_STRING)))
                        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))))
        .build();
  }
}
