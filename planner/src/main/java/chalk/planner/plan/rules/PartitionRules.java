package chalk.planner.plan.rules;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.rel.ChalkPartitionedScan;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelRule;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.convert.ConverterRule;
import org.apache.calcite.rel.core.Filter;
import org.apache.calcite.rel.logical.LogicalFilter;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.sql.SqlKind;
import org.checkerframework.checker.nullness.qual.Nullable;

/** The two rules a partitioned table needs (D106, {@code 20-m5-federation.md} §3). */
public final class PartitionRules {
  private PartitionRules() {}

  /** Drops the partitions a predicate on the partition column cannot match. */
  public static final PartitionPruneRule PRUNE = PartitionPruneRule.create();

  /** {@code ChalkPartitionedScan} in {@code NONE} → the same node in {@code LOCAL}. */
  public static final ChalkPartitionedScanRule CONVERT = new ChalkPartitionedScanRule();

  /** Pushes a projection into every partition, so a branch fetches only the columns asked for. */
  public static final PartitionProjectTransposeRule PROJECT =
      PartitionProjectTransposeRule.create();

  /**
   * {@code Project(PartitionedScan)} → {@code PartitionedScan(Project, …)}.
   *
   * <p>{@code RelFieldTrimmer} would normally do this — it is what prunes a scan's columns
   * everywhere else — but it prunes by rebuilding, and rebuilding a partitioned scan loses the
   * partition values it exists to carry. So the pruning is done here instead, on the one shape that
   * matters: a projection of plain column references, which is what a trimmed scan is.
   *
   * <p>Only when the projection actually narrows. A projection that reorders or renames the same
   * columns is left alone, because pushing it would loop against itself.
   */
  public static final class PartitionProjectTransposeRule extends RelRule<ChalkRuleConfig> {
    private PartitionProjectTransposeRule(ChalkRuleConfig config) {
      super(config);
    }

    private static PartitionProjectTransposeRule create() {
      return new PartitionProjectTransposeRule(
          ChalkRuleConfig.of(
              "PartitionProjectTransposeRule",
              b ->
                  b.operand(LogicalProject.class)
                      .oneInput(b1 -> b1.operand(ChalkPartitionedScan.class).anyInputs()),
              PartitionProjectTransposeRule::new));
    }

    @Override
    public void onMatch(RelOptRuleCall call) {
      LogicalProject project = call.rel(0);
      ChalkPartitionedScan scan = call.rel(1);
      if (project.getProjects().size() >= scan.getRowType().getFieldCount()) {
        return;
      }

      List<Integer> columns = new ArrayList<>(project.getProjects().size());
      for (RexNode node : project.getProjects()) {
        if (!(node instanceof RexInputRef ref)) {
          return;
        }
        columns.add(ref.getIndex());
      }

      // The partition column has to survive: execution-time pruning reads it, and so does the
      // pruning rule if it fires again after this one.
      if (!columns.contains(scan.partitionColumn())) {
        return;
      }

      List<RelNode> projected = new ArrayList<>(scan.getInputs().size());
      for (RelNode input : scan.getInputs()) {
        List<RexNode> refs = new ArrayList<>(columns.size());
        for (int column : columns) {
          refs.add(
              input.getCluster().getRexBuilder()
                  .makeInputRef(input.getRowType().getFieldList().get(column).getType(), column));
        }

        projected.add(
            LogicalProject.create(
                input, java.util.List.of(), refs, project.getRowType().getFieldNames(),
                java.util.Set.of()));
      }

      call.transformTo(scan.projected(projected, project.getRowType()));
    }
  }

  /**
   * Partition pruning at planning time: a {@code WHERE} on the partition column keeps only the
   * partitions whose value it can match.
   *
   * <p>Runs in the Hep pre-pass, after {@code FILTER_INTO_JOIN} and the transposes have pushed
   * predicates down to the leaves, so the filter this matches is the one that really applies to the
   * scan. Only the shapes a partition value can be tested against by inspection are read —
   * {@code = literal}, {@code IN (literals)}, {@code SEARCH} over a points-Sarg, and conjunctions of
   * those. Anything else prunes nothing, which is always correct and sometimes slow.
   */
  public static final class PartitionPruneRule extends RelRule<ChalkRuleConfig> {
    private PartitionPruneRule(ChalkRuleConfig config) {
      super(config);
    }

    private static PartitionPruneRule create() {
      return new PartitionPruneRule(
          ChalkRuleConfig.of(
              "PartitionPruneRule",
              b ->
                  b.operand(LogicalFilter.class)
                      .oneInput(b1 -> b1.operand(ChalkPartitionedScan.class).anyInputs()),
              PartitionPruneRule::new));
    }

