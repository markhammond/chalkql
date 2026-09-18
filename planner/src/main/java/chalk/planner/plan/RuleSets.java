package chalk.planner.plan;

import chalk.planner.plan.rules.ChalkAsOfJoinRule;
import chalk.planner.plan.rules.ChalkFilterRule;
import chalk.planner.plan.rules.ChalkHashAggregateRule;
import chalk.planner.plan.rules.ChalkHashJoinRule;
import chalk.planner.plan.rules.ChalkIndexLookupRule;
import chalk.planner.plan.rules.ChalkIndexOrderedScanRule;
import chalk.planner.plan.rules.ChalkMergeJoinRule;
import chalk.planner.plan.rules.ChalkNestedLoopJoinRule;
import chalk.planner.plan.rules.PartitionRules;
import chalk.planner.plan.rules.PushdownRules;
import chalk.planner.plan.rules.ChalkProjectRule;
import chalk.planner.plan.rules.ChalkProjectScanRule;
import chalk.planner.plan.rules.ChalkSetOpRules;
import chalk.planner.plan.rules.ChalkSortRule;
import chalk.planner.plan.rules.ChalkContextScanRule;
import chalk.planner.plan.rules.ChalkTableScanRule;
import chalk.planner.plan.rules.ChalkTumbleRule;
import chalk.planner.plan.rules.ChalkUnnestRule;
import chalk.planner.plan.rules.ChalkValuesRule;
import chalk.planner.plan.rules.ChalkWindowRule;
import chalk.planner.plan.rules.ChalkWindowTableFunctionRule;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.RelOptRule;
import org.apache.calcite.rel.rules.CoreRules;
import org.apache.calcite.rel.rules.PruneEmptyRules;

/**
 * The pinned rule lists (docs/design/03-planner.md §4.2, §4.3). Both are {@code List}s built in
 * source order — nothing about plan choice may depend on hash iteration order, because that is where
 * Volcano nondeterminism comes from and the golden-plan tests would flake.
 */
public final class RuleSets {
  private RuleSets() {}

  /**
   * The deterministic Hep pre-pass, in the order it runs. Everything here is a rewrite that is
   * unambiguously an improvement, so running it once bottom-up outside the cost model both shrinks
   * the search space and removes a source of nondeterminism.
   */
  public static List<RelOptRule> hep() {
    return hep(DistinctStrategy.JOIN);
  }

