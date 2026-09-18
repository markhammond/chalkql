package chalk.planner.plan.rules;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.rel.ChalkFilter;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.convert.ConverterRule;
import org.apache.calcite.rel.logical.LogicalFilter;
import org.checkerframework.checker.nullness.qual.Nullable;

/** {@code LogicalFilter} → {@code ChalkFilter}. */
public final class ChalkFilterRule extends ConverterRule {
  public static final ChalkFilterRule INSTANCE =
      Config.INSTANCE
          .withConversion(
              LogicalFilter.class, Convention.NONE, ChalkConvention.LOCAL, "ChalkFilterRule")
          .withRuleFactory(ChalkFilterRule::new)
          .toRule(ChalkFilterRule.class);

  private ChalkFilterRule(Config config) {
    super(config);
  }

  @Override
  public @Nullable RelNode convert(RelNode rel) {
    LogicalFilter filter = (LogicalFilter) rel;
    return ChalkFilter.create(ChalkInputs.unordered(filter.getInput()), filter.getCondition());
  }
}
