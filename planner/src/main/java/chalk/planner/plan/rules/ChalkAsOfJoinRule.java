package chalk.planner.plan.rules;

import chalk.planner.UnsupportedFeatureException;
import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.rel.ChalkAsOfJoin;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.convert.ConverterRule;
import org.apache.calcite.rel.core.JoinInfo;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.logical.LogicalAsofJoin;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.SqlKind;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code LogicalAsofJoin} → {@code ChalkAsOfJoin} (D41, D43).
 *
 * <p>Calcite's validator already refuses most of what Chalk cannot run, but a rule that quietly
 * declined would leave the query failing later with "not enough rules to produce a node", which
 * names nothing. So every reason to decline is re-checked here and reported as an
 * {@code UNSUPPORTED} that says which one it was.
 */
public final class ChalkAsOfJoinRule extends ConverterRule {
  public static final ChalkAsOfJoinRule INSTANCE =
      Config.INSTANCE
          .withConversion(
              LogicalAsofJoin.class, Convention.NONE, ChalkConvention.LOCAL, "ChalkAsOfJoinRule")
          .withRuleFactory(ChalkAsOfJoinRule::new)
          .toRule(ChalkAsOfJoinRule.class);

  private ChalkAsOfJoinRule(Config config) {
    super(config);
  }

  @Override
  public @Nullable RelNode convert(RelNode rel) {
    LogicalAsofJoin join = (LogicalAsofJoin) rel;
    check(join);
    return ChalkAsOfJoin.create(
        ChalkInputs.unordered(join.getLeft()),
        ChalkInputs.unordered(join.getRight()),
        join.getCondition(),
        join.getMatchCondition(),
        join.getVariablesSet(),
        join.getJoinType());
  }

  /** Everything an ASOF join must be for Chalk to run it, each with its own message. */
  public static void check(LogicalAsofJoin join) {
    if (join.getJoinType() != JoinRelType.ASOF && join.getJoinType() != JoinRelType.LEFT_ASOF) {
      throw new UnsupportedFeatureException(
          "ASOF JOIN type " + join.getJoinType(), "Only ASOF and LEFT ASOF exist.");
    }

    // V50 (ADR 0024): analyzeCondition reads `a IS NOT DISTINCT FROM b` back as a plain equi-key,
    // and an ASOF join's keys are plain. An ordinary join falls back to a nested loop that carries
    // the condition whole; an ASOF join has no such fallback, so this is a refusal rather than a
    // slower plan.
    if (ChalkInputs.hasNullSafeEquality(join)) {
      throw new UnsupportedFeatureException(
          "ASOF JOIN ON " + join.getCondition(),
          "A null-safe equality (IS NOT DISTINCT FROM) is not an ASOF join key; ASOF matches on "
              + "plain equalities, which never match NULL to NULL.");
    }

    JoinInfo info = join.analyzeCondition();
    if (!info.isEqui()) {
      throw new UnsupportedFeatureException(
          "ASOF JOIN ON " + join.getCondition(),
          "The ON clause of an ASOF JOIN must be equalities between the two inputs; this one has a "
              + "remainder.");
    }

    RexNode match = join.getMatchCondition();
    if (!(match instanceof RexCall call) || !isComparison(call.getKind())) {
      throw new UnsupportedFeatureException(
          "ASOF JOIN MATCH_CONDITION " + match, "It must be one comparison of two columns.");
    }

    int leftFields = join.getLeft().getRowType().getFieldCount();
    RexNode first = call.getOperands().get(0);
    RexNode second = call.getOperands().get(1);
    if (!(first instanceof RexInputRef left) || !(second instanceof RexInputRef right)) {
      throw new UnsupportedFeatureException(
          "ASOF JOIN MATCH_CONDITION " + match, "It compares two columns, not expressions.");
    }

    boolean oneEachSide =
        (left.getIndex() < leftFields) != (right.getIndex() < leftFields);
    if (!oneEachSide) {
      throw new UnsupportedFeatureException(
          "ASOF JOIN MATCH_CONDITION " + match, "It names one column of each input.");
    }
  }

  private static boolean isComparison(SqlKind kind) {
    return kind == SqlKind.LESS_THAN
        || kind == SqlKind.LESS_THAN_OR_EQUAL
        || kind == SqlKind.GREATER_THAN
        || kind == SqlKind.GREATER_THAN_OR_EQUAL;
  }
}
