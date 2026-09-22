package chalk.planner.plan;

import com.google.common.collect.ImmutableList;
import java.util.ArrayList;

import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexDynamicParam;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.type.SqlTypeFamily;
import org.apache.calcite.sql.type.SqlTypeName;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Splits a filter condition into the part an index can answer and the part that is left over
 * (D37, {@code 11-m2-index-support.md} §4).
 *
 * <p>The residual is the load-bearing half. A conjunct this class consumes disappears from the
 * filter above the lookup, so consuming one it cannot actually enforce is a silent wrong-answer bug.
 * Everything it does not understand — {@code IS NULL}, a top-level {@code OR} that is not an IN
 * list, {@code LIKE}, a cast on either side, a comparison between two columns — stays residual, and
 * {@code IndexMatcherTest} asserts consumed against residual for every shape.
 */
public final class IndexMatcher {
  private IndexMatcher() {}

  /**
   * One bound range over a prefix of the index's key columns. All but the last bound column are
   * equalities; only the last may be open, and an empty list on a side means unbounded there.
   */
  public record Range(
      ImmutableList<RexNode> lower,
      boolean lowerInclusive,
      ImmutableList<RexNode> upper,
      boolean upperInclusive,
      boolean prefix) {

    /** An ordinary bounded range: the four bounds, and no prefix (D37). */
    public Range(
        ImmutableList<RexNode> lower,
        boolean lowerInclusive,
        ImmutableList<RexNode> upper,
        boolean upperInclusive) {
      this(lower, lowerInclusive, upper, upperInclusive, false);
    }

    /** How many leading key columns this range constrains at all. */
    public int boundedColumns() {
      return Math.max(lower.size(), upper.size());
    }

    /** True when the range pins every bound column to one value. */
    public boolean isPoint() {
      return lowerInclusive
          && upperInclusive
          && !lower.isEmpty()
          && lower.size() == upper.size()
          && lower.equals(upper);
    }

    @Override
    public String toString() {
      return describe(java.util.function.UnaryOperator.identity());
    }

    /**
     * The same text with each bound passed through {@code render} first. One renderer, two callers:
     * {@link #toString} renders the bounds as they are, and a redacted plan text renders them as
     * pseudonyms (D262) — a range's bounds are the statement's own literals, and a plan text that
     * showed them would be exactly the half-safe log line design 37 exists to stop.
     */
    public String describe(java.util.function.UnaryOperator<RexNode> render) {
      if (prefix) {
        return "prefix" + render(lower, render);
      }

      String low = lower.isEmpty() ? "-inf" : render(lower, render);
      String high = upper.isEmpty() ? "+inf" : render(upper, render);
      return (lowerInclusive ? "[" : "(") + low + ", " + high + (upperInclusive ? "]" : ")");
    }

    private static String render(
        ImmutableList<RexNode> bound, java.util.function.UnaryOperator<RexNode> render) {
      StringBuilder text = new StringBuilder("[");
      for (int i = 0; i < bound.size(); i++) {
        if (i > 0) {
          text.append(", ");
        }
        text.append(render.apply(bound.get(i)));
      }
      return text.append(']').toString();
    }
  }

  /**
   * What an index can and cannot answer about one condition.
   *
   * @param ranges the ranges to union; empty means the index is no use here
   * @param residual what still has to be checked per row, or null when nothing does
   */
  public record Result(ImmutableList<Range> ranges, @Nullable RexNode residual) {
    public static final Result NONE = new Result(ImmutableList.of(), null);

    public boolean matched() {
      return !ranges.isEmpty();
    }
  }

  /** What shapes an index answers, which is its kind read as a question about conditions. */
  public enum Shape {
    /** Ranges on the last bound column, and a {@code LIKE} prefix there (ORDERED, CLUSTERED). */
    RANGES,
    /** The whole key, pinned, and nothing else (HASH). */
    EQUALITY,
    /** A {@code LIKE} prefix on the one key column, and nothing else (PREFIX). */
    PREFIX,
  }

