package chalk.planner.plan.rules;

import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.rel.ChalkTableScan;
import chalk.planner.plan.rel.ProjectedRelOptTable;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.plan.RelRule;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.util.ImmutableBitSet;
import org.apache.calcite.util.ImmutableIntList;
import org.apache.calcite.util.mapping.Mappings;

/**
 * Turns {@code Project(TableScan)} into a scan that reads only the columns the projection needs,
 * which is the IR's {@code Read.projection}.
 *
 * <p>A projection that is a plain selection of distinct columns becomes the pruned scan outright. A
 * projection that <em>computes</em> — {@code GT($5, $2)}, {@code FLOOR($1 TO HOUR)}, {@code a + 1} —
 * keeps its {@code Project}, but over a scan narrowed to the columns its expressions read, with
 * every {@code RexInputRef} shifted onto the narrowed row (F114). Before that the rule returned at
 * the first computed column and the scan read the whole table for a projection that used two of it:
 * the trimmer puts a column-subset {@code Project} over every scan, but where the query's own
 * projection computes, {@code RelBuilder} merges the two into one computing {@code Project} and
 * there is nothing pure left to match.
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

    if (scan.getTable().unwrap(ProjectedRelOptTable.class) != null) {
      return; // already pruned; the projection's refs index its row, not the table's
    }

    // A context relation is not a catalog table — it has no source and no cost profile — so a
    // ChalkTableScan of one is a node that cannot be costed. ChalkContextScanRule owns it, exactly
    // as it owns the bare scan under ChalkTableScanRule.
    if (scan.getTable().unwrap(chalk.planner.entitlement.ContextTable.class) != null) {
      return;
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
      if (!(expression instanceof RexInputRef ref) || !distinct.add(ref.getIndex())) {
        // A computed column, or one column twice — neither is a pure mapping, and both still name
        // the columns the scan has to read.
        pruneUnderComputation(call, project, scan);
        return;
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

  /**
   * The same pruning under a projection that is not a pure mapping: the scan reads the columns the
   * expressions index, and the expressions are rewritten to index the narrowed row (F114).
   *
   * <p>The {@code Project} stays — it is what evaluates the expression — so the plan gains nothing
   * but a narrower {@code Read}, and the alternative is registered beside the unpruned one for
   * Volcano to cost, exactly as the pure case is.
   */
  private static void pruneUnderComputation(
      RelOptRuleCall call, LogicalProject project, TableScan scan) {
    for (RexNode expression : project.getProjects()) {
      if (RexUtil.containsCorrelation(expression)) {
        return; // a correlated reference indexes the variable's row, not this scan's
      }
    }

    ImmutableBitSet used = RelOptUtil.InputFinder.bits(project.getProjects(), null);
    int width = scan.getRowType().getFieldCount();
    if (used.isEmpty() || used.cardinality() >= width) {
      // Nothing to prune; or a projection of constants alone, and a Read that reads no column at
      // all is not a Read (02-ir.md §4 — the projection is never empty).
      return;
    }

    List<Integer> read = used.asList();
    Map<Integer, Integer> positions = new HashMap<>();
    for (int i = 0; i < read.size(); i++) {
      positions.put(read.get(i), i);
    }

    ChalkTableScan pruned =
        ChalkTableScan.create(scan.getCluster(), scan.getTable(), ImmutableIntList.copyOf(read));
    call.transformTo(
        LogicalProject.create(
            pruned,
            project.getHints(),
            RexUtil.apply(Mappings.target(positions, width, read.size()), project.getProjects()),
            // The row type, not the field names: the rewritten projection must produce the very row
            // the projection it replaces did, or Volcano has two members of one set with two shapes.
            project.getRowType(),
            project.getVariablesSet()));
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
