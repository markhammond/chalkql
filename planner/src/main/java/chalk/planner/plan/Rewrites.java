package chalk.planner.plan;

import com.google.common.collect.ImmutableList;
import java.util.List;

/**
 * The named passes the pipeline runs <em>beside</em> its rule lists, and their versions
 * (F76, D269 (d), docs/design/03-planner.md §4.1).
 *
 * <p>A rule list is a fingerprint the planner can take by asking the rules their names. A rewrite
 * written as ordinary code is not: it changes plans and has nothing to hash. So each one is named
 * here with a version its author bumps, and {@link PlannerConfig#configHash} reads the list — which
 * is what makes "two sidecars with equal hashes plan alike" a claim about the whole pipeline rather
 * than about the rules alone.
 *
 * <p>What belongs here is a pass that rewrites the tree for <em>every</em> request of this process.
 * A pass driven by the catalog or by a request — the entitlement pass, the SQL-body inliner, the
 * pushdown gate's policy — is covered by the catalog epoch or by the request's own fields, and a
 * pass Calcite owns is covered by {@code calcite=}.
 */
public final class Rewrites {
  private Rewrites() {}

  /**
   * Each pass as {@code name:version}, in a fixed order.
   *
   * <p>{@code literal-aggregates} is the one this run has: {@code LITERAL_AGG} written as the
   * projected literal it means, after every rule phase and after the trim (F71, ADR 0054). Version
   * 1 is that rewrite as ADR 0054 built it; bump it when what the pass produces changes.
   */
  private static final List<String> PASSES = ImmutableList.of("literal-aggregates:1");

  /** The passes, for {@link PlannerConfig#configHash}. */
  public static List<String> names() {
    return PASSES;
  }

  /** The same as one line, for a summary a human reads. */
  public static String summary() {
    return String.join(",", PASSES);
  }
}
