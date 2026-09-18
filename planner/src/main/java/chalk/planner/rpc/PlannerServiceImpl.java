package chalk.planner.rpc;

import chalk.ir.IrVersion;
import chalk.ir.v1.Plan;
import chalk.planner.PlannerConfig;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.ext.PlanExtensionContext;
import chalk.planner.ext.PlanExtensionRegistry;
import chalk.planner.ir.IrVersionGate;
import chalk.planner.ir.RelToIr;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import chalk.planner.rpc.v1.GetInfoRequest;
import chalk.planner.rpc.v1.GetInfoResponse;
import chalk.planner.rpc.v1.PlanRequest;
import chalk.planner.rpc.v1.PlanResponse;
import chalk.planner.rpc.v1.PlannerOptions;
import chalk.planner.rpc.v1.PlannerServiceGrpc;
import chalk.planner.rpc.v1.PlanningStats;
import chalk.planner.rpc.v1.RegisterCatalogRequest;
import chalk.planner.rpc.v1.RegisterCatalogResponse;
import io.grpc.stub.StreamObserver;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.sql.validate.SqlConformanceEnum;
import org.checkerframework.checker.nullness.qual.Nullable;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

/**
 * The four RPCs (docs/design/03-planner.md §7). Every one is unary and the deadline is the client's
 * business; the shared mutable state is the catalog registry and the in-flight plannings a
 * {@code StopPlanning} call can address (D236).
 */
public final class PlannerServiceImpl extends PlannerServiceGrpc.PlannerServiceImplBase {
  private static final Logger LOG = LoggerFactory.getLogger(PlannerServiceImpl.class);

  private final CatalogRegistry registry;
  private final PlanExtensionRegistry extensions;

  /**
   * The post-conversion trees a narrowing may start from (docs/design/16-entitlements.md §2.1,
   * D233). Empty for every deployment nobody narrows in, and cleared whenever a catalog arrives.
   */
  private final chalk.planner.plan.NarrowCache narrowing = new chalk.planner.plan.NarrowCache();

  /**
   * The plannings a {@code StopPlanning} call can address (D236). Empty for every request that names
   * no {@code request_id}, which is every request that cannot be stopped.
   */
  private final PlanningRegistry inFlight = new PlanningRegistry();

  /**
   * The bounded worker pool every planning runs under (D240). A test that constructs this service
   * without naming one gets a scheduler sized to the machine, which is what keeps a plain test from
   * ever seeing contention; a test of the scheduler itself names a small one explicitly.
   */
  private final PlanningScheduler scheduler;

  /**
   * The sidecar-wide default slice, in milliseconds (D239, D243): what a request's own
   * {@code convergence_sample_period_ms} falls back to when it names none.
   */
  private final long sliceMillis;

  public PlannerServiceImpl(CatalogRegistry registry) {
    this(registry, PlanExtensionRegistry.defaults());
  }

  /** With a registry of its own, so a test can see an empty one refuse, or watch one handler. */
  public PlannerServiceImpl(CatalogRegistry registry, PlanExtensionRegistry extensions) {
    this(registry, extensions, new PlanningScheduler(Runtime.getRuntime().availableProcessors()));
  }

  /** With the scheduler the sidecar's arguments asked for (D243), or a test's own. */
  public PlannerServiceImpl(
      CatalogRegistry registry, PlanExtensionRegistry extensions, PlanningScheduler scheduler) {
    this(registry, extensions, scheduler, PlannerConfig.DEFAULT_SLICE_MILLIS);
  }

  /** With the slice the sidecar's arguments asked for too (D243), or a test's own. */
  public PlannerServiceImpl(
      CatalogRegistry registry,
      PlanExtensionRegistry extensions,
      PlanningScheduler scheduler,
      long sliceMillis) {
    this.registry = registry;
    this.extensions = extensions;
    this.scheduler = scheduler;
    this.sliceMillis = sliceMillis;
  }

  public CatalogRegistry registry() {
    return registry;
  }

