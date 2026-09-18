package chalk.planner.plan.rel;

import chalk.planner.catalog.ChalkTable;
import chalk.planner.plan.CostModel;
import chalk.planner.plan.SourceConvention;
import com.google.common.collect.ImmutableList;
import java.util.List;
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
import org.apache.calcite.util.ImmutableIntList;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The leaf of a pushed subtree: a scan the <em>source</em> performs (D83). Structurally a twin of
 * {@link ChalkTableScan}, but in a {@link SourceConvention}, so everything above it that enters the
 * same convention is pushed with it and the whole thing becomes one {@code RemoteQuery}.
 *
 * <p>Column pruning is a {@link ProjectedRelOptTable} exactly as it is for a local scan, so the
 * metadata handlers answer about the columns this scan actually produces.
 */
public final class SourceScan extends TableScan implements SourceRel {

  private SourceScan(RelOptCluster cluster, RelTraitSet traitSet, RelOptTable table) {
    super(cluster, traitSet, ImmutableList.of(), table);
  }

  /** A scan of every column, carrying the table's declared collation as a trait. */
  public static SourceScan create(
      RelOptCluster cluster, RelOptTable table, SourceConvention convention) {
    RelTraitSet traits =
        cluster
            .traitSetOf(convention)
            .replaceIfs(RelCollationTraitDef.INSTANCE, () -> RelMdCollation.table(table));
    return new SourceScan(cluster, traits, table);
  }

  /** A scan of the given table columns, in that output order. */
  public static SourceScan create(
      RelOptCluster cluster,
      RelOptTable table,
      ImmutableIntList projection,
      SourceConvention convention) {
    return create(cluster, ProjectedRelOptTable.of(table, projection), convention);
  }

  /** The underlying catalog table, whatever projection wraps it. */
  public ChalkTable chalkTable() {
    ChalkTable chalkTable = table.unwrap(ChalkTable.class);
    if (chalkTable == null) {
      throw new IllegalStateException(
          "SourceScan over a table that is not a ChalkTable: " + table.getQualifiedName());
    }
    return chalkTable;
  }

  /** Table column indexes in output order — the IR's {@code Read.projection}. */
  public List<Integer> projection() {
    ProjectedRelOptTable projected = table.unwrap(ProjectedRelOptTable.class);
    if (projected != null) {
      return projected.projection();
    }
    return ImmutableIntList.identity(getRowType().getFieldCount());
  }

  @Override
  public RelNode copy(RelTraitSet traitSet, List<RelNode> inputs) {
    return new SourceScan(getCluster(), traitSet, table);
  }

  /**
   * The table, the projection, and — where the pass wrapped this leaf — its disclosure map, for the
   * reason {@link ChalkTableScan#explainTerms} carries it one convention up (F95).
   *
   * <p>A {@code TableScan}'s digest is these terms, and two occurrences of one table that disclose
   * differently must be two nodes or the planner may unify them and serve one occurrence's verdict
   * on the other's read. Pushing a leaf into a source does not make that stop being true: without
   * the map here, two pushed occurrences are kept apart only by their row types happening to differ,
   * which a self-join over the same columns arranges for them not to. It is the same class of
   * collision ADR 0060 §2 measured for the chain scan, and the {@code RemoteQuery} each of these
   * becomes carries the map on its own {@code Read}.
   *
   * <p>Only ever present on a scan the pass wrapped, so no recorded plan of an unentitled catalog
   * gains a term.
   */
  @Override
  public RelWriter explainTerms(RelWriter pw) {
    RelWriter written = super.explainTerms(pw).item("projection", projection());
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
   * Free, like every other pushed operator: what the source does costs Chalk nothing, and the whole
   * price of the fetch — the round trip and the rows that come back — is charged once, at the
   * boundary. Charging here as well would make "push more" lose to "push less" for no reason.
   */
  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    return planner.getCostFactory().makeCost(0, 0, 0);
  }
}
