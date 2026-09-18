package chalk.planner.plan.rel;

import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.linq4j.Ord;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelFieldCollation;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexCallBinding;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.validate.SqlMonotonicity;
import org.apache.calcite.util.Pair;
import org.apache.calcite.util.mapping.MappingType;
import org.apache.calcite.util.mapping.Mappings;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The trait arithmetic {@code passThroughTraits} and {@code deriveTraits} need, ported from
 * Calcite's package-private {@code EnumerableTraitsUtils} so that Chalk's rels behave exactly as the
 * {@code Enumerable*} ones do ({@code 12-joins.md} §0).
 *
 * <p>Two shapes recur. A <b>projection</b> moves a collation through a field mapping: downwards by
 * rewriting the required collation into input positions, upwards by rewriting the input's collation
 * into output positions and stopping at the first field the projection does not carry. A <b>join</b>
 * that streams its left input in order can offer a required collation to that input and can report
 * the ordering it delivers — but only when the collation names left columns alone and the join type
 * does not null-pad the left side.
 */
public final class ChalkTraits {
  private ChalkTraits() {}

  /**
   * Whether {@code fc} survives the projection — it must be carried by some expression, and a
   * {@code CAST} only preserves an ordering when it is monotonic (a widening numeric cast is; a
   * narrowing or a numeric-to-string one is not).
   */
  private static boolean isCollationOnTrivialExpr(
      List<RexNode> projects,
      RelDataTypeFactory typeFactory,
      Mappings.TargetMapping map,
      RelFieldCollation fc,
      boolean passDown) {
    final int index = fc.getFieldIndex();
    int target = map.getTargetOpt(index);
    if (target < 0) {
      return false;
    }

    final RexNode node = passDown ? projects.get(index) : projects.get(target);
    if (node.isA(SqlKind.CAST)) {
      final RexCall cast = (RexCall) node;
      RelFieldCollation newFieldCollation = RexUtil.apply(map, fc);
      if (newFieldCollation == null) {
        return false;
      }

      final RexCallBinding binding =
          RexCallBinding.create(
              typeFactory, cast, ImmutableList.of(RelCollations.of(newFieldCollation)));
      return cast.getOperator().getMonotonicity(binding) != SqlMonotonicity.NOT_MONOTONIC;
    }

    return true;
  }

  /** A projection's {@code passThroughTraits}: the required collation, restated over the input. */
  public static @Nullable Pair<RelTraitSet, List<RelTraitSet>> passThroughForProject(
      RelTraitSet required,
      List<RexNode> exps,
      RelDataType inputRowType,
      RelDataTypeFactory typeFactory,
      RelTraitSet currentTraits) {
    final RelCollation collation = required.getCollation();
    if (collation == null || collation == RelCollations.EMPTY) {
      return null;
    }

    final Mappings.TargetMapping map = RelOptUtil.permutationIgnoreCast(exps, inputRowType);
    for (RelFieldCollation fc : collation.getFieldCollations()) {
      if (!isCollationOnTrivialExpr(exps, typeFactory, map, fc, true)) {
        return null;
      }
    }

    final RelCollation newCollation = collation.apply(map);
    return Pair.of(
        currentTraits.replace(collation), ImmutableList.of(currentTraits.replace(newCollation)));
  }

