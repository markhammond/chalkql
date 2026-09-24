package chalk.planner.entitlement;

import chalk.planner.catalog.ChalkTable;
import chalk.planner.plan.rel.ProjectedRelOptTable;
import java.util.ArrayList;
import java.util.IdentityHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import org.apache.calcite.plan.RelOptPredicateList;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.core.AggregateCall;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.metadata.RelColumnOrigin;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexExecutor;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexSimplify;
import org.apache.calcite.rex.RexSubQuery;
import org.apache.calcite.rex.RexUtil;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Fail closed: the taint check (docs/design/16-entitlements.md §3.10, D201).
 *
 * <p>The check proves the model for every plan rather than asserting it, and it runs twice — once on
 * the logical tree immediately after the pass, and once on the physical tree after optimisation.
 * <b>Every failure is a {@code POLICY} error and also a bug</b>, and the message says so: a
 * legitimate statement that trips a clause has found a hole in the rewrite, not in itself.
 *
 * <ol>
 *   <li><b>Logical, right after the pass.</b> Every {@code TableScan} of an entitled table anywhere
 *       in the tree — sub-query relations included — is one the pass wrapped, and every aggregate
 *       call over a population-only column is one that column's allow-list names. Here, before the
 *       Hep pass, because {@code AGGREGATE_REDUCE_FUNCTIONS} later rewrites {@code AVG} into
 *       {@code SUM0} and {@code COUNT} and a physical check against a host's list of {@code AVG}
 *       alone would refuse a correct plan.
 *   <li><b>Physical, tainted values reach only aggregates.</b> Every reference whose column origin is
 *       a population-only column, and is that column rather than something derived from it, is an
 *       argument of a population aggregate of the permitted set.
 *   <li><b>Physical, the row predicate survived.</b> At each entitled leaf's consumer the pulled-up
 *       predicates contain every conjunct of the folded {@code Filter_R}, whether the filter was
 *       pushed, merged or became an index lookup.
 *   <li><b>Physical, the root.</b> No root output column has a non-derived origin in a redacted
 *       column.
 * </ol>
 *
 * <p>A node the check does not know fails it: an unknown column origin is not an absent one.
 */
public final class TaintCheck {
  private TaintCheck() {}

  private static final String BUG =
      " This is a POLICY refusal and also a bug in the entitlement rewrite: a statement that trips"
          + " the taint check has found a hole in the rewrite, not in itself. Please report the"
          + " statement and the catalog (docs/design/16-entitlements.md §3.10).";

  // ------------------------------------------------------------------ clause 1

  /**
   * Clause 1, on the logical tree immediately after the pass: an identity set, and the allow-list.
   */
  public static void logical(RelNode rel) {
    everyEntitledScanIsWrapped(rel);
    allowList(rel);
  }

