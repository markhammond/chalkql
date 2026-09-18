package chalk.planner.plan.rules;

import chalk.ir.v1.JoinStrategy;
import chalk.planner.entitlement.ContextTable;
import chalk.planner.plan.ChalkKeySet;
import chalk.planner.plan.PushdownGate;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SourceConvention;
import chalk.planner.plan.rel.ChalkLookupJoin;
import chalk.planner.plan.rel.ChalkProject;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.volcano.RelSubset;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.JoinInfo;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.logical.LogicalAggregate;
import org.apache.calcite.rel.logical.LogicalJoin;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.util.ImmutableBitSet;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A membership over a <b>bound list</b> reaches a remote source as M5's key set
 * (docs/design/16-entitlements.md §2 item 2 and §3.7 item 2; docs/design/20-m5-federation.md §1).
 *
 * <p>A context list above the fold ceiling is not made literal: the fold writes
 * {@code x IN (SELECT k FROM "$chalk$ctx"."list")}, which decorrelates into a semi-join against the
 * list. §2 says that semi-join is shipped to a remote source as a key set in calls sized by the
 * source's own ceiling — and for an entitled read it was not, so the whole table came back and was
 * filtered here (F45). This rule is what makes it so.
 *
 * <p><b>Why {@link CrossSourceJoinRule} does not reach it.</b> That rule asks each side of a join
 * which single source its tables belong to; a context relation is a {@code ContextTable} the
 * executor materialises from the host's binding and belongs to none, so the question has no answer
 * and the rule declines. And it could not have helped if it had one: its lookup shape drives from
 * the <em>left</em> input, and driving from the entitled table means fetching the entitled table.
 * What is wanted is the other direction — the list's keys travel and the table's rows do not — and a
 * semi-join is not commutative, so it needs a rule of its own.
 *
 * <p><b>The rewrite.</b> {@code T ⋉ L} on {@code T.k = L.k} is exactly
 * {@code π_T(distinct(L) ⋈ T)}: a row of {@code T} matches at most one <em>distinct</em> key, so an
 * inner join against the deduplicated list produces each matching row of {@code T} once and no
 * other. So the list becomes the driving side of a {@link ChalkLookupJoin} — deduplicated by an
 * aggregate, which is what makes the two forms equal — the entitled scan becomes the lookup side
 * with {@code k IN (?)} as the last conjunct of its predicate, and a projection puts the driving
 * keys back out of the row.
 *
 * <p><b>A key of several columns (F50).</b> The equality is what the fold wrote, and the tenancy
 * package writes subject grants as pairs: {@code (member_id, org_id) IN (@ctx.subject_pairs)}
 * decorrelates into a semi-join on <em>two</em> keys. The identity above holds for a key of any
 * width as long as "distinct" is taken over the whole key, so the aggregate groups on every key
 * column and the pushed predicate becomes the row-constructor form
 * {@code (member_id, org_id) IN ((?, ?), …)}, batched by {@code max_in_list} as key <em>rows</em>.
 * The source has to say it parses that — {@code supports_row_value_in_list}, which the DuckDB and
 * PostgreSQL profiles declare and SQLite's does not — and where it does not, the hash join still
 * applies and {@code row_predicate_pushed} is honestly false.
 *
 * <p>What that buys is the whole of §3.7: the source receives the membership as key-set calls,
 * {@code row_predicate_pushed} is honestly true for the read, and the rows fetched never exceed the
 * principal's scope. The list's rows are still not in the plan — only its name and row type are,
 * which is the point of the fold ceiling.
 */
public final class ContextKeySetRule extends org.apache.calcite.plan.RelRule<ChalkRuleConfig> {
  private final List<SourceConvention> sources;
  private final PushdownPolicy pushdown;

  private ContextKeySetRule(
      ChalkRuleConfig config, List<SourceConvention> sources, PushdownPolicy pushdown) {
    super(config);
    this.sources = ImmutableList.copyOf(sources);
    this.pushdown = pushdown;
  }

  /** The rule for this catalog's sources, or none when nothing could be pushed anyway. */
  public static @Nullable ContextKeySetRule create(
      List<SourceConvention> sources, PushdownPolicy pushdown) {
    if (sources.isEmpty() || !pushdown.allowsFullRemotePushdown()) {
      return null;
    }
    return new ContextKeySetRule(
        ChalkRuleConfig.of(
            "ContextKeySetRule",
            b ->
                b.operand(LogicalJoin.class)
                    .trait(org.apache.calcite.plan.Convention.NONE)
                    .anyInputs(),
            config -> new ContextKeySetRule(config, sources, pushdown)),
        sources,
        pushdown);
  }

