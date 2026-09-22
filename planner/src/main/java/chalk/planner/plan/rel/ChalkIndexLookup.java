package chalk.planner.plan.rel;

import chalk.ir.v1.Index;
import chalk.ir.v1.IndexKind;
import chalk.ir.v1.SortDirection;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.ChalkSelectivity;
import chalk.planner.plan.CostModel;
import chalk.planner.plan.IndexMatcher;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.DeriveMode;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.AbstractRelNode;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollationTraitDef;
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelFieldCollation;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelWriter;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.sql.SqlExplainLevel;
import org.apache.calcite.util.ImmutableBitSet;
import org.apache.calcite.util.ImmutableIntList;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A range lookup on a declared index, executed by the client through its source's
 * {@code IndexLookupAsync}. Maps to the IR's {@code IndexLookup} (D37).
 *
 * <p>A leaf, like {@link ChalkTableScan}: the index is the input. The residual a lookup cannot
 * enforce becomes a {@link ChalkFilter} above it — nothing is fused in M2 — so this node's output is
 * exactly the rows its ranges cover, in the index's key order.
 */
public final class ChalkIndexLookup extends AbstractRelNode implements ChalkRel {
  private final RelOptTable table;
  private final ChalkTable chalkTable;
  private final Index index;
  private final ImmutableList<IndexMatcher.Range> ranges;
  private final ImmutableIntList projection;
  private final ChalkSelectivity.Estimate selectivity;
  private final long rowGoal;

  private ChalkIndexLookup(
      RelOptCluster cluster,
      RelTraitSet traits,
      RelOptTable table,
      ChalkTable chalkTable,
      Index index,
      ImmutableList<IndexMatcher.Range> ranges,
      ImmutableIntList projection,
      RelDataType rowType,
      ChalkSelectivity.Estimate selectivity,
      long rowGoal) {
    super(cluster, traits);
    this.table = table;
    this.chalkTable = chalkTable;
    this.index = index;
    this.ranges = ranges;
    this.projection = projection;
    this.rowType = rowType;
    this.selectivity = selectivity;
    this.rowGoal = rowGoal;
  }

