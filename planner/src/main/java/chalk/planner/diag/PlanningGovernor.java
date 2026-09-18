package chalk.planner.diag;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptListener;
import org.apache.calcite.plan.volcano.RelSubset;
import org.apache.calcite.plan.volcano.VolcanoPlanner;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.util.CancelFlag;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Watches one Volcano search and ends it early when the host asked for that
 * (docs/design/30-planning-options.md, D235).
 *
 * <p>The second {@link RelOptListener} beside {@link RuleTrace}, and installed only for a request
 * that carries an option able to end a search early: a time budget, a convergence patience, or a
 * request id a {@code StopPlanning} call could name. A request that carries none installs nothing
 * and samples nothing, which is what keeps the option free for everybody who does not use it.
 *
 * <p><b>An evaluation is one rule match fired.</b> Calcite fires {@code ruleAttempted} twice per
 * match — once before the rule runs and once after — so this counts the "before" event, which is
 * exactly one per {@code VolcanoRuleCall.onMatch}. {@link RuleTrace} counts something else and says
 * so: which rules <em>produced</em> an equivalence, as a set, for {@code rules_fired}.
 *
 * <p><b>How the search is ended.</b> Never by throwing from a listener. The governor raises the
 * planner's cancel flag; Calcite's own {@code VolcanoRuleCall.onMatch} calls
 * {@code VolcanoPlanner.checkCancel} before every subsequent match, that throws
 * {@code VolcanoTimeoutException}, {@code TopDownRuleDriver.drive} catches it and returns, and
 * {@code VolcanoPlanner.findBestExp} goes on to build the cheapest plan found so far. Nothing else
 * about the pipeline changes: that plan is checked, converted and reported like any other.
 *
 * <p>Every decision this class makes is a function of the evaluation sequence — the interval is a
 * count and never a duration — so a convergence-terminated plan is the same plan on every machine.
 * The time budget is the one exception, and it is opt-in.
 */
public final class PlanningGovernor implements RelOptListener {
  /** How close to 1.0 an improvement ratio must be to count as none, when the host says nothing. */
  public static final double DEFAULT_RANGE_THRESHOLD = 0.001d;

  /**
   * Evaluations between samples, in count mode, when the host names an interval of zero — which the
   * wire cannot tell apart from "not set", so a request that says nothing about the interval gets
   * the period mode of {@link #DEFAULT_SAMPLE_PERIOD_MILLIS} instead (D239); this constant survives
   * only for a host that explicitly asks for count mode without saying how wide.
   */
  public static final int DEFAULT_EVALUATION_INTERVAL = 500;

  /**
   * Wall-clock cadence between looks in period mode, when the host does not set one (D239). Period
   * mode is the default: it governs whenever {@code ConvergenceEvaluationInterval} is not set,
   * because that is the common case and it costs nothing to watch — one volatile read per rule
   * evaluation, and a look lands within one evaluation of the cadence.
   */
  public static final long DEFAULT_SAMPLE_PERIOD_MILLIS = 20L;

  /** How many samples are kept for the report; a long search is summarised by its ends. */
  private static final int MAX_RECORDED_SAMPLES = 4096;

  /** How a search ended. */
  public enum Termination {
    /** Ran out of work to do, or stopped improving. */
    CONVERGED,
    BUDGET_EXHAUSTED,
    STOPPED_BY_HOST,
    CANCELLED_BY_HOST,
    /** Ended before the root held any complete plan, so there is no plan. */
    ABORTED
  }

  /**
   * What a look does once this run should give up its turn (D240): release the permit it holds and
   * park the calling thread until the scheduler grants it again. Called on the planning's own
   * thread, synchronously, from inside {@link #ruleAttempted}; the Volcano search is never re-entered
   * because nothing under this call ever returns to the driver — the thread simply resumes exactly
   * where {@link #ruleAttempted} left off once {@link #yield()} returns.
   */
  public interface SliceGate {
    void yield();

    /**
     * A stop or a cancel arrived (D241): if this planning is currently parked, waiting for its next
     * slice, grant it a permit at once rather than waiting for its turn — a search that is about to
     * unwind should not have to wait behind a queue for the chance to do so. Safe to call from any
     * thread; a no-op if the planning is not currently parked.
     */
    void expedite();
  }

