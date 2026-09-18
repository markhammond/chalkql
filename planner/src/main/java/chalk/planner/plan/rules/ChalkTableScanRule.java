package chalk.planner.plan.rules;

import chalk.planner.catalog.ChalkTable;
import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.rel.ChalkTableScan;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.convert.ConverterRule;
import org.apache.calcite.rel.logical.LogicalTableScan;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code LogicalTableScan} → {@code ChalkTableScan} over every column: the client reads the table
 * itself.
 *
 * <p>For a source that takes queries this is one of two futures, and the request's pushdown level
 * picks which (D83). At {@code PUSHDOWN_LEVEL_NONE} this rule is the only one registered and a
 * remote table is read through its scan path, which is exactly what the I4 oracle needs; at every
 * other level {@code PushdownRules.ScanRule} takes the table into its source's convention and this
 * rule declines, so there is one answer rather than a cost comparison between two spellings of the
 * same fetch.
 */
public final class ChalkTableScanRule extends ConverterRule {
  /** The M1–M3 instance: every table is scanned locally. */
  public static final ChalkTableScanRule INSTANCE = of(PushdownPolicy.none());

  private final PushdownPolicy policy;

  /** The rule for one request's level. */
  public static ChalkTableScanRule of(PushdownPolicy policy) {
    return new ChalkTableScanRule(
        Config.INSTANCE
            .withConversion(
                LogicalTableScan.class, Convention.NONE, ChalkConvention.LOCAL, "ChalkTableScanRule")
            .as(Config.class),
        policy);
  }

  private ChalkTableScanRule(Config config, PushdownPolicy policy) {
    super(config);
    this.policy = policy;
  }

  @Override
  public @Nullable RelNode convert(RelNode rel) {
    LogicalTableScan scan = (LogicalTableScan) rel;
    // A context relation is not a catalog table: ChalkContextScanRule owns it.
    if (scan.getTable().unwrap(chalk.planner.entitlement.ContextTable.class) != null) {
      return null;
    }
    ChalkTable table = scan.getTable().unwrap(ChalkTable.class);
    if (table != null && table.takesQueries() && policy.allowsRemotePushdown()) {
      return null;
    }
    return ChalkTableScan.create(scan.getCluster(), scan.getTable());
  }
}
