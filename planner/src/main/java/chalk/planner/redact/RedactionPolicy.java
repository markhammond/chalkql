package chalk.planner.redact;

import chalk.planner.rpc.v1.RedactionOptions;
import chalk.planner.rpc.v1.RedactionScope;
import java.util.Arrays;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * What one redaction was asked for (D262, {@code docs/design/37-redacted-sql.md} §1): the host's
 * salt, which literals become pseudonyms, and whether the positions that are not data are kept.
 *
 * <p>The salt is the host's secret. It arrives on the wire because the pseudonym must be the same
 * whichever side computes it, and it is never logged, never returned and never put in a message.
 */
public final class RedactionPolicy {
  /** Which literals a redaction replaces. */
  public enum Scope {
    /** Character and binary strings only. */
    STRINGS,
    /** Every literal. The default. */
    ALL
  }

  private final byte[] salt;
  private final Scope scope;
  private final boolean keepStructural;

  private RedactionPolicy(byte[] salt, Scope scope, boolean keepStructural) {
    this.salt = salt;
    this.scope = scope;
    this.keepStructural = keepStructural;
  }

  /**
   * The policy a request asked for, or null for a request that asked for none — which is every
   * request that does not set the message, and is what makes the whole feature cost nothing.
   *
   * @param present whether the request carried the message at all; an all-default message is a
   *     legitimate ask and must not read as an absent one
   */
  public static @Nullable RedactionPolicy of(RedactionOptions options, boolean present) {
    if (!present) {
      return null;
    }
    if (options.getSalt().isEmpty()) {
      throw new IllegalArgumentException(
          "PlanRequest.redaction carries no salt. A salt absent or empty means a random per-engine"
              + " one, and that decision is the client's: a sidecar that invented a key here would"
              + " give two engines different pseudonyms for the same host secret"
              + " (docs/design/37-redacted-sql.md §2).");
    }
    return new RedactionPolicy(
        options.getSalt().toByteArray(), scopeOf(options.getScope()), options.getKeepStructural());
  }

  /** A policy built directly, for the planner's own tests and for callers inside this package. */
  public static RedactionPolicy of(byte[] salt, Scope scope, boolean keepStructural) {
    if (salt.length == 0) {
      throw new IllegalArgumentException("a redaction needs a salt");
    }
    return new RedactionPolicy(Arrays.copyOf(salt, salt.length), scope, keepStructural);
  }

  private static Scope scopeOf(RedactionScope scope) {
    return switch (scope) {
      case REDACTION_SCOPE_STRINGS -> Scope.STRINGS;
      // UNSPECIFIED reads as ALL, exactly as the wire comment says; a value from a newer client
      // that this build has never heard of is refused rather than quietly read as something else.
      case REDACTION_SCOPE_ALL, REDACTION_SCOPE_UNSPECIFIED -> Scope.ALL;
      case UNRECOGNIZED ->
          throw new IllegalArgumentException(
              "RedactionOptions.scope is not a RedactionScope this planner knows");
    };
  }

  /** The host's secret. Package-private on purpose: nothing outside this package may read it. */
  byte[] salt() {
    return salt;
  }

  public Scope scope() {
    return scope;
  }

  public boolean keepStructural() {
    return keepStructural;
  }
}
