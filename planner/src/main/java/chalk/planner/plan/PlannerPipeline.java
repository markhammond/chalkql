package chalk.planner.plan;

import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.diag.RuleTrace;
import chalk.planner.plan.rel.ChalkProject;
import chalk.planner.types.ChalkTypeSystem;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.Contexts;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.plan.ConventionTraitDef;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelOptRule;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.plan.RelTraitDef;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.plan.hep.HepMatchOrder;
import org.apache.calcite.plan.hep.HepPlanner;
import org.apache.calcite.plan.hep.HepProgram;
import org.apache.calcite.plan.hep.HepProgramBuilder;
import org.apache.calcite.plan.volcano.VolcanoPlanner;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollationTraitDef;
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelRoot;
import org.apache.calcite.rel.core.Correlate;
import org.apache.calcite.rel.core.RelFactories;
import org.apache.calcite.rel.core.Sort;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.runtime.ImmutablePairList;
import org.apache.calcite.sql.SqlExplainFormat;
import org.apache.calcite.sql.SqlExplainLevel;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.fun.SqlLibrary;
import org.apache.calcite.sql.parser.SqlParseException;
import org.apache.calcite.sql.validate.SqlConformanceEnum;
import org.apache.calcite.sql.validate.SqlValidatorUtil;
import org.apache.calcite.sql2rel.RelDecorrelator;
import org.apache.calcite.tools.FrameworkConfig;
import org.apache.calcite.tools.Frameworks;
import org.apache.calcite.tools.Programs;
import org.apache.calcite.tools.RelBuilder;
import org.apache.calcite.tools.RelConversionException;
import org.apache.calcite.tools.ValidationException;
import org.apache.calcite.util.mapping.Mappings;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * parse → validate → SqlToRel → Hep pre-pass → Volcano, per docs/design/03-planner.md §4.
 *
 * <p>One instance per request. Nothing mutable is shared between requests except the catalog
 * registry, and every {@code RexBuilder} and {@code RelOptCluster} is per request, which is half of
 * what makes plans deterministic (§6).
 *
 * <p>Volcano stays behind this class rather than leaking into the service, so D6 — replacing it with
 * a heuristic optimiser — stays a drop-in change.
 */
public final class PlannerPipeline implements AutoCloseable {
  private final ChalkPlanner planner;
  private final PushdownPolicy policy;
  private final List<RelOptRule> volcanoRules;
  private final RuleTrace ruleTrace = new RuleTrace();
  private boolean ruleTraceInstalled;

  /**
   * The flag this request's optimiser reads at every rule match (D235). One per pipeline, handed to
   * Volcano through the framework context because {@code RelOptPlanner.setCancelFlag} is deprecated
   * and ignored — {@code AbstractRelOptPlanner} takes its flag from {@code Context} and from nowhere
   * else. The Hep planners this pipeline builds take no context, so each has a flag of its own and
   * none of them can be ended by this one (D238).
   */
  private final org.apache.calcite.util.CancelFlag cancelFlag;

  /**
   * What this request's parameters are expected to be worth (D284). One holder per pipeline, in the
   * framework context beside the cancel flag, empty until {@link #parameterHints} puts a request's
   * hints in it — and set again, per request, for a pipeline a narrowing re-enters.
   */
  private final ParameterHints parameterHints;

  /** This run's governor, or null for a run no option can end early (D235). */
  private chalk.planner.diag.@Nullable PlanningGovernor governor;

  /**
   * The listener that carries whichever governor this run has. One per pipeline and registered once,
   * because Calcite's listener list cannot have an entry removed and a narrowing re-enters the
   * pipeline that converted the statement (D233): registering each run's governor directly would
   * leave the first one listening and every later one deaf.
   */
  private final chalk.planner.diag.PlanningGovernor.Slot governorSlot =
      new chalk.planner.diag.PlanningGovernor.Slot();

  private boolean governorInstalled;

  /** Enough to build a second planner with decorrelation off; see {@link #decorrelationFailure}. */
  private final RegisteredCatalog catalog;

  private final SqlConformanceEnum conformance;
  private final List<SqlLibrary> libraries;
  private final JoinPolicy joinPolicy;

  /**
   * This request's execution context (step 26, 16-entitlements.md §2). {@code BoundContext.EMPTY}
   * for every request that binds none, which is every request against a catalog without
   * entitlements: an empty context installs no {@code ctx} schema and folds nothing.
   */
  private final chalk.planner.entitlement.BoundContext context;

  /**
   * This request's default schema — the catalog's, or a fresh root of it plus the per-request
   * {@code ctx} schema. This is the schema the <b>statement</b> is parsed, validated and converted
   * over, so an entitled table publishes here the row type a statement sees: a column any rule can
   * withhold or mask to NULL is nullable whatever the source stores (F58, §1, D161).
   */
  private final org.apache.calcite.schema.SchemaPlus defaultSchema;

  /**
   * The same schema with every column exactly as the catalog declares it. The entitlement pass
   * converts each descriptor through a catalog reader of its own over this one (§3.2): a mask is
   * evaluated over the leaf's <em>raw</em> row (§1), and the scan beneath the leaf reads that row —
   * so the descriptor's expressions must be typed by it and not by the disclosed row above (F58).
   */
  private final org.apache.calcite.schema.SchemaPlus declaredSchema;

  /** This request's entitlement choices (§6); the defaults when the client said nothing. */
  private final chalk.planner.entitlement.PolicyOptions policyOptions;

  /**
   * Volcano's two programs. Program 0 enumerates join orders; program 1 does not, because the Hep
   * pre-pass has already chosen one and re-exploring would be factorial (D44). Which one runs is a
   * property of the query, not of the pipeline, so both are registered and {@link #plan} picks.
   */
  private static final int PROGRAM_ENUMERATE_JOINS = 0;
  private static final int PROGRAM_FIXED_JOIN_ORDER = 1;

  private PlannerPipeline(
      ChalkPlanner planner,
      PushdownPolicy policy,
      List<RelOptRule> volcanoRules,
      RegisteredCatalog catalog,
      SqlConformanceEnum conformance,
      List<SqlLibrary> libraries,
      JoinPolicy joinPolicy,
      chalk.planner.entitlement.BoundContext context,
      org.apache.calcite.schema.SchemaPlus defaultSchema,
      org.apache.calcite.schema.SchemaPlus declaredSchema,
      chalk.planner.entitlement.PolicyOptions policyOptions,
      org.apache.calcite.util.CancelFlag cancelFlag,
      ParameterHints parameterHints) {
    this.planner = planner;
    this.policy = policy;
    this.volcanoRules = volcanoRules;
    this.catalog = catalog;
    this.conformance = conformance;
    this.libraries = libraries;
    this.joinPolicy = joinPolicy;
    this.context = context;
    this.defaultSchema = defaultSchema;
    this.declaredSchema = declaredSchema;
    this.policyOptions = policyOptions;
    this.cancelFlag = cancelFlag;
    this.parameterHints = parameterHints;
  }

  /** The outcome of a planning run, plus everything the diagnostics need. */
  public record Result(
      RelNode physical,
      RelDataType parameterRowType,
      String logicalPlanText,
      String physicalPlanText,
      List<String> rulesFired,
      List<String> stages,
      List<chalk.planner.entitlement.DisclosureMap> entitledLeaves,
      List<chalk.planner.entitlement.PolicyExplain.Table> explained,
      List<chalk.planner.rpc.v1.ReportedDisclosure> columnDisclosures,
      java.util.Set<String> pushedRowPredicates,
      List<String> requiredRelations,
      List<String> requiredScalars,
      boolean omitDegraded,
      long parseMicros,
      long validateMicros,
      long convertMicros,
      long optimizeMicros,
      PlanningState planningState) {}

