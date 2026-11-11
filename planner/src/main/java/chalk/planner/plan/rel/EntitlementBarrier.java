package chalk.planner.plan.rel;

import java.util.List;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.SingleRel;
import org.apache.calcite.rel.metadata.DelegatingMetadataRel;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The fence over an entitled leaf (F288): what the statement does with the leaf's rows happens
 * above it, after the entitlement has excluded the rows the principal is not admitted to.
 *
 * <p>Without it the planner's own rules carry a statement's filter through {@code Project_D} and
 * merge it into {@code Filter_R}, where both conditions are evaluated together over every row the
 * table holds: a cast that fails quotes a row the principal is not shown, and a condition that
 * fails only for some value says whether such a row exists. Nothing crosses the fence unless a rule
 * says it may, and the rules let through only what cannot leak (see {@code EntitlementBarrierRules}
 * and {@code Leakproof}), as PostgreSQL's {@code security_barrier} does. It changes no row: it is
 * dropped from the plan once the search has chosen one, and no executor sees it.
 */
public final class EntitlementBarrier extends SingleRel implements DelegatingMetadataRel {

  public EntitlementBarrier(RelOptCluster cluster, RelTraitSet traits, RelNode input) {
    super(cluster, traits, input);
  }

  /** The fence over {@code leaf}, in the logical convention the rewrite works in. */
  public static EntitlementBarrier over(RelNode leaf) {
    return new EntitlementBarrier(
        leaf.getCluster(), leaf.getCluster().traitSetOf(Convention.NONE), leaf);
  }

  /**
   * Every metadata question about the fence is the same question about the leaf it fences: its
   * rows, columns, keys, predicates and order are the leaf's.
   */
  @Override
  public RelNode getMetadataDelegateRel() {
    return getInput();
  }

  @Override
  public RelNode copy(RelTraitSet traitSet, List<RelNode> inputs) {
    return new EntitlementBarrier(getCluster(), traitSet, sole(inputs));
  }

  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    return planner.getCostFactory().makeTinyCost();
  }
}
