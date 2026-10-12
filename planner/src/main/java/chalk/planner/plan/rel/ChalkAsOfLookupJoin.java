package chalk.planner.plan.rel;

import chalk.ir.v1.Index;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.CostModel;
import com.google.common.collect.ImmutableList;
import java.util.List;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelWriter;
import org.apache.calcite.rel.SingleRel;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.util.ImmutableBitSet;
import org.apache.calcite.util.ImmutableIntList;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * An ASOF join whose right input is an in-process table, answered by seeking an ordered index on it
 * (D338, design 63 §4): one range for each distinct combination of a left row's keys and time, whose
 * first row is the match. Maps to the IR's {@code AsOfLookupJoin}.
 *
 * <p>The index's key begins with the right keys' columns and the {@link #fixedColumns()}, each once
 * and in any order, and goes on with the time column, ascending. Its one input is the left; the table
 * is read through the index, as {@link ChalkIndexLookup} reads it.
 */
public final class ChalkAsOfLookupJoin extends SingleRel implements ChalkRel {
  private final RelOptTable table;
  private final ChalkTable chalkTable;
  private final Index index;
  private final ImmutableIntList projection;
  private final ImmutableIntList leftKeys;
  private final ImmutableIntList rightKeys;
  private final ImmutableIntList fixedColumns;
  private final ImmutableList<RexNode> fixedValues;
  private final int leftTime;
  private final int rightTime;
  private final SqlKind match;
  private final JoinRelType joinType;

  private ChalkAsOfLookupJoin(
      RelOptCluster cluster,
      RelTraitSet traits,
      RelNode left,
      RelDataType rowType,
      RelOptTable table,
      ChalkTable chalkTable,
      Index index,
      ImmutableIntList projection,
      ImmutableIntList leftKeys,
      ImmutableIntList rightKeys,
      ImmutableIntList fixedColumns,
      ImmutableList<RexNode> fixedValues,
      int leftTime,
      int rightTime,
      SqlKind match,
      JoinRelType joinType) {
    super(cluster, traits, left);
    this.rowType = rowType;
    this.table = table;
    this.chalkTable = chalkTable;
    this.index = index;
    this.projection = projection;
    this.leftKeys = leftKeys;
    this.rightKeys = rightKeys;
    this.fixedColumns = fixedColumns;
    this.fixedValues = fixedValues;
    this.leftTime = leftTime;
    this.rightTime = rightTime;
    this.match = match;
    this.joinType = joinType;
  }

  /**
   * The seek that answers {@code join}, whose right input is {@code scan}, filtered to {@code
   * fixedValues} on {@code fixedColumns} or not at all.
   *
   * @param match the comparison read as {@code left time <op> right time}
   */
  public static ChalkAsOfLookupJoin create(
      ChalkAsOfJoin join,
      ChalkTableScan scan,
      Index index,
      List<Integer> leftKeys,
      List<Integer> rightKeys,
      List<Integer> fixedColumns,
      List<RexNode> fixedValues,
      int leftTime,
      int rightTime,
      SqlKind match) {
    RelOptCluster cluster = join.getCluster();
    // Rows come out in the left input's order, but this claims none: a parent that wants one gets
    // it from a sort, and the plain join beside this one is there for the parent that does not.
    RelTraitSet traits = cluster.traitSetOf(ChalkConvention.LOCAL);
    return new ChalkAsOfLookupJoin(
        cluster,
        traits,
        join.getLeft(),
        join.getRowType(),
        scan.getTable(),
        scan.chalkTable(),
        index,
        ImmutableIntList.copyOf(scan.projection()),
        ImmutableIntList.copyOf(leftKeys),
        ImmutableIntList.copyOf(rightKeys),
        ImmutableIntList.copyOf(fixedColumns),
        ImmutableList.copyOf(fixedValues),
        leftTime,
        rightTime,
        match,
        join.getJoinType());
  }

  @Override
  public RelNode copy(RelTraitSet traitSet, List<RelNode> inputs) {
    return new ChalkAsOfLookupJoin(
        getCluster(),
        traitSet,
        sole(inputs),
        rowType,
        table,
        chalkTable,
        index,
        projection,
        leftKeys,
        rightKeys,
        fixedColumns,
        fixedValues,
        leftTime,
        rightTime,
        match,
        joinType);
  }

  public ChalkTable chalkTable() {
    return chalkTable;
  }

  @Override
  public RelOptTable getTable() {
    return table;
  }

  public Index index() {
    return index;
  }

  /** Table column indexes, in the order of the right side's fields. */
  public ImmutableIntList projection() {
    return projection;
  }

  public ImmutableIntList leftKeys() {
    return leftKeys;
  }

  /** Fields of the right side, pairwise with {@link #leftKeys()}. */
  public ImmutableIntList rightKeys() {
    return rightKeys;
  }

  /** Table columns held to one value for the whole execution. */
  public ImmutableIntList fixedColumns() {
    return fixedColumns;
  }

  public ImmutableList<RexNode> fixedValues() {
    return fixedValues;
  }

  public int leftTime() {
    return leftTime;
  }

  /** A field of the right side. */
  public int rightTime() {
    return rightTime;
  }

  /** The comparison, read as {@code left time <op> right time}. */
  public SqlKind match() {
    return match;
  }

  public JoinRelType joinType() {
    return joinType;
  }

  @Override
  public RelWriter explainTerms(RelWriter pw) {
    return super.explainTerms(pw)
        .item("table", table.getQualifiedName())
        .item("index", index.getName())
        .item("projection", projection)
        .item("leftKeys", leftKeys)
        .item("rightKeys", rightKeys)
        .itemIf("fixed", fixedColumns + "=" + fixedValues, !fixedColumns.isEmpty())
        .item("match", "$" + leftTime + " " + match.sql + " $" + rightTime)
        .item("joinType", joinType.lowerName);
  }

  /** At most one row per left row: the left input's count, as the plain join's estimate is. */
  @Override
  public double estimateRowCount(RelMetadataQuery mq) {
    return mq.getRowCount(getInput());
  }

  /**
   * One seek and one row for each distinct key and time of the left input, then the plain join's
   * matching over the rows found. The plain join's cost is its own work plus reading the whole
   * table under it; this one reads nothing else, so cost decides between them.
   */
  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    CostModel costs = CostModel.of(chalkTable.costProfile());
    double leftRows = mq.getRowCount(getInput());
    ImmutableBitSet seekColumns = ImmutableBitSet.of(leftKeys).union(ImmutableBitSet.of(leftTime));
    Double distinct = mq.getDistinctRowCount(getInput(), seekColumns, null);
    double seeks = distinct == null ? leftRows : Math.min(distinct, leftRows);
    double work =
        leftRows
            + (seeks * (costs.lookupSeekCost() + costs.lookupRowCost()))
            + CostModel.asOfJoin(leftRows, seeks, mq.getRowCount(this));
    return planner.getCostFactory().makeCost(work, work, 0);
  }
}
