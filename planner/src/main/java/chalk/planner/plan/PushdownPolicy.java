package chalk.planner.plan;

import chalk.planner.rpc.v1.DisabledCapability;
import chalk.planner.rpc.v1.PushdownLevel;
import java.util.EnumSet;
import java.util.List;
import java.util.Set;

/**
 * How much work may be handed to a source. Rules never read the request directly (work plan §5);
 * they read this, so M4's capability-driven pushdown is a change to one object rather than to every
 * rule.
 *
 * <p>The levels, made exact (§2, D87):
 *
 * <ul>
 *   <li>{@code FULL} — everything the descriptor allows.
 *   <li>{@code FILTERS_ONLY} — predicates and projection pruning; no aggregates, sorts, limits or
 *       joins.
 *   <li>{@code PROJECTION_ONLY} — pruning alone.
 *   <li>{@code NONE} — a bare full scan, and no {@code IndexLookup} or {@code RemoteQuery} at all.
 *       The I4 oracle's configuration, unchanged since M1.
 * </ul>
 */
public final class PushdownPolicy {
  private final PushdownLevel level;
  private final boolean disableIndexLookup;
  private final Set<DisabledCapability> disabled;

  public PushdownPolicy(PushdownLevel level, boolean disableIndexLookup) {
    this(level, disableIndexLookup, List.of());
  }

  /** The same, with capabilities the request wants planned as if no source had declared them. */
  public PushdownPolicy(
      PushdownLevel level, boolean disableIndexLookup, List<DisabledCapability> disabled) {
    this.level =
        level == PushdownLevel.PUSHDOWN_LEVEL_UNSPECIFIED ? PushdownLevel.PUSHDOWN_LEVEL_FULL : level;
    this.disableIndexLookup = disableIndexLookup;
    Set<DisabledCapability> set = EnumSet.noneOf(DisabledCapability.class);
    for (DisabledCapability capability : disabled) {
      if (capability != DisabledCapability.DISABLED_CAPABILITY_UNSPECIFIED
          && capability != DisabledCapability.UNRECOGNIZED) {
        set.add(capability);
      }
    }
    this.disabled = set;
  }

  public static PushdownPolicy full() {
    return new PushdownPolicy(PushdownLevel.PUSHDOWN_LEVEL_FULL, false);
  }

  /** The reference (I4) configuration: nothing is pushed anywhere. */
  public static PushdownPolicy none() {
    return new PushdownPolicy(PushdownLevel.PUSHDOWN_LEVEL_NONE, true);
  }

  public PushdownLevel level() {
    return level;
  }

  /** The capabilities this request turned off, whatever the descriptors say (D87). */
  public Set<DisabledCapability> disabledCapabilities() {
    return disabled;
  }

  /** The gate a rule set for {@code convention} decides with. */
  public PushdownGate gateFor(SourceConvention convention) {
    return new PushdownGate(
        convention.capabilities(), convention.profile(), disabled, convention.schemaName());
  }

  /**
   * Whether a scan may read fewer than all of the table's columns
   * (docs/design/03-planner.md §4.3). For a LOCAL source that is the whole of what a level changes.
   */
  public boolean allowsProjectionIntoScan() {
    return level == PushdownLevel.PUSHDOWN_LEVEL_FULL
        || level == PushdownLevel.PUSHDOWN_LEVEL_PROJECTION_ONLY;
  }

  /**
   * Whether a predicate may become an index lookup (M2, §4). {@code disable_index_lookup}
   * unregisters the rule for one request; {@code PUSHDOWN_LEVEL_NONE} excludes it too, because a
   * lookup <em>is</em> work pushed into a source and NONE is the reference (I4) configuration —
   * every {@code Read} full, every predicate evaluated by the client.
   */
  public boolean allowsIndexLookup() {
    return !disableIndexLookup && level != PushdownLevel.PUSHDOWN_LEVEL_NONE;
  }

  /**
   * Whether a remote source may be handed a query at all. At {@code NONE} it is read through its
   * scan path and nothing else, which is what makes the reference run a genuinely different
   * execution rather than the same plan with a flag.
   */
  public boolean allowsRemotePushdown() {
    return level != PushdownLevel.PUSHDOWN_LEVEL_NONE;
  }

  /** Column pruning into a remote query: every level but {@code NONE}. */
  public boolean allowsProjectionIntoRemote() {
    return allowsRemotePushdown();
  }

  /** Predicates into a remote query: {@code FULL} and {@code FILTERS_ONLY}. */
  public boolean allowsFilterIntoRemote() {
    return level == PushdownLevel.PUSHDOWN_LEVEL_FULL
        || level == PushdownLevel.PUSHDOWN_LEVEL_FILTERS_ONLY;
  }

  /** Aggregates, sorts, limits and joins into a remote query: {@code FULL} only. */
  public boolean allowsFullRemotePushdown() {
    return level == PushdownLevel.PUSHDOWN_LEVEL_FULL;
  }

  @Override
  public String toString() {
    return "pushdown="
        + level
        + ",disableIndexLookup="
        + disableIndexLookup
        + (disabled.isEmpty() ? "" : ",disabled=" + disabled);
  }
}
