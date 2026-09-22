package chalk.planner.plan.rel;

import chalk.planner.catalog.ChalkTable;
import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.CostModel;
import com.google.common.collect.ImmutableList;
import java.util.List;
import org.apache.calcite.plan.DeriveMode;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollationTraitDef;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelWriter;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.metadata.RelMdCollation;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A scan the client performs through the source's {@code ScanAsync}. Maps to the IR's {@code Read}.
 *
 * <p>Column pruning is expressed by scanning a {@link ProjectedRelOptTable} rather than by carrying
 * a projection alongside the full row type, so every metadata handler that asks the table a question
 * — row count, unique keys, collations — gets an answer about the columns this scan actually
 * produces.
 */
public final class ChalkTableScan extends TableScan implements ChalkRel {
  private final long rowGoal;

  private ChalkTableScan(
      RelOptCluster cluster, RelTraitSet traitSet, RelOptTable table, long rowGoal) {
    super(cluster, traitSet, ImmutableList.of(), table);
    this.rowGoal = rowGoal;
  }

  /** A scan of every column, carrying the table's declared collation as a trait. */
  public static ChalkTableScan create(RelOptCluster cluster, RelOptTable table) {
    RelTraitSet traits =
        cluster
            .traitSetOf(ChalkConvention.LOCAL)
            .replaceIfs(RelCollationTraitDef.INSTANCE, () -> RelMdCollation.table(table));
    return new ChalkTableScan(cluster, traits, table, 0L);
  }

  /** A scan of the given table columns, in that output order. */
  public static ChalkTableScan create(
      RelOptCluster cluster, RelOptTable table, org.apache.calcite.util.ImmutableIntList projection) {
    return create(cluster, ProjectedRelOptTable.of(table, projection));
  }

  /** The underlying catalog table, whatever projection wraps it. */
  public ChalkTable chalkTable() {
    ChalkTable chalkTable = table.unwrap(ChalkTable.class);
    if (chalkTable == null) {
      throw new IllegalStateException(
          "ChalkTableScan over a table that is not a ChalkTable: " + table.getQualifiedName());
    }
    return chalkTable;
  }

  /** Table column indexes in output order — the IR's {@code Read.projection}. */
  public List<Integer> projection() {
    ProjectedRelOptTable projected = table.unwrap(ProjectedRelOptTable.class);
    if (projected != null) {
      return projected.projection();
    }
    return org.apache.calcite.util.ImmutableIntList.identity(getRowType().getFieldCount());
  }

  /**
   * How many rows a parent expects to pull from this scan before it stops, or zero when nothing
   * above it said. A planning fact and a hint to the source, never a semantic: the scan serves
   * every row it is asked for whatever this says, and the {@code Fetch} or {@code TopN} above stays
   * authoritative.
   */
  public long rowGoal() {
    return rowGoal;
  }

  /**
   * The same scan asked for at most {@code goal} rows. A different rel, not a mutation: the goal is
   * in the digest, so Volcano keeps the two apart and a goaled scan is reachable only from the
   * parent that asked for one.
   */
  public ChalkTableScan withRowGoal(long goal) {
    long wanted = Math.max(0L, goal);
    return wanted == rowGoal
        ? this
        : new ChalkTableScan(getCluster(), traitSet, table, wanted);
  }

  @Override
  public RelNode copy(RelTraitSet traitSet, List<RelNode> inputs) {
    return new ChalkTableScan(getCluster(), traitSet, table, rowGoal);
  }

  /** A leaf: it delivers the table's declared collation and there is nothing below to derive from. */
  @Override
  public DeriveMode getDeriveMode() {
    return DeriveMode.PROHIBITED;
  }

  /**
   * The projection must be part of the digest: two scans of the same table with different projected
   * columns are different rels, and {@code TableScan.explainTerms} names only the table.
   */
  @Override
  public RelWriter explainTerms(RelWriter pw) {
    RelWriter written = super.explainTerms(pw).item("projection", projection());

    // The row goal is a digest item, because a goaled scan estimates and costs differently from the
    // same scan without one and Volcano must never merge the two. Written only when set, so no plan
    // that carries no goal gains a term.
    if (rowGoal > 0) {
      written = written.item("goal", rowGoal);
    }

    // The entitlement rewrite's mark (step 26, 16-entitlements.md §3.12): this leaf's own verdict
    // for every column, in the table's ordinals. It is the *whole* map and not a summary of it,
    // because these terms are the node's digest: two occurrences of one table that disclose
    // differently — a self-join where one side is narrowed to a tenancy — must be two nodes, and a
    // summary that read the same for both let Volcano unify them and give one occurrence's verdict
    // to the other's read. Only ever present on a scan the pass wrapped, so no recorded plan of an
    // unentitled catalog gains a term.
    chalk.planner.entitlement.DisclosureMap disclosure =
        chalk.planner.entitlement.EntitledRelOptTable.disclosureOf(table);
    if (disclosure == null) {
      return written;
    }
    return written
        .item("entitled", disclosure.descriptorHash())
        .item("disclosures", disclosure.columns())
        .item("statistical", disclosure.statisticalColumns());
  }

  /**
   * Cost model v8 (D276, {@code 46-row-goals.md} §2.2): the table's rows, or the goal when a limit
   * above states a smaller one, because that is how many rows this scan will be pulled for.
   *
   * <p>Everything above it then follows from the ordinary bottom-up metadata: the filter costs these
   * rows and estimates {@code sel ×} them, the project the same, the limit its fetch. The goal is a
   * different rel, not a different way of adding up costs, so Volcano's cumulative-cost semantics
   * are untouched.
   */
  @Override
  public double estimateRowCount(RelMetadataQuery mq) {
    double rows = table.getRowCount();
    return rowGoal > 0 ? Math.min(rows, rowGoal) : rows;
  }

  /**
   * {@code rows × scan_row_cost × (projected / total columns)} — the table's cost profile decides
   * what a row costs (D38), and reading fewer columns costs proportionally less. {@code rows} is
   * the goaled count of {@link #estimateRowCount} (cost model v8).
   *
   * <p>The same number goes in both slots because Calcite's {@code VolcanoCost} orders costs by its
   * {@code rowCount} field alone; a cpu term it never reads would be decorative (ADR 0015).
   */
  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double rows = estimateRowCount(mq);
    ProjectedRelOptTable projected = table.unwrap(ProjectedRelOptTable.class);
    int totalColumns =
        projected == null
            ? getRowType().getFieldCount()
            : projected.unprojected().getRowType().getFieldCount();
    double share = totalColumns == 0 ? 1.0 : (double) getRowType().getFieldCount() / totalColumns;
    double work = CostModel.of(chalkTable().costProfile()).scan(rows, share);
    return planner.getCostFactory().makeCost(work, work, 0);
  }
}