  /** The worker pool every planning runs under (D240); this service owns its lifecycle. */
  public PlanningScheduler scheduler() {
    return scheduler;
  }

  @Override
  public void getInfo(GetInfoRequest request, StreamObserver<GetInfoResponse> observer) {
    observer.onNext(
        GetInfoResponse.newBuilder()
            .setMinIrVersion(IrVersion.MINIMUM)
            .setMaxIrVersion(IrVersion.CURRENT)
            .setPlannerVersion(PlannerConfig.plannerVersion())
            .setCalciteVersion(PlannerConfig.calciteVersion())
            .setPlannerConfigHash(PlannerConfig.configHash())
            // What this sidecar accepts as a source's dialect, conformance and libraries (D248),
            // none of it per-request: every engine reads it once, at creation.
            .addAllDialects(DialectDiscovery.dialects())
            .addAllConformances(DialectDiscovery.conformances())
            .addAllLibraries(DialectDiscovery.libraries())
            .setPlanningWorkers(scheduler.workers())
            .build());
    observer.onCompleted();
  }

  @Override
  public void registerCatalog(
      RegisterCatalogRequest request, StreamObserver<RegisterCatalogResponse> observer) {
    try {
      CatalogRegistry.Registration registration = registry.register(request);
      // A tree retained against a catalog version that is no longer the newest is one the pass would
      // read different descriptors for. The fingerprint carries the shape version, so such an entry
      // could never match; clearing is what keeps it from occupying a slot until it is evicted. A
      // registration of a version already held changes nothing and clears nothing.
      if (registration.installed()) {
        narrowing.clear();
      }
      LOG.info(
          "registered catalog instance={} shape={} {} schemas={}",
          registration.instanceId(),
          registration.shapeVersion(),
          request.getBaseShapeVersion().isEmpty()
              ? "whole"
              : "delta over " + request.getBaseShapeVersion(),
          registration.catalog().descriptor().getSchemasCount());
      observer.onNext(RegisterCatalogResponse.getDefaultInstance());
      observer.onCompleted();
    } catch (RuntimeException e) {
      observer.onError(PlanErrors.toStatus(e));
    }
  }

  /**
   * Installs one statistics version (D271 (c), design 44 §3). Separate from the shape so a data-only
   * refresh publishes the tables whose numbers moved and nothing else; the shapes this instance
   * holds are joined with it on the next plan that names one.
   */
  @Override
  public void registerStatistics(
      chalk.planner.rpc.v1.RegisterStatisticsRequest request,
      StreamObserver<chalk.planner.rpc.v1.RegisterStatisticsResponse> observer) {
    try {
      registry.registerStatistics(request);
      LOG.debug(
          "registered statistics instance={} version={} tables={}",
          request.getInstanceId(),
          request.getStatisticsVersion(),
          request.getTablesCount());
      observer.onNext(chalk.planner.rpc.v1.RegisterStatisticsResponse.getDefaultInstance());
      observer.onCompleted();
    } catch (RuntimeException e) {
      observer.onError(PlanErrors.toStatus(e));
    }
  }

  @Override
  public void plan(PlanRequest request, StreamObserver<PlanResponse> observer) {
    try {
      observer.onNext(planOrThrow(request));
      observer.onCompleted();
    } catch (Exception e) {
      observer.onError(PlanErrors.toStatus(e));
    }
  }

  /**
   * Asks the planning named by {@code request_id} for the best complete plan it has (D236).
   *
   * <p>Never an error: an id nothing is in flight under answers {@code found = false}, which the
   * client reads as "that prepare has already finished". The evaluation count is the diagnostic —
   * the live one for a planning still running, the final one for a planning this sidecar remembers
   * having finished.
   */
  @Override
  public void stopPlanning(
      chalk.planner.rpc.v1.StopPlanningRequest request,
      StreamObserver<chalk.planner.rpc.v1.StopPlanningResponse> observer) {
    PlanningRegistry.Stopped stopped = inFlight.stop(request.getRequestId());
    if (stopped.found()) {
      LOG.debug(
          "stop requested for planning {} after {} evaluations",
          request.getRequestId(),
          stopped.evaluations());
    }
    observer.onNext(
        chalk.planner.rpc.v1.StopPlanningResponse.newBuilder()
            .setFound(stopped.found())
            .setEvaluationCount(stopped.evaluations())
            .build());
    observer.onCompleted();
  }

