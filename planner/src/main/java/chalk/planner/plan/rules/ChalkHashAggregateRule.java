package chalk.planner.plan.rules;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.rel.ChalkHashAggregate;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.convert.ConverterRule;
import org.apache.calcite.rel.logical.LogicalAggregate;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code LogicalAggregate} → {@code ChalkHashAggregate}, grouping sets included (D338).
 */
public final class ChalkHashAggregateRule extends ConverterRule {
  public static final ChalkHashAggregateRule INSTANCE =
      Config.INSTANCE
          .withConversion(
              LogicalAggregate.class,
              Convention.NONE,
              ChalkConvention.LOCAL,
              "ChalkHashAggregateRule")
          .withRuleFactory(ChalkHashAggregateRule::new)
          .toRule(ChalkHashAggregateRule.class);

  private ChalkHashAggregateRule(Config config) {
    super(config);
  }

  @Override
  public @Nullable RelNode convert(RelNode rel) {
    LogicalAggregate aggregate = (LogicalAggregate) rel;
    return ChalkHashAggregate.create(
        ChalkInputs.unordered(aggregate.getInput()),
        aggregate.getGroupSet(),
        aggregate.getGroupSets(),
        aggregate.getAggCallList());
  }
}
