package chalk.planner.entitlement;

import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexSimplify;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.util.ImmutableBitSet;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The child's rules, decided on the parent's side of a {@code through} join
 * (docs/design/16-entitlements.md §3.13, D228; owner: "for scalability reasons").
 *
 * <p>The child's descriptor is converted over {@code SELECT … FROM <child>, <parent…>}, so a rule
 * condition's references split into two halves by where they point: the <b>role half</b>, over the
 * parent's columns — "the roles held in the row's tenancy" — and the <b>{@code When} half</b>, over
 * the child's own. The role half is evaluated <em>once per parent row</em>, at the parent's
 * cardinality, and the child reads its answer as a column.
 *
 * <p>Two shapes, exactly as D228 states them.
 *
 * <ul>
 *   <li>Every rule of a list decided on the parent side and none carrying a {@code When}: the parent
 *       projects <b>one ordinal</b> per rule list — the number of the first rule whose role half
 *       holds, or a sentinel — and the child's sanitiser is a switch on it. A realm is one rule list
 *       however many columns carry it, so it is one column for all of them.
 *   <li>Otherwise: <b>one boolean per rule</b> for its role half, and the child's {@code CASE} reads
 *       {@code r_i AND when_i} in order. A rule that reads no parent column at all — the
 *       resource-owner fail-safe is one — keeps its own condition and takes no column, so it still
 *       decides a row whose parent did not match.
 * </ul>
 *
 * <p><b>Splitting a condition is by conjunct, and it fails closed.</b> A top-level conjunct reading
 * only parent {@code k}'s columns is the role half; one reading only the child's is the {@code When}
 * half; one that mixes is reduced to those of its <em>disjuncts</em> that read parent {@code k}
 * alone, and to FALSE when it has none. Dropping conjuncts that are re-applied as the {@code When}
 * half changes nothing; dropping disjuncts can only make a rule fire less often. So
 * {@code role ∧ when} implies the condition the descriptor wrote, for every parent, which is the
 * direction that matters — and across several parents the OR of the role halves restores exactly
 * what a policy compiled per parent meant (§3.13: any path grants).
 */
final class VerdictColumns {

  /** How wide one parent side is before its verdicts: the key, then the match marker. */
  static final int FIXED = 2;

  private VerdictColumns() {}

  /**
   * What one child leaf's rules come to.
   *
   * @param verdicts per parent side, the expressions it projects — over <em>that parent's</em> own
   *     row type — all sides taking the same shape so the child can read them positionally
   * @param conditions the child's rule conditions restated over the joined row, keyed by the
   *     column's table ordinal and the rule's index
   */
  record Plan(ImmutableList<ImmutableList<RexNode>> verdicts, Map<Long, RexNode> conditions) {

    /**
     * How wide one <em>emitted</em> side is. The maximum rather than the first side's, because a
     * side that reaches no row at all is emitted nowhere and takes no slot (D269 (a)); every side
     * that is emitted takes the same number, which is what lets the child read one position.
     */
    int width() {
      int width = 0;
      for (ImmutableList<RexNode> side : verdicts) {
        width = Math.max(width, side.size());
      }
      return width;
    }

    /** Whether every side's verdicts are constants, so no side has to project one at all. */
    boolean constant() {
      for (ImmutableList<RexNode> side : verdicts) {
        for (RexNode verdict : side) {
          if (!(verdict instanceof org.apache.calcite.rex.RexLiteral)) {
            return false;
          }
        }
      }
      return true;
    }

    @Nullable RexNode conditionOf(int tableColumn, int rule) {
      return conditions.get(key(tableColumn, rule));
    }
  }

  private static long key(int tableColumn, int rule) {
    return ((long) tableColumn << 32) | (rule & 0xffffffffL);
  }

