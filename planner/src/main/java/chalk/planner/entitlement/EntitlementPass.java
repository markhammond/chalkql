package chalk.planner.entitlement;

import chalk.ir.v1.Disclosure;
import chalk.ir.v1.DisclosureRule;
import chalk.ir.v1.TableEntitlement;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.rpc.v1.PlaceholderPolicy;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.EnumSet;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import org.apache.calcite.plan.RelOptPredicateList;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.logical.LogicalFilter;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexExecutor;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexSimplify;
import org.apache.calcite.rex.RexSubQuery;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The enforcement rewrite (docs/design/16-entitlements.md §3.1–§3.3, D195–D197).
 *
 * <p>One pass over the converted tree, between {@code SqlToRelConverter} and the Hep pre-pass, and
 * installed only when the registered catalog carries an entitlement. Every {@code TableScan} of an
 * entitled table — each occurrence separately, since one table may appear several times, and
 * sub-query relations, correlates and table-function inputs included — becomes
 *
 * <pre>
 *   Project_D   one expression per table column: that column's sanitiser
 *     Filter_R  the folded row predicate, directly on the scan
 *       Scan    the table, every column
 * </pre>
 *
 * and nothing else in the tree changes. {@code Filter_R} sits below every mask so that it is the
 * first thing the pushdown rules meet over the source (§3.7); {@code Project_D} reads raw columns
 * and is the only place in the plan that may.
 *
 * <p>The <em>query-field rule</em> of the earlier drafts is gone (D195): a predicate, join key,
 * grouping, ordering or window on a masked column operates on the sanitised value, so an agent's
 * {@code ORDER BY last_name} sorts by initials rather than returning nothing.
 */
public final class EntitlementPass {
  private final DescriptorConverter converter;
  private final BoundContext context;
  private final PolicyOptions options;
  private final RexBuilder rexBuilder;
  private final RelDataTypeFactory typeFactory;
  private final RexSimplify simplify;

  /** One conversion per entitled table per request, keyed by the resolved table (§3.2). */
  private final Map<RelOptTable, DescriptorExpressions> converted = new IdentityHashMap<>();

  /** The leaves the pass produced, in the order it met them — the report's own order (§3.12). */
  private final List<DisclosureMap> leaves = new ArrayList<>();

  /**
   * Each emitted {@code Project_D}, and what each of its columns discloses. This is where the
   * disclosure flow of §3.12 starts: a redacted column is a constant and a constant has no column
   * origin, so the one disclosure a caller most needs is the one origins cannot carry.
   */
  private final Map<RelNode, Disclosed[]> emitted = new IdentityHashMap<>();

  /**
   * The folded {@code Filter_R} of each entitled table, by qualified name — what clause 3 of the
   * taint check looks for in the finished plan's pulled-up predicates (§3.10).
   */
  private final Map<String, RexNode> rowPredicates = new java.util.LinkedHashMap<>();

  /**
   * The cross-source associations this catalog declares (D270 (c)): what a step across two sources
   * resolves through, a foreign key being one source's claim about a table of its own schema.
   */
  private final ImmutableList<chalk.ir.v1.Association> associations;

  private EntitlementPass(
      DescriptorConverter converter,
      BoundContext context,
      PolicyOptions options,
      List<chalk.ir.v1.Association> associations,
      RexBuilder rexBuilder,
      RexExecutor executor) {
    this.converter = converter;
    this.context = context;
    this.options = options;
    this.associations = ImmutableList.copyOf(associations);
    this.rexBuilder = rexBuilder;
    this.typeFactory = rexBuilder.getTypeFactory();
    this.simplify = new RexSimplify(rexBuilder, RelOptPredicateList.EMPTY, executor);
  }

  /**
   * What the pass produced: the rewritten tree, one map per entitled leaf occurrence, and what each
   * of the tree's own output columns discloses (§3.12).
   */
  public record Result(
      RelNode rel,
      ImmutableList<DisclosureMap> leaves,
      ImmutableList<Disclosed> disclosures,
      Map<String, RexNode> rowPredicates,
      ImmutableList<PolicyExplain.Table> explained,
      @Nullable Siblings siblings,
      /** Each {@code through} join the finished plan must still hold (§3.10 as extended, §3.13). */
      ImmutableList<TaintCheck.ThroughEvidence> throughJoins) {
    public boolean rewroteAnything() {
      return !leaves.isEmpty();
    }
  }

  /**
   * Where the sibling disclosure columns are, when the request asked for them (§3.12, D207).
   *
   * @param at the first of them; output column {@code f}'s sibling is at {@code at + f}
   * @param present whether output column {@code f} has an entitled origin and therefore a sibling
   */
  public record Siblings(int at, boolean[] present) {}

  /**
   * Runs the pass over {@code rel}, in three phases.
   *
   * <ol>
   *   <li><b>Fold.</b> Every entitled leaf's descriptor is converted and folded against this
   *       principal's context, giving the leaf's disclosure map — computed here rather than in the
   *       rewrite because the next phase needs to know which columns are population-only before a
   *       leaf can decide what to emit for them (§3.5).
   *   <li><b>Trace.</b> Each population-only column's bare pass-throughs are followed upward to
   *       every consumer, and any consumer that is not an allow-listed population aggregate is
   *       {@code POLICY} (§3.4). The aggregates that survive are the ones the guard rewrites.
   *   <li><b>Rewrite.</b> The leaves are emitted and the consuming aggregates guarded (§3.6).
   * </ol>
   */
  public static Result apply(
      RelNode rel,
      DescriptorConverter converter,
      BoundContext context,
      PolicyOptions options,
      List<chalk.ir.v1.Association> associations,
      RexExecutor executor) {
    EntitlementPass pass =
        new EntitlementPass(
            converter, context, options, associations, rel.getCluster().getRexBuilder(), executor);
    RelNode occurrences = pass.distinctOccurrences(rel);
    // Whether this statement is one a statistical column may be read raw in (§3.4, D203) — every
    // output an aggregate or a group key, and no window. It is a property of the whole statement, so
    // it is answered before any leaf decides what to emit.
    pass.statisticalStatement = StatisticalScope.qualifies(occurrences);
    pass.fold(occurrences, List.of());
    // A window over a statistical column is refused whatever the statement's shape (D203, F43):
    // the opt-in is query-set-size control and a partition can be one row, so a window cannot be
    // controlled — and degrading to the mask instead would be refusing it silently.
    StatisticalScope.refuseWindow(occurrences, pass.folds);
    if (pass.anyStatistical()) {
      // On the received tree, before Filter_R is there: a subject grant's own `id IN (@ctx.…)` reads
      // exactly like the pinning this refuses.
      StatisticalScope.refusePinning(occurrences, pass.folds);
    }
    // The comparisons a tested column's rules permit, found on the tree the pass received — where a
    // column reference is still the scan's own (D261, 36-test-verdict.md §2). It runs before the
    // trace, because a comparison the leaf will compute is not a use of the raw value here, which is
    // exactly what §3.4 would otherwise refuse an aggregate's FILTER for.
    pass.lifts =
        TestLift.run(occurrences, pass.folds, options, rel.getCluster().getRexBuilder());
    PopulationTrace.Result traced =
        PopulationTrace.run(occurrences, pass.folds, options, pass.lifts);
    pass.guards = new java.util.IdentityHashMap<>(traced.guards());
    pass.suppressions = traced.suppressions();
    // The aggregate form's own guards (§2, D261): a permitted aggregate whose FILTER is a lifted
    // test is guarded over the population that filter leaves, which is the population it reports on.
    for (Map.Entry<org.apache.calcite.rel.core.Aggregate, List<TestLift.FilteredGuard>> entry :
        pass.lifts.guards().entrySet()) {
      List<PopulationTrace.Guard> here =
          pass.guards.computeIfAbsent(entry.getKey(), key -> new ArrayList<>());
      for (TestLift.FilteredGuard guard : entry.getValue()) {
        // COUNT(*) is the guarding count: §3.6 guards on COUNT(c), which for a NOT NULL column is
        // COUNT(*) by the time the converter is done with it (F43), and a filtered count reads no
        // column of the aggregate's input for a COUNT(c) to be formed over. ADR 0042.
        here.add(new PopulationTrace.Guard(guard.callIndex(), /* argument= */ -1, guard.floor()));
      }
    }
    RelNode rewritten = pass.narrow(pass.rewrite(occurrences), occurrences);
    if (pass.carriers.isEmpty()) {
      return new Result(
          rewritten,
          ImmutableList.copyOf(pass.leaves),
          ImmutableList.copyOf(DisclosureFlow.of(rewritten, pass.emitted)),
          java.util.Collections.unmodifiableMap(pass.rowPredicates),
          pass.explain(options),
          null,
          ImmutableList.copyOf(pass.throughEvidence));
    }

    // The sibling disclosure columns (§3.12, D207): the leaves computed what each row disclosed, and
    // this is where it reaches the root — exactly as far as the statement's shape allows it to.
    DisclosureColumns.Carried carried =
        DisclosureColumns.pullUp(rewritten, pass.carriers, pass.rexBuilder);
    Disclosed[] wide = DisclosureFlow.of(carried.rel(), pass.emitted);
    int width = rewritten.getRowType().getFieldCount();
    Disclosed[] flow = new Disclosed[width * 2];
    boolean[] present = new boolean[width];
    for (int f = 0; f < width; f++) {
      flow[f] = wide[carried.originals()[f]];
      flow[width + f] = Disclosed.FULL;
      present[f] = !carried.sources().get(f).isEmpty() || flow[f] != Disclosed.FULL;
    }
    return new Result(
        DisclosureColumns.siblings(
            carried, java.util.Arrays.copyOf(flow, width), pass.rexBuilder),
        ImmutableList.copyOf(pass.leaves),
        ImmutableList.copyOf(flow),
        java.util.Collections.unmodifiableMap(pass.rowPredicates),
        pass.explain(options),
        new Siblings(width, present),
        ImmutableList.copyOf(pass.throughEvidence));
  }

  // ------------------------------------------------------------------ phase 0: the occurrences

