package chalk.planner.plan.rules;

import chalk.planner.plan.rel.ChalkPartitionedScan;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.RelOptRule;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelRule;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Sort;
import org.apache.calcite.rel.core.Union;
import org.apache.calcite.rel.logical.LogicalSort;
import org.apache.calcite.rel.metadata.RelMdUtil;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.rules.CoreRules;
import org.apache.calcite.rex.RexDynamicParam;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexUtil;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * D276 — a limit a set operation stands in the way of is <b>copied</b> into every branch
 * ({@code docs/design/46-row-goals.md} §3).
 *
 * <pre>
 *   Sort(collation c, offset o, fetch n)          Sort(c, o, n)
 *     Union ALL                             →       Union ALL
 *       A                                             Sort(c, 0, o + n)  A
 *       B                                             Sort(c, 0, o + n)  B
 * </pre>
 *
 * <p>The original stays as the global bound; each branch must contribute its first {@code o + n}
 * candidates and the global sort applies the offset once, so the answer is unchanged and the global
 * node now picks from at most {@code branches × (o + n)} rows rather than from everything. What
 * happens to a copy needs no further rule: a branch that is one remote source's subtree pushes it as
 * a {@code SourceSort} ({@code ORDER BY … LIMIT o + n} in the generated SQL), and a local branch
 * becomes a {@code ChalkTopN}, or a {@code ChalkLimit} over an ordered lookup whose leaf then
 * receives a row goal ({@link ChalkRowGoalRule}).
 *
 * <p>Two rules, because Chalk has two spellings of {@code UNION ALL}:
 *
 * <ul>
 *   <li>{@link #UNION} is Calcite's own {@code SORT_UNION_TRANSPOSE} under one extra operand
 *       predicate ({@link #copyable}), which already <em>is</em> §3: it fires only for {@code ALL}
 *       and a deterministic fetch, drops the offset and pushes {@code offset + fetch} (through
 *       {@code RexUtil.makeOffsetFetchSum}, a literal when both bounds are), and declines a branch
 *       that already satisfies the collation and the bound. A distinct {@code UNION}, an
 *       {@code INTERSECT} and a {@code MINUS} reshape their inputs and are left alone, which its
 *       {@code union.all} guard is.
 *   <li>{@link #PARTITIONED_SCAN} is the same shape over {@link ChalkPartitionedScan}, which means
 *       {@code UNION ALL} but is not a Calcite {@code Union} (D106) and so needs a rule of its own.
 * </ul>
 *
 * <p>Both run in the Hep pass after decorrelation, beside the partition pruning and projection
 * rules, so the branches are already pruned and projected by the time a limit is copied into them.
 *
 * <p><b>A parameterised fetch is copied; one with an offset beside it is not</b> (D288, F122). A
 * copy's bound is {@code offset + fetch}, and with no offset that is the fetch itself — the same
 * {@code RexNode}, which each branch then carries away as its own {@code LIMIT ?} and renders at
 * execution. With an offset it is a <em>sum</em>, and a sum is neither a literal nor a parameter:
 * nothing downstream can read it, and until this run Calcite's own "deterministic fetch" guard let
 * one through and the plan failed to convert several nodes later. Both rules state the shape they
 * accept, so the refusal is where the copy is decided rather than where it is consumed.
 */
public final class LimitCopyRules {
  private LimitCopyRules() {}

  /** {@code Sort(Union ALL)} → the same sort over branches bounded at {@code o + n}. */
  public static final RelOptRule UNION =
      CoreRules.SORT_UNION_TRANSPOSE
          .config
          .withOperandSupplier(
              b0 ->
                  b0.operand(Sort.class)
                      .predicate(sort -> copyable(sort.fetch, sort.offset))
                      .oneInput(b1 -> b1.operand(Union.class).anyInputs()))
          .withDescription("SortUnionTransposeRule(bounded)")
          .toRule();

  /**
   * Whether a bound is one a copy can carry: a literal (with a literal offset, or none), or a
   * parameter with <b>no</b> offset at all.
   *
   * <p>The copy's bound is {@code offset + fetch}. For two literals that sum is a literal and every
   * consumer reads it; for a parameter alone it is the parameter itself, which each branch renders
   * at execution (D288); for a parameter with an offset it is an expression, and an expression is
   * not a bound this planner has any way to read (F122).
   */
  static boolean copyable(@Nullable RexNode fetch, @Nullable RexNode offset) {
    if (fetch instanceof RexLiteral) {
      return offset == null || offset instanceof RexLiteral;
    }
    return fetch instanceof RexDynamicParam && offset == null;
  }

  /** The same over a partitioned scan, which is a {@code UNION ALL} the rule above cannot see. */
  public static final PartitionedScanLimitCopyRule PARTITIONED_SCAN =
      PartitionedScanLimitCopyRule.create();

  /**
   * {@code Sort(PartitionedScan)} → {@code Sort(PartitionedScan(Sort(c, 0, o + n) …))}.
   *
   * <p>The guards of §3, stated here rather than inherited: a bound {@link #copyable} accepts, and
   * never a branch that already delivers the collation within the bound — which is what makes the
   * pass finite, since the rule then matches its own output and declines every branch of it.
   */
  public static final class PartitionedScanLimitCopyRule extends RelRule<ChalkRuleConfig> {
    private PartitionedScanLimitCopyRule(ChalkRuleConfig config) {
      super(config);
    }

    private static PartitionedScanLimitCopyRule create() {
      return new PartitionedScanLimitCopyRule(
          ChalkRuleConfig.of(
              "PartitionedScanLimitCopyRule",
              b ->
                  b.operand(LogicalSort.class)
                      .oneInput(b1 -> b1.operand(ChalkPartitionedScan.class).anyInputs()),
              PartitionedScanLimitCopyRule::new));
    }

    @Override
    public void onMatch(RelOptRuleCall call) {
      LogicalSort sort = call.rel(0);
      ChalkPartitionedScan scan = call.rel(1);

      if (!copyable(sort.fetch, sort.offset)) {
        return;
      }

      // The copies carry no offset: every branch must contribute its first `o + n` candidates, and
      // the sort above applies the offset once to what they produce between them.
      RexNode bound =
          sort.offset == null
              ? sort.fetch
              : RexUtil.makeOffsetFetchSum(
                  sort.getCluster().getRexBuilder(), sort.offset, sort.fetch);

      RelMetadataQuery mq = call.getMetadataQuery();
      List<RelNode> copies = new ArrayList<>(scan.getInputs().size());
      boolean everyBranchAlreadyBounded = true;
      for (RelNode branch : scan.getInputs()) {
        if (RelMdUtil.checkInputForCollationAndLimit(mq, branch, sort.getCollation(), null, bound)) {
          copies.add(branch);
          continue;
        }

        everyBranchAlreadyBounded = false;
        copies.add(LogicalSort.create(branch, sort.getCollation(), null, bound));
      }

      if (everyBranchAlreadyBounded) {
        return;
      }

      call.transformTo(
          sort.copy(sort.getTraitSet(), List.of(scan.copy(scan.getTraitSet(), copies))));
    }
  }
}
