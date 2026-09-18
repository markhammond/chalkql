package chalk.planner.plan.rules;

import com.google.common.collect.ImmutableList;
import com.google.common.collect.ImmutableSet;
import java.util.HashSet;
import java.util.Set;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelRule;
import org.apache.calcite.plan.hep.HepRelVertex;
import org.apache.calcite.plan.volcano.RelSubset;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Correlate;
import org.apache.calcite.rel.core.CorrelationId;
import org.apache.calcite.rel.logical.LogicalCorrelate;
import org.apache.calcite.rel.logical.LogicalJoin;
import org.apache.calcite.rex.RexCorrelVariable;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexSubQuery;

/**
 * A {@code Correlate} whose right side reads nothing of the left is an ordinary join (F79).
 *
 * <p>A correlate <em>is</em> a lateral join, and what makes it one rather than a join is the
 * correlation variable: the right side is re-derived per left row because it names a column of that
 * row. A right side that names none produces the same relation for every left row, so the correlate
 * means exactly {@code left JOIN right ON TRUE} of the same join type — a cross join for
 * {@code INNER}, an outer join for {@code LEFT}, and the semi- and anti-joins of a right side that
 * is a constant relation. Chalk refuses a surviving correlate outright
 * ({@link chalk.planner.plan.CorrelateSupport}, D67), so what this rule turns into a join was a
 * {@code UNSUPPORTED} refusal and never a plan.
 *
 * <p><b>How one is reached.</b> Nothing a statement writes produces it: a correlated sub-query names
 * the outer row by construction. An <b>entitlement</b> does. A leaf that folds to nothing prunes
 * the body of the sub-query with it (F70, ADR 0054), and pruning takes the correlated predicate —
 * the only thing that read the outer row — along with everything else. Where the body is an
 * {@code EXISTS} the whole right side becomes an empty {@code Values} and
 * {@code PruneEmptyRules.CORRELATE_*} answers. Where it is a <b>count</b>, it does not:
 * {@code COUNT} over nothing is {@code 0}, not nothing, so the aggregate stands over the empty
 * {@code Values}, the right side is one row rather than none, and the correlate the prune rules have
 * no shape for is left for {@code CorrelateSupport} to refuse. The statement is
 * {@code SELECT o.id, (SELECT COUNT(*) FROM lines WHERE …) FROM orders o} read by a principal who
 * reaches no line, and its answer is a count of zero beside every visible order.
 *
 * <p>{@code UNNEST} is the one correlate Chalk keeps ({@link ChalkUnnestRule}, D66), and it reads
 * the correlation variable — it is a flatten of the left row's own list — so this rule declines it.
 * Measured: with the reference read through Calcite's own {@code getVariablesUsed} it does not, and
 * the four {@code UNNEST} shapes of {@code UnnestRuleTest} lose the operator that flattens them.
 * See {@link #reads}.
 */
public final class UncorrelatedCorrelateRule extends RelRule<ChalkRuleConfig> {

  public static final UncorrelatedCorrelateRule INSTANCE =
      new UncorrelatedCorrelateRule(
          ChalkRuleConfig.of(
              "UncorrelatedCorrelateRule",
              b -> b.operand(LogicalCorrelate.class).anyInputs(),
              UncorrelatedCorrelateRule::new));

  private UncorrelatedCorrelateRule(ChalkRuleConfig config) {
    super(config);
  }

  @Override
  public boolean matches(RelOptRuleCall call) {
    Correlate correlate = call.rel(0);
    return !reads(correlate.getRight(), correlate.getCorrelationId());
  }

  /**
   * Whether anything under {@code rel} names {@code id}.
   *
   * <p>Walked here rather than through {@code RelOptUtil.getVariablesUsed}, which reads a
   * {@code RelShuttle} over {@code getInputs()} and so sees nothing at all under a planner's own
   * placeholder: the inputs of a rel a rule matched are a {@code HepRelVertex} in the Hep phases and
   * a {@code RelSubset} under Volcano, and both report no inputs. Answering "reads nothing" for
   * every correlate is the wrong direction for this rule — it is what turned an {@code UNNEST}'s
   * correlate, which names the left row's list and is the one correlate Chalk keeps
   * ({@link ChalkUnnestRule}, D66), into a join with nothing left to flatten it.
   *
   * <p>{@code requiredColumns} is not the question either: it records what the correlate was built
   * to carry, and stays as it was when the pruning took the only reference away.
   */
  private static boolean reads(RelNode rel, CorrelationId id) {
    RelNode node = unwrap(rel);
    Set<CorrelationId> used = new HashSet<>();
    node.collectVariablesUsed(used);
    if (used.contains(id)) {
      return true;
    }

    boolean[] found = {false};
    node.accept(
        new RexShuttle() {
          @Override
          public RexNode visitCorrelVariable(RexCorrelVariable variable) {
            found[0] |= variable.id.equals(id);
            return variable;
          }

          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            found[0] |= reads(subQuery.rel, id);
            return super.visitSubQuery(subQuery);
          }
        });
    if (found[0]) {
      return true;
    }

    for (RelNode input : node.getInputs()) {
      if (reads(input, id)) {
        return true;
      }
    }

    return false;
  }

  private static RelNode unwrap(RelNode rel) {
    if (rel instanceof HepRelVertex vertex) {
      return vertex.getCurrentRel();
    }
    if (rel instanceof RelSubset subset) {
      RelNode best = subset.getBest();
      return best != null ? best : subset.getOriginal();
    }
    return rel;
  }

  @Override
  public void onMatch(RelOptRuleCall call) {
    Correlate correlate = call.rel(0);
    RelNode join =
        LogicalJoin.create(
            correlate.getLeft(),
            correlate.getRight(),
            ImmutableList.of(),
            correlate.getCluster().getRexBuilder().makeLiteral(true),
            ImmutableSet.of(),
            correlate.getJoinType());
    call.transformTo(join);
  }
}
