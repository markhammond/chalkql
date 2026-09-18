package chalk.planner.entitlement;

import java.util.List;
import org.apache.calcite.runtime.ImmutablePairList;

/**
 * What happens to an output column no disclosure permits (docs/design/16-entitlements.md §3.11,
 * D161).
 *
 * <p>Three answers, the client's to choose per prepare. {@code Placeholder} — the default — keeps
 * the column as a typed stand-in, so the row shape is the same for every principal.
 * {@code Refuse} makes it a {@code POLICY} error naming the column, for a client that wants to be
 * told. {@code Omit} drops it from the root's field list, for a client that renders what it gets —
 * and only where the disclosure is a constant nothing and the column came from a star: a column the
 * statement named is never made to vanish silently, and a row-dependent disclosure degrades to
 * {@code Placeholder} with the report saying so.
 *
 * <p>All three are about the columns a <b>star's expansion</b> surfaced, which is how §3.11 writes
 * that member (D217 as amended). A column the statement <em>named</em> is the case §3.11 answers
 * separately — a {@code POLICY} error when no disclosure could ever permit it, and otherwise a
 * placeholder — and the policy's second member, {@code named_columns}, is what turns that
 * placeholder into a refusal for a host that would rather be told. It has no {@code Omit}: dropping
 * a column the statement named would be a silent failure.
 */
public final class RedactionPolicy {
  private RedactionPolicy() {}

  /** The field list after {@code Omit}, and whether it had to degrade. */
  public record Result(ImmutablePairList<Integer, String> fields, boolean degraded) {}

  /** Applies the request's choice to the root's field list. */
  public static Result apply(
      ImmutablePairList<Integer, String> fields,
      List<Disclosed> flow,
      StarProvenance stars,
      PolicyOptions options,
      boolean shapeOnly) {
    if (options.namedColumns() == chalk.planner.rpc.v1.NamedColumns.NAMED_COLUMNS_REFUSE) {
      refuseNamed(fields, flow, stars);
    }
    return switch (options.starExpansion()) {
      case STAR_EXPANSION_REFUSE -> refuse(fields, flow, stars);
      // Omit needs a constant nothing for the whole query, which only prepare-time binding gives:
      // under a shape the rules are decided per row at execution, so every column stays and the
      // report says the request degraded (§3.11, D209).
      case STAR_EXPANSION_OMIT ->
          shapeOnly ? new Result(fields, true) : omit(fields, flow, stars);
      default -> new Result(fields, false);
    };
  }

  private static Disclosed at(List<Disclosed> flow, int column) {
    return column >= 0 && column < flow.size() ? flow.get(column) : Disclosed.FULL;
  }

  private static Result refuse(
      ImmutablePairList<Integer, String> fields, List<Disclosed> flow, StarProvenance stars) {
    for (int i = 0; i < fields.size(); i++) {
      // The columns a star surfaced, which is what this option is about: a column the statement
      // named is the policy's `named_columns` member's business (D217).
      if (at(flow, fields.leftList().get(i)) == Disclosed.REDACTED && !stars.isNamed(i)) {
        throw new PolicyException(
            "the output column '"
                + fields.rightList().get(i)
                + "' is one this statement's star surfaced, it discloses nothing for this "
                + "principal, and Redaction.StarExpansion.Refuse asks to be told rather than "
                + "handed a "
                + "placeholder (docs/design/16-entitlements.md §3.11).");
      }
    }
    return new Result(fields, false);
  }

  /**
   * A redacted column the statement <em>named</em>, under {@code Redaction.NamedColumns.Refuse}
   * (D217). §3.11's own answer is a placeholder — the column is named, so nothing is hidden by
   * handing one back — and this is the host that would rather be told.
   */
  private static void refuseNamed(
      ImmutablePairList<Integer, String> fields, List<Disclosed> flow, StarProvenance stars) {
    for (int i = 0; i < fields.size(); i++) {
      if (at(flow, fields.leftList().get(i)) == Disclosed.REDACTED && stars.isNamed(i)) {
        throw new PolicyException(
            "the output column '"
                + fields.rightList().get(i)
                + "' is named by this statement, it discloses nothing for this principal, and "
                + "Redaction.NamedColumns.Refuse asks to be told rather than handed a placeholder "
                + "(docs/design/16-entitlements.md §3.11).");
      }
    }
  }

  private static Result omit(
      ImmutablePairList<Integer, String> fields, List<Disclosed> flow, StarProvenance stars) {
    org.apache.calcite.runtime.PairList<Integer, String> kept =
        org.apache.calcite.runtime.PairList.of();
    // A star whose expansion could not be read reads as all-named, so nothing is dropped; the
    // caller asked for Omit and did not get it, and that is a degradation whether or not any column
    // was undisclosed (§3.11).
    boolean degraded = stars.expansionUnreadable();
    boolean dropped = false;
    for (int i = 0; i < fields.size(); i++) {
      Disclosed disclosed = at(flow, fields.leftList().get(i));
      boolean omittable = disclosed == Disclosed.REDACTED && !stars.isNamed(i);
      if (omittable) {
        dropped = true;
        continue;
      }
      // Omit needs a constant NONE for the whole query, which prepare-time binding gives; a
      // row-dependent disclosure cannot be dropped and becomes a placeholder instead.
      if (disclosed == Disclosed.PER_ROW && !stars.isNamed(i)) {
        degraded = true;
      }
      kept.add(fields.leftList().get(i), fields.rightList().get(i));
    }
    ImmutablePairList<Integer, String> result = kept.immutable();
    // A statement whose every column is undisclosed would have no row at all; keeping them is the
    // only shape a caller can read, and the report says the request degraded.
    if (result.isEmpty()) {
      return new Result(fields, true);
    }
    return new Result(dropped ? result : fields, degraded);
  }
}