  /**
   * The same list, with the {@code DISTINCT}-aggregate expansion the cost model chose for this query
   * (D54). {@link DistinctStrategy#NATIVE} leaves the aggregate alone, which is what makes a mixed
   * {@code COUNT(DISTINCT x), SUM(y)} plannable at all: M1's grouping-sets expansion produced a shape
   * the IR has no node for.
   */
  public static List<RelOptRule> hep(DistinctStrategy distinct) {
    ImmutableList.Builder<RelOptRule> rules = ImmutableList.builder();
    rules.add(
        // 0. Calcite's TUMBLE table function is a projection that appends the bucket bounds (D52).
        //    It runs first so everything below sees an ordinary Project over the scan.
        ChalkTumbleRule.INSTANCE);
    rules.add(
        // 1. Sub-queries out of the way. The MARK variants rewrite IN / EXISTS / SOME to a LEFT MARK
        //    correlate, which decorrelates into a LEFT MARK join that
        //    MARK_TO_SEMI_OR_ANTI_JOIN_RULE turns into the semi or anti join the query means (D43).
        //    Before joins existed the plain variants were enough, because anything that survived was
        //    rejected anyway.
        CoreRules.FILTER_SUB_QUERY_TO_CORRELATE);
    rules.add(
        CoreRules.PROJECT_SUB_QUERY_TO_CORRELATE,
        CoreRules.JOIN_SUB_QUERY_TO_CORRELATE,

        // 2. Constant folding and the usual filter/project tidying. This is what turns
        //    `date '1998-12-01' - interval '90' day` into a literal (TPC-H Q1).
        CoreRules.FILTER_REDUCE_EXPRESSIONS,
        CoreRules.PROJECT_REDUCE_EXPRESSIONS,
        CoreRules.FILTER_MERGE,
        CoreRules.FILTER_PROJECT_TRANSPOSE,
        CoreRules.PROJECT_MERGE,
        CoreRules.PROJECT_REMOVE,
        // Ordering by a column the WHERE clause pins to a constant is ordering by nothing. Without
        // this the constant that PROJECT_REDUCE_EXPRESSIONS substitutes hides the declared collation
        // and corpus query 06 sorts for no reason.
        CoreRules.SORT_REMOVE_CONSTANT_KEYS,

        // 3. Aggregate normalisation. AVG becomes SUM0/COUNT so the executor never sees it (A12);
        //    AGGREGATE_REMOVE deletes a GROUP BY or DISTINCT over a declared unique key, which is the
        //    second statistics-plumbing check (corpus query 25).
        CoreRules.AGGREGATE_REDUCE_FUNCTIONS,
        CoreRules.AGGREGATE_REMOVE,
        CoreRules.AGGREGATE_PROJECT_MERGE,

        // 3a. `SUM(CASE WHEN c THEN x ELSE NULL END)` is `SUM(x) FILTER (WHERE c)`, and the second
        //     is what the IR carries and the executor runs: the aggregate reads the column and the
        //     FILTER decides which rows are counted, so the argument stops being a per-row
        //     expression the executor has to evaluate for every row it then discards. Unambiguously
        //     smaller, which is why it belongs in this collection rather than in Volcano. A mask in
        //     the other arm is not this shape and is left alone — the rule wants a NULL (or, for a
        //     COUNT, a zero) there, and a sanitiser's placeholder is neither.
        CoreRules.AGGREGATE_CASE_TO_FILTER,

        // 4. Joins: a predicate that belongs to one side belongs on that side (D43). All of these
        //    are unambiguous improvements — they shrink what a join has to read — so they run here
        //    rather than costing alternatives in Volcano. JOIN_PUSH_TRANSITIVE_PREDICATES is the
        //    fifth of them and is not in this collection: see {@link #transitivePredicates} (F66).
        CoreRules.FILTER_INTO_JOIN,
        CoreRules.JOIN_CONDITION_PUSH,
        CoreRules.JOIN_PUSH_EXPRESSIONS,
        CoreRules.PROJECT_JOIN_TRANSPOSE,

        // 4a. F16(d): a join whose other side contributes no column and is joined on a key that side
        //     is unique in produces exactly its own rows, so the join is not a join. Both rules read
        //     `areColumnsUnique`, which since step 20 the catalog's declared unique keys answer;
        //     ADR 0019 verified that neither consults referential constraints in 1.42, so a foreign
        //     key alone will not fire them. Both are Calcite `SubstitutionRule`s — strictly smaller
        //     output — which is why they belong here rather than as costed alternatives.
        CoreRules.PROJECT_JOIN_REMOVE,
        CoreRules.AGGREGATE_JOIN_REMOVE,

        // 5. WHERE 1 = 0 becomes an empty Values rather than a scan nobody will read.
        PruneEmptyRules.FILTER_INSTANCE,
        PruneEmptyRules.PROJECT_INSTANCE,
        PruneEmptyRules.SORT_INSTANCE,
        PruneEmptyRules.AGGREGATE_INSTANCE,
        PruneEmptyRules.JOIN_LEFT_INSTANCE,
        PruneEmptyRules.JOIN_RIGHT_INSTANCE,

        // 5a. The same for a correlate, which is a join this list had no shape for when it was
        //     written: ADR 0019's milestone had no correlates at all (F70). An entitlement that
        //     folds a leaf to nothing is the way one is reached — the body of a correlated
        //     sub-query over a table this principal reaches no row of is empty, and without these
        //     an INNER correlate was left over an empty input for `CorrelateSupport` to refuse as
        //     undecorrelatable, rather than the no rows it plainly is. LEFT keeps its left side
        //     with nulls, which is what an outer join means.
        PruneEmptyRules.CORRELATE_RIGHT_INSTANCE,
        PruneEmptyRules.CORRELATE_LEFT_INSTANCE,

        // 5b. And the one those two have no shape for: a correlate whose right side was pruned
        //     down to something that still produces rows, so neither "empty" rule matches, but
        //     that no longer reads the left row at all. A count over a leaf an entitlement folded
        //     to nothing is how one is reached — COUNT over nothing is 0, not nothing — and a
        //     correlate that reads nothing of the left is a join on TRUE (F79).
        chalk.planner.plan.rules.UncorrelatedCorrelateRule.INSTANCE,

        // 6. A set operation over an empty branch is the other branches (D69).
        PruneEmptyRules.UNION_INSTANCE,
        PruneEmptyRules.INTERSECT_INSTANCE,
        PruneEmptyRules.MINUS_INSTANCE);

    if (distinct == DistinctStrategy.JOIN) {
      rules.add(CoreRules.AGGREGATE_EXPAND_DISTINCT_AGGREGATES_TO_JOIN);
    }

    // 7. Windows (D49). PROJECT_TO_LOGICAL_PROJECT_AND_WINDOW is what turns a RexOver in a project
    //    into a LogicalWindow, and it has to run after sub-query removal because a RexOver can sit
    //    inside one. The other two are unambiguous improvements over what it produces: constant
    //    folding inside the window, and pruning the columns the window does not read.
    rules.add(CoreRules.PROJECT_TO_LOGICAL_PROJECT_AND_WINDOW);
    rules.add(CoreRules.WINDOW_REDUCE_EXPRESSIONS);
    rules.add(CoreRules.PROJECT_WINDOW_TRANSPOSE);
    return rules.build();
  }