  /**
   * Splits {@code condition} against an index whose key columns are, in key order, the given field
   * positions of the row {@code condition} is written against. A key column the row does not carry
   * is given as -1 and truncates the usable key there.
   */
  public static Result split(
      RexNode condition,
      List<Integer> keyFields,
      RelDataType rowType,
      boolean equalityOnly,
      RexBuilder rexBuilder) {
    return split(condition, keyFields, rowType, equalityOnly, rexBuilder, List.of());
  }

  /**
   * The same, for an index whose key columns are not all ascending.
   *
   * <p>A range's {@code lower} and {@code upper} are bounds <em>in the index's own key order</em> —
   * which is what {@code IndexKeyRange.Contains} compares and what a source binary-searches with —
   * so on a descending key column the {@code >} bound is the range's upper bound and the {@code <}
   * bound its lower one. Everything above the bound column is an equality and unaffected (D281).
   *
   * @param descending one flag per key column, in key order; shorter or empty means ascending
   */
  public static Result split(
      RexNode condition,
      List<Integer> keyFields,
      RelDataType rowType,
      boolean equalityOnly,
      RexBuilder rexBuilder,
      List<Boolean> descending) {
    return split(
        condition,
        keyFields,
        rowType,
        equalityOnly ? Shape.EQUALITY : Shape.RANGES,
        rexBuilder,
        descending);
  }

