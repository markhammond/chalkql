package chalk.planner.entitlement;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.IdentityHashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.core.AggregateCall;
import org.apache.calcite.rel.core.Correlate;
import org.apache.calcite.rel.core.CorrelationId;
import org.apache.calcite.rel.core.Filter;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.core.SetOp;
import org.apache.calcite.rel.core.Sort;
import org.apache.calcite.rel.core.TableFunctionScan;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.core.Values;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexCorrelVariable;
import org.apache.calcite.rex.RexFieldAccess;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexOver;
import org.apache.calcite.rex.RexSubQuery;
import org.apache.calcite.rex.RexVisitorImpl;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.type.SqlTypeFamily;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The population-only trace (docs/design/16-entitlements.md §3.4, D198).
 *
 * <p>For every leaf occurrence and every column whose folded rules can yield {@code AGGREGATE_ONLY}
 * for a row the principal can see, the raw value's bare pass-throughs are followed upward —
 * projections that carry it as a bare reference, filters, joins, correlates, set operations and
 * sub-query boundaries — to every consumer. The column may leave the leaf raw <b>only</b> when every
 * consumer is an allow-listed aggregate call in an {@code Aggregate} taking it as a bare reference,
 * optionally under a {@code CAST} to a numeric type. Any other consumer is {@code POLICY}, naming
 * the table, the column, the use and — for an aggregate — the function.
 *
 * <p>The rule holds whenever {@code AGGREGATE_ONLY} is <em>possible</em> for a visible row, so a
 * principal who is an auditor in one organisation and a manager in another is refused
 * {@code SELECT amount FROM orders} rather than handed values for one and placeholders for the
 * other. The remedy is a statement whose folded disclosure is constant — {@code WHERE org_id = 2} —
 * or the aggregate they should have written.
 *
 * <p>It runs on the tree the pass received, before the leaves are rewritten: there the column is
 * still the scan's own, so "a bare reference" means what it says. After the rewrite the value that
 * flows is the sanitiser, whose {@code AGGREGATE_ONLY} branch holds the raw column, and a trace over
 * that tree would have to see through a {@code CASE} to say the same thing.
 */
final class PopulationTrace {

  /** One population-only column, as the trace carries it upward. */
  record Taint(
      String table,
      String column,
      List<String> allowList,
      int floor,
      RelDataType type,
      boolean statistical) {}

  /**
   * One aggregate call the guard rewrites: which call, what the guarding count counts, and the floor
   * its column asks for.
   *
   * @param argument the column the guarding {@code COUNT} takes, or -1 for {@code COUNT(*)} — the
   *     group's own size, which is what §3.6's {@code COUNT(c)} <em>is</em> for a NOT NULL c (F43)
   *     and the only count D261's aggregate form can form, since a filtered count reads no column
   *     the aggregate's input carries
   */
  record Guard(int callIndex, int argument, int floor) {}

  /** What the trace found: the aggregates to guard and the ones to suppress, by node identity. */
  record Result(Map<Aggregate, List<Guard>> guards, Map<Aggregate, Integer> suppressions) {}

  private final Map<Aggregate, List<Guard>> guards = new IdentityHashMap<>();

  /** The aggregates a raw statistical predicate or key shaped, and the floor each takes (D203). */
  private final Map<Aggregate, Integer> suppressions = new IdentityHashMap<>();

  /**
   * The floor a statistical read below the next {@code Aggregate} owes it. A predicate is not a node
   * the guard can sit on, so the floor waits here for the aggregate that consumes its rows — which
   * the post-order walk always visits next.
   */
  private int pendingFloor;

  private final Map<TableScan, EntitlementPass.LeafFold> folds;
  private final PolicyOptions options;

  /**
   * The comparisons the leaf will compute instead (D261). An expression this names is not a use of
   * the raw value <em>here</em>: the value never leaves the leaf for it, and what stands in this
   * position is one boolean of the leaf's own making.
   */
  private final TestLift.Plan lifts;

  /** Left-hand taints by correlation id, for the field accesses inside a correlated sub-query. */
  private final Map<CorrelationId, Map<Integer, Taint>> correlations = new HashMap<>();

  private PopulationTrace(
      Map<TableScan, EntitlementPass.LeafFold> folds, PolicyOptions options, TestLift.Plan lifts) {
    this.folds = folds;
    this.options = options;
    this.lifts = lifts;
  }

