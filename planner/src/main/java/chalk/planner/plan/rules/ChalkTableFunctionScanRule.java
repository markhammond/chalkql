package chalk.planner.plan.rules;

import chalk.planner.catalog.UserFunction;
import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.UserOperators;
import chalk.planner.plan.rel.ChalkTableFunctionScan;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.convert.ConverterRule;
import org.apache.calcite.rel.logical.LogicalTableFunctionScan;
import org.apache.calcite.rex.RexCall;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code LogicalTableFunctionScan} → {@code ChalkTableFunctionScan} (D78). Calcite builds the
 * logical node for {@code TABLE(f(args))} when {@code f} is a table <em>function</em>; a table
 * <em>macro</em> has already expanded into a sub-query and never reaches here.
 */
public final class ChalkTableFunctionScanRule extends ConverterRule {
  public static final ChalkTableFunctionScanRule INSTANCE =
      Config.INSTANCE
          .withConversion(
              LogicalTableFunctionScan.class,
              Convention.NONE,
              ChalkConvention.LOCAL,
              "ChalkTableFunctionScanRule")
          .withRuleFactory(ChalkTableFunctionScanRule::new)
          .toRule(ChalkTableFunctionScanRule.class);

  private ChalkTableFunctionScanRule(Config config) {
    super(config);
  }

  @Override
  public @Nullable RelNode convert(RelNode rel) {
    LogicalTableFunctionScan scan = (LogicalTableFunctionScan) rel;
    if (!(scan.getCall() instanceof RexCall call)) {
      return null;
    }

    // Declining rather than refusing: the window table functions HOP and SESSION are
    // LogicalTableFunctionScans too, and they have their own rule. WindowSupport is what turns a
    // table function nothing can convert into a message.
    UserFunction declaration = UserOperators.declarationOf(call.getOperator());
    if (declaration == null || !scan.getInputs().isEmpty()) {
      return null;
    }

    return ChalkTableFunctionScan.create(
        scan.getCluster(), call, scan.getRowType(), declaration);
  }
}