  /**
   * A lookup that produces the same row as {@code scan} — same columns, same order — for the rows
   * the ranges cover.
   */
  public static ChalkIndexLookup create(
      ChalkTableScan scan,
      Index index,
      ImmutableList<IndexMatcher.Range> ranges,
      ChalkSelectivity.Estimate selectivity) {
    RelOptCluster cluster = scan.getCluster();
    ChalkTable chalkTable = scan.chalkTable();
    ImmutableIntList projection = ImmutableIntList.copyOf(scan.projection());
    List<RelCollation> collations = collations(index, ranges, projection);
    RelTraitSet traits =
        cluster.traitSetOf(ChalkConvention.LOCAL).replaceIfs(
            RelCollationTraitDef.INSTANCE, () -> collations);

    return new ChalkIndexLookup(
        cluster,
        traits,
        scan.getTable(),
        chalkTable,
        index,
        ranges,
        projection,
        scan.getRowType(),
        selectivity,
        0L);
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

  public ImmutableList<IndexMatcher.Range> ranges() {
    return ranges;
  }

  /** Table column indexes in output order — the IR's {@code IndexLookup.projection}. */
  public ImmutableIntList projection() {
    return projection;
  }

  /** How selective the ranges are, and whether that was measured or guessed. */
  public ChalkSelectivity.Estimate selectivity() {
    return selectivity;
  }

  /**
   * How many rows a parent expects to pull from this lookup before it stops, or zero when nothing
   * above it said. A planning fact and a hint to the source, never a semantic: the lookup serves
   * every row of its ranges whatever this says, and the {@code Fetch} or {@code TopN} above stays
   * authoritative.
   */
  public long rowGoal() {
    return rowGoal;
  }

  /**
   * The same lookup asked for at most {@code goal} rows. A different rel, not a mutation: the goal
   * is in the digest, so Volcano keeps the two apart and a goaled lookup is reachable only from the
   * parent that asked for one.
   */
  public ChalkIndexLookup withRowGoal(long goal) {
    long wanted = Math.max(0L, goal);
    if (wanted == rowGoal) {
      return this;
    }

    return new ChalkIndexLookup(
        getCluster(),
        traitSet,
        table,
        chalkTable,
        index,
        ranges,
        projection,
        rowType,
        selectivity,
        wanted);
  }

  /**
   * The orderings this lookup delivers.
   *
   * <p>The index's key order, to start with. Then every ordering the equality-bound prefix implies:
   * a column pinned to one value is a tie in every comparison, so a lookup with {@code symbol = ?}
   * on {@code (symbol, ts)} is ordered by {@code [symbol, ts]}, by {@code [ts]} — and by
   * {@code [ts, symbol]}, which is the one that matters, because that is the collation the table
   * itself declares and therefore the one a parent rel will have asked its input for.
   *
   * <p>Claiming it is what lets a lookup be used under a {@code Project} or an {@code Aggregate}
   * that inherited the table's collation as a requirement it does not actually need (ADR 0015).
   * Interleavings other than "the pinned columns first" and "the pinned columns last" are not
   * claimed; they are legitimate and no query has needed one.
   *
   * <p>Only for a single range. Several ranges are a union, and a union of ordered runs is ordered
   * by the leading column only if the runs are read in that column's order, which the executor does
   * not promise.
   */
  private static List<RelCollation> collations(
      Index index, ImmutableList<IndexMatcher.Range> ranges, ImmutableIntList projection) {
    if (!isOrdered(index.getKind()) || ranges.size() != 1) {
      return ImmutableList.of();
    }

    List<RelFieldCollation> full = new ArrayList<>(index.getColumnsCount());
    for (int i = 0; i < index.getColumnsCount(); i++) {
      int field = projection.indexOf(index.getColumns(i));
      if (field < 0) {
        break; // a key column this lookup does not emit ends the ordering it can claim
      }

      full.add(ChalkTable.toFieldCollation(field, direction(index, i)));
    }

    if (full.isEmpty()) {
      return ImmutableList.of();
    }

    List<RelCollation> collations = new ArrayList<>();
    collations.add(RelCollations.of(full));

    int pinned = Math.min(equalityPrefix(ranges.get(0)), full.size());
    for (int drop = 1; drop <= pinned && drop <= full.size(); drop++) {
      List<RelFieldCollation> rest = full.subList(drop, full.size());
      List<RelFieldCollation> constants = full.subList(0, drop);
      if (!rest.isEmpty()) {
        collations.add(RelCollations.of(rest));
      }

      List<RelFieldCollation> trailing = new ArrayList<>(rest);
      trailing.addAll(constants);
      collations.add(RelCollations.of(trailing));
    }

    // RelCompositeTrait asserts its members are in natural order (and Volcano compares trait sets
    // structurally), so the list is sorted and de-duplicated rather than left in derivation order.
    java.util.TreeSet<RelCollation> sorted = new java.util.TreeSet<>();
    sorted.addAll(collations);
    return ImmutableList.copyOf(sorted);
  }

  /** How many leading key columns the range pins to a single value. */
  private static int equalityPrefix(IndexMatcher.Range range) {
    int shared = Math.min(range.lower().size(), range.upper().size());
    int prefix = 0;
    while (prefix < shared && range.lower().get(prefix).equals(range.upper().get(prefix))) {
      prefix++;
    }

    // The last shared column is only pinned when both sides include it.
    if (prefix == shared && prefix > 0 && (!range.lowerInclusive() || !range.upperInclusive())) {
      prefix--;
    }

    return prefix;
  }

  private static SortDirection direction(Index index, int position) {
    return position < index.getDirectionsCount()
        ? index.getDirections(position)
        : SortDirection.SORT_DIRECTION_ASC_NULLS_LAST;
  }

  /**
   * Whether a kind answers ranges and yields rows in key order. The clustered kind does everything
   * the ordered kind does (D257, {@code 34-clustered-indexes.md} §3) — it <em>is</em> ordered, with a
   * copy of its columns beside it — so every rule that asks the kind gets the same answer for both,
   * and only {@link #computeSelfCost} tells them apart.
   */
  public static boolean isOrdered(IndexKind kind) {
    return kind == IndexKind.INDEX_KIND_ORDERED || kind == IndexKind.INDEX_KIND_CLUSTERED;
  }

  /**
   * Whether this lookup's projection lies inside the index's copy — the question the cost model asks,
   * and the same one the source asks before serving the lookup as slices of it. An empty covering set
   * on a clustered index means every column.
   */
  private boolean covered() {
    if (index.getKind() != IndexKind.INDEX_KIND_CLUSTERED) {
      return false;
    }

    if (index.getCoveringCount() == 0) {
      return true;
    }

    for (int column : projection) {
      if (!index.getCoveringList().contains(column)) {
        return false;
      }
    }

    return true;
  }

  /** The columns a unique index makes unique, in this node's own row. */
  public @Nullable ImmutableBitSet uniqueColumns() {
    if (!index.getUnique()) {
      return null;
    }

    ImmutableBitSet.Builder columns = ImmutableBitSet.builder();
    for (int column : index.getColumnsList()) {
      int field = projection.indexOf(column);
      if (field < 0) {
        return null;
      }

      columns.set(field);
    }

    return columns.build();
  }

  @Override
  public RelNode copy(RelTraitSet traitSet, List<RelNode> inputs) {
    return new ChalkIndexLookup(
        getCluster(),
        traitSet,
        table,
        chalkTable,
        index,
        ranges,
        projection,
        rowType,
        selectivity,
        rowGoal);
  }

  /** A leaf: it delivers the index's key order and there is nothing below to derive from. */
  @Override
  public DeriveMode getDeriveMode() {
    return DeriveMode.PROHIBITED;
  }

  /**
   * Everything that makes two lookups different has to be here: the digest is what Volcano uses to
   * decide two rels are the same. {@code sel} is added only at {@code ALL_ATTRIBUTES}, which the
   * digest never uses, so a cost estimate cannot merge or split a {@code RelSet}.
   */
  @Override
  public RelWriter explainTerms(RelWriter pw) {
    RelWriter writer =
        super.explainTerms(pw)
            .item("table", table.getQualifiedName())
            .item("index", index.getName())
            .item("ranges", ranges)
            .item("projection", projection);

    // The row goal is a digest item, because a goaled lookup estimates and costs differently from
    // the same lookup without one and Volcano must never merge the two. Written only when set, so
    // no plan that carries no goal gains a term.
    if (rowGoal > 0) {
      writer.item("goal", rowGoal);
    }

    // D257: the kind, and what the copy is for. Written only for a clustered index — every lookup
    // before D257 was over an ordered or a hash one and its plan text is unchanged — so the text
    // says both that this index is clustered and whether this lookup is the covered case the cost
    // model priced as a scan, which is the whole of the difference between the two kinds.
    if (index.getKind() == IndexKind.INDEX_KIND_CLUSTERED) {
      writer.item("kind", index.getKind()).item("covered", covered());
    }

    if (pw.getDetailLevel() == SqlExplainLevel.ALL_ATTRIBUTES) {
      writer.item("sel", selectivity.text());
    }

    return writer;
  }

  /** Σ range selectivities × table rows, capped at the table, and 1 for a unique equality. */
  @Override
  public double estimateRowCount(RelMetadataQuery mq) {
    Double rows = chalkTable.rowCount();
    if (rows == null) {
      return super.estimateRowCount(mq);
    }

    if (index.getUnique() && allPointsOnTheWholeKey()) {
      return ranges.size();
    }

    return Math.max(1.0, Math.min(rows, selectivity.value() * rows));
  }

  private boolean allPointsOnTheWholeKey() {
    for (IndexMatcher.Range range : ranges) {
      if (!range.isPoint() || range.boundedColumns() != index.getColumnsCount()) {
        return false;
      }
    }

    return true;
  }

  /**
   * {@code ranges × seek + matched rows × lookup row}, per the table's cost profile — or {@code
   * ranges × seek + matched rows × scan row} when this lookup's projection lies inside a clustered
   * index's copy (D257, §3), because those columns are held in the index's own key order and the
   * source reads them sequentially out of it rather than gathering them through the permutation. io
   * is zero: whether reading a row touches a disk is the source's business, and the profile is where
   * a host says so.
   */
  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    CostModel costs = CostModel.of(chalkTable.costProfile());
    double rows = mq.getRowCount(this);
    double work =
        covered() ? costs.clusteredLookup(ranges.size(), rows) : costs.lookup(ranges.size(), rows);
    return planner.getCostFactory().makeCost(work, work, 0);
  }
}
