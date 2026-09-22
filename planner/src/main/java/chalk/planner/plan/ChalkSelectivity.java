package chalk.planner.plan;

import chalk.ir.v1.ColumnStatistics;
import chalk.ir.v1.FrequentValue;
import chalk.ir.v1.HistogramBucket;
import chalk.ir.v1.Literal;
import chalk.ir.v1.StatisticsLevel;
import chalk.ir.v1.Type;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.ir.LiteralConverter;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.rel.metadata.RelMdUtil;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * How selective a predicate is over a Chalk table, from the statistics the source declared (D36).
 * The assumptions are documented rather than inferred, because a cost model whose guesses are
 * invisible is a cost model nobody can debug:
 *
 * <ul>
 *   <li><b>Equality</b> — the most-common-value list when the value is in it, else
 *       {@code 1 / distinct_count}, else Calcite's default. {@code IS NOT DISTINCT FROM} is
 *       equality here: it differs from {@code =} only in what it does with NULLs, and the
 *       {@code null_count} that would refine that is a second-order correction on an estimate.
 *   <li><b>Range</b> — the histogram by bucket interpolation, else uniform between {@code min} and
 *       {@code max} for the numeric and temporal kinds, else Calcite's default.
 *   <li><b>IS NULL</b> — {@code null_count / rows}; {@code IS NOT NULL} its complement.
 *   <li><b>Anything else, and any unknown statistic</b> — {@code RelMdUtil.guessSelectivity}, and
 *       the estimate is reported as a guess so nobody mistakes it for a measurement.
 * </ul>
 *
 * <p>Conjuncts multiply, which assumes independence. That is the same assumption Calcite makes and
 * it is wrong in the usual ways; it is written down here rather than buried.
 */
public final class ChalkSelectivity {
  private ChalkSelectivity() {}

  /**
   * An estimate, whether any part of it was guessed, and whether any part of it came from a value
   * the caller said it expected rather than from the statement itself (D284).
   *
   * @param value the selectivity, in (0, 1]
   * @param guessed true when a default stood in for a statistic the source did not declare
   * @param hinted true when a parameter's expected value stood in for a value the statement did not
   *     carry — a number this plan is costed for and that no execution is held to
   */
  public record Estimate(double value, boolean guessed, boolean hinted) {
    /** An estimate from the statement alone, which is every estimate that reads no hint. */
    public Estimate(double value, boolean guessed) {
      this(value, guessed, false);
    }

    public Estimate times(Estimate other) {
      return new Estimate(
          value * other.value, guessed || other.guessed, hinted || other.hinted);
    }

    /**
     * For the plan text: {@code 0.0714} measured, {@code guess(0.2500)} guessed, {@code
     * hint(0.0008)} from a hinted parameter.
     *
     * <p>A guess wins over a hint when both went into it, because it is the weaker claim and a
     * reader staring at a bad plan needs the weakest one.
     */
    public String text() {
      String number = String.format(java.util.Locale.ROOT, "%.4f", value);
      if (guessed) {
        return "guess(" + number + ")";
      }

      return hinted ? "hint(" + number + ")" : number;
    }
  }

  /** Everything passes. */
  public static final Estimate ALL = new Estimate(1.0, false);

