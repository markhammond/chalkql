package chalk.planner.plan.rules;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.rel.ChalkIntersect;
import chalk.planner.plan.rel.ChalkMinus;
import chalk.planner.plan.rel.ChalkUnion;
import java.util.List;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.convert.ConverterRule;
import org.apache.calcite.rel.core.SetOp;
import org.apache.calcite.rel.logical.LogicalIntersect;
import org.apache.calcite.rel.logical.LogicalMinus;
import org.apache.calcite.rel.logical.LogicalUnion;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * One converter rule per set operation (D69, {@code 15-zero-allocation-execution.md} §6). Each
 * simply re-homes the logical node in {@code ChalkConvention.LOCAL}; the inputs are converted by the
 * planner's own {@code AbstractConverter}s, as they are for every other n-ary rel.
 */
public final class ChalkSetOpRules {
  private ChalkSetOpRules() {}

  /** {@code LogicalUnion} → {@code ChalkUnion}. */
  public static final ConverterRule UNION =
      ConverterRule.Config.INSTANCE
          .withConversion(
              LogicalUnion.class, Convention.NONE, ChalkConvention.LOCAL, "ChalkUnionRule")
          .withRuleFactory(UnionRule::new)
          .toRule(UnionRule.class);

  /** {@code LogicalIntersect} → {@code ChalkIntersect}. */
  public static final ConverterRule INTERSECT =
      ConverterRule.Config.INSTANCE
          .withConversion(
              LogicalIntersect.class, Convention.NONE, ChalkConvention.LOCAL, "ChalkIntersectRule")
          .withRuleFactory(IntersectRule::new)
          .toRule(IntersectRule.class);

  /** {@code LogicalMinus} → {@code ChalkMinus}. */
  public static final ConverterRule MINUS =
      ConverterRule.Config.INSTANCE
          .withConversion(
              LogicalMinus.class, Convention.NONE, ChalkConvention.LOCAL, "ChalkMinusRule")
          .withRuleFactory(MinusRule::new)
          .toRule(MinusRule.class);

  /** The inputs, asked for in this convention; the planner inserts the converters. */
  private static List<RelNode> converted(SetOp rel) {
    return rel.getInputs().stream()
        .map(input -> org.apache.calcite.plan.RelOptRule.convert(
            input, input.getTraitSet().replace(ChalkConvention.LOCAL)))
        .toList();
  }

  /** {@code LogicalUnion} → {@code ChalkUnion}. */
  public static final class UnionRule extends ConverterRule {
    private UnionRule(Config config) {
      super(config);
    }

    @Override
    public @Nullable RelNode convert(RelNode rel) {
      LogicalUnion union = (LogicalUnion) rel;
      return ChalkUnion.create(union.getCluster(), converted(union), union.all);
    }
  }

  /** {@code LogicalIntersect} → {@code ChalkIntersect}. */
  public static final class IntersectRule extends ConverterRule {
    private IntersectRule(Config config) {
      super(config);
    }

    @Override
    public @Nullable RelNode convert(RelNode rel) {
      LogicalIntersect intersect = (LogicalIntersect) rel;
      return ChalkIntersect.create(intersect.getCluster(), converted(intersect), intersect.all);
    }
  }

  /** {@code LogicalMinus} → {@code ChalkMinus}. */
  public static final class MinusRule extends ConverterRule {
    private MinusRule(Config config) {
      super(config);
    }

    @Override
    public @Nullable RelNode convert(RelNode rel) {
      LogicalMinus minus = (LogicalMinus) rel;
      return ChalkMinus.create(minus.getCluster(), converted(minus), minus.all);
    }
  }
}
