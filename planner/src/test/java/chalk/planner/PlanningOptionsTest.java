package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.diag.PlanningGovernor;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.TimeUnit;
import org.apache.calcite.plan.RelOptCost;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * Planning options (docs/design/30-planning-options.md, D234–D239): a sampled convergence test, a
 * time budget, the state each produces, and — D239 — looks on a wall-clock cadence by default.
 *
 * <p><b>No test here asserts a duration.</b> The budget's tests assert the reason and the presence
 * of a state; the convergence tests assert the sampled series and the evaluation count, which are
 * functions of the evaluation sequence and therefore the same on every machine. The default cadence
 * is wall-clock by design (D239), so its own tests assert the one thing that is deterministic about
 * it on this repository: a Chalk search finishes before the cadence ever fires, so it samples
 * nothing and reports which mode did not fire — never how long anything took.
 */
class PlanningOptionsTest {
  /**
   * Four joins — the most Volcano enumerates before the heuristic pass takes over (D44) — across two
   * sources, so every side has a remote alternative as well as a local one and the search is the
   * longest this repository has. Under the default interval of 500 it would not be sampled at all,
   * which is exactly why the interval is a host's to choose from a measured evaluation count.
   */
  private static final String LONG_STATEMENT =
      "SELECT n.n_name, o.o_orderdate, SUM(l.l_extendedprice * (1 - l.l_discount)) AS revenue"
          + " FROM main.customer c, db.orders o, main.lineitem l, db.supplier s, main.nation n"
          + " WHERE c.c_custkey = o.o_custkey AND l.l_orderkey = o.o_orderkey"
          + " AND l.l_suppkey = s.s_suppkey AND s.s_nationkey = n.n_nationkey"
          + " AND o.o_orderdate >= DATE '1994-01-01'"
          + " GROUP BY n.n_name, o.o_orderdate ORDER BY revenue DESC, n.n_name";

  private static RegisteredCatalog catalog;

  @BeforeAll
  static void registerCatalog() {
    catalog =
        new CatalogRegistry()
            .register(
                TestCatalogs.withRemote(
                    "db",
                    TestCatalogs.fullSqlCapabilities().setSupportsValuesJoin(true).build(),
                    TestCatalogs.duckDbProfile()));
  }

  /** What one run produced: the plan, without the per-process rel ids, and the state. */
  private record Run(String planText, PlannerPipeline.PlanningState state) {}