  /** A projection's {@code deriveTraits}: the input's collation, restated over the output. */
  public static @Nullable Pair<RelTraitSet, List<RelTraitSet>> deriveForProject(
      RelTraitSet childTraits,
      List<RexNode> exps,
      RelDataType inputRowType,
      RelDataTypeFactory typeFactory,
      RelTraitSet currentTraits) {
    final RelCollation collation = childTraits.getCollation();
    if (collation == null || collation == RelCollations.EMPTY) {
      return null;
    }

    final int maxField = Math.max(exps.size(), inputRowType.getFieldCount());
    Mappings.TargetMapping mapping = Mappings.create(MappingType.FUNCTION, maxField, maxField);
    for (Ord<RexNode> node : Ord.zip(exps)) {
      if (node.e instanceof RexInputRef ref) {
        mapping.set(ref.getIndex(), node.i);
      } else if (node.e.isA(SqlKind.CAST)) {
        final RexNode operand = ((RexCall) node.e).getOperands().get(0);
        if (operand instanceof RexInputRef ref) {
          mapping.set(ref.getIndex(), node.i);
        }
      } else if (node.e instanceof RexCall call) {
        // A call the catalog declared monotone carries its argument's ordering through (D77). Only
        // the increasing directions are claimed: those preserve an ordering whichever way it runs,
        // where a decreasing one would reverse it and `RelCollation.apply` has no way to say so.
        RexNode operand = soleInputRef(call);
        if (operand instanceof RexInputRef ref && isIncreasing(call, typeFactory, collation)) {
          mapping.set(ref.getIndex(), node.i);
        }
      }
    }

    List<RelFieldCollation> derived = new ArrayList<>();
    for (RelFieldCollation fc : collation.getFieldCollations()) {
      if (isCollationOnTrivialExpr(exps, typeFactory, mapping, fc, false)) {
        derived.add(fc);
      } else {
        break;
      }
    }

    if (derived.isEmpty()) {
      return null;
    }

    final RelCollation newCollation = RelCollations.of(derived).apply(mapping);
    return Pair.of(
        currentTraits.replace(newCollation), ImmutableList.of(currentTraits.replace(collation)));
  }

  /** The one input reference among a call's operands, or null when there is none or several. */
  private static @Nullable RexNode soleInputRef(RexCall call) {
    RexNode found = null;
    for (RexNode operand : call.getOperands()) {
      if (operand instanceof RexInputRef) {
        if (found != null) {
          return null;
        }
        found = operand;
      } else if (!(operand instanceof org.apache.calcite.rex.RexLiteral)) {
        return null;
      }
    }
    return found;
  }

  /** Whether {@code call} is increasing in its argument, given the ordering that argument arrives in. */
  private static boolean isIncreasing(
      RexCall call, RelDataTypeFactory typeFactory, RelCollation collation) {
    RexCallBinding binding =
        RexCallBinding.create(typeFactory, call, ImmutableList.of(collation));
    SqlMonotonicity monotonicity = call.getOperator().getMonotonicity(binding);
    return monotonicity == SqlMonotonicity.INCREASING
        || monotonicity == SqlMonotonicity.STRICTLY_INCREASING;
  }

  /**
   * A join that streams its left input in order: offer the required collation to the left input and
   * nothing to the right. Not for FULL or RIGHT, whose left side is null-padded and therefore
   * reordered.
   */
  public static @Nullable Pair<RelTraitSet, List<RelTraitSet>> passThroughForJoin(
      RelTraitSet required, JoinRelType joinType, int leftFieldCount, RelTraitSet joinTraits) {
    RelCollation collation = required.getCollation();
    if (collation == null
        || collation == RelCollations.EMPTY
        || joinType == JoinRelType.FULL
        || joinType == JoinRelType.RIGHT) {
      return null;
    }

    for (RelFieldCollation fc : collation.getFieldCollations()) {
      if (fc.getFieldIndex() >= leftFieldCount) {
        return null;
      }
    }

    RelTraitSet passed = joinTraits.replace(collation);
    return Pair.of(
        passed, ImmutableList.of(passed, passed.replace(RelCollations.EMPTY)));
  }

  /** The same join's {@code deriveTraits}: it delivers whatever ordering its left input has. */
  public static @Nullable Pair<RelTraitSet, List<RelTraitSet>> deriveForJoin(
      RelTraitSet childTraits,
      int childId,
      JoinRelType joinType,
      RelTraitSet joinTraits,
      RelTraitSet rightTraits) {
    if (childId != 0) {
      return null;
    }

    RelCollation collation = childTraits.getCollation();
    if (collation == null
        || collation == RelCollations.EMPTY
        || joinType == JoinRelType.FULL
        || joinType == JoinRelType.RIGHT) {
      return null;
    }

    RelTraitSet derived = joinTraits.replace(collation);
    return Pair.of(derived, ImmutableList.of(derived, rightTraits));
  }
}
