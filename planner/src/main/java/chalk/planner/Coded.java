package chalk.planner;


/**
 * An error that names the rule it meets: the code a client carries on the exception it raises, and
 * the glossary explains (design 61).
 */
public interface Coded {
  /** The rule this error meets. */
  ErrorCode code();
}