  /** The same, for an index whose kind is not one of the two the boolean can say (D282). */
  public static Result split(
      RexNode condition,
      List<Integer> keyFields,
      RelDataType rowType,
      Shape shape,
      RexBuilder rexBuilder,
      List<Boolean> descending) {
    boolean equalityOnly = shape == Shape.EQUALITY;
    // SEARCH first: a BETWEEN arrives as a Sarg, and expanding it gives back the two comparisons
    // this classifier understands. RexToIr expands the same way, so a residual built from the
    // expanded form produces exactly the IR the unexpanded one would have.
    RexNode expanded = RexUtil.expandSearch(rexBuilder, null, condition);
    List<RexNode> conjuncts = RelOptUtil.conjunctions(expanded);

    int keyLength = usableKeyLength(keyFields);
    if (keyLength == 0 || conjuncts.isEmpty()) {
      return Result.NONE;
    }

    // A hash index locates a whole key or nothing, so a key column the row does not carry does not
    // shorten its key — it makes the index unusable here. Truncating would produce a lookup over a
    // prefix, which is a bucket no hash index has (D280).
    if (equalityOnly && keyLength != keyFields.size()) {
      return Result.NONE;
    }

    // Classify every conjunct against the key column it constrains, if any.
    List<@Nullable RexNode> equality = new ArrayList<>(keyLength);
    List<@Nullable RexNode> equalityConjunct = new ArrayList<>(keyLength);
    List<Bound> lowerBound = new ArrayList<>(keyLength);
    List<Bound> upperBound = new ArrayList<>(keyLength);
    List<Bound> patternBound = new ArrayList<>(keyLength);
    for (int i = 0; i < keyLength; i++) {
      equality.add(null);
      equalityConjunct.add(null);
      lowerBound.add(null);
      upperBound.add(null);
      patternBound.add(null);
    }

    List<RexNode> inList = null;
    RexNode inListConjunct = null;
    Set<RexNode> consumed = new LinkedHashSet<>();

    for (RexNode conjunct : conjuncts) {
      Comparison comparison = classify(conjunct, keyFields, keyLength, rowType);
      if (comparison == null) {
        int pattern = shape == Shape.EQUALITY ? -1 : prefixPattern(conjunct, keyFields, keyLength, rowType);
        if (pattern >= 0 && patternBound.get(pattern) == null) {
          // `col LIKE 'p%'` on a STRING key column: one prefix range, and nothing left to re-check
          // (D282). The pattern travels whole and the client strips its '%' when it binds the bound,
          // because a parameter's text is only known then.
          patternBound.set(
              pattern,
              new Bound(((RexCall) conjunct).getOperands().get(1), true, conjunct));
          continue;
        }

        List<RexNode> values = inListValues(conjunct, keyFields.get(0), rowType);
        if (values != null && inList == null) {
          // Every value of an IN list is an equality, so a hash index can serve one too.
          inList = values;
          inListConjunct = conjunct;
        }

        continue;
      }

      switch (comparison.kind()) {
        case EQUALS -> {
          if (equality.get(comparison.key()) == null) {
            equality.set(comparison.key(), comparison.value());
            equalityConjunct.set(comparison.key(), conjunct);
          }
        }
        case GREATER_THAN, GREATER_THAN_OR_EQUAL -> {
          if (!equalityOnly && lowerBound.get(comparison.key()) == null) {
            lowerBound.set(
                comparison.key(),
                new Bound(
                    comparison.value(), comparison.kind() == SqlKind.GREATER_THAN_OR_EQUAL, conjunct));
          }
        }
        case LESS_THAN, LESS_THAN_OR_EQUAL -> {
          if (!equalityOnly && upperBound.get(comparison.key()) == null) {
            upperBound.set(
                comparison.key(),
                new Bound(
                    comparison.value(), comparison.kind() == SqlKind.LESS_THAN_OR_EQUAL, conjunct));
          }
        }
        default -> {
          // Not a shape this class consumes; it stays residual.
        }
      }
    }

    // Walk the key in order: equalities extend the prefix, and the first column without one may
    // carry at most one lower and one upper bound. Nothing past that column can be used, because
    // the rows a range covers are only contiguous while every earlier column is pinned.
    List<RexNode> prefix = new ArrayList<>();
    int position = 0;

    // An IN list is only usable on the very first key column, and only when no equality already
    // pinned it — `symbol = 'A' AND symbol IN ('A','B')` is a shape the residual can have.
    List<RexNode> leading = null;
    if (inList != null && equality.get(0) == null) {
      leading = inList;
      consumed.add(inListConjunct);
      prefix.add(null); // placeholder, filled per alternative below
      position = 1;
    }

    while (position < keyLength && equality.get(position) != null) {
      prefix.add(equality.get(position));
      consumed.add(equalityConjunct.get(position));
      position++;
    }

    Bound low = position < keyLength ? lowerBound.get(position) : null;
    Bound high = position < keyLength ? upperBound.get(position) : null;

    // A LIKE prefix is used only where the column carries no bound of its own: one range per column
    // is the contract, and a bound is the sharper of the two.
    Bound pattern =
        position < keyLength && low == null && high == null ? patternBound.get(position) : null;

    if (low != null) {
      consumed.add(low.conjunct());
    }

    if (high != null) {
      consumed.add(high.conjunct());
    }

    if (pattern != null) {
      consumed.add(pattern.conjunct());
    }

    if (prefix.isEmpty() && low == null && high == null && pattern == null) {
      return Result.NONE;
    }

    if (equalityOnly && (position < keyLength || low != null || high != null)) {
      // A hash index locates a whole key or nothing: a prefix names no bucket, and neither does a
      // range. Anything less than the full key is left to the scan.
      return Result.NONE;
    }

    if (shape == Shape.PREFIX && (pattern == null || position != 0 || keyLength != 1)) {
      // A prefix index answers prefix ranges on its one key column and nothing else.
      return Result.NONE;
    }

    boolean flip = position < descending.size() && Boolean.TRUE.equals(descending.get(position));
    ImmutableList<Range> ranges =
        pattern != null
            ? prefixRanges(prefix, leading, pattern)
            : ranges(prefix, leading, low, high, flip);
    if (ranges.isEmpty()) {
      return Result.NONE;
    }

    List<RexNode> residual = new ArrayList<>();
    for (RexNode conjunct : conjuncts) {
      if (!consumed.contains(conjunct)) {
        residual.add(conjunct);
      }
    }

    return new Result(
        ranges,
        residual.isEmpty() ? null : RexUtil.composeConjunction(rexBuilder, residual));
  }

