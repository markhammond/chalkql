package chalk.planner.entitlement;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.core.AggregateCall;
import org.apache.calcite.rel.core.CorrelationId;
import org.apache.calcite.rel.hint.RelHint;
import org.apache.calcite.rel.logical.LogicalAggregate;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.util.ImmutableBitSet;

/**
 * The group-size guard (docs/design/16-entitlements.md §3.6).
 *
 * <p>For each {@code Aggregate} the trace found consuming a population-only column, a
 * {@code COUNT(c)} is added to the aggregate's calls when no identical call is there already, and a
 * projection of the same shape is placed above it in which every guarded output is
 * {@code CASE WHEN count >= k THEN agg ELSE NULL END} — k the column's own floor or the catalog's
 * default. Positions are unchanged, so a {@code HAVING} or {@code ORDER BY} above keeps its
 * references, and the added count never reaches the caller.
 *
 * <p>A projection rather than a filter, deliberately: a pushed aggregate carries the extra
 * {@code COUNT} into the source and the guard stays local. {@code COUNT(*)} is not over the column
 * and is not guarded; {@code COUNT(c)} is, its own count included.
 *
 * <p>A composite measure — a user aggregate its host declared {@code Population()} (D295) — is
 * guarded the same way and NULLed as one composite: a {@code CASE} of the composite's own type.
 *
 * <p>This is query-set-size control and the README says so: a group of k rows reveals its aggregate,
 * whatever the k rows are.
 */
final class GroupSizeGuard {
  private GroupSizeGuard() {}

  /**
   * The guarded aggregate: {@code Project_G(Aggregate')}, of the aggregate's own row type — and,
   * where a statistical column shaped this aggregate's rows, a {@code HAVING COUNT(*) >= k} between
   * the two.
   *
   * <p>Suppression rather than NULLing, deliberately, and only for the statistical opt-in (D203):
   * there the <em>group key</em> is a raw value, so a NULLed measure beside a disclosed key of a
   * group of one would say the thing the floor exists to withhold. For a population-only column the
   * key is not the sensitive value and the NULL is the honest answer, which is why the two differ.
   */
  static RelNode apply(
      Aggregate aggregate,
      List<PopulationTrace.Guard> guards,
      int suppressBelow,
      RexBuilder rexBuilder) {
    List<AggregateCall> calls = new ArrayList<>(aggregate.getAggCallList());
    int groupCount = aggregate.getGroupCount();

    // One count per guarded column, reused where the statement already asks for it. Reuse matters
    // twice over: AGGREGATE_REDUCE_FUNCTIONS later rewrites AVG into SUM0 and COUNT and finds this
    // one rather than adding a second.
    Map<Counted, Integer> countOf = new LinkedHashMap<>();
    for (PopulationTrace.Guard guard : guards) {
      countOf.computeIfAbsent(
          new Counted(guard.argument()),
          counted -> {
            int existing = indexOfCount(calls, counted);
            if (existing >= 0) {
              return groupCount + existing;
            }
            calls.add(count(counted, aggregate));
            return groupCount + calls.size() - 1;
          });
    }

    int suppressAt = -1;
    if (suppressBelow > 1) {
      int existing = indexOfCount(calls, Counted.STAR);
      if (existing < 0) {
        calls.add(count(Counted.STAR, aggregate));
        existing = calls.size() - 1;
      }
      suppressAt = groupCount + existing;
    }

    RelNode guardedAggregate =
        LogicalAggregate.create(
            aggregate.getInput(),
            aggregate.getHints(),
            aggregate.getGroupSet(),
            aggregate.getGroupSets(),
            calls);

    RelDataType produced = guardedAggregate.getRowType();
    if (suppressAt >= 0) {
      // The group itself goes, rather than its measures being NULLed: with a raw key, a row is a
      // disclosure whatever its measures say.
      guardedAggregate =
          org.apache.calcite.rel.logical.LogicalFilter.create(
              guardedAggregate,
              rexBuilder.makeCall(
                  SqlStdOperatorTable.GREATER_THAN_OR_EQUAL,
                  rexBuilder.makeInputRef(
                      produced.getFieldList().get(suppressAt).getType(), suppressAt),
                  rexBuilder.makeExactLiteral(java.math.BigDecimal.valueOf(suppressBelow))));
    }
    RelDataType original = aggregate.getRowType();
    Map<Integer, PopulationTrace.Guard> byOutput = new LinkedHashMap<>();
    for (PopulationTrace.Guard guard : guards) {
      byOutput.put(groupCount + guard.callIndex(), guard);
    }

    List<RexNode> projects = new ArrayList<>(original.getFieldCount());
    for (int i = 0; i < original.getFieldCount(); i++) {
      RexNode value =
          rexBuilder.makeInputRef(produced.getFieldList().get(i).getType(), i);
      PopulationTrace.Guard guard = byOutput.get(i);
      if (guard == null) {
        projects.add(value);
        continue;
      }
      int countAt = countOf.get(new Counted(guard.argument()));
      RexNode enough =
          rexBuilder.makeCall(
              SqlStdOperatorTable.GREATER_THAN_OR_EQUAL,
              rexBuilder.makeInputRef(produced.getFieldList().get(countAt).getType(), countAt),
              rexBuilder.makeExactLiteral(java.math.BigDecimal.valueOf(guard.floor())));
      if (value.getType().isStruct()) {
        // D295: a composite measure — a user aggregate declared Population() — is NULLed whole, as
        // one composite. The CASE is typed explicitly as the measure's own composite made nullable,
        // every field as declared: Calcite would infer it through createTypeWithNullability, which
        // copies a record with every field nullable, and the IR's CASE chooses between composites
        // of one type (I-IR-23).
        RelDataType whole =
            rexBuilder.getTypeFactory().enforceTypeWithNullability(value.getType(), true);
        projects.add(
            rexBuilder.makeCall(
                whole,
                SqlStdOperatorTable.CASE,
                List.of(enough, value, rexBuilder.makeNullLiteral(whole))));
        continue;
      }
      // The NULL is a redacted value and the report says so (`Aggregate`, D202): a grid that read it
      // as "no rows" would be wrong twice.
      RexNode redacted =
          rexBuilder.makeNullLiteral(
              rexBuilder
                  .getTypeFactory()
                  .createTypeWithNullability(value.getType(), true));
      projects.add(rexBuilder.makeCall(SqlStdOperatorTable.CASE, enough, value, redacted));
    }

    return LogicalProject.create(
        guardedAggregate,
        com.google.common.collect.ImmutableList.<RelHint>of(),
        projects,
        original.getFieldNames(),
        java.util.Set.<CorrelationId>of());
  }

