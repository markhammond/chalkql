package chalk.planner.plan;

import java.util.List;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexExecutor;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexVisitorImpl;

/**
 * Constant folding, minus the expressions the planner has no business evaluating (D78, V32).
 *
 * <p>{@code ReduceExpressionsRule} asks the executor to evaluate anything it believes is constant,
 * and Calcite's {@code RexExecutorImpl} does that by compiling the expression to Java. A user
 * function has no Java behind it — Chalk's whole point is that the implementation lives in the host
 * process or in a source — so a call to one is <em>skipped</em> here rather than compiled: an
 * unreduced expression is always a correct answer, and the alternative is a compilation failure in
 * the middle of a rule.
 *
 * <p>Only the calls are skipped, not whole batches: an expression standing beside a user-function
 * call still folds. A SQL-bodied function is not affected at all, because by the time any rule runs
 * its call is gone and what is left is built-ins.
 *
 * <p>The delegate is called <em>once</em>, with the whole reducible batch. Calcite's
 * {@code RexExecutorImpl.reduce} asserts that the list it is handed ends up holding exactly one
 * value per expression it was given, so it cannot be called repeatedly against one accumulating
 * list; and one call with everything is also what the planner did before step 22, so no plan that
 * has no user function in it folds differently.
 */
public final class ChalkRexExecutor implements RexExecutor {
  private final RexExecutor delegate;

  public ChalkRexExecutor(RexExecutor delegate) {
    this.delegate = delegate;
  }

  @Override
  public void reduce(RexBuilder rexBuilder, List<RexNode> constExps, List<RexNode> reducedValues) {
    List<RexNode> reducible = new java.util.ArrayList<>(constExps.size());
    for (RexNode expression : constExps) {
      if (!callsUserFunction(expression)) {
        reducible.add(expression);
      }
    }

    if (reducible.size() == constExps.size()) {
      delegate.reduce(rexBuilder, constExps, reducedValues);
      return;
    }
    if (reducible.isEmpty()) {
      // Leaving an expression as it was is a legal answer: it means nothing was folded.
      reducedValues.addAll(constExps);
      return;
    }

    List<RexNode> reduced = new java.util.ArrayList<>(reducible.size());
    delegate.reduce(rexBuilder, reducible, reduced);
    int next = 0;
    for (RexNode expression : constExps) {
      reducedValues.add(callsUserFunction(expression) ? expression : reduced.get(next++));
    }
  }

  /** Whether anything under {@code expression} is a call to a function the catalog declared. */
  public static boolean callsUserFunction(RexNode expression) {
    Finder finder = new Finder();
    expression.accept(finder);
    return finder.found;
  }

  private static final class Finder extends RexVisitorImpl<Void> {
    private boolean found;

    Finder() {
      super(true);
    }

    @Override
    public Void visitCall(RexCall call) {
      if (UserOperators.declarationOf(call.getOperator()) != null) {
        found = true;
      }
      return super.visitCall(call);
    }
  }
}