  /**
   * Computes the plan.
   *
   * @param descriptor the child's descriptor, converted over {@code [child | parent blocks]}
   * @param childWidth how many columns the child has
   * @param blocks per parent side, the (start, width) of its block in the converted row — two sides
   *     onto one parent table share one block, which is what makes the same parent through two
   *     columns two joins to two occurrences of one set of rules
   * @param offsets per parent side, where its own columns start in the joined row, and <b>negative
   *     for a side the leaf does not emit</b> — a path that reaches no row, or a join elision. Such
   *     a side takes no slot and contributes nothing: there is no column to read it from, and a
   *     route that reaches no row grants nothing along it (D269 (a))
   */
  static Plan of(
      DescriptorExpressions descriptor,
      int childWidth,
      List<int[]> blocks,
      List<Integer> offsets,
      RexBuilder rexBuilder,
      RexSimplify simplify,
      List<RexSimplify> underParent) {
    int sides = blocks.size();
    int live = 0;
    for (int offset : offsets) {
      live += offset < 0 ? 0 : 1;
    }
    List<List<RexNode>> verdicts = new ArrayList<>(sides);
    for (int k = 0; k < sides; k++) {
      verdicts.add(new ArrayList<>());
    }
    Map<Long, RexNode> conditions = new LinkedHashMap<>();

    // A realm is one rule list however many columns carry it, and layer A spells that as identical
    // rule lists on each of them — so the signature is the list of converted conditions, and the
    // columns that share one share a verdict.
    Map<String, List<DescriptorExpressions.Column>> groups = new LinkedHashMap<>();
    for (DescriptorExpressions.Column column : descriptor.columns()) {
      if (!readsParent(column, childWidth)) {
        continue;
      }
      groups.computeIfAbsent(signature(column), any -> new ArrayList<>()).add(column);
    }

    for (List<DescriptorExpressions.Column> group : groups.values()) {
      DescriptorExpressions.Column first = group.get(0);
      List<Split> splits = new ArrayList<>(first.rules().size());
      boolean everyRuleOnTheParent = true;
      for (int i = 0; i < first.rules().size(); i++) {
        Split split = split(first.condition(i), childWidth, blocks, rexBuilder, underParent);
        splits.add(split);
        everyRuleOnTheParent &=
            split.decidedByTheParent() && split.whenHalf() == null && split.childHalf() == null;
      }

      List<RexNode> rewritten =
          everyRuleOnTheParent
              ? ordinal(splits, verdicts, offsets, live, rexBuilder, simplify)
              : booleans(splits, verdicts, offsets, live, rexBuilder, simplify);
      for (DescriptorExpressions.Column column : group) {
        for (int i = 0; i < rewritten.size(); i++) {
          conditions.put(key(column.tableColumn(), i), rewritten.get(i));
        }
      }
    }

    ImmutableList.Builder<ImmutableList<RexNode>> built = ImmutableList.builder();
    for (List<RexNode> side : verdicts) {
      built.add(ImmutableList.copyOf(side));
    }
    return new Plan(built.build(), conditions);
  }

  // ------------------------------------------------------------------ the two shapes