  /**
   * The share of {@code table}'s rows that satisfy {@code predicate}, whose input refs index
   * {@code fields} — the table column each position of the predicate's row comes from.
   */
  public static Estimate of(
      @Nullable RexNode predicate, ChalkTable table, List<Integer> fields, RelOptCluster cluster) {
    if (predicate == null || predicate.isAlwaysTrue()) {
      return ALL;
    }

    // The one unwrap of this request's hints on the estimation path. The callers hand over a
    // cluster and never a hint, which is what keeps the firewall of design 49 §3 structural rather
    // than a matter of discipline.
    ParameterHints hints = ParameterHints.of(cluster);
    RexBuilder rexBuilder = cluster.getRexBuilder();
    Double rows = table.rowCount();
    RexNode expanded = RexUtil.expandSearch(rexBuilder, null, predicate);
    Estimate estimate = ALL;

    // Conjuncts multiply, which assumes independence — but two range bounds on the *same* column are
    // the opposite of independent: `x >= a AND x < b` is one interval, and multiplying "the share
    // above a" by "the share at or below b" counts the gap twice. `1994-01-01 <= l_shipdate <
    // 1995-01-01` over a seven-year span came out at 0.31 instead of 0.14, which is the difference
    // between TPC-H Q6 choosing its index and keeping its scan. Bounds are therefore collected per
    // column and turned into one interval width; everything else still multiplies.
    Map<Integer, Interval> intervals = new LinkedHashMap<>();
    for (RexNode conjunct : RelOptUtil.conjunctions(expanded)) {
      Bound bound = bound(conjunct, table, fields, rows, hints);
      if (bound == null) {
        estimate = estimate.times(conjunct(conjunct, table, fields, rows, hints));
      } else {
        intervals.computeIfAbsent(bound.column(), column -> new Interval(bound.hinted())).add(bound);
      }
    }
    for (Interval interval : intervals.values()) {
      estimate = estimate.times(interval.estimate());
    }

    // Never zero: Volcano divides by row counts, and a plan that claims no rows at all defeats
    // every comparison downstream of it.
    return new Estimate(
        Math.min(1.0, Math.max(estimate.value(), 1e-9)), estimate.guessed(), estimate.hinted());
  }

  /**
   * How selective a <b>join condition</b> is, which is Calcite's own guess with one correction:
   * {@code a IS NOT DISTINCT FROM b} is an equi-join key, so it is estimated as {@code a = b}
   * (0.15) rather than as an unrecognised predicate (0.25).
   *
   * <p>This is not a nicety. Calcite's distinct-aggregate expansion joins the per-column aggregates
   * back together on the grouping keys, and it writes those equalities null-safely because NULL is a
   * group like any other; every one of them was a quarter rather than a seventh, compounding over a
   * chain of joins. The estimate is still Calcite's guess and still ignores that the keys are unique
   * — {@code RelMdUtil.getJoinRowCount} multiplies the two inputs by this number and consults no
   * uniqueness at all (ADR 0018) — but it is now the number an equality would have got.
   *
   * @param condition the join condition, over the joined row; null or always-true means everything
   */
  public static double ofJoinCondition(@Nullable RexNode condition, RexBuilder rexBuilder) {
    if (condition == null) {
      return 1.0;
    }

    return RelMdUtil.guessSelectivity(rewriteNullSafeEqualities(condition, rexBuilder));
  }

  /**
   * {@code condition} with every top-level {@code IS NOT DISTINCT FROM} conjunct rewritten to
   * {@code =}. Only the top level matters: {@code guessSelectivity} looks no deeper. The result is
   * for estimating only and is never emitted.
   */
  public static @Nullable RexNode nullSafeEqualitiesAsEqualities(
      @Nullable RexNode condition, RexBuilder rexBuilder) {
    return condition == null ? null : rewriteNullSafeEqualities(condition, rexBuilder);
  }

  private static RexNode rewriteNullSafeEqualities(RexNode condition, RexBuilder rexBuilder) {
    List<RexNode> conjuncts = RelOptUtil.conjunctions(condition);
    List<RexNode> rewritten = new java.util.ArrayList<>(conjuncts.size());
    boolean changed = false;
    for (RexNode conjunct : conjuncts) {
      if (conjunct.getKind() == SqlKind.IS_NOT_DISTINCT_FROM && conjunct instanceof RexCall call) {
        rewritten.add(rexBuilder.makeCall(SqlStdOperatorTable.EQUALS, call.getOperands()));
        changed = true;
      } else {
        rewritten.add(conjunct);
      }
    }

    return changed ? RexUtil.composeConjunction(rexBuilder, rewritten) : condition;
  }

  /** One side of an interval: the share of rows at or below {@code share}, on {@code column}. */
  private record Bound(int column, double share, boolean below, boolean hinted) {}

  /** The bounds seen on one column, narrowed as each arrives. */
  private static final class Interval {
    private double lower;
    private double upper = 1.0;
    private boolean hinted;

    Interval(boolean hinted) {
      this.hinted = hinted;
    }

    void add(Bound bound) {
      hinted |= bound.hinted();
      if (bound.below()) {
        upper = Math.min(upper, bound.share());
      } else {
        lower = Math.max(lower, bound.share());
      }
    }

    Estimate estimate() {
      return new Estimate(clamp(upper - lower), false, hinted);
    }
  }

