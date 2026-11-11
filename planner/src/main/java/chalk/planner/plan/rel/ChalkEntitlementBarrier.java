package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import com.google.common.collect.ImmutableList;
import java.util.List;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.SingleRel;
import org.apache.calcite.rel.metadata.DelegatingMetadataRel;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.util.Pair;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@link EntitlementBarrier} in the local convention, so the search keeps the fence while it
 * chooses: a source's subtree ends below it, and what is above it runs here, over the rows the
 * entitlement admitted (F288). It delivers its input's rows in its input's order, and is dropped
 * from the chosen plan before the plan is written (no IR node is ever made for it).
 */
public final class ChalkEntitlementBarrier extends SingleRel implements ChalkRel, DelegatingMetadataRel {

  private ChalkEntitlementBarrier(RelOptCluster cluster, RelTraitSet traits, RelNode input) {
    super(cluster, traits, input);
  }

  public static ChalkEntitlementBarrier create(RelNode input) {
    return new ChalkEntitlementBarrier(
        input.getCluster(), input.getTraitSet().replace(ChalkConvention.LOCAL), input);
  }

  /**
   * {@code plan} without its fences: each replaced by its input, which already sits where the fence
   * kept it. Unchanged where it holds none.
   */
  public static RelNode strip(RelNode plan) {
    if (plan instanceof ChalkEntitlementBarrier barrier) {
      return strip(barrier.getInput());
    }
    List<RelNode> inputs = plan.getInputs();
    List<RelNode> stripped = new java.util.ArrayList<>(inputs.size());
    boolean changed = false;
    for (RelNode input : inputs) {
      RelNode without = strip(input);
      changed |= without != input;
      stripped.add(without);
    }
    return changed ? plan.copy(plan.getTraitSet(), stripped) : plan;
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
    return new ChalkEntitlementBarrier(getCluster(), traitSet, sole(inputs));
  }

  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    return planner.getCostFactory().makeTinyCost();
  }

  /**
   * No required order is passed down: offering one through the fence let the top-down search
   * choose a plan whose fence read its own set, a cycle the cheapest-plan walk never leaves, where
   * every alternative was priced as one a guard refuses ({@code PUSHDOWN_REQUIRED}). An order the
   * statement asks for is met above the fence, over the admitted rows.
   */
  @Override
  public @Nullable Pair<RelTraitSet, List<RelTraitSet>> passThroughTraits(RelTraitSet required) {
    return null;
  }

  /** And it delivers whatever its input delivers. */
  @Override
  public @Nullable Pair<RelTraitSet, List<RelTraitSet>> deriveTraits(
      RelTraitSet childTraits, int childId) {
    return sameCollation(childTraits);
  }

  private @Nullable Pair<RelTraitSet, List<RelTraitSet>> sameCollation(RelTraitSet other) {
    RelCollation collation = other.getCollation();
    if (collation == null || collation == RelCollations.EMPTY) {
      return null;
    }
    RelTraitSet traits = traitSet.replace(collation);
    return Pair.of(traits, ImmutableList.of(traits));
  }
}
