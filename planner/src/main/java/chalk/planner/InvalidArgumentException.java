package chalk.planner;


/**
 * An argument the planner refuses, naming the rule it meets: a context it cannot bind, an option it
 * cannot honour, a request it will not act on. An {@link IllegalArgumentException}, so it is
 * handled as every refused argument is, and carries the code a client reports it with.
 */
public final class InvalidArgumentException extends IllegalArgumentException implements Coded {
  private static final long serialVersionUID = 1L;

  private final ErrorCode code;

  public InvalidArgumentException(ErrorCode code, String message) {
    super(message);
    this.code = code;
  }

  public InvalidArgumentException(ErrorCode code, String message, Throwable cause) {
    super(message, cause);
    this.code = code;
  }

  @Override
  public ErrorCode code() {
    return code;
  }
}