  /**
   * The conjunct as an interval bound, or null when it is not a range on a column whose statistics
   * can place the value on a line — in which case the general path estimates it (and guesses).
   */
  private static @Nullable Bound bound(
      RexNode conjunct,
      ChalkTable table,
      List<Integer> fields,
      @Nullable Double rows,
      ParameterHints hints) {
    if (!(conjunct instanceof RexCall call) || call.getOperands().size() != 2) {
      return null;
    }

    RexNode left = call.getOperands().get(0);
    RexNode right = call.getOperands().get(1);
    SqlKind kind = call.getKind();
    RexNode column = left;
    RexNode value = right;
    if (!(left instanceof RexInputRef)) {
      column = right;
      value = left;
      kind = kind.reverse();
    }

    boolean below = kind == SqlKind.LESS_THAN || kind == SqlKind.LESS_THAN_OR_EQUAL;
    boolean above = kind == SqlKind.GREATER_THAN || kind == SqlKind.GREATER_THAN_OR_EQUAL;
    if (!below && !above) {
      return null;
    }

    int tableColumn = columnOf(column, fields);
    if (tableColumn < 0) {
      return null;
    }

    ColumnStatistics statistics = table.statistics(tableColumn);
    if (statistics.getLevel() == StatisticsLevel.STATISTICS_LEVEL_UNKNOWN) {
      return null;
    }

    Type type = table.column(tableColumn).getType();
    Comparable<?> wanted;
    boolean hinted = false;
    if (value instanceof RexLiteral rexLiteral) {
      try {
        wanted = LiteralValues.of(LiteralConverter.convert(rexLiteral, type));
      } catch (RuntimeException e) {
        return null;
      }
    } else {
      ParameterHints.Hint hint = hintFor(value, hints);
      // A NULL hint is not an interval bound: no row satisfies a comparison against NULL, so it is
      // left to the general path, which answers the empty estimate for it.
      if (hint == null || hint.isNull()) {
        return null;
      }

      wanted = hinted(hint, type);
      hinted = true;
    }

    Double share = wanted == null ? null : shareBelow(statistics, wanted, rows);
    return share == null ? null : new Bound(tableColumn, share, below, hinted);
  }

  private static Estimate conjunct(
      RexNode conjunct,
      ChalkTable table,
      List<Integer> fields,
      @Nullable Double rows,
      ParameterHints hints) {
    if (conjunct.getKind() == SqlKind.IS_NULL || conjunct.getKind() == SqlKind.IS_NOT_NULL) {
      RexNode operand = ((RexCall) conjunct).getOperands().get(0);
      ColumnStatistics statistics = statisticsOf(operand, table, fields);
      if (statistics == null || statistics.getNullCount() < 0 || rows == null || rows <= 0) {
        return guess(conjunct);
      }

      double nulls = Math.min(statistics.getNullCount() / rows, 1.0);
      return new Estimate(conjunct.getKind() == SqlKind.IS_NULL ? nulls : 1.0 - nulls, false);
    }

    if (!(conjunct instanceof RexCall call) || call.getOperands().size() != 2) {
      return guess(conjunct);
    }

    RexNode left = call.getOperands().get(0);
    RexNode right = call.getOperands().get(1);
    SqlKind kind = call.getKind();

    RexNode column = left;
    RexNode value = right;
    if (!(left instanceof RexInputRef)) {
      column = right;
      value = left;
      kind = kind.reverse();
    }

    int tableColumn = columnOf(column, fields);
    if (tableColumn < 0) {
      return guess(conjunct);
    }

    // A parameter has no value at planning time, so a predicate against one is a guess — unless the
    // caller said what it expects, which is the whole of what a hint buys here (D284). A hint of
    // SQL NULL is a statement about the value too: no row satisfies a comparison against NULL, so
    // the estimate is the floor every estimate is clamped to rather than a guessed quarter.
    ParameterHints.@Nullable Hint hint = null;
    if (!(value instanceof RexLiteral)) {
      hint = hintFor(value, hints);
      if (hint == null) {
        return guess(conjunct);
      }

      if (hint.isNull()) {
        return new Estimate(1e-9, false, true);
      }
    }

    ColumnStatistics statistics = table.statistics(tableColumn);
    if (statistics.getLevel() == StatisticsLevel.STATISTICS_LEVEL_UNKNOWN) {
      return guess(conjunct);
    }

    // Compare in the IR's own units: a Rex TIMESTAMP literal counts milliseconds and the catalog's
    // counts Type.precision units, so the only safe comparison is after the same conversion the
    // rest of the planner makes. A hint arrives in its parameter's units and is coerced the same way.
    Type type = table.column(tableColumn).getType();
    Comparable<?> wanted;
    if (hint != null) {
      wanted = hinted(hint, type);
    } else {
      try {
        wanted = LiteralValues.of(LiteralConverter.convert((RexLiteral) value, type));
      } catch (RuntimeException e) {
        return guess(conjunct);
      }
    }

    if (wanted == null) {
      return guess(conjunct);
    }

    boolean fromHint = hint != null;
    return switch (kind) {
      case EQUALS, IS_NOT_DISTINCT_FROM -> mark(equality(statistics, wanted, rows, conjunct), fromHint);
      case NOT_EQUALS, IS_DISTINCT_FROM -> {
        Estimate equal = equality(statistics, wanted, rows, conjunct);
        yield mark(new Estimate(1.0 - equal.value(), equal.guessed()), fromHint);
      }
      case GREATER_THAN, GREATER_THAN_OR_EQUAL, LESS_THAN, LESS_THAN_OR_EQUAL ->
          mark(range(statistics, wanted, kind, rows, conjunct), fromHint);
      default -> guess(conjunct);
    };
  }

