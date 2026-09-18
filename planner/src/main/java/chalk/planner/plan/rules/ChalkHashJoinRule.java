package chalk.planner.plan.rules;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.rel.ChalkHashJoin;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.convert.ConverterRule;
import org.apache.calcite.rel.core.JoinInfo;
import org.apache.calcite.rel.logical.LogicalJoin;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code LogicalJoin} → {@code ChalkHashJoin}, whenever the condition yields at least one equality
 * between a left and a right column (D43). What is left over becomes the join's {@code
 * post_join_filter}, which {@code RelToIr} recovers from the same {@code JoinInfo}.
 *
 * <p>Every join type: the operator builds a hash table on the right input and streams the left one
 * through it, tracking matched build rows for the outer passes.
 */
public final class ChalkHashJoinRule extends ConverterRule {
  public static final ChalkHashJoinRule INSTANCE =
      Config.INSTANCE
          .withConversion(
              LogicalJoin.class, Convention.NONE, ChalkConvention.LOCAL, "ChalkHashJoinRule")
          .withRuleFactory(ChalkHashJoinRule::new)
          .toRule(ChalkHashJoinRule.class);

  private ChalkHashJoinRule(Config config) {
    super(config);
  }

  @Override
  public @Nullable RelNode convert(RelNode rel) {
    LogicalJoin join = (LogicalJoin) rel;
    if (!ChalkInputs.isExpressible(join.getJoinType())) {
      return null;
    }

    JoinInfo info = join.analyzeCondition();
    if (info.leftKeys.isEmpty()) {
      return null; // no equality to hash on; ChalkNestedLoopJoinRule covers it
    }

    // V50: a null-safe equality reads back as a plain key, so hashing on it would drop the NULL
    // matches. ChalkNestedLoopJoinRule carries the condition whole instead.
    if (ChalkInputs.hasNullSafeEquality(join)) {
      return null;
    }

    return ChalkHashJoin.create(
        ChalkInputs.unordered(join.getLeft()),
        ChalkInputs.unordered(join.getRight()),
        join.getCondition(),
        join.getVariablesSet(),
        join.getJoinType());
  }
}
