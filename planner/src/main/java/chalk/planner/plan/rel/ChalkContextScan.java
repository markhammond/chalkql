package chalk.planner.plan.rel;

import chalk.planner.entitlement.ContextTable;
import chalk.planner.plan.ChalkConvention;
import com.google.common.collect.ImmutableList;
import java.util.List;
import org.apache.calcite.plan.DeriveMode;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelWriter;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A scan of one of the execution context's relations (docs/design/16-entitlements.md §2 and §4).
 *
 * <p>{@code RelToIr} turns it into the IR's {@code ContextTable}, which carries the binding's
 * <em>name</em> and nothing else: the rows are the host's, they are not in the plan, not in the
 * digest and not in the logs, and the executor materialises them in the arena at execution start.
 */
public final class ChalkContextScan extends TableScan implements ChalkRel {

  private ChalkContextScan(RelOptCluster cluster, RelTraitSet traits, RelOptTable table) {
    super(cluster, traits, ImmutableList.of(), table);
  }

  public static ChalkContextScan create(RelOptCluster cluster, RelOptTable table) {
    return new ChalkContextScan(cluster, cluster.traitSetOf(ChalkConvention.LOCAL), table);
  }

  /** The name the host bound the relation under. */
  public String contextName() {
    ContextTable context = getTable().unwrap(ContextTable.class);
    if (context == null) {
      throw new IllegalStateException("a ChalkContextScan over something that is not a ContextTable");
    }
    return context.contextName();
  }

  @Override
  public RelNode copy(RelTraitSet traitSet, List<RelNode> inputs) {
    assert inputs.isEmpty();
    return new ChalkContextScan(getCluster(), traitSet, getTable());
  }

  @Override
  public RelWriter explainTerms(RelWriter pw) {
    return super.explainTerms(pw).item("context", contextName());
  }

  /** A leaf handed over whole: nothing is passed down and nothing is derived. */
  @Override
  public DeriveMode getDeriveMode() {
    return DeriveMode.PROHIBITED;
  }

  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double rows = table.getRowCount();
    return planner.getCostFactory().makeCost(rows, rows, 0);
  }
}