  private static void everyEntitledScanIsWrapped(RelNode rel) {
    if (rel instanceof TableScan scan) {
      ChalkTable table = scan.getTable().unwrap(ChalkTable.class);
      if (table != null
          && table.isEntitled()
          && EntitledRelOptTable.disclosureOf(scan.getTable()) == null
          // A path's key set reads the bridge and the endpoint raw, and says so with its own handle
          // (D265 §2, §7): nothing of either leaves the chain, so there is nothing to sanitise and
          // no disclosure map to carry. A statement's own occurrence of the same table is entitled
          // as that table's policy says, quite separately.
          && !CorrelationRelOptTable.isCorrelation(scan.getTable())) {
        throw new PolicyException(
            "a scan of "
                + table.schemaName()
                + "."
                + table.tableName()
                + " survived the entitlement rewrite unwrapped, so nothing sanitises it."
                + BUG);
      }
      return;
    }
    for (RelNode input : rel.getInputs()) {
      everyEntitledScanIsWrapped(input);
    }
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            everyEntitledScanIsWrapped(subQuery.rel);
            return super.visitSubQuery(subQuery);
          }
        });
  }

  /**
   * Every aggregate call over a population-only column is one that column's allow-list names,
   * re-asserted on the rewritten tree. The trace of §3.4 decided this before the rewrite; this is
   * the clause that proves it of the tree that will actually be optimised.
   */
  private static void allowList(RelNode rel) {
    if (rel instanceof Aggregate aggregate) {
      Map<Integer, Tainted> in = taintedColumnsOf(aggregate.getInput());
      for (AggregateCall call : aggregate.getAggCallList()) {
        for (int argument : call.getArgList()) {
          Tainted tainted = in.get(argument);
          if (tainted == null) {
            continue;
          }
          String function = call.getAggregation().getName().toUpperCase(Locale.ROOT);
          if (!tainted.allowList().contains(function)) {
            throw new PolicyException(
                tainted.name()
                    + " is population-only for this principal and "
                    + function
                    + " is not one of the aggregates it permits ("
                    + String.join(", ", tainted.allowList())
                    + ")."
                    + BUG);
          }
        }
      }
    }
    for (RelNode input : rel.getInputs()) {
      allowList(input);
    }
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            allowList(subQuery.rel);
            return super.visitSubQuery(subQuery);
          }
        });
  }

  /** One population-only column as clause 1 sees it on the rewritten tree. */
  private record Tainted(String name, List<String> allowList) {}

  /**
   * Which of a rewritten node's columns are still the raw population-only value.
   *
   * <p>Structural rather than metadata-driven, and only through the shapes the tree has immediately
   * after the pass: a bare reference in a projection, a filter, a sort. Anything else is not a
   * pass-through and the value stops being traced there, which is safe — the trace of §3.4 already
   * refused every use that is not an aggregate.
   */
  private static Map<Integer, Tainted> taintedColumnsOf(RelNode rel) {
    if (rel instanceof TableScan) {
      // The scan emits raw columns and Project_D above it is where they become values, so nothing
      // leaves a scan tainted as far as this clause is concerned.
      return Map.of();
    }
    if (rel instanceof org.apache.calcite.rel.core.Project project) {
      Map<Integer, Tainted> in = taintedColumnsOf(project.getInput());
      Map<Integer, Tainted> out = new java.util.LinkedHashMap<>();
      DisclosureMap leaf = leafOf(project);
      List<RexNode> exprs = project.getProjects();
      for (int i = 0; i < exprs.size(); i++) {
        if (!(exprs.get(i) instanceof RexInputRef ref)) {
          continue;
        }
        if (leaf != null) {
          // A Project_D: its bare references are the raw columns the leaf emits. A statistical
          // column is one of them and its permitted uses are wider (D203), so the allow-list clause
          // is not the one that judges it.
          int column = tableColumnOf(project.getInput(), ref.getIndex());
          if (leaf.of(column) == Disclosed.AGGREGATE && !leaf.isStatistical(column)) {
            out.put(i, taintOf(leaf, project.getInput(), column));
          }
          continue;
        }
        Tainted tainted = in.get(ref.getIndex());
        if (tainted != null) {
          out.put(i, tainted);
        }
      }
      return out;
    }
    if (rel instanceof org.apache.calcite.rel.core.Filter filter) {
      return taintedColumnsOf(filter.getInput());
    }
    if (rel instanceof org.apache.calcite.rel.core.Sort sort) {
      return taintedColumnsOf(sort.getInput());
    }
    return Map.of();
  }

  /** The disclosure map of the scan directly under {@code project}, when there is one. */
  private static @Nullable DisclosureMap leafOf(org.apache.calcite.rel.core.Project project) {
    RelNode input = leafSpine(project.getInput());
    return input instanceof TableScan scan
        ? EntitledRelOptTable.disclosureOf(scan.getTable())
        : null;
  }

  /**
   * Down from a projection to the leaf it stands on, through everything the pass itself may have put
   * between the two: the row filter, and — under execute-time binding — the joins that compute each
   * distinct membership once as a marker column (§3.2, D209), and the anti-joins of the F36 split.
   *
   * <p>What makes a join one of those is that its right side reads bound relations and nothing else.
   * A join to a catalog table is the statement's own and stops the descent, which is what keeps this
   * from mistaking somebody else's projection for the leaf's own.
   */
  private static RelNode leafSpine(RelNode rel) {
    RelNode node = rel;
    while (true) {
      if (node instanceof org.apache.calcite.plan.volcano.RelSubset subset) {
        RelNode best = subset.getBest();
        node = best == null ? subset.getOriginal() : best;
        continue;
      }
      // The source boundary is not a node of the statement's own: what stands under it is the read
      // the pass wrapped, and the projection above it is still that leaf's own Project_D wherever
      // the cost model decided to evaluate it (§3.8, D261).
      if (node instanceof chalk.planner.plan.rel.SourceToLocalConverter converter) {
        node = converter.getInput();
        continue;
      }
      if (node instanceof org.apache.calcite.rel.core.Filter filter) {
        node = filter.getInput();
        continue;
      }
      if (node instanceof org.apache.calcite.rel.core.Join join
          && MembershipSplit.readsOnlyContext(join.getRight())) {
        node = join.getLeft();
        continue;
      }
      return node;
    }
  }

  private static int tableColumnOf(RelNode leaf, int column) {
    RelNode node = leaf;
    while (!(node instanceof TableScan) && !node.getInputs().isEmpty()) {
      node = node.getInputs().get(0);
    }
    if (!(node instanceof TableScan scan)) {
      return column;
    }
    ProjectedRelOptTable projected = scan.getTable().unwrap(ProjectedRelOptTable.class);
    return projected == null || column >= projected.projection().size()
        ? column
        : projected.projection().get(column);
  }

  private static Tainted taintOf(DisclosureMap map, RelNode leaf, int column) {
    RelNode node = leaf;
    while (!(node instanceof TableScan) && !node.getInputs().isEmpty()) {
      node = node.getInputs().get(0);
    }
    ChalkTable table =
        node instanceof TableScan scan ? scan.getTable().unwrap(ChalkTable.class) : null;
    List<String> allowList = new ArrayList<>();
    String name = map.qualifiedName() + ".column" + column;
    if (table != null && column < table.descriptor().getColumnsCount()) {
      name = map.qualifiedName() + "." + table.descriptor().getColumns(column).getName();
      for (chalk.ir.v1.ColumnEntitlement entitled :
          table.descriptor().getEntitlement().getColumnsList()) {
        if (entitled.getColumn() == column) {
          for (String function : entitled.getAggregateOnlyFunctionsList()) {
            allowList.add(function.trim().toUpperCase(Locale.ROOT));
          }
        }
      }
    }
    return new Tainted(name, allowList);
  }

  // ------------------------------------------------------------------ clauses 2, 3 and 4

  /**
   * One {@code through} join the pass emitted, as the finished plan must still hold it
   * (docs/design/16-entitlements.md §3.13, §3.10 as extended).
   *
   * @param childTable the entitled child, qualified
   * @param childColumn the correlation key, by the child's own column ordinal
   * @param parentTable the parent, qualified
   * @param parentColumn the parent's key, by the parent's own column ordinal
   * @param ownRestriction what the child restricts <b>by itself</b>, folded — the other disjuncts of
   *     its {@code Filter_R} beside this marker, over the child's own columns; null where it has
   *     none, where they fold to FALSE, and where they fold to TRUE (the marker is then elided and
   *     no evidence is recorded at all). F69 reads it.
   */
  public record ThroughEvidence(
      String childTable,
      int childColumn,
      String parentTable,
      int parentColumn,
      boolean rawParent,
      @Nullable RexNode foldedKey,
      @Nullable RexNode ownRestriction) {

    public ThroughEvidence(
        String childTable, int childColumn, String parentTable, int parentColumn) {
      this(childTable, childColumn, parentTable, parentColumn, false, null, null);
    }

    /**
     * The same, for the key-set join of a declared path (D265 §7): the other side of that join is
     * the chain's own base table, scanned <b>raw</b>, so the origin to look for is the table itself
     * rather than an entitled leaf's disclosure map.
     */
    public static ThroughEvidence raw(
        String childTable, int childColumn, String parentTable, int parentColumn) {
      return raw(childTable, childColumn, parentTable, parentColumn, null, null);
    }

    /**
     * The same, where the endpoint predicate pins the chain's key to one constant (F67, ADR 0052):
     * {@code foldedKey} is that constant, and the join may wear it in place of the column.
     */
    public static ThroughEvidence raw(
        String childTable,
        int childColumn,
        String parentTable,
        int parentColumn,
        @Nullable RexNode foldedKey) {
      return raw(childTable, childColumn, parentTable, parentColumn, foldedKey, null);
    }

    /** The same, carrying what the child restricts by itself beside this marker (F69). */
    public static ThroughEvidence raw(
        String childTable,
        int childColumn,
        String parentTable,
        int parentColumn,
        @Nullable RexNode foldedKey,
        @Nullable RexNode ownRestriction) {
      return new ThroughEvidence(
          childTable, childColumn, parentTable, parentColumn, true, foldedKey, ownRestriction);
    }
  }

  /** The physical clauses, after optimisation. */
  public static void physical(
      RelNode physical,
      List<DisclosureMap> leaves,
      Map<String, RexNode> rowPredicates,
      RelMetadataQuery mq,
      RexBuilder rexBuilder,
      RexExecutor executor) {
    physical(physical, leaves, rowPredicates, List.of(), mq, rexBuilder, executor);
  }

  public static void physical(
      RelNode physical,
      List<DisclosureMap> leaves,
      Map<String, RexNode> rowPredicates,
      List<ThroughEvidence> throughJoins,
      RelMetadataQuery mq,
      RexBuilder rexBuilder,
      RexExecutor executor) {
    if (leaves.isEmpty()) {
      return;
    }
    tainted(physical, mq, new IdentityHashMap<>());
    rowPredicateSurvived(
        physical, List.of(), rowPredicates, contextTablesIn(physical), mq, rexBuilder, executor);
    throughJoinsSurvived(physical, throughJoins, mq, rexBuilder, executor);
    root(physical, mq);
  }

  /**
   * Clause 3, as §3.13 extends it: every {@code through} join is still there, with its key
   * condition, somewhere the child's rows had to pass through.
   *
   * <p>A child entitled through a parent has no row predicate a walk could read — its
   * {@code Filter_R} is an OR of join markers, and a marker does not survive above {@code Project_D}
   * to be found textually. What survives is the <em>shape</em>: the join, on the declared key, with
   * the parent's own entitled leaf under it. That leaf carries its own {@code Filter_R} and clause 3
   * proper checks it, so the two together are the whole of "this row's parent was visible".
   */
  private static void throughJoinsSurvived(
      RelNode physical,
      List<ThroughEvidence> joins,
      RelMetadataQuery mq,
      RexBuilder rexBuilder,
      RexExecutor executor) {
    for (ThroughEvidence join : joins) {
      // A leaf the optimiser proved empty is pruned, and its joins go with it: a statement whose own
      // predicate compares a column this principal receives as a placeholder is the commonest way
      // to get one. Asking for the join of a plan that reads no row of the table at all would
      // refuse the one answer that is certainly safe, which is the same reading the pass takes of a
      // `Filter_R` that folded to FALSE (§3.10, §3.13; D265 §7).
      if (!holdsLeafOf(physical, join.childTable(), new IdentityHashMap<>())) {
        continue;
      }
      if (!findThroughJoin(physical, join, mq, new IdentityHashMap<>())
          && !markerCannotDecide(physical, join, mq, rexBuilder, executor)) {
        throw new PolicyException(
            "the visibility of "
                + join.childTable()
                + (join.rawParent() ? " derives along a path through " : " derives through ")
                + join.parentTable()
                + ", and the join that establishes it — on column "
                + join.childColumn()
                + " against the parent's column "
                + join.parentColumn()
                + " — is not in the finished plan, so rows whose parent this principal cannot see"
                + " could reach the result."
                + BUG);
      }
    }
  }

  /**
   * Clause 3's third form: the marker's join is <b>gone</b>, and the plan's own predicates say it
   * could not have decided a row anyway (F69, ADR 0054).
   *
   * <p>A target that holds a tenancy directly <em>and</em> along a path has
   * {@code Filter_R = own OR marker} (D265 §4). Where every row of this leaf that reaches its
   * consumers satisfies {@code own}, the disjunction is true of all of them whatever the marker
   * said, so the key set adds nothing — and a LEFT JOIN to a key set that is {@code DISTINCT} on its
   * key adds and drops no row either, which is exactly the licence Calcite's
   * {@code ProjectJoinRemoveRule} takes when it deletes the join in the Hep pre-pass. Asking for a
   * join no correct plan has a reason to hold is asking for the wrong thing.
   *
   * <p>The evidence is the same {@code RelMdPredicates} clause 3 proper reads for its conjuncts, and
   * it is read at <b>every</b> occurrence of the leaf: an occurrence whose rows are not guaranteed
   * to satisfy {@code own} is one the marker still decides. Where the leaf restricts nothing by
   * itself — the marker is the whole of {@code Filter_R} — there is nothing to imply and the answer
   * is the refusal it was.
   */
  private static boolean markerCannotDecide(
      RelNode physical,
      ThroughEvidence join,
      RelMetadataQuery mq,
      RexBuilder rexBuilder,
      RexExecutor executor) {
    if (join.ownRestriction() == null) {
      return false;
    }
    return ownRestrictionGuaranteed(
        physical,
        List.of(),
        join,
        new RexSimplify(rexBuilder, RelOptPredicateList.EMPTY, executor),
        mq,
        rexBuilder);
  }

  /** {@link #markerCannotDecide}, at every occurrence of the leaf and in every sub-query. */
  private static boolean ownRestrictionGuaranteed(
      RelNode rel,
      List<RelNode> consumers,
      ThroughEvidence join,
      RexSimplify simplify,
      RelMetadataQuery mq,
      RexBuilder rexBuilder) {
    DisclosureMap map = leafMapOf(rel);
    if (map != null) {
      if (!map.qualifiedName().equals(join.childTable())) {
        return true;
      }
      List<RelNode> above = consumers.isEmpty() ? List.of(rel) : consumers;
      return guarantees(
          pulledUpInTableTerms(above, map, mq, rexBuilder),
          join.ownRestriction(),
          simplify,
          rexBuilder);
    }
    List<RelNode> above = prepend(rel, consumers);
    for (RelNode input : rel.getInputs()) {
      if (!ownRestrictionGuaranteed(input, above, join, simplify, mq, rexBuilder)) {
        return false;
      }
    }
    boolean[] everywhere = {true};
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            everywhere[0] &=
                ownRestrictionGuaranteed(
                    subQuery.rel, List.of(), join, simplify, mq, rexBuilder);
            return super.visitSubQuery(subQuery);
          }
        });
    return everywhere[0];
  }

  /** Whether the finished plan still reads any entitled leaf of this table. */
  private static boolean holdsLeafOf(RelNode rel, String table, Map<RelNode, Boolean> seen) {
    if (seen.put(rel, Boolean.TRUE) != null) {
      return false;
    }
    if (rel instanceof TableScan scan) {
      DisclosureMap map = EntitledRelOptTable.disclosureOf(scan.getTable());
      if (map != null && map.qualifiedName().equals(table)) {
        return true;
      }
    }
    for (RelNode input : rel.getInputs()) {
      if (holdsLeafOf(input, table, seen)) {
        return true;
      }
    }
    boolean[] found = {false};
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            found[0] |= holdsLeafOf(subQuery.rel, table, seen);
            return super.visitSubQuery(subQuery);
          }
        });
    return found[0];
  }

  private static boolean findThroughJoin(
      RelNode rel, ThroughEvidence join, RelMetadataQuery mq, Map<RelNode, Boolean> seen) {
    if (seen.put(rel, Boolean.TRUE) != null) {
      return false;
    }
    if (rel instanceof org.apache.calcite.rel.core.Join node && establishes(node, join, mq)) {
      return true;
    }
    if (join.foldedKey() != null && restrictsToTheFoldedKey(rel, join, mq)) {
      return true;
    }
    for (RelNode input : rel.getInputs()) {
      if (findThroughJoin(input, join, mq, seen)) {
        return true;
      }
    }
    boolean[] found = {false};
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            found[0] |= findThroughJoin(subQuery.rel, join, mq, seen);
            return super.visitSubQuery(subQuery);
          }
        });
    return found[0];
  }

  /** Whether this join's condition equates the child's correlation key with the parent's own. */
  private static boolean establishes(
      org.apache.calcite.rel.core.Join node, ThroughEvidence join, RelMetadataQuery mq) {
    int left = node.getLeft().getRowType().getFieldCount();
    for (RexNode conjunct : RelOptUtil.conjunctions(node.getCondition())) {
      if (!(conjunct instanceof org.apache.calcite.rex.RexCall call)
          || call.getKind() != org.apache.calcite.sql.SqlKind.EQUALS
          || call.getOperands().size() != 2
          || !(call.getOperands().get(0) instanceof RexInputRef a)
          || !(call.getOperands().get(1) instanceof RexInputRef b)) {
        continue;
      }
      if ((isOrigin(node, a.getIndex(), left, join.childTable(), join.childColumn(), false, mq)
              && isOrigin(
                  node, b.getIndex(), left, join.parentTable(), join.parentColumn(),
                  join.rawParent(), mq))
          || (isOrigin(node, b.getIndex(), left, join.childTable(), join.childColumn(), false, mq)
              && isOrigin(
                  node, a.getIndex(), left, join.parentTable(), join.parentColumn(),
                  join.rawParent(), mq))) {
        return true;
      }
      // The folded form of the same join (F67): the key set is one row, so its key is projected as
      // the constant the endpoint predicate pinned it to and has no column origin left to match.
      if (join.foldedKey() != null
          && ((isOrigin(node, a.getIndex(), left, join.childTable(), join.childColumn(), false, mq)
                  && isFoldedKey(node, b.getIndex(), left, join.foldedKey(), mq))
              || (isOrigin(
                      node, b.getIndex(), left, join.childTable(), join.childColumn(), false, mq)
                  && isFoldedKey(node, a.getIndex(), left, join.foldedKey(), mq)))) {
        return true;
      }
    }
    return false;
  }

  /**
   * Clause 3's second form, where the endpoint predicate pinned the chain's key to one constant
   * (F67, ADR 0052): the plan restricts the target's own correlation key to that constant directly.
   *
   * <p>A key set of one row is the constant, so the join to it and a filter against it say the same
   * thing, and the optimiser writes whichever it costs less — commonly both, the filter inferred
   * across the join. What is asked for is therefore the <em>restriction</em> rather than the join:
   * a node of the finished plan whose field is pinned to the key set's own constant and whose origin
   * is the target's correlation key. ADR 0048 deviation 4 read a pruned leaf the same way; this
   * reads a collapsed one.
   */
  private static boolean restrictsToTheFoldedKey(
      RelNode rel, ThroughEvidence join, RelMetadataQuery mq) {
    RelOptPredicateList predicates = mq.getPulledUpPredicates(rel);
    if (predicates == null || predicates.constantMap.isEmpty()) {
      return false;
    }
    for (Map.Entry<RexNode, RexNode> pinned : predicates.constantMap.entrySet()) {
      if (!(pinned.getKey() instanceof RexInputRef ref)
          || !sameConstant(pinned.getValue(), join.foldedKey())) {
        continue;
      }
      Set<RelColumnOrigin> origins = mq.getColumnOrigins(rel, ref.getIndex());
      if (origins == null) {
        continue;
      }
      for (RelColumnOrigin origin : origins) {
        if (!origin.isDerived()
            && named(origin, join.childTable(), false)
            && ordinalOf(origin) == join.childColumn()) {
          return true;
        }
      }
    }
    return false;
  }

  /** Whether this side's field is the constant the key set folded to (F67). */
  private static boolean isFoldedKey(
      org.apache.calcite.rel.core.Join node,
      int index,
      int left,
      RexNode folded,
      RelMetadataQuery mq) {
    RelNode side = index < left ? node.getLeft() : node.getRight();
    int field = index < left ? index : index - left;
    if (side instanceof org.apache.calcite.rel.core.Values values) {
      RexNode only = null;
      for (List<org.apache.calcite.rex.RexLiteral> tuple : values.getTuples()) {
        RexNode value = tuple.get(field);
        if (only != null && !only.equals(value)) {
          return false;
        }
        only = value;
      }
      return only != null && sameConstant(only, folded);
    }
    RelOptPredicateList predicates = mq.getPulledUpPredicates(side);
    if (predicates == null) {
      return false;
    }
    for (Map.Entry<RexNode, RexNode> pinned : predicates.constantMap.entrySet()) {
      if (pinned.getKey() instanceof RexInputRef ref
          && ref.getIndex() == field
          && sameConstant(pinned.getValue(), folded)) {
        return true;
      }
    }
    return false;
  }

  /**
   * Whether two constants are the same value. The pass folds the endpoint predicate with the
   * descriptor's own types and the optimiser carries the result through casts of its own, so the
   * comparison is by value rather than by node.
   */
  private static boolean sameConstant(@Nullable RexNode found, @Nullable RexNode wanted) {
    if (found == null || wanted == null) {
      return false;
    }
    RexNode a = withoutCasts(found);
    RexNode b = withoutCasts(wanted);
    if (!(a instanceof org.apache.calcite.rex.RexLiteral x)
        || !(b instanceof org.apache.calcite.rex.RexLiteral y)) {
      return a.equals(b);
    }
    Comparable<?> left = x.getValueAs(Comparable.class);
    Comparable<?> right = y.getValueAs(Comparable.class);
    if (left == null || right == null) {
      return false;
    }
    if (left instanceof java.math.BigDecimal p && right instanceof java.math.BigDecimal q) {
      return p.compareTo(q) == 0;
    }
    return left.equals(right);
  }

  private static RexNode withoutCasts(RexNode node) {
    RexNode found = node;
    while (found.getKind() == org.apache.calcite.sql.SqlKind.CAST
        && found instanceof org.apache.calcite.rex.RexCall call
        && !call.getOperands().isEmpty()) {
      found = call.getOperands().get(0);
    }
    return found;
  }

  /** An origin's ordinal in the table's own numbering, seeing through a pushed-down projection. */
  private static int ordinalOf(RelColumnOrigin origin) {
    ProjectedRelOptTable projected = origin.getOriginTable().unwrap(ProjectedRelOptTable.class);
    int ordinal = origin.getOriginColumnOrdinal();
    if (projected != null && ordinal >= 0 && ordinal < projected.projection().size()) {
      return projected.projection().get(ordinal);
    }
    return ordinal;
  }

  private static boolean isOrigin(
      org.apache.calcite.rel.core.Join node,
      int index,
      int left,
      String table,
      int column,
      boolean raw,
      RelMetadataQuery mq) {
    RelNode side = index < left ? node.getLeft() : node.getRight();
    Set<RelColumnOrigin> origins = mq.getColumnOrigins(side, index < left ? index : index - left);
    if (origins == null) {
      return false;
    }
    for (RelColumnOrigin origin : origins) {
      if (origin.isDerived()) {
        continue;
      }
      if (!named(origin, table, raw)) {
        continue;
      }
      if (ordinalOf(origin) == column) {
        return true;
      }
    }
    return false;
  }

  /**
   * Whether this origin is the table the evidence names: by its entitled leaf's disclosure map for a
   * {@code through} parent, and by the table itself for the raw base of a path's key set (D265 §7),
   * which carries no entitled wrapper because the mechanism reads it raw and discloses none of it.
   */
  private static boolean named(RelColumnOrigin origin, String table, boolean raw) {
    if (!raw) {
      DisclosureMap map = EntitledRelOptTable.disclosureOf(origin.getOriginTable());
      return map != null && map.qualifiedName().equals(table);
    }
    chalk.planner.catalog.ChalkTable found =
        origin.getOriginTable().unwrap(chalk.planner.catalog.ChalkTable.class);
    return found != null && (found.schemaName() + "." + found.tableName()).equals(table);
  }

  /** Clause 2: a raw population-only value reaches only a permitted population aggregate. */
  private static void tainted(RelNode rel, RelMetadataQuery mq, Map<RelNode, Boolean> seen) {
    if (seen.put(rel, Boolean.TRUE) != null) {
      return;
    }
    for (RelNode input : rel.getInputs()) {
      tainted(input, mq, seen);
    }

    if (rel instanceof Aggregate aggregate) {
      for (AggregateCall call : aggregate.getAggCallList()) {
        String function = call.getAggregation().getName().toUpperCase(Locale.ROOT);
        for (int argument : call.getArgList()) {
          if (isRawPopulationOnly(aggregate.getInput(), argument, mq)
              && !PopulationAggregates.isPermitted(call.getAggregation())) {
            throw new PolicyException(
                "a population-only column reaches "
                    + function
                    + ", which is not one of the population aggregates D190 permits."
                    + BUG);
          }
        }
        if (call.filterArg >= 0 && isRawPopulationOnly(aggregate.getInput(), call.filterArg, mq)) {
          throw new PolicyException(
              "a population-only column reaches an aggregate's FILTER clause, which is a predicate"
                  + " on a raw value and therefore an oracle."
                  + BUG);
        }
      }
      return;
    }

    if (rel instanceof org.apache.calcite.rel.core.Sort sort) {
      for (org.apache.calcite.rel.RelFieldCollation field :
          sort.getCollation().getFieldCollations()) {
        if (isRawPopulationOnly(sort.getInput(), field.getFieldIndex(), mq)) {
          throw new PolicyException(
              "a population-only column is a sort key, which orders by the raw value." + BUG);
        }
      }
      return;
    }

    // Every other node. A bare reference is a pass-through and not a use — that is what lets the
    // sanitiser hand the raw value up to the aggregate that may consume it — and so is a CAST of one
    // to a numeric type, since a cast cannot select a row (§3.4). Anything else that reads the raw
    // value is a use, and there is no permitted use outside an aggregate's argument list.
    List<RelNode> inputs = rel.getInputs();
    if (rel instanceof org.apache.calcite.rel.core.Project project) {
      // The leaf's own projection is Project_D, which reads raw columns and is the only place in the
      // plan that may (§3.1): a population-only column's sanitiser holds the column itself in the
      // branch §3.5 permits. It is recognised by position rather than by identity, because a node
      // the pass created has none after optimisation — but a projection sitting directly over the
      // entitled leaf, through filters alone, is that projection however many of the statement's own
      // PROJECT_MERGE folded into it.
      if (leafUnder(project.getInput()) != null) {
        return;
      }
      for (RexNode expr : project.getProjects()) {
        if (isPassThrough(expr)) {
          continue;
        }
        // The one expression over a raw population-only column D261 permits: a comparison of a
        // shape the column's own rules name, whose other operands read no column of the row. It is
        // accepted by its *shape* and by the descriptor's own list of permitted shapes, not on the
        // pass's word — which is what makes this a check rather than an assertion, and is how
        // §3.4's own allow-list is read a few lines above. Where the leaf's projection is
        // recognised by position the clause returned already; this is the same projection after the
        // optimiser put it where the position rule cannot see it, which a pushed plan does.
        if (readsRawOnlyInPermittedTests(expr, inputs, mq)) {
          continue;
        }
        // The other expression §3.5 puts over a raw population-only column: its own sanitiser,
        // recognised by *shape* where the position rule above cannot see it. A declared path puts
        // the verdict the marker joins compute into projections of its own between `Project_D` and
        // the leaf (D265, §3.14), and `leafSpine` descends through filters and context-only joins
        // and not through those — so the projection holding the sanitiser is no longer "directly
        // over the leaf" and the position rule stops recognising it (F86).
        if (isSanitisedUse(expr, inputs, mq)) {
          continue;
        }
        refuseAnyRawUse(expr, inputs, rel, mq);
      }
      return;
    }
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitCall(org.apache.calcite.rex.RexCall call) {
            if (isPassThrough(call)) {
              return call;
            }
            return super.visitCall(call);
          }

          @Override
          public RexNode visitInputRef(RexInputRef ref) {
            refuse(ref, inputs, rel, mq);
            return ref;
          }
        });
  }

  /** The entitled leaf directly below {@code rel}, through filters alone, or null. */
  private static @Nullable DisclosureMap leafUnder(RelNode rel) {
    return leafMapOf(leafSpine(rel));
  }

  /**
   * Whether {@code expr} is a comparison of a population-only column that the column's own rules
   * permit as a test (D261, docs/design/36-test-verdict.md §2).
   *
   * <p>{@code =} and {@code <>} with a bare reference on one side and, on the other, an operand that
   * reads no column of the row; or a disjunction of such equalities over one column, which is what
   * {@code IN (list)} is by the time there is a plan. The shapes are read off the descriptor the
   * origin's own table carries, so the clause decides for itself rather than believing the pass.
   */
  private static boolean readsRawOnlyInPermittedTests(
      RexNode expr, List<RelNode> inputs, RelMetadataQuery mq) {
    if (isPermittedTest(expr, inputs, mq)) {
      return true;
    }
    if (expr instanceof RexInputRef ref) {
      // A bare reference is a pass-through and not a use (§3.4); what matters here is that it is
      // not a *value* use, which the branch above already answered for.
      return !isRawPopulationOnlyRef(ref, inputs, mq);
    }
    if (expr instanceof org.apache.calcite.rex.RexCall call) {
      for (RexNode operand : call.getOperands()) {
        if (!readsRawOnlyInPermittedTests(operand, inputs, mq)) {
          return false;
        }
      }
      return true;
    }
    return true;
  }

  /** Whether this reference reads a raw population-only column of an entitled leaf. */
  private static boolean isRawPopulationOnlyRef(
      RexInputRef ref, List<RelNode> inputs, RelMetadataQuery mq) {
    return isRawPopulationOnlyAt(ref.getIndex(), inputs, mq);
  }

  /** The same, for a field index into the concatenated inputs. */
  private static boolean isRawPopulationOnlyAt(
      int index, List<RelNode> inputs, RelMetadataQuery mq) {
    int offset = 0;
    for (RelNode input : inputs) {
      int width = input.getRowType().getFieldCount();
      if (index < offset + width) {
        return isRawPopulationOnly(input, index - offset, mq);
      }
      offset += width;
    }
    return false;
  }

  /** Whether any reference in {@code expr} reads a raw population-only column. */
  private static boolean readsRawPopulationOnly(
      RexNode expr, List<RelNode> inputs, RelMetadataQuery mq) {
    for (int index : org.apache.calcite.plan.RelOptUtil.InputFinder.bits(expr)) {
      if (isRawPopulationOnlyAt(index, inputs, mq)) {
        return true;
      }
    }
    return false;
  }

  /**
   * Whether every reference to a raw population-only column in {@code expr} stands where §3.5 puts
   * one — as a whole arm of a sanitising {@code CASE} — and nowhere else.
   *
   * <p>This is the sanitiser of §3.2 read by shape rather than by position, and it is the *stronger*
   * of the two readings: where the position rule accepts whatever a projection over the leaf holds,
   * this one accepts the raw value only as a value being carried, never as something a condition
   * selects rows by. A condition may read the column exactly where §3.4's own allow-list already
   * permits it — a comparison of a shape the descriptor names, whose other operands read no column
   * of the row (D261) — and an arm may be a bare reference, a numeric {@code CAST} of one (§3.4's
   * pass-throughs) or a nested sanitiser, which is what a mixed-rights principal's rule list folds
   * to.
   *
   * <p>It discloses nothing a bare reference does not: the clause already lets a raw population-only
   * column travel as a pass-through, and what it exists to catch is a *use* — a predicate, a sort
   * key, arithmetic, an aggregate outside the allow-list — which is precisely what a condition
   * reading the raw value would be, and is refused here.
   */
  private static boolean isSanitisedUse(RexNode expr, List<RelNode> inputs, RelMetadataQuery mq) {
    if (!readsRawPopulationOnly(expr, inputs, mq)) {
      return true;
    }
    if (isPassThrough(expr)) {
      return true;
    }
    if (!(expr instanceof org.apache.calcite.rex.RexCall call)
        || call.getKind() != org.apache.calcite.sql.SqlKind.CASE) {
      return false;
    }
    List<RexNode> operands = call.getOperands();
    for (int i = 0; i < operands.size(); i++) {
      RexNode operand = operands.get(i);
      boolean condition = i % 2 == 0 && i < operands.size() - 1;
      if (condition) {
        if (!readsRawOnlyInPermittedTests(operand, inputs, mq)) {
          return false;
        }
      } else if (!isSanitisedUse(operand, inputs, mq)) {
        return false;
      }
    }
    return true;
  }

  private static boolean isPermittedTest(RexNode expr, List<RelNode> inputs, RelMetadataQuery mq) {
    if (!(expr instanceof org.apache.calcite.rex.RexCall call)) {
      return false;
    }
    if (call.getKind() == org.apache.calcite.sql.SqlKind.OR) {
      for (RexNode operand : call.getOperands()) {
        if (!(operand instanceof org.apache.calcite.rex.RexCall equality)
            || equality.getKind() != org.apache.calcite.sql.SqlKind.EQUALS
            || !isPermittedComparison(equality, inputs, mq, chalk.ir.v1.TestShape.TEST_SHAPE_IN)) {
          return false;
        }
      }
      return !call.getOperands().isEmpty();
    }
    chalk.ir.v1.TestShape shape =
        switch (call.getKind()) {
          case EQUALS -> chalk.ir.v1.TestShape.TEST_SHAPE_EQUALS;
          case NOT_EQUALS -> chalk.ir.v1.TestShape.TEST_SHAPE_NOT_EQUALS;
          default -> chalk.ir.v1.TestShape.TEST_SHAPE_UNSPECIFIED;
        };
    return shape != chalk.ir.v1.TestShape.TEST_SHAPE_UNSPECIFIED
        && isPermittedComparison(call, inputs, mq, shape);
  }

  private static boolean isPermittedComparison(
      org.apache.calcite.rex.RexCall call,
      List<RelNode> inputs,
      RelMetadataQuery mq,
      chalk.ir.v1.TestShape shape) {
    if (call.getOperands().size() != 2) {
      return false;
    }
    RexNode left = call.getOperands().get(0);
    RexNode right = call.getOperands().get(1);
    if (left instanceof RexInputRef leftRef && readsNoColumn(right)) {
      return permitsShape(leftRef, inputs, mq, shape);
    }
    return right instanceof RexInputRef rightRef
        && readsNoColumn(left)
        && permitsShape(rightRef, inputs, mq, shape);
  }

  /** Whether the column this reference reads is population-only and permits this shape. */
  private static boolean permitsShape(
      RexInputRef ref, List<RelNode> inputs, RelMetadataQuery mq, chalk.ir.v1.TestShape shape) {
    int index = ref.getIndex();
    int offset = 0;
    for (RelNode input : inputs) {
      int width = input.getRowType().getFieldCount();
      if (index < offset + width) {
        Set<RelColumnOrigin> origins = mq.getColumnOrigins(input, index - offset);
        if (origins == null || origins.isEmpty()) {
          return false;
        }
        for (RelColumnOrigin origin : origins) {
          if (origin.isDerived()
              || disclosureOf(origin) != Disclosed.AGGREGATE
              || !shapesOf(origin).contains(shape)) {
            return false;
          }
        }
        return true;
      }
      offset += width;
    }
    return false;
  }

  /** The shapes the descriptor permits over this origin's column, read off the descriptor itself. */
  private static Set<chalk.ir.v1.TestShape> shapesOf(RelColumnOrigin origin) {
    RelOptTable table = origin.getOriginTable();
    ChalkTable declared = table.unwrap(ChalkTable.class);
    if (declared == null) {
      return Set.of();
    }
    ProjectedRelOptTable projected = table.unwrap(ProjectedRelOptTable.class);
    int ordinal = origin.getOriginColumnOrdinal();
    if (projected != null && ordinal >= 0 && ordinal < projected.projection().size()) {
      ordinal = projected.projection().get(ordinal);
    }
    Set<chalk.ir.v1.TestShape> shapes = java.util.EnumSet.noneOf(chalk.ir.v1.TestShape.class);
    for (chalk.ir.v1.ColumnEntitlement entitled :
        declared.descriptor().getEntitlement().getColumnsList()) {
      if (entitled.getColumn() != ordinal) {
        continue;
      }
      for (chalk.ir.v1.DisclosureRule rule : entitled.getRulesList()) {
        if (rule.getThen() == chalk.ir.v1.Disclosure.DISCLOSURE_AGGREGATE_ONLY) {
          shapes.addAll(rule.getTestsList());
        }
      }
    }
    return shapes;
  }

  /** Whether an operand reads no column of the row: a parameter, a literal, a folded context value. */
  private static boolean readsNoColumn(RexNode operand) {
    return org.apache.calcite.plan.RelOptUtil.InputFinder.bits(operand).isEmpty()
        && org.apache.calcite.rex.RexUtil.SubQueryFinder.find(operand) == null;
  }

  /** A bare reference, or a {@code CAST} of one to a numeric type (§3.4). */
  private static boolean isPassThrough(RexNode expr) {
    if (expr instanceof RexInputRef) {
      return true;
    }
    return expr.getKind() == org.apache.calcite.sql.SqlKind.CAST
        && expr instanceof org.apache.calcite.rex.RexCall cast
        && cast.getOperands().get(0) instanceof RexInputRef
        && isNumeric(expr.getType());
  }

  private static boolean isNumeric(org.apache.calcite.rel.type.RelDataType type) {
    org.apache.calcite.sql.type.SqlTypeFamily family = type.getSqlTypeName().getFamily();
    return family == org.apache.calcite.sql.type.SqlTypeFamily.NUMERIC
        || family == org.apache.calcite.sql.type.SqlTypeFamily.INTEGER
        || family == org.apache.calcite.sql.type.SqlTypeFamily.EXACT_NUMERIC
        || family == org.apache.calcite.sql.type.SqlTypeFamily.APPROXIMATE_NUMERIC;
  }

  private static void refuseAnyRawUse(
      RexNode expr, List<RelNode> inputs, RelNode rel, RelMetadataQuery mq) {
    expr.accept(
        new RexShuttle() {
          @Override
          public RexNode visitInputRef(RexInputRef ref) {
            refuse(ref, inputs, rel, mq);
            return ref;
          }
        });
  }

  private static void refuse(
      RexInputRef ref, List<RelNode> inputs, RelNode rel, RelMetadataQuery mq) {
    int index = ref.getIndex();
    int offset = 0;
    for (RelNode input : inputs) {
      int width = input.getRowType().getFieldCount();
      if (index < offset + width) {
        if (isRawPopulationOnly(input, index - offset, mq)) {
          throw new PolicyException(
              "a population-only column reaches "
                  + rel.getRelTypeName()
                  + " as a value rather than as a population aggregate's argument."
                  + BUG);
        }
        return;
      }
      offset += width;
    }
  }

  /**
   * Whether column {@code index} of {@code rel} <em>is</em> a population-only column whose only
   * permitted consumer is an aggregate.
   *
   * <p>A column left raw by the <b>statistical</b> opt-in (D203) is deliberately not one. It is
   * {@code AGGREGATE} to everything that judges a value, and it may additionally stand in a
   * predicate, a join condition, an aggregate's FILTER and a grouping key — which is what the
   * logical trace of §3.4 checked, together with the statement-level conditions no physical clause
   * could re-derive: that every output is an aggregate or a group key, that there is no window, and
   * that no individual is pinned. What guards it here is the group-size floor, not this clause.
   */
  private static boolean isRawPopulationOnly(RelNode rel, int index, RelMetadataQuery mq) {
    Set<RelColumnOrigin> origins = mq.getColumnOrigins(rel, index);
    if (origins == null) {
      return false;
    }
    for (RelColumnOrigin origin : origins) {
      if (origin.isDerived() || isStatistical(origin)) {
        continue;
      }
      if (disclosureOf(origin) == Disclosed.AGGREGATE) {
        return true;
      }
    }
    return false;
  }

  private static boolean isStatistical(RelColumnOrigin origin) {
    RelOptTable table = origin.getOriginTable();
    DisclosureMap map = EntitledRelOptTable.disclosureOf(table);
    if (map == null) {
      return false;
    }
    ProjectedRelOptTable projected = table.unwrap(ProjectedRelOptTable.class);
    int ordinal = origin.getOriginColumnOrdinal();
    if (projected != null && ordinal >= 0 && ordinal < projected.projection().size()) {
      ordinal = projected.projection().get(ordinal);
    }
    return map.isStatistical(ordinal);
  }

  private static @Nullable Disclosed disclosureOf(RelColumnOrigin origin) {
    RelOptTable table = origin.getOriginTable();
    DisclosureMap map = EntitledRelOptTable.disclosureOf(table);
    if (map == null) {
      return null;
    }
    ProjectedRelOptTable projected = table.unwrap(ProjectedRelOptTable.class);
    int ordinal = origin.getOriginColumnOrdinal();
    if (projected != null && ordinal >= 0 && ordinal < projected.projection().size()) {
      ordinal = projected.projection().get(ordinal);
    }
    return map.of(ordinal);
  }

  /**
   * Clause 3: at each entitled leaf's consumer, the pulled-up predicates contain every conjunct of
   * the folded {@code Filter_R} — however the optimiser moved it.
   *
   * <p>With one shape that a predicate walk cannot see. A bound list above the fold ceiling is not
   * made literal (§2): the conjunct is {@code col IN (SELECT … FROM ctx.<name>)}, and by the time
   * there is a physical tree that sub-query <em>is</em> a semi-join rather than a predicate, so
   * {@code RelMdPredicates} has nothing to pull up for it. Such a conjunct is therefore checked by
   * its evidence instead: the context relation it reads is still scanned somewhere in the plan. A
   * rule that dropped the semi-join would take the scan with it, which is the failure this exists
   * to catch.
   *
   * <p>The evidence is asked for <em>after</em> the implication, never before (F77, ADR 0058): what
   * the plan's own predicates guarantee is guaranteed whatever the open relation holds, and a
   * statement whose filter implies the folded half of {@code Filter_R} has the marker join deleted
   * as redundant — soundly — along with the scan.
   */
  private static void rowPredicateSurvived(
      RelNode rel,
      List<RelNode> consumers,
      Map<String, RexNode> rowPredicates,
      Set<String> contextTables,
      RelMetadataQuery mq,
      RexBuilder rexBuilder,
      RexExecutor executor) {
    DisclosureMap map = leafMapOf(rel);
    if (map != null) {
      RexNode predicate = rowPredicates.get(map.qualifiedName());
      if (predicate != null) {
        checkPulledUp(
            consumers.isEmpty() ? List.of(rel) : consumers,
            map,
            predicate,
            contextTables,
            mq,
            rexBuilder,
            executor);
      }
      return;
    }
    // Every consumer this leaf's rows pass through, innermost first. The parent alone is not
    // enough: the optimiser routinely pushes a projection *below* the tenancy filter, and merges
    // the filter into a join's own condition, so the node that knows about the predicate is often
    // not the one directly above the scan. Any one of them establishing it is enough — every row of
    // this leaf that reaches the result passed through all of them.
    List<RelNode> above = prepend(rel, consumers);
    for (RelNode input : rel.getInputs()) {
      rowPredicateSurvived(input, above, rowPredicates, contextTables, mq, rexBuilder, executor);
    }
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            rowPredicateSurvived(
                subQuery.rel, List.of(), rowPredicates, contextTables, mq, rexBuilder, executor);
            return super.visitSubQuery(subQuery);
          }
        });
  }

  private static List<RelNode> prepend(RelNode rel, List<RelNode> rest) {
    List<RelNode> chain = new ArrayList<>(rest.size() + 1);
    chain.add(rel);
    chain.addAll(rest);
    return chain;
  }

  /** Every context relation the physical tree still scans, by the name the host bound it under. */
  private static Set<String> contextTablesIn(RelNode rel) {
    Set<String> names = new LinkedHashSet<>();
    collectContextTables(rel, names);
    return names;
  }

  private static void collectContextTables(RelNode rel, Set<String> names) {
    // Any scan of one, whichever kind: the logical tree the fold recorded has a
    // `LogicalTableScan` over the `ctx` schema and the physical one a `ChalkContextScan`, and the
    // conjunct and its evidence have to be compared in the same vocabulary.
    if (rel instanceof TableScan scan) {
      ContextTable context = scan.getTable().unwrap(ContextTable.class);
      if (context != null) {
        names.add(context.contextName());
      }
    }
    for (RelNode input : rel.getInputs()) {
      collectContextTables(input, names);
    }
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            collectContextTables(subQuery.rel, names);
            return super.visitSubQuery(subQuery);
          }
        });
  }

  /** The context relations a conjunct reads through a sub-query; empty when it holds none. */
  private static Set<String> contextTablesOf(RexNode conjunct) {
    Set<String> names = new LinkedHashSet<>();
    conjunct.accept(
        new org.apache.calcite.rex.RexVisitorImpl<Void>(true) {
          @Override
          public Void visitSubQuery(RexSubQuery subQuery) {
            collectContextTables(subQuery.rel, names);
            return super.visitSubQuery(subQuery);
          }
        });
    return names;
  }

  /**
   * Whether the conjunction of {@code available} implies {@code required}, by term containment in
   * disjunctive normal form.
   *
   * <p>The one shape {@code RexSimplify} does not settle and the optimiser produces daily. A
   * transitive predicate carried across an equi-join rewrites
   * {@code (member_id = 3 AND org_id = 2) OR created_by = 3} into
   * {@code member_id = 3 AND (org_id = 2 OR created_by = 3)}: strictly stronger, so no row outside
   * the scope survives it, and not textually the recorded predicate. Both sides go to DNF, and the
   * implication holds when <em>every</em> term of what the plan guarantees contains all the literals
   * of <em>some</em> term of what the policy required — which is sound (a row satisfying the former
   * satisfies the latter) and cheap.
   *
   * <p>Incomplete, deliberately: it proves nothing that needs arithmetic, and it gives up rather
   * than expanding a large conjunction. A refusal is the answer when it cannot prove the implication,
   * which is the fail-closed direction.
   */
  private static boolean implies(List<RexNode> available, RexNode required, RexBuilder rexBuilder) {
    if (available.size() > MAX_DNF_CONJUNCTS) {
      return false;
    }
    List<Set<String>> terms = dnfTerms(RexUtil.composeConjunction(rexBuilder, available), rexBuilder);
    List<Set<String>> needed = dnfTerms(required, rexBuilder);
    if (terms.isEmpty() || needed.isEmpty()) {
      return false;
    }
    for (Set<String> term : terms) {
      boolean covered = false;
      for (Set<String> one : needed) {
        if (term.containsAll(one)) {
          covered = true;
          break;
        }
      }
      if (!covered) {
        return false;
      }
    }
    return true;
  }

  /** The disjunctive normal form of {@code node}, each term as its set of literals by text. */
  private static List<Set<String>> dnfTerms(RexNode node, RexBuilder rexBuilder) {
    RexNode dnf;
    try {
      dnf = RexUtil.toDnf(rexBuilder, node);
    } catch (RuntimeException tooBig) {
      return List.of();
    }
    List<RexNode> disjuncts = RelOptUtil.disjunctions(dnf);
    if (disjuncts.size() > MAX_DNF_TERMS) {
      return List.of();
    }
    List<Set<String>> terms = new ArrayList<>(disjuncts.size());
    for (RexNode disjunct : disjuncts) {
      Set<String> literals = new LinkedHashSet<>();
      for (RexNode literal : RelOptUtil.conjunctions(disjunct)) {
        literals.add(literal.toString());
      }
      terms.add(literals);
    }
    return terms;
  }

  private static final int MAX_DNF_CONJUNCTS = 8;
  private static final int MAX_DNF_TERMS = 64;

  /** The disclosure map of an entitled leaf — a scan or an index lookup — or null. */
  private static @Nullable DisclosureMap leafMapOf(RelNode rel) {
    if (rel instanceof TableScan scan) {
      return EntitledRelOptTable.disclosureOf(scan.getTable());
    }
    if (rel instanceof chalk.planner.plan.rel.ChalkIndexLookup lookup) {
      return EntitledRelOptTable.disclosureOf(lookup.getTable());
    }
    return null;
  }

  private static void checkPulledUp(
      List<RelNode> consumers,
      DisclosureMap map,
      RexNode predicate,
      Set<String> contextTables,
      RelMetadataQuery mq,
      RexBuilder rexBuilder,
      RexExecutor executor) {
    List<RexNode> mapped = pulledUpInTableTerms(consumers, map, mq, rexBuilder);
    Set<String> available = new LinkedHashSet<>();
    for (RexNode term : mapped) {
      available.add(term.toString());
    }

    RexSimplify simplify = new RexSimplify(rexBuilder, RelOptPredicateList.EMPTY, executor);
    for (RexNode conjunct : RelOptUtil.conjunctions(predicate)) {
      if (available.contains(conjunct.toString())) {
        continue;
      }
      // Not textually there. It may still be implied, and usually is: a merged filter can be
      // re-simplified, a SEARCH can be rendered as an OR chain, and the optimiser routinely makes a
      // predicate *stronger* than the one the pass recorded — a transitive predicate carried across
      // an equi-join turns `(member_id = 3 AND org_id = 2) OR created_by = 3` into
      // `member_id = 3 AND (org_id = 2 OR created_by = 3)`, which implies it. So the question is
      // asked semantically, three ways, before the plan is refused.
      //
      // Asked *first*, and of every conjunct including one that reads a context relation (F77,
      // ADR 0058). A conjunct the plan's own predicates establish is established whatever the open
      // relation comes to hold, so the relation's scan is evidence of nothing that is still in
      // question — and where the statement's own filter implies the folded half of `Filter_R` the
      // optimiser deletes the marker join and takes that scan with it, soundly. The evidence test
      // below is for the conjunct nothing implies, which is the failure it was written to catch.
      if (guarantees(mapped, conjunct, simplify, rexBuilder)) {
        continue;
      }
      // A conjunct over a context relation became a semi-join; its evidence is the scan.
      Set<String> reads = contextTablesOf(conjunct);
      if (!reads.isEmpty()) {
        if (contextTables.containsAll(reads)) {
          continue;
        }
        throw new PolicyException(
            "the row predicate of "
                + map.qualifiedName()
                + " does not survive to its consumer: the conjunct reads the context relation(s) "
                + reads
                + ", which the plan no longer scans."
                + BUG);
      }
      throw new PolicyException(
          "the row predicate of "
              + map.qualifiedName()
              + " does not survive to its consumer: the conjunct "
              + conjunct
              + " is not among the predicates the plan pulls up, so rows outside this principal's"
              + " scope could reach the result."
              + BUG);
    }
  }

  /**
   * Everything the plan pulls up at {@code consumers}, restated over the entitled table's own
   * columns — the terms that are <em>guaranteed</em> of every row of this leaf that reaches them.
   *
   * <p>A reference that does not resolve to a column of this table, or resolves to a derived one —
   * which is every column {@code Project_D} sanitises — is dropped rather than translated, so what
   * comes back is over raw values only. That is what makes it sound to compare with a predicate the
   * policy wrote.
   */
  private static List<RexNode> pulledUpInTableTerms(
      List<RelNode> consumers, DisclosureMap map, RelMetadataQuery mq, RexBuilder rexBuilder) {
    List<RexNode> mapped = new ArrayList<>();
    for (RelNode consumer : consumers) {
      RelOptPredicateList pulled = mq.getPulledUpPredicates(consumer);
      if (pulled == null) {
        continue;
      }
      for (RexNode conjunct : pulled.pulledUpPredicates) {
        RexNode inTableTerms = toTableTerms(conjunct, consumer, map, mq, rexBuilder);
        if (inTableTerms != null) {
          mapped.add(inTableTerms);
        }
      }
    }
    return mapped;
  }

  /**
   * Whether what the plan guarantees ({@code available}) establishes {@code required}, asked four
   * ways before the answer is no.
   *
   * <p>A merged filter can be re-simplified, a {@code SEARCH} can be rendered as an OR chain, and
   * the optimiser routinely makes a predicate <em>stronger</em> than the one the pass recorded — a
   * transitive predicate carried across an equi-join turns
   * {@code (member_id = 3 AND org_id = 2) OR created_by = 3} into
   * {@code member_id = 3 AND (org_id = 2 OR created_by = 3)}, which implies it. So the question is
   * asked semantically rather than textually, and "no" is the fail-closed answer.
   */
  private static boolean guarantees(
      List<RexNode> available, RexNode required, RexSimplify simplify, RexBuilder rexBuilder) {
    if (available.isEmpty()) {
      return false;
    }
    RexSimplify assuming = simplify.withPredicates(RelOptPredicateList.of(rexBuilder, available));
    // Under what the plan guarantees, is it simply true?
    if (assuming.simplifyUnknownAsFalse(required).isAlwaysTrue()) {
      return true;
    }
    // Or, the same question the other way round: can a row deny it?
    RexNode denial =
        rexBuilder.makeCall(org.apache.calcite.sql.fun.SqlStdOperatorTable.NOT, required);
    if (assuming.simplifyUnknownAsFalse(denial).isAlwaysFalse()) {
      return true;
    }
    RexNode denied =
        rexBuilder.makeCall(
            org.apache.calcite.sql.fun.SqlStdOperatorTable.AND,
            RexUtil.composeConjunction(rexBuilder, available),
            denial);
    if (simplify.simplifyUnknownAsFalse(denied).isAlwaysFalse()) {
      return true;
    }
    // And the one the simplifier cannot do: term containment in disjunctive normal form.
    return implies(available, required, rexBuilder);
  }

  /**
   * A predicate over {@code consumer}'s row, restated over the entitled table's own columns, or null
   * when any of its references does not resolve to one.
   */
  private static @Nullable RexNode toTableTerms(
      RexNode conjunct,
      RelNode consumer,
      DisclosureMap map,
      RelMetadataQuery mq,
      RexBuilder rexBuilder) {
    boolean[] resolved = {true};
    RexNode mapped =
        conjunct.accept(
            new RexShuttle() {
              @Override
              public RexNode visitInputRef(RexInputRef ref) {
                Set<RelColumnOrigin> origins = mq.getColumnOrigins(consumer, ref.getIndex());
                if (origins == null || origins.size() != 1) {
                  resolved[0] = false;
                  return ref;
                }
                RelColumnOrigin origin = origins.iterator().next();
                DisclosureMap at = EntitledRelOptTable.disclosureOf(origin.getOriginTable());
                if (origin.isDerived() || at == null || !at.qualifiedName().equals(map.qualifiedName())) {
                  resolved[0] = false;
                  return ref;
                }
                ProjectedRelOptTable projected =
                    origin.getOriginTable().unwrap(ProjectedRelOptTable.class);
                int ordinal = origin.getOriginColumnOrdinal();
                if (projected != null && ordinal < projected.projection().size()) {
                  ordinal = projected.projection().get(ordinal);
                }
                return rexBuilder.makeInputRef(ref.getType(), ordinal);
              }
            });
    return resolved[0] ? mapped : null;
  }

  /** Clause 4: no root output column is a redacted or tested column itself. */
  private static void root(RelNode physical, RelMetadataQuery mq) {
    for (int i = 0; i < physical.getRowType().getFieldCount(); i++) {
      Set<RelColumnOrigin> origins = mq.getColumnOrigins(physical, i);
      if (origins == null) {
        continue;
      }
      for (RelColumnOrigin origin : origins) {
        if (origin.isDerived()) {
          continue;
        }
        // A tested column is refused here for the same reason a redacted one is, and it is the
        // stronger reading of the same clause: what may leave the leaf under a TEST verdict is one
        // derived comparison, which is a derived origin, and never the column itself (D261).
        if (disclosureOf(origin) == Disclosed.REDACTED
            || disclosureOf(origin) == Disclosed.TESTED) {
          throw new PolicyException(
              "the output column '"
                  + physical.getRowType().getFieldNames().get(i)
                  + "' is a "
                  + (disclosureOf(origin) == Disclosed.TESTED ? "tested" : "redacted")
                  + " column of an entitled table, undisclosed and unsanitised."
                  + BUG);
        }
      }
    }
  }
}
