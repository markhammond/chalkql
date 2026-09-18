package chalk.planner.entitlement;

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
 * Each distinct membership over a bound list, computed once per leaf as a column
 * (docs/design/16-entitlements.md §3.2, D209).
 *
 * <p>Under execute-time binding nothing folds, so every {@code org_id IN (@ctx.manager_orgs)} in a
 * row predicate <em>and in every rule condition</em> is a sub-query over a {@code BoundTable}. A
 * sub-query cannot stand in a projection the IR can express — that is the wall F36 met, where
 * Calcite's three-valued rewrite needs {@code LITERAL_AGG} — and a sanitiser is a projection.
 *
 * <p>So each distinct membership becomes one <b>marker column</b>, computed once for the leaf:
 *
 * <pre>
 *   Join LEFT on keys = m.cols        one per distinct membership
 *     …
 *     Scan
 *   right: Project(cols…, TRUE AS marker)
 *            Aggregate(group by cols)   the list, without duplicates
 * </pre>
 *
 * <p>The projected {@code TRUE} is nullable through the left join, so the marker column has exactly
 * the membership's own type — nullable {@code BOOLEAN}, TRUE where the list holds the row's key —
 * and drops in where the sub-query stood, in the predicate and in every rule condition alike. That is
 * §3.2's "each distinct rule condition evaluated once per leaf": what is expensive in a condition is
 * the membership, and one join per list is what makes a per-row disclosure cost no join per column.
 *
 * <p>Grouping by every column is what keeps the join row-preserving: at most one right row matches,
 * so the leaf's cardinality is the scan's.
 *
 * <p>The one place the marker is not the membership is where the membership would answer UNKNOWN — a
 * NULL key — and the marker answers NULL instead of FALSE. Under {@code AND} and {@code OR} read
 * unknown-as-false the two are the same, which is the monotonicity {@link MembershipSplit} checks for
 * its own substitution and this checks for the same reason; a membership under a negation is refused
 * by name rather than answered wrongly.
 */
final class MembershipMarkers {
  private MembershipMarkers() {}

  /**
   * The markers one leaf needs: what to join on, and what each membership becomes.
   *
   * @param memberships the distinct memberships, in the order the joins go on
   * @param replacement each membership's digest, and the marker expression that stands for it
   */
  record Plan(ImmutableList<RexSubQuery> memberships, Map<String, RexNode> replacement) {

    /** The same expression with every membership replaced by its marker column. */
    RexNode substitute(RexNode node) {
      RexNode replaced =
          node.accept(
              new RexShuttle() {
                @Override
                public RexNode visitSubQuery(RexSubQuery subQuery) {
                  RexNode marker = replacement.get(subQuery.toString());
                  return marker == null ? super.visitSubQuery(subQuery) : marker;
                }
              });
      return replaced;
    }
  }

  /**
   * The plan for one leaf, or null when it needs no marker at all — which is every leaf whose
   * descriptor holds no membership over a bound list.
   *
   * @param scanWidth how many columns the leaf's scan has; the first marker sits after them
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
    int at = scanWidth;
    for (RexSubQuery membership : memberships.values()) {
      if (!MembershipSplit.twoValued(membership)) {
        for (RexNode expression : expressions) {
          if (!MembershipSplit.monotone(expression, membership.toString())) {
            throw new PolicyException(
                "the entitlement on "
                    + where
                    + " tests membership of a bound list under a negation, and this request binds "
                    + "the context at execution, where the list's rows are not here to be compared. "
                    + "Bind the values at prepare, or write the condition so the membership stands "
                    + "under AND and OR alone (docs/design/16-entitlements.md §2, D209).");
          }
        }
      }
      // The right side is the list without duplicates, plus the TRUE the left join turns into the
      // marker. The marker column is the last of them.
      int width = membership.rel.getRowType().getFieldCount();
      replacement.put(membership.toString(), rexBuilder.makeInputRef(marker, at + width));
      at += width + 1;
    }
    return new Plan(ImmutableList.copyOf(memberships.values()), replacement);
  }

  /** {@code leaf} with one left join per membership, in the order {@link #of} numbered them. */
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

  /** The list without duplicates, with a {@code TRUE} for the left join to turn into the marker. */
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