  /**
   * How this run's optimiser ended, and what the governor saw while it ran (D237).
   *
   * <p>{@code governed} is false for a run no option could end early: there was no listener, no
   * sample was taken and no cost was read, so the counts and the costs are absent rather than zero
   * by coincidence.
   */
  public record PlanningState(
      chalk.planner.diag.PlanningGovernor.Termination termination,
      boolean governed,
      long evaluations,
      long looks,
      int evaluationInterval,
      long runningElapsedNanos,
      long queuedElapsedNanos,
      long slices,
      org.apache.calcite.plan.@Nullable RelOptCost firstCost,
      org.apache.calcite.plan.@Nullable RelOptCost bestCost,
      List<org.apache.calcite.plan.RelOptCost> samples) {

    /** What a run with no governor reports: it finished, and nothing was measured. */
    public static final PlanningState UNGOVERNED =
        new PlanningState(
            chalk.planner.diag.PlanningGovernor.Termination.CONVERGED,
            false,
            0L,
            0L,
            0,
            0L,
            0L,
            0L,
            null,
            null,
            ImmutableList.of());
  }

  /** The pipeline's stage names, in the order {@link #plan} runs them (step 26, §0). */
  public static final String STAGE_PARSE = "parse";

  public static final String STAGE_VALIDATE = "validate";
  public static final String STAGE_SQL_TO_REL = "sql-to-rel";
  public static final String STAGE_ENTITLEMENTS = "entitlements";
  public static final String STAGE_HEP = "hep";
  public static final String STAGE_VOLCANO = "volcano";
  public static final String STAGE_ROOT_PROJECT = "root-project";

  /**
   * What the ordinals this request hinted are named by in the stage list (D284). The ordinals only:
   * a hint's value is not diagnostics, and a caller reading a log should never find one there.
   */
  public static final String STAGE_PARAMETER_HINTS = "parameter hints";

  /**
   * What a narrowing that used the hint says in place of parse, validation and conversion (D233).
   * The base plan's digest follows it, so a reader sees which plan this one started from.
   */
  public static final String STAGE_NARROWED_FROM = "narrowed from";

  /** A pipeline for one request, in the SQL dialect that request asked for (D34). */
  public static PlannerPipeline create(
      RegisteredCatalog catalog, PushdownPolicy policy, SqlConformanceEnum conformance) {
    return create(catalog, policy, conformance, ImmutableList.of());
  }

  /**
   * A pipeline for one request, in that request's dialect (D34) and with that request's function
   * libraries (D60).
   */
  public static PlannerPipeline create(
      RegisteredCatalog catalog,
      PushdownPolicy policy,
      SqlConformanceEnum conformance,
      List<SqlLibrary> libraries) {
    return create(catalog, policy, conformance, libraries, JoinPolicy.of(catalog.joinPolicy()));
  }

  /**
   * The same, with the cross-source join policy this request will be planned under (D104): the
   * catalog's, with the request's merged over it. It reaches the pipeline rather than the rules
   * because {@link RuleSets#volcano} instantiates {@code CrossSourceJoinRule} from it, and it is
   * read again after optimisation for the local-join guardrail.
   */
  public static PlannerPipeline create(
      RegisteredCatalog catalog,
      PushdownPolicy policy,
      SqlConformanceEnum conformance,
      List<SqlLibrary> libraries,
      JoinPolicy joinPolicy) {
    return create(
        catalog,
        policy,
        conformance,
        libraries,
        joinPolicy,
        chalk.planner.entitlement.BoundContext.EMPTY);
  }

  /**
   * The same, with this request's execution context (step 26, D152). A non-empty context puts the
   * per-request {@code ctx} schema beside the catalog's own, so a descriptor's unfolded list or
   * relation resolves; an empty one changes nothing at all, which is what §0's zero-cost property
   * means at this level.
   */
  public static PlannerPipeline create(
      RegisteredCatalog catalog,
      PushdownPolicy policy,
      SqlConformanceEnum conformance,
      List<SqlLibrary> libraries,
      JoinPolicy joinPolicy,
      chalk.planner.entitlement.BoundContext context) {
    return create(
        catalog,
        policy,
        conformance,
        libraries,
        joinPolicy,
        context,
        chalk.planner.entitlement.PolicyOptions.DEFAULTS);
  }

  /**
   * The same, with whatever this request's extensions contributed (step 26c, D212). The pipeline
   * asks the slate for the contributions it knows how to use and takes the default for the rest, so
   * the RPC layer above hands this through without knowing what any extension is.
   */
  public static PlannerPipeline create(
      RegisteredCatalog catalog,
      PushdownPolicy policy,
      SqlConformanceEnum conformance,
      List<SqlLibrary> libraries,
      JoinPolicy joinPolicy,
      chalk.planner.entitlement.BoundContext context,
      chalk.planner.ext.PlanExtensionContext extensions) {
    return create(
        catalog,
        policy,
        conformance,
        libraries,
        joinPolicy,
        context,
        extensions.getOrDefault(
            chalk.planner.entitlement.PolicyOptions.class,
            chalk.planner.entitlement.PolicyOptions.DEFAULTS));
  }

  /**
   * The same, with this request's entitlement choices (step 26, §6): the star policy, what an
   * undisclosed column becomes, what a placeholder holds and the group-size floor for a column whose
   * entitlement names none. They change what the caller is handed, never what the rewrite
   * guarantees.
   */
  public static PlannerPipeline create(
      RegisteredCatalog catalog,
      PushdownPolicy policy,
      SqlConformanceEnum conformance,
      List<SqlLibrary> libraries,
      JoinPolicy joinPolicy,
      chalk.planner.entitlement.BoundContext context,
      chalk.planner.entitlement.PolicyOptions policyOptions) {
    catalog.functions().checkAgainst(libraries);
    List<RelOptRule> rules = RuleSets.volcano(policy, catalog.sourceConventions(), joinPolicy);
    org.apache.calcite.schema.SchemaPlus defaultSchema =
        defaultSchema(catalog, context, policyOptions.placeholders());
    org.apache.calcite.schema.SchemaPlus declaredSchema =
        declaredSchema(catalog, context, policyOptions.placeholders(), defaultSchema);
    org.apache.calcite.util.CancelFlag cancelFlag =
        new org.apache.calcite.util.CancelFlag(new java.util.concurrent.atomic.AtomicBoolean());
    ParameterHints parameterHints = new ParameterHints();
    return new PlannerPipeline(
        // Chalk's own driver rather than Frameworks.getPlanner: Calcite's PlannerImpl, less the
        // structured-type flattener, which would split a composite-valued call into a call per field
        // (D292, ChalkPlanner).
        new ChalkPlanner(
            config(
                catalog,
                policy,
                conformance,
                libraries,
                true,
                joinPolicy,
                defaultSchema,
                cancelFlag,
                parameterHints)),
        policy,
        rules,
        catalog,
        conformance,
        ImmutableList.copyOf(libraries),
        joinPolicy,
        context,
        defaultSchema,
        declaredSchema,
        policyOptions,
        cancelFlag,
        parameterHints);
  }

  /**
   * The schema this request's <b>statement</b> is written over, with the per-request {@code ctx}
   * schema beside it when bound: the catalog's, with an entitled table's withholdable columns
   * nullable under this request's placeholder policy (F58).
   */
  private static org.apache.calcite.schema.SchemaPlus defaultSchema(
      RegisteredCatalog catalog,
      chalk.planner.entitlement.BoundContext context,
      chalk.planner.rpc.v1.PlaceholderPolicy placeholders) {
    chalk.planner.entitlement.ContextSchema contextSchema =
        chalk.planner.entitlement.ContextSchema.of(context);
    return contextSchema == null
        ? catalog.defaultSchema(placeholders)
        : catalog.defaultSchemaWith(
            placeholders, chalk.planner.entitlement.BoundContext.SCHEMA, contextSchema);
  }

