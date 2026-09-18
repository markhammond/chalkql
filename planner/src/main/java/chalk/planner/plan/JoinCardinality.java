package chalk.planner.plan;

import chalk.ir.v1.ForeignKey;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.plan.rel.ChalkIndexLookup;
import chalk.planner.plan.rel.ChalkTableScan;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.plan.volcano.RelSubset;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.AsofJoin;
import org.apache.calcite.rel.core.Filter;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.JoinInfo;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.core.Sort;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.metadata.RelMdUtil;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.util.ImmutableBitSet;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * How many rows a join produces, from what the catalog actually declares (F14).
 *
 * <p>Calcite's {@code RelMdUtil.getJoinRowCount} multiplies the two input row counts by a guessed
 * selectivity — 0.15 per equality — and consults no uniqueness at all. A self-join on a unique key
 * therefore estimated 7.6 × 10<sup>10</sup> rows for {@code m4-window/20}, whose two inputs are
 * 14 986 rows each and whose output cannot exceed 14 986. An estimate that wrong does not merely
 * look silly in the plan text: it decides join order and build side for every query above it.
 *
 * <p>The estimate is made in this order, and the first branch that can answer does:
 *
 * <ol>
 *   <li><b>Uniqueness.</b> When the equi-keys are unique on one side, each row of the other side
 *       matches at most one row, so the output is bounded by the other side's row count. When a
 *       declared foreign key also says every non-NULL row of that other side <em>does</em> match,
 *       the bound is exact: its rows less the rows whose key is NULL.
 *   <li><b>Foreign key.</b> The same fact without uniqueness having been provable: the child's rows
 *       less its NULL keys.
 *   <li><b>Distinct counts.</b> The textbook estimate
 *       {@code left × right / max(ndv_left, ndv_right)}, from the M2 statistics.
 *   <li><b>Calcite's guess</b>, unchanged, so a catalog that declares nothing plans as it did.
 * </ol>
 *
 * <p>Everything not an equi-key — the residual conjuncts — still multiplies by
 * {@code guessSelectivity}, and the outer-join floors Calcite applies are applied here too: a LEFT
 * join emits at least its left input's rows whatever the keys say.
 */
public final class JoinCardinality {
  private JoinCardinality() {}

  /** How deep {@link #resolve} will walk before giving up. Plans are shallow; runaway walks are not. */
  private static final int MAX_DEPTH = 8;

  /**
   * The join's row count, or null when the join is one this class does not model (semi and anti
   * joins keep Calcite's own numbers, which are already bounded by the left input).
   */
  public static @Nullable Double rowCount(Join rel, RelMetadataQuery mq) {
    Double guess = RelMdUtil.getJoinRowCount(mq, rel, rel.getCondition());

    // Semi and anti joins are already bounded by their left input, and an ASOF join emits exactly
    // one row per probe row by construction; neither is what F14 is about, so both keep Calcite's
    // numbers and the plans that were recorded against them.
    if (!rel.getJoinType().projectsRight() || rel instanceof AsofJoin) {
      return guess;
    }

    JoinInfo info = rel.analyzeCondition();
    if (info.leftKeys.isEmpty()) {
      return guess;
    }

    Double leftRows = mq.getRowCount(rel.getLeft());
    Double rightRows = mq.getRowCount(rel.getRight());
    if (leftRows == null || rightRows == null) {
      return guess;
    }

    RexBuilder rexBuilder = rel.getCluster().getRexBuilder();
    RexNode remaining = RexUtil.composeConjunction(rexBuilder, info.nonEquiConditions, true);
    double residual = remaining == null ? 1.0 : RelMdUtil.guessSelectivity(remaining);

    Double inner = equiRowCount(rel, mq, info, leftRows, rightRows);
    if (inner == null) {
      return guess;
    }

    double rows = inner * residual;
    if (rel.getJoinType().generatesNullsOnRight()) {
      rows = Math.max(rows, leftRows);
    }
    if (rel.getJoinType().generatesNullsOnLeft()) {
      rows = Math.max(rows, rightRows);
    }

    return Math.max(1.0, rows);
  }

