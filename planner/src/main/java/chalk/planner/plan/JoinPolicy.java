package chalk.planner.plan;

import chalk.ir.v1.CrossSourceJoinPolicy;
import chalk.ir.v1.JoinStrategy;
import chalk.ir.v1.SourcePairRule;
import com.google.common.collect.ImmutableSet;
import java.util.EnumSet;
import java.util.Set;

/**
 * The cross-source join policy the planner reads (D104,
 * {@code docs/design/20-m5-federation.md} §2).
 *
 * <p>Rev 3's {@code ICrossSourceJoinPolicy} survives as the object a host implements; what it
 * produces is a descriptor, and this is the planner's view of one. Two descriptors go in — the
 * catalog's, built at engine creation and on every refresh, and the request's — and the request wins
 * field by field, because a per-statement override that could only replace the whole policy would
 * make "just forbid lookups for this one query" mean restating everything.
 *
 * <p>Every limit has a planner default, and <b>zero means inherit</b>, exactly as
 * {@code CostProfile} does. That is what lets a host set one field of one pair rule.
 */
public final class JoinPolicy {

  /** The most rows a broadcast may ship into a source's query. */
  public static final long DEFAULT_BROADCAST_MAX_ROWS = 10_000L;

  /** How many IN-list calls an adaptive join's lookup branch may take. */
  public static final int DEFAULT_LOOKUP_MAX_CALLS = 64;

  /** What an unknown remote cardinality is taken to be (D99). */
  public static final long DEFAULT_UNKNOWN_ROW_COUNT = 1_000_000L;

  /** The shipped default: adaptive everywhere, every limit at its default, no pair rules. */
  public static final JoinPolicy DEFAULT =
      new JoinPolicy(CrossSourceJoinPolicy.getDefaultInstance());

  private final CrossSourceJoinPolicy descriptor;

  private JoinPolicy(CrossSourceJoinPolicy descriptor) {
    this.descriptor = descriptor;
  }

  /** The catalog's policy with the request's merged over it, field by field. */
  public static JoinPolicy of(CrossSourceJoinPolicy catalog, CrossSourceJoinPolicy request) {
    return new JoinPolicy(merge(catalog, request));
  }

  /** Just the catalog's. */
  public static JoinPolicy of(CrossSourceJoinPolicy catalog) {
    return new JoinPolicy(catalog);
  }

  /**
   * A field the request sets wins; a field it leaves at its zero inherits the catalog's, and the
   * planner's default stands behind both. The request's pair rules come <em>first</em>, so a
   * request rule shadows a catalog rule for the same pair rather than being appended after it.
   */
  private static CrossSourceJoinPolicy merge(
      CrossSourceJoinPolicy catalog, CrossSourceJoinPolicy request) {
    if (request.equals(CrossSourceJoinPolicy.getDefaultInstance())) {
      return catalog;
    }

    CrossSourceJoinPolicy.Builder merged = CrossSourceJoinPolicy.newBuilder(catalog);
    if (request.getDefaultStrategy() != JoinStrategy.JOIN_STRATEGY_UNSPECIFIED) {
      merged.setDefaultStrategy(request.getDefaultStrategy());
    }
    if (request.getBroadcastMaxRows() != 0) {
      merged.setBroadcastMaxRows(request.getBroadcastMaxRows());
    }
    if (request.getLookupMaxCalls() != 0) {
      merged.setLookupMaxCalls(request.getLookupMaxCalls());
    }
    if (request.getLocalJoinMaxRows() != 0) {
      merged.setLocalJoinMaxRows(request.getLocalJoinMaxRows());
    }
    if (request.getUnknownRowCountAssumption() != 0) {
      merged.setUnknownRowCountAssumption(request.getUnknownRowCountAssumption());
    }
    if (request.getPairsCount() > 0) {
      merged.clearPairs();
      merged.addAllPairs(request.getPairsList());
      merged.addAllPairs(catalog.getPairsList());
    }
    return merged.build();
  }

  /** The descriptor as it will be planned with. */
  public CrossSourceJoinPolicy descriptor() {
    return descriptor;
  }

  /** How many IN-list calls an adaptive join's lookup branch may take. */
  public int lookupMaxCalls() {
    int declared = descriptor.getLookupMaxCalls();
    return declared > 0 ? declared : DEFAULT_LOOKUP_MAX_CALLS;
  }

  /** The local-join guardrail: 0 is unlimited. */
  public long localJoinMaxRows() {
    return Math.max(0L, descriptor.getLocalJoinMaxRows());
  }

  /** What an unknown remote cardinality is taken to be (D99). */
  public long unknownRowCount() {
    long declared = descriptor.getUnknownRowCountAssumption();
    return declared > 0 ? declared : DEFAULT_UNKNOWN_ROW_COUNT;
  }

