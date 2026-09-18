package chalk.planner.plan.rules;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.rel.ChalkProject;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.convert.ConverterRule;
import org.apache.calcite.rel.logical.LogicalProject;
import org.checkerframework.checker.nullness.qual.Nullable;

/** {@code LogicalProject} → {@code ChalkProject}. */
public final class ChalkProjectRule extends ConverterRule {
  public static final ChalkProjectRule INSTANCE =
      Config.INSTANCE
          .withConversion(
              LogicalProject.class, Convention.NONE, ChalkConvention.LOCAL, "ChalkProjectRule")
          .withRuleFactory(ChalkProjectRule::new)
          .toRule(ChalkProjectRule.class);

  private ChalkProjectRule(Config config) {
    super(config);
  }

  @Override
  public @Nullable RelNode convert(RelNode rel) {
    LogicalProject project = (LogicalProject) rel;
    return ChalkProject.create(
        ChalkInputs.unordered(project.getInput()), project.getProjects(), project.getRowType());
  }
}
