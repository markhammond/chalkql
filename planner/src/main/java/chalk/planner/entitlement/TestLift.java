package chalk.planner.entitlement;

import chalk.ir.v1.Disclosure;
import chalk.ir.v1.TestShape;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.EnumSet;
import java.util.IdentityHashMap;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.core.AggregateCall;
import org.apache.calcite.rel.core.Filter;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.core.Sort;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexCorrelVariable;
import org.apache.calcite.rex.RexFieldAccess;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexOver;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexSubQuery;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.rex.RexVisitorImpl;
import org.apache.calcite.sql.SqlKind;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The test verdict's lift (docs/design/36-test-verdict.md §2, D261).
 *
 * <p>For a column whose folded verdict is {@code TEST}, every predicate of a permitted shape the
 * statement writes over it — {@code =}, {@code <>}, {@code IN (list)}, with the other operands
 * dynamic parameters, literals or context references bound per request — is <b>computed in the
 * leaf</b>, over the raw value, as a derived boolean column of {@code Project_D}; the expression
 * above is replaced by a reference to that column. The select-list form {@code SELECT col = ?} is
 * the same substitution. Every other use sees the placeholder, exactly as under {@code NONE}:
 * {@code col = other.col}, {@code UPPER(col) = ?}, {@code col LIKE ?}, {@code col IN (SELECT …)},
 * {@code BETWEEN}, {@code IS NULL}, a sort key, a grouping key, a join key.
 *
 * <p>This class does the <em>looking</em>, on the tree the pass received and before any leaf is
 * rewritten — where a column reference is still the scan's own, so "a bare reference" means what it
 * says and the operand that is not the column is judged as the statement wrote it. What it produces
 * is, per leaf occurrence, the comparisons to compute there, and, per consumer, which of that node's
 * expressions become references to them. The pass builds the columns and performs the substitution.
 *
 * <p><b>Where the lift may reach.</b> Only up a chain of {@code Filter}s and {@code Sort}s, which
 * keep a row a row and keep every column where it was, to one consuming {@code Filter},
 * {@code Project} or {@code Aggregate}. A projection in between renumbers the row and a join,
 * aggregate or set operation makes a row out of several, so the leaf's derived column could not be
 * addressed from there; a test the lift does not reach is simply not lifted, and what the statement
 * then compares is the placeholder — the same answer as under {@code NONE}, which is the
 * conservative direction and the one this layer fails in.
 *
 * <p><b>The aggregate form</b> (§2, D261) is the one place a column whose verdict is
 * {@code AGGREGATE_ONLY} may be compared: as the {@code FILTER} of a permitted aggregate over the
 * table, where the rule carrying the allow-list also names the shape. The comparison is lifted the
 * same way, so the raw value never leaves the leaf for it either, and the group-size guard of §3.6
 * is applied to the call — over the <em>filtered</em> population, which is the population the
 * aggregate reports on and therefore the one the floor is about.
 */
final class TestLift {

  /** One comparison the statement wrote and this leaf may compute (D261). */
  record Lifted(int tableColumn, TestShape shape, RexNode comparison) {}

  /** One aggregate call whose {@code FILTER} is a lifted test, and the floor its column asks for. */
  record FilteredGuard(int callIndex, int floor) {}

  /**
   * What the collection found.
   *
   * @param byLeaf per leaf occurrence, the comparisons to compute in its {@code Project_D}, in the
   *     order they will be appended — so the {@code i}th of them is that leaf's output column
   *     {@code width + i}
   * @param substitutions per consumer, the text of each expression that becomes a reference, and the
   *     ordinal within its leaf's list
   * @param guards per {@code Aggregate}, the calls whose {@code FILTER} is a lifted test over a
   *     population-only column, and the floor each takes
   */
  record Plan(
      Map<TableScan, List<Lifted>> byLeaf,
      Map<RelNode, Map<String, Integer>> substitutions,
      Map<Aggregate, List<FilteredGuard>> guards) {

    static final Plan EMPTY = new Plan(Map.of(), Map.of(), Map.of());

    boolean isEmpty() {
      return byLeaf.isEmpty();
    }

    List<Lifted> of(TableScan scan) {
      return byLeaf.getOrDefault(scan, List.of());
    }

    /** The lifted ordinal this node's expression becomes, or -1 when it is not one. */
    int ordinalAt(RelNode node, RexNode expression) {
      Map<String, Integer> here = substitutions.get(node);
      if (here == null) {
        return -1;
      }
      Integer ordinal = here.get(expression.toString());
      return ordinal == null ? -1 : ordinal;
    }

    /** Whether this node's expression is one the leaf computes instead (for §3.4's trace). */
    boolean lifts(RelNode node, RexNode expression) {
      return ordinalAt(node, expression) >= 0;
    }
  }

