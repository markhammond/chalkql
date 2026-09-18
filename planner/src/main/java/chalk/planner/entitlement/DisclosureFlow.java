package chalk.planner.entitlement;

import chalk.ir.v1.Rel;
import chalk.planner.plan.rel.ChalkContextScan;
import chalk.planner.plan.rel.ChalkHop;
import chalk.planner.plan.rel.ChalkIndexLookup;
import chalk.planner.plan.rel.ChalkLimit;
import chalk.planner.plan.rel.ChalkPartitionedScan;
import chalk.planner.plan.rel.ChalkSession;
import chalk.planner.plan.rel.ChalkTopN;
import chalk.planner.plan.rel.ChalkUnnest;
import chalk.planner.plan.rel.ProjectedRelOptTable;
import chalk.planner.plan.rel.SourceToLocalConverter;
import java.util.ArrayList;
import java.util.EnumMap;
import java.util.EnumSet;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Predicate;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.core.AggregateCall;
import org.apache.calcite.rel.core.Correlate;
import org.apache.calcite.rel.core.Filter;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.core.SetOp;
import org.apache.calcite.rel.core.Sort;
import org.apache.calcite.rel.core.TableFunctionScan;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.core.Uncollect;
import org.apache.calcite.rel.core.Values;
import org.apache.calcite.rel.core.Window;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexVisitorImpl;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * What each column of each node discloses, carried upward from the leaves
 * (docs/design/16-entitlements.md §3.12, D202).
 *
 * <p>Why this rather than {@code RelMetadataQuery.getColumnOrigins}: a redacted column is a
 * <em>constant</em> — a typed NULL or the type's empty value — and a constant has no column origin
 * at all, so the one disclosure a caller most needs to be told about is exactly the one origins
 * cannot carry. The flow starts where the fact is known, at the leaf the pass emitted, and takes the
 * meet upward through the shapes the rewritten tree has.
 *
 * <p>It runs on the tree the pass produced, before the optimiser: the pass preserves every node's
 * arity, so {@code RelRoot.fields} indexes it exactly as it indexed the tree before, and disclosure
 * is a semantic property the optimiser does not change.
 *
 * <p><b>The walk is exhaustive over the IR's rel kinds.</b> Every case below declares which
 * {@code Rel.KindCase}s it covers, {@link #COVERAGE} is read off those declarations, and a test
 * enumerates the IR's own kinds against it — so a kind added later breaks the build rather than
 * quietly degrading every report that meets it. The final case is a safety net rather than a
 * shrug: an unmodelled shape whose input carried anything other than {@code FULL} reads as
 * {@code PER_ROW}, which never tells a caller a value is full when nothing established that it is.
 */
final class DisclosureFlow {
  private final Map<RelNode, Disclosed[]> leaves;
  private final Map<RelNode, Disclosed[]> computed = new IdentityHashMap<>();

  private DisclosureFlow(Map<RelNode, Disclosed[]> leaves) {
    this.leaves = leaves;
  }

  /** The disclosure of each of {@code root}'s output columns, given what each emitted leaf says. */
  static Disclosed[] of(RelNode root, Map<RelNode, Disclosed[]> leaves) {
    return new DisclosureFlow(leaves).at(root);
  }

  // ------------------------------------------------------------------ the cases

  /** What one case does with a node whose width is {@code width}. */
  @FunctionalInterface
  private interface Rule {
    Disclosed[] apply(DisclosureFlow flow, RelNode rel, int width);
  }

  /** One case of the walk: what it matches, what it concludes, and which IR kinds it answers for. */
  private record Case(
      String name, Predicate<RelNode> matches, Rule rule, EnumSet<Rel.KindCase> covers) {}

  private static Case of(
      String name, Predicate<RelNode> matches, Rule rule, Rel.KindCase first, Rel.KindCase... rest) {
    return new Case(name, matches, rule, EnumSet.of(first, rest));
  }

  /**
   * In order; the first that matches decides. Order matters only where one shape is a subtype of
   * another — a context scan is a {@code TableScan} and must be recognised as itself first.
   */
  private static final List<Case> CASES =
      List.of(
          // A context table holds the principal's own bound values, which the principal supplied.
          of(
              "context-table",
              rel -> rel instanceof ChalkContextScan,
              (flow, rel, width) -> filled(width, Disclosed.FULL),
              Rel.KindCase.BOUND_TABLE),
          // Literals disclose themselves.
          of(
              "values",
              rel -> rel instanceof Values,
              (flow, rel, width) -> filled(width, Disclosed.FULL),
              Rel.KindCase.VIRTUAL_TABLE),
          // The leaves the pass emitted, and any scan-derived leaf that still carries the wrapper.
          of(
              "leaf",
              rel -> rel instanceof TableScan || rel instanceof ChalkIndexLookup,
              DisclosureFlow::leaf,
              Rel.KindCase.READ,
              Rel.KindCase.INDEX_LOOKUP),
          // Pass through by position: a filter drops rows and changes no value.
          of(
              "filter",
              rel -> rel instanceof Filter,
              (flow, rel, width) -> flow.at(rel.getInput(0)),
              Rel.KindCase.FILTER),
          // Pass through by position: ordering, offset, limit and the source boundary all reorder
          // or drop rows, and none of them touches a value.
          of(
              "row-preserving",
              rel ->
                  rel instanceof Sort
                      || rel instanceof ChalkLimit
                      || rel instanceof ChalkTopN
                      || rel instanceof SourceToLocalConverter,
              (flow, rel, width) -> flow.at(rel.getInput(0)),
              Rel.KindCase.SORT,
              Rel.KindCase.FETCH,
              Rel.KindCase.TOP_N,
              Rel.KindCase.REMOTE_QUERY),
          // The meet over every column each expression reads; an expression reading none is FULL.
          of("project", rel -> rel instanceof Project, DisclosureFlow::project, Rel.KindCase.PROJECT),
          of(
              "aggregate",
              rel -> rel instanceof Aggregate,
              DisclosureFlow::aggregate,
              Rel.KindCase.AGGREGATE,
              Rel.KindCase.HASH_AGGREGATE,
              Rel.KindCase.STREAM_AGGREGATE),
          // Concatenation by position, left then right — and left alone for a semi or anti join,
          // which projects no right column. A correlate is the same shape.
          of(
              "join",
              rel -> rel instanceof Join || rel instanceof Correlate,
              DisclosureFlow::join,
              Rel.KindCase.JOIN,
              Rel.KindCase.HASH_JOIN,
              Rel.KindCase.MERGE_JOIN,
              Rel.KindCase.NESTED_LOOP_JOIN,
              Rel.KindCase.AS_OF_JOIN,
              Rel.KindCase.LOOKUP_JOIN,
              // An adaptive join is a join whose branches the executor chooses between; a
              // MaterialisedInput is the slot standing for its shared input, and neither branch can
              // disclose more than the inputs this node already meets.
              Rel.KindCase.ADAPTIVE_JOIN,
              Rel.KindCase.MATERIALISED_INPUT),
          // Output i is input i of every branch, and the caller is told the least disclosing.
          of("set-op", rel -> rel instanceof SetOp, DisclosureFlow::branches, Rel.KindCase.SET_OP),
          // The same shape: one branch per partition, all of the table's row type.
          of(
              "partitioned-scan",
              rel -> rel instanceof ChalkPartitionedScan,
              DisclosureFlow::branches,
              Rel.KindCase.PARTITIONED_SCAN),
          of("window", rel -> rel instanceof Window, DisclosureFlow::window, Rel.KindCase.WINDOW),
          of(
              "unnest",
              rel -> rel instanceof ChalkUnnest || rel instanceof Uncollect,
              DisclosureFlow::unnest,
              Rel.KindCase.UNNEST),
          // A table function's own output columns are an arbitrary function of what it reads, so
          // they take the meet over everything beneath; the input row it prepends passes through by
          // position, which is the shape TUMBLE, HOP and SESSION all have.
          of(
              "table-function",
              rel ->
                  rel instanceof TableFunctionScan
                      || rel instanceof ChalkHop
                      || rel instanceof ChalkSession,
              DisclosureFlow::tableFunction,
              Rel.KindCase.TABLE_FUNCTION_SCAN,
              Rel.KindCase.HOP,
              Rel.KindCase.SESSION));

  /** Which case answers for each IR rel kind. The coverage test enumerates the IR against this. */
  static final Map<Rel.KindCase, String> COVERAGE = coverage();

  private static Map<Rel.KindCase, String> coverage() {
    Map<Rel.KindCase, String> out = new EnumMap<>(Rel.KindCase.class);
    for (Case handled : CASES) {
      for (Rel.KindCase kind : handled.covers()) {
        String earlier = out.putIfAbsent(kind, handled.name());
        if (earlier != null) {
          throw new IllegalStateException(
              "two cases claim " + kind + ": " + earlier + " and " + handled.name());
        }
      }
    }
    return Map.copyOf(out);
  }

  // ------------------------------------------------------------------ the walk

  private Disclosed[] at(RelNode rel) {
    Disclosed[] known = leaves.get(rel);
    if (known != null) {
      return known;
    }
    Disclosed[] cached = computed.get(rel);
    if (cached != null) {
      return cached;
    }
    Disclosed[] result = compute(rel);
    computed.put(rel, result);
    return result;
  }

  private Disclosed[] compute(RelNode rel) {
    int width = rel.getRowType().getFieldCount();
    for (Case handled : CASES) {
      if (handled.matches().test(rel)) {
        return handled.rule().apply(this, rel, width);
      }
    }

    // A shape no case models. "Decided per row" is the honest conservative reading — it never tells
    // a caller a value is full when nothing established that it is — but only where an input
    // carried something other than FULL, so an unentitled subtree still reads as itself.
    return filled(width, anythingBeneath(rel) == Disclosed.FULL ? Disclosed.FULL : Disclosed.PER_ROW);
  }

  // ------------------------------------------------------------------ the rules

  /**
   * A leaf the pass emitted answers from {@link #leaves} before this is reached. What is left is a
   * scan of a table with no entitlement — every column full — or a scan-derived node that still
   * carries the wrapper, whose map is by the <em>table's</em> ordinals and is translated through the
   * pruned projection the way every other reader of it does.
   */
  private static Disclosed[] leaf(DisclosureFlow flow, RelNode rel, int width) {
    RelOptTable table = rel.getTable();
    DisclosureMap map = EntitledRelOptTable.disclosureOf(table);
    if (map == null) {
      return filled(width, Disclosed.FULL);
    }
    ProjectedRelOptTable projected =
        table == null ? null : table.unwrap(ProjectedRelOptTable.class);
    Disclosed[] out = new Disclosed[width];
    for (int i = 0; i < width; i++) {
      int ordinal =
          projected != null && i < projected.projection().size() ? projected.projection().get(i) : i;
      out[i] = map.of(ordinal);
    }
    return out;
  }

  private static Disclosed[] project(DisclosureFlow flow, RelNode rel, int width) {
    Project project = (Project) rel;
    Disclosed[] in = flow.at(project.getInput());
    Disclosed[] out = new Disclosed[width];
    List<RexNode> exprs = project.getProjects();
    for (int i = 0; i < width; i++) {
      out[i] = meetOfRefs(exprs.get(i), in);
    }
    return out;
  }

  private static Disclosed[] aggregate(DisclosureFlow flow, RelNode rel, int width) {
    Aggregate aggregate = (Aggregate) rel;
    Disclosed[] in = flow.at(aggregate.getInput());
    Disclosed[] out = filled(width, Disclosed.FULL);
    int at = 0;
    for (int key : aggregate.getGroupSet()) {
      out[at++] = key < in.length ? in[key] : Disclosed.FULL;
    }
    for (AggregateCall call : aggregate.getAggCallList()) {
      // A population aggregate over a masked column is a value derived from masks, and that is
      // what a caller is told: `Masked`, not `Aggregate`. `Aggregate` is what the leaf's own
      // AGGREGATE outcome carries up through here.
      Disclosed measure = Disclosed.FULL;
      for (int argument : call.getArgList()) {
        if (argument < in.length) {
          measure = measure.meet(in[argument]);
        }
      }
      // And what the call's FILTER discloses, which is a value the measure is derived from as much
      // as its arguments are (D261): a count of a leaf's permitted comparisons is reported `Tested`,
      // and one guarded by the floor `Aggregate`, whose NULL is a withheld value rather than "no
      // rows". A FILTER over a raw population-only column is refused before there is a plan (§3.4),
      // so what can stand here is a mask, a placeholder or a comparison the leaf computed.
      if (call.filterArg >= 0 && call.filterArg < in.length) {
        measure = measure.meet(in[call.filterArg]);
      }
      out[at++] = measure;
    }
    return out;
  }

  /** Left's columns then right's, truncated to what the node actually projects. */
  private static Disclosed[] join(DisclosureFlow flow, RelNode rel, int width) {
    List<Disclosed> all = new ArrayList<>(width);
    for (RelNode input : rel.getInputs()) {
      all.addAll(List.of(flow.at(input)));
    }
    Disclosed[] out = filled(width, Disclosed.FULL);
    for (int i = 0; i < width && i < all.size(); i++) {
      out[i] = all.get(i);
    }
    return out;
  }

  /**
   * Output i is input i of every branch. The branches are alternative <em>rows</em> rather than
   * origins of one value, so a column the branches disclose differently is decided per row — a
   * masked column unioned with a full one really does hand the caller some masked values and some
   * raw ones — and a column they agree about keeps that name.
   */
  private static Disclosed[] branches(DisclosureFlow flow, RelNode rel, int width) {
    Disclosed[] out = null;
    for (RelNode input : rel.getInputs()) {
      Disclosed[] in = flow.at(input);
      if (out == null) {
        out = filled(width, Disclosed.FULL);
        for (int i = 0; i < width && i < in.length; i++) {
          out[i] = in[i];
        }
        continue;
      }
      for (int i = 0; i < width && i < in.length; i++) {
        out[i] = out[i] == in[i] ? out[i] : Disclosed.PER_ROW;
      }
    }
    return out == null ? filled(width, Disclosed.FULL) : out;
  }

  /**
   * The input's row passes through by position and the window calls are appended.
   *
   * <p>A call is judged on its <em>arguments</em> alone: {@code SUM(salary) OVER (…)} over a masked
   * salary is masked, and a {@code ROW_NUMBER()} over the same partition is full, because its value
   * is the position and not the value. Partition and order keys are therefore not read here — a
   * masked value used as a key is an ordinary untainted value (§3.1), and the alternative would
   * report every column of every window over an entitled table as redacted.
   */
  private static Disclosed[] window(DisclosureFlow flow, RelNode rel, int width) {
    Window window = (Window) rel;
    Disclosed[] in = flow.at(window.getInput());
    Disclosed[] out = filled(width, Disclosed.FULL);
    int at = 0;
    for (; at < width && at < in.length; at++) {
      out[at] = in[at];
    }
    for (Window.Group group : window.groups) {
      for (RexNode call : group.aggCalls) {
        if (at < width) {
          out[at++] = meetOfRefs(call, in);
        }
      }
    }
    return out;
  }

  /**
   * The input's row passes through by position; the elements the list produced take that list
   * column's own disclosure, and the ordinality — which counts elements rather than reading one —
   * is full.
   */
  private static Disclosed[] unnest(DisclosureFlow flow, RelNode rel, int width) {
    Disclosed[] in = flow.at(rel.getInput(0));
    if (!(rel instanceof ChalkUnnest unnest)) {
      // A bare Uncollect flattens the one column its input holds, so every column it produces is
      // that column's, and the ordinality is no less disclosing than it.
      return filled(width, meetOf(in));
    }
    Disclosed[] out = filled(width, Disclosed.FULL);
    int at = 0;
    for (; at < width && at < in.length; at++) {
      out[at] = in[at];
    }
    Disclosed element =
        unnest.listColumn() < in.length ? in[unnest.listColumn()] : Disclosed.FULL;
    if (at < width) {
      out[at++] = element;
    }
    // With ordinality: the position of an element is not the element.
    return out;
  }

  /**
   * The input row a windowing table function prepends passes through by position; every column the
   * function itself produces takes the meet over what it could have read, which is everything
   * beneath it.
   */
  private static Disclosed[] tableFunction(DisclosureFlow flow, RelNode rel, int width) {
    if (rel.getInputs().isEmpty()) {
      return filled(width, Disclosed.FULL);
    }
    Disclosed[] in = flow.at(rel.getInput(0));
    Disclosed beneath = anythingBeneath(flow, rel);
    Disclosed[] out = filled(width, beneath);
    for (int i = 0; i < width && i < in.length; i++) {
      out[i] = in[i];
    }
    return out;
  }

  // ------------------------------------------------------------------ helpers

  private Disclosed anythingBeneath(RelNode rel) {
    return anythingBeneath(this, rel);
  }

  private static Disclosed anythingBeneath(DisclosureFlow flow, RelNode rel) {
    Disclosed anything = Disclosed.FULL;
    for (RelNode input : rel.getInputs()) {
      anything = anything.meet(meetOf(flow.at(input)));
    }
    return anything;
  }

  private static Disclosed meetOf(Disclosed[] columns) {
    Disclosed meet = Disclosed.FULL;
    for (Disclosed one : columns) {
      meet = meet.meet(one);
    }
    return meet;
  }

  /**
   * The meet over every column whose <em>value</em> the expression reads; {@code FULL} when it reads
   * none.
   *
   * <p>A window function is judged on its arguments and not on its window: the pass runs before
   * {@code PROJECT_TO_WINDOW}, so a window function is still a {@code RexOver} inside a projection
   * here, and visiting it deeply would count its partition and order keys as reads. It is
   * {@code SUM(salary) OVER (…)} that discloses a salary; a {@code ROW_NUMBER() OVER (PARTITION BY
   * salary)} discloses a position, and a masked value used as a key is ordinary untainted data
   * (§3.1).
   */
  private static Disclosed meetOfRefs(RexNode expr, Disclosed[] in) {
    Disclosed[] meet = {Disclosed.FULL};
    RexVisitorImpl<@Nullable Void> reads =
        new RexVisitorImpl<@Nullable Void>(true) {
          @Override
          public @Nullable Void visitInputRef(RexInputRef ref) {
            if (ref.getIndex() < in.length) {
              meet[0] = meet[0].meet(in[ref.getIndex()]);
            }
            return null;
          }

          @Override
          public @Nullable Void visitOver(org.apache.calcite.rex.RexOver over) {
            for (RexNode operand : over.getOperands()) {
              operand.accept(this);
            }
            return null;
          }
        };
    expr.accept(reads);
    return meet[0];
  }

  private static Disclosed[] filled(int width, Disclosed value) {
    Disclosed[] out = new Disclosed[width];
    java.util.Arrays.fill(out, value);
    return out;
  }
}