  private static Estimate mark(Estimate estimate, boolean hinted) {
    return hinted ? new Estimate(estimate.value(), estimate.guessed(), true) : estimate;
  }

  /** What the caller said about this operand, when it is a parameter and the caller said anything. */
  private static ParameterHints.@Nullable Hint hintFor(RexNode value, ParameterHints hints) {
    return value instanceof org.apache.calcite.rex.RexDynamicParam parameter
        ? hints.at(parameter.getIndex())
        : null;
  }

  /** The MCV list if the value is in it, else 1 / distinct_count, else a guess. */
  private static Estimate equality(
      ColumnStatistics statistics, Comparable<?> wanted, @Nullable Double rows, RexNode conjunct) {
    if (rows != null && rows > 0) {
      for (FrequentValue frequent : statistics.getFrequentValuesList()) {
        Comparable<?> value = LiteralValues.of(frequent.getValue());
        if (value != null && LiteralValues.equal(value, wanted)) {
          return new Estimate(Math.min(frequent.getCount() / rows, 1.0), false);
        }
      }
    }

    if (statistics.getDistinctCount() > 0) {
      return new Estimate(1.0 / statistics.getDistinctCount(), false);
    }

    return guess(conjunct);
  }

  /**
   * A range's share, by bucket interpolation when there is a histogram and uniformly between
   * {@code min} and {@code max} otherwise. Both need the value to be a number the planner can
   * place on a line, which {@link LiteralValues#position} answers for the numeric and temporal
   * kinds and declines for everything else.
   */
  private static Estimate range(
      ColumnStatistics statistics,
      Comparable<?> wanted,
      SqlKind kind,
      @Nullable Double rows,
      RexNode conjunct) {
    boolean below = kind == SqlKind.LESS_THAN || kind == SqlKind.LESS_THAN_OR_EQUAL;
    Double share = shareBelow(statistics, wanted, rows);
    return share == null
        ? guess(conjunct)
        : new Estimate(clamp(below ? share : 1.0 - share), false);
  }

  /**
   * The share of rows at or below {@code wanted} — by bucket interpolation when there is a
   * histogram, uniformly between {@code min} and {@code max} otherwise, and null when the value
   * cannot be placed on a line at all.
   */
  private static @Nullable Double shareBelow(
      ColumnStatistics statistics, Comparable<?> wanted, @Nullable Double rows) {
    if (!statistics.getHistogram().getBucketsList().isEmpty() && rows != null && rows > 0) {
      Double share = histogramShare(statistics.getHistogram().getBucketsList(), wanted, rows);
      if (share != null) {
        return share;
      }
    }

    Double low = LiteralValues.position(LiteralValues.of(statistics.getMin()));
    Double high = LiteralValues.position(LiteralValues.of(statistics.getMax()));
    Double at = LiteralValues.position(wanted);
    if (low == null || high == null || at == null || high <= low) {
      return null;
    }

    return (at - low) / (high - low);
  }