  /**
   * The one thread that watches time budgets. It touches nothing but an {@link AtomicReference} and
   * the cancel flag's {@code AtomicBoolean}, which is the only thing about a planning that is safe
   * to touch from another thread.
   */
  private static final ScheduledExecutorService TIMERS =
      Executors.newSingleThreadScheduledExecutor(
          runnable -> {
            Thread thread = new Thread(runnable, "chalk-planning-budget");
            thread.setDaemon(true);
            return thread;
          });

  private final long interval;

  /** True when the host set an explicit evaluation count: the reproducible mode (D239). */
  private final boolean countMode;

  /** Wall-clock cadence between looks in period mode; meaningless in count mode. */
  private final long samplePeriodMillis;

  private final int patience;
  private final double threshold;
  private final long budgetMillis;

  /** The budget as running-time nanoseconds (D242), or 0 for no budget. Computed once. */
  private final long budgetNanos;

  /** Set from the RPC thread by a {@code StopPlanning} call. */
  private volatile boolean stopRequested;

  /** Set from a gRPC callback when the call is cancelled or its session is lost. */
  private volatile boolean cancelRequested;

  /**
   * Set by the period-mode cadence timer; tested and cleared at the top of the next evaluation
   * (D239). Unused in count mode, where {@code seen % interval == 0} decides a look instead.
   */
  private volatile boolean lookRequested;

  /**
   * What a look does once the search should pause here and resume later (D240) — null for a run the
   * scheduler does not own, which is every run reached through {@link chalk.planner.plan.PlannerPipeline}
   * directly rather than through the sidecar's RPC surface. Bound once, before the run starts.
   */
  private @Nullable SliceGate sliceGate;

  /** Slices this planning has run: one to begin with, one more for every look that did not end it. */
  private volatile long slices;

  /**
   * When the slice in progress began running, or 0 when not currently running (D242): parked,
   * finished, or not yet granted. Read and written only by the planning's own thread, except for
   * the getters below, which take a defensive snapshot while a slice may still be in progress.
   */
  private volatile long sliceStartNanos;

  /** When the current wait for a permit began; meaningful only between {@link #recordQueued} and the next grant. */
  private long queueStartNanos;

  /** Running time spent so far, not counting a slice still in progress (D242). */
  private volatile long runningElapsedNanos;

  /** Time spent waiting for a permit so far, not counting a wait still in progress (D242). */
  private volatile long queuedElapsedNanos;

  /** The first of the reasons to be decided wins, whether it came from here or from the timer. */
  private final AtomicReference<Termination> termination = new AtomicReference<>();

  private @Nullable CancelFlag flag;
  private @Nullable VolcanoPlanner planner;

  /** The running-time budget's backstop, rescheduled for what remains at every grant (D242). */
  private @Nullable ScheduledFuture<?> budgetAlarm;

  /** The period-mode look-now flag setter; re-armed at every grant under the scheduler (D242),
   * or for the whole run when there is no scheduler to pause it for. */
  private @Nullable ScheduledFuture<?> cadenceAlarm;

  /** Written only by the planning thread; read by the {@code StopPlanning} diagnostic. */
  private volatile long evaluations;

  /** Looks taken: a period-mode sample or a count-mode interval boundary (D239). */
  private volatile long looks;

  private final List<RelOptCost> samples = new ArrayList<>();
  private @Nullable RelOptCost first;
  private @Nullable RelOptCost best;
  private @Nullable RelOptCost previous;
  private int steady;

  private PlanningGovernor(
      long interval,
      boolean countMode,
      long samplePeriodMillis,
      int patience,
      double threshold,
      long budgetMillis) {
    this.interval = interval;
    this.countMode = countMode;
    this.samplePeriodMillis = samplePeriodMillis;
    this.patience = patience;
    this.threshold = threshold;
    this.budgetMillis = budgetMillis;
    this.budgetNanos = budgetMillis > 0 ? TimeUnit.MILLISECONDS.toNanos(budgetMillis) : 0L;
  }