  /**
   * The same over the <b>declared</b> catalog, which is what the entitlement pass converts each
   * descriptor through (§3.2, F58). The very same object as {@link #defaultSchema} whenever the
   * catalog widens nothing, so a catalog without an entitlement builds one root and not two.
   */
  private static org.apache.calcite.schema.SchemaPlus declaredSchema(
      RegisteredCatalog catalog,
      chalk.planner.entitlement.BoundContext context,
      chalk.planner.rpc.v1.PlaceholderPolicy placeholders,
      org.apache.calcite.schema.SchemaPlus disclosed) {
    if (catalog.defaultSchema(placeholders) == catalog.defaultSchema()) {
      // The catalog widens nothing under this policy, so the two trees are the same tree — and the
      // root just built for the context is the one both halves resolve through.
      return disclosed;
    }
    chalk.planner.entitlement.ContextSchema contextSchema =
        chalk.planner.entitlement.ContextSchema.of(context);
    return contextSchema == null
        ? catalog.defaultSchema()
        : catalog.defaultSchemaWith(chalk.planner.entitlement.BoundContext.SCHEMA, contextSchema);
  }

  /** The same, in the default dialect. */
  public static PlannerPipeline create(RegisteredCatalog catalog, PushdownPolicy policy) {
    return create(catalog, policy, SqlConfigs.DEFAULT_CONFORMANCE);
  }

  private static FrameworkConfig config(
      RegisteredCatalog catalog,
      PushdownPolicy policy,
      SqlConformanceEnum conformance,
      List<SqlLibrary> libraries,
      boolean decorrelate) {
    return config(
        catalog,
        policy,
        conformance,
        libraries,
        decorrelate,
        JoinPolicy.DEFAULT,
        catalog.defaultSchema(),
        new org.apache.calcite.util.CancelFlag(new java.util.concurrent.atomic.AtomicBoolean()),
        new ParameterHints());
  }

  private static FrameworkConfig config(
      RegisteredCatalog catalog,
      PushdownPolicy policy,
      SqlConformanceEnum conformance,
      List<SqlLibrary> libraries,
      boolean decorrelate,
      JoinPolicy joinPolicy,
      org.apache.calcite.schema.SchemaPlus defaultSchema,
      org.apache.calcite.util.CancelFlag cancelFlag,
      ParameterHints parameterHints) {
    return Frameworks.newConfigBuilder()
        // The cancel flag beside the connection config: this is the only way in, because
        // AbstractRelOptPlanner reads its flag from the Context at construction and ignores
        // setCancelFlag entirely (D235). The hint holder travels the same way and for the same
        // reason: the context is the one thing every rel's cluster can reach, and putting the hints
        // anywhere a rule could see them would be the firewall undone (D284, ADR 0074).
        .context(Contexts.of(SqlConfigs.connectionConfig(), cancelFlag, parameterHints))
        .parserConfig(SqlConfigs.parser(conformance))
        .sqlValidatorConfig(SqlConfigs.validator(conformance))
        .sqlToRelConverterConfig(SqlConfigs.sqlToRel().withDecorrelationEnabled(decorrelate))
        // The standard convertlets, plus the one that turns an inlined strict body's guard into a
        // CASE over the arguments that can actually be null (D78). Nothing else in the pipeline
        // needs a convertlet of its own, and no other path converts a statement the inliner
        // rewrote: the entitlement descriptor converter has its own, standard, table.
        .convertletTable(chalk.planner.ir.SqlBodyInliner.CONVERTLETS)
        .defaultSchema(defaultSchema)
        .typeSystem(ChalkTypeSystem.INSTANCE)
        // The standard operators plus Chalk's own (D52): TIME_BUCKET, which is both a function a
        // host may call directly and what the TUMBLE table function is rewritten into; plus the
        // dialect libraries this request asked for (D60), and last the functions this catalog
        // declares (D77).
        .operatorTable(ChalkOperatorTable.instance(libraries, catalog.functions()))
        .traitDefs(traitDefs())
        // Without an executor, ReduceExpressionsRule folds nothing (V10). The wrapper is what keeps
        // a client-bodied user function out of the folder: Calcite would try to compile one and
        // fail, and there is nothing in the planner that could evaluate it anyway (V32).
        .executor(new ChalkRexExecutor(RexUtil.EXECUTOR))
        .programs(
            Programs.of(
                org.apache.calcite.tools.RuleSets.ofList(
                    RuleSets.volcano(policy, catalog.sourceConventions(), joinPolicy))),
            Programs.of(
                org.apache.calcite.tools.RuleSets.ofList(
                    RuleSets.volcano(
                        policy,
                        /* enumerateJoinOrders= */ false,
                        catalog.sourceConventions(),
                        joinPolicy))))
        .build();
  }

  public PushdownPolicy policy() {
    return policy;
  }

  /**
   * Whether a governor has ever been registered on this pipeline's optimiser (D235). False for every
   * request whose options cannot end a search early, which is what the zero-cost assertion reads.
   */
  public boolean governorInstalled() {
    return governorInstalled;
  }

  /** This request's execution context (step 26); {@code BoundContext.EMPTY} when none was bound. */
  public chalk.planner.entitlement.BoundContext boundContext() {
    return context;
  }

  /**
   * What this request's parameters are expected to be worth, for the whole of this request (D284).
   *
   * <p>Called once per request, between the front half and the pass, because a hint is checked
   * against the type the <em>statement</em> inferred for its parameter and there is no such type
   * until the statement has validated. A hint the statement cannot be about is refused here, naming
   * the parameter and the two types and never the value.
   *
   * <p>A narrowing re-enters a pipeline that planned another request, so this is also where the
   * previous request's hints stop applying. The cluster's metadata query is dropped first: an
   * estimate computed under one request's hints is cached on that cluster, and answering the next
   * request out of it would make a hint reach a request that never sent one. The retained front half
   * — the validated statement and the converted tree — reads no hint at all, which is why it is safe
   * to re-enter under any hints, and is the firewall doing double duty (ADR 0074).
   */
  public void parameterHints(
      List<chalk.planner.rpc.v1.ParameterHint> hints, Front front) {
    RelOptCluster cluster = front.root().rel.getCluster();
    cluster.invalidateMetadataQuery();
    parameterHints.set(
        ParameterHintCheck.resolve(hints, irParameterTypes(front.parameterRowType(), cluster)));
  }

  /** The statement's inferred parameter types, as the IR spells them, by ordinal. */
  private static List<chalk.ir.v1.Type> irParameterTypes(
      RelDataType parameterRowType, RelOptCluster cluster) {
    chalk.planner.types.TypeMapper types =
        new chalk.planner.types.TypeMapper(cluster.getTypeFactory());
    List<chalk.ir.v1.Type> irTypes = new ArrayList<>(parameterRowType.getFieldCount());
    for (org.apache.calcite.rel.type.RelDataTypeField field : parameterRowType.getFieldList()) {
      irTypes.add(types.toIr(field.getType()));
    }

    return irTypes;
  }

  /** The parser configuration this request plans under, which the context fold parses with. */
  public org.apache.calcite.sql.parser.SqlParser.Config parserConfig() {
    return SqlConfigs.parser(conformance);
  }

  public List<RelOptRule> volcanoRules() {
    return volcanoRules;
  }

  /**
   * The source a schema's tables belong to, or null for a schema this catalog does not have: what a
   * pair rule of the join policy names, for a refusal that has to name one (F139).
   */
  private @Nullable String sourceOfSchema(String schemaName) {
    for (chalk.ir.v1.Schema schema : catalog.descriptor().getSchemasList()) {
      if (schema.getName().equalsIgnoreCase(schemaName)) {
        return schema.getSourceId();
      }
    }
    return null;
  }

  /**
   * The gate this request decides pushdown with for one source, or null for a source this catalog
   * does not have — which is what lets a refusal name the shape the host declared and did not (F46).
   */
  private @Nullable PushdownGate gateFor(String sourceId) {
    for (SourceConvention convention : catalog.sourceConventions()) {
      if (convention.sourceId().equals(sourceId)) {
        return policy.gateFor(convention);
      }
    }
    return null;
  }

