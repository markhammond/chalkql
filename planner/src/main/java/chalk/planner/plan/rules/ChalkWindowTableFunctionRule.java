package chalk.planner.plan.rules;

import chalk.planner.plan.rel.ChalkHop;
import chalk.planner.plan.rel.ChalkSession;
import com.google.common.collect.ImmutableList;
import java.util.List;
import java.util.Locale;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelRule;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.TableFunctionScan;
import org.apache.calcite.rel.logical.LogicalTableFunctionScan;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.SqlKind;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * D55 — Calcite's {@code HOP} and {@code SESSION} table functions become {@link ChalkHop} and
 * {@link ChalkSession} ({@code 14-windows-ii.md} §1).
 *
 * <p>Both arrive in the shape ADR 0017's V19 recorded for {@code TUMBLE}: a
 * {@code LogicalTableFunctionScan} whose single input is the table and whose {@code call} names the
 * descriptor columns and the intervals. Neither can be a projection the way {@code TUMBLE} could —
 * a hop multiplies rows and a session looks across them — so each becomes an operator.
 *
 * <pre>
 * HOP(TABLE t, DESCRIPTOR(ts), slide, size)              → ChalkHop(t, ts, slide, size)
 * SESSION(TABLE t, DESCRIPTOR(ts), [DESCRIPTOR(k),] gap) → ChalkSession(t, [k], ts, gap)
 * </pre>
 *
 * <p><b>The session's partition key is a second {@code DESCRIPTOR}, not a {@code PARTITION BY}
 * clause.</b> Calcite 1.42 refuses {@code SESSION(TABLE t PARTITION BY k, …)} at validation — "only
 * tables with set semantics may be partitioned" — and its {@code SqlSessionTableFunction} states the
 * supported form as {@code SESSION(TABLE table_name, DESCRIPTOR(timecol), DESCRIPTOR(key) optional,
 * datetime interval)}. ADR 0018 records that as V24.
 *
 * <p>This is a plain {@link RelRule} rather than a {@code ConverterRule} because it has to look
 * inside the invocation before it can say whether it converts at all. A shape it does not recognise
 * is left standing, and {@link chalk.planner.plan.WindowSupport} — which asks {@link #analyse} the
 * same question on the logical tree — turns that into a clean {@code UNSUPPORTED} naming the
 * function rather than a Volcano "cannot plan".
 */
public final class ChalkWindowTableFunctionRule extends RelRule<ChalkRuleConfig> {
  public static final ChalkWindowTableFunctionRule INSTANCE =
      new ChalkWindowTableFunctionRule(
          ChalkRuleConfig.of(
              "ChalkWindowTableFunctionRule",
              b ->
                  b.operand(LogicalTableFunctionScan.class)
                      .trait(Convention.NONE)
                      .anyInputs(),
              ChalkWindowTableFunctionRule::new));

  /** The names Calcite's built-in window table functions go by. */
  public static final String HOP = "HOP";

  public static final String SESSION = "SESSION";

  private ChalkWindowTableFunctionRule(ChalkRuleConfig config) {
    super(config);
  }

  /**
   * What a recognised invocation says. {@code partitionKeys} is empty for a hop and for a session
   * with no key descriptor; {@code size} is unset for a session and {@code gap} for a hop.
   */
  public record Analysis(
      String function,
      int timeColumn,
      List<Integer> partitionKeys,
      @Nullable RexNode slide,
      @Nullable RexNode size,
      @Nullable RexNode gap) {}

  @Override
  public void onMatch(RelOptRuleCall call) {
    LogicalTableFunctionScan scan = call.rel(0);
    Analysis analysis = analyse(scan);
    if (analysis == null) {
      return;
    }

    RelNode input = scan.getInput(0);
    if (HOP.equals(analysis.function())) {
      call.transformTo(
          ChalkHop.create(
              ChalkInputs.unordered(input),
              scan.getRowType(),
              analysis.timeColumn(),
              analysis.slide(),
              analysis.size()));
      return;
    }

    // The input has to arrive ordered by (keys, time); asking for it as a trait is what lets a
    // declared collation or an index-ordered scan satisfy it without a Sort.
    call.transformTo(
        ChalkSession.create(
            ChalkInputs.sorted(
                input,
                ChalkSession.requiredCollation(
                    analysis.partitionKeys(), analysis.timeColumn())),
            scan.getRowType(),
            analysis.partitionKeys(),
            analysis.timeColumn(),
            analysis.gap()));
  }

  /** Whether this scan is a {@code HOP} or a {@code SESSION} at all, whatever its shape. */
  public static boolean isWindowFunction(TableFunctionScan scan) {
    String name = nameOf(scan);
    return HOP.equals(name) || SESSION.equals(name);
  }

  /** The function's name, upper-cased, or the empty string. */
  public static String nameOf(TableFunctionScan scan) {
    return scan.getCall() instanceof RexCall call
        ? call.getOperator().getName().toUpperCase(Locale.ROOT)
        : "";
  }

  /**
   * What the invocation says, or null when this is not a window table function this rule can
   * convert. Asked twice: once by {@link #onMatch}, and once by {@code WindowSupport} on the logical
   * tree so that an unconvertible shape is refused with a message rather than left to Volcano.
   */
  public static @Nullable Analysis analyse(TableFunctionScan scan) {
    if (scan.getInputs().size() != 1 || !(scan.getCall() instanceof RexCall invocation)) {
      return null;
    }

    String name = nameOf(scan);
    List<RexNode> operands = invocation.getOperands();

    // Every window table function's row is its input's fields plus window_start and window_end.
    if (scan.getRowType().getFieldCount() != scan.getInput(0).getRowType().getFieldCount() + 2) {
      return null;
    }

    Integer time = operands.isEmpty() ? null : descriptorColumn(operands.get(0));
    if (time == null) {
      return null;
    }

    if (HOP.equals(name) && operands.size() == 3) {
      return new Analysis(
          HOP, time, ImmutableList.of(), operands.get(1), operands.get(2), null);
    }

    if (SESSION.equals(name) && (operands.size() == 2 || operands.size() == 3)) {
      ImmutableList<Integer> keys = ImmutableList.of();
      if (operands.size() == 3) {
        Integer key = descriptorColumn(operands.get(1));
        if (key == null) {
          return null;
        }

        keys = ImmutableList.of(key);
      }

      return new Analysis(SESSION, time, keys, null, null, operands.get(operands.size() - 1));
    }

    return null;
  }

  /**
   * The single column a {@code DESCRIPTOR(c)} operand names, as an index into the input row, or null
   * when the operand is not a one-column descriptor over an input reference.
   */
  private static @Nullable Integer descriptorColumn(RexNode operand) {
    if (!(operand instanceof RexCall descriptor)
        || descriptor.getKind() != SqlKind.DESCRIPTOR
        || descriptor.getOperands().size() != 1) {
      return null;
    }

    return descriptor.getOperands().get(0) instanceof RexInputRef ref ? ref.getIndex() : null;
  }
}
