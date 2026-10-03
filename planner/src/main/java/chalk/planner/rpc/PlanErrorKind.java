package chalk.planner.rpc;

/**
 * What kind of problem a violation is, finer than its gRPC status and coarser than its {@link
 * chalk.planner.ErrorCode code}. A violation carries it by {@link #wireName() name}, as it does its
 * code.
 */
public enum PlanErrorKind {
  /** The statement does not parse. */
  PARSE("Parse"),
  /** The statement parses but names what does not exist, or combines types SQL does not allow. */
  VALIDATION("Validation"),
  /** Valid SQL the planner cannot express in the IR, or cannot physically plan. */
  UNSUPPORTED("Unsupported"),
  /** A context id no catalog is registered under. Retired from the request path. */
  UNKNOWN_CONTEXT("UnknownContext"),
  /** A catalog registered at an epoch other than the one the request names. */
  EPOCH_MISMATCH("EpochMismatch"),
  /** The client speaks an IR version this planner cannot serve. */
  IR_VERSION("IrVersion"),
  /** A catalog the planner refuses to register. */
  INVALID_CATALOG("InvalidCatalog"),
  /** A guard the planner holds itself to. Reaching one is a bug, to be reported with the statement. */
  INTERNAL("Internal"),
  /**
   * Valid SQL the entitlements refuse. Distinct from VALIDATION because the SQL is well formed, and
   * from UNSUPPORTED because the planner could express it perfectly well and is declining to.
   */
  POLICY("Policy"),
  /**
   * A request the planner will not act on as written, such as an extension no handler is
   * registered for. Never ignored: a policy silently dropped is a policy not enforced.
   */
  INVALID_REQUEST("InvalidRequest"),
  /** A search the request's own options ended before the optimiser had a complete plan. */
  PLANNING_ABORTED("PlanningAborted"),
  /**
   * A catalog version this planner does not hold: evicted, or lost to a restart. A client recovers
   * by registering the version the message names and retrying once.
   */
  UNKNOWN_CATALOG_VERSION("UnknownCatalogVersion");

  private final String wireName;

  PlanErrorKind(String wireName) {
    this.wireName = wireName;
  }

  /** The kind's name, as the wire and a client spell it. */
  public String wireName() {
    return wireName;
  }
}
