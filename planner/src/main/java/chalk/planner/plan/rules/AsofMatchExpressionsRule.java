package chalk.planner.plan.rules;

import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelRule;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.logical.LogicalAsofJoin;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.util.ImmutableBitSet;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * An ASOF join whose match condition compares something other than a column on each side (D337,
 * design 63 §3). Calcite's coercion writes {@code <=($4, CAST($1):TIMESTAMP(9))} for a {@code
 * VALUES} timestamp matched against a TIMESTAMP(9) column, and a computed close such as {@code
 * LEAST(…, @now)} is an expression in the same place; {@link ChalkAsOfJoinRule} takes a column on
 * each side, so either was a planner internal error.
 *
 * <p>Each operand that is not a column is computed by a projection under the join, on the input
 * whose columns it reads, as {@code JoinPushExpressionsRule} does for a join's {@code ON}; the match
 * condition then compares the two new columns, and a projection above the join drops them. An
 * operand that reads both inputs is left as it is, for {@link ChalkAsOfJoinRule} to refuse.
 */
public final class AsofMatchExpressionsRule extends RelRule<ChalkRuleConfig> {

  public static final AsofMatchExpressionsRule INSTANCE =
      new AsofMatchExpressionsRule(
          ChalkRuleConfig.of(
              "AsofMatchExpressionsRule",
              b -> b.operand(LogicalAsofJoin.class).anyInputs(),
              AsofMatchExpressionsRule::new));

  private AsofMatchExpressionsRule(ChalkRuleConfig config) {
    super(config);
  }

  @Override
  public void onMatch(RelOptRuleCall call) {
    LogicalAsofJoin join = call.rel(0);
    if (!(join.getMatchCondition() instanceof RexCall match) || match.getOperands().size() != 2) {
      return;
    }

    RelNode left = join.getLeft();
    RelNode right = join.getRight();
    int leftWidth = left.getRowType().getFieldCount();
    int rightWidth = right.getRowType().getFieldCount();
    RexBuilder rexBuilder = join.getCluster().getRexBuilder();

    // The side each operand reads: a column's own, an expression's when all its columns are on one
    // side, and for a constant the side the other operand is not on, since the match compares a
    // value of the left row with one of the right's.
    List<RexNode> matchOperands = match.getOperands();
    Side[] sides = new Side[2];
    for (int i = 0; i < 2; i++) {
      ImmutableBitSet reads =
          org.apache.calcite.plan.RelOptUtil.InputFinder.bits(matchOperands.get(i));
      if (reads.isEmpty()) {
        sides[i] = null;
      } else if (reads.nextSetBit(0) >= leftWidth) {
        sides[i] = Side.RIGHT;
      } else if (reads.length() - 1 < leftWidth) {
        sides[i] = Side.LEFT;
      } else {
        return;
      }
    }
    for (int i = 0; i < 2; i++) {
      if (sides[i] == null) {
        Side other = sides[1 - i];
        if (other == Side.LEFT) {
          sides[i] = Side.RIGHT;
        } else if (other == Side.RIGHT) {
          sides[i] = Side.LEFT;
        } else {
          sides[i] = i == 0 ? Side.LEFT : Side.RIGHT;
        }
      }
    }

    List<RexNode> leftAdded = new ArrayList<>();
    List<RexNode> rightAdded = new ArrayList<>();
    // Where each match operand will be, as a reference into the join's new row; filled in once the
    // left side's added columns are counted, since the right side's columns move by that many.
    List<Placed> placed = new ArrayList<>(2);
    for (int i = 0; i < 2; i++) {
      RexNode operand = matchOperands.get(i);
      if (operand instanceof RexInputRef) {
        placed.add(new Placed(operand, null, -1));
      } else if (sides[i] == Side.RIGHT) {
        rightAdded.add(shift(operand, -leftWidth, rexBuilder));
        placed.add(new Placed(operand, Side.RIGHT, rightAdded.size() - 1));
      } else {
        leftAdded.add(operand);
        placed.add(new Placed(operand, Side.LEFT, leftAdded.size() - 1));
      }
    }

    if (leftAdded.isEmpty() && rightAdded.isEmpty()) {
      return;
    }

    RelNode newLeft = extended(left, leftAdded, rexBuilder);
    RelNode newRight = extended(right, rightAdded, rexBuilder);
    int shiftRight = leftAdded.size();

    List<RexNode> operands = new ArrayList<>(2);
    for (Placed p : placed) {
      if (p.side() == null) {
        operands.add(remap(p.operand(), leftWidth, shiftRight, rexBuilder));
      } else if (p.side() == Side.LEFT) {
        operands.add(
            rexBuilder.makeInputRef(newLeft.getRowType().getFieldList().get(leftWidth + p.at()).getType(),
                leftWidth + p.at()));
      } else {
        int at = leftWidth + shiftRight + rightWidth + p.at();
        operands.add(
            rexBuilder.makeInputRef(
                newRight.getRowType().getFieldList().get(rightWidth + p.at()).getType(), at));
      }
    }

    RexNode newMatch = rexBuilder.makeCall(match.getOperator(), operands);
    RexNode newCondition = remap(join.getCondition(), leftWidth, shiftRight, rexBuilder);
    RelNode rebuilt =
        LogicalAsofJoin.create(
            newLeft,
            newRight,
            join.getHints(),
            newCondition,
            newMatch,
            join.getJoinType(),
            com.google.common.collect.ImmutableList.of());

    // The join's own row again: the left's columns, then the right's, without the added ones.
    List<RexNode> kept = new ArrayList<>(leftWidth + rightWidth);
    for (int i = 0; i < leftWidth; i++) {
      kept.add(rexBuilder.makeInputRef(rebuilt, i));
    }
    for (int i = 0; i < rightWidth; i++) {
      kept.add(rexBuilder.makeInputRef(rebuilt, leftWidth + shiftRight + i));
    }
    call.transformTo(
        LogicalProject.create(
            rebuilt,
            com.google.common.collect.ImmutableList.of(),
            kept,
            join.getRowType().getFieldNames(),
            com.google.common.collect.ImmutableSet.of()));
  }