  /**
   * The fifth join rule of {@link #hep}, in a phase of its own after it (F66).
   *
   * <p>{@code JOIN_PUSH_TRANSITIVE_PREDICATES} reads what a join's two sides are already known to
   * satisfy, infers what that makes the <em>other</em> side satisfy, and wraps each side in a
   * {@code Filter} carrying it. {@code FILTER_REDUCE_EXPRESSIONS} deletes a {@code Filter} whose
   * condition it can prove from what the input already says. Those are inverses whenever the
   * inference is one the simplifier can prove, and in one Hep collection they undo each other for
   * ever — which is the disease {@link #joinOrdering} already carries the cure for, and which F66
   * is: an entitled column pinned to a constant by its own {@code Filter_R} reaches the join as a
   * <em>nullable</em> constant (D161 — the disclosed row shape is the catalog's, not this
   * principal's), so Calcite's metadata publishes it as {@code IS NOT DISTINCT FROM} while the
   * inference asks for {@code =}, the two never match, and the rule infers the same predicate again
   * every time the reduce rule removes it.
   *
   * <p>A {@code HepProgram}'s phases run to fixpoint in order, so the inference runs after the
   * collection has finished and nothing is left to undo it; what it infers still reaches the leaves,
   * because {@link #afterDecorrelation} pushes and merges filters and carries no reduce rule.
   */
  public static List<RelOptRule> transitivePredicates() {
    return ImmutableList.of(CoreRules.JOIN_PUSH_TRANSITIVE_PREDICATES);
  }

  /**
   * The Hep pass that runs after decorrelation, because what it matches only exists then: a LEFT
   * MARK join becomes the SEMI or ANTI join the sub-query meant, a join whose right side is a
   * distinct aggregate (or a unique key) becomes a semi join, and the tidying rules clean up after
   * both.
   */
  public static List<RelOptRule> afterDecorrelation() {
    return ImmutableList.of(
        CoreRules.MARK_TO_SEMI_OR_ANTI_JOIN_RULE,
        CoreRules.PROJECT_TO_SEMI_JOIN,
        CoreRules.JOIN_TO_SEMI_JOIN,
        CoreRules.JOIN_ON_UNIQUE_TO_SEMI_JOIN,
        // F16(d) again: decorrelation and the semi-join rewrites above both produce projections over
        // joins that did not exist before this pass.
        CoreRules.PROJECT_JOIN_REMOVE,
        CoreRules.AGGREGATE_JOIN_REMOVE,
        CoreRules.FILTER_INTO_JOIN,
        CoreRules.JOIN_CONDITION_PUSH,
        CoreRules.FILTER_MERGE,
        CoreRules.FILTER_PROJECT_TRANSPOSE,
        CoreRules.PROJECT_MERGE,
        CoreRules.PROJECT_REMOVE,
        // D106: prune a partitioned scan once the predicates have reached the leaves, and push the
        // columns the query actually reads into every partition.
        PartitionRules.PRUNE,
        PartitionRules.PROJECT);
  }