  /**
   * A governor for a request that asked for one, or null for a request whose options cannot end a
   * search early. Count mode (D239): {@code evaluationInterval} of zero means period mode at the
   * default cadence ({@link #DEFAULT_SAMPLE_PERIOD_MILLIS}).
   *
   * @param timeBudgetMillis wall-clock budget for the optimiser, 0 for none
   * @param patience consecutive steady samples that mean convergence, 0 for never
   * @param threshold how close to 1.0 a sample's ratio must be to be steady, 0 for the default
   * @param evaluationInterval evaluations between samples; 0 governs by the wall-clock cadence
   *     instead (D239), because count mode is the one a host chooses explicitly
   * @param stoppable whether a {@code StopPlanning} call could name this planning
   */
  public static @Nullable PlanningGovernor create(
      long timeBudgetMillis,
      int patience,
      double threshold,
      int evaluationInterval,
      boolean stoppable) {
    return create(timeBudgetMillis, patience, threshold, evaluationInterval, 0L, stoppable);
  }

  /**
   * The same, naming the wall-clock cadence explicitly (D239).
   *
   * @param samplePeriodMillis cadence between looks in period mode, 0 for the default; ignored when
   *     {@code evaluationInterval} is set, because the count then governs instead
   */
  public static @Nullable PlanningGovernor create(
      long timeBudgetMillis,
      int patience,
      double threshold,
      int evaluationInterval,
      long samplePeriodMillis,
      boolean stoppable) {
    if (timeBudgetMillis <= 0 && patience <= 0 && !stoppable) {
      return null;
    }
    return forScheduling(timeBudgetMillis, patience, threshold, evaluationInterval, samplePeriodMillis);
  }

  /**
   * A governor for every planning the scheduler owns, whatever its options (D240): a bounded worker
   * pool needs a look to know where one planning's turn ends and the next begins, so — unlike
   * {@link #create} — this never returns null. A request with no option of its own still gets the
   * period's default cadence and no patience, budget or stop, which is a governor that only ever
   * yields and never ends a search early.
   */
  public static PlanningGovernor forScheduling(
      long timeBudgetMillis,
      int patience,
      double threshold,
      int evaluationInterval,
      long samplePeriodMillis) {
    boolean countMode = evaluationInterval > 0;
    return new PlanningGovernor(
        countMode ? evaluationInterval : 0L,
        countMode,
        samplePeriodMillis > 0 ? samplePeriodMillis : DEFAULT_SAMPLE_PERIOD_MILLIS,
        Math.max(0, patience),
        threshold > 0 ? threshold : DEFAULT_RANGE_THRESHOLD,
        Math.max(0, timeBudgetMillis));
  }

  /**
   * Binds this governor to the planner it will watch, and starts the budget clock.
   *
   * <p>Called once per run, and a retained pipeline is re-entered: the listener registration itself
   * happens once — Calcite's listener list is a multicast with no way to remove an entry — and
   * everything a run accumulates is reset here.
   */
  public void attach(VolcanoPlanner planner, CancelFlag flag) {
    this.planner = planner;
    this.flag = flag;
    flag.clearCancel();
    termination.set(null);
    evaluations = 0;
    looks = 0;
    lookRequested = false;
    // The first slice: whether or not a scheduler is watching, a planning that never yields still
    // ran in exactly one (D240).
    slices = 1;
    samples.clear();
    first = null;
    best = null;
    previous = null;
    steady = 0;
    if (sliceGate == null) {
      // No scheduler to have already started these clocks (D242): this run's whole lifetime, from
      // here to detach(), is running time, exactly as before D240 — there is no queueing at all.
      sliceStartNanos = System.nanoTime();
      armBudgetAlarmForRemaining();
      if (!countMode) {
        // Period mode (D239): the timer sets the flag on this cadence for as long as this governor
        // is attached — re-armed rather than a one-shot, because there is no yield to re-arm it at.
        cadenceAlarm =
            TIMERS.scheduleAtFixedRate(
                () -> lookRequested = true, samplePeriodMillis, samplePeriodMillis, TimeUnit.MILLISECONDS);
      }
    }
    // Under the scheduler, recordGranted() already started both clocks for the slice the front half
    // is running in — attach() must not restart them, or the front half's own time would be counted
    // as queued rather than running (D240's "the front half runs inside the first slice").
  }