  private enum Side {
    LEFT,
    RIGHT
  }

  /** A match operand, and where its computed column goes when it has one. */
  private record Placed(RexNode operand, @Nullable Side side, int at) {}

  /** {@code input} with {@code added} computed after its own columns, or itself when none are. */
  private static RelNode extended(RelNode input, List<RexNode> added, RexBuilder rexBuilder) {
    if (added.isEmpty()) {
      return input;
    }
    int width = input.getRowType().getFieldCount();
    List<RexNode> exprs = new ArrayList<>(width + added.size());
    List<String> names = new ArrayList<>(width + added.size());
    for (int i = 0; i < width; i++) {
      exprs.add(rexBuilder.makeInputRef(input, i));
      names.add(input.getRowType().getFieldList().get(i).getName());
    }
    for (int i = 0; i < added.size(); i++) {
      exprs.add(added.get(i));
      names.add("$asof_match" + i);
    }
    return LogicalProject.create(
        input,
        com.google.common.collect.ImmutableList.of(),
        exprs,
        names,
        com.google.common.collect.ImmutableSet.of());
  }

  /** {@code node} with every input reference moved by {@code by}. */
  private static RexNode shift(RexNode node, int by, RexBuilder rexBuilder) {
    return node.accept(
        new RexShuttle() {
          @Override
          public RexNode visitInputRef(RexInputRef ref) {
            return rexBuilder.makeInputRef(ref.getType(), ref.getIndex() + by);
          }
        });
  }

  /** {@code node}, over the join's old row, rewritten over its new one. */
  private static RexNode remap(RexNode node, int leftWidth, int shiftRight, RexBuilder rexBuilder) {
    return node.accept(
        new RexShuttle() {
          @Override
          public RexNode visitInputRef(RexInputRef ref) {
            return ref.getIndex() < leftWidth
                ? ref
                : rexBuilder.makeInputRef(ref.getType(), ref.getIndex() + shiftRight);
          }
        });
  }
}
