package chalk.planner.plan;

import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.core.AggregateCall;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexSubQuery;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;

/**
 * {@code LITERAL_AGG} written as what it means: a projected literal over the aggregate
 * (F71, ADR 0054; docs/design/02-ir.md §6).
 *
 * <p>Calcite's three-valued rewrite of {@code IN} and {@code EXISTS} emits
 * {@code Aggregate(g, LITERAL_AGG(v))} — an aggregate call that reads no column and returns
 * {@code v} once per group. The IR has no such call and does not need one: that <em>is</em>
 * {@code Project(g, v)} over {@code Aggregate(g)}, which is the shape
 * {@link chalk.planner.entitlement.MembershipMarkers} already builds by hand and the executor
 * already runs. Before this, a statement whose entitlement put such a membership beside a path's
 * marker was refused at {@code RelToIr} naming an aggregate the policy had produced.
 *
 * <p>The rewrite preserves the group set, the grouping sets and every other call, and the projection
 * restores the aggregate's own row type field for field — so nothing above it sees any difference
 * and a tree that holds no {@code LITERAL_AGG} comes back as the same object.
 *
 * <p><b>The empty global group.</b> {@code Aggregate(∅, [LITERAL_AGG(v)])} returns one row holding
 * {@code v} even over an input with no rows, because a global aggregate always reports. Stripping
 * its only call would leave {@code Aggregate(∅, [])} — a {@code DISTINCT} of nothing, which returns
 * <em>no</em> row over an empty input. So where the group set is empty and nothing else is
 * aggregated, a {@code COUNT(*)} takes the place: it reports once whatever the input holds, and the
 * projection above it drops the count and keeps the literal.
 */
public final class LiteralAggregates {
  private LiteralAggregates() {}

  /** Whether any aggregate under {@code rel} carries a {@code LITERAL_AGG} call. */
  public static boolean present(RelNode rel) {
    if (rel instanceof Aggregate aggregate && holdsLiteralAgg(aggregate)) {
      return true;
    }
    for (RelNode input : rel.getInputs()) {
      if (present(input)) {
        return true;
      }
    }
    boolean[] found = {false};
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            found[0] |= present(subQuery.rel);
            return super.visitSubQuery(subQuery);
          }
        });
    return found[0];
  }

  /**
   * The same tree with every {@code LITERAL_AGG} call projected instead of aggregated.
   *
   * <p>A plain recursion rather than a {@code RelShuttle}: {@code RelShuttleImpl} dispatches every
   * logical node kind to an overload of its own, so a shuttle that overrides only
   * {@code visit(RelNode)} never sees a {@code LogicalAggregate} at all.
   */
  public static RelNode rewrite(RelNode rel) {
    if (!present(rel)) {
      return rel;
    }
    List<RelNode> inputs = rel.getInputs();
    List<RelNode> rebuilt = new ArrayList<>(inputs.size());
    boolean changed = false;
    for (RelNode input : inputs) {
      RelNode next = rewrite(input);
      changed |= next != input;
      rebuilt.add(next);
    }
    RelNode result = changed ? rel.copy(rel.getTraitSet(), rebuilt) : rel;
    result =
        result.accept(
            new RexShuttle() {
              @Override
              public RexNode visitSubQuery(RexSubQuery subQuery) {
                RexSubQuery visited = (RexSubQuery) super.visitSubQuery(subQuery);
                RelNode inner = rewrite(visited.rel);
                return inner == visited.rel ? visited : visited.clone(inner);
              }
            });
    return result instanceof Aggregate aggregate && holdsLiteralAgg(aggregate)
        ? project(aggregate)
        : result;
  }

  private static boolean holdsLiteralAgg(Aggregate aggregate) {
    for (AggregateCall call : aggregate.getAggCallList()) {
      if (call.getAggregation().getKind() == SqlKind.LITERAL_AGG) {
        return true;
      }
    }
    return false;
  }

  /** {@code Project(group keys…, kept calls…, v…)} over the aggregate without the literal calls. */
  @SuppressWarnings("deprecation")
  private static RelNode project(Aggregate aggregate) {
    RexBuilder rex = aggregate.getCluster().getRexBuilder();
    RelDataType rowType = aggregate.getRowType();
    int groups = aggregate.getGroupCount();

    List<AggregateCall> kept = new ArrayList<>(aggregate.getAggCallList().size());
    // Where each original call's value comes from: a field of the new aggregate, or the literal.
    int[] at = new int[aggregate.getAggCallList().size()];
    for (int i = 0; i < aggregate.getAggCallList().size(); i++) {
      AggregateCall call = aggregate.getAggCallList().get(i);
      if (call.getAggregation().getKind() == SqlKind.LITERAL_AGG) {
        at[i] = -1;
        continue;
      }
      at[i] = groups + kept.size();
      kept.add(call);
    }

    boolean counted = aggregate.getGroupCount() == 0 && kept.isEmpty();
    if (counted) {
      // The global group has to keep reporting over an empty input; see the class comment.
      kept.add(
          AggregateCall.create(
              SqlStdOperatorTable.COUNT,
              /* distinct= */ false,
              /* approximate= */ false,
              /* ignoreNulls= */ false,
              ImmutableList.of(),
              ImmutableList.of(),
              /* filterArg= */ -1,
              /* distinctKeys= */ null,
              org.apache.calcite.rel.RelCollations.EMPTY,
              /* groupCount= */ 0,
              aggregate.getInput(),
              /* type= */ null,
              chalk.planner.ReservedNames.PREFIX + "reported"));
    }

    RelNode reduced =
        aggregate.copy(
            aggregate.getTraitSet(),
            aggregate.getInput(),
            aggregate.getGroupSet(),
            aggregate.getGroupSets(),
            kept);

    List<RexNode> projects = new ArrayList<>(rowType.getFieldCount());
    for (int f = 0; f < groups; f++) {
      projects.add(rex.makeInputRef(reduced, f));
    }
    for (int i = 0; i < aggregate.getAggCallList().size(); i++) {
      AggregateCall call = aggregate.getAggCallList().get(i);
      if (at[i] >= 0) {
        projects.add(rex.makeInputRef(reduced, at[i]));
        continue;
      }
      // LITERAL_AGG carries its value in `rexList` and reads no column, so the value per group is
      // the value itself; the cast makes the projection's field the type the call declared, which is
      // what keeps the aggregate's own row type intact above it.
      RexNode value =
          call.rexList.isEmpty() ? rex.makeNullLiteral(call.getType()) : call.rexList.get(0);
      projects.add(rex.ensureType(call.getType(), value, /* matchNullability= */ true));
    }

    return LogicalProject.create(
        reduced,
        ImmutableList.<org.apache.calcite.rel.hint.RelHint>of(),
        projects,
        rowType.getFieldNames(),
        java.util.Set.<org.apache.calcite.rel.core.CorrelationId>of());
  }
}
