package chalk.planner.plan.rules;

import chalk.ir.v1.Index;
import chalk.ir.v1.IndexReversal;
import chalk.ir.v1.SortDirection;
import chalk.planner.plan.IndexMatcher;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.rel.ChalkAsOfJoin;
import chalk.planner.plan.rel.ChalkAsOfLookupJoin;
import chalk.planner.plan.rel.ChalkFilter;
import chalk.planner.plan.rel.ChalkIndexLookup;
import chalk.planner.plan.rel.ChalkTableScan;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.plan.RelRule;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.JoinInfo;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexDynamicParam;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.type.SqlTypeUtil;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code ChalkAsOfJoin} over an in-process table → {@code ChalkAsOfLookupJoin}, which seeks an
 * ordered index on the table once for each distinct key and time of the left input instead of
 * reading it all (D338, design 63 §4). Offered beside the plain join, so cost chooses: a long log
 * read for a few positions and days seeks, and a small table is read.
 *
 * <p>The right input is the table's scan, or a filter on it whose every conjunct holds a column to
 * one value for the whole execution — the {@code ON r.symbol = 'EUR'} an ASOF join's condition on
 * its right input becomes. An index fits when its key begins with the right keys' columns and those
 * fixed columns, each once and in any order, and goes on with the time column, ascending; read
 * backwards for {@code >=} and {@code >}, which needs a reversal of ANY, and forwards for {@code <=}
 * and {@code <}. A table under an access policy keeps the plain join, whose filters the policy owns.
 */
public final class ChalkAsOfLookupRule extends RelRule<ChalkRuleConfig> {
  private final PushdownPolicy policy;
  private final boolean filtered;

  private ChalkAsOfLookupRule(ChalkRuleConfig config, PushdownPolicy policy, boolean filtered) {
    super(config);
    this.policy = policy;
    this.filtered = filtered;
  }

  /** The two shapes of right input, each a rule of its own. */
  public static List<ChalkAsOfLookupRule> rules(PushdownPolicy policy) {
    return List.of(
        new ChalkAsOfLookupRule(
            ChalkRuleConfig.of(
                "ChalkAsOfLookupRule(scan)",
                b ->
                    b.operand(ChalkAsOfJoin.class)
                        .inputs(
                            l -> l.operand(RelNode.class).anyInputs(),
                            r -> r.operand(ChalkTableScan.class).noInputs()),
                config -> new ChalkAsOfLookupRule(config, policy, false)),
            policy,
            false),
        new ChalkAsOfLookupRule(
            ChalkRuleConfig.of(
                "ChalkAsOfLookupRule(filter)",
                b ->
                    b.operand(ChalkAsOfJoin.class)
                        .inputs(
                            l -> l.operand(RelNode.class).anyInputs(),
                            r ->
                                r.operand(ChalkFilter.class)
                                    .oneInput(s -> s.operand(ChalkTableScan.class).noInputs())),
                config -> new ChalkAsOfLookupRule(config, policy, true)),
            policy,
            true));
  }

  @Override
  public boolean matches(RelOptRuleCall call) {
    return policy.allowsIndexLookup();
  }

