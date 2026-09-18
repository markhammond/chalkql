package chalk.planner.plan.rules;

import chalk.planner.entitlement.ContextTable;
import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.rel.ChalkContextScan;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.convert.ConverterRule;
import org.apache.calcite.rel.logical.LogicalTableScan;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code LogicalTableScan} of a {@link ContextTable} → {@code ChalkContextScan}.
 *
 * <p>A separate rule from {@link ChalkTableScanRule} because a context relation is not a catalog
 * table: it has no source, no cost profile, no index and no statistics anybody declared, and the IR
 * node it becomes carries a name rather than a table reference.
 */
public final class ChalkContextScanRule extends ConverterRule {
  public static final ChalkContextScanRule INSTANCE =
      Config.INSTANCE
          .withConversion(
              LogicalTableScan.class, Convention.NONE, ChalkConvention.LOCAL, "ChalkContextScanRule")
          .withRuleFactory(ChalkContextScanRule::new)
          .toRule(ChalkContextScanRule.class);

  private ChalkContextScanRule(Config config) {
    super(config);
  }

  @Override
  public @Nullable RelNode convert(RelNode rel) {
    LogicalTableScan scan = (LogicalTableScan) rel;
    if (scan.getTable().unwrap(ContextTable.class) == null) {
      return null;
    }
    return ChalkContextScan.create(scan.getCluster(), scan.getTable());
  }
}