    @Override
    public void onMatch(RelOptRuleCall call) {
      Filter filter = call.rel(0);
      ChalkPartitionedScan scan = call.rel(1);
      List<RexNode> allowed =
          matchable(
              filter.getCondition(),
              scan.partitionColumn(),
              filter.getCluster().getRexBuilder());
      if (allowed == null) {
        return;
      }

      List<RelNode> keptInputs = new ArrayList<>(scan.getInputs().size());
      List<@Nullable RexNode> keptValues = new ArrayList<>(scan.getInputs().size());
      for (int i = 0; i < scan.getInputs().size(); i++) {
        RexNode value = scan.partitionValues().get(i);

        // A range partition is never pruned by this rule: the predicate would have to be compared
        // against an interval, and a wrong answer here is missing rows.
        if (value == null || contains(allowed, value)) {
          keptInputs.add(scan.getInputs().get(i));
          keptValues.add(value);
        }
      }

      if (keptInputs.size() == scan.getInputs().size() || keptInputs.isEmpty()) {
        // Nothing pruned, or everything: an empty scan is a shape the rest of the planner has no
        // node for, so a predicate that matches no partition is left to filter the rows away.
        return;
      }

      call.transformTo(
          filter.copy(filter.getTraitSet(), scan.pruned(keptInputs, keptValues), filter.getCondition()));
    }

    /**
     * The literal values the predicate can match on the partition column, or null when it says
     * nothing about that column. An empty list means "no value at all", which prunes everything.
     */
    private static @Nullable List<RexNode> matchable(
        RexNode condition, int column, org.apache.calcite.rex.RexBuilder rex) {
      List<RexNode> values = new ArrayList<>();
      for (RexNode conjunct : RelOptUtilConjunctions.of(condition)) {
        List<RexNode> fromConjunct = points(conjunct, column, rex);
        if (fromConjunct != null) {
          values.addAll(fromConjunct);
          return values;
        }
      }
      return null;
    }

    /** The points {@code conjunct} pins the partition column to, or null when it pins none. */
    private static @Nullable List<RexNode> points(
        RexNode conjunct, int column, org.apache.calcite.rex.RexBuilder rex) {
      if (!(conjunct instanceof RexCall call)) {
        return null;
      }
      switch (call.getKind()) {
        case EQUALS -> {
          RexNode left = call.getOperands().get(0);
          RexNode right = call.getOperands().get(1);
          if (isColumn(left, column) && right instanceof RexLiteral) {
            return ImmutableList.of(right);
          }
          if (isColumn(right, column) && left instanceof RexLiteral) {
            return ImmutableList.of(left);
          }
          return null;
        }
        case IN -> {
          if (!isColumn(call.getOperands().get(0), column)) {
            return null;
          }
          List<RexNode> values = new ArrayList<>(call.getOperands().size() - 1);
          for (int i = 1; i < call.getOperands().size(); i++) {
            if (!(call.getOperands().get(i) instanceof RexLiteral literal)) {
              return null;
            }
            values.add(literal);
          }
          return values;
        }
        case SEARCH -> {
          if (!isColumn(call.getOperands().get(0), column)) {
            return null;
          }
          RexNode expanded = RexUtil.expandSearch(rex, null, call);
          return expanded == call ? null : points(expanded, column, rex);
        }
        case OR -> {
          List<RexNode> values = new ArrayList<>();
          for (RexNode operand : call.getOperands()) {
            List<RexNode> branch = points(operand, column, rex);
            if (branch == null) {
              return null;
            }
            values.addAll(branch);
          }
          return values;
        }
        default -> {
          return null;
        }
      }
    }

    private static boolean isColumn(RexNode node, int column) {
      return node instanceof RexInputRef ref && ref.getIndex() == column;
    }

    private static boolean contains(List<RexNode> allowed, RexNode value) {
      for (RexNode candidate : allowed) {
        if (candidate instanceof RexLiteral a
            && value instanceof RexLiteral b
            && java.util.Objects.equals(a.getValue(), b.getValue())) {
          return true;
        }
      }
      return false;
    }
  }

  /**
   * The converter: the same node in {@code ChalkConvention.LOCAL} with every branch converted
   * independently, so one partition can be a pushed remote query and the next a local scan.
   */
  public static final class ChalkPartitionedScanRule extends ConverterRule {
    private ChalkPartitionedScanRule() {
      super(
          Config.INSTANCE
              .withConversion(
                  ChalkPartitionedScan.class,
                  Convention.NONE,
                  ChalkConvention.LOCAL,
                  "ChalkPartitionedScanRule")
              .withRuleFactory(ChalkPartitionedScanRule::new));
    }

    private ChalkPartitionedScanRule(Config config) {
      super(config);
    }

    @Override
    public @Nullable RelNode convert(RelNode rel) {
      ChalkPartitionedScan scan = (ChalkPartitionedScan) rel;
      List<RelNode> converted = new ArrayList<>(scan.getInputs().size());
      for (RelNode input : scan.getInputs()) {
        converted.add(ChalkInputs.unordered(input));
      }
      return scan.intoLocal(converted);
    }
  }

  /** {@code RelOptUtil.conjunctions} under a name that does not shadow the class it comes from. */
  private static final class RelOptUtilConjunctions {
    private RelOptUtilConjunctions() {}

    static List<RexNode> of(RexNode condition) {
      return org.apache.calcite.plan.RelOptUtil.conjunctions(condition);
    }
  }
}
