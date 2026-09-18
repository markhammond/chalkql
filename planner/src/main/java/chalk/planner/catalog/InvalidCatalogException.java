package chalk.planner.catalog;

/**
 * A registered catalog descriptor breaks a rule the client's own {@code CatalogValidator} also
 * applies (docs/design/03-planner.md §3.1). Mapped to gRPC {@code INVALID_ARGUMENT} with {@code
 * PLAN_ERROR_KIND_INVALID_CATALOG}.
 */
public final class InvalidCatalogException extends RuntimeException {
  private static final long serialVersionUID = 1L;

  public InvalidCatalogException(String path, String detail) {
    super("Invalid catalog at " + path + ": " + detail);
  }
}