  /** The ranges for one prefix, expanded over the leading IN list when there is one. */
  private static ImmutableList<Range> ranges(
      List<RexNode> prefix,
      @Nullable List<RexNode> leading,
      @Nullable Bound low,
      @Nullable Bound high,
      boolean flip) {
    if (leading == null) {
      return ImmutableList.of(range(prefix, low, high, flip));
    }

    ImmutableList.Builder<Range> ranges = ImmutableList.builder();
    for (RexNode value : leading) {
      List<RexNode> keys = new ArrayList<>(prefix);
      keys.set(0, value);
      ranges.add(range(keys, low, high, flip));
    }

    return ranges.build();
  }

  /**
   * The prefix ranges for one equality prefix: the pattern is the last bound, the range is open
   * above, and the flags read as the half-open {@code [p, next(p))} an ORDERED index will be sent.
   */
  private static ImmutableList<Range> prefixRanges(
      List<RexNode> prefix, @Nullable List<RexNode> leading, Bound pattern) {
    if (leading == null) {
      return ImmutableList.of(prefixRange(prefix, pattern));
    }

    ImmutableList.Builder<Range> ranges = ImmutableList.builder();
    for (RexNode value : leading) {
      List<RexNode> keys = new ArrayList<>(prefix);
      keys.set(0, value);
      ranges.add(prefixRange(keys, pattern));
    }

    return ranges.build();
  }

  private static Range prefixRange(List<RexNode> prefix, Bound pattern) {
    ImmutableList.Builder<RexNode> lower = ImmutableList.builder();
    lower.addAll(prefix);
    lower.add(pattern.value());
    return new Range(lower.build(), true, ImmutableList.of(), false, true);
  }

  /**
   * {@code col LIKE p} on a key column whose type is STRING, where {@code p} is a bare prefix
   * pattern — a literal ending in one {@code %} with no other wildcard, or a parameter, whose text
   * the client checks when it binds it. Returns the key position, or -1.
   *
   * <p>{@code ESCAPE} is refused: its three-operand form can hide a wildcard behind an escape
   * character, and a range that consumed such a pattern would drop rows. A case-insensitive
   * {@code LIKE} carries the same {@link SqlKind}, so the operator is checked by name rather than by
   * kind.
   */
  private static int prefixPattern(
      RexNode conjunct, List<Integer> keyFields, int keyLength, RelDataType rowType) {
    if (!(conjunct instanceof RexCall call)
        || call.getKind() != SqlKind.LIKE
        || call.getOperands().size() != 2
        || !"LIKE".equals(call.getOperator().getName())) {
      return -1;
    }

    if (!(call.getOperands().get(0) instanceof RexInputRef ref)) {
      return -1;
    }

    RelDataType columnType = rowType.getFieldList().get(ref.getIndex()).getType();
    if (columnType.getSqlTypeName().getFamily() != SqlTypeFamily.CHARACTER) {
      return -1;
    }

    RexNode pattern = call.getOperands().get(1);
    if (pattern instanceof RexLiteral literal) {
      if (literal.getType().getSqlTypeName().getFamily() != SqlTypeFamily.CHARACTER
          || !isBarePrefix(literal.getValueAs(String.class))) {
        return -1;
      }
    } else if (!(pattern instanceof RexDynamicParam parameter)
        || parameter.getType().getSqlTypeName().getFamily() != SqlTypeFamily.CHARACTER) {
      return -1;
    }

    return keyOf(ref.getIndex(), keyFields, keyLength);
  }

