package chalk.planner.plan;

import chalk.ir.v1.CatalogContext;
import chalk.planner.catalog.ChalkTable;
import java.util.LinkedHashMap;
import java.util.Map;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.metadata.RelMetadataProvider;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Plans kept to be priced again on newer numbers (design 70 §3): the physical tree of each plan a
 * request asked to keep, by plan digest, with the metadata handlers it was planned under and the
 * statement it is a plan of. The least recently used goes past a bound. Memory policy only: a plan
 * no longer kept is not priced, so no margin holds a new plan back against it, and the caller's
 * check decides without its cost.
 *
 * <p>A kept tree is priced as it is. While it is, every table it reads answers with the numbers of
 * the catalog it is priced on ({@link ChalkTable#pricedOn}), so a plan made on older numbers is
 * priced on newer ones without anything in it being rebuilt.
 */
public final class KeptPlans {
  /** Plans kept at most: one for each standing view with a plan check, and some to spare. */
  public static final int DEFAULT_LIMIT = 256;

  private record Kept(RelNode physical, RelMetadataProvider metadata, long logicalDigest) {}

  private final Map<Long, Kept> plans;

  public KeptPlans() {
    this(DEFAULT_LIMIT);
  }

  public KeptPlans(int limit) {
    this.plans =
        new LinkedHashMap<>(16, 0.75f, true) {
          @Override
          protected boolean removeEldestEntry(Map.Entry<Long, Kept> eldest) {
            return size() > limit;
          }
        };
  }

  /**
   * Keeps {@code physical} as the plan {@code digest} names, of the statement {@code logicalDigest}
   * names (D342), with the handlers it was planned under.
   */
  public synchronized void keep(long digest, long logicalDigest, RelNode physical) {
    plans.put(
        digest, new Kept(physical, physical.getCluster().getMetadataProvider(), logicalDigest));
  }

  /** Whether the plan {@code digest} names is kept. */
  public synchronized boolean holds(long digest) {
    return plans.containsKey(digest);
  }

  /**
   * The cost of the plan {@code digest} names on {@code catalog}'s numbers, or null when it is not
   * kept as a plan of the statement {@code logicalDigest} names: a plan of another statement answers
   * other rows, so its cost says nothing of this one's. Priced under the handlers it was planned
   * under, on this thread, with a metadata query that holds nothing from the numbers it was planned
   * on; one pricing of a tree at a time, since the tree's cluster is shared.
   */
  public @Nullable Double price(long digest, long logicalDigest, CatalogContext catalog) {
    Kept kept;
    synchronized (this) {
      kept = plans.get(digest);
    }

    if (kept == null || kept.logicalDigest() != logicalDigest) {
      return null;
    }

    synchronized (kept) {
      var cluster = kept.physical().getCluster();
      cluster.setMetadataProvider(kept.metadata());
      cluster.invalidateMetadataQuery();
      ChalkTable.PricingScope scope = ChalkTable.pricedOn(catalog);
      try {
        return cost(kept.physical(), cluster.getMetadataQuery());
      } finally {
        scope.close();
        cluster.invalidateMetadataQuery();
      }
    }
  }

  /** The cumulative row work of {@code physical}: what the optimiser compares plans by. */
  public static @Nullable Double cost(RelNode physical, RelMetadataQuery mq) {
    RelOptCost cost = mq.getCumulativeCost(physical);
    return cost == null ? null : cost.getRows();
  }

  /**
   * Whether a plan that costs {@code next} saves less than {@code cheaperBy} of {@code known}, the
   * cost of the plan its caller runs, both on the same numbers: such a plan is held back, and the
   * caller keeps the plan it runs. A cost that is missing or not a number decides nothing, and the
   * plan is sent.
   */
  public static boolean savesTooLittle(
      @Nullable Double next, @Nullable Double known, double cheaperBy) {
    if (next == null || known == null || next.isNaN() || known.isNaN()) {
      return false;
    }

    return next >= known * (1.0 - cheaperBy);
  }
}
