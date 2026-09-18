package chalk.planner.plan.rules;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.rel.ChalkWindow;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptRule;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.convert.ConverterRule;
import org.apache.calcite.rel.core.Window;
import org.apache.calcite.rel.logical.LogicalWindow;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexWindowBound;
import org.apache.calcite.rex.RexWindowBounds;
import org.apache.calcite.sql.SqlAggFunction;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code LogicalWindow} → a <b>stack</b> of {@link ChalkWindow}s, one per Calcite group, in
 * Calcite's group order (D48, {@code 13-window-functions.md} §3).
 *
 * <p>Stacking is what makes the output column order Calcite's for free. A {@code LogicalWindow}'s row
 * is its input's fields followed by every group's calls in global ordinal order, and ordinals are
 * handed out group by group — so the stack's row after group <i>k</i> is exactly the prefix of that
 * row, and this rule takes each level's row type by slicing the logical one rather than inventing
 * names.
 *
 * <p>Calcite parks a window's constant arguments in {@code Window.constants} and refers to them with
 * {@code RexInputRef}s past the end of the input row — {@code NTILE($3)}, {@code rows between $3
 * PRECEDING}. Those are resolved into the literals they name here, before the stack is built, for two
 * reasons: every surviving reference then indexes the <em>original</em> input, whose fields keep
 * their positions as each level widens the row; and {@code RelToIr} gets a literal where the IR wants
 * one instead of having to carry a second row.
 */
public final class ChalkWindowRule extends ConverterRule {
  public static final ChalkWindowRule INSTANCE =
      Config.INSTANCE
          .withConversion(
              LogicalWindow.class, Convention.NONE, ChalkConvention.LOCAL, "ChalkWindowRule")
          .withRuleFactory(ChalkWindowRule::new)
          .toRule(ChalkWindowRule.class);

  private ChalkWindowRule(Config config) {
    super(config);
  }

  @Override
  public @Nullable RelNode convert(RelNode rel) {
    LogicalWindow logical = (LogicalWindow) rel;
    RelOptCluster cluster = logical.getCluster();
    List<RelDataTypeField> fields = logical.getRowType().getFieldList();
    int inputFieldCount = logical.getInput().getRowType().getFieldCount();

    RelNode current = logical.getInput();
    int emitted = inputFieldCount;
    for (Window.Group group : logical.groups) {
      Window.Group resolved = resolve(group, inputFieldCount, logical.constants);
      RelCollation needed = ChalkWindow.requiredCollation(resolved);
      RelNode ordered =
          RelOptRule.convert(
              current, cluster.traitSetOf(ChalkConvention.LOCAL).replace(needed).simplify());

      emitted += group.aggCalls.size();
      RelDataType rowType =
          cluster.getTypeFactory().createStructType(fields.subList(0, emitted));
      current = ChalkWindow.create(ordered, rowType, resolved);
    }

    return current;
  }

  /** The group with every constant reference replaced by the literal it names. */
  private static Window.Group resolve(
      Window.Group group, int inputFieldCount, List<RexLiteral> constants) {
    List<Window.RexWinAggCall> calls = new ArrayList<>(group.aggCalls.size());
    for (Window.RexWinAggCall call : group.aggCalls) {
      List<RexNode> operands = new ArrayList<>(call.getOperands().size());
      for (RexNode operand : call.getOperands()) {
        operands.add(resolve(operand, inputFieldCount, constants));
      }

      calls.add(
          new Window.RexWinAggCall(
              call.getParserPosition(),
              (SqlAggFunction) call.getOperator(),
              call.getType(),
              operands,
              call.ordinal,
              call.distinct,
              call.ignoreNulls));
    }

    return new Window.Group(
        group.keys,
        group.isRows,
        bound(group.lowerBound, inputFieldCount, constants),
        bound(group.upperBound, inputFieldCount, constants),
        group.exclude,
        group.orderKeys,
        calls);
  }

  private static RexWindowBound bound(
      RexWindowBound bound, int inputFieldCount, List<RexLiteral> constants) {
    RexNode offset = bound.getOffset();
    if (offset == null) {
      return bound;
    }

    RexNode resolved = resolve(offset, inputFieldCount, constants);
    return bound.isPreceding()
        ? RexWindowBounds.preceding(resolved)
        : RexWindowBounds.following(resolved);
  }

  private static RexNode resolve(RexNode node, int inputFieldCount, List<RexLiteral> constants) {
    return node.accept(
        new RexShuttle() {
          @Override
          public RexNode visitInputRef(RexInputRef ref) {
            int index = ref.getIndex();
            if (index < inputFieldCount) {
              return ref;
            }

            int constant = index - inputFieldCount;
            if (constant >= constants.size()) {
              throw new IllegalStateException(
                  "window reference $" + index + " names no input field and no constant");
            }

            return constants.get(constant);
          }
        });
  }
}