  /**
   * Whether {@code pattern} is {@code p%}: one trailing {@code %}, and no {@code %} or {@code _}
   * anywhere before it. The same rule the client applies to a parameter's text at bind time.
   */
  public static boolean isBarePrefix(@Nullable String pattern) {
    if (pattern == null || pattern.isEmpty() || pattern.charAt(pattern.length() - 1) != '%') {
      return false;
    }

    for (int i = 0; i < pattern.length() - 1; i++) {
      char c = pattern.charAt(i);
      if (c == '%' || c == '_') {
        return false;
      }
    }

    return true;
  }

  private static Range range(
      List<RexNode> prefix, @Nullable Bound low, @Nullable Bound high, boolean flip) {
    // On a descending key column the smaller value comes last, so the bound that opens the range in
    // index order is the `<` one.
    Bound first = flip ? high : low;
    Bound last = flip ? low : high;

    ImmutableList.Builder<RexNode> lower = ImmutableList.builder();
    ImmutableList.Builder<RexNode> upper = ImmutableList.builder();
    lower.addAll(prefix);
    upper.addAll(prefix);

    boolean lowerInclusive = true;
    boolean upperInclusive = true;
    if (first != null) {
      lower.add(first.value());
      lowerInclusive = first.inclusive();
    }

    if (last != null) {
      upper.add(last.value());
      upperInclusive = last.inclusive();
    }

    ImmutableList<RexNode> lowerKeys = lower.build();
    ImmutableList<RexNode> upperKeys = upper.build();
    return new Range(
        lowerKeys.isEmpty() ? ImmutableList.of() : lowerKeys,
        lowerInclusive,
        upperKeys.isEmpty() ? ImmutableList.of() : upperKeys,
        upperInclusive);
  }

  /** Key columns are usable up to the first one the row does not carry. */
  private static int usableKeyLength(List<Integer> keyFields) {
    int length = 0;
    while (length < keyFields.size() && keyFields.get(length) >= 0) {
      length++;
    }

    return length;
  }

  /**
   * {@code col OP value} where col is one of the key columns and value is a literal or a parameter.
   * A cast on either side, a comparison between two columns and anything under a NOT are all
   * refused: the bound has to be a constant of the key column's own type, or the range would not be
   * the set of rows the predicate selects.
   */
  private static @Nullable Comparison classify(
      RexNode conjunct, List<Integer> keyFields, int keyLength, RelDataType rowType) {
    if (!(conjunct instanceof RexCall call) || call.getOperands().size() != 2) {
      return null;
    }

    SqlKind kind = call.getKind();
    RexNode left = call.getOperands().get(0);
    RexNode right = call.getOperands().get(1);

    if (!(left instanceof RexInputRef ref)) {
      if (!(right instanceof RexInputRef swapped) || !isBound(left, swapped, rowType)) {
        return null;
      }

      // `5 < ts` is `ts > 5`; Calcite's own RexUtil.invert does exactly this reversal.
      SqlKind reversed = kind.reverse();
      int key = keyOf(swapped.getIndex(), keyFields, keyLength);
      return key < 0 || !isComparison(reversed) ? null : new Comparison(key, reversed, left);
    }

    if (!isBound(right, ref, rowType) || !isComparison(kind)) {
      return null;
    }

    int key = keyOf(ref.getIndex(), keyFields, keyLength);
    return key < 0 ? null : new Comparison(key, kind, right);
  }