  /**
   * Runs the trace. Answers the aggregates the guard must rewrite; throws {@link PolicyException}
   * naming the first use no disclosure permits.
   */
  static Result run(
      RelNode root,
      Map<TableScan, EntitlementPass.LeafFold> folds,
      PolicyOptions options,
      TestLift.Plan lifts) {
    boolean any = false;
    for (EntitlementPass.LeafFold fold : folds.values()) {
      if (!fold.map().taintedColumns().isEmpty()) {
        any = true;
        break;
      }
    }
    // A catalog with no population-only column runs no analysis at all, and the pass is then purely
    // local (§3.1). This is that sentence, made cheap.
    if (!any) {
      return new Result(java.util.Collections.emptyMap(), java.util.Collections.emptyMap());
    }

    PopulationTrace trace = new PopulationTrace(folds, options, lifts);
    Map<Integer, Taint> atRoot = trace.visit(root);
    if (!atRoot.isEmpty()) {
      Taint taint = atRoot.values().iterator().next();
      throw refuse(taint, "a projection to the result", null);
    }
    return new Result(trace.guards, trace.suppressions);
  }

  // ------------------------------------------------------------------ the walk

  /** Which of {@code rel}'s output columns carry a population-only value, still raw. */
  private Map<Integer, Taint> visit(RelNode rel) {
    if (rel instanceof TableScan scan) {
      return atLeaf(scan);
    }
    if (rel instanceof Project project) {
      return atProject(project);
    }
    if (rel instanceof Filter filter) {
      Map<Integer, Taint> in = visit(filter.getInput());
      refuseAnyUse(filter.getCondition(), in, "a predicate");
      return in;
    }
    if (rel instanceof Join join) {
      Map<Integer, Taint> in = concatenated(join);
      refuseAnyUse(join.getCondition(), in, "a join condition");
      return in;
    }
    if (rel instanceof Correlate correlate) {
      Map<Integer, Taint> left = visit(correlate.getLeft());
      correlations.put(correlate.getCorrelationId(), left);
      Map<Integer, Taint> right = visit(correlate.getRight());
      Map<Integer, Taint> out = new LinkedHashMap<>(left);
      int offset = correlate.getLeft().getRowType().getFieldCount();
      right.forEach((index, taint) -> out.put(offset + index, taint));
      // A correlate whose join type keeps only the left row still carries the left's taints.
      return correlate.getJoinType().projectsRight() ? out : left;
    }
    if (rel instanceof Aggregate aggregate) {
      return atAggregate(aggregate);
    }
    if (rel instanceof Sort sort) {
      Map<Integer, Taint> in = visit(sort.getInput());
      for (org.apache.calcite.rel.RelFieldCollation field : sort.getCollation().getFieldCollations()) {
        Taint taint = in.get(field.getFieldIndex());
        if (taint != null) {
          throw refuse(taint, "a sort key", null);
        }
      }
      return in;
    }
    if (rel instanceof SetOp setOp) {
      // Output i comes from input i of every branch (§3.4).
      Map<Integer, Taint> out = new LinkedHashMap<>();
      for (RelNode input : setOp.getInputs()) {
        out.putAll(visit(input));
      }
      return out;
    }
    if (rel instanceof Values) {
      return Map.of();
    }
    if (rel instanceof TableFunctionScan functionScan) {
      Map<Integer, Taint> in = concatenatedInputs(functionScan);
      refuseAnyUse(functionScan.getCall(), in, "a table function's argument");
      return Map.of();
    }

    // A node the trace does not know fails it, which is the fail-closed reading of §3.10 at the
    // logical level: a population-only value passing through something nothing understands is
    // exactly the escape the layer exists to close.
    Map<Integer, Taint> in = concatenatedInputs(rel);
    if (!in.isEmpty()) {
      throw refuse(in.values().iterator().next(), rel.getRelTypeName(), null);
    }
    return Map.of();
  }

  private Map<Integer, Taint> atLeaf(TableScan scan) {
    EntitlementPass.LeafFold fold = folds.get(scan);
    if (fold == null) {
      return Map.of();
    }
    List<Integer> projection = EntitlementPass.projectionOf(scan);
    RelDataType rowType = EntitlementPass.disclosedRowType(scan);
    Map<Integer, Taint> out = new LinkedHashMap<>();
    for (int i = 0; i < projection.size(); i++) {
      int column = projection.get(i);
      if (fold.map().of(column) != Disclosed.AGGREGATE) {
        continue;
      }
      DescriptorExpressions.Column entitled = fold.descriptor().column(column);
      out.put(
          i,
          new Taint(
              fold.table().schemaName() + "." + fold.table().tableName(),
              rowType.getFieldList().get(column).getName(),
              entitled == null ? List.of() : entitled.allowList(),
              options.minGroupSize(entitled == null ? 0 : entitled.minGroupSize()),
              rowType.getFieldList().get(column).getType(),
              fold.statistical().contains(column)));
    }
    return out;
  }