  /**
   * Binds the scheduler's gate for this run (D240), or leaves it unbound for a run the scheduler
   * does not own. Called once, before {@link #attach}, by whichever caller decided this planning is
   * scheduled — {@code PlannerServiceImpl}, never a test that talks to
   * {@link chalk.planner.plan.PlannerPipeline} directly.
   */
  public void bindSliceGate(@Nullable SliceGate gate) {
    this.sliceGate = gate;
  }

  /**
   * The scheduler is about to enqueue this planning for the first time (D242). Called once, by
   * {@code PlanningScheduler.run}, before the ticket exists anywhere a permit could reach it.
   */
  public void recordQueued() {
    queueStartNanos = System.nanoTime();
  }

  /**
   * This planning was just granted a permit — its very first, called by the scheduler the instant
   * {@code Ticket.awaitGrant()} first returns, or another after a look yielded, called by
   * {@link #ruleAttempted} itself the instant {@link SliceGate#yield} returns (D242).
   */
  public void recordGranted() {
    long now = System.nanoTime();
    queuedElapsedNanos += now - queueStartNanos;
    sliceStartNanos = now;
    armBudgetAlarmForRemaining();
    if (!countMode) {
      lookRequested = false;
      cadenceAlarm =
          TIMERS.schedule(() -> lookRequested = true, samplePeriodMillis, TimeUnit.MILLISECONDS);
    }
  }

  /**
   * This planning is giving up its permit at a look that did not end the search, and is about to
   * park (D242). Called by {@link #ruleAttempted} itself, immediately before {@link SliceGate#yield}.
   */
  public void recordYielding() {
    long now = System.nanoTime();
    runningElapsedNanos += now - sliceStartNanos;
    sliceStartNanos = 0L;
    queueStartNanos = now;
    cancelBudgetAlarm();
    cancelCadenceAlarm();
  }

  /** This planning's whole run is over, whatever the outcome (D242). Called once, by the scheduler. */
  public void recordFinished() {
    long now = System.nanoTime();
    if (sliceStartNanos != 0L) {
      runningElapsedNanos += now - sliceStartNanos;
      sliceStartNanos = 0L;
    }
    cancelBudgetAlarm();
    cancelCadenceAlarm();
  }

  /** Slices run so far: one to begin with, one more for every look that did not end the search. */
  public long slices() {
    return slices;
  }

  /**
   * Running time spent so far, across every slice, including one still in progress (D242): what
   * {@link #ruleAttempted} charges the budget against, and what the wire reports as
   * {@code running_elapsed_us}.
   */
  public long runningElapsedNanos() {
    long start = sliceStartNanos;
    return runningElapsedNanos + (start == 0L ? 0L : System.nanoTime() - start);
  }

  /** Time spent waiting for a permit so far (D242); zero for a run the scheduler never queued. */
  public long queuedElapsedNanos() {
    return queuedElapsedNanos;
  }

  /** Stops the budget and cadence clocks. The cancel flag is left as it is; the pipeline reads it. */
  public void detach() {
    cancelBudgetAlarm();
    cancelCadenceAlarm();
  }

  /**
   * (Re)schedules the running-time budget's backstop for what remains, from now (D242): both, as
   * D235 asks — the planning thread tests the deadline at every look, against running time, and the
   * timer is the backstop for a search whose looks are far apart. A remainder that is already spent
   * ends the search immediately rather than scheduling a non-positive delay.
   */
  private void armBudgetAlarmForRemaining() {
    if (budgetNanos <= 0) {
      return;
    }
    long remaining = budgetNanos - runningElapsedNanos;
    if (remaining <= 0) {
      end(Termination.BUDGET_EXHAUSTED);
      return;
    }
    budgetAlarm =
        TIMERS.schedule(() -> end(Termination.BUDGET_EXHAUSTED), remaining, TimeUnit.NANOSECONDS);
  }

