package chalk.planner.rpc;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.IrVersion;
import chalk.planner.CorpusQueries;
import chalk.planner.TestCatalogs;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.ext.PlanExtensionRegistry;
import chalk.planner.rpc.v1.PlanRequest;
import chalk.planner.rpc.v1.PlanResponse;
import chalk.planner.rpc.v1.PlannerOptions;
import chalk.planner.rpc.v1.PlanningOptions;
import chalk.planner.rpc.v1.PlanningPriority;
import chalk.planner.rpc.v1.PushdownLevel;
import java.util.List;
import java.util.concurrent.Callable;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

/**
 * The bounded worker pool that time-slices planning (docs/design/30-planning-options.md, D240,
 * D241): a slice runs from one look to the next, yielding is parking, the Volcano search is never
 * re-entered, and three queues are served 16 : 4 : 1.
 *
 * <p>The slice length is pinned to a handful of rule evaluations under D239's count mode — the test
 * hook this brief asks for, reused rather than invented, because it is already exactly "a slice
 * length expressed in evaluations". Nothing here asserts a duration; {@link Timeout} is a backstop
 * against a genuine hang, and a poll for a state {@code CountDownLatch} exposes is a readiness
 * barrier, never a bound on how long planning takes.
 */
class PlanningSchedulerTest {
  // Short aliases for the sequence assertions below, which are easiest to read set side by side.
  private static final PlanningScheduler.Priority H = PlanningScheduler.Priority.HIGH;
  private static final PlanningScheduler.Priority N = PlanningScheduler.Priority.NORMAL;
  private static final PlanningScheduler.Priority L = PlanningScheduler.Priority.LOW;

  private static final String LONG_STATEMENT =
      "SELECT n.n_name, o.o_orderdate, SUM(l.l_extendedprice * (1 - l.l_discount)) AS revenue"
          + " FROM main.customer c, db.orders o, main.lineitem l, db.supplier s, main.nation n"
          + " WHERE c.c_custkey = o.o_custkey AND l.l_orderkey = o.o_orderkey"
          + " AND l.l_suppkey = s.s_suppkey AND s.s_nationkey = n.n_nationkey"
          + " AND o.o_orderdate >= DATE '1994-01-01'"
          + " GROUP BY n.n_name, o.o_orderdate ORDER BY revenue DESC, n.n_name";

  private static final chalk.ir.v1.CatalogContext CATALOG_DESCRIPTOR =
      TestCatalogs.withRemote(
          "db",
          TestCatalogs.fullSqlCapabilities().setSupportsValuesJoin(true).build(),
          TestCatalogs.duckDbProfile());

  /**
   * The whole corpus, planned through the RPC layer under W = 1 (sliced into 3-evaluation turns)
   * and again under W = 8 (a slice so large nothing ever yields): every plan is byte-identical
   * (D240, D243). Slicing changes when a search runs, never what it decides.
   */
  @Test
  @Timeout(120)
  void the_corpus_is_byte_identical_under_w1_sliced_and_w8_unsliced() throws Exception {
    List<CorpusQueries.Query> corpus = new java.util.ArrayList<>();
    corpus.addAll(CorpusQueries.all());
    corpus.addAll(CorpusQueries.m2());
    corpus.addAll(CorpusQueries.m3());
    assertThat(corpus).isNotEmpty();

    try (PlanningScheduler wide = new PlanningScheduler(8);
        PlanningScheduler narrow = new PlanningScheduler(1)) {
      CatalogRegistry wideRegistry = new CatalogRegistry();
      RegisteredCatalog wideCatalog = wideRegistry.register(TestCatalogs.corpus());
      PlannerServiceImpl wideService =
          new PlannerServiceImpl(wideRegistry, PlanExtensionRegistry.defaults(), wide);

      CatalogRegistry narrowRegistry = new CatalogRegistry();
      RegisteredCatalog narrowCatalog = narrowRegistry.register(TestCatalogs.corpus());
      PlannerServiceImpl narrowService =
          new PlannerServiceImpl(narrowRegistry, PlanExtensionRegistry.defaults(), narrow);

      for (CorpusQueries.Query query : corpus) {
        PlanResponse baseline =
            corpusPlan(wideService, wideCatalog, query, 1_000_000);
        PlanResponse sliced = corpusPlan(narrowService, narrowCatalog, query, 3);

        assertThat(sliced.getPlan().toByteArray())
            .as("%s: sliced under W=1 vs unsliced under W=8", query.name())
            .isEqualTo(baseline.getPlan().toByteArray());
        assertThat(sliced.getPlan().getPlanDigest()).isEqualTo(baseline.getPlan().getPlanDigest());
      }
    }
  }