  /**
   * What one guarding count counts: a column, or nothing at all.
   *
   * @param argument the column, or -1 for {@code COUNT(*)} — the group's own size, which is what
   *     §3.6's {@code COUNT(c)} is for a NOT NULL c (F43) and what D261's aggregate form guards on
   */
  private record Counted(int argument) {
    static final Counted STAR = new Counted(-1);

    List<Integer> arguments() {
      return argument < 0 ? List.of() : List.of(argument);
    }
  }

  /** An identical count already among the calls, or -1. */
  private static int indexOfCount(List<AggregateCall> calls, Counted counted) {
    for (int i = 0; i < calls.size(); i++) {
      AggregateCall call = calls.get(i);
      if (call.getAggregation().getKind() == org.apache.calcite.sql.SqlKind.COUNT
          && !call.isDistinct()
          && !call.isApproximate()
          && call.filterArg < 0
          && call.getArgList().equals(counted.arguments())) {
        return i;
      }
    }
    return -1;
  }

  @SuppressWarnings("deprecation")
  private static AggregateCall count(Counted counted, Aggregate aggregate) {
    return AggregateCall.create(
        SqlStdOperatorTable.COUNT,
        /* distinct= */ false,
        /* approximate= */ false,
        /* ignoreNulls= */ false,
        List.of(),
        counted.arguments(),
        /* filterArg= */ -1,
        (ImmutableBitSet) null,
        org.apache.calcite.rel.RelCollations.EMPTY,
        aggregate.getGroupCount(),
        aggregate.getInput(),
        null,
        null);
  }
}