  /**
   * The share of the cartesian product {@code predicate} keeps, with every equi-key conjunct
   * estimated from uniqueness and distinct counts rather than guessed at 0.15. Conjuncts that are
   * not equi-keys keep Calcite's guess, and the product is clamped away from zero because Volcano
   * divides by row counts.
   */
  public static double selectivity(Join rel, RelMetadataQuery mq, @Nullable RexNode predicate) {
    if (predicate == null || predicate.isAlwaysTrue()) {
      return 1.0;
    }

    int leftFields = rel.getLeft().getRowType().getFieldCount();
    List<RexNode> residual = new ArrayList<>();
    double selectivity = 1.0;
    for (RexNode conjunct : RelOptUtil.conjunctions(predicate)) {
      int[] key = equiKey(conjunct, leftFields, rel.getRowType().getFieldCount());
      Double share = key == null ? null : keySelectivity(rel, mq, key[0], key[1]);
      if (share == null) {
        residual.add(conjunct);
      } else {
        selectivity *= share;
      }
    }

    if (!residual.isEmpty()) {
      selectivity *=
          RelMdUtil.guessSelectivity(
              RexUtil.composeConjunction(rel.getCluster().getRexBuilder(), residual, true));
    }

    return Math.max(1e-9, Math.min(1.0, selectivity));
  }

  /**
   * One equi-key's share of the cartesian product: {@code 1 / max(ndv_left, ndv_right)}, which is
   * {@code 1 / rows} on a side whose key is unique. Null when neither side can say.
   */
  private static @Nullable Double keySelectivity(
      Join rel, RelMetadataQuery mq, int leftField, int rightField) {
    ImmutableBitSet leftKey = ImmutableBitSet.of(leftField);
    ImmutableBitSet rightKey = ImmutableBitSet.of(rightField);
    Double leftNdv = distinct(mq, rel.getLeft(), leftKey);
    Double rightNdv = distinct(mq, rel.getRight(), rightKey);
    if (leftNdv == null && rightNdv == null) {
      return null;
    }

    double ndv = Math.max(leftNdv == null ? 0 : leftNdv, rightNdv == null ? 0 : rightNdv);
    return ndv <= 1 ? null : 1.0 / ndv;
  }

  /** The conjunct as a (left field, right-relative field) equi-key pair, or null. */
  private static int @Nullable [] equiKey(RexNode conjunct, int leftFields, int totalFields) {
    if (conjunct.getKind() != SqlKind.EQUALS
        && conjunct.getKind() != SqlKind.IS_NOT_DISTINCT_FROM) {
      return null;
    }
    if (!(conjunct instanceof RexCall call) || call.getOperands().size() != 2) {
      return null;
    }
    if (!(call.getOperands().get(0) instanceof RexInputRef left)
        || !(call.getOperands().get(1) instanceof RexInputRef right)) {
      return null;
    }

    int a = left.getIndex();
    int b = right.getIndex();
    if (a >= totalFields || b >= totalFields) {
      return null;
    }
    if (a < leftFields && b >= leftFields) {
      return new int[] {a, b - leftFields};
    }
    if (b < leftFields && a >= leftFields) {
      return new int[] {b, a - leftFields};
    }

    return null;
  }

