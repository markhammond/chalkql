package chalk.planner.plan.rules;

import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.rel.ChalkTableScan;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelRule;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.util.ImmutableIntList;

/**
 * Turns {@code Project(TableScan)} — where the projection is a plain selection of distinct columns —
 * into a scan that reads only those columns, which is the IR's {@code Read.projection}.
 *
 * <p>This is the whole of {@code PushdownLevel} in M1 (docs/design/03-planner.md §4.3): at {@code
 * FULL} and {@code PROJECTION_ONLY} the scan prunes, at {@code NONE} and {@code FILTERS_ONLY} it
 * reads the full table row and a {@code ChalkProject} above it does the trimming. That difference is
 * what makes the reference (I4) configuration a genuinely different execution rather than the same
 * plan with a flag.
 *
 * <p>Renaming is fine — {@code SELECT symbol AS s} still prunes — because Volcano compares row types
 * structurally, not by field name, and the plan's output names come from {@code RelRoot.fields}.
 */
public final class ChalkProjectScanRule extends RelRule<ChalkRuleConfig> {
  private final PushdownPolicy policy;

  public ChalkProjectScanRule(PushdownPolicy policy) {
    this(config(policy), policy);
  }

  private ChalkProjectScanRule(ChalkRuleConfig config, PushdownPolicy policy) {
    super(config);
    this.policy = policy;
  }

  private static ChalkRuleConfig config(PushdownPolicy policy) {
    // TableScan, not LogicalTableScan: since ChalkTable became a TranslatableTable the leaf is
    // already a ChalkTableScan by the time a Project sits above it (V-M2-1), and the trimmer still
    // makes LogicalTableScans on other paths. One operand covers both.
    return ChalkRuleConfig.of(
        "ChalkProjectScanRule",
        b ->
            b.operand(LogicalProject.class)
                .oneInput(b1 -> b1.operand(TableScan.class).noInputs()),
        config -> new ChalkProjectScanRule(config, policy));
  }

  @Override
  public boolean matches(RelOptRuleCall call) {
    return policy.allowsProjectionIntoScan();
  }

  @Override
  public void onMatch(RelOptRuleCall call) {
    LogicalProject project = call.rel(0);
    TableScan scan = call.rel(1);

    if (scan instanceof ChalkTableScan chalkScan && !isIdentity(chalkScan.projection())) {
      return; // already pruned; the projection's refs index its row, not the table's
    }

    // A source that takes queries gets its projection through PushdownRules, in its own convention
    // (D83): pruning it into a local ChalkTableScan here would put the leaf back in CHALK_LOCAL and
    // the whole subtree would stop being pushable.
    chalk.planner.catalog.ChalkTable table =
        scan.getTable().unwrap(chalk.planner.catalog.ChalkTable.class);
    if (table != null && table.takesQueries() && policy.allowsRemotePushdown()) {
      return;
    }

    List<Integer> columns = new ArrayList<>(project.getProjects().size());
    Set<Integer> distinct = new HashSet<>();
    for (RexNode expression : project.getProjects()) {
      if (!(expression instanceof RexInputRef ref)) {
        return; // a computed column; a ChalkProject has to evaluate it
      }
      if (!distinct.add(ref.getIndex())) {
        return; // SELECT a, a — one scan column cannot appear twice in the scan's output
      }
      columns.add(ref.getIndex());
    }

    if (columns.size() == scan.getRowType().getFieldCount() && isIdentity(columns)) {
      return; // nothing to prune; ChalkTableScanRule already covers this
    }

    call.transformTo(
        ChalkTableScan.create(
            scan.getCluster(), scan.getTable(), ImmutableIntList.copyOf(columns)));
  }

  private static boolean isIdentity(List<Integer> columns) {
    for (int i = 0; i < columns.size(); i++) {
      if (columns.get(i) != i) {
        return false;
      }
    }
    return true;
  }
}
