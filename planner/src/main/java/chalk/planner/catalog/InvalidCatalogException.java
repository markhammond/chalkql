package chalk.planner.catalog;

import chalk.planner.Coded;
import chalk.planner.ErrorCode;

/**
 * A registered catalog descriptor breaks a rule the client's own {@code CatalogValidator} also
 * applies (docs/design/03-planner.md §3.1). Mapped to gRPC {@code INVALID_ARGUMENT} with the kind {@code
 * InvalidCatalog}.
 */
public final class InvalidCatalogException extends RuntimeException implements Coded {
  private static final long serialVersionUID = 1L;

  private final ErrorCode code;

  /** @param code the rule the catalog breaks */
  public InvalidCatalogException(ErrorCode code, String path, String detail) {
    super("Invalid catalog at " + path + ": " + detail);
    this.code = code;
  }

  @Override
  public ErrorCode code() {
    return code;
  }
}