  private static PlanResponse corpusPlan(
      PlannerServiceImpl service, RegisteredCatalog catalog, CorpusQueries.Query query, int interval)
      throws Exception {
    PlanRequest.Builder request =
        PlanRequest.newBuilder()
            .setSql(query.sql())
            .setContextId(catalog.contextId())
            .setCatalogEpoch(catalog.epoch())
            .setClientIrVersion(IrVersion.CURRENT)
            .setOptions(
                PlannerOptions.newBuilder()
                    .setPushdown(PushdownLevel.PUSHDOWN_LEVEL_FULL)
                    .setConformanceValue(wireConformance(query.conformance()))
                    .addAllLibrariesValue(
                        query.libraries().stream().map(PlanningSchedulerTest::wireLibrary).toList()))
            .setPlanningOptions(
                PlanningOptions.newBuilder()
                    .setConvergenceEvaluationInterval(interval)
                    .setPriority(PlanningPriority.PLANNING_PRIORITY_NORMAL));
    return service.planOrThrow(request.build());
  }

  /** The wire number for a corpus query's conformance (D34) — {@link SqlConfigs#conformance(int)}, inverted. */
  private static int wireConformance(org.apache.calcite.sql.validate.SqlConformanceEnum conformance) {
    return switch (conformance) {
      case DEFAULT -> 0;
      case LENIENT -> 2;
      case BABEL -> 3;
      case STRICT_92 -> 4;
      case STRICT_99 -> 5;
      case PRAGMATIC_99 -> 6;
      case STRICT_2003 -> 7;
      case PRAGMATIC_2003 -> 8;
      case MYSQL_5 -> 9;
      case ORACLE_10 -> 10;
      case ORACLE_12 -> 11;
      case SQL_SERVER_2008 -> 12;
      case PRESTO -> 13;
      case BIG_QUERY -> 14;
      default ->
          throw new IllegalArgumentException("no wire value in this corpus test for " + conformance);
    };
  }

  /** The wire number for a corpus query's library (D60) — {@link SqlConfigs#libraries}, inverted. */
  private static int wireLibrary(org.apache.calcite.sql.fun.SqlLibrary library) {
    return switch (library) {
      case BIG_QUERY -> 2;
      case CALCITE -> 3;
      case CLICKHOUSE -> 4;
      case HIVE -> 5;
      case MSSQL -> 6;
      case MYSQL -> 7;
      case ORACLE -> 8;
      case POSTGRESQL -> 9;
      case REDSHIFT -> 10;
      case SNOWFLAKE -> 11;
      case SPARK -> 12;
      case SPATIAL -> 13;
      case ALL -> 14;
      default ->
          throw new IllegalArgumentException("no wire value in this corpus test for " + library);
    };
  }

  /** The worker count in force is readable off GetInfo (D243), whatever the scheduler was built with. */
  @Test
  void get_info_reports_the_worker_count_in_force() throws Exception {
    try (PlanningScheduler scheduler = new PlanningScheduler(3)) {
      CatalogRegistry registry = new CatalogRegistry();
      PlannerServiceImpl service =
          new PlannerServiceImpl(registry, PlanExtensionRegistry.defaults(), scheduler);

      java.util.concurrent.atomic.AtomicReference<chalk.planner.rpc.v1.GetInfoResponse> captured =
          new java.util.concurrent.atomic.AtomicReference<>();
      service.getInfo(
          chalk.planner.rpc.v1.GetInfoRequest.getDefaultInstance(),
          new io.grpc.stub.StreamObserver<>() {
            @Override
            public void onNext(chalk.planner.rpc.v1.GetInfoResponse value) {
              captured.set(value);
            }

            @Override
            public void onError(Throwable t) {}

            @Override
            public void onCompleted() {}
          });

      assertThat(captured.get().getPlanningWorkers()).isEqualTo(3);
    }
  }

  /**
   * A single worker and a slice of three evaluations: this statement's ~192 evaluations cross many
   * dozen slice boundaries, each one parking and resuming the very same virtual thread. What comes
   * back is byte-identical to a run with a wide pool and a slice so large it never yields — D240's
   * whole claim, that slicing changes when a search runs and never what it decides.
   */
  @Test
  @Timeout(30)
  void a_sliced_planning_yields_resumes_and_plans_the_same_plan() throws Exception {
    PlanResponse baseline;
    try (PlanningScheduler wide = new PlanningScheduler(8)) {
      baseline = planOnce(wide, 1_000_000);
    }

    PlanResponse sliced;
    try (PlanningScheduler narrow = new PlanningScheduler(1)) {
      sliced = planOnce(narrow, 3);
    }

    assertThat(sliced.getPlan().toByteArray()).isEqualTo(baseline.getPlan().toByteArray());
    assertThat(sliced.getPlan().getPlanDigest()).isEqualTo(baseline.getPlan().getPlanDigest());
    // A search this shallow a slice of 3 evaluations crosses many dozen times: this is the proof
    // the search actually ran under the sliced scheduler and was not somehow short-circuited.
    assertThat(sliced.getPlanningState().getEvaluationCount()).isGreaterThan(50);
    assertThat(sliced.getPlanningState().getTerminationReason())
        .isEqualTo(chalk.planner.rpc.v1.PlanningTerminationReason.PLANNING_TERMINATION_REASON_CONVERGED);
  }

