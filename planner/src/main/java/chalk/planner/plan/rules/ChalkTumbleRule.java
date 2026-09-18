package chalk.planner.plan.rules;

import chalk.planner.plan.ChalkOperatorTable;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelRule;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.TableFunctionScan;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rel.logical.LogicalTableFunctionScan;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;

/**
 * D52 — Calcite's {@code TUMBLE} table function, rewritten into the projection that computes the same
 * thing row by row ({@code 13-window-functions.md} §3).
 *
 * <pre>
 * SELECT … FROM TABLE(TUMBLE(TABLE bars, DESCRIPTOR(ts), INTERVAL '5' MINUTE))
 *   →  Project(bars.*, window_start = TIME_BUCKET(INTERVAL '5' MINUTE, ts),
 *                      window_end   = window_start + INTERVAL '5' MINUTE)
 * </pre>
 *
 * <p>That is exactly the semantics Calcite's own enumerable implementation gives {@code TUMBLE}: one
 * output row per input row, carrying the bucket it falls in. No operator is needed, and the result
 * is a shape the rest of the planner already knows how to optimise — the trimmer drops
 * {@code window_end} when nothing reads it, an aggregate above groups by {@code window_start}, and an
 * index-ordered scan can still serve the ordering underneath.
 *
 * <p>{@code HOP} and {@code SESSION} are not rewritten: they multiply rows and detect gaps
 * respectively, which needs an operator. They reach {@link chalk.planner.plan.WindowSupport} as an
 * unrewritten {@code TableFunctionScan} and are refused there, naming §9.
 */
public final class ChalkTumbleRule extends RelRule<ChalkRuleConfig> {
  public static final ChalkTumbleRule INSTANCE =
      new ChalkTumbleRule(
          ChalkRuleConfig.of(
              "ChalkTumbleRule",
              b ->
                  b.operand(LogicalTableFunctionScan.class)
                      .trait(Convention.NONE)
                      .anyInputs(),
              ChalkTumbleRule::new));

  private ChalkTumbleRule(ChalkRuleConfig config) {
    super(config);
  }

  /** The name Calcite's built-in tumbling table function goes by. */
  public static final String TUMBLE = "TUMBLE";

  @Override
  public void onMatch(RelOptRuleCall call) {
    LogicalTableFunctionScan scan = call.rel(0);
    if (!isTumble(scan) || scan.getInputs().size() != 1) {
      return;
    }

    RexCall invocation = (RexCall) scan.getCall();
    List<RexNode> operands = invocation.getOperands();
    if (operands.size() != 2) {
      // TUMBLE's optional third operand is an alignment offset, which TIME_BUCKET spells as an
      // origin timestamp rather than an interval. Refusing here leaves the scan unrewritten, and
      // WindowSupport turns that into a clean UNSUPPORTED naming the function.
      return;
    }

    RexNode time = descriptorColumn(operands.get(0));
    if (time == null) {
      return;
    }

    RelNode input = scan.getInput(0);
    RexBuilder rexBuilder = scan.getCluster().getRexBuilder();
    List<RelDataTypeField> outputFields = scan.getRowType().getFieldList();
    int inputWidth = input.getRowType().getFieldCount();
    if (outputFields.size() != inputWidth + 2) {
      return;
    }

    List<RexNode> projects = new ArrayList<>(outputFields.size());
    for (int i = 0; i < inputWidth; i++) {
      projects.add(rexBuilder.makeInputRef(input, i));
    }

    RexNode size = operands.get(1);
    RelDataType startType = outputFields.get(inputWidth).getType();
    RelDataType endType = outputFields.get(inputWidth + 1).getType();
    RexNode start =
        rexBuilder.makeCall(startType, ChalkOperatorTable.TIME_BUCKET, ImmutableList.of(size, time));
    RexNode end =
        rexBuilder.makeCall(
            endType, SqlStdOperatorTable.DATETIME_PLUS, ImmutableList.of(start, size));
    projects.add(start);
    projects.add(end);

    call.transformTo(
        LogicalProject.create(
            input,
            ImmutableList.of(),
            projects,
            scan.getRowType(),
            com.google.common.collect.ImmutableSet.of()));
  }

  /** Whether this scan is a {@code TUMBLE} invocation at all. */
  public static boolean isTumble(TableFunctionScan scan) {
    return scan.getCall() instanceof RexCall invocation
        && TUMBLE.equalsIgnoreCase(invocation.getOperator().getName());
  }

  /**
   * The single column a {@code DESCRIPTOR(ts)} operand names, as a reference into the input row, or
   * null when the operand is not a one-column descriptor.
   */
  private static RexNode descriptorColumn(RexNode operand) {
    if (!(operand instanceof RexCall descriptor)
        || descriptor.getKind() != SqlKind.DESCRIPTOR
        || descriptor.getOperands().size() != 1) {
      return null;
    }

    RexNode column = descriptor.getOperands().get(0);
    return column instanceof RexInputRef ? column : null;
  }
}