  /** The inner-join cardinality of the equi-keys alone, or null when nothing can say. */
  private static @Nullable Double equiRowCount(
      Join rel, RelMetadataQuery mq, JoinInfo info, double leftRows, double rightRows) {
    ImmutableBitSet leftKeys = ImmutableBitSet.of((Iterable<Integer>) info.leftKeys);
    ImmutableBitSet rightKeys = ImmutableBitSet.of((Iterable<Integer>) info.rightKeys);

    Boolean leftUnique = unique(mq, rel.getLeft(), leftKeys);
    Boolean rightUnique = unique(mq, rel.getRight(), rightKeys);

    // 1 and 2. A foreign key from the many side to the one side says exactly how many rows match.
    Double fromRight = matchingRows(rel, mq, info, /* childOnLeft= */ true, leftRows);
    if (fromRight != null && Boolean.TRUE.equals(rightUnique)) {
      return fromRight;
    }
    Double fromLeft = matchingRows(rel, mq, info, /* childOnLeft= */ false, rightRows);
    if (fromLeft != null && Boolean.TRUE.equals(leftUnique)) {
      return fromLeft;
    }

    // 1 without a foreign key: a bound, not a measurement, so the smaller of it and the estimate.
    Double estimate = distinctRowCount(rel, mq, leftKeys, rightKeys, leftRows, rightRows);
    if (Boolean.TRUE.equals(rightUnique)) {
      return estimate == null ? leftRows : Math.min(leftRows, estimate);
    }
    if (Boolean.TRUE.equals(leftUnique)) {
      return estimate == null ? rightRows : Math.min(rightRows, estimate);
    }

    // 2 alone: the child's rows that have a key at all.
    if (fromRight != null) {
      return fromRight;
    }
    if (fromLeft != null) {
      return fromLeft;
    }

    // 3. left × right / max(ndv_left, ndv_right).
    return estimate;
  }

  /** {@code left × right / max(ndv_left, ndv_right)}, or null when neither side declares one. */
  private static @Nullable Double distinctRowCount(
      Join rel,
      RelMetadataQuery mq,
      ImmutableBitSet leftKeys,
      ImmutableBitSet rightKeys,
      double leftRows,
      double rightRows) {
    Double leftNdv = distinct(mq, rel.getLeft(), leftKeys);
    Double rightNdv = distinct(mq, rel.getRight(), rightKeys);
    if (leftNdv == null && rightNdv == null) {
      return null;
    }

    double ndv = Math.max(leftNdv == null ? 0 : leftNdv, rightNdv == null ? 0 : rightNdv);
    return ndv <= 0 ? null : (leftRows * rightRows) / ndv;
  }

  /**
   * When a declared foreign key covers the equi-keys from the child side to the parent side, the
   * child's rows less the rows whose key is NULL — every other row matches exactly one parent row.
   * Null when no such key is declared.
   */
  private static @Nullable Double matchingRows(
      Join rel, RelMetadataQuery mq, JoinInfo info, boolean childOnLeft, double childRows) {
    Resolved child = resolve(childOnLeft ? rel.getLeft() : rel.getRight(), 0);
    Resolved parent = resolve(childOnLeft ? rel.getRight() : rel.getLeft(), 0);
    if (child == null || parent == null) {
      return null;
    }

    List<Integer> childFields = childOnLeft ? info.leftKeys : info.rightKeys;
    List<Integer> parentFields = childOnLeft ? info.rightKeys : info.leftKeys;
    List<Integer> childColumns = child.columns(childFields);
    List<Integer> parentColumns = parent.columns(parentFields);
    if (childColumns == null || parentColumns == null) {
      return null;
    }

    for (ForeignKey key : child.table.foreignKeys()) {
      if (!key.getParentTable().equalsIgnoreCase(parent.table.tableName())) {
        continue;
      }
      if (key.getColumnsCount() != childColumns.size()
          || key.getParentColumnsCount() != parentColumns.size()) {
        continue;
      }
      if (!covers(key, childColumns, parentColumns)) {
        continue;
      }

      // The NULL count is the base table's; `childRows` is what the input is estimated to deliver,
      // which a filter under it may have cut down a long way. Subtracting the whole table's NULLs
      // from a filtered estimate would collapse the join to one row and mislead everything above
      // it, so the count is scaled by the share of the table the input still carries.
      Double baseRows = child.table.rowCount();
      double share = baseRows == null || baseRows <= 0
          ? 1.0
          : Math.min(1.0, childRows / baseRows);
      double nulls = 0;
      for (int column : childColumns) {
        long nullCount = child.table.statistics(column).getNullCount();
        if (nullCount > 0) {
          nulls = Math.max(nulls, nullCount * share);
        }
      }

      return Math.max(1.0, childRows - nulls);
    }

    return null;
  }

