package chalk.planner;

/**
 * Valid SQL that the planner cannot express in the IR, or cannot physically plan in this milestone.
 * Mapped to gRPC {@code UNIMPLEMENTED} with {@code PLAN_ERROR_KIND_UNSUPPORTED}
 * (docs/design/03-planner.md §7).
 */
public final class UnsupportedFeatureException extends RuntimeException {
  private static final long serialVersionUID = 1L;

  private final String feature;

  public UnsupportedFeatureException(String feature, String detail) {
    super(feature + " is not supported by this planner. " + detail);
    this.feature = feature;
  }

  public String feature() {
    return feature;
  }
}
