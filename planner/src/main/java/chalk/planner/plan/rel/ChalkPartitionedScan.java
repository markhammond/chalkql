package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.AbstractRelNode;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelWriter;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexNode;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A scan of a partitioned table (D106, {@code 20-m5-federation.md} §3): the union of its partitions,
 * each of which is a table in its own source, together with the partition-column value each holds.
 *
 * <p>Semantically a {@code UNION ALL} — no ordering is claimed, duplicates are kept, the row type is
 * every branch's — and that is what the executor does with it. What it adds is memory: the value
 * behind each branch, so a predicate on the partition column can prune at planning
 * ({@link chalk.planner.plan.rules.PartitionPruneRule}) and a key set bound at execution can prune
 * again.
 *
 * <p>It starts in {@code Convention.NONE}, because {@code ChalkTable.toRel} runs before there is an
 * optimiser and the pruning rule wants a logical tree; {@link
 * chalk.planner.plan.rules.ChalkPartitionedScanRule} converts it, and every branch converts
 * independently — which is exactly what lets one partition be a pushed remote query and the next a
 * local scan.
 *
 * <p>It is <em>not</em> a Calcite {@code Union}, though that is what it means. Extending one makes
 * {@code RelFieldTrimmer} rebuild it through {@code RelBuilder.union}, which returns a
 * {@code LogicalUnion} — and the partition values, which are the whole reason the node exists, are
 * gone. As its own rel it survives the trimmer intact; what it gives up is the trimmer's column
 * pruning, which {@link chalk.planner.plan.rules.PartitionRules#PROJECT} does instead.
 *
 * <p>It used to implement Calcite's {@code RelStructuredTypeFlattener.SelfFlatteningRel}, because the
 * flattener dispatches on rel class with no fallback and failed conversion with "no 'rewriteRel'
 * method found" on any rel it had no overload for (ADR 0022). Chalk converts through its own driver
 * now, {@link chalk.planner.plan.ChalkPlanner}, which never runs the flattener — neither over a
 * statement nor over a SQL-bodied table function's expansion (D292, ADR 0077) — so nothing is left to
 * ask this node to flatten itself.
 */
public final class ChalkPartitionedScan extends AbstractRelNode implements ChalkRel {

  private ImmutableList<RelNode> inputs;
  private final List<@Nullable RexNode> partitionValues;
  private final int partitionColumn;
  private final String tableName;

  private ChalkPartitionedScan(
      RelOptCluster cluster,
      RelTraitSet traits,
      List<RelNode> inputs,
      List<@Nullable RexNode> partitionValues,
      int partitionColumn,
      RelDataType rowType,
      String tableName) {
    super(cluster, traits);
    this.inputs = ImmutableList.copyOf(inputs);
    this.partitionValues = java.util.Collections.unmodifiableList(new ArrayList<>(partitionValues));
    this.partitionColumn = partitionColumn;
    this.rowType = rowType;
    this.tableName = tableName;
  }

  /** The logical form, as {@code ChalkTable.toRel} builds it. */
  public static ChalkPartitionedScan logical(
      RelOptCluster cluster,
      List<RelNode> inputs,
      List<@Nullable RexNode> partitionValues,
      int partitionColumn,
      RelDataType rowType,
      String tableName) {
    return new ChalkPartitionedScan(
        cluster,
        cluster.traitSetOf(Convention.NONE),
        inputs,
        partitionValues,
        partitionColumn,
        rowType,
        tableName);
  }

  /** The same node in {@code ChalkConvention.LOCAL}, over converted inputs. */
  public ChalkPartitionedScan intoLocal(List<RelNode> converted) {
    return new ChalkPartitionedScan(
        getCluster(),
        getCluster().traitSetOf(ChalkConvention.LOCAL),
        converted,
        partitionValues,
        partitionColumn,
        rowType,
        tableName);
  }

  /** The same node over projected branches, with the projected row type (partition pruning aside). */
  public ChalkPartitionedScan projected(List<RelNode> projectedInputs, RelDataType projectedRowType) {
    return new ChalkPartitionedScan(
        getCluster(),
        getTraitSet(),
        projectedInputs,
        partitionValues,
        partitionColumn,
        projectedRowType,
        tableName);
  }

  /** The same node with a subset of its partitions, after pruning. */
  public ChalkPartitionedScan pruned(
      List<RelNode> keptInputs, List<@Nullable RexNode> keptValues) {
    return new ChalkPartitionedScan(
        getCluster(), getTraitSet(), keptInputs, keptValues, partitionColumn, rowType, tableName);
  }

  /** The partition-column value each branch holds, parallel to the inputs. Null for a range. */
  public List<@Nullable RexNode> partitionValues() {
    return partitionValues;
  }

  /** The partition column, as an index into this node's row. */
  public int partitionColumn() {
    return partitionColumn;
  }

  /** The logical table's name, for diagnostics. */
  public String tableName() {
    return tableName;
  }

  @Override
  public List<RelNode> getInputs() {
    return inputs;
  }

  @Override
  public RelNode copy(RelTraitSet traitSet, List<RelNode> newInputs) {
    return new ChalkPartitionedScan(
        getCluster(), traitSet, newInputs, partitionValues, partitionColumn, rowType, tableName);
  }

  /**
   * Volcano replaces an input when one subset is merged into another. Calcite's own {@code SetOp}
   * keeps a non-final {@code ImmutableList} for exactly this and rebuilds it; so does this.
   */
  @Override
  public void replaceInput(int ordinalInParent, RelNode p) {
    List<RelNode> replaced = new ArrayList<>(inputs);
    replaced.set(ordinalInParent, p);
    inputs = ImmutableList.copyOf(replaced);
    recomputeDigest();
  }

  @Override
  public RelWriter explainTerms(RelWriter pw) {
    // Inputs first: RelWriterImpl asserts that a rel with inputs explains all of them, and it
    // checks after the first non-input item.
    RelWriter written = super.explainTerms(pw);
    for (int i = 0; i < inputs.size(); i++) {
      written = written.input("partition#" + i, inputs.get(i));
    }

    written = written.item("table", tableName);
    // The expressions themselves, not a string built from them: a list renders exactly as this used
    // to — `[a, range, b]` — and a plan text that redacts (D262) can only redact what it is handed
    // as an expression. A partition value is a literal off the statement, so it must be redactable.
    List<Object> values = new java.util.ArrayList<>(partitionValues.size());
    for (RexNode value : partitionValues) {
      values.add(value == null ? "range" : value);
    }
    return written.item("partitions", values);
  }

  @Override
  public double estimateRowCount(RelMetadataQuery mq) {
    double rows = 0;
    for (RelNode input : inputs) {
      Double branch = mq.getRowCount(input);
      rows += branch == null ? 0 : branch;
    }
    return Math.max(1.0, rows);
  }

  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    // The union itself is free: every row is forwarded, exactly as UNION ALL is (D69). What a
    // partitioned scan costs is what its branches cost, and they carry that themselves.
    double rows = estimateRowCount(mq);
    return planner.getCostFactory().makeCost(rows, 0, 0);
  }
}