  private final Map<TableScan, EntitlementPass.LeafFold> folds;
  private final PolicyOptions options;
  private final RexBuilder rexBuilder;

  private final Map<TableScan, List<Lifted>> byLeaf = new IdentityHashMap<>();
  private final Map<TableScan, Map<String, Integer>> ordinals = new IdentityHashMap<>();
  private final Map<RelNode, Map<String, Integer>> substitutions = new IdentityHashMap<>();
  private final Map<Aggregate, List<FilteredGuard>> guards = new IdentityHashMap<>();

  private TestLift(
      Map<TableScan, EntitlementPass.LeafFold> folds, PolicyOptions options, RexBuilder rexBuilder) {
    this.folds = folds;
    this.options = options;
    this.rexBuilder = rexBuilder;
  }

  /**
   * Collects the comparisons every entitled leaf of {@code root} may compute.
   *
   * <p>A catalog in which no folded verdict is {@code TEST} and no population-only rule names a
   * shape does no work here at all, which is §0's zero-cost property: the walk does not start.
   */
  static Plan run(
      RelNode root,
      Map<TableScan, EntitlementPass.LeafFold> folds,
      PolicyOptions options,
      RexBuilder rexBuilder) {
    boolean any = false;
    for (EntitlementPass.LeafFold fold : folds.values()) {
      if (fold.map().columns().contains(Disclosed.TESTED) || namesAShape(fold)) {
        any = true;
        break;
      }
    }
    if (!any) {
      return Plan.EMPTY;
    }

    TestLift lift = new TestLift(folds, options, rexBuilder);
    lift.visit(root);
    if (lift.byLeaf.isEmpty()) {
      return Plan.EMPTY;
    }
    return new Plan(
        Map.copyOf(lift.byLeaf),
        Map.copyOf(lift.substitutions),
        Map.copyOf(lift.guards));
  }

  /** Whether any rule this leaf can still reach permits a comparison at all. */
  private static boolean namesAShape(EntitlementPass.LeafFold fold) {
    for (int column = 0; column < fold.map().columnCount(); column++) {
      if (!shapes(fold, column, Disclosure.DISCLOSURE_AGGREGATE_ONLY).isEmpty()
          || !shapes(fold, column, Disclosure.DISCLOSURE_TEST).isEmpty()) {
        return true;
      }
    }
    return false;
  }

  // ------------------------------------------------------------------ the walk