  /**
   * Two plannings at once under one worker: only one runs at a time, and both still finish with the
   * plan an unsliced run produces. Proves the scheduler multiplexes correctly, not just that one
   * planning can yield to itself when nothing else is queued.
   */
  @Test
  @Timeout(30)
  void two_concurrent_plannings_under_one_worker_both_finish_correctly() throws Exception {
    PlanResponse baseline;
    try (PlanningScheduler wide = new PlanningScheduler(8)) {
      baseline = planOnce(wide, 1_000_000);
    }

    try (PlanningScheduler narrow = new PlanningScheduler(1)) {
      var pool = java.util.concurrent.Executors.newFixedThreadPool(2);
      try {
        var first = pool.submit(() -> planOnce(narrow, 3));
        var second = pool.submit(() -> planOnce(narrow, 4));

        PlanResponse a = first.get();
        PlanResponse b = second.get();

        assertThat(a.getPlan().toByteArray()).isEqualTo(baseline.getPlan().toByteArray());
        assertThat(b.getPlan().toByteArray()).isEqualTo(baseline.getPlan().toByteArray());
      } finally {
        pool.shutdownNow();
      }
    }
  }

  /**
   * The 16 : 4 : 1 schedule over a synthetic mix (D241): a supply of high, normal and low tickets
   * that never actually run anything, so the sequence is a pure function of the selection logic —
   * asserted, never timed. Abundant supply in all three queues draws exactly 16, 4 and 1 in the
   * first 21 grants, in the scheduler's own evenly interleaved order.
   */
  @Test
  void the_1641_schedule_over_a_synthetic_mix_of_high_normal_and_low() {
    try (PlanningScheduler scheduler = new PlanningScheduler(1)) {
      List<PlanningScheduler.Priority> sequence =
          scheduler.simulateGrantOrderForTest(100, 100, 100, 21);

      // Evenly interleaved, not clumped: the low turn is not stranded at one end, and no single
      // queue is ever served many times in a row. Asserted verbatim, not merely by count.
      assertThat(sequence)
          .containsExactly(
              H, N, L, H, H, H, H, N, H, H, H, H, N, H, H, H, H, N, H, H, H);
    }
  }

  /**
   * A queue with nothing in it gives its share to the others (D241): with no low supply at all, 21
   * grants are exactly high and normal in the same 4-to-16 proportion the schedule already gives
   * them, and every grant still comes from a queue that actually had something in it.
   */
  @Test
  void an_empty_queues_share_passes_to_the_others() {
    try (PlanningScheduler scheduler = new PlanningScheduler(1)) {
      List<PlanningScheduler.Priority> sequence =
          scheduler.simulateGrantOrderForTest(100, 100, 0, 21);

      // The low queue's one slot in the schedule now falls through to high, the next non-empty
      // queue in the schedule's own order — not to normal, and not skipped outright.
      assertThat(sequence)
          .containsExactly(H, N, H, H, H, H, N, H, H, H, H, N, H, H, H, H, N, H, H, H, H);
    }
  }

  /**
   * A finite synthetic mix runs out: once every queue is empty, no more grants come, whatever is
   * still asked for (D241) — the schedule never invents a ticket that was not there.
   */
  @Test
  void the_schedule_stops_once_every_queue_is_empty() {
    try (PlanningScheduler scheduler = new PlanningScheduler(1)) {
      List<PlanningScheduler.Priority> sequence = scheduler.simulateGrantOrderForTest(2, 1, 1, 21);

      assertThat(sequence).containsExactly(H, N, L, H);
    }
  }