  /**
   * Redacts one statement's literals so the text can be logged (D262,
   * {@code docs/design/37-redacted-sql.md}). Reads no catalog, holds no state, and never logs the
   * salt it was given.
   */
  @Override
  public void redactSql(
      chalk.planner.rpc.v1.RedactSqlRequest request,
      StreamObserver<chalk.planner.rpc.v1.RedactSqlResponse> observer) {
    try {
      chalk.planner.redact.RedactionPolicy policy =
          chalk.planner.redact.RedactionPolicy.of(request.getRedaction(), request.hasRedaction());
      if (policy == null) {
        throw new IllegalArgumentException(
            "RedactSqlRequest carries no redaction options; a redaction needs at least a salt"
                + " (docs/design/37-redacted-sql.md §2).");
      }
      chalk.planner.redact.SqlRedactor.Result result =
          chalk.planner.redact.SqlRedactor.redact(
              request.getSql(), SqlConfigs.conformance(request.getConformanceValue()), policy);
      observer.onNext(
          chalk.planner.rpc.v1.RedactSqlResponse.newBuilder()
              .setRedactedSql(result.redactedSql())
              .setStructuralHash(com.google.protobuf.ByteString.copyFrom(result.structuralHash()))
              .setParsed(result.parsed())
              .build());
      observer.onCompleted();
    } catch (RuntimeException e) {
      observer.onError(PlanErrors.toStatus(e));
    }
  }

  /** The in-flight plannings, for tests and for the cancellation path. */
  public PlanningRegistry inFlight() {
    return inFlight;
  }

