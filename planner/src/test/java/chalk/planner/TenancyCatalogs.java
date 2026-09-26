package chalk.planner;

import static chalk.planner.TestCatalogs.column;
import static chalk.planner.TestCatalogs.nullable;
import static chalk.planner.TestCatalogs.type;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.ColumnEntitlement;
import chalk.ir.v1.Disclosure;
import chalk.ir.v1.Enforcement;
import chalk.ir.v1.DisclosureRule;
import chalk.ir.v1.Expr;
import chalk.ir.v1.Field;
import chalk.ir.v1.Literal;
import chalk.ir.v1.QueryLanguage;
import chalk.ir.v1.RowCountKind;
import chalk.ir.v1.RowType;
import chalk.ir.v1.Schema;
import chalk.ir.v1.SourceCapabilities;
import chalk.ir.v1.SourceKind;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableEntitlement;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.UniqueKey;
import chalk.ir.v1.VirtualRow;
import chalk.planner.rpc.v1.ContextRelationKind;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.ContextScalar;
import chalk.planner.rpc.v1.RequestContext;

/**
 * The {@code tenancy} fixture as the Java tests need it — the entitlements written directly as
 * {@code TableEntitlement}, which is §8's "layer A alone" (docs/design/16-entitlements.md §8).
 *
 * <p>It mirrors {@code Chalk.TestKit.TenancyFixture} on the client side: same tables, same columns,
 * same descriptor text, same principals. What it is not is a copy of the corpus catalog — the plans
 * here are asserted by shape, not by golden text, so it carries no statistics.
 */
public final class TenancyCatalogs {
  private TenancyCatalogs() {}

  public static final String CONTEXT_ID = "tenancy";
  public static final long EPOCH = 1L;

  /** The floor {@code orders.amount} declares in the ordinary fixture: 2 or more, so it guards. */
  public static final int AMOUNT_FLOOR = 3;

  /** Two tables and one reference table, which is the smallest catalog the leaf rewrite needs. */
  public static CatalogContext catalog() {
    return catalog(AMOUNT_FLOOR);
  }

