package chalk.planner.plan.rules;

import chalk.planner.plan.ChalkConvention;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.RelOptRule;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.rex.RexNode;

/** Converting a logical rel's inputs into Chalk's convention. */
final class ChalkInputs {
  private ChalkInputs() {}

  /**
   * The input in {@code ChalkConvention.LOCAL}, <b>with no ordering required</b>.
   *
   * <p>The alternative — {@code input.getTraitSet().replace(LOCAL)}, which every M1 converter rule
   * did — carries the collation trait {@code LogicalFilter.create} and {@code LogicalProject.create}
   * derived from the table, and so turns an inherited <em>claim</em> into a <em>requirement</em>. A
   * physical alternative delivering a different ordering (an index lookup on a second key, a merge
   * join's key order) then lands in a {@code RelSubset} nobody asks for and is never considered.
   * That is ADR 0015 V-M2-6, and it survives {@code setTopDownOpt(true)} untouched: top-down changes
   * how alternatives are found, not what a rule asks its input for (V15, ADR 0016).
   *
   * <p>Asking for nothing is safe because a subset that delivers an ordering satisfies one that
   * requires none, and because under top-down {@code deriveTraits} carries the real orderings back
   * up from the leaves — which is where they are known. The logical rels keep their collation
   * traits, which is what {@link ChalkMergeJoinRule} reads to find out what an input can deliver.
   */
  static RelNode unordered(RelNode input) {
    return RelOptRule.convert(input, input.getCluster().traitSetOf(ChalkConvention.LOCAL));
  }

  /**
   * Whether the IR has this join type at all. {@code LEFT_MARK} is Calcite's own: a mark join
   * carries a boolean column saying whether a match was found, and only
   * {@code MarkToSemiOrAntiJoinRule} — which does not always fire — turns it into a semi or anti
   * join. Chalk's sub-query rules do not produce one, and a rule that quietly converted it would
   * emit a plan the IR has no node for (ADR 0016).
   */
  static boolean isExpressible(JoinRelType type) {
    return switch (type) {
      case INNER, LEFT, RIGHT, FULL, SEMI, ANTI -> true;
      default -> false;
    };
  }

  /**
   * The same, into a source's convention (D83): the input as that source would deliver it, with no
   * ordering required. Asking with the logical rel's inherited collation would demand a subset the
   * pushed rels never land in, and the whole subtree would silently stop being pushable.
   */
  static RelNode intoSource(RelNode input, chalk.planner.plan.SourceConvention convention) {
    return RelOptRule.convert(input, input.getCluster().traitSetOf(convention));
  }

  /**
   * Whether this join's condition holds an equality that must match NULL to NULL —
   * {@code a IS NOT DISTINCT FROM b}, and the condition Calcite's own
   * {@code MinusToAntiJoinRule} builds.
   *
   * <p><b>V50, ADR 0024.</b> {@code Join.analyzeCondition()} loses that. Calcite's
   * {@code JoinInfo.of} runs {@link RelOptUtil#splitJoinCondition} with a {@code filterNulls} list
   * and then constructs the {@code JoinInfo} from the keys and the non-equi remainder alone,
   * discarding the flags — so a null-safe equality reads back as an ordinary equi-key and the two
   * NULLs quietly stop matching. Every Chalk node that carries keys carries plain ones, so a rule
   * that built one here would produce a plan that is wrong rather than slow: measured as a
   * self-join on {@code IS NOT DISTINCT FROM} losing its NULL pair, and as {@code EXCEPT} over a
   * nullable column keeping a NULL that another NULL should have removed.
   *
   * <p>The equi-join rules therefore refuse such a condition and leave it to
   * {@link ChalkNestedLoopJoinRule}, which carries the condition whole and evaluates
   * {@code IS NOT DISTINCT FROM} as the kernel it is.
   *
   * <p><b>Only where a key can actually be NULL.</b> Over two NOT NULL columns
   * {@code a IS NOT DISTINCT FROM b} <em>is</em> {@code a = b}, and refusing there would cost every
   * decorrelated {@code LATERAL} its hash join for nothing: Calcite's decorrelator writes the
   * null-safe form whether or not the correlation key is nullable, and twelve corpus queries join
   * on a NOT NULL {@code symbol} that way.
   *
   * <p><b>And only where <em>both</em> keys can be (F82).</b> The pair the two operators disagree on
   * is a NULL matching a NULL, which needs a NULL on each side: where one key is NOT NULL, a NULL on
   * the other matches nothing under either reading — {@code NULL IS NOT DISTINCT FROM x} is FALSE
   * for every value {@code x} — and an outer join still emits the unmatched row from its own pass.
   * Reading it as "either side" is what put a source's remote query on a nested loop's inner side
   * for {@code m7-tenancy} 09: D265 clause (h) put a rule on {@code orders.member_id}, so the
   * sanitiser types the decorrelated count's key nullable, {@code members.id} on the other side
   * stays NOT NULL, and a join whose two operators cannot differ lost the hash join anyway.
   */
  static boolean hasNullSafeEquality(Join join) {
    List<Integer> leftKeys = new ArrayList<>();
    List<Integer> rightKeys = new ArrayList<>();
    List<Boolean> filterNulls = new ArrayList<>();
    List<RexNode> nonEqui = new ArrayList<>();
    RelOptUtil.splitJoinCondition(
        join.getLeft(),
        join.getRight(),
        join.getCondition(),
        leftKeys,
        rightKeys,
        filterNulls,
        nonEqui);

    List<RelDataTypeField> left = join.getLeft().getRowType().getFieldList();
    List<RelDataTypeField> right = join.getRight().getRowType().getFieldList();
    for (int i = 0; i < filterNulls.size(); i++) {
      if (Boolean.FALSE.equals(filterNulls.get(i))
          && left.get(leftKeys.get(i)).getType().isNullable()
          && right.get(rightKeys.get(i)).getType().isNullable()) {
        return true;
      }
    }

    return false;
  }

  /** The input in {@code LOCAL}, sorted the way a merge join needs it. */
  static RelNode sorted(RelNode input, RelCollation collation) {
    RelTraitSet traits =
        input.getCluster().traitSetOf(ChalkConvention.LOCAL).replace(collation).simplify();
    return RelOptRule.convert(input, traits);
  }
}