  /** The most rows a broadcast may ship into {@code right}'s query, for this ordered pair. */
  public long broadcastMaxRows(String left, String right) {
    SourcePairRule rule = ruleFor(left, right);
    if (rule != null && rule.getBroadcastMaxRows() > 0) {
      return rule.getBroadcastMaxRows();
    }
    return descriptor.getBroadcastMaxRows() > 0
        ? descriptor.getBroadcastMaxRows()
        : DEFAULT_BROADCAST_MAX_ROWS;
  }

  /**
   * The strategies this ordered pair may use. A pair rule with an empty {@code allowed} allows every
   * one, which is how a rule that only sets {@code preferred} or {@code broadcast_max_rows} behaves.
   */
  public Set<JoinStrategy> allowed(String left, String right) {
    SourcePairRule rule = ruleFor(left, right);
    if (rule == null || rule.getAllowedCount() == 0) {
      return ALL;
    }

    EnumSet<JoinStrategy> allowed = EnumSet.noneOf(JoinStrategy.class);
    for (JoinStrategy strategy : rule.getAllowedList()) {
      if (strategy != JoinStrategy.JOIN_STRATEGY_UNSPECIFIED) {
        allowed.add(strategy);
      }
    }

    // LOCAL is the fallback that always exists (§1). A policy that forbids everything would make a
    // legal query unplannable, which is a worse answer than a slow plan.
    allowed.add(JoinStrategy.JOIN_STRATEGY_LOCAL);
    return allowed;
  }

  /**
   * Whether {@code driving}'s keys may be looked up in {@code lookup}: {@code LOOKUP} is among the
   * strategies this ordered pair allows (F139).
   *
   * <p>The question the two entitlement exchanges ask — {@code ContextKeySetRule} with the request
   * context as the driving side, which belongs to no source and is named by the empty id, and
   * {@code ParentKeySetRule} with every source the parent's visible keys are computed from. Each is
   * a {@code LOOKUP} by every other measure, and the plan text shows it as one, so a pair rule that
   * forbids looking up into a source binds it as it binds a join the statement wrote. {@code
   * preferred} is not read: it chooses among strategies, and these rules offer only the one.
   */
  public boolean allowsLookup(String driving, String lookup) {
    return allowed(driving, lookup).contains(JoinStrategy.JOIN_STRATEGY_LOOKUP);
  }

  /** The strategy this pair prefers outright, or {@code UNSPECIFIED} when cost decides. */
  public JoinStrategy preferred(String left, String right) {
    SourcePairRule rule = ruleFor(left, right);
    return rule == null ? JoinStrategy.JOIN_STRATEGY_UNSPECIFIED : rule.getPreferred();
  }

  /** What a pair no rule names gets. {@code UNSPECIFIED} is read as {@code ADAPTIVE}. */
  public JoinStrategy defaultStrategy() {
    return descriptor.getDefaultStrategy() == JoinStrategy.JOIN_STRATEGY_UNSPECIFIED
        ? JoinStrategy.JOIN_STRATEGY_ADAPTIVE
        : descriptor.getDefaultStrategy();
  }

  /**
   * The first rule whose pair matches, in order. An empty source id on a side matches any source
   * there, so {@code {left_source: "", right_source: "duck"}} says "nothing may look up into duck".
   */
  private SourcePairRule ruleFor(String left, String right) {
    for (SourcePairRule rule : descriptor.getPairsList()) {
      boolean leftMatches = rule.getLeftSource().isEmpty() || rule.getLeftSource().equals(left);
      boolean rightMatches = rule.getRightSource().isEmpty() || rule.getRightSource().equals(right);
      if (leftMatches && rightMatches) {
        return rule;
      }
    }
    return null;
  }

  private static final Set<JoinStrategy> ALL =
      ImmutableSet.of(
          JoinStrategy.JOIN_STRATEGY_LOCAL,
          JoinStrategy.JOIN_STRATEGY_LOOKUP,
          JoinStrategy.JOIN_STRATEGY_BROADCAST,
          JoinStrategy.JOIN_STRATEGY_ADAPTIVE);

  @Override
  public String toString() {
    return "JoinPolicy{default="
        + defaultStrategy()
        + ",broadcastMaxRows="
        + broadcastMaxRows("", "")
        + ",lookupMaxCalls="
        + lookupMaxCalls()
        + ",localJoinMaxRows="
        + localJoinMaxRows()
        + ",unknownRowCount="
        + unknownRowCount()
        + ",pairs="
        + descriptor.getPairsCount()
        + "}";
  }
}