  /** Plans one request. Exposed so tests can assert on the plan without a channel. */
  public PlanResponse planOrThrow(PlanRequest request) throws Exception {
    int clientIrVersion = request.getClientIrVersion() == 0 ? IrVersion.CURRENT : request.getClientIrVersion();
    if (!IrVersionGate.isServable(clientIrVersion)) {
      throw new PlanErrors.IrVersionException(clientIrVersion, IrVersion.MINIMUM, IrVersion.CURRENT);
    }

    // One definite version per plan (D271 (a)): the plan's digest, every entitled read's descriptor
    // hash and D260's staleness are all keyed to the shape this statement is planned against, so a
    // range would license planning against a snapshot the engine no longer holds. A version this
    // planner no longer holds is answered by name — memory policy, recovered by the client's one
    // retry — and the epoch is not checked at all any more.
    String instanceId =
        request.getInstanceId().isEmpty() ? request.getContextId() : request.getInstanceId();
    String shapeVersion = request.getShapeVersion();
    RegisteredCatalog catalog =
        registry
            .find(instanceId, shapeVersion)
            .orElseThrow(
                () ->
                    new PlanErrors.UnknownCatalogVersionException(
                        instanceId,
                        shapeVersion.isEmpty() ? "(newest)" : shapeVersion,
                        "the shape a plan names. Register it and retry."));

    // The host's own text, checked where it arrives and before anything rewrites it (D223): a quoted
    // identifier beginning with `$chalk$` would otherwise be indistinguishable from one of the
    // planner's own markers — the context marker, the per-request context schema, the strict-function
    // guard — and the markers are what decide what the entitlement pass sees. Here rather than in the
    // pipeline, because this is the boundary text crosses from the host; the pipeline's own callers
    // hand it text the planner itself wrote.
    chalk.planner.ReservedNames.check(request.getSql(), "this statement");

    PlannerOptions options = request.getOptions();
    PushdownPolicy policy =
        new PushdownPolicy(
            options.getPushdown(),
            options.getDisableIndexLookup(),
            options.getDisabledCapabilitiesList());
    // Per request, and checked before anything is parsed: a dialect this planner does not know is
    // an INVALID_ARGUMENT naming the value, not a statement quietly planned as something else (D34).
    SqlConformanceEnum conformance = SqlConfigs.conformance(options.getConformanceValue());
    // The same rule for the dialect function libraries (D60): unknown value, named error.
    java.util.List<org.apache.calcite.sql.fun.SqlLibrary> libraries =
        SqlConfigs.libraries(options.getLibrariesValueList());
    // The cross-source join policy the catalog carries, with this request's merged over it (D104).
    chalk.planner.plan.JoinPolicy joinPolicy =
        chalk.planner.plan.JoinPolicy.of(catalog.joinPolicy(), options.getJoinPolicy());
    // What this request asked to have redacted, or null for every request that asked for nothing
    // (D262). Read here, where an unusable one is refused before anything is planned; an absent
    // message means no visitor runs, no seed is derived and no extra byte travels back.
    chalk.planner.redact.@Nullable RedactionPolicy redaction =
        chalk.planner.redact.RedactionPolicy.of(request.getRedaction(), request.hasRedaction());

    // The named values this statement is planned with (step 26, D152; step 26c, D212) — a core
    // field, read here and put on the extension slate for whichever handler wants it. Absent means
    // execute-time binding, or, for a statement that binds nothing, nothing at all: an empty
    // binding installs no ctx schema and folds nothing.
    chalk.planner.entitlement.BoundContext boundContext =
        chalk.planner.entitlement.BoundContext.of(request.getContext());
    // Everything else this request asked for that is not core, through the one registry (D212). An
    // extension no handler is registered for is refused here, naming the type URL; nothing below
    // this line knows what any of them is.
    PlanExtensionContext extensionContext = extensions.bind(request);
    extensionContext.put(chalk.planner.entitlement.BoundContext.class, boundContext);

    // Every planning gets a governor now, whatever its options (D240): the scheduler needs a look to
    // know where this planning's turn ends and the next begins, so there is no "nothing can end this
    // search early" fast path left at this level — that zero-cost property was D235's, before a
    // bounded worker pool needed every planning to yield.
    chalk.planner.rpc.v1.PlanningOptions planning = request.getPlanningOptions();
    // The request's own period if it named one, else this sidecar's slice (D243) — the same
    // fallback CHALK_PLANNER_SLICE_MS gives D239's period by default, named once for the process
    // rather than repeated by every caller.
    long samplePeriodMillis =
        planning.getConvergenceSamplePeriodMs() > 0
            ? planning.getConvergenceSamplePeriodMs()
            : sliceMillis;
    chalk.planner.diag.PlanningGovernor governor =
        chalk.planner.diag.PlanningGovernor.forScheduling(
            planning.getTimeBudgetMs(),
            planning.getConvergencePatience(),
            planning.getConvergenceRangeThreshold(),
            planning.getConvergenceEvaluationInterval(),
            samplePeriodMillis);
    if (planning.getStopAtFirstPlan()) {
      // The host's stop was already asked for when this request was written (D236). Marked here
      // rather than waited for: a StopPlanning call can only address a planning that is already in
      // flight, so a stop made before the prepare began has no other way in.
      governor.requestStop();
    }

    long start = System.nanoTime();

    // Incremental narrowing (D233): the base plan's front half, when this sidecar still holds it and
    // this request is planned exactly as that one was. It is a hint and only a hint — the request
    // carries the whole union either way, so a miss converts the statement again and the plan is the
    // same one.
    String fingerprint = fingerprintOf(request, catalog, registry.statisticsVersion(instanceId));
    chalk.planner.plan.NarrowCache.Entry leased =
        narrowing.lease(request.getNarrowFrom(), fingerprint, boundContext);
    PlannerPipeline pipeline =
        leased != null
            ? leased.pipeline()
            : PlannerPipeline.create(
                catalog, policy, conformance, libraries, joinPolicy, boundContext, extensionContext);
    java.util.concurrent.atomic.AtomicBoolean keep = new java.util.concurrent.atomic.AtomicBoolean();
    // The call's own cancellation, and the loss of its session, observed for exactly as long as this
    // request is being planned (D236). gRPC cancels the request Context on both, so one listener
    // covers both — every planning has a governor to tell now (D240).
    io.grpc.Context.CancellationListener cancellation = ignored -> governor.requestCancel();
    io.grpc.Context.current().addListener(cancellation, Runnable::run);
    inFlight.register(planning.getRequestId(), governor);
    try {
      // The whole of this planning — the front half, the optimiser, the IR conversion — runs on the
      // scheduler's virtual thread, never on this gRPC call's own (D240); this call blocks on it,
      // which is a wait and not planning, so the pool's own threads still never plan.
      return scheduler.run(
          governor,
          priorityOf(planning.getPriority()),
          planning.getSessionName(),
          planning.getSessionMaxConcurrency(),
          () -> {
            PlannerPipeline.Front front =
                leased != null ? leased.front() : pipeline.front(request.getSql());
            // The statement's own redaction, computed once and used twice (D262): the text the
            // response carries, and — when the request also asked for plan text — the seed the
            // plan's own literals are keyed by, so a host reads one pseudonym for one value
            // wherever it is logged.
            chalk.planner.redact.SqlRedactor.@Nullable Result redacted =
                redaction == null
                    ? null
                    : chalk.planner.redact.SqlRedactor.redact(
                        request.getSql(), conformance, redaction);
            PlannerPipeline.Result result =
                pipeline.finish(
                    front,
                    boundContext,
                    options.getIncludePlanText(),
                    leased == null ? null : String.format("%016x", request.getNarrowFrom()),
                    governor,
                    redacted == null || !options.getIncludePlanText()
                        ? null
                        : new chalk.planner.redact.PlanTextRedactor(
                            chalk.planner.redact.SqlRedactor.pseudonyms(
                                redacted.structuralHash(), redaction),
                            redaction));

            long convertStart = System.nanoTime();
            RelDataType parameterRowType = result.parameterRowType();
            RelToIr toIr =
                new RelToIr(
                    new chalk.planner.types.TypeMapper(
                        result.physical().getCluster().getTypeFactory()),
                    result.physical().getCluster().getRexBuilder(),
                    RelMetadataQuery.instance(),
                    new IrVersionGate(clientIrVersion));
            Plan plan =
                toIr.toPlan(result.physical(), parameterRowType, catalog.contextId(), catalog.epoch());
            long irMicros = (System.nanoTime() - convertStart) / 1_000L;

            PlanningStats.Builder stats =
                PlanningStats.newBuilder()
                    .setTotalMicros((System.nanoTime() - start) / 1_000L)
                    .setParseMicros(result.parseMicros())
                    .setValidateMicros(result.validateMicros())
                    .setConvertMicros(result.convertMicros() + irMicros)
                    .setOptimizeMicros(result.optimizeMicros());
            if (options.getIncludePlanText()) {
              stats.addAllRulesFired(result.rulesFired());
            }
            stats.addAllStages(result.stages());

            PlanResponse.Builder response =
                PlanResponse.newBuilder()
                    .setPlan(plan)
                    .setStats(stats)
                    // How this planning ended, always (D237). A request that carried no option gets
                    // the times, the reason CONVERGED and no costs, because nothing sampled any.
                    .setPlanningState(
                        planningState(
                            result.planningState(),
                            (System.nanoTime() - start) / 1_000L,
                            result.optimizeMicros(),
                            planning.getSessionName()));
            if (redacted != null) {
              // The statement's literals as keyed pseudonyms, for a host that logs statement text
              // (D262). The request's own SQL is redacted — the text the host wrote, after the
              // client's parameter rewrite — and not the validated tree, whose identifiers are
              // expanded and whose star is gone; ADR 0043 §3 records why that means one more parse.
              response.setRedactedSql(redacted.redactedSql());
            }
            if (options.getIncludePlanText()) {
              // The stats block, and only for a request that asked for a governed search (D237): a
              // plan text that nothing could have cut short says nothing about it, so every plan
              // text recorded before this option existed is byte-identical to the one it was.
              response.setPlanText(
                  planningBlock(result.planningState())
                      + "-- logical\n"
                      + result.logicalPlanText()
                      + "-- physical\n"
                      + result.physicalPlanText());
            }
            // What each handler has to say, into the one response slot (D212). A handler whose
            // feature did not run contributes nothing, so a plain plan's response carries no
            // extension at all — which is what "no bytes when unused" means on this side of the
            // call.
            extensionContext.put(PlannerPipeline.Result.class, result);
            extensionContext.put(Plan.class, plan);
            extensions.afterConversion(extensionContext);
            response.addAllExtensions(extensions.onResponse(extensionContext));

            // Only a plan with an open half can be narrowed, so only such a plan is worth
            // retaining: a catalog with no entitlement and a context with nothing left open retain
            // nothing at all, which is §0's zero-cost property at this level (D232, D233).
            if (leased != null) {
              pipeline.releaseSearchSpace(front);
              narrowing.release(leased, plan.getPlanDigest());
              keep.set(true);
            } else if (boundContext.anyShape()) {
              boolean retained =
                  narrowing.retain(plan.getPlanDigest(), pipeline, front, fingerprint, boundContext);
              keep.set(retained);
              if (retained) {
                // What is kept is the converted tree and the cluster that owns it, not the
                // optimiser's search space, which is the largest thing in a pipeline and of no use
                // to a narrowing.
                pipeline.releaseSearchSpace(front);
              }
            }
            return response.build();
          });
    } finally {
      io.grpc.Context.current().removeListener(cancellation);
      inFlight.release(planning.getRequestId(), governor);
      if (leased != null) {
        // The cache owns this pipeline either way; what a failed narrowing must not do is leave the
        // entry leased for ever, which would make the base unreachable until it was evicted.
        if (!keep.get()) {
          narrowing.release(leased, 0L);
        }
      } else if (!keep.get()) {
        pipeline.close();
      }
    }
  }