  private void cancelBudgetAlarm() {
    ScheduledFuture<?> scheduled = budgetAlarm;
    if (scheduled != null) {
      scheduled.cancel(false);
      budgetAlarm = null;
    }
  }

  private void cancelCadenceAlarm() {
    ScheduledFuture<?> cadence = cadenceAlarm;
    if (cadence != null) {
      cadence.cancel(false);
      cadenceAlarm = null;
    }
  }

  /**
   * The host asked for the best complete plan there is (D236). If the root already holds one the
   * search ends now; if it does not, this arms "end at the first complete plan", which the next rule
   * boundaries test — so a stop on its own can never leave a request without a plan.
   */
  public void requestStop() {
    stopRequested = true;
    expediteIfParked();
  }

  /** The call went away. The search ends at the next rule boundary, with or without a plan. */
  public void requestCancel() {
    cancelRequested = true;
    end(Termination.CANCELLED_BY_HOST);
    expediteIfParked();
  }

  /**
   * A stop or a cancel is granted a permit at once if this run is currently parked (D241) — a
   * no-op for a run the scheduler does not own, and a no-op if it is not currently parked, which
   * {@link SliceGate#expedite} is defined to make safe either way.
   */
  private void expediteIfParked() {
    SliceGate gate = sliceGate;
    if (gate != null) {
      gate.expedite();
    }
  }

  /** How this search ended, or null while it is still running and for one that ran to completion. */
  public @Nullable Termination termination() {
    return termination.get();
  }

  /** Rule matches fired. Safe to read from another thread. */
  public long evaluations() {
    return evaluations;
  }

  /** Looks taken: a period-mode sample or a count-mode interval boundary (D239). */
  public long looks() {
    return looks;
  }

  /**
   * The evaluation interval in force, or 0 in period mode, where the interval is not a count and
   * there is nothing to report (D239).
   */
  public int evaluationInterval() {
    return countMode ? (int) interval : 0;
  }

  /** The root's best cost at the first sample that saw a complete plan, or null if none did. */
  public @Nullable RelOptCost firstCost() {
    return first;
  }

  /** The root's best cost at the last sample that saw a complete plan. */
  public @Nullable RelOptCost bestCost() {
    return best;
  }

  /** Every sample that saw a complete plan, in order. The measurement the ADR reports. */
  public List<RelOptCost> samples() {
    return List.copyOf(samples);
  }

  /** Whether this governor is the reason the search stopped. */
  public boolean raised() {
    return termination.get() != null;
  }

  @Override
  public void ruleAttempted(RuleAttemptedEvent event) {
    if (!event.isBefore()) {
      // The second of the pair, after the rule ran. One match, one evaluation.
      return;
    }
    long seen = ++evaluations;
    if (termination.get() != null) {
      // Already raised; the next checkCancel ends the drive. Nothing left to decide.
      return;
    }
    if (cancelRequested) {
      end(Termination.CANCELLED_BY_HOST);
      return;
    }
    if (stopRequested) {
      // Armed until the root holds something to return.
      if (rootBest() != null) {
        end(Termination.STOPPED_BY_HOST);
      }
      return;
    }
    // A look: an interval boundary in count mode, or the cadence flag in period mode — tested and
    // cleared here, at the top of the evaluation, exactly where D239 puts it (one volatile read).
    boolean look = countMode ? seen % interval == 0 : lookRequested;
    if (look) {
      if (!countMode) {
        lookRequested = false;
      }
      looks++;
      // Running time, not wall time (D242): a search parked behind others must never be charged for
      // the queueing around it. runningElapsedNanos() folds in the slice still in progress.
      if (budgetNanos > 0 && runningElapsedNanos() >= budgetNanos) {
        end(Termination.BUDGET_EXHAUSTED);
        return;
      }
      sample();
      // A look that did not end the search is this slice's last evaluation under the scheduler
      // (D240): give up the permit and park here, on this same thread, until it is granted again.
      // Nothing above this call is re-entered — the Volcano search resumes exactly where this
      // returns, on whichever thread that turns out to be scheduled on next.
      SliceGate gate = sliceGate;
      if (gate != null && termination.get() == null) {
        recordYielding();
        gate.yield();
        recordGranted();
        slices++;
      }
    }
  }

