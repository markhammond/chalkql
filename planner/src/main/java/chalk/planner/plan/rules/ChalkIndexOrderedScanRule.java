package chalk.planner.plan.rules;

import chalk.ir.v1.Index;
import chalk.planner.plan.ChalkSelectivity;
import chalk.planner.plan.IndexMatcher;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.rel.ChalkIndexLookup;
import chalk.planner.plan.rel.ChalkTableScan;
import com.google.common.collect.ImmutableList;
import java.util.List;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelRule;

/**
 * D50 — the index-ordered scan. An {@link ChalkIndexLookup} whose single range is open on both sides
 * is not a lookup at all: it is a scan of the whole table <em>in the index's key order</em>. This
 * rule offers one beside every {@link ChalkTableScan}, for each ordered index the scan projects the
 * leading key column of ({@code 13-window-functions.md} §3).
 *
 * <p>Nothing here reads a required ordering, and nothing needs to. The lookup claims the index's key
 * order as a trait, so a parent that wants that order finds it; a parent that wants nothing gets the
 * plain scan, because {@code rows × lookup_row_cost + seek} is four times {@code rows ×
 * scan_row_cost} and the scan wins on cost every time the ordering is not needed. Against a
 * {@link chalk.planner.plan.rel.ChalkSort} at {@code rows × log2(rows) × keys} the lookup wins on
 * every table above a few hundred rows — which is the difference between "a moving average per
 * symbol sorts 100 800 rows" and "it streams".
 *
 * <p>The same alternative is what serves a {@code GROUP BY symbol ORDER BY ts}-shaped window on
 * {@code bars}, whose declared collation is {@code (ts, symbol)} but whose unique index is
 * {@code (symbol, ts)}.
 */
public final class ChalkIndexOrderedScanRule extends RelRule<ChalkRuleConfig> {
  private final PushdownPolicy policy;

  public ChalkIndexOrderedScanRule(PushdownPolicy policy) {
    this(config(policy), policy);
  }

  private ChalkIndexOrderedScanRule(ChalkRuleConfig config, PushdownPolicy policy) {
    super(config);
    this.policy = policy;
  }

  private static ChalkRuleConfig config(PushdownPolicy policy) {
    return ChalkRuleConfig.of(
        "ChalkIndexOrderedScanRule",
        b -> b.operand(ChalkTableScan.class).noInputs(),
        config -> new ChalkIndexOrderedScanRule(config, policy));
  }

  @Override
  public boolean matches(RelOptRuleCall call) {
    return policy.allowsIndexLookup();
  }

  @Override
  public void onMatch(RelOptRuleCall call) {
    ChalkTableScan scan = call.rel(0);

    // D276: a goaled scan is one limit's own alternative, and a lookup made from it would carry a
    // goal computed for a scan into a set of its own. ChalkRowGoalRule offers the goaled ordered
    // lookup directly, over the plain one this rule produced, so declining costs nothing.
    if (scan.rowGoal() > 0) {
      return;
    }

    List<Integer> projection = scan.projection();

    for (Index index : scan.chalkTable().indexes()) {
      // D257: the clustered kind is ordered, so it matches this rule exactly as the ordered kind
      // does; what differs is what the lookup then costs.
      if (!ChalkIndexLookup.isOrdered(index.getKind()) || index.getColumnsCount() == 0) {
        continue;
      }

      // A lookup that does not emit the index's leading key column claims no ordering at all
      // (ChalkIndexLookup.collations truncates at the first key column the row does not carry), so
      // it would be a strictly more expensive scan. Offer nothing.
      if (projection.indexOf(index.getColumns(0)) < 0) {
        continue;
      }

      IndexMatcher.Range whole =
          new IndexMatcher.Range(ImmutableList.of(), true, ImmutableList.of(), true);
      call.transformTo(
          ChalkIndexLookup.create(scan, index, ImmutableList.of(whole), ChalkSelectivity.ALL));
    }
  }
}