  /**
   * One ordinal per rule list: the number of the first rule whose role half holds, or a sentinel
   * one past the last rule. The child's condition for rule {@code i} is then {@code v = i + 1},
   * which is first-match-wins by construction — at most one of them can hold.
   *
   * <p><b>Across several routes</b> (D269 (a), design 38 §4): the least ordinal wins, which is
   * D222's precedence applied across the routes that reach the row. A side the leaf does not emit
   * reaches no row and is the sentinel outright; an emitted side projects its ordinal, so a route
   * that matched nothing is the NULL its LEFT JOIN gives it and is read as the sentinel too.
   */
  private static List<RexNode> ordinal(
      List<Split> splits,
      List<List<RexNode>> verdicts,
      List<Integer> offsets,
      int live,
      RexBuilder rexBuilder,
      RexSimplify simplify) {
    RelDataType type = rexBuilder.getTypeFactory().createSqlType(SqlTypeName.INTEGER);
    RexNode sentinel = rexBuilder.makeLiteral(splits.size() + 1, type);

    List<RexNode> perSide = new ArrayList<>(verdicts.size());
    boolean anyVaries = false;
    for (int k = 0; k < verdicts.size(); k++) {
      if (offsets.get(k) < 0) {
        perSide.add(sentinel);
        continue;
      }
      List<RexNode> operands = new ArrayList<>();
      for (int i = 0; i < splits.size(); i++) {
        RexNode role = splits.get(i).roleHalf().get(k);
        if (role.isAlwaysFalse()) {
          continue;
        }
        RexNode value = rexBuilder.makeLiteral(i + 1, type);
        if (role.isAlwaysTrue()) {
          operands.add(value);
          break;
        }
        operands.add(role);
        operands.add(value);
      }
      RexNode expression =
          operands.isEmpty()
              ? sentinel
              : operands.size() % 2 == 1
                  // The loop stopped on a rule that holds for every row of this route, so the last
                  // operand is that rule's ordinal and it is already the ELSE arm.
                  ? operands.size() == 1
                      ? operands.get(0)
                      : rexBuilder.makeCall(SqlStdOperatorTable.CASE, operands)
                  : rexBuilder.makeCall(SqlStdOperatorTable.CASE, appendElse(operands, sentinel));
      RexNode folded = simplify.simplify(expression);
      anyVaries |= !(folded instanceof org.apache.calcite.rex.RexLiteral);
      perSide.add(folded);
    }

    // Uniform across the emitted sides: a literal where it folded and one route reaches the row, so
    // the child reads one position whatever each route's own rules came to. Where **several**
    // routes reach it, every one of them projects — a constant that is the verdict of the rows the
    // route reached says nothing about a row it did not reach, and the NULL is what says so.
    List<RexNode> refs = new ArrayList<>(verdicts.size());
    boolean project = anyVaries || live > 1;
    for (int k = 0; k < verdicts.size(); k++) {
      if (offsets.get(k) < 0) {
        refs.add(sentinel);
        continue;
      }
      if (!project) {
        refs.add(perSide.get(k));
        continue;
      }
      int slot = verdicts.get(k).size();
      verdicts.get(k).add(perSide.get(k));
      refs.add(
          reaching(
              rexBuilder.makeInputRef(
                  nullable(rexBuilder, perSide.get(k).getType()),
                  offsets.get(k) + FIXED + slot),
              sentinel,
              rexBuilder));
    }
    RexNode combined = least(refs, rexBuilder);

    List<RexNode> conditions = new ArrayList<>(splits.size());
    for (int i = 0; i < splits.size(); i++) {
      conditions.add(
          simplify.simplifyUnknownAsFalse(
              rexBuilder.makeCall(
                  SqlStdOperatorTable.EQUALS,
                  combined,
                  rexBuilder.makeLiteral(i + 1, type))));
    }
    return conditions;
  }

  /**
   * One boolean per rule for its role half, and the child's condition {@code r_i AND when_i}. A rule
   * that reads no parent column keeps its own condition and takes no column: it decides a row whose
   * parent did not match, which is what the resource-owner fail-safe is for.
   *
   * <p>A side the leaf does not emit contributes nothing, which is exactly FALSE under the OR the
   * halves compose to; and where several routes reach the row every one of them projects, so a
   * route that matched nothing reads NULL and {@code IS TRUE} makes it the FALSE it is.
   */
  private static List<RexNode> booleans(
      List<Split> splits,
      List<List<RexNode>> verdicts,
      List<Integer> offsets,
      int live,
      RexBuilder rexBuilder,
      RexSimplify simplify) {
    List<RexNode> conditions = new ArrayList<>(splits.size());
    for (Split split : splits) {
      if (!split.decidedByTheParent()) {
        conditions.add(split.original());
        continue;
      }

      boolean anyVaries = false;
      for (int k = 0; k < verdicts.size(); k++) {
        if (offsets.get(k) < 0) {
          continue;
        }
        RexNode role = split.roleHalf().get(k);
        anyVaries |= !role.isAlwaysTrue() && !role.isAlwaysFalse();
      }
      boolean project = anyVaries || live > 1;

      List<RexNode> held = new ArrayList<>(verdicts.size());
      for (int k = 0; k < verdicts.size(); k++) {
        if (offsets.get(k) < 0) {
          continue;
        }
        RexNode role = split.roleHalf().get(k);
        if (!project) {
          held.add(role);
          continue;
        }
        int slot = verdicts.get(k).size();
        verdicts.get(k).add(role);
        held.add(
            rexBuilder.makeCall(
                SqlStdOperatorTable.IS_TRUE,
                rexBuilder.makeInputRef(
                    nullable(rexBuilder, role.getType()), offsets.get(k) + FIXED + slot)));
      }

      if (split.childHalf() != null) {
        held.add(split.childHalf());
      }
      RexNode any =
          held.isEmpty()
              ? rexBuilder.makeLiteral(false)
              : RexUtil.composeDisjunction(rexBuilder, held);
      RexNode whole =
          split.whenHalf() == null
              ? any
              : rexBuilder.makeCall(SqlStdOperatorTable.AND, any, split.whenHalf());
      conditions.add(simplify.simplifyUnknownAsFalse(whole));
    }
    return conditions;
  }