  /**
   * What the front half produced, kept so that a <em>narrowing</em> can start from it (§2.1, D233).
   *
   * <p>The statement's own text holds no context reference — {@code @ctx} is the descriptor's
   * vocabulary, folded inside the pass — so parse, validation and conversion are a function of the
   * statement and the catalog alone and give the same tree whatever this request binds. That is what
   * makes the tree reusable, and it is why the identity D233 asks for holds by construction: a
   * narrowing runs the very same pass over the very same tree, with the union of the bindings.
   */
  public record Front(
      SqlNode validated,
      RelRoot root,
      RelDataType parameterRowType,
      chalk.planner.entitlement.StarProvenance stars,
      org.apache.calcite.rel.metadata.RelMetadataProvider metadataProvider,
      long parseMicros,
      long validateMicros,
      long convertMicros) {}

  /** Runs the whole pipeline. */
  public Result plan(String sql, boolean includePlanText)
      throws SqlParseException, ValidationException, RelConversionException {
    return plan(sql, includePlanText, ImmutableList.of());
  }

  /** The same, with what the caller expects this statement's parameters to be worth (D284). */
  public Result plan(
      String sql, boolean includePlanText, List<chalk.planner.rpc.v1.ParameterHint> hints)
      throws SqlParseException, ValidationException, RelConversionException {
    Front front = front(sql);
    parameterHints(hints, front);
    return finish(front, context, includePlanText);
  }

  /**
   * Parse, validate and convert — the half a narrowing skips when the sidecar retained it (D233).
   */
  public Front front(String sql) throws SqlParseException, ValidationException, RelConversionException {
    long t0 = System.nanoTime();
    SqlNode parsed = planner.parse(sql);

    // Babel's grammar has productions the core parser does not — CREATE TABLE, BEGIN, COMMIT, SHOW,
    // DISCARD — so under BABEL a statement that is not a query can reach code that assumed one.
    // Refused here by name (D259, ADR 0039 §2); a no-op under every other conformance.
    BabelStatementSupport.check(parsed, conformance);

    long t1 = System.nanoTime();
    // The star policy and the star provenance, on the parse tree and before validation (§3.11,
    // D160). Before, and not merely early: the validator expands a select list *in place*, so after
    // it there is no star left in the tree to see. The policy is review discipline — a star is
    // expanded and resolved before the rewrite either way — and the provenance is the one bit Omit
    // needs, which is whether the statement named a column itself.
    chalk.planner.entitlement.StarProvenance stars = chalk.planner.entitlement.StarProvenance.NONE;
    if (catalog.hasEntitlements()) {
      chalk.planner.entitlement.StarProvenance.check(
          parsed, policyOptions.star(), entitledNames());
      stars = chalk.planner.entitlement.StarProvenance.of(parsed);
    }
    // SQL bodies are inlined before validation, so a call to one disappears into ordinary algebra
    // and everything downstream — the validator, the optimiser, pushdown — sees only what the body
    // is made of (D78, docs/design/17-user-defined-functions.md §2).
    SqlNode inlined = chalk.planner.ir.SqlBodyInliner.inline(parsed, catalog.functions());
    // The statement's own words, for a refusal that names an expression (D296). Nothing is read
    // from it unless a refusal asks.
    StatementText text = StatementText.of(sql, parserConfig(), catalog.functions());
    SqlNode validated;
    try {
      validated = planner.validate(inlined);
    } catch (ValidationException e) {
      // Calcite's own type checks can stop at a misplaced composite first — a composite value IN a subquery
      // reads to it as a row of the composite's fields, a comparison with a scalar has no signature —
      // and the refusal by name below should not depend on which of the two reached it (D291).
      CompositeSupport.checkUnvalidated(inlined, planner.validator(), text);
      throw e;
    }
    RelDataType parameterRowType = planner.getParameterRowType();

    // The one LATERAL shape the general decorrelator gets wrong, refused here and not later: it is a
    // question about the statement's own text, and `Planner.rel` decorrelates unconditionally, so
    // after conversion there is no correlate left to ask (ADR 0026).
    LateralCorrelationSupport.check(validated);

    // Where a composite value may not stand — a sort, grouping or partition key, a comparison, a CASE
    // or COALESCE result beside a scalar or a composite of another type (D295), a built-in
    // aggregate's argument — refused on the validated statement, while every expression
    // still has its validated type and before conversion can fold a comparison away or build a sort
    // nothing can execute (D291, ADR 0077).
    CompositeSupport.check(validated, java.util.Objects.requireNonNull(planner.validator()), text);

    long t2 = System.nanoTime();
    RelRoot root = convert(sql, validated);
    long t3 = System.nanoTime();
    return new Front(
        validated,
        root,
        parameterRowType,
        stars,
        // What conversion left in force, so that a narrowing starts the pass from the same state a
        // fresh conversion would (D233). Calcite keeps the handler provider in a *thread* local that
        // conversion sets; a narrowing runs on whichever thread the call arrived on and would
        // otherwise find none, and a re-entered cluster would still be carrying the provider the
        // previous run's optimiser installed. Both are settled by restoring this one.
        root.rel.getCluster().getMetadataProvider(),
        micros(t0, t1),
        micros(t1, t2),
        micros(t2, t3));
  }

  /**
   * The pass, the optimiser and everything after them, over a front half this pipeline produced
   * (D233). {@code context} is a parameter rather than the field, because a narrowing runs this half
   * again with the union of the bindings and nothing else about the request changes.
   */
  public Result finish(Front front, chalk.planner.entitlement.BoundContext context, boolean includePlanText)
      throws RelConversionException {
    return finish(front, context, includePlanText, null);
  }

  /**
   * The same, saying in the stage list which plan's front half this one started from (D233).
   *
   * @param narrowedFrom the base plan's digest as text, or null for a plan built from SQL
   */
  public Result finish(
      Front front,
      chalk.planner.entitlement.BoundContext context,
      boolean includePlanText,
      @Nullable String narrowedFrom)
      throws RelConversionException {
    return finish(front, context, includePlanText, narrowedFrom, null);
  }

  /**
   * The same, under this request's planning options (D234). A narrowing is governed exactly as a
   * prepare is: the retained pipeline gets this run's governor, and a stop or a cancel ends its
   * search the same way (D238).
   *
   * @param governor what may end this run's search early, or null for a run that runs to completion
   */
  public Result finish(
      Front front,
      chalk.planner.entitlement.BoundContext context,
      boolean includePlanText,
      @Nullable String narrowedFrom,
      chalk.planner.diag.@Nullable PlanningGovernor governor)
      throws RelConversionException {
    return finish(front, context, includePlanText, narrowedFrom, governor, null);
  }

