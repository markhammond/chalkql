package chalk.planner.plan.rules;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.rel.ChalkNestedLoopJoin;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.convert.ConverterRule;
import org.apache.calcite.rel.logical.LogicalJoin;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code LogicalJoin} → {@code ChalkNestedLoopJoin}, always. Costed at left × right, so it wins only
 * when nothing else applies — a condition with no equality in it, or a cross join (D43).
 */
public final class ChalkNestedLoopJoinRule extends ConverterRule {
  public static final ChalkNestedLoopJoinRule INSTANCE =
      Config.INSTANCE
          .withConversion(
              LogicalJoin.class, Convention.NONE, ChalkConvention.LOCAL, "ChalkNestedLoopJoinRule")
          .withRuleFactory(ChalkNestedLoopJoinRule::new)
          .toRule(ChalkNestedLoopJoinRule.class);

  private ChalkNestedLoopJoinRule(Config config) {
    super(config);
  }

  @Override
  public @Nullable RelNode convert(RelNode rel) {
    LogicalJoin join = (LogicalJoin) rel;
    if (!ChalkInputs.isExpressible(join.getJoinType())) {
      return null;
    }

    return ChalkNestedLoopJoin.create(
        ChalkInputs.unordered(join.getLeft()),
        ChalkInputs.unordered(join.getRight()),
        join.getCondition(),
        join.getVariablesSet(),
        join.getJoinType());
  }
}