  /**
   * One route's ordinal as the meet reads it: the value where the route matched, and the sentinel
   * where the LEFT JOIN left it NULL (D269 (a)).
   */
  private static RexNode reaching(RexNode ordinal, RexNode sentinel, RexBuilder rexBuilder) {
    return rexBuilder.makeCall(SqlStdOperatorTable.COALESCE, ordinal, sentinel);
  }

  /**
   * The least of several ordinals, which is first match winning across the routes that reach the
   * row (design 38 §4 as D269 (a) amends it). Written as the nested {@code CASE} on {@code >=} the
   * report's own meet uses ({@link DisclosureColumns}), because {@code LEAST} is a library function
   * the IR does not carry. Every operand is already the sentinel where its route matched nothing,
   * so there is no NULL left for the comparison to answer UNKNOWN on.
   */
  private static RexNode least(List<RexNode> values, RexBuilder rexBuilder) {
    RexNode least = values.get(0);
    for (int i = 1; i < values.size(); i++) {
      RexNode other = values.get(i);
      least =
          rexBuilder.makeCall(
              SqlStdOperatorTable.CASE,
              rexBuilder.makeCall(SqlStdOperatorTable.GREATER_THAN_OR_EQUAL, least, other),
              other,
              least);
    }
    return least;
  }

  private static List<RexNode> appendElse(List<RexNode> operands, RexNode otherwise) {
    List<RexNode> whole = new ArrayList<>(operands);
    whole.add(otherwise);
    return whole;
  }

  private static RelDataType nullable(RexBuilder rexBuilder, RelDataType type) {
    return rexBuilder.getTypeFactory().createTypeWithNullability(type, true);
  }

  // ------------------------------------------------------------------ the split

  /** One rule condition, split by where its references point. */
  private record Split(
      RexNode original,
      /** Per parent side, the half decidable there, over that parent's own row type. */
      List<RexNode> roleHalf,
      @Nullable RexNode whenHalf,
      /**
       * The half of a <em>mixed</em> conjunct the child decides, which is OR-ed with the sides
       * rather than AND-ed (D265 §4). A table that holds one perspective directly and another along
       * a path writes one condition reading both, and dropping the child's disjuncts would take the
       * direct perspective away; null where no conjunct mixes, and null where none of them left
       * anything on the child, which is the same thing as FALSE under an OR.
       */
      @Nullable RexNode childHalf,
      boolean decidedByTheParent) {}