  /**
   * K heavy plannings submitted, then one light one: the light one completes before any heavy one —
   * a deterministic consequence of D241 (every planning is served high for its first slice, and a
   * light query fits in that one slice), not a timing. Heavy needs about 64 slices of 3 evaluations
   * each over a ~192-evaluation search; light needs one, whatever slice it lands in.
   */
  @Test
  @Timeout(30)
  void k_heavy_plannings_then_one_light_the_light_finishes_first() throws Exception {
    int heavyCount = 4;
    ExecutorService pool = Executors.newFixedThreadPool(heavyCount + 1);
    try (PlanningScheduler scheduler = new PlanningScheduler(1)) {
      List<Future<PlanResponse>> heavies = new java.util.ArrayList<>();
      for (int i = 0; i < heavyCount; i++) {
        heavies.add(pool.submit(() -> planOnce(scheduler, 3)));
      }
      // Not raced: light needing one slice against each heavy needing ~64 means light finishes
      // first whether it arrives just after the first heavy's first slice or well behind it in the
      // high queue — the assertion below is about relative progress, not about who got there when.
      Future<PlanResponse> light = pool.submit(() -> planTrivial(scheduler));

      PlanResponse lightResult = light.get();
      assertThat(lightResult.getPlan().hasRoot()).isTrue();
      for (Future<PlanResponse> heavy : heavies) {
        assertThat(heavy.isDone())
            .as("a heavy planning needing ~64 slices should not yet be done when light, needing"
                + " one, has just finished")
            .isFalse();
      }
      for (Future<PlanResponse> heavy : heavies) {
        heavy.get();
      }
    } finally {
      pool.shutdownNow();
    }
  }

  /**
   * A low-priority planning under sustained high-priority load still completes (D241): the low
   * queue's one turn in twenty-one always comes, so a low-priority search that needs many slices
   * makes progress and finishes even while several high-priority searches keep the pool busy. No
   * aging is needed for this — the schedule's own ratio is the whole guarantee.
   */
  @Test
  @Timeout(30)
  void a_low_priority_planning_under_sustained_high_load_still_completes() throws Exception {
    int highCount = 3;
    ExecutorService pool = Executors.newFixedThreadPool(highCount + 1);
    try (PlanningScheduler scheduler = new PlanningScheduler(1)) {
      List<Future<PlanResponse>> highLoad = new java.util.ArrayList<>();
      for (int i = 0; i < highCount; i++) {
        highLoad.add(pool.submit(() -> planOnce(scheduler, PlanningPriority.PLANNING_PRIORITY_HIGH, 3)));
      }
      Future<PlanResponse> low =
          pool.submit(() -> planOnce(scheduler, PlanningPriority.PLANNING_PRIORITY_LOW, 3));

      PlanResponse lowResult = low.get();
      assertThat(lowResult.getPlan().hasRoot()).isTrue();
      assertThat(lowResult.getPlanningState().getTerminationReason())
          .isEqualTo(chalk.planner.rpc.v1.PlanningTerminationReason.PLANNING_TERMINATION_REASON_CONVERGED);
      for (Future<PlanResponse> high : highLoad) {
        high.get();
      }
    } finally {
      pool.shutdownNow();
    }
  }

  /**
   * A stop of a parked planning is granted a permit at once, ahead of a planning that arrived first
   * and would otherwise have gone next (D241) — proved with synthetic bodies under the scheduler's
   * real park/grant machinery, not a governed search, so which one is "first" is unambiguous.
   */
  @Test
  @Timeout(30)
  void a_stop_of_a_parked_planning_is_granted_ahead_of_an_earlier_arrival() throws Exception {
    orderOfTwoParkedArrivals(/* cancel= */ false);
  }

  /** The same, for a cancel (D241, D236): the flag is different, the scheduling is identical. */
  @Test
  @Timeout(30)
  void a_cancel_of_a_parked_planning_is_granted_ahead_of_an_earlier_arrival() throws Exception {
    orderOfTwoParkedArrivals(/* cancel= */ true);
  }

