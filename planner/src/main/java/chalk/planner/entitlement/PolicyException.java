package chalk.planner.entitlement;

/**
 * Valid SQL the entitlements refuse ({@code PlanErrorKind.POLICY}, D143).
 *
 * <p>Distinct from a validation error because the SQL is well formed, and from
 * {@code UNSUPPORTED} because the planner could express it perfectly well and is declining to. Every
 * message names the table, the column and the use, because a refusal a developer cannot act on is a
 * refusal they will work around (docs/design/16-entitlements.md §3.12).
 *
 * <p>Both refusals of §3.5 are decided on the <em>folded</em> rules and never on the descriptor's
 * text: the same statement is legitimate for another principal.
 */
public final class PolicyException extends RuntimeException {
  private static final long serialVersionUID = 1L;

  public PolicyException(String message) {
    super(message);
  }

  /** A use of a column no disclosure at this leaf permits. */
  public static PolicyException use(String table, String column, String use, String remedy) {
    return new PolicyException(
        table
            + "."
            + column
            + " is population-only for this principal and "
            + use
            + " is not a population aggregate over it. "
            + remedy);
  }
}