  private static Split split(
      RexNode condition,
      int childWidth,
      List<int[]> blocks,
      RexBuilder rexBuilder,
      List<RexSimplify> underParent) {
    ImmutableBitSet bits = RelOptUtil.InputFinder.bits(condition);
    boolean readsParent = false;
    for (int bit : bits) {
      readsParent |= bit >= childWidth;
    }
    if (!readsParent) {
      return new Split(condition, List.of(), null, null, false);
    }

    List<RexNode> child = new ArrayList<>();
    List<RexNode> mixed = new ArrayList<>();
    List<List<RexNode>> perSide = new ArrayList<>(blocks.size());
    for (int k = 0; k < blocks.size(); k++) {
      perSide.add(new ArrayList<>());
    }

    for (RexNode conjunct : RelOptUtil.conjunctions(condition)) {
      ImmutableBitSet used = RelOptUtil.InputFinder.bits(conjunct);
      if (!used.isEmpty() && used.asList().get(used.asList().size() - 1) < childWidth) {
        child.add(conjunct);
        continue;
      }
      for (int k = 0; k < blocks.size(); k++) {
        perSide.get(k).add(forSide(conjunct, used, blocks.get(k), rexBuilder));
      }
      mixed.add(forChild(conjunct, childWidth, rexBuilder));
    }

    List<RexNode> roleHalf = new ArrayList<>(blocks.size());
    for (int k = 0; k < blocks.size(); k++) {
      int[] block = blocks.get(k);
      RexNode role = RexUtil.composeConjunction(rexBuilder, perSide.get(k));
      // Over the parent's own row type: the block's columns, renumbered from zero.
      RexNode local =
          role.accept(
              new RexShuttle() {
                @Override
                public RexNode visitInputRef(RexInputRef ref) {
                  return rexBuilder.makeInputRef(ref.getType(), ref.getIndex() - block[0]);
                }
              });
      // Under that parent's own Filter_R (§3.3 applied to the parent): a manager whose threads are
      // all in one organisation gets a constant verdict, and a constant verdict is no column at all.
      roleHalf.add(underParent.get(k).simplifyUnknownAsFalse(local));
    }

    // What the child itself decides of the mixed conjuncts, OR-ed with the sides below. FALSE is
    // exactly nothing under an OR, so it is reported as no half at all — which is what keeps a
    // `through` child, whose conditions read the parent alone, taking the ordinal shape it did.
    RexNode childHalf = mixed.isEmpty() ? null : RexUtil.composeConjunction(rexBuilder, mixed);
    if (childHalf != null && childHalf.isAlwaysFalse()) {
      childHalf = null;
    }

    return new Split(
        condition,
        roleHalf,
        child.isEmpty() ? null : RexUtil.composeConjunction(rexBuilder, child),
        childHalf,
        true);
  }

  /**
   * One conjunct as the <em>child</em> can decide it: the disjunction of those of its disjuncts that
   * read no column beyond the child's own row — a context scalar among them, which reads none at all
   * — and FALSE where it has none.
   *
   * <p>Sound in the same direction the rest of the split is: a disjunct implies the disjunction, so
   * what is kept implies the condition the descriptor wrote, and a rule can only fire less often.
   */
  private static RexNode forChild(RexNode conjunct, int childWidth, RexBuilder rexBuilder) {
    List<RexNode> kept = new ArrayList<>();
    for (RexNode disjunct : RelOptUtil.disjunctions(conjunct)) {
      ImmutableBitSet reads = RelOptUtil.InputFinder.bits(disjunct);
      boolean ours = true;
      for (int bit : reads) {
        ours &= bit < childWidth;
      }
      if (ours) {
        kept.add(disjunct);
      }
    }
    return kept.isEmpty()
        ? rexBuilder.makeLiteral(false)
        : RexUtil.composeDisjunction(rexBuilder, kept);
  }

  /**
   * One conjunct as parent {@code block} can decide it: itself where it reads that block alone, and
   * otherwise the disjunction of those of its disjuncts that do — FALSE where it has none, which is
   * a rule that cannot fire through this path.
   */
  private static RexNode forSide(
      RexNode conjunct, ImmutableBitSet used, int[] block, RexBuilder rexBuilder) {
    if (within(used, block)) {
      return conjunct;
    }
    List<RexNode> kept = new ArrayList<>();
    for (RexNode disjunct : RelOptUtil.disjunctions(conjunct)) {
      ImmutableBitSet reads = RelOptUtil.InputFinder.bits(disjunct);
      if (!reads.isEmpty() && within(reads, block)) {
        kept.add(disjunct);
      }
    }
    return kept.isEmpty()
        ? rexBuilder.makeLiteral(false)
        : RexUtil.composeDisjunction(rexBuilder, kept);
  }

  private static boolean within(ImmutableBitSet used, int[] block) {
    for (int bit : used) {
      if (bit < block[0] || bit >= block[0] + block[1]) {
        return false;
      }
    }
    return true;
  }

  private static boolean readsParent(DescriptorExpressions.Column column, int childWidth) {
    for (int i = 0; i < column.rules().size(); i++) {
      for (int bit : RelOptUtil.InputFinder.bits(column.condition(i))) {
        if (bit >= childWidth) {
          return true;
        }
      }
    }
    return false;
  }

  private static String signature(DescriptorExpressions.Column column) {
    StringBuilder text = new StringBuilder();
    for (int i = 0; i < column.rules().size(); i++) {
      text.append(column.condition(i)).append(' ');
    }
    return text.toString();
  }
}