  /**
   * The Hep pass that orders more joins than Volcano should enumerate (D44): collapse the joins into
   * one {@code MultiJoin} and let {@code LoptOptimizeJoinRule} choose a left-deep order from the M2
   * statistics. Run only when {@link JoinOrdering#needsHeuristicOrdering} says so, because for a
   * small query Volcano's own enumeration is both exhaustive and cheap.
   */
  public static List<RelOptRule> joinOrdering() {
    return ImmutableList.of(
        CoreRules.JOIN_TO_MULTI_JOIN,
        CoreRules.PROJECT_MULTI_JOIN_MERGE,
        CoreRules.FILTER_MULTI_JOIN_MERGE,
        CoreRules.MULTI_JOIN_OPTIMIZE);
  }

  /**
   * The Volcano rule list: one converter per physical rel, the sort rule, and the handful of logical
   * rules that genuinely need the cost model to choose between alternatives.
   */
  public static List<RelOptRule> volcano(PushdownPolicy policy) {
    return volcano(policy, /* enumerateJoinOrders= */ true, ImmutableList.of());
  }

  /** The same, with one generic pushdown rule set per remote source in the catalog (D83). */
  public static List<RelOptRule> volcano(
      PushdownPolicy policy, List<SourceConvention> sources) {
    return volcano(policy, /* enumerateJoinOrders= */ true, sources);
  }

  /** The same, with the cross-source join strategies the policy allows (D103, M5). */
  public static List<RelOptRule> volcano(
      PushdownPolicy policy, List<SourceConvention> sources, JoinPolicy joinPolicy) {
    return volcano(policy, /* enumerateJoinOrders= */ true, sources, joinPolicy);
  }

  /**
   * The same list, with the join-order rules left out when the Hep pre-pass has already fixed an
   * order (D44). Leaving them in would make {@code LoptOptimizeJoinRule}'s work pointless and the
   * search factorial again — six tables never finished planning.
   */
  public static List<RelOptRule> volcano(PushdownPolicy policy, boolean enumerateJoinOrders) {
    return volcano(policy, enumerateJoinOrders, ImmutableList.of());
  }

  /**
   * The Volcano list for one request: the fixed rules, then {@link PushdownRules} instantiated once
   * per source convention the catalog carries (D83). The pushdown rules come last so that the
   * source-order dependence stays where it belongs — in the catalog, whose epoch already covers it
   * — rather than in {@code PlannerConfig.configHash}, which names rule <em>classes</em>.
   */
  public static List<RelOptRule> volcano(
      PushdownPolicy policy, boolean enumerateJoinOrders, List<SourceConvention> sources) {
    return volcano(policy, enumerateJoinOrders, sources, JoinPolicy.DEFAULT);
  }

