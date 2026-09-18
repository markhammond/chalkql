package chalk.planner.rpc;

import chalk.planner.diag.PlanningGovernor;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The plannings a {@code StopPlanning} call can address, by the {@code request_id} their options
 * carried (docs/design/30-planning-options.md, D236).
 *
 * <p>Empty for every deployment nobody stops anything in: a request with no {@code request_id}
 * installs no governor and takes no entry here.
 *
 * <p>Beside the live entries it keeps a small ring of the ids that have <em>finished</em>, with the
 * evaluation count each ended on. That is the diagnostic a host — and the cancellation test — reads
 * to see that a planning it cancelled is no longer running: {@code found} goes false and the count
 * stops moving.
 */
public final class PlanningRegistry {
  /** How many finished plannings are remembered for the diagnostic. */
  static final int REMEMBERED = 64;

  private final Map<String, PlanningGovernor> live = new ConcurrentHashMap<>();

  private final Map<String, Long> finished =
      new LinkedHashMap<>(16, 0.75f, false) {
        private static final long serialVersionUID = 1L;

        @Override
        protected boolean removeEldestEntry(Map.Entry<String, Long> eldest) {
          return size() > REMEMBERED;
        }
      };

  /** What one {@code StopPlanning} call is told. */
  public record Stopped(boolean found, long evaluations) {}

  /** Registers an in-flight planning under its id. An empty id registers nothing. */
  public void register(String requestId, PlanningGovernor governor) {
    if (!requestId.isEmpty()) {
      live.put(requestId, governor);
    }
  }

  /** Takes the entry out and remembers where it got to. */
  public void release(String requestId, PlanningGovernor governor) {
    if (requestId.isEmpty()) {
      return;
    }
    live.remove(requestId, governor);
    synchronized (finished) {
      finished.put(requestId, governor.evaluations());
    }
  }

  /**
   * Marks the planning under {@code requestId} as one whose host wants the best complete plan it
   * has; the governor sees it at the next rule boundary (D236).
   */
  public Stopped stop(String requestId) {
    PlanningGovernor governor = live.get(requestId);
    if (governor != null) {
      governor.requestStop();
      return new Stopped(true, governor.evaluations());
    }
    Long last;
    synchronized (finished) {
      last = finished.get(requestId);
    }
    return new Stopped(false, last == null ? 0L : last);
  }

  /** The governor of an in-flight planning, for a caller that wants to cancel rather than stop. */
  public @Nullable PlanningGovernor find(String requestId) {
    return requestId.isEmpty() ? null : live.get(requestId);
  }

  /** How many plannings are in flight. For tests. */
  public int size() {
    return live.size();
  }
}
