package chalk.planner.plan;

import chalk.ir.v1.Literal;
import chalk.ir.v1.Type;
import com.google.common.collect.ImmutableList;
import com.google.common.collect.ImmutableMap;
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
 * <h2>A holder, not a value</h2>
 *
 * <p>One instance per <em>pipeline</em>, joined to the framework context beside the connection
 * configuration and the cancel flag, holding one request's hints at a time (ADR 0074). It is a
 * holder rather than a value because a pipeline outlives a request: a narrowing re-enters a retained
 * one, and the hints it plans under are the narrowing's own. {@link
 * PlannerPipeline#parameterHints} is the only writer, and it drops whatever the previous request's
 * hints made the metadata cache remember before it sets the new ones.
 *
 * <p>The retained half a narrowing starts from — the validated statement and the converted tree —
 * reads no hint at all, which is what makes it safe to re-enter under any hints: the firewall below
 * is also what makes hints and narrowing compose.
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
 */
public final class ParameterHints {
  /** What {@link #of(RelOptCluster)} answers for a planner that carries no holder. */
  private static final ParameterHints NONE = new ParameterHints(true);

  private final boolean fixed;

  /**
   * This request's hints. Volatile because a planning runs on the scheduler's virtual thread while
   * the call that set them ran on the gRPC one; the reference is replaced and never mutated.
   */
  private volatile ImmutableMap<Integer, Hint> byOrdinal = ImmutableMap.of();

  /** A holder for one pipeline, empty until a request puts its hints in it. */
  public ParameterHints() {
    this(false);
  }

  private ParameterHints(boolean fixed) {
    this.fixed = fixed;
  }

  /**
   * One parameter's expected value, with the type the statement inferred for that parameter.
   *
   * @param literal the value, or null for a hint of SQL NULL — which is a statement about the value
   *     and not the absence of one, so a comparison against it selects no row
   * @param type the parameter's own inferred type, which is the units {@code literal} is in
   */
  public record Hint(@Nullable Literal literal, Type type) {
    /** The caller expects SQL NULL here. */
    public boolean isNull() {
      return literal == null;
    }
  }

  /**
   * Puts one request's hints in this holder, by ordinal. Only {@link PlannerPipeline} calls it, and
   * only once per request.
   */
  void set(java.util.Map<Integer, Hint> hints) {
    if (fixed) {
      throw new IllegalStateException(
          "the empty ParameterHints is shared by every planner that carries no holder of its own "
              + "and must never be written to");
    }

    byOrdinal = ImmutableMap.copyOf(hints);
  }

  /**
   * The hints this cluster's planner was built with, or an empty holder. The one way in, and the one
   * place the unwrap is written.
   */
  public static ParameterHints of(RelOptCluster cluster) {
    RelOptPlanner planner = cluster.getPlanner();
    if (planner == null) {
      return NONE;
    }

    org.apache.calcite.plan.@Nullable Context context = planner.getContext();
    if (context == null) {
      return NONE;
    }

    ParameterHints hints = context.unwrap(ParameterHints.class);
    return hints == null ? NONE : hints;
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
   * bound parameter is inferred exact-numeric, so a hint of another family has already been refused
   * by name at the request boundary; this last clause is the belt to that brace, and it declines
   * rather than rounds.
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
      case DECIMAL_VALUE -> wholeDecimal(literal, hint.type().getScale());
      default -> null;
    };
  }

  /**
   * A decimal hint as a count, or null when it is a fraction. A bound is a number of rows and never
   * a share of them, so a fractional hint is declined rather than rounded into one.
   */
  private static @Nullable Long wholeDecimal(Literal literal, int scale) {
    try {
      return new java.math.BigDecimal(
              chalk.planner.ir.LiteralConverter.fromLittleEndian16(
                  literal.getDecimalValue().getUnscaled().toByteArray()),
              scale)
          .longValueExact();
    } catch (ArithmeticException e) {
      return null;
    }
  }
}
