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

  private ChalkTableScan(RelOptCluster cluster, RelTraitSet traitSet, RelOptTable table) {
    super(cluster, traitSet, ImmutableList.of(), table);
  }

  /** A scan of every column, carrying the table's declared collation as a trait. */
  public static ChalkTableScan create(RelOptCluster cluster, RelOptTable table) {
    RelTraitSet traits =
        cluster
            .traitSetOf(ChalkConvention.LOCAL)
            .replaceIfs(RelCollationTraitDef.INSTANCE, () -> RelMdCollation.table(table));
    return new ChalkTableScan(cluster, traits, table);
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

  @Override
  public RelNode copy(RelTraitSet traitSet, List<RelNode> inputs) {
    return new ChalkTableScan(getCluster(), traitSet, table);
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
   * {@code rows × scan_row_cost × (projected / total columns)} — the table's cost profile decides
   * what a row costs (D38), and reading fewer columns costs proportionally less.
   *
   * <p>The same number goes in both slots because Calcite's {@code VolcanoCost} orders costs by its
   * {@code rowCount} field alone; a cpu term it never reads would be decorative (ADR 0015).
   */
  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double rows = table.getRowCount();
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
