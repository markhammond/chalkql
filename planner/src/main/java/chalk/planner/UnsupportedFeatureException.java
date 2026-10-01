package chalk.planner;


/**
 * Valid SQL that the planner cannot express in the IR, or cannot physically plan.
 * Mapped to gRPC {@code UNIMPLEMENTED} with the kind {@code Unsupported}
 * (docs/design/03-planner.md §7).
 */
public final class UnsupportedFeatureException extends RuntimeException implements Coded {
  private static final long serialVersionUID = 1L;

  private final String feature;

  private final ErrorCode code;

  /**
   * @param code the rule the statement meets: unsupported SQL, an unsupported function or type, an
   *     argument that must be a constant
   */
  public UnsupportedFeatureException(ErrorCode code, String feature, String detail) {
    super(feature + " is not supported by this planner. " + detail);
    this.feature = feature;
    this.code = code;
  }

  @Override
  public ErrorCode code() {
    return code;
  }

  public String feature() {
    return feature;
  }
}