  /**
   * The same, rendering the plan text with this statement's literals redacted (D262). A
   * <em>second</em> rendering, selected by the request: a null redactor is the one the corpus
   * goldens are recorded through, and it goes through {@code RelOptUtil.dumpPlan} unchanged.
   *
   * @param redactor what turns each literal into its pseudonym, or null for the plain rendering
   */
  public Result finish(
      Front front,
      chalk.planner.entitlement.BoundContext context,
      boolean includePlanText,
      @Nullable String narrowedFrom,
      chalk.planner.diag.@Nullable PlanningGovernor governor,
      chalk.planner.redact.@Nullable PlanTextRedactor redactor)
      throws RelConversionException {
    this.governor = governor;
    long t3start = System.nanoTime();
    if (front.metadataProvider() != null) {
      front.root().rel.getCluster().setMetadataProvider(front.metadataProvider());
      front.root().rel.getCluster().invalidateMetadataQuery();
    }
    SqlNode validated = front.validated();
    RelRoot root = front.root();
    RelDataType parameterRowType = front.parameterRowType();
    chalk.planner.entitlement.StarProvenance stars = front.stars();
    // The *unprojected* relation is what gets optimised, as Calcite's own Prepare does. The root
    // projection is applied afterwards, by rootProject: the required collation below is stated over
    // this row, so projecting first would leave it pointing at a column the row no longer has
    // (ADR 0014).
    RelNode logical = root.rel;

    // The stages that actually ran. A narrowing that hit says which plan it started from in their
    // place, because parse, validation and conversion are exactly what it did not do (D233).
    List<String> stages =
        narrowedFrom == null
            ? new ArrayList<>(List.of(STAGE_PARSE, STAGE_VALIDATE, STAGE_SQL_TO_REL))
            : new ArrayList<>(List.of(STAGE_NARROWED_FROM + " " + narrowedFrom));

    // The entitlement rewrite (step 26, 16-entitlements.md §3), between SqlToRelConverter and the
    // Hep pre-pass and installed only when the registered catalog carries an entitlement. For every
    // other catalog this is one boolean and the stage list does not name it, which is §0's zero-cost
    // property at this level and is what the zero-cost class asserts.
    List<chalk.planner.entitlement.DisclosureMap> entitledLeaves = ImmutableList.of();
    List<chalk.planner.entitlement.PolicyExplain.Table> explained = ImmutableList.of();
    List<chalk.planner.entitlement.Disclosed> flow = ImmutableList.of();
    chalk.planner.entitlement.EntitlementPass.Siblings siblings = null;
    java.util.Map<String, RexNode> rowPredicates = java.util.Map.of();
    List<chalk.planner.entitlement.TaintCheck.ThroughEvidence> throughJoins = ImmutableList.of();
    if (catalog.hasEntitlements()) {
      // A request that binds nothing at all against an entitled catalog has told the planner
      // neither the values nor the shape, and a policy's @ctx names would then be read as columns.
      // Refusing it by name is the fail-closed answer, and it now names both ways out (§2, D209).
      if (context.isEmpty() && !context.shapeOnly()) {
        throw new chalk.planner.entitlement.PolicyException(
            "this catalog carries an entitlement and the request binds no execution context, so"
                + " there is nothing for the policy's @ctx names to resolve against. Prepare with a"
                + " context — the primary mode — or with a shape-only one and bind the values at"
                + " execution (docs/design/16-entitlements.md §2, D152, D209).");
      }
      stages.add(STAGE_ENTITLEMENTS);
      chalk.planner.entitlement.EntitlementPass.Result rewritten =
          chalk.planner.entitlement.EntitlementPass.apply(
              logical,
              descriptorConverter(logical, context),
              context,
              policyOptions,
              catalog.descriptor().getAssociationsList(),
              rexExecutor());
      logical = rewritten.rel();
      entitledLeaves = rewritten.leaves();
      explained = rewritten.explained();
      flow = rewritten.disclosures();
      siblings = rewritten.siblings();
      rowPredicates = rewritten.rowPredicates();
      throughJoins = rewritten.throughJoins();
      // Clause 1 of the taint check, here and not later: AGGREGATE_REDUCE_FUNCTIONS rewrites AVG
      // into SUM0 and COUNT in the Hep pass below, and a physical check against a host's list of
      // AVG alone would refuse a correct plan (§3.10).
      chalk.planner.entitlement.TaintCheck.logical(logical);
    }

    // Which of this request's parameters were planned against a value the caller expected (D284).
    // The ordinals and nothing else: a hint's value never reaches a stage list, a log line, an
    // exception, the plan text or a digest. A request that sent none adds nothing here, so every
    // stage list recorded before hints existed reads exactly as it did.
    if (!parameterHints.isEmpty()) {
      stages.add(
          STAGE_PARAMETER_HINTS
              + " "
              + parameterHints.ordinals().stream()
                  .map(String::valueOf)
                  .collect(java.util.stream.Collectors.joining(", ")));
    }

    long t3 = System.nanoTime();
    stages.add(STAGE_HEP);
    // The pass is part of the front half the split above measures, so it is charged to `convert`.
    long passMicros = micros(t3start, t3);
    // The Hep pre-pass decorrelates too — SqlToRelConverter only decorrelates the top-level query —
    // so the same failure can surface here (see convert above).
    try {
      logical = hep(logical);
    } catch (AssertionError e) {
      throw decorrelationFailure(e);
    }

    // What this milestone cannot express, refused on the logical tree so a caller gets UNSUPPORTED
    // naming the feature rather than Volcano's "cannot plan" (13-window-functions.md §9,
    // 14-windows-ii.md §1 and §8).
    WindowSupport.check(logical);
    CorrelateSupport.check(logical);
    String logicalText = includePlanText ? explain(logical, redactor) : "";

    // Chalk's own metadata handlers, in front of Calcite's defaults. ADR 0001 left this chain out
    // of M1 because an empty one is behaviourally identical to no chain; from M2 it carries the
    // Selectivity handler that decides whether an index lookup is worth choosing, so it is no longer
    // empty (11-m2-index-support.md §4).
    //
    // setMetadataProvider also points this thread's RelMetadataQueryBase.THREAD_PROVIDERS at a
    // Janino provider over the chain, which is what makes `RelMetadataQuery.instance()` — the one
    // RelToIr is handed — see the same handlers as the optimiser did. The provider is a constant,
    // so the generated dispatch compiles once per process rather than once per request.
    logical.getCluster().setMetadataProvider(ChalkRelMetadata.SOURCE);
    logical.getCluster().invalidateMetadataQuery();

    RelOptPlanner optPlanner = logical.getCluster().getPlanner();
    if (optPlanner instanceof VolcanoPlanner volcano) {
      // Top-down from step 17 (D47). Bottom-up Volcano puts a physical alternative in the RelSubset
      // its traits satisfy and nowhere else, so an index lookup or a merge join whose ordering is
      // not the one a parent inherited is invisible to that parent (ADR 0015 V-M2-6). Top-down asks
      // each physical rel what it can pass down and what it delivers instead, which is what makes
      // ordering-dependent operators findable. Pinned rather than left to a system property,
      // because it changes which alternatives are explored and therefore the golden plans.
      volcano.setTopDownOpt(true);
    }
    if (includePlanText && !ruleTraceInstalled) {
      // Once per pipeline, whatever it is re-entered with: the planner's listener is a multicast and
      // a second registration would count every rule twice (D233).
      optPlanner.addListener(ruleTrace);
      ruleTraceInstalled = true;
    }
    ruleTrace.reset();

    // The governor, on the same terms and for the same reason (D235). The *slot* is registered once
    // and points at this run's governor; `attach` is what clears the previous run's cancel flag,
    // counts and samples.
    governorSlot.set(governor);
    if (governor != null) {
      if (!governorInstalled) {
        optPlanner.addListener(governorSlot);
        governorInstalled = true;
      }
      if (optPlanner instanceof VolcanoPlanner volcano) {
        governor.attach(volcano, cancelFlag);
      }
    } else {
      // A run with no governor must not inherit the flag a previous run of a retained pipeline
      // raised, or its very first rule match would throw.
      cancelFlag.clearCancel();
    }

    // The root's required traits must include the ordering the query asks for. Without it the
    // sort's RelSet is merged with its input's and Volcano is free to pick the unsorted member —
    // ORDER BY would silently disappear. This mirrors Calcite's own Prepare.getDesiredRootTraitSet.
    RelTraitSet required =
        logical
            .getCluster()
            .traitSet()
            .replace(ChalkConvention.LOCAL)
            .replace(requiredCollation(root, logical))
            .simplify();
    RelNode optimised;
    try {
      optimised = planner.transform(programFor(logical), required, logical);
    } catch (RelOptPlanner.CannotPlanException cannotPlan) {
      // The flag was raised before the root held anything to build from, so there is no plan and
      // saying "not enough rules" would be a lie (D235). Everything else this can mean — a query
      // this milestone's rules genuinely cannot plan — is re-thrown untouched.
      if (governor != null && governor.raised()) {
        throw new PlanningAbortedException(governor, cannotPlan);
      }
      throw cannotPlan;
    } finally {
      if (governor != null) {
        governor.detach();
      }
    }

    // What the caller is told (§3.12), computed on the optimised tree before the root projection,
    // because Omit edits the field list that projection is built from.
    ImmutablePairList<Integer, String> fields = root.fields;
    List<chalk.planner.rpc.v1.ReportedDisclosure> disclosures = ImmutableList.of();
    java.util.Set<String> pushed = java.util.Set.of();
    boolean omitDegraded = false;
    if (!entitledLeaves.isEmpty()) {
      // Clauses 2, 3 and 4, on the optimised tree and before the root projection: what is being
      // proved is that the optimiser moved the policy about without losing it (§3.10).
      optimised.getCluster().invalidateMetadataQuery();
      chalk.planner.entitlement.TaintCheck.physical(
          optimised,
          entitledLeaves,
          rowPredicates,
          throughJoins,
          optimised.getCluster().getMetadataQuery(),
          optimised.getCluster().getRexBuilder(),
          rexExecutor());

      chalk.planner.entitlement.RedactionPolicy.Result trimmed =
          chalk.planner.entitlement.RedactionPolicy.apply(
              fields,
              flow,
              stars.widened(validated, fields.size()),
              policyOptions,
              context.shapeOnly());
      fields = trimmed.fields();
      omitDegraded = trimmed.degraded();
      // The sibling disclosure columns, beside the ones that survived Omit and in their order
      // (§3.12, D207). After the trim, so a column the caller never sees takes no sibling with it.
      if (siblings != null) {
        fields =
            chalk.planner.entitlement.DisclosureColumns.extend(
                fields, siblings.at(), siblings.present(), policyOptions.disclosureColumnSuffix());
      }
      disclosures = chalk.planner.entitlement.DisclosureReport.columns(fields.leftList(), flow);
      pushed =
          chalk.planner.entitlement.DisclosureReport.pushedRowPredicates(optimised, throughJoins);
    }

    RelNode physical = rootProject(optimised, fields);

    // The two refusals that can only be made on the *physical* tree: a remote query on the inner
    // side of a nested loop, and a local join that would pull more rows across a boundary than the
    // policy allows (D105, D104). Both name the join and what it would have cost.
    CrossSourceSupport.check(physical, joinPolicy);

    // And the entitlement's own refusal of the same kind (D199): a table whose host declared
    // PUSHDOWN_REQUIRED may not have its row predicate evaluated locally. It is here beside the
    // others because it too is about which alternative cost chose.
    chalk.planner.entitlement.PushdownRequired.check(
        physical,
        rowPredicates,
        throughJoins,
        pushed,
        this::gateFor,
        new chalk.planner.entitlement.PushdownRequired.Exchanges(joinPolicy, this::sourceOfSchema));

    long t4 = System.nanoTime();
    PlanningState state = planningState(governor);
    // The stage says how the search ended when it did not simply finish, and says nothing extra when
    // it did — so every plan recorded before this existed keeps the stage list it had.
    stages.add(stageForVolcano(state));
    stages.add(STAGE_ROOT_PROJECT);
    return new Result(
        physical,
        parameterRowType,
        logicalText,
        includePlanText ? explainWithCost(physical, redactor) : "",
        ruleTrace.rulesFired(),
        ImmutableList.copyOf(stages),
        entitledLeaves,
        explained,
        disclosures,
        pushed,
        chalk.planner.entitlement.DisclosureReport.requiredRelations(physical),
        chalk.planner.entitlement.DisclosureReport.requiredScalars(physical),
        omitDegraded,
        // What *this* request spent. A narrowing that started from a retained tree did not parse,
        // validate or convert anything, and saying so is the point of measuring at all (D233); what
        // it did do in that half is the entitlement pass, which is charged to `convert` either way.
        narrowedFrom == null ? front.parseMicros() : 0L,
        narrowedFrom == null ? front.validateMicros() : 0L,
        (narrowedFrom == null ? front.convertMicros() : 0L) + passMicros,
        micros(t3, t4),
        state);
  }

