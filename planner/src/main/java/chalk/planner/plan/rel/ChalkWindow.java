package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.CostModel;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelFieldCollation;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Window;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.util.Pair;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * One partition-and-order of window calls — a single Calcite {@link Window.Group}. Maps 1:1 onto the
 * IR's {@code Window} (D48, {@code 13-window-functions.md} §2 and §3); a {@code LogicalWindow} with
 * several groups becomes a <em>stack</em> of these, one per group, in Calcite's group order.
 *
 * <p><b>It requires its input ordered</b> by (partition keys ASC NULLS LAST, then the order keys as
 * written), and says so as the trait of its input, so phase A's machinery satisfies the requirement
 * with, in cost order: nothing at all, an index-ordered scan (D50), or an enforced {@link ChalkSort}.
 * That same ordering is what the output delivers, which is what makes {@code ORDER BY symbol, ts}
 * after a per-symbol window sort-free.
 *
 * <p>The group carries no {@code constants}: {@code ChalkWindowRule} resolves Calcite's
 * constant references — {@code NTILE($3)}, {@code rows between $3 PRECEDING} — into the literals
 * they name before building this node. That is what lets the stack work: every remaining
 * {@code RexInputRef} indexes the <em>original</em> input, whose fields stay at the same positions
 * as each stacked window widens the row.
 */
public final class ChalkWindow extends Window implements ChalkRel {

  private ChalkWindow(
      RelOptCluster cluster,
      RelTraitSet traitSet,
      RelNode input,
      RelDataType rowType,
      Group group) {
    super(
        cluster,
        traitSet,
        ImmutableList.of(),
        input,
        ImmutableList.<RexLiteral>of(),
        rowType,
        ImmutableList.of(group));
  }

  /**
   * A window over {@code input} for one group. The input is asked for {@link
   * #requiredCollation(Group)} and the node claims the same ordering for its own output.
   */
  public static ChalkWindow create(RelNode input, RelDataType rowType, Group group) {
    RelOptCluster cluster = input.getCluster();
    RelCollation collation = requiredCollation(group);
    RelTraitSet traits = cluster.traitSetOf(ChalkConvention.LOCAL).replace(collation).simplify();
    return new ChalkWindow(cluster, traits, input, rowType, group);
  }

  /** The one group this node evaluates. */
  public Group group() {
    return groups.get(0);
  }

  /** How many fields of this node's row come from its input. */
  public int inputFieldCount() {
    return getInput().getRowType().getFieldCount();
  }

  /**
   * The ordering the input must deliver: the partition keys ascending with NULLs last, then the
   * order keys as the query wrote them. A key that is both a partition key and an order key appears
   * once — {@code PARTITION BY symbol ORDER BY symbol} orders by {@code symbol}, not by it twice.
   */
  public static RelCollation requiredCollation(Group group) {
    List<RelFieldCollation> fields = new ArrayList<>();
    Set<Integer> seen = new LinkedHashSet<>();
    for (int key : group.keys) {
      if (seen.add(key)) {
        fields.add(
            new RelFieldCollation(
                key, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.LAST));
      }
    }

    for (RelFieldCollation field : group.orderKeys.getFieldCollations()) {
      if (seen.add(field.getFieldIndex())) {
        fields.add(field);
      }
    }

    return RelCollations.of(fields);
  }

  /** The ordering this node's own output delivers, which is the one it required of its input. */
  public RelCollation collation() {
    return requiredCollation(group());
  }

  @Override
  public ChalkWindow copy(RelTraitSet traitSet, List<RelNode> inputs) {
    if (inputs.size() != 1) {
      throw new IllegalArgumentException("a ChalkWindow has exactly one input");
    }
    return new ChalkWindow(getCluster(), traitSet, inputs.get(0), getRowType(), group());
  }

  /**
   * {@code Window.copy(constants)} exists for the rules that fold a window's constant arguments.
   * A {@code ChalkWindow} has none — {@code ChalkWindowRule} resolved them into literals — so the
   * only legal argument is an empty list, and anything else is a caller that has mistaken this node
   * for a {@code LogicalWindow}.
   */
  @Override
  public ChalkWindow copy(List<RexLiteral> constants) {
    if (!constants.isEmpty()) {
      throw new IllegalArgumentException(
          "a ChalkWindow carries no constants; its window arguments are already literals");
    }
    return this;
  }

  /**
   * A parent's required ordering goes down when it names input fields only and this node's own
   * ordering already satisfies it — which is the common case, {@code ORDER BY symbol, ts} over a
   * window partitioned by {@code symbol} and ordered by {@code ts}. Anything else does not: the
   * window has to see its input in its own order, so it cannot offer a different one.
   */
  @Override
  public @Nullable Pair<RelTraitSet, List<RelTraitSet>> passThroughTraits(RelTraitSet required) {
    RelCollation wanted = required.getCollation();
    if (wanted == null || wanted.getFieldCollations().isEmpty()) {
      return null;
    }

    int inputFields = inputFieldCount();
    for (RelFieldCollation field : wanted.getFieldCollations()) {
      if (field.getFieldIndex() >= inputFields) {
        return null;
      }
    }

    RelCollation own = collation();
    if (!own.satisfies(wanted)) {
      return null;
    }

    RelTraitSet passed = traitSet.replace(own);
    return Pair.of(passed, ImmutableList.of(passed));
  }

  /**
   * Cost model v4 ({@code 13-window-functions.md} §3): one pass over the input per call, plus a
   * documented log factor when the frame needs a deque or two pointers rather than a running
   * accumulator. The sort-or-index choice below is where cost actually decides something; the window
   * itself is the same work whichever way its input arrives.
   */
  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double rows = mq.getRowCount(getInput());
    double work = CostModel.window(rows, group().aggCalls.size(), needsFrameSearch(group()));
    return planner.getCostFactory().makeCost(work, work, 0);
  }

  /**
   * Whether the frame is one a running accumulator can serve — the whole partition, or everything up
   * to the current row — or one whose bounds both move, which costs the log factor.
   */
  private static boolean needsFrameSearch(Group group) {
    boolean lowerFixed = group.lowerBound.isUnbounded() && group.lowerBound.isPreceding();
    boolean upperFixed =
        (group.upperBound.isUnbounded() && group.upperBound.isFollowing())
            || group.upperBound.isCurrentRow();
    return !(lowerFixed && upperFixed);
  }
}