  /**
   * One sample: the root subset's best cost, and what it says about whether the search is still
   * buying anything.
   *
   * <p>Convergence is {@code patience} consecutive samples whose improvement over the previous one
   * is within {@code threshold} of 1 by Calcite's own {@link RelOptCost#divideBy} — the geometric
   * mean over the non-zero finite components of rows, cpu and io.
   */
  private void sample() {
    RelOptCost cost = rootCost();
    if (cost == null || cost.isInfinite()) {
      // No complete plan yet: there is nothing to compare and nothing to converge to.
      return;
    }
    if (first == null) {
      first = cost;
    }
    best = cost;
    if (samples.size() < MAX_RECORDED_SAMPLES) {
      samples.add(cost);
    }
    if (patience <= 0) {
      previous = cost;
      return;
    }
    if (previous != null) {
      if (Math.abs(cost.divideBy(previous) - 1.0d) <= threshold) {
        if (++steady >= patience) {
          previous = cost;
          end(Termination.CONVERGED);
          return;
        }
      } else {
        steady = 0;
      }
    }
    previous = cost;
  }

  /** The root subset's best cost, or null when the root holds no complete plan. */
  private @Nullable RelOptCost rootCost() {
    VolcanoPlanner volcano = planner;
    RelSubset root = rootSubset();
    if (volcano == null || root == null || root.getBest() == null) {
      return null;
    }
    // getCost of a RelSubset is its bestCost and reads no metadata, so this costs a field read.
    return volcano.getCost(root, root.getCluster().getMetadataQuery());
  }

  /** The best plan the root holds, or null when it holds none. */
  private @Nullable RelNode rootBest() {
    RelSubset root = rootSubset();
    return root == null ? null : root.getBest();
  }

  private @Nullable RelSubset rootSubset() {
    VolcanoPlanner volcano = planner;
    if (volcano == null) {
      return null;
    }
    // The very subset findBestExp will build from: the top-down driver never re-canonizes the root,
    // so what this reads is what the plan will be built from.
    return volcano.getRoot() instanceof RelSubset subset ? subset : null;
  }

  /** Decides the reason, once, and raises the flag the driver reads. */
  private void end(Termination reason) {
    if (!termination.compareAndSet(null, reason)) {
      return;
    }
    CancelFlag raised = flag;
    if (raised != null) {
      raised.requestCancel();
    }
  }

  @Override
  public void relEquivalenceFound(RelEquivalenceEvent event) {
    // Not a rule firing.
  }

  @Override
  public void ruleProductionSucceeded(RuleProductionEvent event) {
    // Counted by RuleTrace, as the rules that produced something; not an evaluation.
  }

  @Override
  public void relDiscarded(RelDiscardedEvent event) {
    // Nothing to record.
  }

  @Override
  public void relChosen(RelChosenEvent event) {
    // Nothing to record.
  }

  /**
   * The listener a pipeline registers, once, and points at this run's governor.
   *
   * <p>Calcite's listener list is a multicast with no way to remove an entry, and a pipeline can be
   * re-entered: a narrowing runs again through the pipeline that converted the statement (D233).
   * Registering each run's governor directly would leave the first one listening for ever and the
   * second one hearing nothing — which is what a narrowing's evaluation count of zero looked like
   * before this existed.
   */
  public static final class Slot implements RelOptListener {
    private volatile @Nullable PlanningGovernor target;

    /** Points this slot at one run's governor, or at nothing for an ungoverned run. */
    public void set(@Nullable PlanningGovernor governor) {
      this.target = governor;
    }

    @Override
    public void ruleAttempted(RuleAttemptedEvent event) {
      PlanningGovernor governor = target;
      if (governor != null) {
        governor.ruleAttempted(event);
      }
    }

    @Override
    public void relEquivalenceFound(RelEquivalenceEvent event) {}

    @Override
    public void ruleProductionSucceeded(RuleProductionEvent event) {}

    @Override
    public void relDiscarded(RelDiscardedEvent event) {}

    @Override
    public void relChosen(RelChosenEvent event) {}
  }
}