  /** What the governor saw, or {@link PlanningState#UNGOVERNED} for a run that had none. */
  private static PlanningState planningState(
      chalk.planner.diag.@Nullable PlanningGovernor governor) {
    if (governor == null) {
      return PlanningState.UNGOVERNED;
    }
    chalk.planner.diag.PlanningGovernor.Termination reason = governor.termination();
    return new PlanningState(
        // A governed run that nothing ended is a run that finished: CONVERGED, with its last sample.
        reason == null ? chalk.planner.diag.PlanningGovernor.Termination.CONVERGED : reason,
        true,
        governor.evaluations(),
        governor.looks(),
        governor.evaluationInterval(),
        governor.runningElapsedNanos(),
        governor.queuedElapsedNanos(),
        governor.slices(),
        governor.firstCost(),
        governor.bestCost(),
        governor.samples());
  }

  /** {@code volcano}, and how the search ended when something ended it early (D235). */
  public static String stageForVolcano(PlanningState state) {
    if (!state.governed()
        || state.termination() == chalk.planner.diag.PlanningGovernor.Termination.CONVERGED) {
      return STAGE_VOLCANO;
    }
    return STAGE_VOLCANO + " " + state.termination().name().toLowerCase(java.util.Locale.ROOT).replace('_', ' ');
  }

  /**
   * A search the governor ended before the root held any complete plan (D235). There is no plan to
   * return and nothing is wrong with the statement; what the caller gets is the reason and the
   * state.
   */
  public static final class PlanningAbortedException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    private final transient PlanningState state;

    PlanningAbortedException(
        chalk.planner.diag.PlanningGovernor governor, Throwable cause) {
      super(
          "planning was ended before the optimiser had a complete plan ("
              + String.valueOf(governor.termination())
                  .toLowerCase(java.util.Locale.ROOT)
                  .replace('_', ' ')
              + " after "
              + governor.evaluations()
              + " rule evaluations). Raise the time budget, or prepare without one"
              + " (docs/design/30-planning-options.md, D235).",
          cause);
      this.state =
          new PlanningState(
              chalk.planner.diag.PlanningGovernor.Termination.ABORTED,
              true,
              governor.evaluations(),
              governor.looks(),
              governor.evaluationInterval(),
              governor.runningElapsedNanos(),
              governor.queuedElapsedNanos(),
              governor.slices(),
              governor.firstCost(),
              governor.bestCost(),
              governor.samples());
    }