  @Override
  public void onMatch(RelOptRuleCall call) {
    LogicalJoin join = call.rel(0);
    if (join.getJoinType() != JoinRelType.SEMI || !join.getVariablesSet().isEmpty()) {
      return;
    }
    // V50's reason, unchanged: a null-safe equality reads back as a plain key and a lookup call
    // binds plain values.
    if (ChalkInputs.hasNullSafeEquality(join)) {
      return;
    }

    JoinInfo info = join.analyzeCondition();
    if (info.leftKeys.isEmpty() || !info.isEqui()) {
      // Nothing to drive with, or a residual a lookup call cannot carry.
      return;
    }

    for (int listKey : info.rightKeys) {
      if (!readsContextRelation(join.getRight(), listKey)) {
        return;
      }
    }

    String sourceId = CrossSourceJoinRule.singleSourceOf(join.getLeft());
    SourceConvention convention = sourceId == null ? null : conventionOf(sourceId);
    if (convention == null) {
      return; // a local table has no source to ship a key set to, and LOCAL is already correct
    }

    // D156's barrier is a gate rule and it applies here too: under LOCAL enforcement the source
    // receives a projected scan and no predicate at all, so the tenant set never appears in remote
    // query text — and a key set is exactly the tenant set.
    chalk.planner.plan.MaskLocality.Redacted withheld =
        chalk.planner.plan.MaskLocality.of(join.getLeft());
    if (withheld != null && withheld.isLocalOnly()) {
      return;
    }

    PushdownGate gate = pushdown.gateFor(convention);
    int maxInList = gate.maxInList();
    if (maxInList <= 0) {
      return; // the descriptor says no IN list is ever pushed
    }
    if (info.leftKeys.size() > 1 && !gate.supportsRowValueInList()) {
      // A composite key set is `(a, b) IN ((?, ?), …)`, and a source that does not declare that
      // spelling would answer a syntax error. The hash join still applies, which is what a list of
      // tuples gets there — `row_predicate_pushed` honestly false (F50).
      return;
    }

    // The subtree the key set goes into: a scan of one of the source's tables under a filter and a
    // plain-column projection. `Filter_R` sits directly on the scan and `Project_D` above it, so a
    // masked column's CASE is above this join rather than inside its left input — which is why an
    // entitled leaf is a shape this can read at all. And the key set is not a mask: it is compared
    // against the raw tenancy column, below every disclosure, which is where the row predicate is
    // defined (§3.1).
    CrossSourceJoinRule.LookupSide side =
        CrossSourceJoinRule.LookupSide.of(join.getLeft(), convention, gate);
    if (side == null) {
      return;
    }

    RexBuilder rex = join.getCluster().getRexBuilder();
    RelNode lookup = side.build(info.leftKeys, ChalkKeySet.KEY_SET_IN, maxInList, rex);
    if (lookup == null) {
      return;
    }

    RelNode driving = distinctKeys(join.getRight(), info.rightKeys, rex);
    RelNode joined =
        ChalkLookupJoin.create(
            ChalkInputs.unordered(driving),
            lookup,
            keyEquality(driving, lookup, info.leftKeys, side, rex),
            join.getVariablesSet(),
            JoinRelType.INNER,
            maxInList,
            JoinStrategy.JOIN_STRATEGY_LOOKUP);

    call.transformTo(
        withoutDrivingKeys(
            joined, join, side, driving.getRowType().getFieldCount(), rex));
  }

  /**
   * The list's key column, deduplicated: a projection of the one column the membership reads and an
   * aggregate over it with no aggregate calls.
   *
   * <p>The aggregate is not tidiness. A lookup join emits one row per driving row that matched, so a
   * list holding a key twice would return the matching rows of the entitled table twice — where the
   * semi-join it replaces returns them once. Nothing refuses a host from binding a list with a
   * repeat, and this is what makes the two shapes equal whether or not it did.
   */
  private static RelNode distinctKeys(RelNode list, List<Integer> keys, RexBuilder rex) {
    List<RexNode> projects = new ArrayList<>(keys.size());
    List<String> names = new ArrayList<>(keys.size());
    for (int key : keys) {
      RelDataTypeField field = list.getRowType().getFieldList().get(key);
      projects.add(rex.makeInputRef(field.getType(), key));
      names.add(field.getName());
    }

    RelNode projected =
        LogicalProject.create(list, List.of(), projects, names, java.util.Set.of());
    return LogicalAggregate.create(
        projected, List.of(), ImmutableBitSet.range(keys.size()), null, List.of());
  }

