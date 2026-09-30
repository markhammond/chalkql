package chalk.planner.entitlement;

import chalk.planner.plan.ChalkContextMembership;
import chalk.planner.rpc.v1.PolicyRefusalReason;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.CorrelationId;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.hint.RelHint;
import org.apache.calcite.rel.logical.LogicalAggregate;
import org.apache.calcite.rel.logical.LogicalJoin;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexSubQuery;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.util.ImmutableBitSet;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Each distinct membership over a bound list, answered once per leaf in a form a projection can hold
 * (docs/design/16-entitlements.md §3.2, D209; F161).
 *
 * <p>Under execute-time binding nothing folds, so every {@code org_id IN (@ctx.manager_orgs)} in a
 * row predicate <em>and in every rule condition</em> is a sub-query over a {@code BoundTable}. A
 * sub-query cannot stand in a projection the IR can express — that is the wall F36 met, where
 * Calcite's three-valued rewrite needs {@code LITERAL_AGG} — and a sanitiser is a projection.
 *
 * <p>A membership over a <b>whole context list</b> — the one shape the context fold writes — becomes
 * a {@link ChalkContextMembership} call: an expression the executor answers from a hash set of the
 * list, SQL's {@code IN} exactly, which stands wherever the sub-query stood and adds nothing to the
 * leaf. It replaced a {@code LEFT} join per list, which nested the plan one level deeper per list
 * until a wide policy's plan could not be read (F161), and which answered NULL where the membership
 * answers FALSE, so it had to be kept from a negation.
 *
 * <p>Any other membership over the context — a host's {@code IN (SELECT a FROM @ctx.rel WHERE …)},
 * say — keeps its <b>marker column</b>, computed once for the leaf:
 *
 * <pre>
 *   Join LEFT on keys = m.cols        one per such membership
 *     …
 *     Scan
 *   right: Project(cols…, TRUE AS marker)
 *            Aggregate(group by cols)   the rows, without duplicates
 * </pre>
 *
 * <p>The projected {@code TRUE} is nullable through the left join, so the marker column has exactly
 * the membership's own type and drops in where the sub-query stood. Grouping by every column is what
 * keeps the join row-preserving. The one place the marker is not the membership is where the
 * membership would answer UNKNOWN — a NULL key — and the marker answers NULL instead of FALSE; under
 * {@code AND} and {@code OR} read unknown-as-false the two are the same, which is the monotonicity
 * {@link MembershipSplit} checks for its own substitution and this checks for the same reason, and a
 * marker under a negation is refused by name rather than answered wrongly.
 */
final class MembershipMarkers {
  private MembershipMarkers() {}

  /**
   * What one leaf's memberships become.
   *
   * @param memberships the memberships that need a marker join, in the order the joins go on —
   *     empty wherever every membership is over a whole list
   * @param replacement each membership's digest, and the expression that stands for it: the set
   *     lookup, or the marker column
   * @param lookups each membership's digest that became a set lookup, and the call
   */
  record Plan(
      ImmutableList<RexSubQuery> memberships,
      Map<String, RexNode> replacement,
      Map<String, RexNode> lookups) {

    /** The same expression with every membership replaced by what stands for it. */
    RexNode substitute(RexNode node) {
      return replace(node, replacement);
    }

    /**
     * The same expression with only the set lookups in place — what clause 3 of the taint check
     * reads a leaf's row predicate by (§3.10, V68). A lookup is a predicate the leaf's filter holds
     * and the plan pulls up like any other; a marker column does not survive above
     * {@code Project_D} to be found, so a membership that took one stays the sub-query whose
     * relation the plan still scans.
     */
    RexNode lookupsOnly(RexNode node) {
      return replace(node, lookups);
    }

    private static RexNode replace(RexNode node, Map<String, RexNode> with) {
      return node.accept(
          new RexShuttle() {
            @Override
            public RexNode visitSubQuery(RexSubQuery subQuery) {
              RexNode stands = with.get(subQuery.toString());
              return stands == null ? super.visitSubQuery(subQuery) : stands;
            }
          });
    }
  }