  /**
   * A projection carries a taint through only as a bare reference, or as a {@code CAST} of one to a
   * numeric type — {@code AVG(CAST(amount AS DOUBLE))} is the shape people write daily, and the
   * converter puts that cast in a projection below the aggregate. Everything else is a value use.
   */
  private Map<Integer, Taint> atProject(Project project) {
    Map<Integer, Taint> in = visit(project.getInput());
    Map<Integer, Taint> out = new LinkedHashMap<>();
    List<RexNode> exprs = project.getProjects();
    for (int i = 0; i < exprs.size(); i++) {
      RexNode expr = exprs.get(i);
      Taint direct = bareReference(expr, in);
      if (direct != null) {
        out.put(i, direct);
        continue;
      }
      // A comparison the leaf computes is not a use of the raw value here (D261): what this
      // projection will hold is one boolean the leaf derived, and the value never left it. The
      // aggregate form is the only place a population-only column reaches one — the converter
      // writes a FILTER as `IS TRUE(<comparison>)` in this very projection — and §3.4's refusals
      // are unchanged for every other position and for whatever else the expression reads.
      refuseAnyUse(project, expr, in, "a value use (" + expr + ")");
    }
    return out;
  }

  /** The taint {@code expr} carries when it is one, or null. */
  private static @Nullable Taint bareReference(RexNode expr, Map<Integer, Taint> in) {
    if (expr instanceof RexInputRef ref) {
      return in.get(ref.getIndex());
    }
    if (expr.getKind() == SqlKind.CAST
        && expr instanceof RexCall cast
        && cast.getOperands().get(0) instanceof RexInputRef ref
        && isNumeric(expr.getType())) {
      // A cast cannot select a row, so it is not a use; the value stays tainted.
      return in.get(ref.getIndex());
    }
    return null;
  }

  private static boolean isNumeric(RelDataType type) {
    SqlTypeFamily family = type.getSqlTypeName().getFamily();
    return family == SqlTypeFamily.NUMERIC
        || family == SqlTypeFamily.INTEGER
        || family == SqlTypeFamily.EXACT_NUMERIC
        || family == SqlTypeFamily.APPROXIMATE_NUMERIC;
  }

  private Map<Integer, Taint> atAggregate(Aggregate aggregate) {
    Map<Integer, Taint> in = visit(aggregate.getInput());
    int floor = pendingFloor;
    pendingFloor = 0;
    for (int key : aggregate.getGroupSet()) {
      Taint taint = in.get(key);
      if (taint == null) {
        continue;
      }
      if (!taint.statistical()) {
        throw refuse(taint, "a grouping key", null);
      }
      // A raw grouping key is k-anonymous, and that is the whole of what makes it safe (D203).
      floor = Math.max(floor, taint.floor());
    }
    if (floor > 1) {
      suppressions.merge(aggregate, floor, Math::max);
    }

    if (in.isEmpty()) {
      return Map.of();
    }

    List<Guard> guarded = new ArrayList<>();
    List<AggregateCall> calls = aggregate.getAggCallList();
    for (int i = 0; i < calls.size(); i++) {
      AggregateCall call = calls.get(i);
      Taint taint = argumentTaint(call, in);
      if (taint == null) {
        continue;
      }
      String function = call.getAggregation().getName().toUpperCase(Locale.ROOT);
      if (call.filterArg >= 0 && in.containsKey(call.filterArg)) {
        Taint filtered = in.get(call.filterArg);
        if (filtered == null || !filtered.statistical()) {
          throw refuse(taint, "an aggregate's FILTER clause", function);
        }
        // A statistical FILTER is permitted, and this call reads nothing else of the column:
        // COUNT(*) FILTER (WHERE …) takes no argument at all.
        if (argumentOnly(call, in) == null) {
          continue;
        }
      }
      if (call.getArgList().size() != 1) {
        throw refuse(taint, "an aggregate over more than this column", function);
      }
      if (!taint.allowList().contains(function)) {
        throw refuse(taint, "an aggregate outside this column's allow-list", function);
      }
      // An effective floor of one or less is no guard at all (D211): no COUNT(c), no projection.
      // The refusals above still ran — the floor decides what a permitted aggregate costs, never
      // what is permitted.
      if (taint.floor() > 1) {
        guarded.add(new Guard(i, call.getArgList().get(0), taint.floor()));
      }
    }

    if (!guarded.isEmpty()) {
      guards.put(aggregate, guarded);
    }
    // An aggregate result is a population, not a value: nothing tainted leaves this node.
    return Map.of();
  }

  /** The taint one of this call's <em>arguments</em> carries, ignoring its FILTER. */
  private static @Nullable Taint argumentOnly(AggregateCall call, Map<Integer, Taint> in) {
    for (int argument : call.getArgList()) {
      Taint taint = in.get(argument);
      if (taint != null) {
        return taint;
      }
    }
    return null;
  }