  /**
   * The plan text's stats block: how the search ended and what it measured, for a governed run, and
   * nothing at all for an ungoverned one (D237).
   */
  private static String planningBlock(PlannerPipeline.PlanningState state) {
    if (!state.governed()) {
      return "";
    }
    StringBuilder line =
        new StringBuilder("-- planning: ")
            .append(state.termination().name().toLowerCase(java.util.Locale.ROOT).replace('_', ' '))
            .append(" after ")
            .append(state.evaluations())
            .append(" evaluations");
    if (state.firstCost() != null && state.bestCost() != null) {
      line.append(", first ")
          .append(state.firstCost())
          .append(", best ")
          .append(state.bestCost())
          .append(String.format(
              java.util.Locale.ROOT, ", ratio %.4f", state.bestCost().divideBy(state.firstCost())));
    }
    return line.append('\n').toString();
  }

  /** The pipeline's own account of a run, on the wire (D237). */
  static chalk.planner.rpc.v1.PlanningState planningState(
      PlannerPipeline.PlanningState state, long elapsedMicros, long optimiserMicros) {
    return planningState(state, elapsedMicros, optimiserMicros, "");
  }

  /** The same, naming the planning session this request ran under (D245, D247), if any. */
  static chalk.planner.rpc.v1.PlanningState planningState(
      PlannerPipeline.PlanningState state, long elapsedMicros, long optimiserMicros, String sessionName) {
    chalk.planner.rpc.v1.PlanningState.Builder built =
        chalk.planner.rpc.v1.PlanningState.newBuilder()
            .setTerminationReason(reason(state.termination()))
            .setElapsedUs(Math.max(0L, elapsedMicros))
            .setOptimiserElapsedUs(Math.max(0L, optimiserMicros))
            .setEvaluationCount(state.evaluations())
            .setLooks(Math.max(0L, state.looks()))
            .setEvaluationInterval(Math.max(0, state.evaluationInterval()))
            .setRunningElapsedUs(Math.max(0L, state.runningElapsedNanos() / 1_000L))
            .setQueuedElapsedUs(Math.max(0L, state.queuedElapsedNanos() / 1_000L))
            .setSlices(Math.max(0L, state.slices()))
            .setSession(sessionName);
    if (state.firstCost() != null) {
      built.setFirstCost(cost(state.firstCost()));
    }
    if (state.bestCost() != null) {
      built.setBestCost(cost(state.bestCost()));
    }
    return built.build();
  }

