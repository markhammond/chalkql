package chalk.planner.diag;

import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;
import org.apache.calcite.plan.RelOptListener;

/**
 * Records which rules actually produced an equivalence during planning, for {@code
 * PlanningStats.rules_fired}. A {@link LinkedHashSet} rather than a plain list: the interesting
 * information is which rules participated, in the order they first did, and repetition is noise.
 *
 * <p><b>This is not the evaluation count.</b> What this records is the set of rules that
 * <em>produced</em> something, from {@code ruleProductionSucceeded}. {@link PlanningGovernor} counts
 * something else and counts it as a number: rule matches <em>fired</em>, one per {@code
 * ruleAttempted} "before" event, which is one per {@code VolcanoRuleCall.onMatch} (D235). A match
 * that fires and produces nothing is an evaluation and is not in this set, so the two never agree
 * and are not meant to.
 */
public final class RuleTrace implements RelOptListener {
  private final Set<String> fired = new LinkedHashSet<>();

  public List<String> rulesFired() {
    return new ArrayList<>(fired);
  }

  /** Forgets what it saw, for a pipeline the sidecar re-enters with another binding (D233). */
  public void reset() {
    fired.clear();
  }

  @Override
  public void relEquivalenceFound(RelEquivalenceEvent event) {
    // Not a rule firing; ignore.
  }

  @Override
  public void ruleAttempted(RuleAttemptedEvent event) {
    // Only successful productions are interesting.
  }

  @Override
  public void ruleProductionSucceeded(RuleProductionEvent event) {
    if (event.getRuleCall() != null && event.getRuleCall().getRule() != null) {
      fired.add(event.getRuleCall().getRule().toString());
    }
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