  private void visit(RelNode rel) {
    for (RelNode input : rel.getInputs()) {
      visit(input);
    }
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            visit(subQuery.rel);
            return super.visitSubQuery(subQuery);
          }
        });

    if (rel instanceof Aggregate aggregate) {
      atAggregate(aggregate);
      return;
    }
    if (rel instanceof Filter filter) {
      TableScan leaf = chainLeaf(filter.getInput());
      if (leaf != null) {
        collect(filter, filter.getCondition(), leaf, Disclosure.DISCLOSURE_TEST);
      }
      return;
    }
    if (rel instanceof Project project) {
      TableScan leaf = chainLeaf(project.getInput());
      if (leaf != null) {
        for (RexNode expr : project.getProjects()) {
          collect(project, expr, leaf, Disclosure.DISCLOSURE_TEST);
        }
      }
    }
  }

  /**
   * The aggregate form (§2): a test of a permitted shape as the {@code FILTER} of a permitted
   * aggregate over a population-only column, guarded by that column's floor.
   *
   * <p>The {@code FILTER} is a column of the projection below the aggregate — that is what
   * {@code SqlToRelConverter} makes of it — so the lift is recorded against that projection, and the
   * guard against this aggregate's call.
   */
  private void atAggregate(Aggregate aggregate) {
    if (!(aggregate.getInput() instanceof Project project)) {
      return;
    }
    TableScan leaf = chainLeaf(project.getInput());
    if (leaf == null) {
      return;
    }
    EntitlementPass.LeafFold fold = folds.get(leaf);
    List<FilteredGuard> guarded = new ArrayList<>();
    List<AggregateCall> calls = aggregate.getAggCallList();
    for (int c = 0; c < calls.size(); c++) {
      AggregateCall call = calls.get(c);
      if (call.filterArg < 0 || call.filterArg >= project.getProjects().size()) {
        continue;
      }
      // The converter writes a FILTER as `IS TRUE(<condition>)` in the projection below the
      // aggregate, so what is looked for is the comparison inside it; what the substitution then
      // replaces is that comparison, and the IS TRUE around it is the statement's own.
      Found found =
          find(project.getProjects().get(call.filterArg), leaf, Disclosure.DISCLOSURE_AGGREGATE_ONLY);
      if (found == null) {
        continue;
      }

      // "A permitted aggregate over the table": the call has to be one the column's own allow-list
      // names, which is the same list §3.4 judges every other consumer of it by, and it is asked
      // *before* anything is lifted — a lift is what tells the trace this is not a raw use, so an
      // aggregate outside the list must never reach one. COUNT(*) FILTER takes no argument and is
      // permitted exactly when COUNT is on the list.
      DescriptorExpressions.Column entitled = fold.descriptor().column(found.match().tableColumn());
      String function = call.getAggregation().getName().toUpperCase(Locale.ROOT);
      if (entitled == null || !entitled.allowList().contains(function)) {
        continue;
      }

      lift(leaf, project, found.expression(), found.match());
      int floor = options.minGroupSize(entitled.minGroupSize());
      if (floor > 1) {
        guarded.add(new FilteredGuard(c, floor));
      }
    }
    if (!guarded.isEmpty()) {
      guards.put(aggregate, guarded);
    }
  }

  /**
   * The entitled leaf at the bottom of this row-preserving chain, or null.
   *
   * <p>Filters and sorts keep a row a row and keep every column where it was, so an expression above
   * them indexes the leaf's own row and the leaf's derived column can be addressed from there. A
   * projection renumbers, and everything else combines rows; the chain stops at either.
   */
  private @Nullable TableScan chainLeaf(RelNode rel) {
    if (rel instanceof TableScan scan) {
      return folds.containsKey(scan) ? scan : null;
    }
    if (rel instanceof Filter filter) {
      return chainLeaf(filter.getInput());
    }
    if (rel instanceof Sort sort) {
      return chainLeaf(sort.getInput());
    }
    return null;
  }

  /** Every permitted comparison inside {@code expr}, outermost first, lifted. */
  private void collect(RelNode consumer, RexNode expr, TableScan leaf, Disclosure under) {
    Match match = match(expr, leaf, under);
    if (match != null) {
      lift(leaf, consumer, expr, match);
      return;
    }
    if (expr instanceof RexCall call) {
      for (RexNode operand : call.getOperands()) {
        collect(consumer, operand, leaf, under);
      }
    }
  }

  /** One permitted comparison and the sub-expression it is, without lifting anything. */
  private record Found(RexNode expression, Match match) {}

  /** The outermost permitted comparison inside {@code expr}, or null. No side effect. */
  private @Nullable Found find(RexNode expr, TableScan leaf, Disclosure under) {
    Match match = match(expr, leaf, under);
    if (match != null) {
      return new Found(expr, match);
    }
    if (expr instanceof RexCall call) {
      for (RexNode operand : call.getOperands()) {
        Found found = find(operand, leaf, under);
        if (found != null) {
          return found;
        }
      }
    }
    return null;
  }

  private void lift(TableScan leaf, RelNode consumer, RexNode expr, Match match) {
    List<Lifted> lifted = byLeaf.computeIfAbsent(leaf, key -> new ArrayList<>());
    Map<String, Integer> byText = ordinals.computeIfAbsent(leaf, key -> new LinkedHashMap<>());
    Integer ordinal = byText.get(match.comparison().toString());
    if (ordinal == null) {
      ordinal = lifted.size();
      lifted.add(new Lifted(match.tableColumn(), match.shape(), match.comparison()));
      byText.put(match.comparison().toString(), ordinal);
    }
    substitutions
        .computeIfAbsent(consumer, key -> new LinkedHashMap<>())
        .put(expr.toString(), ordinal);
  }

  // ------------------------------------------------------------------ the shapes

  /** One permitted comparison, restated over the leaf's raw row. */
  private record Match(int tableColumn, TestShape shape, RexNode comparison) {}

  /**
   * Whether {@code expr} is a comparison this leaf permits, and what it is over the raw row.
   *
   * <p>{@code SEARCH} is expanded before it is judged: the converter writes an {@code IN} list of
   * literals as a {@code Sarg} and one of parameters as an {@code OR} of equalities, and the two are
   * the same statement. The expansion is judged as the {@code OR} it is, which also keeps the
   * comparison this leaf computes in the vocabulary the executor already has.
   */
  private @Nullable Match match(RexNode expr, TableScan leaf, Disclosure under) {
    if (expr.getKind() == SqlKind.SEARCH) {
      RexNode expanded;
      try {
        expanded = RexUtil.expandSearch(rexBuilder, null, expr);
      } catch (RuntimeException unexpanded) {
        return null;
      }
      return expanded.getKind() == SqlKind.SEARCH ? null : match(expanded, leaf, under);
    }
    if (!(expr instanceof RexCall call)) {
      return null;
    }
    return switch (call.getKind()) {
      case EQUALS -> comparison(call, leaf, under, TestShape.TEST_SHAPE_EQUALS);
      case NOT_EQUALS -> comparison(call, leaf, under, TestShape.TEST_SHAPE_NOT_EQUALS);
      case OR -> disjunction(call, leaf, under);
      default -> null;
    };
  }

  /** {@code col = <bindable>} or {@code col <> <bindable>}, in either operand order. */
  private @Nullable Match comparison(
      RexCall call, TableScan leaf, Disclosure under, TestShape shape) {
    if (call.getOperands().size() != 2) {
      return null;
    }
    RexNode left = call.getOperands().get(0);
    RexNode right = call.getOperands().get(1);
    int column = testedColumn(left, leaf, under, shape);
    RexNode operand = right;
    boolean columnIsLeft = true;
    if (column < 0) {
      column = testedColumn(right, leaf, under, shape);
      operand = left;
      columnIsLeft = false;
    }
    if (column < 0 || !isBindable(operand)) {
      return null;
    }

    RexNode raw = rexBuilder.makeInputRef(EntitlementPass.rawColumnType(leaf, column), column);
    RexNode restated =
        columnIsLeft
            ? rexBuilder.makeCall(call.getType(), call.getOperator(), List.of(raw, operand))
            : rexBuilder.makeCall(call.getType(), call.getOperator(), List.of(operand, raw));
    return new Match(column, shape, restated);
  }

  /**
   * {@code col IN (a, b, …)} as the converter leaves it: a disjunction of equalities over one
   * column, every other operand bindable.
   *
   * <p>Matched as a whole and before its operands, so a rule permitting {@code IN} and not
   * {@code Equals} admits the list and not a single probe. Where the rule permits {@code Equals} the
   * disjunction is left to the operand walk instead, which lifts each equality on its own — a
   * disjunction of permitted equalities is permitted equalities, and refusing it while permitting
   * each of them in a statement of its own would be a rule about spelling.
   */
  private @Nullable Match disjunction(RexCall call, TableScan leaf, Disclosure under) {
    if (call.getOperands().size() < 2) {
      return null;
    }
    int column = -1;
    List<RexNode> restated = new ArrayList<>(call.getOperands().size());
    for (RexNode operand : call.getOperands()) {
      if (!(operand instanceof RexCall equality) || equality.getKind() != SqlKind.EQUALS) {
        return null;
      }
      Match one = comparison(equality, leaf, under, TestShape.TEST_SHAPE_IN);
      if (one == null || (column >= 0 && one.tableColumn() != column)) {
        return null;
      }
      column = one.tableColumn();
      restated.add(one.comparison());
    }
    return new Match(
        column,
        TestShape.TEST_SHAPE_IN,
        RexUtil.composeDisjunction(rexBuilder, restated));
  }

  /**
   * The table column {@code expr} is a bare reference to, when it is one and the leaf permits this
   * shape over it; -1 otherwise.
   *
   * <p>A bare reference and nothing else: {@code UPPER(col) = ?} is a function of the column and
   * {@code CAST(col AS …) = ?} is one too — a cast is a pass-through for a population aggregate
   * (§3.4) because it cannot select a row, and here it could, since the comparison it feeds is the
   * disclosure.
   */
  private int testedColumn(RexNode expr, TableScan leaf, Disclosure under, TestShape shape) {
    if (!(expr instanceof RexInputRef ref)) {
      return -1;
    }
    List<Integer> projection = EntitlementPass.projectionOf(leaf);
    if (ref.getIndex() < 0 || ref.getIndex() >= projection.size()) {
      return -1;
    }
    int column = projection.get(ref.getIndex());
    EntitlementPass.LeafFold fold = folds.get(leaf);
    Disclosed outcome = fold.map().of(column);
    boolean right =
        under == Disclosure.DISCLOSURE_TEST
            ? outcome == Disclosed.TESTED
            : outcome == Disclosed.AGGREGATE;
    return right && shapes(fold, column, under).contains(shape) ? column : -1;
  }

  /**
   * The shapes this leaf's still-reachable rules permit over one column under one verdict.
   *
   * <p>Read off the <em>folded</em> rules and never the descriptor's text (§3.5): the same statement
   * is legitimate for another principal, and a rule this principal can never match permits nothing.
   */
  private static Set<TestShape> shapes(
      EntitlementPass.@Nullable LeafFold fold, int column, Disclosure under) {
    if (fold == null || column < 0) {
      return Set.of();
    }
    DescriptorExpressions.Column entitled = fold.descriptor().column(column);
    if (entitled == null) {
      return Set.of();
    }
    Set<TestShape> shapes = EnumSet.noneOf(TestShape.class);
    for (int rule : fold.reachable().get(column).rules()) {
      if (entitled.rules().get(rule).getThen() == under) {
        shapes.addAll(entitled.rules().get(rule).getTestsList());
      }
    }
    return shapes;
  }

  /**
   * Whether an operand is one the policy admits beside the column: a dynamic parameter, a literal or
   * a context reference bound per request — anything, that is, that reads no column of the row.
   *
   * <p>A comparison with a <b>column</b> is refused precisely so that a {@code VALUES} list cannot
   * turn one probe into a thousand (§3), and a sub-query, a correlated field access and a window are
   * refused for the same reason: each of them is a value the statement can vary per row, and a
   * comparison that varies per row is not one probe but as many as there are rows.
   */
  private static boolean isBindable(RexNode operand) {
    boolean[] bindable = {true};
    operand.accept(
        new RexVisitorImpl<Void>(true) {
          @Override
          public Void visitInputRef(RexInputRef ref) {
            bindable[0] = false;
            return null;
          }

          @Override
          public Void visitSubQuery(RexSubQuery subQuery) {
            bindable[0] = false;
            return null;
          }

          @Override
          public Void visitFieldAccess(RexFieldAccess access) {
            bindable[0] = false;
            return null;
          }

          @Override
          public Void visitCorrelVariable(RexCorrelVariable variable) {
            bindable[0] = false;
            return null;
          }

          @Override
          public Void visitOver(RexOver over) {
            bindable[0] = false;
            return null;
          }
        });
    return bindable[0];
  }

  /** The shapes one leaf actually used, by column, for the report and the audit (§3, D261). */
  static ImmutableList<String> shapeNames(List<Lifted> lifted, int tableColumn) {
    Set<String> names = new LinkedHashSet<>();
    for (Lifted one : lifted) {
      if (one.tableColumn() == tableColumn) {
        names.add(one.shape().name().replace("TEST_SHAPE_", ""));
      }
    }
    return ImmutableList.copyOf(names);
  }
}