  private static chalk.planner.rpc.v1.PlanningCost cost(org.apache.calcite.plan.RelOptCost cost) {
    return chalk.planner.rpc.v1.PlanningCost.newBuilder()
        .setRows(cost.getRows())
        .setCpu(cost.getCpu())
        .setIo(cost.getIo())
        .build();
  }

  /**
   * The queue this request is relegated to once its first slice is over (D241): unspecified reads
   * as normal, so a request that names no priority is normal, exactly as the wire comment says.
   */
  private static PlanningScheduler.Priority priorityOf(
      chalk.planner.rpc.v1.PlanningPriority priority) {
    return switch (priority) {
      case PLANNING_PRIORITY_HIGH -> PlanningScheduler.Priority.HIGH;
      case PLANNING_PRIORITY_LOW -> PlanningScheduler.Priority.LOW;
      case PLANNING_PRIORITY_NORMAL, PLANNING_PRIORITY_UNSPECIFIED, UNRECOGNIZED ->
          PlanningScheduler.Priority.NORMAL;
    };
  }

  private static chalk.planner.rpc.v1.PlanningTerminationReason reason(
      chalk.planner.diag.PlanningGovernor.Termination termination) {
    return switch (termination) {
      case CONVERGED -> chalk.planner.rpc.v1.PlanningTerminationReason.PLANNING_TERMINATION_REASON_CONVERGED;
      case BUDGET_EXHAUSTED ->
          chalk.planner.rpc.v1.PlanningTerminationReason.PLANNING_TERMINATION_REASON_BUDGET_EXHAUSTED;
      case STOPPED_BY_HOST ->
          chalk.planner.rpc.v1.PlanningTerminationReason.PLANNING_TERMINATION_REASON_STOPPED_BY_HOST;
      case CANCELLED_BY_HOST ->
          chalk.planner.rpc.v1.PlanningTerminationReason.PLANNING_TERMINATION_REASON_CANCELLED_BY_HOST;
      case ABORTED -> chalk.planner.rpc.v1.PlanningTerminationReason.PLANNING_TERMINATION_REASON_ABORTED;
    };
  }

  /**
   * Everything about this request that the retained front half was built under (D233). A narrowing
   * may reuse a tree only when every one of these is what it was: the statement, the catalog version
   * it was converted against — shape and statistics both, since statistics decide costs and costs
   * decide plans (D271 (c)) — every request option — dialect, libraries, pushdown, join policy — the
   * statement's declared parameter types, and the extensions, which is where the entitlement options
   * the pass reads travel. The context is deliberately absent: being able to plan the same statement
   * with another binding is the whole point.
   */
  private static String fingerprintOf(
      PlanRequest request, RegisteredCatalog catalog, String statisticsVersion) {
    // include_plan_text changes what the response carries and never the plan, so a narrowing that
    // asks for the text may still start from a tree converted without it.
    PlannerOptions options =
        request.getOptions().toBuilder().setIncludePlanText(false).build();
    return catalog.contextId()
        + "\u0000"
        + catalog.epoch()
        + "\u0000"
        + request.getShapeVersion()
        + "\u0000"
        + statisticsVersion
        + "\u0000"
        + request.getSql()
        + "\u0000"
        + options
        + "\u0000"
        + request.getParameterTypesList()
        + "\u0000"
        + request.getExtensionsList()
        + "\u0000"
        + request.getClientIrVersion();
  }
}
