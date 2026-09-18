package chalk.planner.plan.rel;

import java.util.List;
import org.apache.calcite.plan.DeriveMode;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.PhysicalNode;
import org.apache.calcite.util.Pair;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Marker for a relational operator in {@code ChalkConvention.LOCAL}. Every implementation maps 1:1
 * onto an IR node kind (docs/design/03-planner.md §5.1); {@code RelToIr} switches on the concrete
 * class and anything else is a planner bug.
 *
 * <p>Also a {@link PhysicalNode}, which is what Calcite's top-down optimiser (D47,
 * {@code 12-joins.md} §0) calls to move orderings around: {@code passThroughTraits} offers a parent's
 * required collation to an input that could satisfy it, {@code deriveTraits} reports the ordering
 * this rel actually delivers given its input's. The defaults are {@code EnumerableRel}'s — null,
 * meaning "no opinion" — rather than {@code PhysicalNode}'s, which throw.
 */
public interface ChalkRel extends PhysicalNode {

  @Override
  default @Nullable Pair<RelTraitSet, List<RelTraitSet>> passThroughTraits(RelTraitSet required) {
    return null;
  }

  @Override
  default @Nullable Pair<RelTraitSet, List<RelTraitSet>> deriveTraits(
      RelTraitSet childTraits, int childId) {
    return null;
  }

  @Override
  default DeriveMode getDeriveMode() {
    return DeriveMode.LEFT_FIRST;
  }
}
