package chalk.planner.plan.rules;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.rel.ChalkMergeJoin;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelFieldCollation;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.convert.ConverterRule;
import org.apache.calcite.rel.core.JoinInfo;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.logical.LogicalJoin;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.util.ImmutableIntList;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code LogicalJoin} → {@code ChalkMergeJoin}, when both inputs already deliver an ordering whose
 * leading fields are the equality keys, in the same order and the same directions (D43).
 *
 * <p><b>Any</b> order of the keys will do, not the one {@code JoinInfo} happened to list: {@code
 * bars b JOIN bars q ON b.symbol = q.symbol AND b.ts = q.ts} has {@code JoinInfo} keys
 * {@code (symbol, ts)} and inputs collated {@code (ts, symbol)}, and refusing the permutation would
 * throw the merge join away for no reason. The left input's collations are searched first because
 * the join's own output order follows them; the right input then has to agree.
 *
 * <p>The rule never asks an input to sort. It fires only when the ordering is already there, so a
 * merge join is an opportunity the plan already contains rather than a reason to add a sort — that
 * is what keeps it from being chosen for every equi-join in the corpus.
 *
 * <p>Not for SEMI or ANTI in this step ({@code 12-joins.md} §8).
 */
public final class ChalkMergeJoinRule extends ConverterRule {
  public static final ChalkMergeJoinRule INSTANCE =
      Config.INSTANCE
          .withConversion(
              LogicalJoin.class, Convention.NONE, ChalkConvention.LOCAL, "ChalkMergeJoinRule")
          .withRuleFactory(ChalkMergeJoinRule::new)
          .toRule(ChalkMergeJoinRule.class);

  private ChalkMergeJoinRule(Config config) {
    super(config);
  }

  @Override
  public @Nullable RelNode convert(RelNode rel) {
    LogicalJoin join = (LogicalJoin) rel;
    JoinRelType type = join.getJoinType();
    if (type != JoinRelType.INNER
        && type != JoinRelType.LEFT
        && type != JoinRelType.RIGHT
        && type != JoinRelType.FULL) {
      return null;
    }

    // V50: the same null-safe equality a hash join refuses; a merge join's keys are plain too.
    if (ChalkInputs.hasNullSafeEquality(join)) {
      return null;
    }

    JoinInfo info = join.analyzeCondition();
    if (info.leftKeys.isEmpty()) {
      return null;
    }

    RelMetadataQuery mq = join.getCluster().getMetadataQuery();
    Match match = match(mq, join.getLeft(), join.getRight(), info);
    if (match == null) {
      return null;
    }

    return ChalkMergeJoin.create(
        ChalkInputs.sorted(join.getLeft(), match.leftCollation()),
        ChalkInputs.sorted(join.getRight(), match.rightCollation()),
        join.getCondition(),
        join.getVariablesSet(),
        type,
        match.leftKeys(),
        match.rightKeys(),
        match.directions());
  }

  /** The key order both inputs already deliver, with the directions they deliver it in. */
  private record Match(
      ImmutableIntList leftKeys,
      ImmutableIntList rightKeys,
      List<RelFieldCollation> directions,
      RelCollation leftCollation,
      RelCollation rightCollation) {}

  private static @Nullable Match match(
      RelMetadataQuery mq, RelNode left, RelNode right, JoinInfo info) {
    List<RelCollation> leftCollations = mq.collations(left);
    List<RelCollation> rightCollations = mq.collations(right);
    if (leftCollations == null || rightCollations == null) {
      return null;
    }

    int keyCount = info.leftKeys.size();
    for (RelCollation candidate : leftCollations) {
      List<RelFieldCollation> prefix = prefixOver(candidate, info.leftKeys);
      if (prefix == null) {
        continue;
      }

      // The same key order, restated over the right input's fields.
      List<Integer> leftKeys = new ArrayList<>(keyCount);
      List<Integer> rightKeys = new ArrayList<>(keyCount);
      for (RelFieldCollation field : prefix) {
        int position = info.leftKeys.indexOf(field.getFieldIndex());
        leftKeys.add(info.leftKeys.get(position));
        rightKeys.add(info.rightKeys.get(position));
      }

      RelCollation rightNeeded = restate(prefix, rightKeys);
      if (delivers(rightCollations, rightNeeded)) {
        return new Match(
            ImmutableIntList.copyOf(leftKeys),
            ImmutableIntList.copyOf(rightKeys),
            ImmutableList.copyOf(prefix),
            RelCollations.of(ImmutableList.copyOf(prefix)),
            rightNeeded);
      }
    }

    return null;
  }

  /**
   * The first {@code keys.size()} fields of {@code collation}, when between them they are exactly
   * the keys — each once, in whatever order. Null otherwise.
   */
  private static @Nullable List<RelFieldCollation> prefixOver(
      RelCollation collation, ImmutableIntList keys) {
    List<RelFieldCollation> fields = collation.getFieldCollations();
    if (fields.size() < keys.size()) {
      return null;
    }

    List<RelFieldCollation> prefix = fields.subList(0, keys.size());
    List<Integer> seen = new ArrayList<>(prefix.size());
    for (RelFieldCollation field : prefix) {
      if (!keys.contains(field.getFieldIndex()) || seen.contains(field.getFieldIndex())) {
        return null;
      }
      seen.add(field.getFieldIndex());
    }

    return prefix;
  }

  /** The same ordering, over the other input's key columns. */
  private static RelCollation restate(List<RelFieldCollation> prefix, List<Integer> keys) {
    List<RelFieldCollation> fields = new ArrayList<>(prefix.size());
    for (int i = 0; i < prefix.size(); i++) {
      RelFieldCollation template = prefix.get(i);
      fields.add(new RelFieldCollation(keys.get(i), template.getDirection(), template.nullDirection));
    }
    return RelCollations.of(fields);
  }

  /** True when one of the collations starts with {@code needed} — Calcite's {@code satisfies}. */
  private static boolean delivers(List<RelCollation> collations, RelCollation needed) {
    for (RelCollation collation : collations) {
      if (collation.satisfies(needed)) {
        return true;
      }
    }

    return false;
  }
}
