package chalk.planner.plan;

import chalk.planner.entitlement.BoundContext;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The post-conversion trees the sidecar keeps so that a <em>narrowing</em> can skip the front half
 * (docs/design/16-entitlements.md §2.1, D233).
 *
 * <p>What is retained is one {@link PlannerPipeline} and the {@link PlannerPipeline.Front} it
 * produced — the validated statement and the converted tree, before the entitlement pass. The
 * pipeline itself has to be kept and not only the tree: Calcite interns a row type per type factory
 * and every request builds its own, so a tree from one request is not something another request's
 * cluster can be handed. A narrowing therefore re-enters the pipeline that made the tree.
 *
 * <p><b>What this can and cannot change.</b> Nothing. The request carries the whole union of the
 * bindings either way, and the pass is a function of the tree, the context and the options; a hit
 * runs the same pass over the same tree, and a miss converts the same statement again and then runs
 * it. That is why {@code narrow_from} is a hint and why declining it is always safe.
 *
 * <p>Bounded by a count, evicted least-recently-used, and cleared whenever a catalog is registered —
 * the fingerprint already carries the context id and the epoch, so a stale entry could never match,
 * and clearing is what keeps it from lingering. An entry is <em>leased</em> while a request is
 * inside it, and a second request for the same base plans from SQL rather than waiting: one pipeline
 * is one Calcite planner and two requests may not share it.
 */
public final class NarrowCache {
  /** How many trees a sidecar keeps when the host says nothing (D233). */
  public static final int DEFAULT_MAX_RETAINED = 256;

  /**
   * What a deployment sets the count with: the system property
   * {@code -Dchalk.planner.narrowRetained=N}, or the environment variable
   * {@code CHALK_PLANNER_NARROW_RETAINED}, in that order. Zero turns retention off entirely.
   *
   * <p>Read here rather than carried on {@link chalk.planner.PlannerConfig}: it is a number a
   * deployment sets once and nothing between the command line and this class would do anything with
   * it but pass it along.
   */
  public static final String MAX_RETAINED_PROPERTY = "chalk.planner.narrowRetained";

  /** The environment variable's name, for a deployment that configures by environment. */
  public static final String MAX_RETAINED_ENV = "CHALK_PLANNER_NARROW_RETAINED";

  private final int capacity;
  private final Map<Long, Entry> byDigest = new LinkedHashMap<>(16, 0.75f, true);

  public NarrowCache() {
    this(configured());
  }

  public NarrowCache(int capacity) {
    this.capacity = Math.max(0, capacity);
  }

  private static int configured() {
    String declared = System.getProperty(MAX_RETAINED_PROPERTY);
    if (declared == null || declared.isBlank()) {
      declared = System.getenv(MAX_RETAINED_ENV);
    }
    if (declared == null || declared.isBlank()) {
      return DEFAULT_MAX_RETAINED;
    }
    try {
      return Math.max(0, Integer.parseInt(declared.trim()));
    } catch (NumberFormatException ignored) {
      return DEFAULT_MAX_RETAINED;
    }
  }

  /** One retained front half, and every plan digest that has been served from it. */
  public static final class Entry {
    private final PlannerPipeline pipeline;
    private final PlannerPipeline.Front front;
    private final String fingerprint;
    private final BoundContext context;
    private final Set<Long> digests = new LinkedHashSet<>();
    private boolean leased;

    private Entry(
        PlannerPipeline pipeline,
        PlannerPipeline.Front front,
        String fingerprint,
        BoundContext context) {
      this.pipeline = pipeline;
      this.front = front;
      this.fingerprint = fingerprint;
      this.context = context;
    }

    public PlannerPipeline pipeline() {
      return pipeline;
    }

    public PlannerPipeline.Front front() {
      return front;
    }
  }

  /**
   * The entry this request may start from, marked as in use, or null to plan from SQL.
   *
   * <p>Null for every reason there is: nothing retained under that digest, a request planned under
   * other options, another request already inside the pipeline, or a union the retained per-request
   * {@code ctx} schema cannot answer for.
   */
  public synchronized @Nullable Entry lease(long digest, String fingerprint, BoundContext union) {
    if (capacity == 0 || digest == 0) {
      return null;
    }
    Entry entry = byDigest.get(digest);
    if (entry == null || entry.leased || !entry.fingerprint.equals(fingerprint)) {
      return null;
    }
    if (!schemaAnswersFor(entry.context, union)) {
      return null;
    }
    entry.leased = true;
    return entry;
  }

  /**
   * Whether the retained pipeline's {@code ctx} schema is the one the union would have been given.
   *
   * <p>The schema is built once, from the base's bindings, and a narrowing cannot rebuild it. Every
   * relation the union still leaves unfolded must therefore already be in it, with the same row type
   * and the same row count — the count because a {@code ContextTable}'s statistic is its binding's
   * own and a different one is a different cost. A name that <em>folded</em> is not in the union's
   * unfolded set at all and nothing resolves against it, so the base's spare table is harmless; a
   * name that gained rows above the fold ceiling is the one case this declines, and it then plans
   * from SQL.
   */
  private static boolean schemaAnswersFor(BoundContext base, BoundContext union) {
    for (BoundContext.Relation relation : union.unfolded()) {
      BoundContext.Relation retained = base.relation(relation.name());
      if (retained == null
          || !retained.rowType().equals(relation.rowType())
          || retained.rows().size() != relation.rows().size()) {
        return false;
      }
    }
    return true;
  }

  /**
   * Gives a leased entry back, reachable from the plan it has just produced as well (D233), so that
   * a second narrowing of the same statement starts where the first one left off.
   */
  public synchronized void release(Entry entry, long producedDigest) {
    entry.leased = false;
    if (capacity > 0 && producedDigest != 0) {
      entry.digests.add(producedDigest);
      byDigest.put(producedDigest, entry);
    }
    evict();
  }

  /**
   * Keeps this request's pipeline for a narrowing of its plan, or closes it.
   *
   * @return true when the pipeline is now the cache's to close, false when the caller must close it
   */
  public synchronized boolean retain(
      long digest,
      PlannerPipeline pipeline,
      PlannerPipeline.Front front,
      String fingerprint,
      BoundContext context) {
    if (capacity == 0 || digest == 0 || byDigest.containsKey(digest)) {
      return false;
    }
    Entry entry = new Entry(pipeline, front, fingerprint, context);
    entry.digests.add(digest);
    byDigest.put(digest, entry);
    evict();
    return true;
  }

  /** Everything this cache holds, closed. Called whenever a catalog is registered. */
  public synchronized void clear() {
    List<Entry> entries = new ArrayList<>(new LinkedHashSet<>(byDigest.values()));
    byDigest.clear();
    for (Entry entry : entries) {
      if (!entry.leased) {
        entry.pipeline.close();
      }
    }
  }

  /** How many plans are reachable from what is retained. For tests and for the log line. */
  public synchronized int size() {
    return byDigest.size();
  }

  private void evict() {
    while (byDigest.size() > capacity) {
      Entry eldest = null;
      Long at = null;
      for (Map.Entry<Long, Entry> candidate : byDigest.entrySet()) {
        if (!candidate.getValue().leased) {
          at = candidate.getKey();
          eldest = candidate.getValue();
          break;
        }
      }
      if (eldest == null) {
        return;
      }
      byDigest.keySet().removeAll(eldest.digests);
      byDigest.remove(at);
      eldest.pipeline.close();
    }
  }
}
