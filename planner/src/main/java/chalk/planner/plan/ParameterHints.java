package chalk.planner.plan;

import chalk.ir.v1.Literal;
import chalk.planner.rpc.v1.ParameterHint;
import com.google.common.collect.ImmutableList;
import com.google.common.collect.ImmutableMap;
import java.util.List;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptPlanner;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * What the caller expects this request's parameters to be worth, for estimation only (D284).
 *
 * <p>A hint is metadata and never semantics. It changes which plan is cheapest and nothing else: the
 * tree the pass hands to Volcano is the same tree, the {@code RexDynamicParam}s in the finished plan
 * are the same set, and the values bound at execution need not resemble the hints at all. That is
 * what makes a wrong hint a slow query rather than a wrong answer.
 *
 * <h2>The firewall</h2>
 *
 * <p>This class is reachable <b>only</b> through the planner's context —
 * {@code cluster.getPlanner().getContext().unwrap(ParameterHints.class)}, which is what {@link
 * #of(RelOptCluster)} does — and is read by exactly three places: {@link ChalkSelectivity}, {@link
 * chalk.planner.plan.rel.ChalkLimit} and {@link chalk.planner.plan.rel.ChalkTopN}. No rule, no
 * {@code RexSimplify} call, no {@code RexToIr} path, no pushdown gate and no digest is given one,
 * and none may take one: an estimate that reached a rewrite would turn an expectation into a
 * semantic. {@code ChalkRowGoalRule} reads a hinted bound only as {@code ChalkLimit} already answers
 * it, which is the "through them" of design 49 §3.
 *
 * <p>One instance per request, immutable, joined to the framework context beside the connection
 * configuration and the cancel flag. A fresh pipeline per hinted request is what keeps one request's
 * metadata cache from seeing another's hints.
 */
public final class ParameterHints {
  /** No hints at all, which is every request that sends none. */
  public static final ParameterHints EMPTY = new ParameterHints(ImmutableMap.of());

  private final ImmutableMap<Integer, Hint> byOrdinal;

  private ParameterHints(ImmutableMap<Integer, Hint> byOrdinal) {
    this.byOrdinal = byOrdinal;
  }

  /**
   * One parameter's expected value.
   *
   * @param literal the value, or null for a hint of SQL NULL — which is a statement about the value
   *     and not the absence of one, so a comparison against it selects no row
   */
  public record Hint(@Nullable Literal literal) {
    /** The caller expects SQL NULL here. */
    public boolean isNull() {
      return literal == null;
    }
  }

  /** A hint of SQL NULL. */
  public static final Hint NULL = new Hint(null);

  /** The request's hints, keyed by ordinal. A later entry for an ordinal replaces an earlier one. */
  public static ParameterHints of(List<ParameterHint> hints) {
    if (hints.isEmpty()) {
      return EMPTY;
    }

    java.util.LinkedHashMap<Integer, Hint> byOrdinal = new java.util.LinkedHashMap<>();
    for (ParameterHint hint : hints) {
      switch (hint.getValueCase()) {
        case LITERAL -> byOrdinal.put(hint.getOrdinal(), new Hint(hint.getLiteral()));
        case IS_NULL -> byOrdinal.put(hint.getOrdinal(), NULL);
        // A hint that says nothing is not a hint, and it is not an error either: a sparse container
        // is the API's own shape. It is dropped, so the stage list names only the ordinals the
        // caller actually said something about.
        case VALUE_NOT_SET -> byOrdinal.remove(hint.getOrdinal());
      }
    }

    return byOrdinal.isEmpty() ? EMPTY : new ParameterHints(ImmutableMap.copyOf(byOrdinal));
  }

  /**
   * The hints this cluster's planner was built with, or {@link #EMPTY}. The one way in, and the one
   * place the unwrap is written.
   */
  public static ParameterHints of(RelOptCluster cluster) {
    RelOptPlanner planner = cluster.getPlanner();
    if (planner == null) {
      return EMPTY;
    }

    org.apache.calcite.plan.@Nullable Context context = planner.getContext();
    if (context == null) {
      return EMPTY;
    }

    ParameterHints hints = context.unwrap(ParameterHints.class);
    return hints == null ? EMPTY : hints;
  }

  public boolean isEmpty() {
    return byOrdinal.isEmpty();
  }

  /** What the caller said about parameter {@code ordinal}, or null when it said nothing. */
  public @Nullable Hint at(int ordinal) {
    return byOrdinal.get(ordinal);
  }

  /** The hinted ordinals, ascending — what the stage list names. */
  public ImmutableList<Integer> ordinals() {
    return ImmutableList.sortedCopyOf(byOrdinal.keySet());
  }

  /**
   * A hinted bound's value: the integer the caller expects, or null when it said nothing, said NULL,
   * or said something that is not an integer.
   *
   * <p>A bound is an integer and never a percentage. Calcite's grammar has no {@code LIMIT 10%} and a
   * {@code LIMIT ?} parameter is inferred integer-typed, so a hint of another family has already been
   * refused by name at the request boundary; this last clause is the belt to that brace, and it
   * declines rather than rounds.
   */
  public @Nullable Long boundAt(int ordinal) {
    Hint hint = byOrdinal.get(ordinal);
    if (hint == null || hint.isNull()) {
      return null;
    }

    Literal literal = hint.literal();
    return switch (literal.getValueCase()) {
      case I8_VALUE -> (long) literal.getI8Value();
      case I16_VALUE -> (long) literal.getI16Value();
      case I32_VALUE -> (long) literal.getI32Value();
      case I64_VALUE -> literal.getI64Value();
      default -> null;
    };
  }
}