  /**
   * The same catalog with {@code orders.amount} declaring {@code floor} (D211): {@code 0} to inherit
   * whatever the host gives at planning, {@code 1} to disable the guard for the column whatever that
   * default says, {@code 2} or more to name the floor.
   */
  public static CatalogContext catalog(int floor) {
    return CatalogContext.newBuilder()
        .setContextId(CONTEXT_ID)
        .setEpoch(EPOCH)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("main")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .setCapabilities(
                    SourceCapabilities.newBuilder()
                        .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE)
                        .build())
                .addTables(orgs())
                .addTables(members())
                .addTables(orders(floor))
                .addTables(threads())
                .addTables(messages())
                .addTables(attachments())
                .addTables(symbols()))
        .build();
  }

  /**
   * The same fixture with {@code members} and {@code orders} on a <b>remote</b> source that speaks
   * SQL — §8's query 16 — so the row predicate has somewhere to be pushed to
   * (docs/design/16-entitlements.md §3.7, §3.8).
   *
   * @param enforcement what the two entitled tables declare: {@code PUSHDOWN} (the default),
   *     {@code LOCAL} (no predicate reaches the source at all) or {@code PUSHDOWN_REQUIRED}
   * @param capabilities what the source declares it can take; a profile without
   *     {@code PREDICATE_SHAPE_IN} is what makes {@code PUSHDOWN_REQUIRED} bite
   * @param pushMasks the table's half of D153's mask opt-in
   */
  public static CatalogContext remote(
      Enforcement enforcement, SourceCapabilities capabilities, boolean pushMasks) {
    Table remoteMembers = withEnforcement(members(), enforcement, pushMasks);
    Table remoteOrders = withEnforcement(orders(), enforcement, pushMasks);
    return CatalogContext.newBuilder()
        .setContextId(CONTEXT_ID)
        .setEpoch(EPOCH)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("main")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .setCapabilities(
                    SourceCapabilities.newBuilder()
                        .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE)
                        .build())
                .addTables(orgs())
                .addTables(symbols())
                // A client-bodied predicate, which is what §8's query 16 puts beside the tenancy
                // conjunct: it belongs to no source and can never be pushed, so a mixed filter has
                // to split or the whole table comes back.
                .addFunctions(
                    chalk.ir.v1.FunctionDescriptor.newBuilder()
                        .setName("is_vip")
                        .setKind(chalk.ir.v1.FunctionKind.FUNCTION_KIND_SCALAR)
                        .setVolatility(chalk.ir.v1.Volatility.VOLATILITY_IMMUTABLE)
                        .setReturnType(type(TypeKind.TYPE_KIND_BOOL))
                        .addParameters(
                            chalk.ir.v1.Parameter.newBuilder()
                                .setName("id")
                                .setType(type(TypeKind.TYPE_KIND_I32)))
                        .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())))
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("pg")
                .setName("remote")
                .setKind(SourceKind.SOURCE_KIND_REMOTE)
                .setDialect(TestCatalogs.duckDbProfile().getDialect())
                .setCapabilities(capabilities)
                .setDialectProfile(TestCatalogs.duckDbProfile())
                .addTables(remoteMembers)
                .addTables(remoteOrders))
        .build();
  }

  /**
   * The same remote fixture with the source's own row security trusted (D156): the pass skips
   * {@code Filter_R} for its tables and nothing else — the disclosures are still Chalk's.
   */
  public static CatalogContext trustedRemote() {
    CatalogContext catalog = remote();
    return catalog.toBuilder()
        .setSchemas(
            1, catalog.getSchemas(1).toBuilder().setTrustSourceRowLevelSecurity(true).build())
        .build();
  }

  /** The default remote fixture: a source that takes every shape, and ordinary {@code PUSHDOWN}. */
  public static CatalogContext remote() {
    return remote(
        Enforcement.ENFORCEMENT_PUSHDOWN, TestCatalogs.fullSqlCapabilities().build(), false);
  }

  /**
   * The same remote fixture with {@code members} entitled by a subject <b>pair</b> —
   * {@code (id, org_id) IN (@ctx.subject_pairs)}, which is what the tenancy package writes for a
   * grant confined to one organisation and what decorrelates into a <em>two-key</em> semi-join
   * (F50).
   *
   * @param capabilities what the source declares; a descriptor without
   *     {@code supports_row_value_in_list} is what leaves a composite list local
   */
  public static CatalogContext remotePairs(SourceCapabilities capabilities) {
    CatalogContext catalog =
        remote(Enforcement.ENFORCEMENT_PUSHDOWN, capabilities, false);
    Schema source = catalog.getSchemas(1);
    Table members = source.getTables(0);
    Table entitled =
        members.toBuilder()
            .setEntitlement(
                members.getEntitlement().toBuilder()
                    .setRowPredicate("(id, org_id) IN (@ctx.subject_pairs)"))
            .build();
    return catalog.toBuilder()
        .setSchemas(1, source.toBuilder().setTables(0, entitled))
        .build();
  }

  /**
   * The remote fixture with the <b>global escape</b> on both entitled tables (§5), the shape
   * {@code threads} already has: the grant that reaches every organisation folds the row predicate
   * to TRUE, which leaves the entitled leaf a bare scan and is what a statement's own sub-query over
   * a second occurrence of the table then has to survive (F72, F83).
   */
  public static CatalogContext remoteWithGlobalEscape() {
    CatalogContext catalog = remote();
    Schema.Builder source = catalog.getSchemas(1).toBuilder();
    for (int table = 0; table < source.getTablesCount(); table++) {
      Table entitled = source.getTables(table);
      source.setTables(
          table,
          entitled.toBuilder()
              .setEntitlement(
                  entitled.getEntitlement().toBuilder()
                      .setRowPredicate(
                          entitled.getEntitlement().getRowPredicate() + " OR @ctx.global")));
    }
    return catalog.toBuilder().setSchemas(1, source).build();
  }

  /**
   * The remote fixture with a {@code through} that <b>crosses a source boundary</b> (§3.13, D229,
   * F52): {@code messages} on the remote source and its parent {@code threads} in process, so the
   * join the pass compiles cannot go to one source and the parent's visible keys have to travel
   * instead.
   *
   * <p>The child's declared foreign key goes with the move: a foreign key names a table of the same
   * schema, and the catalog refuses one that does not. That is also why a cross-source parent is
   * never elided — "every child row has a visible parent" rests on the declaration (V152), and there
   * is none to rest on.
   *
   * @param enforcement what {@code messages} declares: {@code PUSHDOWN}, or
   *     {@code PUSHDOWN_REQUIRED} to make the exchange compulsory
   * @param capabilities what the child's source declares; one without {@code PREDICATE_SHAPE_IN} is
   *     what leaves the local join in place
   */
  public static CatalogContext throughAcrossSources(
      Enforcement enforcement, SourceCapabilities capabilities) {
    Table messages = messages();
    Table child =
        withEnforcement(
            messages.toBuilder()
                .clearForeignKeys()
                .setEntitlement(
                    messages.getEntitlement().toBuilder()
                        .setThrough(
                            0, messages.getEntitlement().getThrough(0).toBuilder()
                                .setParentSchema("main")))
                .build(),
            enforcement,
            false);
    CatalogContext base = remote(Enforcement.ENFORCEMENT_PUSHDOWN, capabilities, false);
    return base.toBuilder()
        .setSchemas(0, base.getSchemas(0).toBuilder().addTables(threads()))
        .setSchemas(1, base.getSchemas(1).toBuilder().addTables(child))
        .build();
  }

  /** The same two tables in one source, which is what the cross-source plan is compared against. */
  public static CatalogContext throughInOneSource(Enforcement enforcement) {
    CatalogContext base = remote();
    return base.toBuilder()
        .setSchemas(
            1,
            base.getSchemas(1).toBuilder()
                .addTables(threads())
                .addTables(withEnforcement(messages(), enforcement, false)))
        .build();
  }

  /** A source that takes a row-constructor IN list, as the DuckDB and PostgreSQL profiles do. */
  public static SourceCapabilities withRowValueInLists() {
    return TestCatalogs.fullSqlCapabilities().setSupportsRowValueInList(true).build();
  }

  /** A source that declares everything but the IN shape — what {@code PUSHDOWN_REQUIRED} meets. */
  public static SourceCapabilities withoutInLists() {
    SourceCapabilities full = TestCatalogs.fullSqlCapabilities().build();
    // The ceiling goes with the shape: a catalog that declares a list size and refuses IN lists is
    // refused at registration, and rightly.
    SourceCapabilities.Builder builder =
        full.toBuilder().clearPushablePredicates().setMaxInList(0);
    for (chalk.ir.v1.PredicateShape shape : full.getPushablePredicatesList()) {
      if (shape != chalk.ir.v1.PredicateShape.PREDICATE_SHAPE_IN) {
        builder.addPushablePredicates(shape);
      }
    }
    return builder.build();
  }

  /**
   * The same source, willing to be handed a mask (D153's other half) and able to render the one
   * this fixture uses — a capability a source has to declare like any other, since a mask it cannot
   * spell is no more pushable than any other expression it cannot spell.
   */
  public static SourceCapabilities takingMasks() {
    return TestCatalogs.fullSqlCapabilities()
        .setSupportsMaskPushdown(true)
        .addPushableFunctions(chalk.ir.v1.FunctionId.FUNCTION_ID_SUBSTRING)
        .build();
  }

  private static Table withEnforcement(Table table, Enforcement enforcement, boolean pushMasks) {
    return table.toBuilder()
        .setEntitlement(
            table.getEntitlement().toBuilder()
                .setEnforcement(enforcement)
                .setPushMasks(pushMasks))
        .build();
  }

  /** Reference data with no entitlement at all: the unrestricted half of every zero-cost check. */
  public static Table symbols() {
    return Table.newBuilder()
        .setName("symbols")
        .setRowCount(5)
        .addColumns(column("symbol", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("base", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  /** Organisations: the tenancy container, itself unentitled. */
  public static Table orgs() {
    return Table.newBuilder()
        .setName("orgs")
        .setRowCount(3)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("name", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  /**
   * Members, with the row predicate on {@code org_id} and three protected columns: {@code
   * first_name} full for a manager and initial-masked for an agent, {@code last_name} the same, and
   * {@code national_id} — NOT NULL in the catalog — never disclosed at all, which is what makes the
   * placeholder widen a type and the retyper earn its place.
   */
  public static Table members() {
    return Table.newBuilder()
        .setName("members")
        .setRowCount(12)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("first_name", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("last_name", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("national_id", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("postcode", nullable(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate("org_id IN (@ctx.manager_orgs) OR org_id IN (@ctx.agent_orgs)")
                .setDescriptorHash("0123456789abcdef0123456789abcdef")
                .addColumns(masked(2, "SUBSTRING(first_name, 1, 1)"))
                .addColumns(masked(3, "SUBSTRING(last_name, 1, 1)"))
                .addColumns(
                    ColumnEntitlement.newBuilder()
                        .setColumn(4)
                        .setOtherwise(Disclosure.DISCLOSURE_NONE))
                .build())
        .build();
  }

  /** {@code FULL} inside the manager's organisations, {@code MASKED} everywhere else (D196). */
  private static ColumnEntitlement masked(int column, String mask) {
    return ColumnEntitlement.newBuilder()
        .setColumn(column)
        .setMask(mask)
        .addRules(
            DisclosureRule.newBuilder()
                .setWhen("org_id IN (@ctx.manager_orgs)")
                .setThen(Disclosure.DISCLOSURE_FULL))
        .addRules(
            DisclosureRule.newBuilder()
                .setWhen("org_id IN (@ctx.agent_orgs)")
                .setThen(Disclosure.DISCLOSURE_MASKED))
        .setOtherwise(Disclosure.DISCLOSURE_NONE)
        .build();
  }

  /** Orders, whose {@code amount} is population-only for an auditor (D190, §3.4). */
  public static Table orders() {
    return orders(AMOUNT_FLOOR);
  }

  /** The same, with {@code amount}'s group-size floor named (D211). */
  public static Table orders(int floor) {
    return Table.newBuilder()
        .setName("orders")
        .setRowCount(40)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("member_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("amount", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("note", nullable(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate(
                    "org_id IN (@ctx.manager_orgs) OR org_id IN (@ctx.agent_orgs)"
                        + " OR org_id IN (@ctx.auditor_orgs)")
                .setDescriptorHash("fedcba9876543210fedcba9876543210")
                .addColumns(
                    ColumnEntitlement.newBuilder()
                        .setColumn(3)
                        .addAggregateOnlyFunctions("SUM")
                        .addAggregateOnlyFunctions("SUM0")
                        .addAggregateOnlyFunctions("COUNT")
                        .addAggregateOnlyFunctions("AVG")
                        .setMinGroupSize(floor)
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen("org_id IN (@ctx.manager_orgs)")
                                .setThen(Disclosure.DISCLOSURE_FULL))
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen("org_id IN (@ctx.auditor_orgs)")
                                .setThen(Disclosure.DISCLOSURE_AGGREGATE_ONLY))
                        .setOtherwise(Disclosure.DISCLOSURE_NONE))
                .addColumns(masked(4, "'********'"))
                .build())
        .build();
  }

  // ------------------------------------------------------------------ through a parent (§3.13)

  /**
   * Threads: the parent of {@code messages}, restricted by the tenancy as any table is, and with the
   * global escape a grant that reaches every organisation binds — which is what makes the child's
   * join elidable (D226).
   */
  public static Table threads() {
    return Table.newBuilder()
        .setName("threads")
        .setRowCount(20)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("org_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("member_id", type(TypeKind.TYPE_KIND_I32)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setRowPredicate(
                    "org_id IN (@ctx.manager_orgs) OR org_id IN (@ctx.agent_orgs) OR @ctx.global")
                .setDescriptorHash("aaaabbbbccccddddaaaabbbbccccdddd")
                .build())
        .build();
  }

  /**
   * Messages: no tenancy column of its own, entitled <em>through</em> its thread (§3.13). Its
   * {@code content} is disclosed by the roles held in the thread's organisation, which is the rule
   * list D228 turns into one verdict column on the parent's side.
   */
  public static Table messages() {
    return Table.newBuilder()
        .setName("messages")
        .setRowCount(200)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("thread_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("content", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("first_viewed_at", nullable(TypeKind.TYPE_KIND_TIMESTAMP)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addForeignKeys(
            chalk.ir.v1.ForeignKey.newBuilder()
                .setName("messages_thread")
                .addColumns(1)
                .setParentTable("threads")
                .addParentColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setDescriptorHash("1111222233334444111122223333444")
                .addThrough(
                    chalk.ir.v1.ParentVisibility.newBuilder()
                        .setColumn(1)
                        .setParentTable("threads")
                        .setParentColumn(0))
                .addColumns(
                    ColumnEntitlement.newBuilder()
                        .setColumn(2)
                        .setMask("SUBSTRING(content, 1, 8)")
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen(
                                    "threads.org_id IN (@ctx.manager_orgs) OR @ctx.global")
                                .setThen(Disclosure.DISCLOSURE_FULL))
                        .addRules(
                            DisclosureRule.newBuilder()
                                .setWhen("threads.org_id IN (@ctx.agent_orgs)")
                                .setThen(Disclosure.DISCLOSURE_MASKED))
                        .setOtherwise(Disclosure.DISCLOSURE_NONE))
                .build())
        .build();
  }

  /** Attachments: two hops from a tenancy column, which is the chain D226 says recurses. */
  public static Table attachments() {
    return Table.newBuilder()
        .setName("attachments")
        .setRowCount(50)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("message_id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("name", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addForeignKeys(
            chalk.ir.v1.ForeignKey.newBuilder()
                .setName("attachments_message")
                .addColumns(1)
                .setParentTable("messages")
                .addParentColumns(0))
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .setEntitlement(
            TableEntitlement.newBuilder()
                .setDescriptorHash("5555666677778888555566667777888")
                .addThrough(
                    chalk.ir.v1.ParentVisibility.newBuilder()
                        .setColumn(1)
                        .setParentTable("messages")
                        .setParentColumn(0))
                .build())
        .build();
  }

  // ------------------------------------------------------------------ the principals

  /** A manager in {@code orgs} and an agent in none: everything they can see, they see in full. */
  public static RequestContext manager(int... orgs) {
    return principal(orgs, new int[0], new int[0]);
  }

  /** An agent in {@code orgs}: rows visible, protected columns masked. */
  public static RequestContext agent(int... orgs) {
    return principal(new int[0], orgs, new int[0]);
  }

  /** An auditor in {@code orgs}: {@code amount} population-only, {@code note} masked. */
  public static RequestContext auditor(int... orgs) {
    return principal(new int[0], new int[0], orgs);
  }

  /**
   * A principal holding only confined subject grants, bound as {@code (subject, within)} pairs
   * (F50). Every other list is empty, so a predicate that ORs the tenancy memberships beside the
   * pair membership folds down to the pair one.
   */
  public static RequestContext subjects(int[][] pairs) {
    return RequestContext.newBuilder()
        .addScalars(ContextScalar.newBuilder().setName("user").setValue(i32(1)))
        .addScalars(ContextScalar.newBuilder().setName("global").setValue(bool(false)))
        .addRelations(orgList("manager_orgs", new int[0]))
        .addRelations(orgList("agent_orgs", new int[0]))
        .addRelations(orgList("auditor_orgs", new int[0]))
        .addRelations(pairList("subject_pairs", pairs))
        .build();
  }

  /** A principal with no grant at all: the row predicate folds to FALSE. */
  public static RequestContext nobody() {
    return principal(new int[0], new int[0], new int[0]);
  }

  /** A manager in one organisation and an agent in another — the mixed-rights principal of §8. */
  public static RequestContext managerAndAgent(int managerOrg, int agentOrg) {
    return principal(new int[] {managerOrg}, new int[] {agentOrg}, new int[0]);
  }

  /** A manager in one set of organisations and an agent in another — two lists, two memberships. */
  public static RequestContext managerAndAgentLists(int[] managerOrgs, int[] agentOrgs) {
    return principal(managerOrgs, agentOrgs, new int[0]);
  }

  /** A manager in one organisation and an auditor in another — D198's strict case. */
  public static RequestContext managerAndAuditor(int managerOrg, int auditorOrg) {
    return principal(new int[] {managerOrg}, new int[0], new int[] {auditorOrg});
  }

  /**
   * The same bindings every principal above has, as a <em>shape</em>: the names, the kinds and the
   * types, and no value at all (D209). One plan serves every principal that binds this shape.
   */
  public static RequestContext shape() {
    return RequestContext.newBuilder()
        .setShapeOnly(true)
        .addScalars(
            ContextScalar.newBuilder()
                .setName("user")
                .setValue(Expr.newBuilder().setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))))
        .addScalars(
            ContextScalar.newBuilder()
                .setName("global")
                .setValue(
                    Expr.newBuilder().setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_BOOL))))
        .addRelations(orgList("manager_orgs", new int[0]))
        .addRelations(orgList("agent_orgs", new int[0]))
        .addRelations(orgList("auditor_orgs", new int[0]))
        .build();
  }

  private static RequestContext principal(int[] managerOrgs, int[] agentOrgs, int[] auditorOrgs) {
    return principal(managerOrgs, agentOrgs, auditorOrgs, false);
  }

  /**
   * A manager in one organisation with the <em>agent</em> half left open — partial binding (D232).
   *
   * <p>One leaf then carries both spellings of the same question: {@code org_id IN (1)} as a literal
   * from the folded list, and a membership marker over a bound table for the one nothing folded.
   */
  public static RequestContext managerWithAgentShape(int managerOrg) {
    return RequestContext.newBuilder()
        .addScalars(ContextScalar.newBuilder().setName("user").setValue(i32(1)))
        .addScalars(ContextScalar.newBuilder().setName("global").setValue(bool(false)))
        .addRelations(orgList("manager_orgs", new int[] {managerOrg}))
        .addRelations(orgList("agent_orgs", new int[0]).toBuilder().clearRows().setShape(true))
        .addRelations(orgList("auditor_orgs", new int[0]))
        .build();
  }

  /**
   * A grant that reaches every organisation (§5): the one principal for whom {@code threads}' own
   * predicate folds to TRUE, which is what lets the child's join be elided (D226).
   */
  public static RequestContext global() {
    return principal(new int[0], new int[0], new int[0], true);
  }

  private static RequestContext principal(
      int[] managerOrgs, int[] agentOrgs, int[] auditorOrgs, boolean everywhere) {
    return RequestContext.newBuilder()
        .addScalars(ContextScalar.newBuilder().setName("user").setValue(i32(1)))
        .addScalars(ContextScalar.newBuilder().setName("global").setValue(bool(everywhere)))
        .addRelations(orgList("manager_orgs", managerOrgs))
        .addRelations(orgList("agent_orgs", agentOrgs))
        .addRelations(orgList("auditor_orgs", auditorOrgs))
        .build();
  }

  private static Expr bool(boolean value) {
    return Expr.newBuilder()
        .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_BOOL))
        .setLiteral(Literal.newBuilder().setBoolValue(value))
        .build();
  }

  private static ContextRelationValue orgList(String name, int[] orgs) {
    ContextRelationValue.Builder builder =
        ContextRelationValue.newBuilder()
            .setName(name)
            .setKind(ContextRelationKind.CONTEXT_RELATION_KIND_LIST)
            .setRowType(
                RowType.newBuilder()
                    .addFields(
                        Field.newBuilder()
                            .setName("id")
                            .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))));
    for (int org : orgs) {
      builder.addRows(VirtualRow.newBuilder().addValues(i32(org)));
    }
    return builder.build();
  }

  /** A bound list of {@code (subject, within)} pairs: two columns, {@code NOT NULL} in both. */
  private static ContextRelationValue pairList(String name, int[][] pairs) {
    ContextRelationValue.Builder builder =
        ContextRelationValue.newBuilder()
            .setName(name)
            .setKind(ContextRelationKind.CONTEXT_RELATION_KIND_LIST)
            .setRowType(
                RowType.newBuilder()
                    .addFields(
                        Field.newBuilder()
                            .setName("subject")
                            .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32)))
                    .addFields(
                        Field.newBuilder()
                            .setName("within")
                            .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))));
    for (int[] pair : pairs) {
      builder.addRows(VirtualRow.newBuilder().addValues(i32(pair[0])).addValues(i32(pair[1])));
    }
    return builder.build();
  }

  private static Expr i32(int value) {
    return Expr.newBuilder()
        .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32))
        .setLiteral(Literal.newBuilder().setI32Value(value))
        .build();
  }
}