  /**
   * {@code drivingKey = lookupKey} for every key column, conjoined, over the joined row the lookup
   * join produces. The driving side's columns are the key columns in {@code keys}' own order, so
   * position {@code i} of a bound row is column {@code i} of the driving side and key column
   * {@code i} of the lookup side.
   */
  private static RexNode keyEquality(
      RelNode driving,
      RelNode lookup,
      List<Integer> lookupKeys,
      CrossSourceJoinRule.LookupSide side,
      RexBuilder rex) {
    int width = driving.getRowType().getFieldCount();
    List<RexNode> equalities = new ArrayList<>(lookupKeys.size());
    for (int i = 0; i < lookupKeys.size(); i++) {
      // Where F54 pruned the lookup row — the entitled leaf's projection makes something of its own
      // columns and stays in process — the key sits where the pruning put it.
      int at = side.drivenKey(lookupKeys.get(i));
      RelDataTypeField left = driving.getRowType().getFieldList().get(i);
      RelDataTypeField right = lookup.getRowType().getFieldList().get(at);
      equalities.add(
          rex.makeCall(
              SqlStdOperatorTable.EQUALS,
              rex.makeInputRef(left.getType(), i),
              rex.makeInputRef(right.getType(), width + at)));
    }
    return org.apache.calcite.rex.RexUtil.composeConjunction(rex, equalities);
  }

  /**
   * The lookup side's own columns, which is what the semi-join produced: the driving keys are the
   * leading columns of the joined row and are dropped here, so the rule's output row type is the
   * one the tree above already expects.
   */
  private static RelNode withoutDrivingKeys(
      RelNode joined,
      LogicalJoin join,
      CrossSourceJoinRule.LookupSide side,
      int drivingWidth,
      RexBuilder rex) {
    int width = join.getRowType().getFieldCount();
    // F54: what the lookup side could not send to the source is restated here, above the join, over
    // the pruned row it did send for.
    List<RexNode> restore = side.restore(rex);
    List<RexNode> projects = new ArrayList<>(width);
    List<RelDataTypeField> fields = joined.getRowType().getFieldList();
    for (int i = 0; i < width; i++) {
      projects.add(
          restore == null
              ? rex.makeInputRef(fields.get(drivingWidth + i).getType(), drivingWidth + i)
              : shift(restore.get(i), drivingWidth));
    }
    return ChalkProject.create(joined, projects, join.getRowType());
  }

  /** An expression over the lookup side's own row, restated over the driven row it sits in. */
  private static RexNode shift(RexNode node, int by) {
    return node.accept(
        new org.apache.calcite.rex.RexShuttle() {
          @Override
          public RexNode visitInputRef(org.apache.calcite.rex.RexInputRef ref) {
            return new org.apache.calcite.rex.RexInputRef(ref.getIndex() + by, ref.getType());
          }
        });
  }

  /**
   * Whether this input is a scan of a bound <b>list</b>, and the key reads one of its own columns.
   *
   * <p>A projection over it is allowed as long as the key traces back to a column: the Hep pre-pass
   * routinely prunes a context relation's row down to the one column the membership reads.
   *
   * <p>A list and not any context relation, because a list is {@code NOT NULL} in every column by
   * construction (§2, refused at bind) and a key set is a set of values a source is handed. A
   * relation the host bound as a table may hold a NULL, which a semi-join ignores and a bound
   * placeholder would have to carry; §2 item 2's promise is about a list, and this is that.
   */
  private static boolean readsContextRelation(RelNode rel, int key) {
    RelNode node = unwrap(rel);
    int column = key;
    for (int round = 0; round < 4; round++) {
      if (node instanceof LogicalProject project) {
        if (!(project.getProjects().get(column) instanceof RexInputRef ref)) {
          return false;
        }
        column = ref.getIndex();
        node = unwrap(project.getInput());
        continue;
      }
      break;
    }
    if (!(node instanceof TableScan scan)) {
      return false;
    }
    ContextTable context = scan.getTable().unwrap(ContextTable.class);
    return context != null && context.relation().isList();
  }

  private static RelNode unwrap(RelNode rel) {
    if (rel instanceof RelSubset subset) {
      RelNode best = subset.getBest();
      return best != null ? best : subset.getOriginal();
    }
    return rel;
  }

  private @Nullable SourceConvention conventionOf(String sourceId) {
    for (SourceConvention convention : sources) {
      if (convention.sourceId().equals(sourceId)) {
        return convention;
      }
    }
    return null;
  }
}