  /** The share of rows at or below {@code wanted}, from the equi-height buckets. */
  private static @Nullable Double histogramShare(
      List<HistogramBucket> buckets, Comparable<?> wanted, double rows) {
    Double at = LiteralValues.position(wanted);
    if (at == null) {
      return null;
    }

    double below = 0;
    double previousUpper = Double.NEGATIVE_INFINITY;
    for (HistogramBucket bucket : buckets) {
      Double upper = LiteralValues.position(LiteralValues.of(bucket.getUpper()));
      if (upper == null) {
        return null;
      }

      if (at >= upper) {
        below += bucket.getCount();
        previousUpper = upper;
        continue;
      }

      // Inside this bucket: interpolate linearly across it, which is what "equi-height" buys.
      double span = upper - previousUpper;
      double within = span <= 0 || Double.isInfinite(previousUpper) ? 0.5 : (at - previousUpper) / span;
      below += bucket.getCount() * Math.max(0, Math.min(1, within));
      return below / rows;
    }

    return below / rows;
  }

  /**
   * A hint's value in the <b>column's</b> own units, or null when it cannot be placed there.
   *
   * <p>This is the coercion design 49 §2 asks for, and it is the same one a literal bound gets: a
   * hint arrives typed by the parameter the statement inferred and is compared against statistics
   * typed by the column, and the two need not agree on scale or precision. An {@code int} hint
   * against a {@code DECIMAL(18,2)} column is a hundred times its own unscaled value; a millisecond
   * timestamp hint against a nanosecond column is a million times its own. Getting that wrong would
   * not produce a slightly worse estimate — it would produce one off by orders of magnitude.
   *
   * <p>Outside the numeric and temporal kinds there is nothing to rescale, so a hint of the same
   * kind passes through and a hint of another is declined.
   */
  private static @Nullable Comparable<?> hinted(ParameterHints.Hint hint, Type columnType) {
    Literal literal = hint.literal();
    if (literal == null) {
      return null;
    }

    Comparable<?> value = LiteralValues.of(literal);
    if (value == null) {
      return null;
    }

    java.math.BigDecimal real = real(value, hint.type());
    if (real == null) {
      // Not a kind with units: a string, a binary string, a UUID, a boolean. Comparable only
      // against a column of the same kind.
      return hint.type().getKind() == columnType.getKind() ? value : null;
    }

    return units(real, columnType);
  }

  /** A value in its type's units, as the number it actually denotes. */
  private static java.math.@Nullable BigDecimal real(Comparable<?> value, Type type) {
    return switch (type.getKind()) {
      case TYPE_KIND_I8, TYPE_KIND_I16, TYPE_KIND_I32, TYPE_KIND_I64, TYPE_KIND_DATE,
              TYPE_KIND_TIME, TYPE_KIND_INTERVAL_DAY, TYPE_KIND_INTERVAL_YEAR ->
          value instanceof Long number ? java.math.BigDecimal.valueOf(number) : null;
      case TYPE_KIND_FP32, TYPE_KIND_FP64 ->
          value instanceof Double number ? java.math.BigDecimal.valueOf(number) : null;
      case TYPE_KIND_DECIMAL ->
          value instanceof java.math.BigInteger unscaled
              ? new java.math.BigDecimal(unscaled, type.getScale())
              : null;
      // The IR counts a timestamp in Type.precision units of a second, so seconds is the one
      // vocabulary two timestamps of different precisions share.
      case TYPE_KIND_TIMESTAMP, TYPE_KIND_TIMESTAMP_TZ ->
          value instanceof Long number
              ? java.math.BigDecimal.valueOf(number)
                  .divide(
                      java.math.BigDecimal.valueOf(
                          LiteralConverter.unitsPerSecond(type.getPrecision())))
              : null;
      default -> null;
    };
  }