  /** Whether the key's pairs are exactly the join's pairs, in any order. */
  private static boolean covers(ForeignKey key, List<Integer> child, List<Integer> parent) {
    for (int i = 0; i < child.size(); i++) {
      boolean found = false;
      for (int k = 0; k < key.getColumnsCount(); k++) {
        if (key.getColumns(k) == child.get(i) && key.getParentColumns(k) == parent.get(i)) {
          found = true;
          break;
        }
      }
      if (!found) {
        return false;
      }
    }

    return true;
  }

  private static @Nullable Boolean unique(
      RelMetadataQuery mq, RelNode input, ImmutableBitSet keys) {
    try {
      return mq.areColumnsUnique(input, keys);
    } catch (RuntimeException e) {
      return null;
    }
  }

  private static @Nullable Double distinct(
      RelMetadataQuery mq, RelNode input, ImmutableBitSet keys) {
    try {
      Double ndv = mq.getDistinctRowCount(input, keys, null);
      return ndv == null || ndv <= 0 ? null : ndv;
    } catch (RuntimeException e) {
      return null;
    }
  }

  /**
   * The Chalk table a join input reads, and the table column each of its fields comes from. The
   * walk is deliberately narrow — a scan or a lookup, under projections that only rename, filters
   * and sorts — because a foreign key's claim ("every non-NULL row matches") survives exactly those
   * and nothing else. Anything wider returns null and the estimate falls through to the next branch.
   */
  private static @Nullable Resolved resolve(RelNode input, int depth) {
    if (depth > MAX_DEPTH) {
      return null;
    }

    if (input instanceof RelSubset subset) {
      RelNode best = subset.getBest();
      return resolve(best != null ? best : subset.getOriginal(), depth + 1);
    }
    if (input instanceof ChalkTableScan scan) {
      return new Resolved(scan.chalkTable(), new ArrayList<>(scan.projection()));
    }
    if (input instanceof ChalkIndexLookup lookup) {
      return new Resolved(lookup.chalkTable(), new ArrayList<>(lookup.projection()));
    }
    if (input instanceof TableScan scan) {
      ChalkTable table = scan.getTable().unwrap(ChalkTable.class);
      if (table == null) {
        return null;
      }
      List<Integer> identity = new ArrayList<>();
      for (int i = 0; i < scan.getRowType().getFieldCount(); i++) {
        identity.add(i);
      }
      return new Resolved(table, identity);
    }
    if (input instanceof Filter filter) {
      // A filter only removes rows; the foreign key still holds for the ones that remain, and the
      // caller multiplies by the filter's own selectivity through the input's row count.
      return resolve(filter.getInput(), depth + 1);
    }
    if (input instanceof Sort sort) {
      return sort.fetch == null && sort.offset == null ? resolve(sort.getInput(), depth + 1) : null;
    }
    if (input instanceof Project project) {
      Resolved below = resolve(project.getInput(), depth + 1);
      if (below == null) {
        return null;
      }
      List<Integer> mapped = new ArrayList<>(project.getProjects().size());
      for (RexNode expression : project.getProjects()) {
        if (expression instanceof RexInputRef ref && ref.getIndex() < below.fields.size()) {
          mapped.add(below.fields.get(ref.getIndex()));
        } else {
          mapped.add(-1);
        }
      }
      return new Resolved(below.table, mapped);
    }

    return null;
  }

  /** A join input that reads one Chalk table, and where each of its fields comes from. */
  private static final class Resolved {
    private final ChalkTable table;
    private final List<Integer> fields;

    Resolved(ChalkTable table, List<Integer> fields) {
      this.table = table;
      this.fields = fields;
    }

    /** The table columns behind these field ordinals, or null when any of them is derived. */
    @Nullable List<Integer> columns(List<Integer> ordinals) {
      List<Integer> columns = new ArrayList<>(ordinals.size());
      for (int ordinal : ordinals) {
        if (ordinal < 0 || ordinal >= fields.size() || fields.get(ordinal) < 0) {
          return null;
        }
        columns.add(fields.get(ordinal));
      }
      return columns;
    }
  }
}
