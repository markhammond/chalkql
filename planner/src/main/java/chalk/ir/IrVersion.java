package chalk.ir;

/**
 * The plan IR version this build speaks. Twin of {@code Chalk.Ir.IrVersion} on the client
 * (docs/design/02-ir.md §2); the two constants must always agree.
 */
public final class IrVersion {
  /** The version the planner stamps on every {@code Plan.ir_version}. */
  public static final int CURRENT = 1;

  /** Oldest client IR version this planner will serve. */
  public static final int MINIMUM = 1;

  private IrVersion() {}
}
