package chalk.planner.plan.rules;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.rel.ChalkValues;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.convert.ConverterRule;
import org.apache.calcite.rel.logical.LogicalValues;
import org.checkerframework.checker.nullness.qual.Nullable;

/** {@code LogicalValues} → {@code ChalkValues}. */
public final class ChalkValuesRule extends ConverterRule {
  public static final ChalkValuesRule INSTANCE =
      Config.INSTANCE
          .withConversion(
              LogicalValues.class, Convention.NONE, ChalkConvention.LOCAL, "ChalkValuesRule")
          .withRuleFactory(ChalkValuesRule::new)
          .toRule(ChalkValuesRule.class);

  private ChalkValuesRule(Config config) {
    super(config);
  }

  @Override
  public @Nullable RelNode convert(RelNode rel) {
    LogicalValues values = (LogicalValues) rel;
    return ChalkValues.create(values.getCluster(), values.getRowType(), values.getTuples());
  }
}
