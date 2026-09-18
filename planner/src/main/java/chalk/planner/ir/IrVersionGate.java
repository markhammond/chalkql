package chalk.planner.ir;

import chalk.ir.IrVersion;
import chalk.ir.v1.Rel;
import java.util.EnumMap;
import java.util.Map;

/**
 * When each IR node kind was introduced (docs/design/02-ir.md §2, rule 1). A planner at IR version N
 * serves a client at N−k by declining to use nodes the client cannot read: a rule that would produce
 * one is not registered for that request, and if the query cannot be planned without it the planner
 * fails with {@code PLAN_ERROR_KIND_IR_VERSION} rather than emitting a node the client will reject.
 *
 * <p>Every M1 kind is version 1, so nothing is gated yet. The table exists now so that adding a node
 * in M2 is a one-line change here rather than a design conversation.
 */
public final class IrVersionGate {
  private static final Map<Rel.KindCase, Integer> SINCE_VERSION = sinceVersion();

  private final int clientIrVersion;

  public IrVersionGate(int clientIrVersion) {
    this.clientIrVersion = clientIrVersion;
  }

  /** A gate that permits everything this build knows about. */
  public static IrVersionGate current() {
    return new IrVersionGate(IrVersion.CURRENT);
  }

  public int clientIrVersion() {
    return clientIrVersion;
  }

  /** True when the client can read this node kind. */
  public boolean allows(Rel.KindCase kind) {
    return sinceVersion(kind) <= clientIrVersion;
  }

  /** The IR version that introduced this node kind. */
  public static int sinceVersion(Rel.KindCase kind) {
    return SINCE_VERSION.getOrDefault(kind, IrVersion.CURRENT);
  }

  /** True when this planner can serve a client at that IR version at all. */
  public static boolean isServable(int clientIrVersion) {
    return clientIrVersion >= IrVersion.MINIMUM && clientIrVersion <= IrVersion.CURRENT;
  }

  private static Map<Rel.KindCase, Integer> sinceVersion() {
    Map<Rel.KindCase, Integer> map = new EnumMap<>(Rel.KindCase.class);
    for (Rel.KindCase kind : Rel.KindCase.values()) {
      if (kind != Rel.KindCase.KIND_NOT_SET) {
        map.put(kind, 1);
      }
    }
    return Map.copyOf(map);
  }
}