  /** The inverse: a real value back into {@code type}'s units, as the estimator compares them. */
  private static @Nullable Comparable<?> units(java.math.BigDecimal real, Type type) {
    try {
      return switch (type.getKind()) {
        case TYPE_KIND_I8, TYPE_KIND_I16, TYPE_KIND_I32, TYPE_KIND_I64, TYPE_KIND_DATE,
                TYPE_KIND_TIME, TYPE_KIND_INTERVAL_DAY, TYPE_KIND_INTERVAL_YEAR ->
            real.setScale(0, java.math.RoundingMode.HALF_EVEN).longValueExact();
        case TYPE_KIND_FP32, TYPE_KIND_FP64 -> real.doubleValue();
        case TYPE_KIND_DECIMAL ->
            real.setScale(type.getScale(), java.math.RoundingMode.HALF_EVEN).unscaledValue();
        case TYPE_KIND_TIMESTAMP, TYPE_KIND_TIMESTAMP_TZ ->
            real.multiply(
                    java.math.BigDecimal.valueOf(
                        LiteralConverter.unitsPerSecond(type.getPrecision())))
                .setScale(0, java.math.RoundingMode.HALF_EVEN)
                .longValueExact();
        default -> null;
      };
    } catch (ArithmeticException e) {
      // A hint too large for the column it is compared against says nothing this estimator can use.
      return null;
    }
  }

  private static @Nullable ColumnStatistics statisticsOf(
      RexNode node, ChalkTable table, List<Integer> fields) {
    int column = columnOf(node, fields);
    return column < 0 ? null : table.statistics(column);
  }

  /** The table column a predicate's input ref names, or -1. */
  private static int columnOf(RexNode node, List<Integer> fields) {
    if (!(node instanceof RexInputRef ref)) {
      return -1;
    }

    int field = ref.getIndex();
    return field < 0 || field >= fields.size() ? -1 : fields.get(field);
  }

  private static Estimate guess(RexNode conjunct) {
    return new Estimate(RelMdUtil.guessSelectivity(conjunct), true);
  }

  private static double clamp(double value) {
    return Math.max(1e-9, Math.min(1.0, value));
  }

  /** Reads IR literals as values the estimator can compare and place on a line. */
  static final class LiteralValues {
    private LiteralValues() {}

    /** The literal's value, or null when it is unset or a kind with no ordering. */
    static @Nullable Comparable<?> of(Literal literal) {
      if (literal == null) {
        return null;
      }

      return switch (literal.getValueCase()) {
        case BOOL_VALUE -> literal.getBoolValue();
        case I8_VALUE -> (long) literal.getI8Value();
        case I16_VALUE -> (long) literal.getI16Value();
        case I32_VALUE -> (long) literal.getI32Value();
        case I64_VALUE -> literal.getI64Value();
        case FP32_VALUE -> (double) literal.getFp32Value();
        case FP64_VALUE -> literal.getFp64Value();
        case STRING_VALUE -> literal.getStringValue();
        case DATE_VALUE -> (long) literal.getDateValue();
        case TIME_VALUE -> literal.getTimeValue();
        case TIMESTAMP_VALUE -> literal.getTimestampValue();
        case TIMESTAMP_TZ_VALUE -> literal.getTimestampTzValue();
        case INTERVAL_DAY_VALUE -> literal.getIntervalDayValue();
        case INTERVAL_YEAR_VALUE -> (long) literal.getIntervalYearValue();
        case DECIMAL_VALUE ->
            new java.math.BigInteger(literal.getDecimalValue().getUnscaled().toByteArray());
        default -> null;
      };
    }

    /**
     * Where a value sits on the number line, for interpolation. Declines for anything without a
     * meaningful distance — strings, binary, UUIDs — which then falls back to a guess.
     */
    static @Nullable Double position(@Nullable Comparable<?> value) {
      return switch (value) {
        case Long number -> (double) number;
        case Integer number -> (double) number;
        case Double number -> number;
        case java.math.BigDecimal number -> number.doubleValue();
        case java.math.BigInteger number -> number.doubleValue();
        case null, default -> null;
      };
    }

    /** Whether two IR literal values are the same value. */
    @SuppressWarnings({"rawtypes", "unchecked"})
    static boolean equal(Comparable<?> left, Comparable<?> right) {
      Double leftPosition = position(left);
      Double rightPosition = position(right);
      if (leftPosition != null && rightPosition != null) {
        return leftPosition.doubleValue() == rightPosition.doubleValue();
      }

      try {
        return ((Comparable) left).compareTo(right) == 0;
      } catch (ClassCastException e) {
        return false;
      }
    }
  }
}
