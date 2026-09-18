package chalk.planner.rpc;

import chalk.planner.diag.PlanningGovernor;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.Deque;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.Callable;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.locks.ReentrantLock;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A bounded pool of {@code W} workers time-slices the sidecar's plannings (docs/design/30-planning-options.md,
 * D240, D241): every planning gets a {@link PlanningGovernor}, whatever its options, and a slice runs
 * from one look to the next. At a look the governor calls {@link Ticket#yield()}, which releases this
 * planning's permit and parks its thread until the scheduler grants the permit again; the Volcano
 * search is never re-entered, because nothing under that call ever returns to the driver — the
 * thread simply resumes exactly where {@code ruleAttempted} left off, on whichever turn it is
 * granted next.
 *
 * <p>Every planning runs on a virtual thread ({@link Executors#newVirtualThreadPerTaskExecutor()}):
 * the gRPC executor's threads never plan. {@code PlannerServiceImpl} hands a request to
 * {@link #run} and blocks the calling gRPC thread until it finishes — which is a wait, not
 * planning, so the "never plan" property still holds — while the real work, including every park
 * and resume, happens on the virtual thread this class owns.
 *
 * <p><b>The policy (D241).</b> Three FIFO queues, high, normal and low. Every new planning enters
 * high for its first slice, so a simple query is never held up behind a complex one that has
 * already had one; after its first slice a planning is relegated to the queue of its own request
 * priority and re-queued at the back of that queue after every later slice. Permits are granted by
 * a fixed schedule of 21 turns — 16 to high, 4 to normal, 1 to low — a queue's share passing to the
 * others while it is empty; the schedule is evenly interleaved rather than clumped, so a share never
 * arrives as one long burst. A stop or a cancel on a parked planning is granted a permit at once,
 * out of turn and without disturbing the schedule's own accounting. No aging: the low queue's one
 * turn in twenty-one always comes, which is what makes starvation impossible by construction.
 *
 * <p><b>Planning sessions (D245, D246, D247).</b> A named partition of the workers a cooperating
 * host declares inline on a request, identified by the name and not by the gRPC channel — two
 * channels naming the same session share one cap. There is no lifecycle RPC: the first request
 * naming a session registers its cap, and a later request naming the same session with a different
 * non-zero cap replaces it; a cap of zero leaves the registered one as it is, and a cap at or above
 * {@code W} is no cap at all. The dispatcher skips a queued planning whose session already holds its
 * cap's worth of permits, leaving its place in the queue exactly as it was, so a session's own cap is
 * the only thing that ever holds it back — advisory quality-of-service for a host's own cooperating
 * callers, never isolation between callers that do not cooperate.
 *
 * <p><b>The registry is bounded.</b> A host that names a unique session per request would otherwise
 * leak one registry entry per request for the sidecar's whole lifetime. {@code --planning-session-limit}
 * (default {@value #DEFAULT_SESSION_LIMIT}) bounds how many distinct sessions {@link #sessions} holds
 * at once; registering or naming a session touches it, and a registration that would take the count
 * over the limit evicts the least recently used session with no running planning and no queued one —
 * a session in use is never evicted, whatever its age, so the registry may exceed the limit for as
 * long as every session in it stays busy. An evicted session named again starts over, cap zero and
 * all, exactly as if it had never been registered.
 */
public final class PlanningScheduler implements AutoCloseable {
  /** A planning's priority once its first slice is over (D241); every planning starts in high. */
  public enum Priority {
    HIGH,
    NORMAL,
    LOW
  }

  /** Of every 21 permits granted, this many go to the high queue. */
  private static final int HIGH_SHARE = 16;

  private static final int NORMAL_SHARE = 4;
  private static final int LOW_SHARE = 1;

  /** The fixed, evenly interleaved 21-slot schedule (D241); see {@link #buildSchedule}. */
  private static final Priority[] GRANT_SCHEDULE = buildSchedule();

  /** {@code --planning-session-limit}'s default: how many distinct sessions {@link #sessions} holds
   * before naming a new one evicts the least recently used idle one. */
  public static final int DEFAULT_SESSION_LIMIT = 1024;

  private final int workers;
  private final int sessionLimit;
  private final ExecutorService plannerExecutor;

  private final ReentrantLock lock = new ReentrantLock();
  private final Deque<Ticket> high = new ArrayDeque<>();
  private final Deque<Ticket> normal = new ArrayDeque<>();
  private final Deque<Ticket> low = new ArrayDeque<>();

  /** A stop or a cancel of a parked planning (D241): checked before the weighted schedule. */
  private final Deque<Ticket> expedited = new ArrayDeque<>();

  /**
   * Registered planning sessions, by name (D245), oldest-touched first — access order, so every
   * lookup or insert moves the entry it names to the most-recently-used end. Guarded by
   * {@link #lock}, like the queues.
   */
  private final Map<String, Session> sessions = new LinkedHashMap<>(16, 0.75f, true);

  /** How many sessions {@link #evictIfOverLimit} has removed. Package-private test hook. */
  private int sessionEvictions;

  private int availablePermits;
  private int scheduleCursor;

  /** Every grant so far, by which queue it was drawn from. Package-private test hook. */
  private final List<Priority> grantLog = new ArrayList<>();

  public PlanningScheduler(int workers) {
    this(workers, DEFAULT_SESSION_LIMIT);
  }

  /** The same, naming the session registry's bound explicitly (D245's amendment) instead of taking
   * {@link #DEFAULT_SESSION_LIMIT}. */
  public PlanningScheduler(int workers, int sessionLimit) {
    if (workers < 1) {
      throw new IllegalArgumentException("workers must be at least 1, got " + workers);
    }
    if (sessionLimit < 1) {
      throw new IllegalArgumentException("sessionLimit must be at least 1, got " + sessionLimit);
    }
    this.workers = workers;
    this.sessionLimit = sessionLimit;
    this.availablePermits = workers;
    this.plannerExecutor = Executors.newVirtualThreadPerTaskExecutor();
  }

  /** The worker count in force, for the sidecar's build-info response (D243). */
  public int workers() {
    return workers;
  }

  /**
   * Test-only: how many tickets are currently waiting for a permit — queued or expedited, either
   * way not yet running. A state a test polls for rather than a duration it waits out: once this is
   * at least the count a test just submitted, every one of them is genuinely parked, not merely
   * "probably parked by now".
   */
  int queuedForTest() {
    lock.lock();
    try {
      return high.size() + normal.size() + low.size() + expedited.size();
    } finally {
      lock.unlock();
    }
  }

  /** Test-only: a registered session's cap, or -1 for a name nothing has registered (D245). */
  int sessionCapForTest(String name) {
    lock.lock();
    try {
      Session session = sessions.get(name);
      return session == null ? -1 : session.cap;
    } finally {
      lock.unlock();
    }
  }

  /** Test-only: how many distinct sessions are registered right now (D245's bound). */
  int sessionRegistrySizeForTest() {
    lock.lock();
    try {
      return sessions.size();
    } finally {
      lock.unlock();
    }
  }

  /** Test-only: how many sessions {@link #evictIfOverLimit} has evicted so far (D245's bound). */
  int sessionEvictionsForTest() {
    lock.lock();
    try {
      return sessionEvictions;
    } finally {
      lock.unlock();
    }
  }

  /**
   * Runs {@code body} under this scheduler, on a virtual thread it owns, blocking the caller until
   * it finishes. {@code governor} is bound to the ticket before {@code body} starts, so a look
   * inside it yields through this scheduler; pass a fresh governor per call, never a shared one.
   * {@code priority} is where this planning goes once its first slice is over — every planning is
   * served from the high queue for that first slice, whatever priority it names (D241).
   */
  public <T> T run(PlanningGovernor governor, Priority priority, Callable<T> body) throws Exception {
    return run(governor, priority, "", 0, body);
  }

  /**
   * The same, naming a planning session (D245): {@code sessionName} empty runs uncapped and
   * outside every session. {@code sessionMaxConcurrency} registers or updates the session's cap —
   * see the class documentation for exactly how — and is ignored when {@code sessionName} is empty.
   */
  public <T> T run(
      PlanningGovernor governor,
      Priority priority,
      String sessionName,
      int sessionMaxConcurrency,
      Callable<T> body)
      throws Exception {
    Ticket ticket = new Ticket(priority, sessionName);
    registerSession(sessionName, sessionMaxConcurrency);
    governor.bindSliceGate(ticket);
    // The queued clock starts here, before this ticket exists anywhere a permit could reach it
    // (D242) — enqueueFirst below can grant it before this method even returns.
    governor.recordQueued();

    CompletableFuture<T> result = new CompletableFuture<>();
    plannerExecutor.execute(
        () -> {
          try {
            ticket.awaitGrant();
            // The very first grant (D242): every later one is recorded by the governor itself, the
            // instant SliceGate.yield() returns, because only ruleAttempted knows a look happened.
            governor.recordGranted();
            try {
              result.complete(body.call());
            } finally {
              governor.recordFinished();
              release(ticket);
            }
          } catch (Throwable t) {
            result.completeExceptionally(t);
          }
        });
    enqueueFirst(ticket);

    try {
      return result.join();
    } catch (CompletionException e) {
      Throwable cause = e.getCause();
      if (cause instanceof Exception ex) {
        throw ex;
      }
      throw e;
    }
  }

  /** Every grant so far, by which queue it was drawn from (D241). Package-private test hook. */
  List<Priority> grantLog() {
    lock.lock();
    try {
      return List.copyOf(grantLog);
    } finally {
      lock.unlock();
    }
  }

  /**
   * Test-only: seeds the three queues with synthetic tickets that never run anything, then draws
   * {@code grants} permits from them through the real selection logic — a pure simulation of the
   * weighted schedule (D241), so the sequence can be asserted without any concurrency, any governed
   * planning, and no timing at all. Package-private, and never called outside a test in this package.
   */
  List<Priority> simulateGrantOrderForTest(int highCount, int normalCount, int lowCount, int grants) {
    lock.lock();
    try {
      for (int i = 0; i < highCount; i++) {
        high.addLast(new Ticket(Priority.HIGH));
      }
      for (int i = 0; i < normalCount; i++) {
        normal.addLast(new Ticket(Priority.NORMAL));
      }
      for (int i = 0; i < lowCount; i++) {
        low.addLast(new Ticket(Priority.LOW));
      }
      List<Priority> drawn = new ArrayList<>();
      for (int i = 0; i < grants; i++) {
        Ticket next = pickNext();
        if (next == null) {
          break;
        }
        drawn.add(next.requestedPriority);
      }
      return drawn;
    } finally {
      lock.unlock();
    }
  }

  /**
   * Registers or updates a planning session's cap (D245). A no-op for an unnamed session. The
   * first request naming a session creates it with the cap it carries, whatever that cap is —
   * including zero, which is how a session that is never given a cap runs uncapped; a later request
   * naming the same session replaces the cap only when it carries a different, non-zero one.
   */
  private void registerSession(String name, int maxConcurrency) {
    if (name.isEmpty()) {
      return;
    }
    lock.lock();
    try {
      Session existing = sessions.get(name);
      if (existing == null) {
        sessions.put(name, new Session(maxConcurrency));
        evictIfOverLimit();
      } else if (maxConcurrency > 0) {
        existing.cap = maxConcurrency;
      }
    } finally {
      lock.unlock();
    }
  }

  /**
   * Keeps {@link #sessions} at or under {@link #sessionLimit} (D245's bound) by evicting the least
   * recently used session with nothing queued or running — {@link Session#inFlight} zero — walking
   * from the access-ordered map's oldest entry forward until enough room is freed or every remaining
   * session turns out to be in use, in which case the registry is left over the limit rather than
   * evicting something a queued or running planning still names. Called with {@link #lock} held,
   * immediately after a put that may have grown the map past the limit.
   */
  private void evictIfOverLimit() {
    Iterator<Map.Entry<String, Session>> it = sessions.entrySet().iterator();
    while (sessions.size() > sessionLimit && it.hasNext()) {
      if (it.next().getValue().inFlight == 0) {
        it.remove();
        sessionEvictions++;
      }
    }
  }

  /**
   * Whether granting {@code ticket} now would put its session over its cap (D246). False for a
   * ticket that names no session, a session nothing has registered a cap for, a cap of zero
   * (uncapped), or a cap at or above the worker count (also uncapped, since the pool itself already
   * bounds it that far). Called with {@link #lock} held.
   */
  private boolean isCappedOut(Ticket ticket) {
    if (ticket.sessionName.isEmpty()) {
      return false;
    }
    Session session = sessions.get(ticket.sessionName);
    if (session == null || session.cap <= 0 || session.cap >= workers) {
      return false;
    }
    return session.running >= session.cap;
  }

  /**
   * The first ticket in {@code queue}, front to back, whose session is not at its cap — skipping
   * (never removing) any that are, so a capped-out planning keeps its exact place in line and is
   * the only thing a session's cap ever holds back (D246). Called with {@link #lock} held.
   */
  private @Nullable Ticket pollEligible(Deque<Ticket> queue) {
    Iterator<Ticket> it = queue.iterator();
    while (it.hasNext()) {
      Ticket candidate = it.next();
      if (!isCappedOut(candidate)) {
        it.remove();
        return candidate;
      }
    }
    return null;
  }

  /** A newly submitted planning always enters the high queue for its first slice (D241). */
  private void enqueueFirst(Ticket ticket) {
    lock.lock();
    try {
      beginInFlight(ticket);
      high.addLast(ticket);
      pump();
    } finally {
      lock.unlock();
    }
  }

  /**
   * A ticket naming a session is in the registry's eyes "in use" from here — once, for the whole
   * run, not once per slice — until {@link #endInFlight} says otherwise (D245's bound): between the
   * two this session is never the one {@link #evictIfOverLimit} removes, however long it sits idle
   * in the access order while queued or running. Called with {@link #lock} held.
   */
  private void beginInFlight(Ticket ticket) {
    if (ticket.sessionName.isEmpty()) {
      return;
    }
    Session session = sessions.get(ticket.sessionName);
    if (session != null) {
      session.inFlight++;
    }
  }

  /** The other half of {@link #beginInFlight}, called once a ticket is done for good. Called with
   * {@link #lock} held. */
  private void endInFlight(Ticket ticket) {
    if (ticket.sessionName.isEmpty()) {
      return;
    }
    Session session = sessions.get(ticket.sessionName);
    if (session != null) {
      session.inFlight--;
    }
  }

  /**
   * Called by a ticket's {@link Ticket#yield()}, on the planning's own thread: gives up the permit
   * this planning holds, re-queues it — in its own priority's queue from here on, whatever slice
   * this is (D241) — and does not return until it is granted again.
   */
  private void yield(Ticket ticket) {
    lock.lock();
    try {
      availablePermits++;
      leaveSession(ticket);
      // The queue of its own request priority, whether this is the first yield or the fifth — every
      // planning is relegated there after its first slice and stays for every later one (D241).
      queueFor(ticket.requestedPriority).addLast(ticket);
      ticket.rearm();
      pump();
    } finally {
      lock.unlock();
    }
    ticket.awaitGrant();
  }

  /**
   * A stop or a cancel arrived for this ticket (D241, D236). If it is currently sitting in one of
   * the three queues — parked, not running — it is pulled out and granted a permit at once, ahead of
   * everything the weighted schedule would otherwise have served first, and without moving the
   * schedule's own cursor. A ticket that is not in any queue is either already running, in which case
   * the flag it was told about is enough, or already expedited, in which case there is nothing more
   * to do.
   */
  private void expedite(Ticket ticket) {
    lock.lock();
    try {
      if (high.remove(ticket) || normal.remove(ticket) || low.remove(ticket)) {
        expedited.addLast(ticket);
        pump();
      }
    } finally {
      lock.unlock();
    }
  }

  /** A planning finished (or failed): give back its permit if it still holds one, and its session's
   * in-flight count (D245's bound), unconditionally — the ticket is done either way. */
  private void release(Ticket ticket) {
    lock.lock();
    try {
      if (ticket.holdsPermit) {
        ticket.holdsPermit = false;
        availablePermits++;
        leaveSession(ticket);
        pump();
      }
      endInFlight(ticket);
    } finally {
      lock.unlock();
    }
  }

  /** Grants permits while any are free and something is queued. Called with {@link #lock} held. */
  private void pump() {
    while (availablePermits > 0) {
      Ticket next = pickNext();
      if (next == null) {
        return;
      }
      availablePermits--;
      next.holdsPermit = true;
      enterSession(next);
      next.grant();
    }
  }

  /** A granted ticket's session, if it has one, now holds one more of its cap (D246). */
  private void enterSession(Ticket ticket) {
    if (ticket.sessionName.isEmpty()) {
      return;
    }
    Session session = sessions.get(ticket.sessionName);
    if (session != null) {
      session.running++;
    }
  }

  /** A ticket giving up its permit — yielding or finished — frees its session's count (D246). */
  private void leaveSession(Ticket ticket) {
    if (ticket.sessionName.isEmpty()) {
      return;
    }
    Session session = sessions.get(ticket.sessionName);
    if (session != null) {
      session.running--;
    }
  }

  /**
   * Which ticket the next permit goes to (D241): an expedited one first, or the schedule's current
   * slot, walked forward until a non-empty queue is found — its grant goes to the next in line, and
   * the schedule resumes from just after wherever it stopped. Called with {@link #lock} held.
   */
  private Ticket pickNext() {
    Ticket expeditedTicket = expedited.pollFirst();
    if (expeditedTicket != null) {
      return expeditedTicket;
    }
    for (int tries = 0; tries < GRANT_SCHEDULE.length; tries++) {
      Priority want = GRANT_SCHEDULE[scheduleCursor];
      scheduleCursor = (scheduleCursor + 1) % GRANT_SCHEDULE.length;
      Ticket candidate = pollEligible(queueFor(want));
      if (candidate != null) {
        grantLog.add(want);
        return candidate;
      }
    }
    return null;
  }

  private Deque<Ticket> queueFor(Priority priority) {
    return switch (priority) {
      case HIGH -> high;
      case NORMAL -> normal;
      case LOW -> low;
    };
  }

  /**
   * The 21-slot schedule, evenly interleaved: each priority's {@code i}-th turn wants the position
   * {@code i * 21 / weight}, and merging the three priorities' ideal positions in order (high before
   * normal before low on a tie) spreads a share across the whole cycle instead of clumping it at one
   * end — the low queue's one turn is never twenty grants away from every high queue turn, only ever
   * one cycle away from its last.
   */
  private static Priority[] buildSchedule() {
    int total = HIGH_SHARE + NORMAL_SHARE + LOW_SHARE;
    record Slot(double position, int rank, Priority priority) {}

    List<Slot> slots = new ArrayList<>(total);
    for (int i = 0; i < HIGH_SHARE; i++) {
      slots.add(new Slot((double) i * total / HIGH_SHARE, 0, Priority.HIGH));
    }
    for (int i = 0; i < NORMAL_SHARE; i++) {
      slots.add(new Slot((double) i * total / NORMAL_SHARE, 1, Priority.NORMAL));
    }
    for (int i = 0; i < LOW_SHARE; i++) {
      slots.add(new Slot((double) i * total / LOW_SHARE, 2, Priority.LOW));
    }
    slots.sort(Comparator.comparingDouble(Slot::position).thenComparingInt(Slot::rank));

    Priority[] schedule = new Priority[total];
    for (int i = 0; i < total; i++) {
      schedule[i] = slots.get(i).priority();
    }
    return schedule;
  }

  @Override
  public void close() {
    plannerExecutor.close();
  }

  /**
   * A registered planning session (D245): its cap, and how many of its plannings currently hold a
   * permit. Touched only under {@link #lock}, so plain fields are enough.
   */
  private static final class Session {
    int cap;
    int running;

    /** From the scheduler's {@code beginInFlight} to its {@code endInFlight}: zero is what makes a
     * session eligible for {@code evictIfOverLimit} to remove, whatever its place in the access
     * order. */
    int inFlight;

    Session(int cap) {
      this.cap = cap;
    }
  }

  /**
   * One planning's place in the scheduler: a park/resume gate plus the bookkeeping the governor
   * yields through. Not thread-confined — {@link #yield()} and {@link #expedite()} run on whichever
   * thread calls them, {@link #grant()} on whichever thread is granting permits at the time.
   */
  private final class Ticket implements PlanningGovernor.SliceGate {
    final Priority requestedPriority;

    /** Empty for a planning that names no session (D245): never capped, never counted. */
    final String sessionName;

    /** Completed by {@link #grant()}; replaced by {@link #rearm()} for the next wait. */
    private volatile CompletableFuture<Void> gate = new CompletableFuture<>();

    /** True only while this ticket actually holds one of the scheduler's permits. */
    private volatile boolean holdsPermit;

    Ticket(Priority requestedPriority) {
      this(requestedPriority, "");
    }

    Ticket(Priority requestedPriority, String sessionName) {
      this.requestedPriority = requestedPriority;
      this.sessionName = sessionName;
    }

    /** Blocks the calling (virtual) thread until {@link #grant()} completes this ticket's gate. */
    void awaitGrant() {
      gate.join();
    }

    /** A fresh gate for the next wait, after this one has already been granted once. */
    void rearm() {
      gate = new CompletableFuture<>();
    }

    /** Wakes whatever is parked in {@link #awaitGrant()}. Never blocks. */
    void grant() {
      gate.complete(null);
    }

    /** {@link PlanningGovernor.SliceGate}: called on the planning's own thread, at a look. */
    @Override
    public void yield() {
      PlanningScheduler.this.yield(this);
    }

    /** {@link PlanningGovernor.SliceGate}: called from any thread, on a stop or a cancel. */
    @Override
    public void expedite() {
      PlanningScheduler.this.expedite(this);
    }
  }
}