  /**
   * The same, with the cross-source join strategies of D103 (M5). {@link CrossSourceJoinRule}
   * enumerates every strategy the policy allows for a pair, as alternatives cost chooses between;
   * {@code LOCAL} needs no rule of its own, because {@link ChalkHashJoinRule} already produces it.
   */
  public static List<RelOptRule> volcano(
      PushdownPolicy policy,
      boolean enumerateJoinOrders,
      List<SourceConvention> sources,
      JoinPolicy joinPolicy) {
    List<RelOptRule> rules = new ArrayList<>();
    rules.add(ChalkTableScanRule.of(policy));
    rules.add(new ChalkProjectScanRule(policy));
    rules.add(ChalkFilterRule.INSTANCE);
    rules.add(new ChalkIndexLookupRule(policy));
    // D50: an unbounded lookup on an ordered index is a scan in that index's key order.
    rules.add(new ChalkIndexOrderedScanRule(policy));
    rules.add(ChalkProjectRule.INSTANCE);
    rules.add(ChalkSortRule.INSTANCE);
    rules.add(ChalkHashAggregateRule.INSTANCE);
    rules.add(ChalkWindowRule.INSTANCE);
    // D55, D66: the two window table functions and every spelling of UNNEST.
    rules.add(ChalkWindowTableFunctionRule.INSTANCE);
    rules.add(ChalkUnnestRule.DIRECT);
    rules.add(ChalkUnnestRule.PROJECTED);
    rules.add(ChalkUnnestRule.ChalkUncollectRule.INSTANCE);
    rules.add(ChalkValuesRule.INSTANCE);
    // A scan of one of the execution context's relations (step 26). The rule declines every
    // other LogicalTableScan, so a catalog without a bound context is unaffected.
    rules.add(ChalkContextScanRule.INSTANCE);
    // D106: a partitioned table's union, converted branch by branch.
    rules.add(PartitionRules.CONVERT);
    // A client-bodied table function is a leaf the client evaluates (D78, step 22).
    rules.add(chalk.planner.plan.rules.ChalkTableFunctionScanRule.INSTANCE);

    // D69: one converter per set operation, plus the two rewrites that are unambiguous wins —
    // UNION_MERGE folds a nested UNION into one n-ary node, UNION_PULL_UP_CONSTANTS lifts a column
    // every branch pins to the same literal. MINUS_TO_ANTI_JOIN is costed against the operator
    // (V29); INTERSECT_TO_DISTINCT stays out, because the operator is cheaper than its rewrite.
    rules.add(ChalkSetOpRules.UNION);
    rules.add(ChalkSetOpRules.INTERSECT);
    rules.add(ChalkSetOpRules.MINUS);
    rules.add(CoreRules.UNION_MERGE);
    rules.add(CoreRules.UNION_PULL_UP_CONSTANTS);
    rules.add(CoreRules.MINUS_TO_ANTI_JOIN);
    rules.add(ChalkHashJoinRule.INSTANCE);
    rules.add(ChalkMergeJoinRule.INSTANCE);
    rules.add(ChalkNestedLoopJoinRule.INSTANCE);
    rules.add(ChalkAsOfJoinRule.INSTANCE);

    // Join order, for the queries small enough to enumerate (D44).
    if (enumerateJoinOrders) {
      rules.add(CoreRules.JOIN_COMMUTE);
      rules.add(CoreRules.JOIN_ASSOCIATE);
    }

    // CoreRules.SORT_REMOVE was here as belt and braces next to ChalkSortRule. Under top-down
    // (D47) the root's required collation arrives through passThrough/enforce, and removing the
    // rule changes no M1 or M2 plan — ChalkSortRule stays because it is also the only converter
    // from LogicalSort, and it is what produces ChalkTopN and ChalkLimit (ADR 0016).
    rules.add(CoreRules.SORT_PROJECT_TRANSPOSE);
    rules.add(CoreRules.PROJECT_MERGE);
    rules.add(CoreRules.FILTER_MERGE);

    // M4 (D83): one convention per remote source, one generic rule set each.
    for (SourceConvention source : sources) {
      rules.addAll(PushdownRules.forSource(source, policy));
    }

    // M5 (D103): the cross-source strategies, after the per-source rules so that a pushed subtree
    // is available to be looked into by the time a join over two of them is considered.
    RelOptRule crossSource =
        chalk.planner.plan.rules.CrossSourceJoinRule.create(sources, policy, joinPolicy);
    if (crossSource != null) {
      rules.add(crossSource);
    }

    // And the one shape that rule cannot read: a semi-join against a *context relation*, which
    // belongs to no source and has to drive rather than be looked up (16-entitlements.md §2, F45).
    RelOptRule contextKeySet = chalk.planner.plan.rules.ContextKeySetRule.create(sources, policy);
    if (contextKeySet != null) {
      rules.add(contextKeySet);
    }

    // And the other shape with a side that must drive rather than be looked up: the join a
    // `through` compiles into, where the parent is in another source than its child and it is the
    // parent's visible keys that travel (16-entitlements.md §3.13, D229, F52).
    RelOptRule parentKeySet =
        chalk.planner.plan.rules.ParentKeySetRule.create(sources, policy, joinPolicy);
    if (parentKeySet != null) {
      rules.add(parentKeySet);
    }
    return ImmutableList.copyOf(rules);
  }

  /** Stable names for {@code PlannerConfig.configHash()}. */
  public static List<String> names(List<RelOptRule> rules) {
    List<String> names = new ArrayList<>(rules.size());
    for (RelOptRule rule : rules) {
      names.add(rule.toString());
    }
    return ImmutableList.copyOf(names);
  }
}
