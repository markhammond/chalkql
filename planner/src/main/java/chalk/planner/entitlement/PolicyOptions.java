package chalk.planner.entitlement;

import chalk.planner.rpc.v1.EntitlementsOptions;
import chalk.planner.rpc.v1.NamedColumns;
import chalk.planner.rpc.v1.PlaceholderPolicy;
import chalk.planner.rpc.v1.StarExpansion;
import chalk.planner.rpc.v1.StarPolicy;

/**
 * One prepare's entitlement choices, with the defaults filled in
 * (docs/design/16-entitlements.md §6).
 *
 * <p>None of them changes what the rewrite guarantees; each is about what the caller is handed. The
 * defaults are what a statement gets when the client says nothing, which is what makes the whole
 * message absent for a catalog without entitlements.
 *
 * <p>{@code starExpansion} and {@code namedColumns} are the two members of the request's one
 * redaction policy (D217 as amended), and they stay two because the questions differ:
 * {@code starExpansion} is about the columns a <em>star</em> surfaced, which is how §3.11 writes it,
 * and {@code namedColumns} about a column the statement <em>named</em>, which §3.11 answers
 * separately and never by omitting.
 */
public record PolicyOptions(
    StarPolicy star,
    StarExpansion starExpansion,
    PlaceholderPolicy placeholders,
    int defaultMinGroupSize,
    boolean refuseWhenNoVisibleRows,
    boolean explain,
    boolean includeDisclosureColumns,
    String disclosureColumnSuffix,
    NamedColumns namedColumns) {

  /**
   * What a sibling disclosure column's name is its column's name plus, when the host names none
   * (D207). The double underscore is the convention consumers associate with a system-supplied
   * column, and unlike {@code $} it survives as a property name and as an identifier on the wire.
   */
  public static final String DEFAULT_DISCLOSURE_SUFFIX = "__disclosure";

  /**
   * There is no shipped floor (D211). A host that wants one gives it at planning; a host that says
   * nothing gets none, and a catalog that declares population-only columns and no small-cell rule
   * pays nothing for them.
   */
  public static final int NO_MIN_GROUP_SIZE = 0;

  /** The same, without the two the sibling columns added: every request that asks for none. */
  public PolicyOptions(
      StarPolicy star,
      StarExpansion starExpansion,
      PlaceholderPolicy placeholders,
      int defaultMinGroupSize,
      boolean refuseWhenNoVisibleRows,
      boolean explain) {
    this(
        star,
        starExpansion,
        placeholders,
        defaultMinGroupSize,
        refuseWhenNoVisibleRows,
        explain,
        false,
        DEFAULT_DISCLOSURE_SUFFIX,
        NamedColumns.NAMED_COLUMNS_PLACEHOLDER);
  }

  /** These options with the sibling disclosure columns asked for, under {@code suffix} (D207). */
  public PolicyOptions withDisclosureColumns(String suffix) {
    return new PolicyOptions(
        star,
        starExpansion,
        placeholders,
        defaultMinGroupSize,
        refuseWhenNoVisibleRows,
        explain,
        true,
        suffix == null || suffix.isEmpty() ? DEFAULT_DISCLOSURE_SUFFIX : suffix,
        namedColumns);
  }

  public static final PolicyOptions DEFAULTS =
      new PolicyOptions(
          StarPolicy.STAR_POLICY_ALLOW,
          StarExpansion.STAR_EXPANSION_PLACEHOLDER,
          PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL,
          NO_MIN_GROUP_SIZE,
          false,
          false,
          false,
          DEFAULT_DISCLOSURE_SUFFIX,
          NamedColumns.NAMED_COLUMNS_PLACEHOLDER);

  public static PolicyOptions of(EntitlementsOptions proto) {
    if (proto == null) {
      return DEFAULTS;
    }
    // The redaction policy is one nested message (D217 as amended). An absent one is both defaults,
    // which is what an UNSPECIFIED member reads as anyway, so nothing has to test `hasRedaction`.
    chalk.planner.rpc.v1.Redaction redaction = proto.getRedaction();
    return new PolicyOptions(
        proto.getStarPolicy() == StarPolicy.STAR_POLICY_UNSPECIFIED
                || proto.getStarPolicy() == StarPolicy.UNRECOGNIZED
            ? StarPolicy.STAR_POLICY_ALLOW
            : proto.getStarPolicy(),
        redaction.getStarExpansion() == StarExpansion.STAR_EXPANSION_UNSPECIFIED
                || redaction.getStarExpansion() == StarExpansion.UNRECOGNIZED
            ? StarExpansion.STAR_EXPANSION_PLACEHOLDER
            : redaction.getStarExpansion(),
        proto.getPlaceholderPolicy() == PlaceholderPolicy.PLACEHOLDER_POLICY_UNSPECIFIED
                || proto.getPlaceholderPolicy() == PlaceholderPolicy.UNRECOGNIZED
            ? PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL
            : proto.getPlaceholderPolicy(),
        proto.getDefaultMinGroupSize(),
        proto.getRefuseWhenNoVisibleRows(),
        proto.getExplain(),
        proto.getIncludeDisclosureColumns(),
        proto.getDisclosureColumnSuffix().isEmpty()
            ? DEFAULT_DISCLOSURE_SUFFIX
            : proto.getDisclosureColumnSuffix(),
        redaction.getNamedColumns() == NamedColumns.NAMED_COLUMNS_UNSPECIFIED
                || redaction.getNamedColumns() == NamedColumns.UNRECOGNIZED
            ? NamedColumns.NAMED_COLUMNS_PLACEHOLDER
            : redaction.getNamedColumns());
  }

  /**
   * The floor this column's population aggregates are guarded by (D211): its own declared value, or
   * the host's default at planning when it declares {@code 0}. An explicit {@code 1} disables the
   * guard for the column whatever the default says, and {@link #guards(int)} is what reads that.
   */
  public int minGroupSize(int declared) {
    return declared == 0 ? defaultMinGroupSize : declared;
  }

  /** Whether a column declaring {@code declared} is guarded at all: an effective floor above one. */
  public boolean guards(int declared) {
    return minGroupSize(declared) > 1;
  }
}