  /**
   * A planning parked past its own budget still plans (D242): the budget is charged only against
   * running time, so however long a search waits behind another one, none of that wait counts
   * against it. The budget (500 ms) and the occupier's hold (3 s, six times over) are both real,
   * generous margins — the budget is far more running time than parsing, validating, converting and
   * optimising this statement needs even on a cold, unwarmed JVM, and the hold is far longer than
   * that budget — so the queued time genuinely and unmistakably exceeds it; nothing here asserts a
   * duration, only the outcome: had the wait counted, this would have aborted, and it does not.
   */
  @Test
  @Timeout(30)
  void a_planning_parked_past_its_budget_still_plans() throws Exception {
    ExecutorService pool = Executors.newFixedThreadPool(2);
    try (PlanningScheduler scheduler = new PlanningScheduler(1)) {
      CountDownLatch occupierStarted = new CountDownLatch(1);
      chalk.planner.diag.PlanningGovernor occupierGovernor =
          chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0);
      Future<?> occupier =
          pool.submit(
              () ->
                  scheduler.run(
                      occupierGovernor,
                      PlanningScheduler.Priority.NORMAL,
                      () -> {
                        occupierStarted.countDown();
                        Thread.sleep(3_000);
                        return null;
                      }));
      occupierStarted.await();

      Future<PlanResponse> target =
          pool.submit(
              () -> {
                CatalogRegistry registry = new CatalogRegistry();
                RegisteredCatalog catalog = registry.register(CATALOG_DESCRIPTOR);
                PlannerServiceImpl service =
                    new PlannerServiceImpl(registry, PlanExtensionRegistry.defaults(), scheduler);
                PlanRequest request =
                    PlanRequest.newBuilder()
                        .setSql(LONG_STATEMENT)
                        .setContextId(catalog.contextId())
                        .setCatalogEpoch(catalog.epoch())
                        .setClientIrVersion(IrVersion.CURRENT)
                        .setOptions(
                            PlannerOptions.newBuilder().setPushdown(PushdownLevel.PUSHDOWN_LEVEL_FULL))
                        .setPlanningOptions(
                            PlanningOptions.newBuilder()
                                .setTimeBudgetMs(500)
                                .setPriority(PlanningPriority.PLANNING_PRIORITY_NORMAL))
                        .build();
                return service.planOrThrow(request);
              });

      PlanResponse response = target.get();
      assertThat(response.getPlan().hasRoot()).isTrue();
      assertThat(response.getPlanningState().getTerminationReason())
          .isEqualTo(chalk.planner.rpc.v1.PlanningTerminationReason.PLANNING_TERMINATION_REASON_CONVERGED);
      // Wired end to end, not merely zero by omission.
      assertThat(response.getPlanningState().getQueuedElapsedUs()).isPositive();
      assertThat(response.getPlanningState().getRunningElapsedUs()).isPositive();
      assertThat(response.getPlanningState().getSlices()).isPositive();

      occupier.get();
    } finally {
      pool.shutdownNow();
    }
  }

  /**
   * One worker, one occupier holding it, two more tickets parked behind it in arrival order; the
   * second of the two is stopped or cancelled while still parked, and is proved to run before the
   * first once the occupier lets go.
   */
  private static void orderOfTwoParkedArrivals(boolean cancel) throws Exception {
    try (PlanningScheduler scheduler = new PlanningScheduler(1)) {
      ExecutorService pool = Executors.newFixedThreadPool(3);
      try {
        java.util.concurrent.CopyOnWriteArrayList<String> ranOrder =
            new java.util.concurrent.CopyOnWriteArrayList<>();
        CountDownLatch occupierStarted = new CountDownLatch(1);
        CountDownLatch occupierGo = new CountDownLatch(1);

        chalk.planner.diag.PlanningGovernor occupierGovernor =
            chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0);
        Future<?> occupier =
            pool.submit(
                () ->
                    scheduler.run(
                        occupierGovernor,
                        PlanningScheduler.Priority.NORMAL,
                        () -> {
                          occupierStarted.countDown();
                          occupierGo.await();
                          return null;
                        }));
        // A signal, not a poll: the occupier's body only runs once it holds the sole permit, so
        // this is the point from which submitting anything else is guaranteed to find it taken.
        occupierStarted.await();

        chalk.planner.diag.PlanningGovernor earlyGovernor =
            chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0);
        Future<?> early =
            pool.submit(
                () ->
                    scheduler.run(
                        earlyGovernor,
                        PlanningScheduler.Priority.NORMAL,
                        () -> {
                          ranOrder.add("early");
                          return null;
                        }));
        waitUntilQueued(scheduler, 1);

        chalk.planner.diag.PlanningGovernor stoppedGovernor =
            chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0);
        Future<?> stopped =
            pool.submit(
                () ->
                    scheduler.run(
                        stoppedGovernor,
                        PlanningScheduler.Priority.NORMAL,
                        () -> {
                          ranOrder.add("stopped");
                          return null;
                        }));
        waitUntilQueued(scheduler, 2);

        if (cancel) {
          stoppedGovernor.requestCancel();
        } else {
          stoppedGovernor.requestStop();
        }
        // The expedite is synchronous (it takes the scheduler's lock), so by the time it returns
        // the ticket has already moved to the front of the line; only the occupier's own release
        // remains before it is granted.
        occupierGo.countDown();

        occupier.get();
        stopped.get();
        early.get();

        assertThat(ranOrder).containsExactly("stopped", "early");
      } finally {
        pool.shutdownNow();
      }
    }
  }

  /** Polls a state, not a duration: returns as soon as at least {@code atLeast} are queued. */
  private static void waitUntilQueued(PlanningScheduler scheduler, int atLeast) {
    while (scheduler.queuedForTest() < atLeast) {
      Thread.onSpinWait();
    }
  }

  /**
   * A session capped at one bounds how many of its plannings hold a permit at once (D245, D246):
   * with a second worker free, a planning that names no session and arrives after the session's
   * second is granted the free permit ahead of it — a session's own cap is the only thing that ever
   * holds one of its plannings back, never anyone else's turn.
   */
  @Test
  @Timeout(30)
  void a_session_capped_at_one_bounds_its_own_concurrency_and_lets_others_pass() throws Exception {
    ExecutorService pool = Executors.newFixedThreadPool(4);
    try (PlanningScheduler scheduler = new PlanningScheduler(2)) {
      CountDownLatch session1Started = new CountDownLatch(1);
      CountDownLatch releaseSession1 = new CountDownLatch(1);
      chalk.planner.diag.PlanningGovernor session1Governor =
          chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0);
      Future<?> session1 =
          pool.submit(
              () ->
                  scheduler.run(
                      session1Governor,
                      PlanningScheduler.Priority.NORMAL,
                      "s",
                      1,
                      () -> {
                        session1Started.countDown();
                        releaseSession1.await();
                        return null;
                      }));
      session1Started.await();
      assertThat(scheduler.sessionCapForTest("s")).isEqualTo(1);

      CountDownLatch session2Ran = new CountDownLatch(1);
      chalk.planner.diag.PlanningGovernor session2Governor =
          chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0);
      Future<?> session2 =
          pool.submit(
              () ->
                  scheduler.run(
                      session2Governor,
                      PlanningScheduler.Priority.NORMAL,
                      "s",
                      1,
                      () -> {
                        session2Ran.countDown();
                        return null;
                      }));
      waitUntilQueued(scheduler, 1);

      // A free permit exists (W=2, only session1 holds one) but session2 cannot take it — it stays
      // parked, in place, while a later, unrelated arrival is granted the free permit instead.
      CountDownLatch uncappedStarted = new CountDownLatch(1);
      CountDownLatch releaseUncapped = new CountDownLatch(1);
      chalk.planner.diag.PlanningGovernor uncappedGovernor =
          chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0);
      Future<?> uncapped =
          pool.submit(
              () ->
                  scheduler.run(
                      uncappedGovernor,
                      PlanningScheduler.Priority.NORMAL,
                      () -> {
                        uncappedStarted.countDown();
                        releaseUncapped.await();
                        return null;
                      }));
      uncappedStarted.await();

      assertThat(session2Ran.getCount()).as("session2 must still be capped out").isEqualTo(1L);

      releaseUncapped.countDown();
      uncapped.get();

      // session2 is still capped out: session1 has not released yet, and the freed permit finds
      // nothing else eligible.
      assertThat(session2Ran.getCount()).as("session2 still capped out after uncapped finished")
          .isEqualTo(1L);

      releaseSession1.countDown();
      session1.get();
      session2.get();
    } finally {
      pool.shutdownNow();
    }
  }

  /**
   * Two independent submissions naming the same session — as two gRPC channels would (D245) — share
   * one cap: the second cannot exceed what the first already committed it to.
   */
  @Test
  @Timeout(30)
  void two_independent_submissions_naming_one_session_share_its_cap() throws Exception {
    ExecutorService pool = Executors.newFixedThreadPool(2);
    try (PlanningScheduler scheduler = new PlanningScheduler(4)) {
      CountDownLatch firstStarted = new CountDownLatch(1);
      CountDownLatch releaseFirst = new CountDownLatch(1);
      chalk.planner.diag.PlanningGovernor firstGovernor =
          chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0);
      Future<?> first =
          pool.submit(
              () ->
                  scheduler.run(
                      firstGovernor,
                      PlanningScheduler.Priority.NORMAL,
                      "shared",
                      1,
                      () -> {
                        firstStarted.countDown();
                        releaseFirst.await();
                        return null;
                      }));
      firstStarted.await();

      CountDownLatch secondRan = new CountDownLatch(1);
      chalk.planner.diag.PlanningGovernor secondGovernor =
          chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0);
      Future<?> second =
          pool.submit(
              () ->
                  // A second, unrelated submission of the same name: the cap it names (0, saying
                  // nothing new) does not matter — the registered cap of 1 from the first is what
                  // holds, exactly as if this arrived on a different channel.
                  scheduler.run(
                      secondGovernor,
                      PlanningScheduler.Priority.NORMAL,
                      "shared",
                      0,
                      () -> {
                        secondRan.countDown();
                        return null;
                      }));
      waitUntilQueued(scheduler, 1);
      assertThat(secondRan.getCount()).isEqualTo(1L);

      releaseFirst.countDown();
      first.get();
      second.get();
    } finally {
      pool.shutdownNow();
    }
  }

  /** A later request naming the same session with a cap of zero leaves the registered cap as it is. */
  @Test
  void a_later_zero_cap_leaves_the_registered_cap_unchanged() throws Exception {
    try (PlanningScheduler scheduler = new PlanningScheduler(4)) {
      scheduler.run(
          chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0),
          PlanningScheduler.Priority.NORMAL,
          "quota",
          2,
          () -> null);
      assertThat(scheduler.sessionCapForTest("quota")).isEqualTo(2);

      scheduler.run(
          chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0),
          PlanningScheduler.Priority.NORMAL,
          "quota",
          0,
          () -> null);
      assertThat(scheduler.sessionCapForTest("quota")).as("cap 0 leaves the registered cap").isEqualTo(2);

      scheduler.run(
          chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0),
          PlanningScheduler.Priority.NORMAL,
          "quota",
          5,
          () -> null);
      assertThat(scheduler.sessionCapForTest("quota"))
          .as("a different non-zero cap replaces it")
          .isEqualTo(5);
    }
  }

  /** A cap at or above the worker count is no cap at all (D246). */
  @Test
  @Timeout(30)
  void a_cap_at_the_worker_count_behaves_as_uncapped() throws Exception {
    ExecutorService pool = Executors.newFixedThreadPool(2);
    try (PlanningScheduler scheduler = new PlanningScheduler(2)) {
      CountDownLatch bothStarted = new CountDownLatch(2);
      CountDownLatch release = new CountDownLatch(1);
      Callable<Void> body =
          () -> {
            bothStarted.countDown();
            release.await();
            return null;
          };

      Future<?> a =
          pool.submit(
              () ->
                  scheduler.run(
                      chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0),
                      PlanningScheduler.Priority.NORMAL,
                      "wide",
                      2,
                      body));
      Future<?> b =
          pool.submit(
              () ->
                  scheduler.run(
                      chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0),
                      PlanningScheduler.Priority.NORMAL,
                      "wide",
                      2,
                      body));

      // A backstop, not a bound this test relies on: both really did run at once, or this times out
      // and fails rather than silently passing on the one that got in first.
      assertThat(bothStarted.await(20, TimeUnit.SECONDS)).isTrue();

      release.countDown();
      a.get();
      b.get();
    } finally {
      pool.shutdownNow();
    }
  }

  /**
   * Below the registry's limit, a session that finished and went fully idle keeps its registered
   * cap: eviction only happens when a new registration would push the registry over the limit, never
   * merely because a session has nothing in flight right now (D245's amendment).
   */
  @Test
  void a_session_that_completed_and_went_idle_keeps_its_cap_below_the_limit() throws Exception {
    try (PlanningScheduler scheduler = new PlanningScheduler(4)) {
      runNamed(scheduler, "idle-session", 3);
      assertThat(scheduler.sessionCapForTest("idle-session")).isEqualTo(3);

      runNamed(scheduler, "idle-session", 0);
      assertThat(scheduler.sessionCapForTest("idle-session"))
          .as("cap 0 on a later request leaves an idle, still-registered session's cap unchanged")
          .isEqualTo(3);
    }
  }

  /**
   * With the limit reached, naming a third distinct idle session evicts the least recently used of
   * the first two, and the registry holds exactly the limit's worth afterward (D245's amendment).
   */
  @Test
  void with_the_limit_reached_the_least_recently_used_idle_session_is_evicted() throws Exception {
    try (PlanningScheduler scheduler = new PlanningScheduler(4, 2)) {
      runNamed(scheduler, "first", 1);
      runNamed(scheduler, "second", 1);
      assertThat(scheduler.sessionRegistrySizeForTest()).isEqualTo(2);

      runNamed(scheduler, "third", 1);

      assertThat(scheduler.sessionRegistrySizeForTest()).isEqualTo(2);
      assertThat(scheduler.sessionEvictionsForTest()).isEqualTo(1);
      assertThat(scheduler.sessionCapForTest("first"))
          .as("the least recently used of the three is the one evicted")
          .isEqualTo(-1);
      assertThat(scheduler.sessionCapForTest("second")).isEqualTo(1);
      assertThat(scheduler.sessionCapForTest("third")).isEqualTo(1);
    }
  }

  /**
   * A session with a planning still queued or running is never evicted, however long it has sat
   * untouched in the access order — the bound skips it and evicts a younger, idle session instead
   * (D245's amendment: "a session in use is never evicted, whatever its age").
   */
  @Test
  @Timeout(30)
  void the_oldest_session_in_use_is_skipped_and_a_younger_idle_one_evicted_instead() throws Exception {
    ExecutorService pool = Executors.newFixedThreadPool(1);
    try (PlanningScheduler scheduler = new PlanningScheduler(4, 2)) {
      CountDownLatch eldestStarted = new CountDownLatch(1);
      CountDownLatch releaseEldest = new CountDownLatch(1);
      Future<?> eldest =
          pool.submit(
              () ->
                  scheduler.run(
                      chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0),
                      PlanningScheduler.Priority.NORMAL,
                      "eldest",
                      1,
                      () -> {
                        eldestStarted.countDown();
                        releaseEldest.await();
                        return null;
                      }));
      eldestStarted.await();

      runNamed(scheduler, "middle", 1);
      assertThat(scheduler.sessionRegistrySizeForTest()).isEqualTo(2);

      runNamed(scheduler, "newcomer", 1);

      assertThat(scheduler.sessionRegistrySizeForTest()).isEqualTo(2);
      assertThat(scheduler.sessionCapForTest("eldest"))
          .as("a session with a planning still running is never evicted, whatever its age")
          .isEqualTo(1);
      assertThat(scheduler.sessionCapForTest("middle"))
          .as("a younger, idle session is evicted in its place")
          .isEqualTo(-1);
      assertThat(scheduler.sessionCapForTest("newcomer")).isEqualTo(1);

      releaseEldest.countDown();
      eldest.get();
    } finally {
      pool.shutdownNow();
    }
  }

  /**
   * An evicted session named again starts over, exactly as if it had never been registered: cap 0
   * runs uncapped rather than resurrecting the cap it had before eviction, and a non-zero cap is
   * registered afresh at that value (D245's amendment). A limit of one makes every later
   * registration evict whatever is there, so each step's eviction is the test's own setup for the
   * next.
   */
  @Test
  void an_evicted_session_named_again_starts_over() throws Exception {
    try (PlanningScheduler scheduler = new PlanningScheduler(4, 1)) {
      runNamed(scheduler, "victim1", 2);
      runNamed(scheduler, "victim2", 5);
      assertThat(scheduler.sessionCapForTest("victim1"))
          .as("registering a second distinct session evicts the first at a limit of one")
          .isEqualTo(-1);

      runNamed(scheduler, "victim1", 0);
      assertThat(scheduler.sessionCapForTest("victim1"))
          .as("an evicted session named again with cap 0 starts over uncapped, not at its old cap")
          .isEqualTo(0);
      assertThat(scheduler.sessionCapForTest("victim2"))
          .as("that registration in turn evicted victim2")
          .isEqualTo(-1);

      runNamed(scheduler, "victim2", 7);
      assertThat(scheduler.sessionCapForTest("victim2"))
          .as("an evicted session named again with a cap is registered afresh at exactly that cap")
          .isEqualTo(7);
    }
  }

  /** Runs a trivial, immediately-completing planning naming {@code session} with cap {@code cap}. */
  private static void runNamed(PlanningScheduler scheduler, String session, int cap)
      throws Exception {
    scheduler.run(
        chalk.planner.diag.PlanningGovernor.forScheduling(0, 0, 0, 0, 0),
        PlanningScheduler.Priority.NORMAL,
        session,
        cap,
        () -> null);
  }

  private static PlanResponse planOnce(PlanningScheduler scheduler, int interval) throws Exception {
    return planOnce(scheduler, PlanningPriority.PLANNING_PRIORITY_NORMAL, interval);
  }


  /** A statement with too few evaluations to need a second slice under any but a tiny interval. */
  private static PlanResponse planTrivial(PlanningScheduler scheduler) throws Exception {
    CatalogRegistry registry = new CatalogRegistry();
    RegisteredCatalog catalog = registry.register(CATALOG_DESCRIPTOR);
    PlannerServiceImpl service =
        new PlannerServiceImpl(registry, PlanExtensionRegistry.defaults(), scheduler);
    PlanRequest request =
        PlanRequest.newBuilder()
            .setSql("SELECT c_custkey FROM main.customer")
            .setContextId(catalog.contextId())
            .setCatalogEpoch(catalog.epoch())
            .setClientIrVersion(IrVersion.CURRENT)
            .setOptions(PlannerOptions.newBuilder().setPushdown(PushdownLevel.PUSHDOWN_LEVEL_FULL))
            .setPlanningOptions(
                PlanningOptions.newBuilder()
                    .setConvergenceEvaluationInterval(1_000_000)
                    .setPriority(PlanningPriority.PLANNING_PRIORITY_NORMAL))
            .build();
    return service.planOrThrow(request);
  }

  private static PlanResponse planOnce(
      PlanningScheduler scheduler, PlanningPriority priority, int interval) throws Exception {
    CatalogRegistry registry = new CatalogRegistry();
    RegisteredCatalog catalog = registry.register(CATALOG_DESCRIPTOR);
    PlannerServiceImpl service =
        new PlannerServiceImpl(registry, PlanExtensionRegistry.defaults(), scheduler);
    PlanRequest request =
        PlanRequest.newBuilder()
            .setSql(LONG_STATEMENT)
            .setContextId(catalog.contextId())
            .setCatalogEpoch(catalog.epoch())
            .setClientIrVersion(IrVersion.CURRENT)
            .setOptions(PlannerOptions.newBuilder().setPushdown(PushdownLevel.PUSHDOWN_LEVEL_FULL))
            .setPlanningOptions(
                PlanningOptions.newBuilder()
                    .setConvergenceEvaluationInterval(interval)
                    .setPriority(priority))
            .build();
    return service.planOrThrow(request);
  }
}