  /**
   * An expanded points-Sarg comes back as a disjunction of equalities on one column. On the first
   * key column that is an IN list: one point range per value. Anywhere else, and for any other
   * disjunction, this returns null and the conjunct stays residual.
   */
  private static @Nullable List<RexNode> inListValues(
      RexNode conjunct, int firstKeyField, RelDataType rowType) {
    if (firstKeyField < 0 || conjunct.getKind() != SqlKind.OR) {
      return null;
    }

    List<RexNode> disjuncts = ((RexCall) conjunct).getOperands();
    List<RexNode> values = new ArrayList<>(disjuncts.size());
    for (RexNode disjunct : disjuncts) {
      if (!(disjunct instanceof RexCall call)
          || call.getKind() != SqlKind.EQUALS
          || call.getOperands().size() != 2) {
        return null;
      }

      RexNode left = call.getOperands().get(0);
      RexNode right = call.getOperands().get(1);
      RexNode value;
      if (left instanceof RexInputRef ref
          && ref.getIndex() == firstKeyField
          && isBound(right, ref, rowType)) {
        value = right;
      } else if (right instanceof RexInputRef ref
          && ref.getIndex() == firstKeyField
          && isBound(left, ref, rowType)) {
        value = left;
      } else {
        return null;
      }

      values.add(value);
    }

    if (values.isEmpty()) {
      return null;
    }

    // Distinct, and in value order when every value is a literal, so the ranges the planner emits
    // are sorted and non-overlapping and the plan does not depend on how the query spelled the list.
    List<RexNode> distinct = new ArrayList<>(new LinkedHashSet<>(values));
    if (distinct.stream().allMatch(RexLiteral.class::isInstance)) {
      try {
        distinct.sort(IndexMatcher::compareLiterals);
      } catch (ClassCastException | NullPointerException | IllegalArgumentException e) {
        // Values that are not mutually comparable keep the order the query gave them, which is
        // still deterministic. Sorting is a nicety; determinism is not.
        distinct = new ArrayList<>(new LinkedHashSet<>(values));
      }
    }

    return distinct;
  }

  @SuppressWarnings({"rawtypes", "unchecked"})
  private static int compareLiterals(RexNode left, RexNode right) {
    Comparable leftValue = ((RexLiteral) left).getValue();
    Comparable rightValue = ((RexLiteral) right).getValue();
    if (leftValue == null || rightValue == null) {
      throw new NullPointerException("a NULL literal is not an IN-list value");
    }

    return leftValue.compareTo(rightValue);
  }

  private static int keyOf(int field, List<Integer> keyFields, int keyLength) {
    for (int i = 0; i < keyLength; i++) {
      if (keyFields.get(i) == field) {
        return i;
      }
    }

    return -1;
  }

  /**
   * Whether {@code node} can bound {@code column}'s key: a literal or a parameter, of the column's
   * own type family — and, for a parameter, of its exact precision and scale as well.
   *
   * <p>A literal may be coarser than the column ({@code TIMESTAMP '…'} against a TIMESTAMP(9)
   * column), because {@code RelToIr} re-expresses it in the column's units. A parameter cannot: the
   * client binds it by the type the plan declares for it, and nothing re-scales that.
   */
  private static boolean isBound(RexNode node, RexInputRef column, RelDataType rowType) {
    SqlTypeName wanted = rowType.getFieldList().get(column.getIndex()).getType().getSqlTypeName();
    if (node instanceof RexLiteral literal) {
      return literal.getType().getSqlTypeName().getFamily() == wanted.getFamily();
    }

    if (!(node instanceof RexDynamicParam parameter)) {
      return false;
    }

    RelDataType columnType = rowType.getFieldList().get(column.getIndex()).getType();
    RelDataType parameterType = parameter.getType();
    return parameterType.getSqlTypeName() == wanted
        && parameterType.getPrecision() == columnType.getPrecision()
        && parameterType.getScale() == columnType.getScale();
  }

  private static boolean isComparison(SqlKind kind) {
    return kind == SqlKind.EQUALS
        || kind == SqlKind.GREATER_THAN
        || kind == SqlKind.GREATER_THAN_OR_EQUAL
        || kind == SqlKind.LESS_THAN
        || kind == SqlKind.LESS_THAN_OR_EQUAL;
  }

  private record Comparison(int key, SqlKind kind, RexNode value) {}

  private record Bound(RexNode value, boolean inclusive, RexNode conjunct) {}
}
