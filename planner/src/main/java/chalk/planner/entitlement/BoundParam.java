package chalk.planner.entitlement;

import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexDynamicParam;

/**
 * A context scalar the host binds at <em>execution</em>, standing in the plan as a dynamic parameter
 * that carries its own name (docs/design/16-entitlements.md §2, D209).
 *
 * <p>Under prepare-time binding a scalar is folded into a literal and this class never exists. Under
 * execute-time binding there is no value to fold, so the pass plans the parameter instead and the IR
 * records the name on it as {@code DynamicParam.bound_key}; the executor binds it from the request's
 * context rather than from the statement's own parameters.
 *
 * <p>A subclass rather than a marked index, for two reasons. It is what tells {@code RelToIr} to
 * write a name instead of an index, without threading a per-request map through the conversion; and
 * it is what lets the pushdown gate refuse to send one to a source — {@code RemoteQuery.parameters}
 * are ordered by index and the generated SQL's placeholders by position, and a parameter that is
 * neither the caller's nor positioned among them cannot be lined up against the other.
 *
 * <p>The index is above every index a statement can have, so a bound parameter and the statement's
 * own {@code ?7} are never the same node to anything that compares by index or by digest; the digest
 * is the name the host bound, so plan text and the explain oracle read as the policy wrote them.
 */
public final class BoundParam extends RexDynamicParam {
  /**
   * Where bound parameters start. A statement's own parameters are numbered from zero by the parser,
   * so anything from here up is one of these and nothing else.
   */
  public static final int BASE = 1_000_000;

  private final String key;

  BoundParam(RelDataType type, int slot, String key) {
    super(type, BASE + slot);
    this.key = key;
    this.digest = "@ctx." + key;
  }

  /** The name the host bound the value under — {@code DynamicParam.bound_key}. */
  public String key() {
    return key;
  }

  /** Whether this node is one, for a caller that holds a {@code RexDynamicParam}. */
  public static boolean is(Object node) {
    return node instanceof BoundParam;
  }
}
