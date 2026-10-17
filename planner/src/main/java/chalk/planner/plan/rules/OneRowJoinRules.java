package chalk.planner.plan.rules;

import java.util.List;
import org.apache.calcite.plan.RelOptRule;
import org.apache.calcite.plan.RelRule;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.AsofJoin;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.core.Values;
import org.apache.calcite.rel.rules.SingleValuesOptimizationRules;
import org.apache.calcite.rex.RexUtil;

/**
 * A join with a one-row constant table is a projection of its other side (D348): the row's values
 * become literals in the projection, and the join condition, with them put in, a filter of the other
 * side, or for an outer join a {@code CASE} in the projection. These are Calcite's
 * {@link SingleValuesOptimizationRules}, held to the joins a single row answers exactly.
 *
 * <p><b>Why.</b> {@code WITH reporting AS (SELECT CAST('EUR' AS VARCHAR(32)) AS symbol)}, crossed
 * into a statement, is a one-row table whose column the statement reads as a constant. Left as a
 * join, the planner folds the column into the constant where it is read, the table's columns go
 * unused, and {@code JOIN_ON_UNIQUE_TO_SEMI_JOIN} turns the cross join into a semi join against one
 * row that nothing in the statement asked for, which an incremental view does not keep. Replaced by
 * its literals, the table never becomes a join.
 *
 * <p><b>What they are kept off.</b>
 *
 * <ul>
 *   <li>An ASOF join, which Calcite counts as a {@link Join}: its match condition and its
 *       "closest row" are not in the condition the rewrite reads, so a one-row side would become a
 *       filter that keeps every row the keys match rather than the closest one.
 *   <li>A one-row side computed by an expression that is not deterministic, which the rewrite would
 *       compute once for each row of the other side rather than once.
 *   <li>Every join type but those a single row answers exactly: an inner join from either side, the
 *       outer join that keeps the other side, and a semi join whose one row is its right side. A
 *       semi join whose one row is its <em>left</em> side keeps that row once however many rows of
 *       the right match, which a filter of the right does not; Calcite's own check lets it through.
 * </ul>
 *
 * <p>Each rule reads its operands by position, so each operand tree here is the one Calcite's own
 * instance declares, with these predicates added.
 */
public final class OneRowJoinRules {
  private OneRowJoinRules() {}

  /** A one-row {@code VALUES} on the left. */
  public static final RelOptRule LEFT =
      narrow(
          SingleValuesOptimizationRules.JOIN_LEFT_INSTANCE,
          "OneRowJoin(left)",
          b0 ->
              b0.operand(Join.class)
                  .predicate(OneRowJoinRules::oneRowLeft)
                  .inputs(
                      b1 -> b1.operand(Values.class).predicate(Values::isSingleValue).noInputs(),
                      b2 -> b2.operand(RelNode.class).anyInputs()));

  /** A one-row {@code VALUES} on the right. */
  public static final RelOptRule RIGHT =
      narrow(
          SingleValuesOptimizationRules.JOIN_RIGHT_INSTANCE,
          "OneRowJoin(right)",
          b0 ->
              b0.operand(Join.class)
                  .predicate(OneRowJoinRules::oneRowRight)
                  .inputs(
                      b1 -> b1.operand(RelNode.class).anyInputs(),
                      b2 -> b2.operand(Values.class).predicate(Values::isSingleValue).noInputs()));

  /** A projection of one row on the left: a constant the {@code VALUES} cannot hold, such as a cast. */
  public static final RelOptRule LEFT_PROJECT =
      narrow(
          SingleValuesOptimizationRules.JOIN_LEFT_PROJECT_INSTANCE,
          "OneRowJoin(left, projected)",
          b0 ->
              b0.operand(Join.class)
                  .predicate(OneRowJoinRules::oneRowLeft)
                  .inputs(
                      b1 ->
                          b1.operand(Project.class)
                              .predicate(OneRowJoinRules::deterministic)
                              .oneInput(
                                  b2 ->
                                      b2.operand(Values.class)
                                          .predicate(Values::isSingleValue)
                                          .noInputs()),
                      b3 -> b3.operand(RelNode.class).anyInputs()));

  /** A projection of one row on the right. */
  public static final RelOptRule RIGHT_PROJECT =
      narrow(
          SingleValuesOptimizationRules.JOIN_RIGHT_PROJECT_INSTANCE,
          "OneRowJoin(right, projected)",
          b0 ->
              b0.operand(Join.class)
                  .predicate(OneRowJoinRules::oneRowRight)
                  .inputs(
                      b1 -> b1.operand(RelNode.class).anyInputs(),
                      b2 ->
                          b2.operand(Project.class)
                              .predicate(OneRowJoinRules::deterministic)
                              .oneInput(
                                  b3 ->
                                      b3.operand(Values.class)
                                          .predicate(Values::isSingleValue)
                                          .noInputs())));

  /** The four, in the order a rule list takes them. */
  public static final List<RelOptRule> ALL = List.of(LEFT, RIGHT, LEFT_PROJECT, RIGHT_PROJECT);

  /** An inner join, or the right join that keeps the other side, of one row on the left. */
  private static boolean oneRowLeft(Join join) {
    if (join instanceof AsofJoin) {
      return false;
    }

    return switch (join.getJoinType()) {
      case INNER, RIGHT -> true;
      default -> false;
    };
  }

  /** An inner join, the left join that keeps the other side, or a semi join, of one row on the right. */
  private static boolean oneRowRight(Join join) {
    if (join instanceof AsofJoin) {
      return false;
    }

    return switch (join.getJoinType()) {
      case INNER, LEFT, SEMI -> true;
      default -> false;
    };
  }

  /** A projection whose every expression gives the same value however many times it is computed. */
  private static boolean deterministic(Project project) {
    return project.getProjects().stream().allMatch(RexUtil::isDeterministic);
  }

  /** Calcite's rule, with its own operand tree and these predicates, under a name of its own. */
  private static RelOptRule narrow(
      RelOptRule rule, String description, RelRule.OperandTransform operands) {
    return ((RelRule<?>) rule)
        .config
        .withDescription(description)
        .withOperandSupplier(operands)
        .toRule();
  }
}
