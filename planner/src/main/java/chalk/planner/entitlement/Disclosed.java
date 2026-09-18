package chalk.planner.entitlement;

/**
 * What one column of one leaf occurrence discloses, after the folding of §3.3
 * (docs/design/16-entitlements.md §3.1).
 *
 * <p>Five names, and they are the disclosure map's codomain rather than the descriptor's: the four
 * of {@code Disclosure} are what a <em>rule</em> can say about a row, and these are what the
 * <em>pass</em> concluded about a column at this leaf once the principal's context had been folded
 * in and the outcome simplified under the leaf's own conjuncts. A column whose rules resolved to one
 * constant name gets that name; one whose outcome still depends on the row gets {@link #PER_ROW}.
 *
 * <p>The order is the meet order of §3.12, most disclosing first, which is what {@link #meet} uses:
 * a derived column takes the least disclosing of its origins.
 */
public enum Disclosed {
  /** The column, unchanged. Every column of a table with no entitlement. */
  FULL,
  /** The column's mask, for every row. */
  MASKED,
  /** Raw and tainted on purpose: only an allow-listed population aggregate may consume it (§3.4). */
  AGGREGATE,
  /** Mixed across rows — a rule condition the fold could not decide. */
  PER_ROW,
  /**
   * A placeholder to every use but one: the comparisons the rule permits, computed in the leaf over
   * the raw value and disclosed as one boolean each (§3.1 as D261 amends it).
   *
   * <p>It sits between {@link #PER_ROW} and {@link #REDACTED} in the meet because that is what it
   * is: the value is a placeholder exactly as under {@code REDACTED}, and one bit more than that
   * placeholder is disclosed. A derived column over a tested origin and a redacted one is redacted,
   * which is the conservative direction and the true one — the second origin disclosed nothing.
   */
  TESTED,
  /** A placeholder, for every row. */
  REDACTED;

  /** The less disclosing of two, which is the meet the report takes over a column's origins. */
  public Disclosed meet(Disclosed other) {
    return compareTo(other) >= 0 ? this : other;
  }

  /**
   * Whether this column leaves the leaf as something other than itself — §3.8's gate question, and
   * the {@code redacted} half of the read's per-column outcomes.
   *
   * <p>{@link #TESTED} is deliberately not one of them either, and for the same reason read the
   * other way round (D261): what leaves the leaf is a <em>comparison</em> the policy permitted,
   * computed where the raw value is, and a source holding the raw value anyway is exactly where it
   * belongs — so the derived boolean pushes with the leaf's row predicate. The value itself is a
   * placeholder, a constant, which reads nothing and is never the gate's question.
   *
   * <p>{@link #AGGREGATE} is deliberately <em>not</em> redacted. A population-only column leaves the
   * leaf raw and tainted on purpose (§3.4), so a reference to it is the column and a source may read
   * it; nothing non-bare over it can exist for the gate to judge, because the trace refuses every
   * such use before a plan is built. §3.6 relies on this: a pushed aggregate carries its extra
   * {@code COUNT} into the source while the guard stays local.
   */
  public boolean isRedactedFromPushdown() {
    return this == MASKED || this == REDACTED || this == PER_ROW;
  }

  /**
   * Whether the leaf hands this column's value back as something other than itself.
   *
   * <p>The same question {@link #isRedactedFromPushdown} asks, without the pushdown exception
   * {@link #TESTED} carries: a tested column's <em>value</em> is the placeholder, so nothing above
   * the leaf may read it, and what the gate of §3.8 is asked about is the leaf's own derived
   * comparison, which discloses the one bit the policy named and which the source may therefore
   * compute (D261).
   */
  public boolean isWithheldValue() {
    return this == MASKED || this == REDACTED || this == PER_ROW || this == TESTED;
  }

  /** The wire spelling of this outcome on an entitled {@code Read} (§3.10). */
  public chalk.ir.v1.DisclosureOutcome wire() {
    return switch (this) {
      case FULL -> chalk.ir.v1.DisclosureOutcome.DISCLOSURE_OUTCOME_FULL;
      case MASKED -> chalk.ir.v1.DisclosureOutcome.DISCLOSURE_OUTCOME_MASKED;
      case AGGREGATE -> chalk.ir.v1.DisclosureOutcome.DISCLOSURE_OUTCOME_AGGREGATE;
      case PER_ROW -> chalk.ir.v1.DisclosureOutcome.DISCLOSURE_OUTCOME_PER_ROW;
      case TESTED -> chalk.ir.v1.DisclosureOutcome.DISCLOSURE_OUTCOME_TESTED;
      case REDACTED -> chalk.ir.v1.DisclosureOutcome.DISCLOSURE_OUTCOME_REDACTED;
    };
  }
}