  /**
   * The plan for one leaf, or null when it needs nothing at all — which is every leaf whose
   * descriptor holds no membership over a bound list.
   *
   * @param scanWidth how many columns the leaf's row has; the first marker, if any, sits after them
   * @param expressions the row predicate and every expression of the descriptor
   * @param where the qualified table name, for the one message this can refuse with
   */
  static @Nullable Plan of(
      int scanWidth, List<RexNode> expressions, RexBuilder rexBuilder, String where) {
    Map<String, RexSubQuery> memberships = new LinkedHashMap<>();
    for (RexNode expression : expressions) {
      MembershipSplit.collect(expression, memberships);
    }
    if (memberships.isEmpty()) {
      return null;
    }

    RelDataType marker =
        rexBuilder
            .getTypeFactory()
            .createTypeWithNullability(
                rexBuilder.getTypeFactory().createSqlType(SqlTypeName.BOOLEAN), true);

    Map<String, RexNode> replacement = new LinkedHashMap<>();
    Map<String, RexNode> lookups = new LinkedHashMap<>();
    List<RexSubQuery> joined = new ArrayList<>();
    int at = scanWidth;
    for (RexSubQuery membership : memberships.values()) {
      RexNode lookup = ChalkContextMembership.of(membership, rexBuilder);
      if (lookup != null) {
        replacement.put(membership.toString(), lookup);
        lookups.put(membership.toString(), lookup);
        continue;
      }

      if (!MembershipSplit.twoValued(membership)) {
        for (RexNode expression : expressions) {
          if (!MembershipSplit.monotone(expression, membership.toString())) {
            throw new PolicyException(
                PolicyRefusalReason.POLICY_REFUSAL_REASON_BINDING,
                "the entitlement on "
                    + where
                    + " tests membership of a bound relation under a negation, and this request "
                    + "binds the context at execution, where the relation's rows are not here to be "
                    + "compared. Bind the values at prepare, test a whole list (IN (@ctx.name)), or "
                    + "write the condition so the membership stands under AND and OR alone "
                    + "(docs/design/16-entitlements.md §2, D209).");
          }
        }
      }
      // The right side is the relation's rows without duplicates, plus the TRUE the left join turns
      // into the marker. The marker column is the last of them.
      int width = membership.rel.getRowType().getFieldCount();
      replacement.put(membership.toString(), rexBuilder.makeInputRef(marker, at + width));
      joined.add(membership);
      at += width + 1;
    }
    return new Plan(ImmutableList.copyOf(joined), replacement, lookups);
  }

  /**
   * {@code leaf} with one left join per membership that took a marker, in the order {@link #of}
   * numbered them — the leaf itself where every membership became a set lookup.
   */
  static RelNode attach(RelNode leaf, Plan plan, RexBuilder rexBuilder) {
    RelNode input = leaf;
    for (RexSubQuery membership : plan.memberships()) {
      RelNode right = marked(membership.rel, rexBuilder);
      int offset = input.getRowType().getFieldCount();
      List<RexNode> equalities = new ArrayList<>(membership.getOperands().size());
      for (int i = 0; i < membership.getOperands().size(); i++) {
        RexNode key = membership.getOperands().get(i);
        RexNode member =
            rexBuilder.makeInputRef(right.getRowType().getFieldList().get(i).getType(), offset + i);
        equalities.add(rexBuilder.makeCall(SqlStdOperatorTable.EQUALS, key, member));
      }
      input =
          LogicalJoin.create(
              input,
              right,
              ImmutableList.<RelHint>of(),
              RexUtil.composeConjunction(rexBuilder, equalities),
              java.util.Set.<CorrelationId>of(),
              JoinRelType.LEFT);
    }
    return input;
  }

  /** The rows without duplicates, with a {@code TRUE} for the left join to turn into the marker. */
  private static RelNode marked(RelNode list, RexBuilder rexBuilder) {
    int width = list.getRowType().getFieldCount();
    RelNode distinct =
        LogicalAggregate.create(
            list,
            ImmutableList.<RelHint>of(),
            ImmutableBitSet.range(0, width),
            null,
            ImmutableList.<org.apache.calcite.rel.core.AggregateCall>of());

    List<RexNode> projects = new ArrayList<>(width + 1);
    List<String> names = new ArrayList<>(width + 1);
    for (int i = 0; i < width; i++) {
      projects.add(rexBuilder.makeInputRef(distinct, i));
      names.add("k" + i);
    }
    projects.add(rexBuilder.makeLiteral(true));
    names.add("matched");
    return LogicalProject.create(
        distinct,
        ImmutableList.<RelHint>of(),
        projects,
        names,
        java.util.Set.<CorrelationId>of());
  }
}