  /** The taint an aggregate call's argument carries, or null when it takes none. */
  private static @Nullable Taint argumentTaint(AggregateCall call, Map<Integer, Taint> in) {
    for (int argument : call.getArgList()) {
      Taint taint = in.get(argument);
      if (taint != null) {
        return taint;
      }
    }
    return in.get(call.filterArg);
  }

  private Map<Integer, Taint> concatenated(Join join) {
    Map<Integer, Taint> out = new LinkedHashMap<>(visit(join.getLeft()));
    int offset = join.getLeft().getRowType().getFieldCount();
    visit(join.getRight()).forEach((index, taint) -> out.put(offset + index, taint));
    return out;
  }

  private Map<Integer, Taint> concatenatedInputs(RelNode rel) {
    Map<Integer, Taint> out = new LinkedHashMap<>();
    int offset = 0;
    for (RelNode input : rel.getInputs()) {
      int base = offset;
      visit(input).forEach((index, taint) -> out.put(base + index, taint));
      offset += input.getRowType().getFieldCount();
    }
    return out;
  }

  // ------------------------------------------------------------------ expressions

  /**
   * Refuses if {@code expr} reads a tainted value at all — including through a window aggregate,
   * whose partition can be one row, and through a correlated field access, which resolves to the
   * corresponding column of the node that defines the variable.
   */
  private void refuseAnyUse(@Nullable RexNode expr, Map<Integer, Taint> in, String use) {
    refuseAnyUse(null, expr, in, use);
  }

  /**
   * The same, at a node whose expressions the leaf may compute some of (D261): a sub-expression the
   * lift names is skipped whole, because the raw value never leaves the leaf for it.
   */
  private void refuseAnyUse(
      @Nullable RelNode at, @Nullable RexNode expr, Map<Integer, Taint> in, String use) {
    if (expr == null) {
      return;
    }
    expr.accept(
        new RexVisitorImpl<Void>(true) {
          @Override
          public Void visitCall(org.apache.calcite.rex.RexCall call) {
            if (at != null && lifts.lifts(at, call)) {
              return null;
            }
            return super.visitCall(call);
          }

          @Override
          public Void visitInputRef(RexInputRef ref) {
            Taint taint = in.get(ref.getIndex());
            if (taint != null) {
              // The statistical opt-in (D203): the raw value may reach a predicate, a join condition
              // and an aggregate's FILTER, and the aggregate whose rows it shaped then carries the
              // group-size floor as a HAVING that drops the group.
              if (taint.statistical()) {
                pendingFloor = Math.max(pendingFloor, taint.floor());
                return null;
              }
              throw refuse(taint, use, null);
            }
            return null;
          }

          @Override
          public Void visitOver(RexOver over) {
            for (RexNode operand : over.getOperands()) {
              operand.accept(
                  new RexVisitorImpl<Void>(true) {
                    @Override
                    public Void visitInputRef(RexInputRef ref) {
                      Taint taint = in.get(ref.getIndex());
                      if (taint != null) {
                        throw refuse(taint, "a window aggregate", over.getAggOperator().getName());
                      }
                      return null;
                    }
                  });
            }
            return super.visitOver(over);
          }

          @Override
          public Void visitFieldAccess(RexFieldAccess access) {
            if (access.getReferenceExpr() instanceof RexCorrelVariable variable) {
              Map<Integer, Taint> outer = correlations.get(variable.id);
              Taint taint = outer == null ? null : outer.get(access.getField().getIndex());
              if (taint != null) {
                throw refuse(taint, use, null);
              }
              return null;
            }
            return super.visitFieldAccess(access);
          }

          @Override
          public Void visitSubQuery(RexSubQuery subQuery) {
            // A sub-query's own tree is traced in full, and a taint that reaches its output is a
            // query use of the value by whatever the sub-query feeds.
            Map<Integer, Taint> inner = visit(subQuery.rel);
            if (!inner.isEmpty()) {
              throw refuse(inner.values().iterator().next(), "a sub-query's result", null);
            }
            return super.visitSubQuery(subQuery);
          }
        });
  }

  private static PolicyException refuse(Taint taint, String use, @Nullable String function) {
    StringBuilder message = new StringBuilder();
    message
        .append(taint.table())
        .append('.')
        .append(taint.column())
        .append(" is population-only for this principal and ")
        .append(use)
        .append(" is not one of the aggregates it permits");
    if (function != null) {
      message.append("; the function is ").append(function);
    }
    message
        .append(". Permitted: ")
        .append(taint.allowList().isEmpty() ? "none" : String.join(", ", taint.allowList()))
        .append(", each taking the column as a bare reference, optionally under a CAST to a numeric")
        .append(" type. A predicate, a grouping key, a sort key, a FILTER clause, a window aggregate")
        .append(" or a value use of the raw value is refused for every principal, because a")
        .append(" comparison on a raw value is an oracle (docs/design/16-entitlements.md §3.4).");
    return new PolicyException(message.toString());
  }
}