  /**
   * The same tree with every entitled scan a node of its own (§3.1: "each occurrence separately").
   *
   * <p>What the converter hands the pass is a <em>graph</em>, not a tree: two references to one
   * table in one statement — a self-join, a table read in both branches of a {@code UNION} — can
   * arrive as one {@code TableScan} object appearing twice, and every phase here keys on scan
   * identity, deliberately, because the fold simplifies each leaf's rules under <em>that leaf's</em>
   * own conjuncts (V63). Shared identity therefore made the last occurrence's fold the answer for
   * all of them, which is the one thing §3.1 says must not happen: a self-join of an entitled table
   * where one occurrence is narrowed reported both sides alike. Giving each occurrence its own node
   * first costs one rebuild of a tree the pass rebuilds anyway.
   */
  private RelNode distinctOccurrences(RelNode rel) {
    if (rel instanceof TableScan scan) {
      return descriptorOf(scan) == null ? scan : freshOccurrence(scan);
    }

    List<RelNode> inputs = rel.getInputs();
    List<RelNode> rebuilt = new ArrayList<>(inputs.size());
    boolean changed = false;
    for (RelNode input : inputs) {
      RelNode next = distinctOccurrences(input);
      changed |= next != input;
      rebuilt.add(next);
    }

    RelNode result = changed ? rel.copy(rel.getTraitSet(), rebuilt) : rel;
    return result.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            RexSubQuery visited = (RexSubQuery) super.visitSubQuery(subQuery);
            RelNode inner = distinctOccurrences(visited.rel);
            return inner == visited.rel ? visited : visited.clone(inner);
          }
        });
  }

  /** A new node reading exactly what {@code scan} read, of whichever kind the converter made. */
  private static TableScan freshOccurrence(TableScan scan) {
    if (scan instanceof chalk.planner.plan.rel.ChalkTableScan) {
      return chalk.planner.plan.rel.ChalkTableScan.create(scan.getCluster(), scan.getTable());
    }
    return org.apache.calcite.rel.logical.LogicalTableScan.create(
        scan.getCluster(), scan.getTable(), scan.getHints());
  }

  // ------------------------------------------------------------------ phase 1: the fold

  /** One entitled leaf occurrence, folded: what it filters, what it discloses and why. */
  record LeafFold(
      ChalkTable table,
      DescriptorExpressions descriptor,
      @Nullable RexNode predicate,
      boolean trusted,
      RexSimplify underFilter,
      List<Reachable> reachable,
      DisclosureMap map,
      List<RexNode> statementConjuncts,
      java.util.Set<Integer> statistical,
      java.util.Set<Integer> declaredStatistical,
      /** The parents this leaf's visibility derives through, or null when it derives through none. */
      @Nullable Through through) {

    /** What the oracle of §3.12 says about the parents, or nothing where there are none. */
    List<ExplainedParent> explainedParents() {
      if (through == null) {
        return List.of();
      }
      List<ExplainedParent> parents = new ArrayList<>(through.layout().entries().size());
      for (int k = 0; k < through.layout().entries().size(); k++) {
        ThroughEntry entry = through.layout().entries().get(k);
        if (entry.path() != null) {
          continue;
        }
        ChalkTable parent = entry.parent().unwrap(ChalkTable.class);
        parents.add(
            new ExplainedParent(
                parent.schemaName(),
                parent.tableName(),
                entry.column(),
                parent.descriptor().getColumns(entry.parentColumn()).getName(),
                through.elided()[k]));
      }
      return parents;
    }

    /** The same for the declared paths (D265 §6): the route, the fold and the key it joins on. */
    List<ExplainedPath> explainedPaths() {
      if (through == null) {
        return List.of();
      }
      List<ExplainedPath> paths = new ArrayList<>();
      for (int k = 0; k < through.layout().entries().size(); k++) {
        ThroughEntry entry = through.layout().entries().get(k);
        if (entry.path() == null) {
          continue;
        }
        PathPlan path = entry.path();
        ChalkTable endpoint = path.endpoint().unwrap(ChalkTable.class);
        List<ExplainedStep> steps = new ArrayList<>(path.onward().size() + 1);
        ChalkTable base = path.base().unwrap(ChalkTable.class);
        ChalkTable from = table();
        steps.add(
            new ExplainedStep(
                base.schemaName(),
                base.tableName(),
                from.descriptor().getColumns(entry.column()).getName(),
                base.descriptor().getColumns(path.baseKey()).getName(),
                path.related()));
        ChalkTable at = base;
        for (PathStep step : path.onward()) {
          ChalkTable to = step.table().unwrap(ChalkTable.class);
          steps.add(
              new ExplainedStep(
                  to.schemaName(),
                  to.tableName(),
                  at.descriptor().getColumns(step.fromColumn()).getName(),
                  to.descriptor().getColumns(step.toColumn()).getName(),
                  false));
          at = to;
        }
        paths.add(
            new ExplainedPath(
                path.kind(),
                ImmutableList.copyOf(steps),
                endpoint.schemaName(),
                endpoint.tableName(),
                through.elided()[k],
                through.dropped()[k],
                path.endpointPredicate() == null ? "" : path.endpointPredicate().toString(),
                path.pathPredicate() == null ? "" : path.pathPredicate().toString(),
                entry.column()));
      }
      return paths;
    }
  }

  /** One resolved parent, as the oracle of §3.12 reports it (§3.13, D230). */
  record ExplainedParent(
      String schema, String table, int column, String parentColumnName, boolean elided) {}

  /** One resolved path, as the oracle of §3.12 reports it (D265 §6). */
  record ExplainedPath(
      String kind,
      ImmutableList<ExplainedStep> steps,
      String endpointSchema,
      String endpointTable,
      boolean elided,
      boolean dropped,
      String endpointPredicate,
      String pathPredicate,
      int keyColumn) {}

  /** One step of a resolved path, by the names the policy wrote. */
  record ExplainedStep(
      String schema, String table, String fromColumn, String toColumn, boolean toChild) {}

  /** Every entitled leaf's fold, by scan identity: two occurrences are two leaves (§3.1). */
  private final Map<TableScan, LeafFold> folds = new IdentityHashMap<>();

  /** Which aggregate calls the guard rewrites, filled by the trace (§3.6). */
  private Map<org.apache.calcite.rel.core.Aggregate, List<PopulationTrace.Guard>> guards =
      java.util.Collections.emptyMap();

  /** The comparisons each leaf computes for a tested column, and where they are read (D261). */
  private TestLift.Plan lifts = TestLift.Plan.EMPTY;

  /**
   * How many derived test columns each rewritten node carries beyond the row it was typed against
   * (D261). A leaf that lifts nothing is absent, which is every leaf of every catalog that grants no
   * test at all.
   */
  private final Map<RelNode, Integer> widened = new IdentityHashMap<>();

  /** Whether this statement is one a statistical column may be read raw in (§3.4, D203). */
  private boolean statisticalStatement;

  /** Which aggregates a raw statistical predicate or key shaped, and the floor each takes (§3.6). */
  private Map<org.apache.calcite.rel.core.Aggregate, Integer> suppressions =
      java.util.Collections.emptyMap();

  /**
   * The oracle of §3.12, one entry per entitled table — the first occurrence of each, since a table
   * read twice is one table to a caller asking what its policy resolved to.
   */
  private ImmutableList<PolicyExplain.Table> explain(PolicyOptions options) {
    java.util.Map<String, PolicyExplain.Table> byName = new java.util.LinkedHashMap<>();
    for (Map.Entry<TableScan, LeafFold> entry : folds.entrySet()) {
      LeafFold fold = entry.getValue();
      String name = fold.map().qualifiedName();
      if (!byName.containsKey(name)) {
        byName.put(name, PolicyExplain.of(fold, fullRowType(entry.getKey()), options));
      }
    }
    return ImmutableList.copyOf(byName.values());
  }

  private boolean anyStatistical() {
    for (LeafFold fold : folds.values()) {
      if (!fold.statistical().isEmpty()) {
        return true;
      }
    }
    return false;
  }

  /**
   * The fold, carrying <em>the leaf's own conjuncts</em> down to it (§2 item 3, §3.3).
   *
   * <p>Those are the folded row predicate's and, above it, the conjuncts of any {@code Filter} the
   * statement itself put directly over the scan — which is the shape {@code SqlToRelConverter}
   * produces for a {@code WHERE} over one table. This is what §3.4's remedy relies on: under
   * {@code WHERE org_id = 2} a mixed principal's disclosure is the constant the rules say for
   * organisation 2, so a population-only column stops being population-only and a per-row mask
   * becomes a constant one. Only a chain of filters is followed, because a projection in between
   * renumbers the columns and a conjunct over the renumbered row is not one over the leaf's.
   */
  private void fold(RelNode rel, List<RexNode> conjuncts) {
    if (rel instanceof TableScan scan) {
      DescriptorExpressions descriptor = descriptorOf(scan);
      if (descriptor != null) {
        folds.put(scan, fold(scan, descriptor, conjuncts));
      }
      return;
    }
    if (rel instanceof org.apache.calcite.rel.core.Filter filter) {
      List<RexNode> below = new ArrayList<>(conjuncts);
      below.addAll(RelOptUtilConjunctions.of(filter.getCondition()));
      fold(filter.getInput(), below);
    } else {
      for (RelNode input : rel.getInputs()) {
        fold(input, List.of());
      }
    }
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            fold(subQuery.rel, List.of());
            return super.visitSubQuery(subQuery);
          }
        });
  }

  // ------------------------------------------------------------------ phase 3: the walk

  private RelNode rewrite(RelNode rel) {
    if (rel instanceof TableScan scan) {
      LeafFold leaf = folds.get(scan);
      return leaf == null ? scan : emit(scan, leaf);
    }

    List<RelNode> inputs = rel.getInputs();
    List<RelNode> rewritten = new ArrayList<>(inputs.size());
    for (RelNode input : inputs) {
      rewritten.add(rewrite(input));
    }

    // A leaf that lifted a comparison hands back a wider row than the statement was typed against
    // (D261): the derived booleans are appended, so every column the statement reads keeps its
    // index and only a node whose own row type is its input's grows with it. Filters and sorts
    // carry them on, a projection reads what it needs of them and ends the carry, and anything else
    // — a join, an aggregate, a set operation — is handed the row it was typed against.
    RelNode carried = carry(rel, rewritten);
    if (carried != null) {
      return carried;
    }
    boolean changed = false;
    boolean retyped = false;
    for (int i = 0; i < rewritten.size(); i++) {
      rewritten.set(i, narrow(rewritten.get(i), inputs.get(i)));
      changed |= rewritten.get(i) != inputs.get(i);
      retyped |= RelRetyper.differs(inputs.get(i).getRowType(), rewritten.get(i).getRowType());
    }

    RelNode result;
    if (retyped) {
      result = RelRetyper.rebuild(rel, rewritten);
    } else if (changed) {
      result = rel.copy(rel.getTraitSet(), rewritten);
    } else {
      result = rel;
    }

    // The group-size guard, on the aggregate the trace found consuming a population-only column
    // (§3.6). It is a projection over the aggregate rather than a filter, so a pushed aggregate
    // carries the extra COUNT into the source and the guard stays local.
    List<PopulationTrace.Guard> guarded = guards.get(rel);
    Integer suppress = suppressions.get(rel);
    if ((guarded != null || suppress != null)
        && result instanceof org.apache.calcite.rel.core.Aggregate aggregate) {
      return GroupSizeGuard.apply(
          aggregate,
          guarded == null ? List.of() : guarded,
          suppress == null ? 0 : suppress,
          rexBuilder);
    }

    // Chalk converts with `expand = false`, so a sub-query is still a RexSubQuery inside a Filter,
    // Project or Join condition rather than a Correlate. Its relation is a tree of its own and the
    // pass owns it too, or an entitled table read inside an EXISTS would be read raw.
    return result.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            RexSubQuery visited = (RexSubQuery) super.visitSubQuery(subQuery);
            RelNode inner = rewrite(visited.rel);
            return inner == visited.rel ? visited : visited.clone(inner);
          }
        });
  }

  // ------------------------------------------------------------------ the test lift (D261)

  /**
   * {@code rel} rebuilt over an input that carries derived test columns, or null when it is not one
   * of the three kinds that can carry them.
   *
   * <p>A {@code Filter} and a {@code Sort} take their row type from their input, so they grow with
   * it and hand the columns on; a {@code Project} states its own, so it reads what it needs of them
   * and the carry ends there. Every other kind makes a row out of several and is handed the row the
   * statement was typed against, which {@link #narrow} restores.
   */
  private @Nullable RelNode carry(RelNode rel, List<RelNode> rewritten) {
    if (rewritten.size() != 1) {
      return null;
    }
    RelNode in = rewritten.get(0);
    int extra = widened.getOrDefault(in, 0);
    if (extra == 0) {
      return null;
    }
    int base = in.getRowType().getFieldCount() - extra;

    if (rel instanceof org.apache.calcite.rel.core.Filter filter) {
      RelNode result =
          LogicalFilter.create(in, substitute(filter.getCondition(), rel, in, base));
      widened.put(result, extra);
      return result;
    }
    if (rel instanceof org.apache.calcite.rel.core.Sort sort) {
      RelNode result =
          org.apache.calcite.rel.logical.LogicalSort.create(
              in, sort.getCollation(), sort.offset, sort.fetch);
      widened.put(result, extra);
      return result;
    }
    if (rel instanceof org.apache.calcite.rel.core.Project project) {
      List<RexNode> projects = new ArrayList<>(project.getProjects().size());
      for (RexNode expr : project.getProjects()) {
        projects.add(substitute(expr, rel, in, base));
      }
      return LogicalProject.create(
          in,
          project.getHints(),
          projects,
          project.getRowType().getFieldNames(),
          project.getVariablesSet());
    }
    return null;
  }

  /**
   * Every expression of {@code at} the leaf computes instead, replaced by a reference to the column
   * it computes it in (§2, D261).
   *
   * <p>Outermost first, and never inside one that matched: a comparison the leaf computes is one
   * value, and what the statement wrote around it is unchanged.
   */
  private RexNode substitute(RexNode expr, RelNode at, RelNode in, int base) {
    int ordinal = lifts.ordinalAt(at, expr);
    if (ordinal >= 0) {
      return rexBuilder.makeInputRef(
          in.getRowType().getFieldList().get(base + ordinal).getType(), base + ordinal);
    }
    if (!(expr instanceof org.apache.calcite.rex.RexCall call)) {
      return expr;
    }
    List<RexNode> operands = new ArrayList<>(call.getOperands().size());
    boolean changed = false;
    for (RexNode operand : call.getOperands()) {
      RexNode next = substitute(operand, at, in, base);
      changed |= next != operand;
      operands.add(next);
    }
    return changed ? rexBuilder.makeCall(call.getType(), call.getOperator(), operands) : expr;
  }

  /**
   * {@code rewritten} back at the arity {@code original} had, where a leaf's derived test columns
   * have travelled as far as they can (D261).
   *
   * <p>A projection of the row the statement was typed against, and nothing else: the columns it
   * drops are the leaf's own and no node above ever indexed them.
   */
  private RelNode narrow(RelNode rewritten, RelNode original) {
    int extra = widened.getOrDefault(rewritten, 0);
    if (extra == 0) {
      return rewritten;
    }
    int base = rewritten.getRowType().getFieldCount() - extra;
    List<RexNode> projects = new ArrayList<>(base);
    for (int i = 0; i < base; i++) {
      projects.add(rexBuilder.makeInputRef(rewritten, i));
    }
    return LogicalProject.create(
        rewritten,
        ImmutableList.<org.apache.calcite.rel.hint.RelHint>of(),
        projects,
        original.getRowType().getFieldNames(),
        java.util.Set.<org.apache.calcite.rel.core.CorrelationId>of());
  }

  /** The converted descriptor for this scan's table, or null when the table carries none. */
  private @Nullable DescriptorExpressions descriptorOf(TableScan scan) {
    RelOptTable relOptTable = scan.getTable();
    ChalkTable table = relOptTable.unwrap(ChalkTable.class);
    if (table == null || !table.descriptor().hasEntitlement()) {
      return null;
    }
    return converted.computeIfAbsent(
        relOptTable,
        key -> DescriptorExpressions.of(table, key, converter, layoutOf(scan).from()));
  }

  // ------------------------------------------------------------------ through (§3.13)

  /** The parents one entitled table derives its visibility through, resolved against the schema. */
  private final Map<RelOptTable, ThroughLayout> layouts = new IdentityHashMap<>();

  /**
   * One {@code through} entry as the pass reads it: the correlation key, the parent table as the
   * query resolves it, the parent's own key, and which column block of the converted row the
   * parent's columns occupy (D225).
   *
   * @param total whether the correlation column is NOT NULL and a declared foreign key over it names
   *     this parent's key — which is what makes the join elidable when the parent's predicate folds
   *     to TRUE (D226)
   */
  private record ThroughEntry(
      int column,
      RelOptTable parent,
      int parentColumn,
      int[] block,
      boolean total,
      @Nullable PathPlan path) {

    ThroughEntry(int column, RelOptTable parent, int parentColumn, int[] block, boolean total) {
      this(column, parent, parentColumn, block, total, null);
    }
  }

  /**
   * One declared path as the pass reads it (D265 §2, §4): the steps beyond the first — every one a
   * join down to a parent — and the endpoint predicate, folded at the far end of the chain.
   *
   * <p>The entry around it carries what the target sees of it: {@link ThroughEntry#column} is the
   * target's own join column (its unique key for a {@code Related} path, its foreign key for an
   * {@code Inherited} one), {@link ThroughEntry#parent} the endpoint and
   * {@link ThroughEntry#parentColumn} the endpoint's key — so a path drops into exactly the slots a
   * {@code through} parent occupies, and everything above it is the same machinery.
   *
   * @param base the table the first step arrives at: the bridge of a {@code Related} path, or the
   *     first parent of an {@code Inherited} one
   * @param baseKey the column of {@code base} the target joins back to
   * @param onward the steps after the first, each a join from the current table's column to the next
   *     table's declared unique key
   * @param related whether the first step went up, to a bridge whose semi-join is never elided
   */
  private record PathPlan(
      String kind,
      RelOptTable base,
      int baseKey,
      ImmutableList<PathStep> onward,
      RelOptTable endpoint,
      @Nullable RexNode endpointPredicate,
      boolean related,
      @Nullable RexNode pathPredicate,
      ImmutableList<Integer> projected,
      int endpointBlock) {

    PathPlan(
        String kind,
        RelOptTable base,
        int baseKey,
        ImmutableList<PathStep> onward,
        RelOptTable endpoint,
        @Nullable RexNode endpointPredicate,
        boolean related) {
      this(kind, base, baseKey, onward, endpoint, endpointPredicate, related, null,
          ImmutableList.of(), 0);
    }

    /** How many endpoint columns this path's side carries beyond the key, marker and verdicts. */
    int extra() {
      return projected.size();
    }
  }

  /** One join of a path's chain: from the current table's column to {@code table}'s key. */
  private record PathStep(int fromColumn, RelOptTable table, int toColumn) {}

  /** Every entry of one table, and the parents as the generated statement's {@code FROM} names them. */
  private record ThroughLayout(ImmutableList<ThroughEntry> entries, ImmutableList<List<String>> from) {
    boolean isEmpty() {
      return entries.isEmpty();
    }
  }

  private ThroughLayout layoutOf(TableScan scan) {
    return layouts.computeIfAbsent(scan.getTable(), key -> resolveThrough(scan));
  }

  private ThroughLayout resolveThrough(TableScan scan) {
    ChalkTable table = scan.getTable().unwrap(ChalkTable.class);
    TableEntitlement entitlement = table.descriptor().getEntitlement();
    if (entitlement.getThroughCount() == 0 && entitlement.getInheritedCount() == 0) {
      return new ThroughLayout(ImmutableList.of(), ImmutableList.of());
    }

    org.apache.calcite.plan.RelOptSchema schema = scan.getTable().getRelOptSchema();
    Map<String, int[]> blocks = new java.util.LinkedHashMap<>();
    List<List<String>> from = new ArrayList<>();
    List<ThroughEntry> entries = new ArrayList<>(entitlement.getThroughCount());
    // Which declared path each path entry came from, so the second pass below can reach its text.
    Map<Integer, chalk.ir.v1.InheritedVisibility> declaredOf = new java.util.LinkedHashMap<>();
    int offset = fullRowType(scan).getFieldCount();

    for (chalk.ir.v1.ParentVisibility through : entitlement.getThroughList()) {
      String schemaName =
          through.getParentSchema().isEmpty() ? table.schemaName() : through.getParentSchema();
      List<String> qualified = List.of(schemaName, through.getParentTable());
      RelOptTable parent = schema == null ? null : schema.getTableForMember(qualified);
      if (parent == null) {
        // Registration proved the parent is in the catalog (D225), so this is a catalog the
        // planner did not register — a bug rather than a policy, and refused as one.
        throw new PolicyException(
            "the entitlement on "
                + table.schemaName()
                + "."
                + table.tableName()
                + " derives visibility through '"
                + String.join(".", qualified)
                + "', which this query's schema does not resolve"
                + " (docs/design/16-entitlements.md §3.13, D225).");
      }
      int[] block = blocks.get(String.join(".", qualified));
      if (block == null) {
        block = new int[] {offset, parent.getRowType().getFieldCount()};
        blocks.put(String.join(".", qualified), block);
        from.add(qualified);
        offset += block[1];
      }
      entries.add(
          new ThroughEntry(
              through.getColumn(),
              parent,
              through.getParentColumn(),
              block,
              isTotal(table, through)));
    }

    // The declared paths, whose endpoints' blocks follow the parents' in the same converted row —
    // the order `ThroughParents` gave the registration check, so a rule condition naming
    // `<endpoint_table>.<column>` resolves at the same offsets here (D265 §2).
    for (chalk.ir.v1.InheritedVisibility declared : entitlement.getInheritedList()) {
      String endpointSchema =
          declared.getEndpointSchema().isEmpty()
              ? table.schemaName()
              : declared.getEndpointSchema();
      List<String> qualified = List.of(endpointSchema, declared.getEndpointTable());
      RelOptTable endpoint = resolved(qualified, table);
      int[] block = blocks.get(String.join(".", qualified));
      if (block == null) {
        block = new int[] {offset, endpoint.getRowType().getFieldCount()};
        blocks.put(String.join(".", qualified), block);
        from.add(qualified);
        offset += block[1];
      }

      chalk.ir.v1.VisibilityStep first = declared.getSteps(0);
      String firstSchema = first.getSchema().isEmpty() ? table.schemaName() : first.getSchema();
      RelOptTable base = resolved(List.of(firstSchema, first.getTable()), table);
      // The first step: up to the bridge for a `Related` path, down to a parent for an `Inherited`
      // one. Either way the child is the side holding the key, and a step across two sources is
      // built only where an association states it (F84).
      refuseUndeclaredCrossSourceStep(
          table,
          declared,
          0,
          scan.getTable(),
          first.getFromColumn(),
          base,
          first.getToColumn(),
          first.getDirection() != chalk.ir.v1.StepDirection.STEP_DIRECTION_TO_CHILD);
      List<PathStep> onward = new ArrayList<>(declared.getStepsCount() - 1);
      RelOptTable at = base;
      boolean total =
          first.getDirection() != chalk.ir.v1.StepDirection.STEP_DIRECTION_TO_CHILD
              && !table.descriptor().getColumns(first.getFromColumn()).getType().getNullable();
      for (int i = 1; i < declared.getStepsCount(); i++) {
        chalk.ir.v1.VisibilityStep step = declared.getSteps(i);
        String stepSchema = step.getSchema().isEmpty() ? table.schemaName() : step.getSchema();
        RelOptTable to = resolved(List.of(stepSchema, step.getTable()), table);
        // Every later step goes down, so the table we are at holds the key (§1, D265).
        refuseUndeclaredCrossSourceStep(
            table, declared, i, at, step.getFromColumn(), to, step.getToColumn(), true);
        ChalkTable here = at.unwrap(ChalkTable.class);
        total =
            total
                && here != null
                && !here.descriptor().getColumns(step.getFromColumn()).getType().getNullable();
        onward.add(new PathStep(step.getFromColumn(), to, step.getToColumn()));
        at = to;
      }

      boolean related =
          first.getDirection() == chalk.ir.v1.StepDirection.STEP_DIRECTION_TO_CHILD;
      entries.add(
          new ThroughEntry(
              first.getFromColumn(),
              endpoint,
              declared.getStepsCount() == 1
                  ? first.getToColumn()
                  : declared.getSteps(declared.getStepsCount() - 1).getToColumn(),
              block,
              total && !related,
              new PathPlan(
                  declared.getKind(),
                  base,
                  first.getToColumn(),
                  ImmutableList.copyOf(onward),
                  endpoint,
                  endpointPredicate(declared, endpoint),
                  related)));
      declaredOf.put(entries.size() - 1, declared);
    }

    // The path predicates, converted over the target's own row with the endpoints in scope — the
    // very layout a column rule of a path's perspective is converted over (D279 §2). It runs here,
    // after the loop, because the `FROM` the conversion needs is complete only once every path has
    // its block.
    ImmutableList<List<String>> resolvedFrom = ImmutableList.copyOf(from);
    List<String> texts = new ArrayList<>();
    List<Integer> at = new ArrayList<>();
    for (Map.Entry<Integer, chalk.ir.v1.InheritedVisibility> pending : declaredOf.entrySet()) {
      String sql = pending.getValue().getPathPredicate();
      if (!sql.isBlank()) {
        texts.add("(" + sql + ")");
        at.add(pending.getKey());
      }
    }
    if (!texts.isEmpty()) {
      List<RexNode> converted =
          converter.convert(
              texts,
              scan.getTable().getQualifiedName(),
              resolvedFrom,
              table.schemaName() + "." + table.tableName());
      int childWidth = fullRowType(scan).getFieldCount();
      for (int i = 0; i < at.size(); i++) {
        int k = at.get(i);
        ThroughEntry entry = entries.get(k);
        PathPlan path = entry.path();
        int[] block = entry.block();
        entries.set(
            k,
            new ThroughEntry(
                entry.column(),
                entry.parent(),
                entry.parentColumn(),
                block,
                entry.total(),
                new PathPlan(
                    path.kind(),
                    path.base(),
                    path.baseKey(),
                    path.onward(),
                    path.endpoint(),
                    path.endpointPredicate(),
                    path.related(),
                    converted.get(i),
                    endpointColumns(converted.get(i), block, childWidth, table, path),
                    block[0])));
      }
    }
    return new ThroughLayout(ImmutableList.copyOf(entries), resolvedFrom);
  }

  /**
   * Which of the endpoint's columns a path predicate names, ascending — what the chain projects
   * beside its key so the marker can read them above the join (D279 §4).
   *
   * <p>A reference outside the target's own row and outside this path's endpoint block is one
   * registration proved cannot be written, so it is refused here rather than read at an offset that
   * means something else.
   */
  private ImmutableList<Integer> endpointColumns(
      RexNode predicate, int[] block, int childWidth, ChalkTable table, PathPlan path) {
    java.util.TreeSet<Integer> columns = new java.util.TreeSet<>();
    for (int bit : org.apache.calcite.plan.RelOptUtil.InputFinder.bits(predicate)) {
      if (bit < childWidth) {
        continue;
      }
      if (bit < block[0] || bit >= block[0] + block[1]) {
        throw new PolicyException(
            "the entitlement on "
                + table.schemaName()
                + "."
                + table.tableName()
                + " has a path predicate on the path of kind '"
                + path.kind()
                + "' that reads a table other than its own row and the endpoint '"
                + path.endpoint().getQualifiedName()
                + "'. A path predicate is decided above the join, where this table's row and that"
                + " endpoint's are, and nothing else is there"
                + " (docs/design/47-conjoined-across-a-path.md §2, §4, D279).");
      }
      columns.add(bit - block[0]);
    }
    return ImmutableList.copyOf(columns);
  }

  /**
   * A step that crosses a source is built like any other, and refused here where nothing declares it
   * (F84, docs/design/38-existential-visibility.md §5; the association is
   * docs/design/45-typed-tenancy-surface.md §3, D270).
   *
   * <p>The chain is ordinary rels over ordinary scans, so a step across two sources is the same two
   * joins as a step within one and the optimiser costs the exchange between them. What a step across
   * a source cannot do without is a <b>declaration</b>: a foreign key is one source's claim about a
   * table of its own schema, and an association is the host's claim across two. Registration refuses
   * a step that has neither ({@code ThroughParents.checkStep}), so this is unreachable from a catalog
   * that registered — which is exactly why it is here: were a path to reach the pass with a step
   * nothing states, the chain would be a join between two sources on a correspondence nobody
   * asserted, and that must fail closed rather than plan.
   *
   * @param ordinal the step's position in the declared path, as the registration check numbers it
   * @param down whether {@code from} is the child of this step, which is where the key lives
   */
  private void refuseUndeclaredCrossSourceStep(
      ChalkTable target,
      chalk.ir.v1.InheritedVisibility declared,
      int ordinal,
      RelOptTable from,
      int fromColumn,
      RelOptTable to,
      int toColumn,
      boolean down) {
    ChalkTable here = from.unwrap(ChalkTable.class);
    ChalkTable there = to.unwrap(ChalkTable.class);
    if (here == null || there == null || here.sourceId().equals(there.sourceId())) {
      return;
    }
    ChalkTable child = down ? here : there;
    int childColumn = down ? fromColumn : toColumn;
    ChalkTable parent = down ? there : here;
    int parentColumn = down ? toColumn : fromColumn;
    if (associates(child, childColumn, parent, parentColumn)) {
      return;
    }

    throw new PolicyException(
        "the entitlement on "
            + target.schemaName()
            + "."
            + target.tableName()
            + " derives visibility along a path of kind '"
            + declared.getKind()
            + "' whose step "
            + ordinal
            + " crosses a source — '"
            + here.schemaName()
            + "."
            + here.tableName()
            + "' is served by '"
            + here.sourceId()
            + "' and '"
            + there.schemaName()
            + "."
            + there.tableName()
            + "' by '"
            + there.sourceId()
            + "' — and this catalog declares no association between them. A foreign key is one"
            + " source's claim about a table of its own schema, so a step across two sources is"
            + " built only where the host states it (F84,"
            + " docs/design/38-existential-visibility.md §5;"
            + " docs/design/45-typed-tenancy-surface.md §3, D270).");
  }

  /** Whether this catalog declares the association the step relies on, by name, as D270 writes it. */
  private boolean associates(
      ChalkTable child, int childColumn, ChalkTable parent, int parentColumn) {
    if (childColumn >= child.descriptor().getColumnsCount()
        || parentColumn >= parent.descriptor().getColumnsCount()) {
      return false;
    }
    String childColumnName = child.descriptor().getColumns(childColumn).getName();
    String parentColumnName = parent.descriptor().getColumns(parentColumn).getName();
    for (chalk.ir.v1.Association association : associations) {
      if (association.getFromSchema().equalsIgnoreCase(child.schemaName())
          && association.getFromTable().equalsIgnoreCase(child.tableName())
          && association.getFromColumn().equalsIgnoreCase(childColumnName)
          && association.getToSchema().equalsIgnoreCase(parent.schemaName())
          && association.getToTable().equalsIgnoreCase(parent.tableName())
          && association.getToColumn().equalsIgnoreCase(parentColumnName)) {
        return true;
      }
    }
    return false;
  }

  /**
   * The table a step names, resolved through the <b>declared</b> catalog (F68).
   *
   * <p>Not the tree the statement was resolved against. A path's chain is scanned <em>raw</em>
   * (§2, deviation 3), and "raw" is a claim about the row as much as about the policy: the endpoint
   * predicate and the verdict expressions over these tables are converted through the declared
   * catalog, because that is the row a leaf's own scan reads (F58), and a chain table with a
   * rule-protected column resolved through the disclosed tree was widened to nullable under them
   * (D161, ADR 0038) — a nullability the source does not produce. The two have to be one row type.
   */
  private RelOptTable resolved(List<String> qualified, ChalkTable table) {
    RelOptTable found = converter.declaredTable(qualified);
    if (found == null) {
      // Registration proved every step is in the catalog (D265 §3), so this is a catalog the
      // planner did not register — a bug rather than a policy, and refused as one.
      throw new PolicyException(
          "the entitlement on "
              + table.schemaName()
              + "."
              + table.tableName()
              + " derives visibility along a path through '"
              + String.join(".", qualified)
              + "', which this query's schema does not resolve"
              + " (docs/design/38-existential-visibility.md §3, D265).");
    }
    return found;
  }

  /**
   * The endpoint predicate, converted over the endpoint's <em>own</em> row and folded for this
   * principal (D265 §2): the one thing a path consults at the far end, in place of the endpoint's
   * entitlement.
   */
  private @Nullable RexNode endpointPredicate(
      chalk.ir.v1.InheritedVisibility declared, RelOptTable endpoint) {
    String sql = declared.getEndpointPredicate();
    if (sql.isBlank()) {
      return null;
    }
    ChalkTable table = endpoint.unwrap(ChalkTable.class);
    return converter.convert(List.of("(" + sql + ")"), endpoint, table).get(0);
  }

  /**
   * Whether every row of the child has a parent row: the correlation column is NOT NULL and a
   * declared foreign key over it names this parent's key (D226's "verified").
   *
   * <p>Chalk's catalog has no separate verified bit. {@code Table.foreign_keys} <em>is</em> the
   * host's assertion that every non-NULL value of the child's column occurs in the parent's (F14),
   * and the planner already costs joins by it. NOT NULL plus that declaration is therefore the whole
   * of "every child row has a visible parent" once the parent's own predicate is TRUE.
   */
  private static boolean isTotal(ChalkTable table, chalk.ir.v1.ParentVisibility through) {
    if (table.descriptor().getColumns(through.getColumn()).getType().getNullable()) {
      return false;
    }
    for (chalk.ir.v1.ForeignKey key : table.foreignKeys()) {
      if (key.getColumnsCount() == 1
          && key.getParentColumnsCount() == 1
          && key.getColumns(0) == through.getColumn()
          && key.getParentColumns(0) == through.getParentColumn()
          && key.getParentTable().equalsIgnoreCase(through.getParentTable())) {
        return true;
      }
    }
    return false;
  }

  // ------------------------------------------------------------------ the leaf

  private LeafFold fold(
      TableScan scan, DescriptorExpressions descriptor, List<RexNode> statementConjuncts) {
    ChalkTable table = scan.getTable().unwrap(ChalkTable.class);

    ThroughLayout layout = layoutOf(scan);
    if (!layout.isEmpty()) {
      return throughFold(scan, descriptor, statementConjuncts, layout);
    }

    // Filter_R first: the folded row predicate, simplified on its own. Its conjuncts are then what
    // the sanitisers are simplified under (§3.3), which is what makes a one-organisation agent's
    // disclosure the constant MASKED rather than a CASE over an identifier the filter has fixed.
    RexNode predicate = descriptor.rowPredicate();
    RexNode folded =
        predicate == null ? null : simplify.simplifyUnknownAsFalse(predicate);
    if (folded != null && folded.isAlwaysTrue()) {
      folded = null;
    }
    return leafFold(scan, descriptor, statementConjuncts, folded, table.trustsSourceRowLevelSecurity());
  }

  /**
   * One entitled leaf whose visibility derives through a parent (§3.13, D226–D228).
   *
   * <p>Each entry becomes a join to the parent's <em>own</em> entitled scan — recursively, so a
   * parent with a {@code through} of its own brings its join — projected to the parent's key, a
   * match marker, and the verdict columns the child's rules read (D228). {@code Filter_R} is then
   * the OR of the markers with whatever the child restricts by itself: any path grants, and "all
   * paths required" is not in the algebra.
   *
   * <p><b>Elision.</b> Where the parent's own predicate folds to TRUE, the correlation key is NOT
   * NULL and declared as a foreign key to that parent, and the child's rules need no verdict column
   * at all — a global grant is the case that reaches it — every child row has a visible parent whose
   * verdict is a constant, so the join is omitted and the visibility is ALL.
   */
  private LeafFold throughFold(
      TableScan scan,
      DescriptorExpressions descriptor,
      List<RexNode> statementConjuncts,
      ThroughLayout layout) {
    ChalkTable table = scan.getTable().unwrap(ChalkTable.class);
    int childWidth = fullRowType(scan).getFieldCount();

    // What this table restricts by *itself*, folded. Read twice below: once to decide whether a
    // path's marker could change `Filter_R` at all, and once for the visibility the report carries.
    RexNode own =
        descriptor.rowPredicate() == null
            ? null
            : simplify.simplifyUnknownAsFalse(descriptor.rowPredicate());

    List<int[]> blocks = new ArrayList<>(layout.entries().size());
    for (ThroughEntry entry : layout.entries()) {
      blocks.add(entry.block());
    }

    // What each parent's own scan comes to for this principal: what elision reads, and — exactly as
    // §3.3 simplifies a leaf's rules under its own Filter_R — what each parent's verdicts are
    // simplified under, which is what makes a manager's message FULL rather than PER_ROW.
    List<ParentFold> parents = new ArrayList<>(layout.entries().size());
    List<RexSimplify> underParent = new ArrayList<>(layout.entries().size());
    for (ThroughEntry entry : layout.entries()) {
      ParentFold parent = parentFold(scan, entry);
      parents.add(parent);
      List<RexNode> conjuncts =
          parent.ownRowPredicate() == null
              ? List.of()
              : RelOptUtilConjunctions.of(parent.ownRowPredicate());
      underParent.add(
          conjuncts.isEmpty()
              ? simplify
              : simplify.withPredicates(RelOptPredicateList.of(rexBuilder, conjuncts)));
    }

    boolean[] elided = new boolean[layout.entries().size()];
    boolean[] dropped = new boolean[layout.entries().size()];
    List<Integer> zero = new ArrayList<>(layout.entries().size());
    for (int k = 0; k < layout.entries().size(); k++) {
      // A path whose endpoint predicate folds to FALSE grants nothing, and its joins go with it
      // (D265 §4). A `through` parent keeps its join, as §3.13 built it: the parent's own leaf is
      // where its emptiness is established, and clause 3 reads it there.
      dropped[k] =
          layout.entries().get(k).path() != null
              && parents.get(k).visibility() == DisclosureMap.Visibility.NONE;
      // Which sides the first pass sees, so that it and the second agree on how many slots a side
      // takes: a dropped side is emitted nowhere and has no position to read (D269 (a)).
      zero.add(dropped[k] ? -1 : 0);
    }

    // What each side projects of its endpoint, so a column rule spanning both rows is decided off
    // those columns rather than dropped as a conjunct nothing could read (D279 §2).
    List<VerdictColumns.@Nullable Projection> projections =
        new ArrayList<>(layout.entries().size());
    for (ThroughEntry entry : layout.entries()) {
      PathPlan path = entry.path();
      projections.add(
          path == null || path.projected().isEmpty()
              ? null
              : new VerdictColumns.Projection(entry.block(), path.projected()));
    }

    // First pass, to learn how wide a parent side is: the expressions it builds are thrown away and
    // only the shape survives, because a verdict's position depends on that width and the width
    // depends on the verdicts.
    int verdicts =
        VerdictColumns.of(
                descriptor, childWidth, blocks, zero, rexBuilder, simplify, underParent,
                projections, -1)
            .width();

    if (verdicts == 0) {
      for (int k = 0; k < layout.entries().size(); k++) {
        elided[k] =
            !dropped[k]
                && (parents.get(k).visibility() == DisclosureMap.Visibility.ALL
                        && layout.entries().get(k).total()
                    // A marker that cannot change `Filter_R` is a join the optimiser may remove and
                    // the taint check would then miss: where this table's own predicate already
                    // folds to TRUE, the side is dead and is not emitted at all.
                    || (own != null && own.isAlwaysTrue()));
      }
    }
    int kept = 0;
    for (int k = 0; k < layout.entries().size(); k++) {
      kept += elided[k] || dropped[k] ? 0 : 1;
    }

    // A path that carries a path predicate projects the endpoint columns it names beyond the key,
    // the marker and the verdicts, so a side is as wide as its own route needs (D279 §4).
    List<Integer> offsets = new ArrayList<>(layout.entries().size());
    int at = childWidth;
    for (int k = 0; k < layout.entries().size(); k++) {
      boolean gone = elided[k] || dropped[k];
      offsets.add(gone ? -1 : at);
      if (!gone) {
        at += VerdictColumns.FIXED + verdicts + extraOf(layout.entries().get(k));
      }
    }

    VerdictColumns.Plan plan =
        VerdictColumns.of(
            descriptor, childWidth, blocks, offsets, rexBuilder, simplify, underParent,
            projections, verdicts);

    // Filter_R: any path grants (D226). An elided entry contributes TRUE, so the OR folds away and
    // the leaf is unfiltered; a kept one contributes its marker.
    List<RexNode> terms = new ArrayList<>(layout.entries().size() + 1);
    boolean anyDecider = false;
    for (int k = 0; k < layout.entries().size(); k++) {
      if (dropped[k]) {
        terms.add(rexBuilder.makeLiteral(false));
        continue;
      }
      if (elided[k]) {
        terms.add(rexBuilder.makeLiteral(true));
        continue;
      }
      RexNode matched =
          rexBuilder.makeCall(
              SqlStdOperatorTable.IS_NOT_NULL,
              rexBuilder.makeInputRef(
                  typeFactory.createTypeWithNullability(
                      typeFactory.createSqlType(org.apache.calcite.sql.type.SqlTypeName.BOOLEAN),
                      true),
                  offsets.get(k) + 1));

      // The decider of a cross-row confinement (D279 §4): the chain reached an endpoint row a
      // confined grant could admit, and this says whether the target's own row is the one it was
      // confined to. The target's columns are read plain and the endpoint's off the chain's
      // projection.
      PathPlan path = layout.entries().get(k).path();
      if (path == null || path.pathPredicate() == null) {
        terms.add(matched);
        continue;
      }
      anyDecider = true;
      terms.add(
          rexBuilder.makeCall(
              SqlStdOperatorTable.AND,
              matched,
              overTheJoinedRow(
                  path, offsets.get(k) + VerdictColumns.FIXED + verdicts, childWidth)));
    }
    if (descriptor.rowPredicate() != null) {
      terms.add(simplify.simplifyUnknownAsFalse(descriptor.rowPredicate()));
    }

    // Exactly one `Through` and no other restriction is the INNER JOIN that the OR collapses to
    // (D226): the join is the filter, and there is no `Filter_R` above it at all. A path predicate
    // is not in the join — it is decided above it — so a side that carries one keeps its filter.
    boolean inner =
        kept == 1
            && layout.entries().size() == 1
            && descriptor.rowPredicate() == null
            && !anyDecider;
    RexNode folded =
        inner
            ? null
            : simplify.simplifyUnknownAsFalse(RexUtil.composeDisjunction(rexBuilder, terms));
    if (folded != null && folded.isAlwaysTrue()) {
      folded = null;
    }

    // Visibility is what this principal's grants come to over the table, from the context and the
    // statement alone (D207, D230) — and the marker the filter reads is not a thing the simplifier
    // can settle, so it is derived from the parents' own folds rather than from Filter_R.
    DisclosureMap.Visibility visibility = DisclosureMap.Visibility.SOME;
    boolean anyElided = false;
    boolean everyParentEmpty = true;
    for (int k = 0; k < layout.entries().size(); k++) {
      anyElided |= elided[k];
      everyParentEmpty &=
          dropped[k]
              || (!elided[k] && parents.get(k).visibility() == DisclosureMap.Visibility.NONE);
    }
    if (anyElided || (own != null && own.isAlwaysTrue())) {
      visibility = DisclosureMap.Visibility.ALL;
    } else if (everyParentEmpty && (own == null || own.isAlwaysFalse())) {
      visibility = DisclosureMap.Visibility.NONE;
      folded = rexBuilder.makeLiteral(false);
    }

    // Under execute-time binding the cross-row half of a rule condition is a sub-query over a bound
    // list (D279 §4), and a sub-query cannot stand in a projection the IR can express (§3.2, D209).
    // Each distinct one becomes one marker column over the **joined** row — the target's columns and
    // the sides' beside them — exactly as a leaf's own memberships become columns over its scan, and
    // the sanitiser reads the column instead.
    //
    // Only where a side projects endpoint columns, which is the only way a condition decided here
    // can hold a membership at all: without one, every membership of a rule is either the parent
    // side's — computed in its own sub-tree, where its markers are already attached — or the child's
    // own half, which is what it was before this and stays so. Narrow on purpose: a leaf that
    // carries no cross-row group plans exactly as it planned.
    var projected = false;
    for (VerdictColumns.Projection projection : projections) {
      projected |= projection != null;
    }

    List<RexNode> conditions = new ArrayList<>();
    if (projected) {
      for (DescriptorExpressions.Column column : descriptor.columns()) {
        for (int rule = 0; rule < column.rules().size(); rule++) {
          RexNode condition = plan.conditionOf(column.tableColumn(), rule);
          if (condition != null) {
            conditions.add(condition);
          }
        }
      }
    }

    MembershipMarkers.Plan markers =
        conditions.isEmpty()
            ? null
            : MembershipMarkers.of(
                at, conditions, rexBuilder, table.schemaName() + "." + table.tableName());

    DescriptorExpressions rewritten =
        descriptor
            .rewriteConditions(
                (column, rule, condition) -> {
                  RexNode restated = plan.conditionOf(column, rule);
                  return restated == null || markers == null
                      ? restated
                      : markers.substitute(restated);
                })
            .withRowPredicate(folded);

    Through through =
        new Through(
            layout,
            plan,
            elided,
            dropped,
            ImmutableList.copyOf(offsets),
            verdicts,
            inner,
            markers,
            // FALSE restricts nothing a plan could imply, and TRUE has already elided the marker,
            // so the only value worth carrying to clause 3 is a restriction that is still a
            // question (F69).
            own == null || own.isAlwaysFalse() || own.isAlwaysTrue() ? null : own);
    return leafFold(
        scan,
        rewritten,
        statementConjuncts,
        folded,
        table.trustsSourceRowLevelSecurity(),
        visibility,
        through);
  }

  /** How many columns one side carries beyond the key, the marker and the verdicts (D279 §4). */
  private static int extraOf(ThroughEntry entry) {
    return entry.path() == null ? 0 : entry.path().extra();
  }

  /**
   * A path predicate as the <b>joined</b> row reads it (D279 §4): the target's own columns where
   * they are, and each endpoint column at its position in the chain's projection, which starts at
   * {@code base}.
   *
   * <p>The projected columns come through a LEFT JOIN and are nullable there whatever the endpoint
   * stores, so the reference takes the nullable type and the whole term is read unknown-as-false
   * beside the marker — a row the chain did not reach is a row this path does not grant.
   */
  private RexNode overTheJoinedRow(PathPlan path, int base, int childWidth) {
    ImmutableList<Integer> projected = path.projected();
    int block = path.endpointBlock();
    return path.pathPredicate()
        .accept(
            new org.apache.calcite.rex.RexShuttle() {
              @Override
              public RexNode visitInputRef(org.apache.calcite.rex.RexInputRef ref) {
                if (ref.getIndex() < childWidth) {
                  return ref;
                }
                int column = ref.getIndex() - block;
                int slot = projected.indexOf(column);
                return rexBuilder.makeInputRef(
                    typeFactory.createTypeWithNullability(ref.getType(), true), base + slot);
              }
            });
  }

  /**
   * What the parent's own entitled scan comes to for this principal (§3.13, D226, D230).
   *
   * @param visibility how much of the parent this principal can see, which is what elision reads —
   *     ALL over a key every child row carries means every child row has a visible parent
   * @param ownRowPredicate the parent's folded predicate where it is over the parent's <em>own</em>
   *     row, which the verdicts are then simplified under as §3.3 simplifies any leaf's rules; null
   *     for a parent whose own restriction is a join, whose predicate reads columns the parent's row
   *     does not have
   */
  private record ParentFold(
      DisclosureMap.Visibility visibility, @Nullable RexNode ownRowPredicate) {}

  private ParentFold parentFold(TableScan child, ThroughEntry entry) {
    if (entry.path() != null) {
      return pathFold(entry.path());
    }
    TableScan scan = parentScan(child, entry);
    DescriptorExpressions descriptor = descriptorOf(scan);
    if (descriptor == null) {
      return new ParentFold(DisclosureMap.Visibility.ALL, null);
    }
    LeafFold parent = fold(scan, descriptor, List.of());
    return new ParentFold(
        parent.map().visibility(),
        parent.through() == null ? parent.predicate() : null);
  }

  /**
   * What a declared path comes to for this principal (D265 §4): the endpoint predicate, folded, and
   * nothing else — a path consults no other table's entitlement, so this is the only fold there is.
   *
   * <p>FALSE is a side that grants nothing and whose joins are dropped; TRUE is a global grant of
   * the kind, which for an {@code Inherited} path with total keys is ALL, exactly as §3.13 elides a
   * {@code through}, and for a {@code Related} path is SOME: a total foreign key on the bridge says
   * every bridge row has a target row, not that every target row has a bridge row.
   */
  private ParentFold pathFold(PathPlan path) {
    RexNode predicate = path.endpointPredicate();
    RexNode folded = predicate == null ? null : simplify.simplifyUnknownAsFalse(predicate);
    if (folded != null && folded.isAlwaysFalse()) {
      return new ParentFold(DisclosureMap.Visibility.NONE, folded);
    }

    // And the decider above the join (D279 §4). Folded to FALSE it reaches nothing either, and the
    // joins go with it; still standing, it leaves the path SOME however total its keys are, because
    // the marker reads the endpoint's column through those joins and elision would take them away.
    RexNode decider =
        path.pathPredicate() == null
            ? null
            : simplify.simplifyUnknownAsFalse(path.pathPredicate());
    if (decider != null && decider.isAlwaysFalse()) {
      return new ParentFold(DisclosureMap.Visibility.NONE, folded);
    }
    if (folded == null || folded.isAlwaysTrue()) {
      if (decider != null && !decider.isAlwaysTrue()) {
        return new ParentFold(DisclosureMap.Visibility.SOME, null);
      }
      return new ParentFold(
          path.related() ? DisclosureMap.Visibility.SOME : DisclosureMap.Visibility.ALL, null);
    }
    return new ParentFold(DisclosureMap.Visibility.SOME, folded);
  }

  /**
   * A fresh scan of the parent, of the kind that table's own {@code toRel} makes.
   *
   * <p>Asking the table rather than picking a kind is what keeps a <em>remote</em> parent remote: a
   * {@code ChalkTableScan} is the local convention, and a parent scanned under it would never meet
   * the pushdown rules at all — its own row predicate would stay in process and its rows would come
   * back whole (§3.7, §3.13).
   */
  private static TableScan parentScan(TableScan child, ThroughEntry entry) {
    RelNode scan =
        entry
            .parent()
            .toRel(org.apache.calcite.plan.ViewExpanders.simpleContext(child.getCluster()));
    if (scan instanceof TableScan table) {
      return table;
    }
    return org.apache.calcite.rel.logical.LogicalTableScan.create(
        child.getCluster(), entry.parent(), ImmutableList.of());
  }

  /** What one {@code through} leaf's parents came to (§3.13). */
  /**
   * @param own what this table restricts <b>by itself</b>, folded for this principal — the other
   *     disjuncts of {@code Filter_R} beside the markers, or null where there are none and where
   *     they fold to FALSE. Clause 3 reads it for the marker the optimiser removed (F69).
   */
  private record Through(
      ThroughLayout layout,
      VerdictColumns.Plan plan,
      boolean[] elided,
      boolean[] dropped,
      ImmutableList<Integer> offsets,
      int verdicts,
      boolean inner,
      MembershipMarkers.@Nullable Plan markers,
      @Nullable RexNode own) {}

  /**
   * The same, with the leaf's row predicate already decided — which is what a branch of the F36
   * split has: its own half of the disjunction, over its own semi-joined or anti-joined rows.
   */
  private LeafFold leafFold(
      TableScan scan,
      DescriptorExpressions descriptor,
      List<RexNode> statementConjuncts,
      @Nullable RexNode folded,
      boolean trusted) {
    return leafFold(scan, descriptor, statementConjuncts, folded, trusted, null, null);
  }

  private LeafFold leafFold(
      TableScan scan,
      DescriptorExpressions descriptor,
      List<RexNode> statementConjuncts,
      @Nullable RexNode folded,
      boolean trusted,
      DisclosureMap.@Nullable Visibility declaredVisibility,
      @Nullable Through through) {
    ChalkTable table = scan.getTable().unwrap(ChalkTable.class);
    TableEntitlement entitlement = table.descriptor().getEntitlement();
    RelDataType rowType = fullRowType(scan);

    // What every row reaching this leaf satisfies, and therefore what the rules may be simplified
    // under (§3.3). The statement's own conjuncts always; the row predicate's only where the pass
    // actually emits it as `Filter_R`.
    //
    // Under `trust_source_row_level_security` (D156) it does not: the filter is skipped and the rows the
    // source returns are whatever the source's own row security let through, so assuming the
    // predicate here would fold a rule condition that is merely *implied by scope* to TRUE and hand
    // every row the mask — including rows outside the scope, whose masked value is a disclosure the
    // policy never granted. So a trusted leaf is simplified over all the rows the source returns,
    // which makes a column masked in scope and redacted out of it exactly what it is: PER_ROW (F44).
    List<RexNode> conjuncts = new ArrayList<>(statementConjuncts);
    if (folded != null && !folded.isAlwaysFalse() && !trusted) {
      conjuncts.addAll(RelOptUtilConjunctions.of(folded));
    }
    RexSimplify underFilter =
        conjuncts.isEmpty()
            ? simplify
            : simplify.withPredicates(RelOptPredicateList.of(rexBuilder, conjuncts));

    // A `through` leaf's visibility is derived from its parents' own folds rather than from
    // Filter_R, whose markers no simplifier can settle (§3.13, D230).
    DisclosureMap.Visibility visibility =
        declaredVisibility != null
            ? declaredVisibility
            : folded == null
                ? DisclosureMap.Visibility.ALL
                : folded.isAlwaysFalse()
                    ? DisclosureMap.Visibility.NONE
                    : DisclosureMap.Visibility.SOME;

    // The disclosure map, read off the folded rules and never off the descriptor's text (§3.5): the
    // same statement is legitimate for another principal.
    List<Disclosed> outcomes = new ArrayList<>(rowType.getFieldCount());
    List<Reachable> reachable = new ArrayList<>(rowType.getFieldCount());
    java.util.Set<Integer> statistical = new java.util.LinkedHashSet<>();
    // The same columns without the statement's own qualification: what the descriptor declares
    // statistical and this principal could reach. A window is refused over one of these whatever
    // the statement's shape, because a window is one of the three things D203 forbids and giving
    // it the mask instead would be doing so silently (F43).
    java.util.Set<Integer> declaredStatistical = new java.util.LinkedHashSet<>();
    for (int column = 0; column < rowType.getFieldCount(); column++) {
      DescriptorExpressions.Column entitled = descriptor.column(column);
      Reachable names = reachable(entitled, entitlement, underFilter, conjuncts);
      reachable.add(names);
      Set<Disclosure> reachableNames = names.names(entitled);
      // The statistical opt-in (D203): where the rules would mask or restrict this column to a
      // population, and the statement is one whose every output is an aggregate or a group key, the
      // leaf emits it raw instead — tainted, guarded by the group-size floor, and permitted in the
      // positions §3.4 names.
      boolean opted =
          entitled != null
              && entitled.entitlement().getStatistical()
              && (reachableNames.contains(Disclosure.DISCLOSURE_MASKED)
                  || reachableNames.contains(Disclosure.DISCLOSURE_AGGREGATE_ONLY));
      if (opted) {
        declaredStatistical.add(column);
      }
      if (statisticalStatement && opted) {
        statistical.add(column);
        outcomes.add(Disclosed.AGGREGATE);
        continue;
      }
      outcomes.add(outcome(reachableNames));
    }

    // The shapes each column's rules permit over it, for the read's own breadcrumbs (D261): read off
    // the descriptor rather than off this principal's fold, because what the client's invariant has
    // to decide is whether an expression *is* the leaf's permitted comparison, and the policy
    // decision — whether this principal reaches the rule at all — is the trace's and was made above.
    java.util.Map<Integer, ImmutableList<String>> permitted = new java.util.LinkedHashMap<>();
    for (DescriptorExpressions.Column entitled : descriptor.columns()) {
      java.util.Set<String> shapes = new java.util.LinkedHashSet<>();
      for (DisclosureRule rule : entitled.rules()) {
        for (chalk.ir.v1.TestShape shape : rule.getTestsList()) {
          shapes.add(shape.name().replace("TEST_SHAPE_", ""));
        }
      }
      if (!shapes.isEmpty()) {
        permitted.put(entitled.tableColumn(), ImmutableList.copyOf(shapes));
      }
    }

    DisclosureMap map =
        new DisclosureMap(
            table.schemaName(),
            table.tableName(),
            entitlement.getDescriptorHash(),
            outcomes,
            // The folded predicate's own answer, whatever the trust setting: `visibility` is what
            // this principal's grants come to over this table, computed from the context and the
            // statement alone (D207), and D156 skips `Filter_R` and nothing else. Saying ALL under
            // trust would tell a caller that its grants reach every row, which is the one thing the
            // setting does not mean (F44).
            visibility,
            statistical)
            .permitting(permitted);
    String contradiction = contradiction(statementConjuncts, folded, rowType);
    if (!contradiction.isEmpty()) {
      map = map.contradicting(contradiction);
    }
    return new LeafFold(
        table,
        descriptor,
        folded,
        trusted,
        underFilter,
        reachable,
        map,
        statementConjuncts,
        statistical,
        declaredStatistical,
        through);
  }

  /**
   * {@code Project_D(Filter_R(Scan))} for one leaf occurrence.
   *
   * <p>The scan is of the table's every column, whatever the scan the pass replaced projected, and
   * {@code Project_D} then produces exactly that scan's own row — so a leaf that arrived pruned
   * leaves pruned and the descriptor's expressions, which index the table's row type, still drop
   * straight onto it.
   */
  private RelNode emit(TableScan scan, LeafFold fold) {
    if (fold.through() != null) {
      return emitThrough(scan, fold, null);
    }
    if (context.anyShape()) {
      // Execute-time binding (D209), or partial binding with a shape among the names this leaf
      // reads (D232): a membership over a name nothing folded is a sub-query, and the markers of
      // §3.2 are what let a sanitiser be a projection at all. What *was* folded is literal in the
      // same leaf, and `MembershipMarkers` marks only what is still a sub-query — so a leaf whose
      // every membership folded takes no marker and no join.
      return emitMarked(scan, fold);
    }
    ImmutableList<MembershipSplit.Branch> branches =
        fold.predicate() == null || fold.trusted()
            ? null
            : MembershipSplit.of(
                fold.predicate(), fold.descriptor().expressions(), rexBuilder);
    if (branches == null) {
      return emitLeaf(scan, fold, null);
    }

    // The whole folded predicate, recorded once for the table: clause 3 of the taint check asks for
    // its conjuncts above every leaf of the table, and a conjunct that reads a context relation is
    // established by the relation still being scanned (§3.10, ADR 0025 V68) — which both branches
    // keep true, one through its semi-join and one through its anti-join.
    rowPredicates.put(fold.map().qualifiedName(), fold.predicate());

    List<RelNode> emitted = new ArrayList<>(branches.size());
    for (MembershipSplit.Branch branch : branches) {
      emitted.add(emitLeaf(scan, fold, branch));
    }
    if (emitted.size() == 1) {
      return emitted.get(0);
    }
    RelNode union = org.apache.calcite.rel.logical.LogicalUnion.create(emitted, /* all= */ true);
    // Both branches are this leaf's, so both carry the same derived test columns in the same
    // positions, and the union of them does too (D261).
    int extra = widened.getOrDefault(emitted.get(0), 0);
    if (extra > 0) {
      widened.put(union, extra);
    }
    return union;
  }

  /**
   * {@code Project_D(Filter_R(Scan))} for one leaf occurrence, or for one branch of it.
   *
   * <p>A branch is the F36 split: its scan is anti-joined against every membership it does not
   * satisfy, its filter is the memberships it does satisfy and what is left of the predicate, and
   * both its sanitisers and its disclosure map are folded again with those memberships' answers
   * substituted — so the branches are two leaves of one table, and the report meets them as
   * alternative rows.
   */
  private RelNode emitLeaf(TableScan scan, LeafFold whole, MembershipSplit.@Nullable Branch branch) {
    return emitLeaf(scan, whole, branch, null);
  }

  private RelNode emitLeaf(
      TableScan scan,
      LeafFold whole,
      MembershipSplit.@Nullable Branch branch,
      @Nullable ParentProjection asParent) {
    LeafFold fold = branch == null ? whole : branchFold(scan, whole, branch);

    leaves.add(fold.map());
    if (branch == null && fold.predicate() != null && !fold.trusted()) {
      rowPredicates.put(fold.map().qualifiedName(), fold.predicate());
    }
    RelNode leaf = withDisclosure(scan, fold.map());
    if (branch != null) {
      for (RexSubQuery membership : branch.fails()) {
        leaf = antiJoin(leaf, membership);
      }
    }
    RexNode condition = branchCondition(fold, branch);
    if (condition != null) {
      leaf = LogicalFilter.create(leaf, condition);
    }
    return project(scan, fold, leaf, asParent);
  }

  /**
   * The same leaf under execute-time binding: one marker column per distinct membership, and the
   * sanitisers over them (§3.2, D209).
   *
   * <p>Nothing folded, so a rule condition is a sub-query and a sub-query cannot stand in a
   * projection the IR can express. {@link MembershipMarkers} computes each distinct membership once,
   * as a column of the leaf, and every expression of the descriptor then reads that column instead —
   * so a per-row disclosure costs one join per <em>list</em> and none per column.
   */
  private RelNode emitMarked(TableScan scan, LeafFold whole) {
    return emitMarked(scan, whole, null);
  }

  private RelNode emitMarked(
      TableScan scan, LeafFold whole, @Nullable ParentProjection asParent) {
    List<RexNode> expressions = new ArrayList<>(whole.descriptor().expressions());
    if (whole.predicate() != null) {
      expressions.add(whole.predicate());
    }
    // A parent side's verdicts are the child's rules read here, so their memberships are this
    // leaf's too: they are computed once, beside the parent's own, and read the same markers
    // (§3.13, D228; ADR 0025 V97).
    if (asParent != null) {
      expressions.addAll(asParent.verdicts());
    }
    MembershipMarkers.Plan markers =
        MembershipMarkers.of(
            fullRowType(scan).getFieldCount(),
            expressions,
            rexBuilder,
            whole.map().qualifiedName());
    if (markers == null) {
      return emitLeaf(scan, whole, null, asParent);
    }

    DescriptorExpressions descriptor = whole.descriptor().rewrite(markers::substitute);
    RexNode predicate = whole.predicate() == null ? null : markers.substitute(whole.predicate());
    LeafFold fold =
        leafFold(scan, descriptor, whole.statementConjuncts(), predicate, whole.trusted());

    // What clause 3 looks for is the predicate as it was *before* the markers (§3.10, V68): its
    // memberships are relations the plan still scans, which is the evidence the check reads them by,
    // and a marker column does not survive above Project_D to be found textually.
    if (whole.predicate() != null && !whole.trusted()) {
      rowPredicates.put(whole.map().qualifiedName(), whole.predicate());
    }

    leaves.add(fold.map());
    RelNode leaf =
        MembershipMarkers.attach(withDisclosure(scan, fold.map()), markers, rexBuilder);
    if (fold.predicate() != null && !fold.trusted() && !fold.predicate().isAlwaysTrue()) {
      leaf = LogicalFilter.create(leaf, fold.predicate());
    }
    if (asParent == null) {
      return project(scan, fold, leaf, null);
    }
    List<RexNode> substituted = new ArrayList<>(asParent.verdicts().size());
    for (RexNode verdict : asParent.verdicts()) {
      substituted.add(markers.substitute(verdict));
    }
    return project(
        scan, fold, leaf, new ParentProjection(asParent.keyColumn(), substituted));
  }

  // ------------------------------------------------------------------ through: the joins (§3.13)

  /**
   * {@code Project_D(Filter_R(Join(Scan, parent side…)))} for a leaf entitled through a parent
   * (§3.13, D226).
   *
   * <p>Each kept entry joins to the parent's own entitled scan on {@code child.column =
   * parent.key} — LEFT, so the marker says whether the parent granted, and INNER where one entry is
   * the whole restriction and the OR collapses to it. The correlation key is read <em>raw</em>
   * beneath the child's sanitiser, which is what lets the mechanism work without the key being
   * disclosed to the statement.
   */
  private RelNode emitThrough(TableScan scan, LeafFold fold, @Nullable ParentProjection asParent) {
    Through through = fold.through();
    leaves.add(fold.map());

    // The evidence clause 3 reads for this leaf (§3.10 as extended): the joins have to still be
    // there, with their key conditions, at the child's consumer. A leaf whose filter is FALSE —
    // no parent grants anything and the child restricts nothing else — is pruned to empty by the
    // optimiser, joins and all, and asking for a join of a plan that returns no row would refuse
    // the one answer that is certainly safe.
    boolean empty = fold.predicate() != null && fold.predicate().isAlwaysFalse();
    for (int k = 0; k < through.layout().entries().size() && !empty; k++) {
      if (through.elided()[k] || through.dropped()[k]) {
        continue;
      }
      ThroughEntry entry = through.layout().entries().get(k);
      if (entry.path() != null) {
        // The key set's own join: the target's key against the key the chain groups by, which comes
        // raw off the table the first step arrived at (D265 §7). The rest of the chain — the joins
        // down to the endpoint and the endpoint predicate — is inside that sub-tree and travels with
        // it, so this one join is what the finished plan must still hold.
        ChalkTable base = entry.path().base().unwrap(ChalkTable.class);
        throughEvidence.add(
            TaintCheck.ThroughEvidence.raw(
                fold.map().qualifiedName(),
                entry.column(),
                base.schemaName() + "." + base.tableName(),
                entry.path().baseKey(),
                foldedBaseKey(entry.path()),
                through.own()));
        continue;
      }
      ChalkTable parent = entry.parent().unwrap(ChalkTable.class);
      throughEvidence.add(
          new TaintCheck.ThroughEvidence(
              fold.map().qualifiedName(),
              entry.column(),
              parent.schemaName() + "." + parent.tableName(),
              entry.parentColumn(),
              false,
              null,
              through.own()));
    }

    RelNode leaf = withDisclosure(scan, fold.map());
    RelDataType childRow = fullRowType(scan);
    for (int k = 0; k < through.layout().entries().size(); k++) {
      if (through.elided()[k] || through.dropped()[k]) {
        continue;
      }
      ThroughEntry entry = through.layout().entries().get(k);
      RelNode side = parentSide(scan, entry, through.plan().verdicts().get(k));
      int offset = leaf.getRowType().getFieldCount();
      org.apache.calcite.rel.core.JoinRelType kind =
          through.inner()
              ? org.apache.calcite.rel.core.JoinRelType.INNER
              : org.apache.calcite.rel.core.JoinRelType.LEFT;
      // A join condition is over its inputs' own rows, so the references take the *inputs'* types:
      // the output's would be the left join's widened ones, and the IR compares them exactly.
      RexNode on =
          rexBuilder.makeCall(
              SqlStdOperatorTable.EQUALS,
              rexBuilder.makeInputRef(
                  leaf.getRowType().getFieldList().get(entry.column()).getType(), entry.column()),
              rexBuilder.makeInputRef(
                  side.getRowType().getFieldList().get(0).getType(), offset));
      leaf =
          org.apache.calcite.rel.logical.LogicalJoin.create(
              leaf,
              side,
              ImmutableList.<org.apache.calcite.rel.hint.RelHint>of(),
              on,
              java.util.Set.<org.apache.calcite.rel.core.CorrelationId>of(),
              kind);
    }

    if (through.markers() != null) {
      leaf = MembershipMarkers.attach(leaf, through.markers(), rexBuilder);
    }
    if (fold.predicate() != null && !fold.predicate().isAlwaysTrue()) {
      leaf = LogicalFilter.create(leaf, fold.predicate());
    }
    return project(scan, fold, leaf, asParent);
  }

  /** Each {@code through} join the plan must still hold at the child's consumer (§3.10 as extended). */
  private final List<TaintCheck.ThroughEvidence> throughEvidence = new ArrayList<>();

  /**
   * The parent's own entitled scan, projected to what the child reads of it: its key, a match
   * marker, and the verdict columns of D228 — computed here, at the parent's cardinality, once per
   * parent row.
   */
  private RelNode parentSide(TableScan child, ThroughEntry entry, List<RexNode> verdicts) {
    if (entry.path() != null) {
      return pathSide(child, entry.path(), verdicts);
    }
    TableScan scan = parentScan(child, entry);
    DescriptorExpressions descriptor = descriptorOf(scan);
    LeafFold fold = fold(scan, descriptor, List.of());
    ParentProjection projection = new ParentProjection(entry.parentColumn(), verdicts);
    if (fold.through() != null) {
      return emitThrough(scan, fold, projection);
    }
    if (context.anyShape()) {
      return emitMarked(scan, fold, projection);
    }
    return emitLeaf(scan, fold, null, projection);
  }

  /**
   * What the parent side of a {@code through} join projects instead of the parent's own row: the
   * key the join matches on, and the verdicts the child's rules read (§3.13, D228).
   */
  private record ParentProjection(int keyColumn, List<RexNode> verdicts) {}

  /**
   * What the endpoint predicate pins the chain's own key to, where it pins it at all (F67,
   * ADR 0052).
   *
   * <p>A path whose endpoint predicate fixes the endpoint's <b>unique key</b> to one constant —
   * {@code Inherited("vendor").Through("vendors")} for a principal holding one vendor grant, whose
   * predicate folds to {@code id = 1} — has a key set of exactly one row, and the optimiser
   * therefore projects that constant in place of the column: {@code $chalk$key=[1]}. The join is
   * still there and still restricts the target's rows to the key set, but its far side no longer has
   * a column origin, so clause 3 could not recognise its own join. Clause 3 takes this constant as
   * the other form the same join may wear.
   *
   * <p>Null wherever nothing is pinned, which is the fail-closed default: the key set then keeps its
   * column and clause 3 asks for the join by its origins, as it always did. Only a chain of one
   * table can be pinned this way — with steps onward the key is on the base and the predicate is on
   * the endpoint, so the projection keeps reading a column.
   */
  private @Nullable RexNode foldedBaseKey(PathPlan path) {
    if (path.endpointPredicate() == null || !path.onward().isEmpty()) {
      return null;
    }
    RexNode folded = simplify.simplifyUnknownAsFalse(path.endpointPredicate());
    if (folded.isAlwaysTrue() || folded.isAlwaysFalse()) {
      return null;
    }
    org.apache.calcite.plan.RelOptPredicateList predicates =
        org.apache.calcite.plan.RelOptPredicateList.of(
            rexBuilder, org.apache.calcite.plan.RelOptUtil.conjunctions(folded));
    for (Map.Entry<RexNode, RexNode> pinned : predicates.constantMap.entrySet()) {
      if (pinned.getKey() instanceof org.apache.calcite.rex.RexInputRef ref
          && ref.getIndex() == path.baseKey()) {
        return pinned.getValue();
      }
    }
    return null;
  }

  /**
   * The <b>key set</b> of a declared path, which the target joins back to (D265 §4).
   *
   * <pre>
   *   K = SELECT DISTINCT base.key [, MIN(v_1), MIN(v_2), …]
   *       FROM base [JOIN … down to the endpoint]
   *       WHERE &lt;endpoint predicate, folded for this principal&gt;
   * </pre>
   *
   * <p>Every table in the chain is scanned <b>raw</b>: a path consults no other table's entitlement
   * (§2), and none of these columns leaves the chain — the key joins back to the target's own key,
   * the marker says whether it matched, and the verdicts are ordinals. That is the same bargain
   * D227 strikes for a parent's correlation key, and it is what keeps the marketplace shape acyclic
   * where the bridge is itself entitled through the target.
   *
   * <p>{@code DISTINCT} is what keeps the join from multiplying the target's rows: the target's key
   * is unique and the key set's is distinct. The {@code MIN} is §4's rule for a row related to many
   * tenants — the strongest matching rule across them, which is "any path grants" applied to rules.
   */
  private RelNode pathSide(TableScan child, PathPlan path, List<RexNode> verdicts) {
    RelNode rel = rawScan(child, path.base());
    int at = 0;
    for (PathStep step : path.onward()) {
      RelNode right = rawScan(child, step.table());
      int rightAt = rel.getRowType().getFieldCount();
      RexNode on =
          rexBuilder.makeCall(
              SqlStdOperatorTable.EQUALS,
              rexBuilder.makeInputRef(
                  rel.getRowType().getFieldList().get(at + step.fromColumn()).getType(),
                  at + step.fromColumn()),
              rexBuilder.makeInputRef(
                  right.getRowType().getFieldList().get(step.toColumn()).getType(),
                  rightAt + step.toColumn()));
      rel =
          org.apache.calcite.rel.logical.LogicalJoin.create(
              rel,
              right,
              ImmutableList.<org.apache.calcite.rel.hint.RelHint>of(),
              on,
              java.util.Set.<org.apache.calcite.rel.core.CorrelationId>of(),
              org.apache.calcite.rel.core.JoinRelType.INNER);
      at = rightAt;
    }

    // The endpoint predicate and the verdicts are written over the endpoint's own row, which starts
    // where the last step's table does.
    int endpoint = at;
    RexNode predicate = path.endpointPredicate();
    if (predicate != null) {
      RexNode folded = simplify.simplifyUnknownAsFalse(shift(predicate, endpoint));
      if (!folded.isAlwaysTrue()) {
        rel = LogicalFilter.create(rel, folded);
      }
    }

    List<RexNode> grouped = new ArrayList<>(verdicts.size() + 1 + path.extra());
    List<String> names = new ArrayList<>(verdicts.size() + 1 + path.extra());
    grouped.add(rexBuilder.makeInputRef(rel, path.baseKey()));
    names.add(chalk.planner.ReservedNames.PREFIX + "key");
    for (int i = 0; i < verdicts.size(); i++) {
      grouped.add(shift(verdicts.get(i), endpoint));
      names.add(chalk.planner.ReservedNames.PREFIX + "verdict" + i);
    }

    // The endpoint columns the path predicate names (D279 §4). An `Inherited` path is one endpoint
    // row per target key — a foreign key onto a unique key — so each of these is a function of the
    // key: they are grouped by beside it rather than aggregated, and the chain stays one row per
    // key without a MIN.
    for (int i = 0; i < path.extra(); i++) {
      grouped.add(rexBuilder.makeInputRef(rel, endpoint + path.projected().get(i)));
      names.add(chalk.planner.ReservedNames.PREFIX + "path" + i);
    }

    RelNode projected =
        LogicalProject.create(
            rel,
            ImmutableList.<org.apache.calcite.rel.hint.RelHint>of(),
            grouped,
            names,
            java.util.Set.<org.apache.calcite.rel.core.CorrelationId>of());

    List<org.apache.calcite.rel.core.AggregateCall> calls = new ArrayList<>(verdicts.size());
    for (int i = 0; i < verdicts.size(); i++) {
      calls.add(least(projected, i + 1));
    }
    org.apache.calcite.util.ImmutableBitSet.Builder keys =
        org.apache.calcite.util.ImmutableBitSet.builder();
    keys.set(0);
    for (int i = 0; i < path.extra(); i++) {
      keys.set(1 + verdicts.size() + i);
    }
    RelNode distinct =
        org.apache.calcite.rel.logical.LogicalAggregate.create(
            projected,
            ImmutableList.<org.apache.calcite.rel.hint.RelHint>of(),
            keys.build(),
            null,
            ImmutableList.copyOf(calls));

    // The shape every side takes, so the target reads one position whatever the route was: the key,
    // a match marker, then the verdicts — all nullable, so the LEFT JOIN above answers the same way.
    // The aggregate puts its group keys first — the chain's key, then the projected endpoint
    // columns — and the MIN verdicts after them.
    int aggregated = 1 + path.extra();
    List<RexNode> side = new ArrayList<>(verdicts.size() + VerdictColumns.FIXED + path.extra());
    List<String> sideNames = new ArrayList<>(side.size());
    side.add(rexBuilder.makeInputRef(distinct, 0));
    sideNames.add(distinct.getRowType().getFieldList().get(0).getName());
    side.add(
        rexBuilder.makeCast(
            typeFactory.createTypeWithNullability(
                typeFactory.createSqlType(org.apache.calcite.sql.type.SqlTypeName.BOOLEAN), true),
            rexBuilder.makeLiteral(true)));
    sideNames.add(chalk.planner.ReservedNames.PREFIX + "matched");
    for (int i = 0; i < verdicts.size(); i++) {
      RexNode ref = rexBuilder.makeInputRef(distinct, aggregated + i);
      side.add(
          rexBuilder.makeCast(
              typeFactory.createTypeWithNullability(ref.getType(), true), ref));
      sideNames.add(chalk.planner.ReservedNames.PREFIX + "verdict" + i);
    }
    for (int i = 0; i < path.extra(); i++) {
      RexNode ref = rexBuilder.makeInputRef(distinct, 1 + i);
      side.add(
          rexBuilder.makeCast(
              typeFactory.createTypeWithNullability(ref.getType(), true), ref));
      sideNames.add(chalk.planner.ReservedNames.PREFIX + "path" + i);
    }

    return LogicalProject.create(
        distinct,
        ImmutableList.<org.apache.calcite.rel.hint.RelHint>of(),
        side,
        sideNames,
        java.util.Set.<org.apache.calcite.rel.core.CorrelationId>of());
  }

  /** {@code MIN} over one column of the projection below, which is §4's ordinal rule. */
  @SuppressWarnings("deprecation")
  private static org.apache.calcite.rel.core.AggregateCall least(RelNode input, int column) {
    return org.apache.calcite.rel.core.AggregateCall.create(
        SqlStdOperatorTable.MIN,
        /* distinct= */ false,
        /* approximate= */ false,
        /* ignoreNulls= */ false,
        List.of(),
        List.of(column),
        /* filterArg= */ -1,
        (org.apache.calcite.util.ImmutableBitSet) null,
        org.apache.calcite.rel.RelCollations.EMPTY,
        /* groupCount= */ 1,
        input,
        null,
        null);
  }

  /** One expression over a table's own row, read at {@code offset} in the chain's joined row. */
  private RexNode shift(RexNode expression, int offset) {
    if (offset == 0) {
      return expression;
    }
    return expression.accept(
        new org.apache.calcite.rex.RexShuttle() {
          @Override
          public RexNode visitInputRef(org.apache.calcite.rex.RexInputRef ref) {
            return rexBuilder.makeInputRef(ref.getType(), ref.getIndex() + offset);
          }
        });
  }

  /**
   * A raw scan of one table of a path's chain: an <b>unimplemented</b> scan, with no entitled
   * wrapper at all — which is what "the bridge contributes existence and nothing else" means (§0,
   * §2) — left for the optimiser to implement like any other leaf (§5).
   *
   * <p><b>Why a {@code LogicalTableScan} and not the handle's own {@code toRel}</b> (F84). This read
   * {@code CorrelationRelOptTable.of(table).toRel(…)}, which is a {@code ChalkTableScan} and is
   * therefore already in {@code ChalkConvention.LOCAL}: born implemented, and born implemented in
   * the client. No rule could then see it — not {@code PushdownRules}, which converts a scan in
   * {@code Convention.NONE}; not {@code CrossSourceJoinRule}; not {@code ParentKeySetRule} — so a
   * chain never pushed and never exchanged, whether its tables shared a source or not, and §5's
   * "the marker pushes as one remote query" was unreachable. Entering in {@code Convention.NONE} is
   * what makes the chain "ordinary rels over ordinary scans" true of the plan as well as of the
   * pass: the same rules that implement a statement's own scan implement these, and the exchange
   * between two sources is one of M5's strategies at the planner's costing rather than a fetch.
   *
   * <p>The correlation handle travels with the table and every implementation carries it, so clause
   * 1 still reads these scans as the mechanism's own and {@code Read.correlation} still reaches the
   * wire.
   */
  private static RelNode rawScan(TableScan child, RelOptTable table) {
    return org.apache.calcite.rel.logical.LogicalTableScan.create(
        child.getCluster(), CorrelationRelOptTable.of(table), ImmutableList.of());
  }

  /** {@code Project_D} over whatever the leaf turned out to be, and what each column discloses. */
  private RelNode project(TableScan scan, LeafFold fold, RelNode leaf) {
    return project(scan, fold, leaf, null);
  }

  private RelNode project(
      TableScan scan, LeafFold fold, RelNode leaf, @Nullable ParentProjection asParent) {
    if (asParent != null) {
      return projectAsParent(scan, fold, leaf, asParent);
    }
    RelDataType rowType = fullRowType(scan);
    // What the statement was typed against, and therefore what this projection must hand back: the
    // disclosed row, whose withholdable columns are nullable whatever the source stores (F58).
    RelDataType disclosedRow = disclosedRowType(scan);
    List<Integer> projection = projectionOf(scan);
    List<String> names = new ArrayList<>(scan.getRowType().getFieldNames());

    List<RexNode> sanitisers = new ArrayList<>(projection.size());
    for (int column : projection) {
      sanitisers.add(sanitiser(column, rowType, disclosedRow, fold));
    }

    Disclosed[] perColumn = new Disclosed[projection.size()];
    for (int i = 0; i < projection.size(); i++) {
      perColumn[i] = fold.map().of(projection.get(i));
    }

    // The comparisons a tested column's rules permit, computed here and nowhere else: this
    // projection is the one place in the plan that reads a raw column (§3.1), and one boolean per
    // comparison is the whole of what a TEST verdict discloses (D261). They are appended, so every
    // column the statement was typed against keeps its index and only what asks for them sees them.
    List<TestLift.Lifted> lifted = lifts.of(scan);
    for (int i = 0; i < lifted.size(); i++) {
      sanitisers.add(test(lifted.get(i), fold));
      names.add(chalk.planner.ReservedNames.PREFIX + "test" + i);
    }
    if (!lifted.isEmpty()) {
      // What the report and the audit observer are handed (§3, D261): the column, by the name the
      // table declares it under, and the shapes this statement used — never a parameter's value,
      // which is a context value and §3.12 keeps those out of an audit event.
      List<DisclosureMap.Tested> tested = new ArrayList<>();
      java.util.Set<Integer> seen = new java.util.LinkedHashSet<>();
      for (TestLift.Lifted one : lifted) {
        if (seen.add(one.tableColumn())) {
          tested.add(
              new DisclosureMap.Tested(
                  rowType.getFieldList().get(one.tableColumn()).getName(),
                  TestLift.shapeNames(lifted, one.tableColumn())));
        }
      }
      fold.map().testing(tested);
    }

    Disclosed[] disclosed = java.util.Arrays.copyOf(perColumn, sanitisers.size());
    for (int i = 0; i < lifted.size(); i++) {
      // A derived comparison discloses what the column it compares does: TESTED where the verdict
      // is TEST, and AGGREGATE where it is AGGREGATE_ONLY and the comparison is a permitted
      // aggregate's FILTER — so a count the guard may NULL is reported `Aggregate` and its NULL is
      // read as a withheld value rather than as "no rows" (D202, D261).
      disclosed[perColumn.length + i] = fold.map().of(lifted.get(i).tableColumn());
    }
    int width = sanitisers.size();

    if (options.includeDisclosureColumns()) {
      // Beside each value, what it disclosed for this row, as the report's own ordinal (D207). It is
      // computed here because here is where the rules are: the same conditions the sanitiser chooses
      // a value by, choosing a name instead. DisclosureColumns carries them to the root. A derived
      // test column is one of the values and takes a name of its own, which is TESTED for every row.
      for (int i = 0; i < projection.size(); i++) {
        sanitisers.add(disclosureOrdinal(projection.get(i), fold));
        names.add(DisclosureColumns.CARRIED_PREFIX + i);
      }
      for (int i = 0; i < lifted.size(); i++) {
        sanitisers.add(DisclosureColumns.ordinal(chalk.ir.v1.Disclosure.DISCLOSURE_TEST, rexBuilder));
        names.add(DisclosureColumns.CARRIED_PREFIX + (projection.size() + i));
      }
    }

    RelNode projected =
        LogicalProject.create(
            leaf,
            com.google.common.collect.ImmutableList.<org.apache.calcite.rel.hint.RelHint>of(),
            sanitisers,
            names,
            java.util.Set.<org.apache.calcite.rel.core.CorrelationId>of());

    if (!options.includeDisclosureColumns()) {
      emitted.put(projected, disclosed);
      if (!lifted.isEmpty()) {
        widened.put(projected, lifted.size());
      }
      return projected;
    }

    // The carried columns are the leaf's own and no node above indexes them, so the leaf hands back
    // a row of exactly the arity it had: the pass rebuilds a parent whose input's type changed, and
    // an arity it did not ask for is not a change it could carry out. The pull-up puts them back.
    Disclosed[] wide = new Disclosed[width * 2];
    java.util.Arrays.fill(wide, Disclosed.FULL);
    System.arraycopy(disclosed, 0, wide, 0, disclosed.length);
    emitted.put(projected, wide);

    List<RexNode> narrow = new ArrayList<>(width);
    for (int i = 0; i < width; i++) {
      narrow.add(rexBuilder.makeInputRef(projected, i));
    }
    RelNode restored =
        LogicalProject.create(
            projected,
            com.google.common.collect.ImmutableList.<org.apache.calcite.rel.hint.RelHint>of(),
            narrow,
            names.subList(0, width),
            java.util.Set.<org.apache.calcite.rel.core.CorrelationId>of());
    emitted.put(restored, disclosed);
    carriers.put(restored, projected);
    if (!lifted.isEmpty()) {
      widened.put(restored, lifted.size());
    }
    return restored;
  }

  /**
   * One lifted comparison as the leaf computes it (§2, D261): the sanitiser's own {@code CASE},
   * branch for branch, with the comparison where the rule permits <em>this shape</em> and a NULL
   * everywhere else.
   *
   * <p>A blanket comparison would answer for rows the rule does not reach — a principal who may
   * confirm an identifier in one organisation would learn of a match in another — so the test is
   * conditioned exactly as the value is, and a row no test rule matches answers unknown, which is
   * what a predicate, a {@code FILTER} and a select list each read as "not this row".
   *
   * <p>Three-valued for the value too: a NULL raw value compares to NULL, and a host that must hide
   * nullness does not grant a test on a nullable column (§3).
   */
  private RexNode test(TestLift.Lifted lifted, LeafFold fold) {
    DescriptorExpressions.Column entitled = fold.descriptor().column(lifted.tableColumn());
    Reachable reachable = fold.reachable().get(lifted.tableColumn());
    RelDataType unknown =
        typeFactory.createTypeWithNullability(
            typeFactory.createSqlType(org.apache.calcite.sql.type.SqlTypeName.BOOLEAN), true);
    RexNode none = rexBuilder.makeNullLiteral(unknown);

    List<RexNode> operands = new ArrayList<>();
    for (int i = 0; i < reachable.rules().size(); i++) {
      int rule = reachable.rules().get(i);
      chalk.ir.v1.DisclosureRule declared = entitled.rules().get(rule);
      RexNode value =
          declared.getTestsList().contains(lifted.shape()) ? lifted.comparison() : none;
      RexNode condition = reachable.conditions().get(i);
      if (condition.isAlwaysTrue()) {
        operands.add(value);
        return cast(operands, unknown);
      }
      operands.add(condition);
      operands.add(value);
    }
    operands.add(none);
    return cast(operands, unknown);
  }

  /**
   * The lifted comparison, cast to a nullable boolean and <b>not simplified</b>.
   *
   * <p>A sanitiser is simplified under the leaf's own conjuncts (§3.3), and those are the folded row
   * predicate's <em>and the statement's own</em> — which is exactly what a lifted comparison must not
   * be simplified under, because the statement's conjunct <em>is</em> the comparison: assuming it
   * while computing it reduces {@code national_id = 'x'} to TRUE and the predicate then selects every
   * visible row. The rule conditions around it were simplified at fold time, where the assumption is
   * the one §3.3 means; the comparison itself is left to the Hep pass, which simplifies it under
   * whatever is true where it ends up.
   */
  private RexNode cast(List<RexNode> operands, RelDataType target) {
    RexNode expression =
        operands.size() == 1
            ? operands.get(0)
            : rexBuilder.makeCall(SqlStdOperatorTable.CASE, operands);
    return rexBuilder.makeCast(target, expression, /* matchNullability= */ true, /* safe= */ false);
  }

  /**
   * {@code Project_D} for a leaf standing on the parent side of a {@code through} join: the key,
   * the match marker, and the verdicts (§3.13, D228).
   *
   * <p>It is the leaf's own projection and not one above it, because that is where the raw row is
   * (§3.1) — the verdicts read the parent's tenancy columns, and under execute-time binding they
   * read the marker columns that live below this projection and nowhere else. Every column of it is
   * nullable, so the join above answers the same way whether it is LEFT or INNER.
   */
  private RelNode projectAsParent(
      TableScan scan, LeafFold fold, RelNode leaf, ParentProjection asParent) {
    RelDataType rowType = fullRowType(scan);
    List<RexNode> projects = new ArrayList<>(2 + asParent.verdicts().size());
    List<String> names = new ArrayList<>(2 + asParent.verdicts().size());

    projects.add(sanitiser(asParent.keyColumn(), rowType, disclosedRowType(scan), fold));
    names.add(rowType.getFieldList().get(asParent.keyColumn()).getName());
    projects.add(
        rexBuilder.makeCast(
            typeFactory.createTypeWithNullability(
                typeFactory.createSqlType(org.apache.calcite.sql.type.SqlTypeName.BOOLEAN), true),
            rexBuilder.makeLiteral(true)));
    names.add(chalk.planner.ReservedNames.PREFIX + "matched");
    for (int i = 0; i < asParent.verdicts().size(); i++) {
      RexNode verdict = asParent.verdicts().get(i);
      projects.add(
          rexBuilder.makeCast(
              typeFactory.createTypeWithNullability(verdict.getType(), true), verdict));
      names.add(chalk.planner.ReservedNames.PREFIX + "verdict" + i);
    }

    RelNode projected =
        LogicalProject.create(
            leaf,
            ImmutableList.<org.apache.calcite.rel.hint.RelHint>of(),
            projects,
            names,
            java.util.Set.<org.apache.calcite.rel.core.CorrelationId>of());
    Disclosed[] disclosed = new Disclosed[projects.size()];
    java.util.Arrays.fill(disclosed, Disclosed.FULL);
    disclosed[0] = fold.map().of(asParent.keyColumn());
    emitted.put(projected, disclosed);
    return projected;
  }

  /**
   * Every leaf whose carried disclosure columns are waiting one node down: the narrowing projection
   * the pass handed the tree, and the wide one under it that holds them (D207).
   */
  private final Map<RelNode, RelNode> carriers = new IdentityHashMap<>();

  /**
   * What column {@code column} disclosed for this row, as the report's own ordinal (§3.12, D207).
   *
   * <p>The sanitiser's own {@code CASE}, branch for branch, with a name where it has a value: the two
   * cannot disagree because they are built from the same reachable rules in the same order.
   */
  private RexNode disclosureOrdinal(int column, LeafFold fold) {
    if (fold.statistical().contains(column)) {
      return ordinal(Disclosure.DISCLOSURE_AGGREGATE_ONLY);
    }
    DescriptorExpressions.Column entitled = fold.descriptor().column(column);
    Reachable reachable = fold.reachable().get(column);

    List<RexNode> operands = new ArrayList<>();
    for (int i = 0; i < reachable.rules().size(); i++) {
      RexNode condition = reachable.conditions().get(i);
      RexNode value = ordinal(entitled.rules().get(reachable.rules().get(i)).getThen());
      if (condition.isAlwaysTrue()) {
        operands.add(value);
        return finishOrdinal(operands, fold.underFilter());
      }
      operands.add(condition);
      operands.add(value);
    }
    operands.add(ordinal(reachable.otherwise()));
    return finishOrdinal(operands, fold.underFilter());
  }

  private RexNode finishOrdinal(List<RexNode> operands, RexSimplify underFilter) {
    RexNode expression =
        operands.size() == 1
            ? operands.get(0)
            : rexBuilder.makeCall(SqlStdOperatorTable.CASE, operands);
    return underFilter.simplify(expression);
  }

  /** A disclosure as the ordinal {@code ReportedDisclosure} gives it, which is the meet's order. */
  private RexNode ordinal(Disclosure name) {
    return DisclosureColumns.ordinal(normalise(name), rexBuilder);
  }

  /**
   * One branch's own fold: the leaf's descriptor with every membership of the branch replaced by the
   * answer that branch gives it, and the disclosure map derived again under that answer.
   *
   * <p>This is what makes the branches say different things — a manager's rows disclosed in full on
   * the semi-join branch, the creator's own rows on the anti-join branch — and what keeps the
   * sub-query out of {@code Project_D}, where a scalar {@code IN} would need the aggregate the IR
   * does not carry.
   */
  private LeafFold branchFold(TableScan scan, LeafFold whole, MembershipSplit.Branch branch) {
    DescriptorExpressions descriptor = whole.descriptor();
    for (RexSubQuery membership : branch.holds()) {
      String text = membership.toString();
      descriptor =
          descriptor.rewrite(
              expression -> MembershipSplit.substitute(expression, text, true, rexBuilder));
    }
    for (RexSubQuery membership : branch.fails()) {
      String text = membership.toString();
      descriptor =
          descriptor.rewrite(
              expression -> MembershipSplit.substitute(expression, text, false, rexBuilder));
    }
    // The branch predicate is kept even where it folded to TRUE: the branch's rows are the ones its
    // memberships decide, so its visibility is SOME and never ALL, which is what the report says.
    return leafFold(
        scan, descriptor, whole.statementConjuncts(), branch.predicate(), whole.trusted());
  }

  /** What stands in {@code Filter_R}: the branch's memberships, then what is left of the predicate. */
  private @Nullable RexNode branchCondition(LeafFold fold, MembershipSplit.@Nullable Branch branch) {
    List<RexNode> conjuncts = new ArrayList<>();
    if (branch != null) {
      conjuncts.addAll(branch.holds());
    }
    if (fold.predicate() != null && !fold.trusted()) {
      conjuncts.add(fold.predicate());
    }
    if (conjuncts.isEmpty()) {
      return null;
    }
    RexNode condition = RexUtil.composeConjunction(rexBuilder, conjuncts);
    return condition.isAlwaysTrue() ? null : condition;
  }

  /**
   * {@code left} anti-joined against the membership's own relation on its operands — the half of the
   * F36 split that answers "this list does not hold this row" without a three-valued {@code NOT IN}.
   *
   * <p>An anti-join outputs its left row and nothing else, so the leaf's row type and every
   * expression that indexes it are unchanged; and a NULL key never matches, which for {@code ANTI}
   * keeps the row — exactly the rows a filter reading {@code IN} as unknown-as-false would have
   * dropped.
   */
  private RelNode antiJoin(RelNode left, RexSubQuery membership) {
    int width = left.getRowType().getFieldCount();
    RelNode right = membership.rel;
    List<RexNode> equalities = new ArrayList<>(membership.getOperands().size());
    for (int i = 0; i < membership.getOperands().size(); i++) {
      RexNode key = membership.getOperands().get(i);
      RexNode member =
          rexBuilder.makeInputRef(right.getRowType().getFieldList().get(i).getType(), width + i);
      equalities.add(
          rexBuilder.makeCall(SqlStdOperatorTable.EQUALS, key, member));
    }
    return org.apache.calcite.rel.logical.LogicalJoin.create(
        left,
        right,
        ImmutableList.<org.apache.calcite.rel.hint.RelHint>of(),
        RexUtil.composeConjunction(rexBuilder, equalities),
        java.util.Set.<org.apache.calcite.rel.core.CorrelationId>of(),
        org.apache.calcite.rel.core.JoinRelType.ANTI);
  }

  /**
   * The table's every column as the <b>catalog declares</b> it, whatever this scan projects: the row
   * the source stores, which is the row the descriptor's expressions index and the scan beneath the
   * leaf reads (§3.1, F58).
   *
   * <p>It is not the scan's own row type any more. The table publishes to the validator and the
   * converter the row a statement <em>sees</em> — a column any rule can withhold or mask to NULL is
   * nullable there (§1, D161) — and that is {@link #disclosedRowType}, which is what the leaf's
   * {@code Project_D} produces and what every node above it was typed against.
   */
  RelDataType fullRowType(TableScan scan) {
    RelOptTable table = unprojected(scan.getTable());
    ChalkTable chalk = table.unwrap(ChalkTable.class);
    return chalk == null ? table.getRowType() : chalk.declaredRowType(typeFactory);
  }

  /** The table's every column as a statement sees it: the row {@code Project_D} hands back (F58). */
  static RelDataType disclosedRowType(TableScan scan) {
    return unprojected(scan.getTable()).getRowType();
  }

  /**
   * The type of one table column as the <b>catalog declares</b> it — the type the raw value has
   * inside {@code Project_D}, which is where a lifted comparison is computed (D261).
   */
  static RelDataType rawColumnType(TableScan scan, int tableColumn) {
    RelOptTable table = unprojected(scan.getTable());
    ChalkTable chalk = table.unwrap(ChalkTable.class);
    RelDataType rowType =
        chalk == null
            ? table.getRowType()
            : chalk.declaredRowType(scan.getCluster().getTypeFactory());
    return rowType.getFieldList().get(tableColumn).getType();
  }

  /** The table column each of this scan's output columns reads, in output order. */
  static List<Integer> projectionOf(TableScan scan) {
    chalk.planner.plan.rel.ProjectedRelOptTable projected =
        scan.getTable().unwrap(chalk.planner.plan.rel.ProjectedRelOptTable.class);
    return projected == null
        ? org.apache.calcite.util.ImmutableIntList.identity(scan.getRowType().getFieldCount())
        : projected.projection();
  }

  private static RelOptTable unprojected(RelOptTable table) {
    chalk.planner.plan.rel.ProjectedRelOptTable projected =
        table.unwrap(chalk.planner.plan.rel.ProjectedRelOptTable.class);
    return projected == null ? table : projected.unprojected();
  }

  /**
   * The scan again, over a table handle that carries this leaf's disclosure map (§3.8, §3.10) and
   * publishes the table's <b>declared</b> row type.
   *
   * <p>The declared row type is what this scan actually reads: the sanitisers above it are evaluated
   * over the raw row (§3.1), and the {@code Read} this becomes tells the source the columns it
   * stores. A statement's own row type is the disclosed one, and {@code Project_D} is where the two
   * meet — it casts each sanitiser to the disclosed column type (F58).
   */
  private RelNode withDisclosure(TableScan scan, DisclosureMap map) {
    RelOptTable entitled =
        EntitledRelOptTable.of(unprojected(scan.getTable()), map, fullRowType(scan));
    if (scan instanceof chalk.planner.plan.rel.ChalkTableScan) {
      return chalk.planner.plan.rel.ChalkTableScan.create(scan.getCluster(), entitled);
    }
    return org.apache.calcite.rel.logical.LogicalTableScan.create(
        scan.getCluster(), entitled, scan.getHints());
  }

  // ------------------------------------------------------------------ the rules

  /** Which disclosures a column's folded rules can still yield at this leaf, in rule order. */
  record Reachable(
      List<Integer> rules,
      List<RexNode> conditions,
      Disclosure otherwise,
      boolean otherwiseReachable) {

    /** The disclosure names still on the table here, read off the folded rules and never the text. */
    Set<Disclosure> names(DescriptorExpressions.@Nullable Column entitled) {
      Set<Disclosure> names = EnumSet.noneOf(Disclosure.class);
      if (entitled != null) {
        for (int rule : rules) {
          names.add(normalise(entitled.rules().get(rule).getThen()));
        }
      }
      if (otherwiseReachable) {
        names.add(normalise(otherwise));
      }
      return names;
    }
  }

  /**
   * What this column discloses at this leaf (§3.1). One reachable name is that name; several is
   * {@code PER_ROW}, except that a reachable {@code AGGREGATE_ONLY} dominates — it is the constraint
   * the trace of §3.4 and the guard of §3.6 both key on, and a column that is population-only for
   * some rows is population-only for the statement.
   */
  private static Disclosed outcome(Set<Disclosure> names) {
    if (names.contains(Disclosure.DISCLOSURE_AGGREGATE_ONLY)) {
      return Disclosed.AGGREGATE;
    }
    // TEST dominates NONE the way AGGREGATE_ONLY dominates everything (D261): the leaf emits the
    // placeholder for the column under both, and what tells them apart is that a permitted
    // comparison is computed at the leaf as well. So a principal who may test some of the rows they
    // can see and nothing of the rest is TESTED and not PER_ROW — the value is the same stand-in for
    // every row, and the comparison answers over the raw value wherever the rule reaches it.
    if (names.contains(Disclosure.DISCLOSURE_TEST)
        && EnumSet.of(Disclosure.DISCLOSURE_TEST, Disclosure.DISCLOSURE_NONE).containsAll(names)) {
      return Disclosed.TESTED;
    }
    if (names.size() == 1) {
      return switch (names.iterator().next()) {
        case DISCLOSURE_FULL -> Disclosed.FULL;
        case DISCLOSURE_MASKED -> Disclosed.MASKED;
        case DISCLOSURE_TEST -> Disclosed.TESTED;
        default -> Disclosed.REDACTED;
      };
    }
    return Disclosed.PER_ROW;
  }

  /**
   * The rules that can still fire, with every condition simplified under the leaf's own conjuncts.
   *
   * <p>A condition is read as {@code IS TRUE}: first match wins means the rule matches when its
   * condition holds, not when it is unknown — and it keeps the sanitiser's own nullability off the
   * conditions, which is what stops a NOT NULL column widening for no reason.
   */
  private Reachable reachable(
      DescriptorExpressions.@Nullable Column entitled,
      TableEntitlement entitlement,
      RexSimplify underFilter,
      List<RexNode> conjuncts) {
    Disclosure otherwise =
        entitled == null
            ? defaultDisclosure(entitlement)
            : normalise(entitled.entitlement().getOtherwise());

    if (entitled == null) {
      return new Reachable(List.of(), List.of(), otherwise, true);
    }

    List<Integer> rules = new ArrayList<>();
    List<RexNode> conditions = new ArrayList<>();
    boolean otherwiseReachable = true;
    List<DisclosureRule> declared = entitled.rules();
    for (int i = 0; i < declared.size(); i++) {
      RexNode condition =
          holds(entitled.condition(i), conjuncts)
              ? rexBuilder.makeLiteral(true)
              : underFilter.simplifyUnknownAsFalse(
                  rexBuilder.makeCall(SqlStdOperatorTable.IS_TRUE, entitled.condition(i)));
      if (condition.isAlwaysFalse()) {
        continue;
      }
      rules.add(i);
      conditions.add(condition);
      if (condition.isAlwaysTrue()) {
        otherwiseReachable = false;
        break;
      }
    }
    return new Reachable(rules, conditions, otherwise, otherwiseReachable);
  }

  /**
   * Whether a rule's condition is one of the conjuncts every row reaching this leaf already
   * satisfies — in which case it holds, without asking the simplifier.
   *
   * <p>Not an optimisation. {@code RexSimplify} does not reduce a {@code SEARCH} against an
   * identical {@code SEARCH} among its assumptions, so an auditor in <em>two</em> organisations —
   * whose scope condition is textually the row predicate — got a {@code CASE} and a {@code PER_ROW}
   * report where an auditor in one got a constant {@code MASKED}. The conservative answer was not
   * wrong, but it cost the caller the constant name, and {@code Omit} and a bare mask with it, for
   * no reason anyone could see. Textual identity is the narrow, sound case: a condition that
   * <em>is</em> a conjunct of the filter below it holds for every row above it.
   */
  private boolean holds(RexNode condition, List<RexNode> conjuncts) {
    if (holdsUnderConjuncts(condition, conjuncts)) {
      return true;
    }
    // The same question of the condition simplified on its own, which is what a rule condition
    // built by a compiler needs: a policy package writes one scope expression per role, so the
    // condition arrives as an OR whose other disjuncts this principal's empty lists made FALSE,
    // and only after simplification is it the conjunct the filter below already carries.
    return holdsUnderConjuncts(simplify.simplifyUnknownAsFalse(condition), conjuncts);
  }

  private static boolean holdsUnderConjuncts(RexNode condition, List<RexNode> conjuncts) {
    String text = condition.toString();
    for (RexNode conjunct : conjuncts) {
      if (conjunct.toString().equals(text)) {
        return true;
      }
    }
    return false;
  }

  private static Disclosure defaultDisclosure(TableEntitlement entitlement) {
    Disclosure declared = entitlement.getDefaultDisclosure();
    return declared == Disclosure.DISCLOSURE_UNSPECIFIED || declared == Disclosure.UNRECOGNIZED
        ? Disclosure.DISCLOSURE_FULL
        : declared;
  }

  /** {@code UNSPECIFIED} reads as {@code NONE}: a column whose rules run out discloses nothing. */
  private static Disclosure normalise(Disclosure declared) {
    return declared == Disclosure.DISCLOSURE_UNSPECIFIED || declared == Disclosure.UNRECOGNIZED
        ? Disclosure.DISCLOSURE_NONE
        : declared;
  }

  // ------------------------------------------------------------------ the sanitiser

  /**
   * The sanitiser of one column: {@code CASE WHEN when_1 THEN leaf(then_1) … ELSE leaf(otherwise)
   * END}, built from data rather than recognised in a converted tree (§3.2), pruned to the branches
   * the fold left reachable and simplified under the leaf's own conjuncts.
   */
  private RexNode sanitiser(
      int column, RelDataType rowType, RelDataType disclosedRow, LeafFold fold) {
    RelDataType columnType = rowType.getFieldList().get(column).getType();
    // What the statement's own expressions were typed against, which is what this column has to come
    // back as: the declared type, widened where any rule of this table can withhold the value or mask
    // it to NULL (F58, §1, D161). The two are the same type for every column no rule can withhold.
    RelDataType disclosedType = disclosedRow.getFieldList().get(column).getType();
    String columnName = rowType.getFieldList().get(column).getName();
    RexNode ref = rexBuilder.makeInputRef(columnType, column);
    if (fold.statistical().contains(column)) {
      // Raw by opt-in: the sanitiser is not applied at all, and §3.4's positions plus the group-size
      // guard are what stands in its place — cast to the disclosed type, because the row shape is
      // the statement's and this column's opt-in is not a shape the caller can see (D203).
      return disclosedType.equals(columnType)
          ? ref
          : rexBuilder.makeCast(disclosedType, ref, /* matchNullability= */ true, /* safe= */ false);
    }
    DescriptorExpressions.Column entitled = fold.descriptor().column(column);
    Reachable reachable = fold.reachable().get(column);
    ChalkTable table = fold.table();

    List<RexNode> operands = new ArrayList<>();
    for (int i = 0; i < reachable.rules().size(); i++) {
      int rule = reachable.rules().get(i);
      RexNode condition = reachable.conditions().get(i);
      RexNode value =
          value(entitled.rules().get(rule).getThen(), entitled, rule, ref, columnType, table,
              columnName);
      if (condition.isAlwaysTrue()) {
        operands.add(value);
        return finish(operands, disclosedType, fold.underFilter(), ref);
      }
      operands.add(condition);
      operands.add(value);
    }
    operands.add(
        value(reachable.otherwise(), entitled, -1, ref, columnType, table, columnName));
    return finish(operands, disclosedType, fold.underFilter(), ref);
  }

  private RexNode finish(
      List<RexNode> operands, RelDataType disclosedType, RexSimplify underFilter, RexNode ref) {
    RexNode expression =
        operands.size() == 1 ? operands.get(0) : rexBuilder.makeCall(SqlStdOperatorTable.CASE, operands);
    RexNode simplified = underFilter.simplify(expression);
    // Where the fold came to the column itself, the sanitiser *is* the column and the projection
    // says so, with the column's own declared type (F91). The disclosed type is nullable for every
    // column any rule of the table can withhold, this principal's included, so taking it here wrote
    // a cast that only re-stated a nullability the value cannot have — `CAST($2 AS STRING?)` over a
    // NOT NULL column a policy discloses in full. Calcite folded that cast away until the 1.43 pin,
    // where `RexSimplify` stopped folding through one because a cast can throw
    // (`docs/design/calcite-143-trial.md` §7, `SafeRexVisitor`); the plan then carried it, and a
    // host reading the plan's output types was told the column may be NULL where the report said
    // `Full`. Not emitting it is what the simplifier was being relied on for.
    if (simplified.equals(ref)) {
      return ref;
    }
    // The column as the statement sees it: its declared type, already widened where any rule of the
    // table can withhold the value or mask it to NULL (F58), and widened here too where this
    // principal's own sanitiser turns out nullable and the row type did not say so — which the
    // retyper above then carries up, as it always did (V62). Keeping the disclosed type is what
    // stops a mask's incidental precision — SUBSTRING(x, 1, 1) is VARCHAR(1) — from changing the
    // statement's row shape (D161: every principal gets the same one).
    if (disclosedType.isStruct()) {
      return composite(simplified, disclosedType);
    }
    RelDataType target =
        typeFactory.createTypeWithNullability(
            disclosedType, disclosedType.isNullable() || simplified.getType().isNullable());
    return rexBuilder.makeCast(target, simplified, /* matchNullability= */ true, /* safe= */ false);
  }

  /**
   * D302: a composite column's sanitiser, which is never cast — no cast of a composite exists — and
   * is typed explicitly as the column's composite made nullable, fields as declared. Its rules can
   * only disclose it or withhold it (registration refuses every other verdict), so what the fold
   * leaves is the column, its NULL composite, or a {@code CASE} choosing between the two: a choice
   * between composites of one type (D295). Calcite's own {@code CASE} inference goes through
   * {@code createTypeWithNullability}, which would make every field nullable and so type the choice
   * as another composite.
   */
  private RexNode composite(RexNode simplified, RelDataType disclosedType) {
    RelDataType target = chalk.planner.types.TypeMapper.nullable(typeFactory, disclosedType);
    if (simplified instanceof org.apache.calcite.rex.RexLiteral literal && literal.isNull()) {
      return rexBuilder.makeNullLiteral(target);
    }
    if (simplified instanceof org.apache.calcite.rex.RexCall call
        && call.getKind() == org.apache.calcite.sql.SqlKind.CASE) {
      return rexBuilder.makeCall(target, SqlStdOperatorTable.CASE, call.getOperands());
    }
    if (org.apache.calcite.sql.type.SqlTypeUtil.equalSansNullability(
        typeFactory, simplified.getType(), target)) {
      return simplified;
    }
    throw new PolicyException(
        "a composite column's disclosure folded to " + simplified + ", which is neither the column,"
            + " its NULL composite nor a choice between the two. This is a bug in the entitlement"
            + " rewrite: please report the statement and the catalog.");
  }

  /** {@code leaf(name)} of §3.2. */
  private RexNode value(
      Disclosure name,
      DescriptorExpressions.@Nullable Column entitled,
      int rule,
      RexNode ref,
      RelDataType columnType,
      ChalkTable table,
      String columnName) {
    return switch (normalise(name)) {
      case DISCLOSURE_FULL -> ref;
      case DISCLOSURE_MASKED -> {
        RexNode mask = entitled == null ? null : rule < 0 ? entitled.mask() : entitled.maskOf(rule);
        if (mask == null) {
          throw new PolicyException(
              table.schemaName()
                  + "."
                  + table.tableName()
                  + "."
                  + columnName
                  + " discloses MASKED and states no mask, so there is nothing to put in the "
                  + "value's place (docs/design/16-entitlements.md §1).");
        }
        yield mask;
      }
      // Tainted on purpose. The trace of §3.4 has already refused every consumer that is not an
      // allow-listed population aggregate, so reaching here means the raw value may leave the leaf
      // and §3.6 guards every call that takes it.
      case DISCLOSURE_AGGREGATE_ONLY -> ref;
      // A tested column's *value* is a placeholder exactly as under NONE (D261). What the rule
      // permits is a comparison, and a comparison is not a value: it is lifted into this same
      // projection as a derived boolean of its own (TestLift), beside this stand-in and never in
      // place of it.
      case DISCLOSURE_TEST -> placeholder(entitled, rule, columnType);
      default -> placeholder(entitled, rule, columnType);
    };
  }

  /**
   * What stands in for an undisclosed value (§3.11, D162, D224): the matching rule's own {@code
   * placeholder} where it states one, else the column's, and otherwise the request's {@code
   * PlaceholderPolicy} — a typed NULL with the output widened to nullable, or the empty value with
   * the declared nullability kept.
   *
   * @param rule the rule that redacted the value, or -1 for the {@code otherwise} branch
   */
  private RexNode placeholder(
      DescriptorExpressions.@Nullable Column entitled, int rule, RelDataType columnType) {
    RexNode declared = entitled == null ? null : entitled.placeholderOf(rule);
    if (declared != null) {
      return declared;
    }
    if (options.placeholders() == PlaceholderPolicy.PLACEHOLDER_POLICY_AS_EMPTY
        && hasEmptyValue(columnType)) {
      return rexBuilder.makeZeroLiteral(columnType);
    }
    // D302: a composite column's placeholder is the NULL composite of its own type, fields as declared.
    return rexBuilder.makeNullLiteral(chalk.planner.types.TypeMapper.nullable(typeFactory, columnType));
  }

  /**
   * Whether {@code PlaceholdersAsEmpty} has a value for this type — which is to say whether Calcite's
   * {@code RexBuilder.makeZeroLiteral} defines one (F47).
   *
   * <p>Calcite's table covers characters, binaries, every numeric kind, BOOLEAN, DATE, TIME and
   * TIMESTAMP (the epoch) and TIMESTAMP_TZ (year one, not the epoch); for every other type name it
   * throws {@code Util.unexpected}. Chalk's own reachable remainder is UUID, LIST (an ARRAY) and the
   * two interval families, and for those the placeholder is a typed NULL with the output widened —
   * "empty" is a policy choice and not a property of a type, so there is no zero table of Chalk's own
   * to fall back to and an error inside the sidecar is the one answer that helps nobody.
   *
   * <p>The set is enumerated rather than discovered by catching the throw, because an
   * {@code AssertionError} is not a control-flow signal and a list that says which types have an
   * empty value is a list a test can hold against the documentation.
   */
  static boolean hasEmptyValue(RelDataType type) {
    return switch (type.getSqlTypeName()) {
      case CHAR, VARCHAR, BINARY, VARBINARY, TINYINT, SMALLINT, INTEGER, BIGINT, DECIMAL, FLOAT,
              REAL, DOUBLE, BOOLEAN, DATE, TIME, TIME_TZ, TIME_WITH_LOCAL_TIME_ZONE, TIMESTAMP,
              TIMESTAMP_TZ, TIMESTAMP_WITH_LOCAL_TIME_ZONE ->
          true;
      default -> false;
    };
  }

  /**
   * The column on which this statement's own predicate and this principal's scope are disjoint
   * (§3.12, D207), or empty when they are not.
   *
   * <p>{@code WHERE org_id = 3} against a scope of {1, 2} returns nothing, and to a caller that
   * reads the same as an empty table. What distinguishes the two is that the <em>statement alone</em>
   * is satisfiable and the statement <em>with</em> the policy is not, which is a question about this
   * principal's context and this statement's text and about no hidden row at all — which is exactly
   * what D207 permits an acknowledgement to depend on.
   *
   * <p>Answered here rather than after the Hep pass, where §3.12 first looks for it: the pruning to
   * empty that would be observed there is this contradiction's <em>consequence</em>, and the
   * conjuncts that make it are in hand at the leaf while there they have been merged and moved.
   */
  private String contradiction(
      List<RexNode> statementConjuncts, @Nullable RexNode folded, RelDataType rowType) {
    if (folded == null || statementConjuncts.isEmpty() || folded.isAlwaysFalse()) {
      return "";
    }
    List<RexNode> statement = new ArrayList<>();
    for (RexNode conjunct : statementConjuncts) {
      if (RexUtil.SubQueryFinder.find(conjunct) == null) {
        statement.add(conjunct);
      }
    }
    if (statement.isEmpty() || RexUtil.SubQueryFinder.find(folded) != null) {
      return "";
    }

    RexNode alone = simplify.simplifyUnknownAsFalse(RexUtil.composeConjunction(rexBuilder, statement));
    if (alone.isAlwaysFalse()) {
      return "";
    }
    List<RexNode> both = new ArrayList<>(statement);
    both.add(folded);
    if (!simplify
        .simplifyUnknownAsFalse(RexUtil.composeConjunction(rexBuilder, both))
        .isAlwaysFalse()) {
      return "";
    }

    // The column both sides speak about: the one the caller has to change.
    org.apache.calcite.util.ImmutableBitSet statementColumns =
        org.apache.calcite.util.ImmutableBitSet.of();
    for (RexNode conjunct : statement) {
      statementColumns = statementColumns.union(RelOptUtil.InputFinder.bits(conjunct));
    }
    org.apache.calcite.util.ImmutableBitSet shared =
        statementColumns.intersect(RelOptUtil.InputFinder.bits(folded));
    for (int column : shared) {
      if (column < rowType.getFieldCount()) {
        return rowType.getFieldList().get(column).getName();
      }
    }
    return "";
  }

  /** {@code RexUtil.conjunctions}, named so the leaf reads as prose. */
  private static final class RelOptUtilConjunctions {
    private RelOptUtilConjunctions() {}

    static List<RexNode> of(RexNode predicate) {
      List<RexNode> conjuncts = org.apache.calcite.plan.RelOptUtil.conjunctions(predicate);
      List<RexNode> usable = new ArrayList<>(conjuncts.size());
      for (RexNode conjunct : conjuncts) {
        // A conjunct holding a sub-query is not something RexSimplify can reason with, and handing
        // it one makes the simplifier walk a relation it has no metadata for.
        if (RexUtil.SubQueryFinder.find(conjunct) == null) {
          usable.add(conjunct);
        }
      }
      return usable;
    }
  }
}