  private static Run run(String sql, PlanningGovernor governor) throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            catalog,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            ImmutableList.of())) {
      PlannerPipeline.Front front = pipeline.front(sql);
      PlannerPipeline.Result result =
          pipeline.finish(front, chalk.planner.entitlement.BoundContext.EMPTY, true, null, governor);
      // `explain` and not `explainWithCost`: the latter prints each rel's id, and ids come from a
      // per-process counter, so two runs in one JVM would differ in nothing else.
      return new Run(PlannerPipeline.explain(result.physical()), result.planningState());
    }
  }

  /** The series a reader can quote: the rows component of each sample, in order. */
  static List<String> series(PlannerPipeline.PlanningState state) {
    List<String> lines = new ArrayList<>();
    for (RelOptCost cost : state.samples()) {
      lines.add(String.format(Locale.ROOT, "%.0f", cost.getRows()));
    }
    return lines;
  }

  /**
   * A long search under a small interval and a small patience converges, and says so: the reason,
   * the evaluation count, the first and best costs, and a sampled series whose last samples are
   * flat.
   */
  @Test
  void a_long_search_converges_and_says_so() throws Exception {
    Run converged = run(LONG_STATEMENT, governor(3, 5));

    assertThat(converged.state().termination()).isEqualTo(PlanningGovernor.Termination.CONVERGED);
    assertThat(converged.state().governed()).isTrue();
    assertThat(converged.state().evaluations()).isPositive();
    assertThat(converged.state().firstCost()).isNotNull();
    assertThat(converged.state().bestCost()).isNotNull();
    assertThat(converged.state().samples()).hasSizeGreaterThanOrEqualTo(4);

    // The last `patience` samples are the ones that decided it: each within the threshold of the one
    // before, by Calcite's own divideBy — the geometric mean over the non-zero finite components.
    List<RelOptCost> samples = converged.state().samples();
    for (int i = samples.size() - 3; i < samples.size(); i++) {
      assertThat(Math.abs(samples.get(i).divideBy(samples.get(i - 1)) - 1.0))
          .isLessThanOrEqualTo(0.001);
    }

    // The search bought something before it stopped, and what it stopped at is a complete plan the
    // rest of the pipeline accepted.
    assertThat(converged.state().bestCost().isLt(converged.state().firstCost())).isTrue();
    assertThat(converged.planText()).contains("ChalkHashAggregate");

    // D242, reached without a scheduler: a run nothing ever yields still ran in exactly one slice,
    // and the whole of its own lifetime — there is no queueing outside the scheduler at all — counts
    // as running time.
    assertThat(converged.state().slices()).isEqualTo(1L);
    assertThat(converged.state().runningElapsedNanos()).isPositive();
    assertThat(converged.state().queuedElapsedNanos()).isZero();
  }

  /**
   * The same statement with no options at all: it runs to completion, nothing is sampled, and the
   * plan is at least as good as the one convergence stopped at.
   */
  @Test
  void the_same_statement_with_no_options_runs_to_completion() throws Exception {
    Run full = run(LONG_STATEMENT, null);

    assertThat(full.state().governed()).isFalse();
    assertThat(full.state().termination()).isEqualTo(PlanningGovernor.Termination.CONVERGED);
    assertThat(full.state().evaluations()).isZero();
    assertThat(full.state().firstCost()).isNull();
    assertThat(full.state().bestCost()).isNull();
    assertThat(full.state().samples()).isEmpty();
  }

  /** The same options twice give the same plan: the interval is a count and the samples are fixed. */
  @Test
  void a_convergence_terminated_plan_is_reproducible() throws Exception {
    Run first = run(LONG_STATEMENT, governor(3, 5));
    Run second = run(LONG_STATEMENT, governor(3, 5));

    assertThat(second.planText()).isEqualTo(first.planText());
    assertThat(second.state().evaluations()).isEqualTo(first.state().evaluations());
    assertThat(series(second.state())).isEqualTo(series(first.state()));
  }

  /**
   * A budget spent before the root holds any complete plan is {@code ABORTED} with the state in the
   * error — not a plan, and not a "there are not enough rules".
   */
  @Test
  void a_budget_spent_before_the_first_plan_aborts_with_the_state() {
    // One millisecond is over before the first physical alternative for the root exists. Nothing
    // here asserts how long anything took: what is asserted is that the reason and the state travel.
    PlanningGovernor governor = PlanningGovernor.create(1, 0, 0, 0, false);

    assertThatThrownBy(() -> run(LONG_STATEMENT, governor))
        .isInstanceOf(PlannerPipeline.PlanningAbortedException.class)
        .hasMessageContaining("before the optimiser had a complete plan")
        .satisfies(
            e -> {
              PlannerPipeline.PlanningState state =
                  ((PlannerPipeline.PlanningAbortedException) e).state();
              assertThat(state.termination()).isEqualTo(PlanningGovernor.Termination.ABORTED);
              assertThat(state.governed()).isTrue();
            });
  }

  /**
   * A stop before any complete plan exists arms "end at the first one", so a stop on its own never
   * leaves a request without a plan (D236).
   */
  @Test
  void a_stop_before_the_first_plan_returns_the_first_plan() throws Exception {
    PlanningGovernor governor = PlanningGovernor.create(0, 0, 0, 0, true);
    governor.requestStop();

    Run stopped = run(LONG_STATEMENT, governor);

    assertThat(stopped.state().termination())
        .isEqualTo(PlanningGovernor.Termination.STOPPED_BY_HOST);
    assertThat(stopped.planText()).contains("ChalkHashAggregate");
    // Fewer evaluations than the unstopped search: it ended at the first complete plan.
    assertThat(stopped.state().evaluations())
        .isLessThan(run(LONG_STATEMENT, governor(0, 1)).state().evaluations());
  }

  /** A request whose options cannot end a search early installs no listener and samples nothing. */
  @Test
  void no_governor_is_installed_when_no_option_can_end_a_search() throws Exception {
    assertThat(PlanningGovernor.create(0, 0, 0, 0, false)).isNull();

    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            catalog,
            PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            ImmutableList.of())) {
      PlannerPipeline.Result result = pipeline.plan(LONG_STATEMENT, false);

      assertThat(pipeline.governorInstalled()).isFalse();
      assertThat(result.planningState().governed()).isFalse();
      assertThat(result.planningState().evaluations()).isZero();
      assertThat(result.planningState().firstCost()).isNull();
      assertThat(result.stages()).contains("volcano");
      assertThat(result.stages()).noneMatch(stage -> stage.startsWith("volcano "));
    }
  }

  /** The stage list says how a search ended, and only when something ended it early. */
  @Test
  void the_stage_names_the_reason_only_when_a_search_was_cut_short() throws Exception {
    PlanningGovernor governor = PlanningGovernor.create(0, 0, 0, 0, true);
    governor.requestStop();
    Run stopped = run(LONG_STATEMENT, governor);

    assertThat(PlannerPipeline.stageForVolcano(stopped.state())).isEqualTo("volcano stopped by host");
    // CONVERGED is what a full run reports too, so the stage stays plain for it: what says a search
    // was sampled is the evaluation count and the costs, not the stage.
    assertThat(PlannerPipeline.stageForVolcano(run(LONG_STATEMENT, governor(3, 5)).state()))
        .isEqualTo("volcano");
  }

  /**
   * D239: whether the period or an explicit count governs is decided once, from the options a
   * request carries — not from a run, and not from how long anything takes. An unset interval reads
   * as the period, which reports no count; a set one is the count, whatever period is named beside
   * it, because the count is the reproducible mode and takes over.
   */
  @Test
  void the_interval_reported_says_which_mode_governs() {
    assertThat(PlanningGovernor.create(0, 3, 0.001, 0, true).evaluationInterval())
        .as("no explicit interval: the period governs and there is no count to report")
        .isZero();
    assertThat(PlanningGovernor.create(0, 3, 0.001, 5, true).evaluationInterval())
        .as("an explicit interval governs instead")
        .isEqualTo(5);
    assertThat(PlanningGovernor.create(0, 3, 0.001, 5, 1L, true).evaluationInterval())
        .as("the explicit interval wins even when a period is also named")
        .isEqualTo(5);
  }

  /**
   * With no explicit interval, the period governs by default (D239): the same run a plain prepare
   * gets, still governed because a stop could address it, and it converges by running to completion
   * exactly as an ungoverned run does. What is asserted is the reason and the mode reported, not
   * whether the wall-clock cadence happened to fire — that is a measurement in ADR 0031, not a test.
   */
  @Test
  void with_no_interval_the_period_governs_by_default() throws Exception {
    PlanningGovernor governor = PlanningGovernor.create(0, 0, 0, 0, true);

    Run run = run(LONG_STATEMENT, governor);

    assertThat(run.state().governed()).isTrue();
    assertThat(run.state().termination()).isEqualTo(PlanningGovernor.Termination.CONVERGED);
    assertThat(run.state().evaluations()).isPositive();
    assertThat(governor.evaluationInterval()).isZero();
  }

  /**
   * An explicit interval is the reproducible mode and takes over from the period whatever the period
   * is (D239): the same search, same interval, same series as {@link #a_long_search_converges_and_says_so()}.
   */
  @Test
  void an_explicit_interval_governs_instead_of_the_period() throws Exception {
    PlanningGovernor governor =
        PlanningGovernor.create(0, 3, 0.001, 5, TimeUnit.SECONDS.toMillis(1), true);

    Run run = run(LONG_STATEMENT, governor);

    assertThat(governor.evaluationInterval()).isEqualTo(5);
    // Every look is an interval boundary; not every one finds a complete plan to sample yet, so
    // looks only ever exceeds or matches the samples actually recorded.
    assertThat(governor.looks()).isGreaterThanOrEqualTo(governor.samples().size());
    assertThat(governor.looks()).isPositive();
    assertThat(run.state().termination()).isEqualTo(PlanningGovernor.Termination.CONVERGED);
  }

  /**
   * The corpus with no options is byte-identical to the corpus with no options: every plan, every
   * digest. The assertion that the option costs nobody anything who does not use it (D235).
   */
  @Test
  void the_corpus_with_no_options_is_byte_identical() {
    CorpusPlanner planner = new CorpusPlanner();
    for (CorpusQueries.Query query : corpus()) {
      chalk.ir.v1.Plan first = planner.plan(query);
      chalk.ir.v1.Plan second = planner.plan(query);

      assertThat(second.toByteArray())
          .as("%s: two plans with no planning options", query.name())
          .isEqualTo(first.toByteArray());
      assertThat(second.getPlanDigest()).isEqualTo(first.getPlanDigest());
    }
  }

  /**
   * The corpus under a generous convergence setting — a patience and an interval far larger than any
   * corpus search reaches, so nothing can converge — is the same corpus, plan for plan.
   *
   * <p>That is the strongest form of the claim D235 makes about the sampling itself: reading the
   * root's best cost every so often is an observation, and an observation must not move the answer.
   * No corpus statement fires enough rule matches to reach even one sample at this interval (the
   * widest is 75 — see ADR 0029's measurement), so the search is also untouched by the convergence
   * test, and every difference this could find would be one the sampling itself caused.
   */
  @Test
  void the_corpus_under_a_generous_convergence_setting_is_the_same_corpus() throws Exception {
    CorpusPlanner planner = new CorpusPlanner();
    RegisteredCatalog corpusCatalog = planner.catalog();
    for (CorpusQueries.Query query : corpus()) {
      chalk.ir.v1.Plan plain = planner.plan(query);
      chalk.ir.v1.Plan governed =
          planWithGovernor(corpusCatalog, query, PlanningGovernor.create(0, 50, 0.001, 1000, true));

      assertThat(governed.toByteArray())
          .as("%s: generous convergence vs no options", query.name())
          .isEqualTo(plain.toByteArray());
    }
  }

  /** The corpus queries this test plans: the milestones whose searches are the longest. */
  private static List<CorpusQueries.Query> corpus() {
    List<CorpusQueries.Query> queries = new ArrayList<>();
    queries.addAll(CorpusQueries.all());
    queries.addAll(CorpusQueries.m2());
    queries.addAll(CorpusQueries.m3());
    return queries;
  }

  private static chalk.ir.v1.Plan planWithGovernor(
      RegisteredCatalog corpusCatalog, CorpusQueries.Query query, PlanningGovernor governor)
      throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            corpusCatalog, PushdownPolicy.full(), query.conformance(), query.libraries())) {
      PlannerPipeline.Result result =
          pipeline.finish(
              pipeline.front(query.sql()),
              chalk.planner.entitlement.BoundContext.EMPTY,
              false,
              null,
              governor);
      return new chalk.planner.ir.RelToIr(
              new chalk.planner.types.TypeMapper(result.physical().getCluster().getTypeFactory()),
              result.physical().getCluster().getRexBuilder(),
              org.apache.calcite.rel.metadata.RelMetadataQuery.instance(),
              chalk.planner.ir.IrVersionGate.current())
          .toPlan(
              result.physical(),
              result.parameterRowType(),
              corpusCatalog.contextId(),
              corpusCatalog.epoch());
    }
  }

  /**
   * The registry a {@code StopPlanning} call reads (D236): an id in flight is found and marked, an
   * id that has finished is not found but still answers with the count it ended on, and an id
   * nothing was ever registered under answers zero.
   */
  @Test
  void the_in_flight_registry_finds_marks_and_remembers() throws Exception {
    chalk.planner.rpc.PlanningRegistry registry = new chalk.planner.rpc.PlanningRegistry();
    PlanningGovernor governor = PlanningGovernor.create(0, 0, 0, 0, true);

    assertThat(registry.stop("absent").found()).isFalse();
    assertThat(registry.stop("absent").evaluations()).isZero();

    registry.register("r1", governor);
    assertThat(registry.size()).isEqualTo(1);
    assertThat(registry.find("r1")).isSameAs(governor);
    assertThat(registry.stop("r1").found()).isTrue();

    // The mark reached the governor, so the very next run it watches ends at its first complete plan.
    Run stopped = run(LONG_STATEMENT, governor);
    assertThat(stopped.state().termination())
        .isEqualTo(PlanningGovernor.Termination.STOPPED_BY_HOST);

    registry.release("r1", governor);
    assertThat(registry.size()).isZero();
    chalk.planner.rpc.PlanningRegistry.Stopped after = registry.stop("r1");
    assertThat(after.found()).isFalse();
    assertThat(after.evaluations()).isEqualTo(stopped.state().evaluations());
  }

  /**
   * A cancellation ends the search at the next rule boundary, with or without a plan (D236). Here
   * there is none yet, so the result is {@code ABORTED} — which is what the RPC layer turns into a
   * status the caller is, by then, no longer listening for.
   */
  @Test
  void a_cancel_before_the_first_plan_aborts() {
    PlanningGovernor governor = PlanningGovernor.create(0, 0, 0, 0, true);
    governor.requestCancel();

    assertThatThrownBy(() -> run(LONG_STATEMENT, governor))
        .isInstanceOf(PlannerPipeline.PlanningAbortedException.class)
        .satisfies(
            e ->
                assertThat(((PlannerPipeline.PlanningAbortedException) e).state().termination())
                    .isEqualTo(PlanningGovernor.Termination.ABORTED));
    assertThat(governor.termination()).isEqualTo(PlanningGovernor.Termination.CANCELLED_BY_HOST);
  }

  static PlanningGovernor governor(int patience, int interval) {
    return PlanningGovernor.create(0, patience, 0.001, interval, true);
  }
}