  @Override
  public void onMatch(RelOptRuleCall call) {
    ChalkAsOfJoin join = call.rel(0);
    ChalkFilter filter = filtered ? call.rel(2) : null;
    ChalkTableScan scan = filtered ? call.rel(3) : call.rel(2);

    // A goaled scan belongs to one limit (D276); an entitled one to its policy, whose filters and
    // masks must stay where the client's invariant checks for them.
    if (scan.rowGoal() > 0
        || scan.chalkTable().isEntitled()
        || chalk.planner.entitlement.EntitledRelOptTable.disclosureOf(scan.getTable()) != null) {
      return;
    }

    if (ChalkInputs.hasNullSafeEquality(join)) {
      return;
    }
    JoinInfo info = join.analyzeCondition();
    if (!info.isEqui()) {
      return;
    }

    int leftFields = join.getLeft().getRowType().getFieldCount();
    if (!(join.getMatchCondition() instanceof RexCall match)
        || !(match.getOperands().get(0) instanceof RexInputRef first)
        || !(match.getOperands().get(1) instanceof RexInputRef second)
        || (first.getIndex() < leftFields) == (second.getIndex() < leftFields)) {
      return;
    }
    boolean leftFirst = first.getIndex() < leftFields;
    int leftTime = (leftFirst ? first : second).getIndex();
    int rightTime = (leftFirst ? second : first).getIndex() - leftFields;
    SqlKind kind = leftFirst ? match.getKind() : match.getKind().reverse();

    List<Integer> projection = scan.projection();
    RelDataType leftRow = join.getLeft().getRowType();
    RelDataType rightRow = scan.getRowType();

    // The columns a range holds to a value: the right keys' columns, each against its left key.
    Set<Integer> held = new HashSet<>();
    for (int k = 0; k < info.rightKeys.size(); k++) {
      int column = projection.get(info.rightKeys.get(k));
      if (!held.add(column)
          || !sameType(leftRow, info.leftKeys.get(k), rightRow, info.rightKeys.get(k))) {
        return;
      }
    }

    // And the columns the filter fixes, when every one of its conjuncts fixes one.
    List<Integer> fixedColumns = new ArrayList<>();
    List<RexNode> fixedValues = new ArrayList<>();
    if (filter != null) {
      for (RexNode conjunct : RelOptUtil.conjunctions(filter.getCondition())) {
        Fixed fixed = fixed(conjunct);
        if (fixed == null || !held.add(projection.get(fixed.field()))) {
          return;
        }
        fixedColumns.add(projection.get(fixed.field()));
        fixedValues.add(fixed.value());
      }
    }

    int timeColumn = projection.get(rightTime);
    if (held.contains(timeColumn) || !sameType(leftRow, leftTime, rightRow, rightTime)) {
      return;
    }

    boolean backwards = kind == SqlKind.GREATER_THAN || kind == SqlKind.GREATER_THAN_OR_EQUAL;
    for (Index index : scan.chalkTable().indexes()) {
      if (fits(index, held, timeColumn, backwards)) {
        call.transformTo(
            ChalkAsOfLookupJoin.create(
                join,
                scan,
                index,
                info.leftKeys,
                info.rightKeys,
                fixedColumns,
                fixedValues,
                leftTime,
                rightTime,
                kind));
        return;
      }
    }
  }

  /**
   * Whether {@code index}'s key is the held columns, each once and in any order, then the time
   * column ascending, and can be read in the direction the seek needs.
   */
  private static boolean fits(Index index, Set<Integer> held, int timeColumn, boolean backwards) {
    if (!ChalkIndexLookup.isOrdered(index.getKind()) || index.getColumnsCount() <= held.size()) {
      return false;
    }
    Set<Integer> prefix = new HashSet<>(index.getColumnsList().subList(0, held.size()));
    if (!prefix.equals(held) || index.getColumns(held.size()) != timeColumn) {
      return false;
    }
    int at = held.size();
    SortDirection direction =
        at < index.getDirectionsCount()
            ? index.getDirections(at)
            : SortDirection.SORT_DIRECTION_ASC_NULLS_LAST;
    if (direction != SortDirection.SORT_DIRECTION_ASC_NULLS_LAST
        && direction != SortDirection.SORT_DIRECTION_ASC_NULLS_FIRST) {
      return false;
    }
    return !backwards || index.getReversal() == IndexReversal.INDEX_REVERSAL_ANY;
  }

  private record Fixed(int field, RexNode value) {}

  /**
   * A conjunct that holds a column of the scan to one value for the execution: {@code $i = v} or
   * {@code v = $i}, where {@code v} is a literal, a parameter, or an expression over no column, of
   * the column's type.
   */
  private static @Nullable Fixed fixed(RexNode conjunct) {
    if (!(conjunct instanceof RexCall call) || call.getKind() != SqlKind.EQUALS) {
      return null;
    }
    RexNode a = call.getOperands().get(0);
    RexNode b = call.getOperands().get(1);
    RexInputRef column = a instanceof RexInputRef ref ? ref : b instanceof RexInputRef ref ? ref : null;
    RexNode value = column == a ? b : a;
    if (column == null
        || !(value instanceof RexLiteral || value instanceof RexDynamicParam
            || IndexMatcher.isComputed(value))
        || !SqlTypeUtil.equalSansNullability(column.getType(), value.getType())) {
      return null;
    }
    return new Fixed(column.getIndex(), value);
  }

  private static boolean sameType(RelDataType left, int leftField, RelDataType right, int rightField) {
    return SqlTypeUtil.equalSansNullability(
        left.getFieldList().get(leftField).getType(), right.getFieldList().get(rightField).getType());
  }
}
