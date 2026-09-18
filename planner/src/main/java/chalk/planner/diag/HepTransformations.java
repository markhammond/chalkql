package chalk.planner.diag;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import org.apache.calcite.plan.RelOptListener;
import org.apache.calcite.plan.RelOptPlanner;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Counts the transformations each rule produces in the deterministic Hep phases.
 *
 * <p>{@link RuleTrace} and {@link PlanningGovernor} both watch the <em>Volcano</em> planner, which
 * is where every other rule figure in this tree comes from. The Hep phases had no counter at all,
 * and F66 is a defect measured in exactly that quantity: two rules that are inverses of each other
 * undoing each other until the request's deadline passes. What separates the defect from the fix is
 * the number of applications and not an elapsed time — which is the only kind of bound a test here
 * may assert — so the regression needs the Hep planner's own count.
 *
 * <p>Off unless a caller installs one: {@link #attach} is a thread-local read, and the pipeline does
 * it once per Hep phase, so a request that asked for no diagnostics allocates nothing and attaches
 * no listener.
 *
 * <p>{@code HepPlanner} reports one transformation twice — once before it is applied and once after
 * — so only the "before" half is counted, which makes the figure "rule applications" and nothing
 * else.
 *
 * <p>A counter installed with a <b>ceiling</b> throws the moment the run passes it, naming the rules
 * and their counts. That is what makes a regression here a failure a second long instead of a
 * request that never returns: the shapes F66 covers did not plan at all, so a test that only read
 * the count afterwards would hang rather than fail.
 */
public final class HepTransformations implements RelOptListener, AutoCloseable {
  private static final ThreadLocal<HepTransformations> INSTALLED = new ThreadLocal<>();

  private final Map<String, Integer> counts = new LinkedHashMap<>();
  private final int ceiling;
  private int total;

  private HepTransformations(int ceiling) {
    this.ceiling = ceiling;
  }

  /**
   * Installs a counter on this thread until it is closed, and returns it. Nested installations are
   * not a thing this supports: the second one replaces the first, which is what a test wants and
   * what nothing in the sidecar does.
   */
  public static HepTransformations install() {
    return install(Integer.MAX_VALUE);
  }

  /**
   * The same, ending the run with an {@link IllegalStateException} once {@code ceiling}
   * transformations have been produced.
   */
  public static HepTransformations install(int ceiling) {
    HepTransformations counter = new HepTransformations(ceiling);
    INSTALLED.set(counter);
    return counter;
  }

  /** Attaches this thread's counter, if it has one, to a Hep planner about to run. */
  public static void attach(RelOptPlanner planner) {
    @Nullable HepTransformations counter = INSTALLED.get();
    if (counter != null) {
      planner.addListener(counter);
    }
  }

  @Override
  public void close() {
    INSTALLED.remove();
  }

  /** How many transformations every rule whose name contains {@code substring} produced. */
  public int of(String substring) {
    int total = 0;
    for (Map.Entry<String, Integer> entry : counts.entrySet()) {
      if (entry.getKey().contains(substring)) {
        total += entry.getValue();
      }
    }
    return total;
  }

  /** How many transformations every rule produced, over every Hep phase of this request. */
  public int total() {
    return total;
  }

  /** The rules that transformed anything, most first, as {@code name=count} — for a message. */
  public List<String> byRule() {
    List<Map.Entry<String, Integer>> entries = new ArrayList<>(counts.entrySet());
    entries.sort((a, b) -> Integer.compare(b.getValue(), a.getValue()));
    List<String> lines = new ArrayList<>(entries.size());
    for (Map.Entry<String, Integer> entry : entries) {
      lines.add(entry.getKey() + "=" + entry.getValue());
    }
    return lines;
  }

  @Override
  public void ruleProductionSucceeded(RuleProductionEvent event) {
    if (!event.isBefore() || event.getRuleCall() == null || event.getRuleCall().getRule() == null) {
      return;
    }
    counts.merge(event.getRuleCall().getRule().toString(), 1, Integer::sum);
    total++;
    if (total > ceiling) {
      throw new IllegalStateException(
          "the Hep pre-pass produced more than "
              + ceiling
              + " transformations and has not reached a fixpoint; by rule: "
              + byRule());
    }
  }

  @Override
  public void relEquivalenceFound(RelEquivalenceEvent event) {
    // Not a transformation.
  }

  @Override
  public void ruleAttempted(RuleAttemptedEvent event) {
    // HepPlanner does not raise these; a match that produces nothing is not what F66 counts.
  }

  @Override
  public void relDiscarded(RelDiscardedEvent event) {
    // Nothing to record.
  }

  @Override
  public void relChosen(RelChosenEvent event) {
    // Nothing to record.
  }
}
