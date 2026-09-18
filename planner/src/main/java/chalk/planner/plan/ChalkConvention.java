package chalk.planner.plan;

import chalk.planner.plan.rel.ChalkRel;
import chalk.planner.plan.rel.ChalkSort;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.plan.ConventionTraitDef;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelNode;

/**
 * Chalk's own calling convention (D12). Named {@code LOCAL} from the start because M5 gives every
 * source its own convention and this one is the client-side, locally-executed one.
 *
 * <p>Not {@code EnumerableConvention}: enumerable rels come with {@code EnumerableCalc} fusion and
 * an {@code implement()} path Chalk never uses, whereas Chalk rels map 1:1 onto IR nodes, own their
 * cost, and are what M2's index lookups and M5's per-source conventions need anyway.
 */
public final class ChalkConvention extends Convention.Impl {
  public static final ChalkConvention LOCAL = new ChalkConvention("CHALK_LOCAL");

  private ChalkConvention(String name) {
    super(name, ChalkRel.class);
  }

  /**
   * Mirrors {@code EnumerableConvention}: let Volcano insert an abstract converter when a subtree is
   * already in this convention but does not yet satisfy the required traits, so a collation
   * requirement can be met by an enforcer rather than failing to plan.
   */
  @Override
  public boolean useAbstractConvertersForConversion(RelTraitSet fromTraits, RelTraitSet toTraits) {
    return fromTraits.containsIfApplicable(LOCAL) && !fromTraits.satisfies(toTraits);
  }

  /**
   * The physical enforcer the top-down optimiser inserts when a required collation can be neither
   * passed through nor derived (D47): a {@link ChalkSort}, exactly as {@code EnumerableConvention}
   * inserts an {@code EnumerableSort}.
   *
   * <p>This is what makes {@code ORDER BY} over an arbitrary tree plan. Under the M1 bottom-up
   * optimiser the same job was done by {@code ChalkSortRule}'s second alternative and by the
   * abstract converters above; from D47 the enforcer is the general mechanism and the rule keeps
   * only the parts that are not a sort.
   */
  @Override
  public RelNode enforce(RelNode input, RelTraitSet required) {
    RelNode rel = input;
    if (input.getConvention() != LOCAL) {
      rel =
          ConventionTraitDef.INSTANCE.convert(
              input.getCluster().getPlanner(), input, LOCAL, /* allowInfiniteCostConverters= */ true);
      if (rel == null) {
        throw new IllegalStateException("cannot convert to " + LOCAL + ": " + input);
      }
    }

    RelCollation collation = required.getCollation();
    if (collation != null && collation != RelCollations.EMPTY) {
      rel = ChalkSort.create(rel, collation);
    }
    return rel;
  }
}
