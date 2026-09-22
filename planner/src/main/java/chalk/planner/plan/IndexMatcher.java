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
      boolean upperInclusive) {

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
    for (int i = 0; i < keyLength; i++) {
      equality.add(null);
      equalityConjunct.add(null);
      lowerBound.add(null);
      upperBound.add(null);
    }

    List<RexNode> inList = null;
    RexNode inListConjunct = null;
    Set<RexNode> consumed = new LinkedHashSet<>();

    for (RexNode conjunct : conjuncts) {
      Comparison comparison = classify(conjunct, keyFields, keyLength, rowType);
      if (comparison == null) {
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
    if (low != null) {
      consumed.add(low.conjunct());
    }

    if (high != null) {
      consumed.add(high.conjunct());
    }

    if (prefix.isEmpty() && low == null && high == null) {
      return Result.NONE;
    }

    if (equalityOnly && (position < keyLength || low != null || high != null)) {
      // A hash index locates a whole key or nothing: a prefix names no bucket, and neither does a
      // range. Anything less than the full key is left to the scan.
      return Result.NONE;
    }

    boolean flip = position < descending.size() && Boolean.TRUE.equals(descending.get(position));
    ImmutableList<Range> ranges = ranges(prefix, leading, low, high, flip);
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
