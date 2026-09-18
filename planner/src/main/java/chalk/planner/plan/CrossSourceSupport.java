package chalk.planner.plan;

import chalk.planner.UnsupportedFeatureException;
import chalk.planner.plan.rel.ChalkNestedLoopJoin;
import chalk.planner.plan.rel.SourceToLocalConverter;
import java.util.Locale;
import org.apache.calcite.plan.volcano.RelSubset;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.metadata.RelMetadataQuery;

/**
 * The two things a federated plan may not be, refused on the physical tree (D104, D105).
 *
 * <p>Both checks have to run <em>after</em> optimisation, because both are about which alternative
 * cost chose. A rule cannot know: at the time it fires, its inputs are {@code RelSubset}s and
 * whether the right side will end up as a remote query or a local scan has not been decided.
 *
 * <ol>
 *   <li><b>No {@code RemoteQuery} on the inner side of a {@code NestedLoopJoin}.</b> The M4
 *       assertion, carried into M5 (§3). A nested loop's inner side is the one a streaming
 *       implementation would re-read per outer row, and a remote query there is a plan whose cost is
 *       a lie however this executor happens to buffer it today. The planner rewrites that shape to a
 *       {@link chalk.planner.plan.rel.ChalkLookupJoin} where it can — an equality gives
 *       {@code CrossSourceJoinRule} something to bind — and refuses where it cannot. The inner side
 *       is read as {@code I-IR-20} reads it, the whole subtree and not the boundary chain above it
 *       (F55): a fetch under a join on that side is re-read per outer row like any other, and
 *       reading it the shallow way left the client to refuse what this was written to refuse.
 *   <li><b>The local-join guardrail.</b> {@code CrossSourceJoinPolicy.local_join_max_rows} caps what
 *       a plan may fetch across a boundary into a local join. Zero is unlimited, which is the
 *       default; a host that sets it is saying "I would rather be told than wait".
 * </ol>
 */
public final class CrossSourceSupport {
  private CrossSourceSupport() {}

  /** Refuses a physical plan that breaks either rule, naming the join and the estimate. */
  public static void check(RelNode physical, JoinPolicy policy) {
    RelMetadataQuery mq = physical.getCluster().getMetadataQuery();
    walk(physical, policy, mq);
  }

  private static void walk(RelNode rel, JoinPolicy policy, RelMetadataQuery mq) {
    RelNode node = resolve(rel);
    if (node instanceof ChalkNestedLoopJoin nested
        && chalk.planner.plan.rel.SourceCosts.fetchesRemotely(nested.getRight())) {
      throw new UnsupportedFeatureException(
          "a remote query on the inner side of a nested-loop join",
          "The plan puts source '"
              + sourceOf(nested.getRight())
              + "' under a nested-loop join's inner side, which would fetch it once per outer row. "
              + "Give the join an equality between the two sides so it can be planned as a lookup "
              + "join, or narrow the sides so a hash join over two fetches is affordable.");
    }
    if (node instanceof Join join
        && !(node instanceof chalk.planner.plan.rel.ChalkLookupJoin)
        && !(node instanceof chalk.planner.plan.rel.ChalkAdaptiveJoin)) {
      checkGuardrail(join, policy, mq);
    }
    for (RelNode input : node.getInputs()) {
      walk(input, policy, mq);
    }
  }

  /**
   * A local join over one or two remote boundaries fetches both sides in full. When the policy caps
   * that, the sum of what would be fetched is what it is capped against — not the join's output,
   * because a join that returns nothing can still have pulled sixty thousand rows to find out.
   */
  private static void checkGuardrail(Join join, JoinPolicy policy, RelMetadataQuery mq) {
    long ceiling = policy.localJoinMaxRows();
    if (ceiling <= 0) {
      return;
    }

    double fetched = 0;
    for (RelNode input : join.getInputs()) {
      if (chalk.planner.plan.rel.SourceCosts.isRemote(input)) {
        Double rows = mq.getRowCount(resolve(input));
        fetched += rows == null ? policy.unknownRowCount() : rows;
      }
    }
    if (fetched <= ceiling) {
      return;
    }

    throw new UnsupportedFeatureException(
        "a local cross-source join over " + Math.round(fetched) + " fetched rows",
        String.format(
            Locale.ROOT,
            "The plan would fetch about %d rows into a local %s to join %s, and the join policy's "
                + "local_join_max_rows is %d. Raise it, narrow the query, or allow a lookup or "
                + "broadcast strategy for this pair of sources.",
            Math.round(fetched),
            join.getClass().getSimpleName(),
            describe(join),
            ceiling));
  }

  private static String describe(Join join) {
    String left = sourceOf(join.getLeft());
    String right = sourceOf(join.getRight());
    return "'" + left + "' to '" + right + "'";
  }

  private static String sourceOf(RelNode rel) {
    RelNode node = resolve(rel);
    if (node instanceof SourceToLocalConverter boundary) {
      return boundary.sourceId();
    }
    if (node instanceof org.apache.calcite.rel.SingleRel single) {
      return sourceOf(single.getInput());
    }
    if (node instanceof org.apache.calcite.rel.core.TableScan scan) {
      chalk.planner.catalog.ChalkTable table =
          scan.getTable().unwrap(chalk.planner.catalog.ChalkTable.class);
      return table == null ? "?" : table.sourceId();
    }
    for (RelNode input : node.getInputs()) {
      String found = sourceOf(input);
      if (!"?".equals(found)) {
        return found;
      }
    }
    return "?";
  }

  private static RelNode resolve(RelNode rel) {
    if (rel instanceof RelSubset subset) {
      RelNode best = subset.getBest();
      return best != null ? best : subset.getOriginal();
    }
    return rel;
  }
}