    /** The state, with {@code ABORTED} for its reason and whatever the governor had measured. */
    public PlanningState state() {
      return state;
    }
  }

  /**
   * {@code SqlToRel}, with Calcite's decorrelation failures turned into a message a caller can act
   * on (D67).
   *
   * <p>Both of Calcite 1.42's decorrelators check their own result through {@code Litmus.THROW} —
   * so unconditionally, not under {@code -ea} — and both can produce a tree whose row type differs
   * from the query's by a nullability. Chalk runs the general one (V26), which decorrelates every
   * lateral shape the corpus asks for except a non-{@code COUNT} aggregate on the <em>inner</em>
   * side of a {@code CROSS JOIN LATERAL}: there it widens the measure to nullable over an inner
   * join that cannot produce a NULL, and the first rule that merges the projection back together
   * disagrees with it. That is D67's case, reported as {@code UNSUPPORTED} rather than as an
   * internal error.
   */
  private RelRoot convert(String sql, SqlNode validated)
      throws SqlParseException, ValidationException, RelConversionException {
    try {
      return planner.rel(validated);
    } catch (AssertionError e) {
      throw decorrelationFailure(e);
    }
  }

  /**
   * The failure, as {@code UNSUPPORTED} naming the shape. The correlate itself is gone by the time
   * this is reached — a decorrelator that rewrote the tree and then failed its own check has
   * already removed it — so the message names the construct and the way out of it rather than the
   * correlation's columns; a correlate that <em>survives</em> is a different path, and
   * {@link CorrelateSupport} names that one exactly.
   */
  private static chalk.planner.UnsupportedFeatureException decorrelationFailure(
      AssertionError failure) {
    String detail = String.valueOf(failure.getMessage());
    int newline = detail.indexOf('\n');
    return new chalk.planner.UnsupportedFeatureException(
        "correlated subquery could not be decorrelated: a LATERAL or correlated sub-query whose "
            + "decorrelated form Calcite cannot re-type ("
            + (newline < 0 ? detail : detail.substring(0, newline))
            + ")",
        "Chalk decorrelates LATERAL and correlated sub-queries into ordinary joins and never "
            + "re-runs the right side per left row (docs/design/14-windows-ii.md §8). An aggregate "
            + "on the inner side of a CROSS JOIN LATERAL is the shape Calcite 1.42 cannot rewrite; "
            + "LEFT JOIN LATERAL (…) ON TRUE decorrelates cleanly and means the same thing when "
            + "every left row has a match.");
  }

  /**
   * The unoptimised relational tree: parse, validate, {@code SqlToRel}, and nothing else.
   *
   * <p>This is the tree {@link DistinctStrategy} is asked about, before any rule has run, which is
   * why it is reachable: a test that asserts which strategy a query gets needs the same input the
   * pipeline gives it. One call per pipeline, like {@link #plan}.
   */
  public RelNode logical(String sql)
      throws SqlParseException, ValidationException, RelConversionException {
    return planner
        .rel(
            planner.validate(
                chalk.planner.ir.SqlBodyInliner.inline(planner.parse(sql), catalog.functions())))
        .rel;
  }

  /**
   * What ordering the finished plan must deliver.
   *
   * <p>{@code RelRoot.collation} is fixed before the Hep pre-pass, and the pre-pass can legitimately
   * weaken it: {@code SORT_REMOVE_CONSTANT_KEYS} drops a key the WHERE clause pins to a constant. If
   * the root is still a {@code Sort} its collation is the authority, because it is the one the rest
   * of the tree was rewritten against. Otherwise the pre-pass removed the sort altogether, and what
   * the query asked for is the honest requirement even if satisfying it costs a sort nobody needs.
   *
   * <p>Either way the collation is stated over the unprojected root row, which is the row being
   * optimised — that is the whole point of projecting afterwards (ADR 0014).
   */
  private static RelCollation requiredCollation(RelRoot root, RelNode afterHep) {
    if (root.collation.getFieldCollations().isEmpty()) {
      return RelCollations.EMPTY;
    }
    return afterHep instanceof Sort sort ? sort.getCollation() : root.collation;
  }

  /**
   * The final projection: the SELECT list's columns, in its order, under its names.
   *
   * <p>Two things are being fixed here at once, which is why they share one place (ADR 0014).
   *
   * <ul>
   *   <li><b>Columns the SELECT list drops.</b> {@code ORDER BY volume} over {@code SELECT symbol,
   *       ts} needs {@code volume} in the row the {@code Sort} sees and out of the row the client
   *       gets, so the projection has to come after the sort — and therefore after optimisation,
   *       because the ordering is a required root trait.
   *   <li><b>Names the optimiser threw away.</b> {@code ProjectRemoveRule} compares indexes and
   *       types and not names, so a rename-only {@code Project} is trivial to it and disappears,
   *       leaving the catalog's spelling where the query's should be (ADR 0001 §3). A pure rename
   *       over a {@code Project} is applied in place, because a {@code Project} already defines its
   *       own output row; anything else gets the projection on top, which is what a rename is.
   * </ul>
   *
   * <p>Nothing is added when the indexes are already the identity and the names already agree, which
   * is every corpus query, so no recorded plan changes shape.
   */
  private static RelNode rootProject(RelNode optimised, ImmutablePairList<Integer, String> fields) {
    RelDataType produced = optimised.getRowType();
    boolean refTrivial = Mappings.isIdentity(fields.leftList(), produced.getFieldCount());
    if (refTrivial && fields.rightList().equals(produced.getFieldNames())) {
      return optimised;
    }

    RelDataTypeFactory typeFactory = optimised.getCluster().getTypeFactory();
    List<RelDataType> types = new ArrayList<>(fields.size());
    for (int index : fields.leftList()) {
      types.add(produced.getFieldList().get(index).getType());
    }

    // A row type's field names have to be distinct — Calcite's Project asserts it, and a client
    // reading columns by name could not tell two `symbol`s apart anyway. `SELECT s.symbol, t.symbol`
    // is legal SQL and only becomes writable once there are joins, so the second one is renamed the
    // way Calcite renames its own duplicates: `symbol0`. Nothing changes when the names are already
    // distinct, which is every query without a join (ADR 0016).
    List<String> names = SqlValidatorUtil.uniquify(fields.rightList(), /* caseSensitive= */ true);
    RelDataType rowType = typeFactory.createStructType(types, names);

    if (refTrivial && optimised instanceof ChalkProject project) {
      return project.copy(
          project.getTraitSet(), project.getInput(), project.getProjects(), rowType);
    }

    RexBuilder rexBuilder = optimised.getCluster().getRexBuilder();
    List<RexNode> projects = new ArrayList<>(fields.size());
    for (int index : fields.leftList()) {
      projects.add(rexBuilder.makeInputRef(optimised, index));
    }
    return ChalkProject.create(optimised, projects, rowType);
  }

  /**
   * The Hep pre-pass: deterministic, bottom-up, in the order {@link RuleSets#hep} lists.
   *
   * <p>Which list depends on one property of the query: whether its {@code DISTINCT} aggregates are
   * small enough for the executor's native single pass or big enough to be worth a join (D54). The
   * choice is made here, on the tree as it arrives, because it decides whether an expansion rule
   * runs at all rather than which of two rewrites wins.
   *
   * <p>Two phases, not one collection: {@link RuleSets#transitivePredicates} says why, and
   * {@link #orderJoins} is the same argument about a different pair (F66).
   */
  private static RelNode hep(RelNode rel) {
    HepProgramBuilder builder = new HepProgramBuilder();
    builder.addMatchOrder(HepMatchOrder.BOTTOM_UP);
    builder.addRuleCollection(RuleSets.hep(DistinctStrategy.of(rel)));
    for (RelOptRule rule : RuleSets.transitivePredicates()) {
      builder.addRuleInstance(rule);
    }
    HepProgram program = builder.build();

    HepPlanner hepPlanner = new HepPlanner(program);
    chalk.planner.diag.HepTransformations.attach(hepPlanner);
    hepPlanner.setRoot(rel);
    RelNode reduced = hepPlanner.findBestExp();

    RelBuilder relBuilder = RelFactories.LOGICAL_BUILDER.create(reduced.getCluster(), null);

    // Decorrelate whatever the subquery rules turned into a Correlate. From step 17 this is what
    // makes IN / EXISTS / scalar sub-queries into joins the optimiser can cost; before that it
    // existed so a correlated subquery produced a clear UNSUPPORTED rather than a Volcano failure.
    //
    // Only when there is something to do. `RelDecorrelator.decorrelateQuery` runs its
    // remove-correlation rules unconditionally, and on a tree the general decorrelator has already
    // rewritten (V26) those rules can change a column's nullability and then fail Hep's own
    // type check — which is a failure about nothing, since there is no correlation left.
    if (hasCorrelation(reduced)) {
      reduced = RelDecorrelator.decorrelateQuery(reduced, relBuilder);
    }

    // What decorrelation just produced: LEFT MARK joins to turn into semi/anti joins, and the
    // filters and projects that follow from doing so.
    reduced = rewrite(reduced, RuleSets.afterDecorrelation());

    // Too many joins for Volcano to enumerate: fix an order heuristically first (D44).
    reduced = orderJoins(reduced);

    // Trim columns nothing reads. This is also what puts a Project directly above each scan, which
    // is what ChalkProjectScanRule needs in order to prune the scan itself. Chalk's own trimmer,
    // because Calcite's keeps every column that defines an input's collation and for a catalog that
    // declares one that is every table's ordering key in every scan (F114).
    reduced = new ChalkFieldTrimmer(relBuilder).trim(reduced);

    // Last, so that nothing above can put one back: LITERAL_AGG written as the projected literal it
    // means (F71). The sub-query rules of `RuleSets.hep` are the only thing in the pipeline that
    // emits it, and this runs after every phase of them and after the trim; Volcano's pinned list
    // and the pushdown rules carry no rule that produces one, so what reaches `RelToIr` — and what
    // the taint check, the cross-source check and the pushdown check all read — is free of it.
    return LiteralAggregates.rewrite(reduced);
  }

  /**
   * The descriptor converter for this request (§3.2): a catalog reader, a validator and a
   * {@code SqlToRelConverter} over the query's own cluster, so every expression the pass drops onto
   * a leaf indexes that leaf's row type.
   */
  private chalk.planner.entitlement.DescriptorConverter descriptorConverter(
      RelNode logical, chalk.planner.entitlement.BoundContext context) {
    return new chalk.planner.entitlement.DescriptorConverter(
        logical.getCluster(),
        // The declared catalog: a mask is evaluated over the leaf's raw row (§1), and the scan
        // beneath the leaf reads exactly that row, so every RexInputRef the descriptor yields must
        // be typed by the column the source stores and not by the disclosed one above it (F58).
        declaredSchema,
        ChalkOperatorTable.instance(libraries, catalog.functions()),
        SqlConfigs.validator(conformance),
        SqlConfigs.parser(conformance),
        SqlConfigs.sqlToRel(),
        SqlConfigs.connectionConfig(),
        context);
  }

  /**
   * The folder the pass simplifies with. The same wrapper the framework config installs (V32): a
   * client-bodied user function in a mask must not be handed to Calcite's compiler, which would try
   * to compile it and fail over something there is nothing here to evaluate anyway.
   */
  private static org.apache.calcite.rex.RexExecutor rexExecutor() {
    return new ChalkRexExecutor(RexUtil.EXECUTOR);
  }

  /** The catalog's entitled tables, as the star policy asks about them by name (§3.11). */
  private chalk.planner.entitlement.StarProvenance.EntitledNames entitledNames() {
    return new chalk.planner.entitlement.StarProvenance.EntitledNames() {
      @Override
      public String entitledTable(org.apache.calcite.sql.SqlIdentifier identifier) {
        List<String> names = identifier.names;
        String table = names.get(names.size() - 1);
        for (chalk.ir.v1.Schema schema : catalog.descriptor().getSchemasList()) {
          for (chalk.ir.v1.Table declared : schema.getTablesList()) {
            if (declared.hasEntitlement() && declared.getName().equalsIgnoreCase(table)) {
              return schema.getName() + "." + declared.getName();
            }
          }
        }
        return null;
      }

      @Override
      public boolean any() {
        return catalog.hasEntitlements();
      }
    };
  }

  /** Which Volcano program this tree gets: the one that reorders joins, or the one that does not. */
  private static int programFor(RelNode logical) {
    return JoinOrdering.needsHeuristicOrdering(logical)
        ? PROGRAM_FIXED_JOIN_ORDER
        : PROGRAM_ENUMERATE_JOINS;
  }

  /**
   * The heuristic join-ordering pass (D44). Above {@link JoinOrdering#VOLCANO_MAX_JOINS} joins,
   * {@code JOIN_COMMUTE} and {@code JOIN_ASSOCIATE} would explore a space that grows factorially, so
   * the joins are collapsed into a {@code MultiJoin} and {@code LoptOptimizeJoinRule} picks a
   * left-deep order from the M2 statistics before Volcano ever sees them.
   *
   * <p>Below the threshold this does nothing at all, which keeps every M1 and M2 plan and every
   * small join query on the exhaustive path.
   */
  private static RelNode orderJoins(RelNode rel) {
    if (!JoinOrdering.needsHeuristicOrdering(rel)) {
      return rel;
    }

    // One phase per rule, not one collection of them: JOIN_TO_MULTI_JOIN and MULTI_JOIN_OPTIMIZE
    // are inverses, and in a single Hep collection they undo each other forever — TPC-H Q5 never
    // finished planning. A HepProgram's phases run to fixpoint in order, so collapsing finishes
    // before optimising starts and the collapsing rule is gone by then.
    HepProgramBuilder builder = new HepProgramBuilder();
    builder.addMatchOrder(HepMatchOrder.BOTTOM_UP);
    for (RelOptRule rule : RuleSets.joinOrdering()) {
      builder.addRuleInstance(rule);
    }

    HepPlanner planner = new HepPlanner(builder.build());
    chalk.planner.diag.HepTransformations.attach(planner);
    planner.setRoot(rel);
    return planner.findBestExp();
  }

  /** Whether anything under {@code rel} is still correlated. */
  private static boolean hasCorrelation(RelNode rel) {
    if (rel instanceof Correlate || !rel.getVariablesSet().isEmpty()) {
      return true;
    }

    for (RelNode input : rel.getInputs()) {
      if (hasCorrelation(input)) {
        return true;
      }
    }

    return false;
  }

  /** One deterministic bottom-up Hep pass over {@code rules}. */
  private static RelNode rewrite(RelNode rel, List<RelOptRule> rules) {
    HepProgramBuilder builder = new HepProgramBuilder();
    builder.addMatchOrder(HepMatchOrder.BOTTOM_UP);
    builder.addRuleCollection(rules);
    HepPlanner planner = new HepPlanner(builder.build());
    chalk.planner.diag.HepTransformations.attach(planner);
    planner.setRoot(rel);
    return planner.findBestExp();
  }

  /** {@code RelOptUtil.dumpPlan} with attributes, for diagnostics only. */
  public static String explain(RelNode rel) {
    return explain(rel, null);
  }

  /**
   * The same, with this statement's literals as pseudonyms when a redaction asked for it (D262).
   * A null redactor is {@code RelOptUtil.dumpPlan} itself, byte for byte, which is what the corpus
   * goldens are recorded through.
   */
  public static String explain(RelNode rel, chalk.planner.redact.@Nullable PlanTextRedactor redactor) {
    return redactor == null
        ? RelOptUtil.dumpPlan("", rel, SqlExplainFormat.TEXT, SqlExplainLevel.EXPPLAN_ATTRIBUTES)
        : redactor.explain(rel, SqlExplainLevel.EXPPLAN_ATTRIBUTES);
  }

  /**
   * The same with the estimates: {@code ALL_ATTRIBUTES} prints each node's row count and cumulative
   * cost, and it is the level at which a {@code ChalkFilter} and a {@code ChalkIndexLookup} add
   * their {@code sel=} item — so a reader can see both what the planner thought a predicate would
   * select and whether it measured that or guessed it ({@code 11-m2-index-support.md} §4).
   */
  public static String explainWithCost(RelNode rel) {
    return explainWithCost(rel, null);
  }

  /** The same, redacted (D262); a null redactor is the plain rendering, byte for byte. */
  public static String explainWithCost(
      RelNode rel, chalk.planner.redact.@Nullable PlanTextRedactor redactor) {
    return redactor == null
        ? RelOptUtil.dumpPlan("", rel, SqlExplainFormat.TEXT, SqlExplainLevel.ALL_ATTRIBUTES)
        : redactor.explain(rel, SqlExplainLevel.ALL_ATTRIBUTES);
  }

  @SuppressWarnings({"rawtypes", "unchecked"})
  private static List<RelTraitDef> traitDefs() {
    return (List) ImmutableList.of(ConventionTraitDef.INSTANCE, RelCollationTraitDef.INSTANCE);
  }

  private static long micros(long from, long to) {
    return (to - from) / 1_000L;
  }

  /**
   * Lets go of the optimiser's search space, keeping the converted tree and the cluster that owns it
   * (D233).
   *
   * <p>A retained pipeline would otherwise hold the whole {@code RelSet} graph of the last plan it
   * produced, which is by far the largest thing in it and is of no use to a narrowing: the next run
   * through this pipeline clears the planner before it adds its rules anyway, which is exactly what
   * this does early. What a narrowing needs is the tree and the type factory that interned its row
   * types, and those stay.
   */
  public void releaseSearchSpace(Front front) {
    RelOptPlanner optPlanner = front.root().rel.getCluster().getPlanner();
    optPlanner.clear();
    front.root().rel.getCluster().invalidateMetadataQuery();
  }

  @Override
  public void close() {
    planner.close();
  }
}
