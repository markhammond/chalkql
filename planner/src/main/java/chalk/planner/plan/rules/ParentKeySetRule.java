package chalk.planner.plan.rules;

import chalk.ir.v1.JoinStrategy;
import chalk.ir.v1.ParentVisibility;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.plan.ChalkKeySet;
import chalk.planner.plan.JoinPolicy;
import chalk.planner.plan.PushdownGate;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SourceConvention;
import chalk.planner.plan.rel.ChalkLookupJoin;
import chalk.planner.plan.rel.ChalkProject;
import chalk.planner.plan.rel.ProjectedRelOptTable;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.JoinInfo;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.logical.LogicalJoin;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.util.ImmutableBitSet;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A {@code through} parent ships its <b>visible keys</b> to the child's source as a key set
 * (docs/design/16-entitlements.md §3.13, D229; F52, F55).
 *
 * <p>Where a child's visibility derives through a parent in the same source, the join, the parent's
 * folded predicate and the key condition go to that source as one remote query. Anywhere else they
 * cannot, and what was left was the fallback: the child fetched whole and joined here, with
 * {@code row_predicate_pushed} honestly false. D229 asks instead for M5's lookup with the
 * <em>parent</em> driving — the parent's own entitled scan, at the parent's cardinality, is the
 * small side, and its visible keys travel to the child's source in calls of at most
 * {@code max_in_list}.
 *
 * <p><b>"Anywhere else" is the condition, not "in another source" (F55).</b> The first cut asked
 * that both sides belong to exactly one source each and that the two differ. The first half of that
 * is the point; the second excluded a parent side belonging to <em>no</em> source, which is what a
 * context with an open half makes of every parent: the memberships are then joins to context tables,
 * and a context table belongs to nobody. What travels is the parent's visible keys, and how they
 * were computed is the optimiser's business rather than this rule's — so what is asked is that the
 * child is in a source that takes an {@code IN} list and that the parent side is not that same
 * source's.
 *
 * <p><b>Why a rule of its own, and not a swap in {@link CrossSourceJoinRule}.</b> That rule reaches
 * a cross-source join by offering both orientations and letting cost choose, but an orientation is
 * something only an {@code INNER} join has: a semi-join does not commute, and a {@code through} whose
 * child needs no verdict column is exactly a semi-join by the time the optimiser sees it — the
 * marker the pass projects is a constant and the join collapses. So the rewrite is F45's rather than
 * a commutation, and for the same reason F45 needed one: {@code T ⋉ P} on {@code T.k = P.pk} is
 * {@code π_T(P ⋈ T)}, and the driving side is the one whose keys travel. Here the parent's entitled
 * scan stands where F45's deduplicated context list stood.
 *
 * <p><b>No aggregate over the driving side.</b> F45 deduplicates its list because nothing refuses a
 * host from binding a repeated key. A {@code through} parent's key is a <em>declared unique key</em>
 * — registration refuses a parent without one, because the join must not multiply rows (D226) — so
 * the rule asks the metadata whether the driving key is unique and declines where it cannot be told,
 * which leaves the local join and an honest {@code row_predicate_pushed = false}. Where the metadata
 * cannot be told <em>and says so as "false"</em>, which is what it does over a subset holding a join,
 * {@link #keyStaysUnique} answers by the same kind of walk {@link #isOrigin} makes.
 *
 * <p>What it declines, each falling back to the local join rather than failing: a {@code LEFT} join,
 * which is what more than one parent or a child with a restriction of its own compiles to and which
 * cannot be driven from its right side; a child whose leaf is not a scan the key set can be put into;
 * a child source that takes no {@code IN} list, or a composite key without
 * {@code supports_row_value_in_list}; a parent whose visible keys could exceed
 * {@code max_in_list × lookup_max_calls}; a child under {@code LOCAL} enforcement, where D156
 * keeps the tenant set — which a key set is — out of the source's query text; and a child whose
 * source a pair rule of the join policy forbids looking up into (F139).
 *
 * <p><b>The join policy binds it (F139).</b> This rule used to read the policy for
 * {@code lookup_max_calls} alone and never ask {@code allowed(left, right)}, so a host that had
 * forbidden every look-up into a source could still have the entitlement pass send member keys
 * there. The exchange is a {@code LOOKUP} by every other measure and the plan text shows it as one,
 * so the pair rule binds it: every source the parent's visible keys are computed from is asked —
 * the parent side need not be one source's (F55), and a context relation among its inputs is asked
 * about as the empty id — and any one that may not look up into the child's source keeps the join
 * here.
 */
public final class ParentKeySetRule extends org.apache.calcite.plan.RelRule<ChalkRuleConfig> {

  private final List<SourceConvention> sources;
  private final PushdownPolicy pushdown;
  private final JoinPolicy policy;

  private ParentKeySetRule(
      ChalkRuleConfig config,
      List<SourceConvention> sources,
      PushdownPolicy pushdown,
      JoinPolicy policy) {
    super(config);
    this.sources = ImmutableList.copyOf(sources);
    this.pushdown = pushdown;
    this.policy = policy;
  }

  /** The rule for this catalog's sources, or none when nothing could be pushed anyway. */
  public static @Nullable ParentKeySetRule create(
      List<SourceConvention> sources, PushdownPolicy pushdown, JoinPolicy policy) {
    if (sources.isEmpty() || !pushdown.allowsFullRemotePushdown()) {
      return null;
    }
    return new ParentKeySetRule(
        ChalkRuleConfig.of(
            "ParentKeySetRule",
            b ->
                b.operand(LogicalJoin.class)
                    .trait(org.apache.calcite.plan.Convention.NONE)
                    .anyInputs(),
            config -> new ParentKeySetRule(config, sources, pushdown, policy)),
        sources,
        pushdown,
        policy);
  }

  @Override
  public void onMatch(RelOptRuleCall call) {
    LogicalJoin join = call.rel(0);
    if (join.getJoinType() != JoinRelType.SEMI && join.getJoinType() != JoinRelType.INNER) {
      return;
    }
    if (!join.getVariablesSet().isEmpty()) {
      return;
    }
    // V50's reason, unchanged: a null-safe equality reads back as a plain key and a lookup call
    // binds plain values.
    if (ChalkInputs.hasNullSafeEquality(join)) {
      return;
    }

    JoinInfo info = join.analyzeCondition();
    // A `through` correlates on one column, so one key is the whole of the shape this reads; a
    // residual over the joined row would have to be evaluated after the call, which a lookup join
    // does not carry.
    if (info.leftKeys.size() != 1 || !info.isEqui()) {
      return;
    }

    String childSource = CrossSourceJoinRule.singleSourceOf(join.getLeft());
    String parentSource = CrossSourceJoinRule.singleSourceOf(join.getRight());
    // One source on both sides is D229's first half, and the pushdown rules already make it one
    // remote query. A parent side that belongs to *no* single source — which is what a shape makes
    // of it, the memberships being joins to context tables — is the F55 case and is exactly what
    // this rule is for: whatever computes the parent's visible keys, they are what travels.
    if (childSource == null || childSource.equals(parentSource)) {
      return;
    }
    SourceConvention convention = conventionOf(childSource);
    if (convention == null) {
      return; // the child is in process: there is no source to ship a key set to
    }

    // D156's barrier, as `ContextKeySetRule` applies it: under LOCAL enforcement the source receives
    // a projected scan and no predicate at all, and a key set *is* the tenant set.
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

    CrossSourceJoinRule.LookupSide side =
        CrossSourceJoinRule.LookupSide.of(join.getLeft(), convention, gate);
    if (side == null) {
      return;
    }
    RelNode parent = resolve(join.getRight());
    if (!isThroughJoin(side, parent, info)) {
      return;
    }

    // The parent's key is what the driving side is deduplicated by, and a `through` parent's is a
    // declared unique key (D226). Asking the metadata rather than the descriptor is what keeps the
    // rewrite exact for the shape actually in hand: a projection of the parent's leaf that lost the
    // key's uniqueness is one this declines. Where the metadata cannot be told — which is what it
    // amounts to under a shape, see {@link #keyStaysUnique} — the same walk that traced the key
    // answers instead.
    RelMetadataQuery mq = call.getMetadataQuery();
    Boolean unique = mq.areColumnsUnique(parent, ImmutableBitSet.of(info.rightKeys));
    if (!Boolean.TRUE.equals(unique)
        && !keyStaysUnique(mq, parent, info.rightKeys.get(0), 0)) {
      return;
    }

    // The ceiling the adaptive join applies at execution, applied here to an estimate: past it the
    // exchange is more round trips than a fetch, and the local join is the honest answer.
    Double parentRows = mq.getRowCount(parent);
    long ceiling = (long) maxInList * policy.lookupMaxCalls();
    if (parentRows == null || parentRows > ceiling) {
      return;
    }

    // F139: a pair rule that forbids looking up into the child's source forbids this exchange too.
    if (!lookupAllowed(join.getRight(), childSource)) {
      return;
    }

    RexBuilder rex = join.getCluster().getRexBuilder();
    RelNode lookup = side.build(info.leftKeys, ChalkKeySet.KEY_SET_IN, maxInList, rex);
    if (lookup == null) {
      return;
    }

    RelNode joined =
        ChalkLookupJoin.create(
            ChalkInputs.unordered(join.getRight()),
            lookup,
            driven(join, side, info, rex),
            join.getVariablesSet(),
            JoinRelType.INNER,
            maxInList,
            JoinStrategy.JOIN_STRATEGY_LOOKUP);

    call.transformTo(asWritten(joined, join, side, rex));
  }

  /**
   * Whether the join policy lets the parent side's keys be looked up in the child's source (F139):
   * asked of every source the side's rows are computed from, the child's own excepted — keys that
   * never left that source cross no pair — and with a context relation among them as the empty id,
   * which is how a pair rule names "anything". A side that reads no table at all is asked about as
   * the empty id too, so a rule forbidding every look-up into the child's source still binds it.
   */
  private boolean lookupAllowed(RelNode parentSide, String childSource) {
    java.util.Set<String> driving = CrossSourceJoinRule.sourcesOf(parentSide);
    if (driving.isEmpty()) {
      return policy.allowsLookup("", childSource);
    }
    for (String source : driving) {
      if (!source.equals(childSource) && !policy.allowsLookup(source, childSource)) {
        return false;
      }
    }
    return true;
  }

  /**
   * Whether this join is the one a {@code through} or a declared path compiled into: the child's own
   * leaf on the left, the correlation column as its key, and on the right either the declared parent
   * with its declared key or the path's key set, grouped by the key the chain joins back on.
   *
   * <p>The shape is the pass's and nothing else reaches it. A statement that joins the same two
   * tables by hand has the child's own {@code through} join <em>under</em> its left input, which is
   * not a scan a key set can be put into; and a child with no {@code through} and no path has no
   * entry to match. So this is a narrow reading on purpose: the rewrite is sound for any semi-join
   * whose right side is unique on the key, and offering it for every one of those is a cost question
   * M5 has not opened.
   *
   * <p><b>The path's key set</b> (F84, docs/design/38-existential-visibility.md §4, §5). A declared
   * path compiles into the same join one column further out: the target's own key — or its foreign
   * key, for an {@code Inherited} path — against {@code K.$chalk$key}, where {@code K} is
   * {@code DISTINCT} on the column the first step arrives at. That is the second of §5's two hops,
   * and this is the rule that costs it: what travels is the key set's keys, whatever computed them,
   * which is the F55 reading of "anywhere else" applied to a chain rather than to a parent's leaf.
   * The uniqueness the rewrite needs is asked of the metadata below exactly as it is for a parent.
   */
  private static boolean isThroughJoin(
      CrossSourceJoinRule.LookupSide side, RelNode parent, JoinInfo info) {
    org.apache.calcite.plan.RelOptTable scanned = side.scanTable();
    ChalkTable child = scanned.unwrap(ChalkTable.class);
    if (child == null) {
      return false;
    }
    int childColumn = tableColumn(side.scanColumn(info.leftKeys.get(0)), scanned);
    if (childColumn < 0) {
      return false;
    }

    for (ParentVisibility through : child.descriptor().getEntitlement().getThroughList()) {
      if (through.getColumn() != childColumn) {
        continue;
      }
      String schema =
          through.getParentSchema().isEmpty() ? child.schemaName() : through.getParentSchema();
      if (isOrigin(
          parent, info.rightKeys.get(0), schema, through.getParentTable(),
          through.getParentColumn())) {
        return true;
      }
    }

    for (chalk.ir.v1.InheritedVisibility declared :
        child.descriptor().getEntitlement().getInheritedList()) {
      chalk.ir.v1.VisibilityStep first = declared.getSteps(0);
      if (first.getFromColumn() != childColumn) {
        continue;
      }
      String schema = first.getSchema().isEmpty() ? child.schemaName() : first.getSchema();
      if (isOrigin(parent, info.rightKeys.get(0), schema, first.getTable(), first.getToColumn())) {
        return true;
      }
    }
    return false;
  }

  /**
   * Whether this column of {@code rel} reads the named parent's declared key.
   *
   * <p>Walked here rather than asked of {@code RelMetadataQuery}: a rule sees its inputs as
   * {@code RelSubset}s, and Calcite's column-origin handler answers "no origin" for one rather than
   * "unknown", so every origin under an optimiser's input would read as derived. The walk follows
   * exactly the nodes the pass put on a parent side — a projection of plain references over the
   * parent's own entitled leaf, and a join where that parent has a {@code through} of its own — and
   * declines anything else.
   */
  private static boolean isOrigin(
      RelNode rel, int column, String schema, String table, int parentColumn) {
    RelNode node = resolve(rel);
    int at = column;
    for (int round = 0; round < 8; round++) {
      if (node instanceof org.apache.calcite.rel.core.Project project) {
        if (!(project.getProjects().get(at) instanceof RexInputRef ref)) {
          return false;
        }
        at = ref.getIndex();
        node = resolve(project.getInput());
        continue;
      }
      if (node instanceof org.apache.calcite.rel.core.Filter
          || node instanceof org.apache.calcite.rel.core.Sort) {
        node = resolve(((org.apache.calcite.rel.SingleRel) node).getInput());
        continue;
      }
      // A path's key set is `DISTINCT` on the column it joins back on (F84, D265 §4), so a grouping
      // key reads its input's column and the walk follows it exactly as it follows a projection of
      // a plain reference. Only a grouping key: an aggregate call is derived.
      if (node instanceof org.apache.calcite.rel.core.Aggregate aggregate) {
        if (at >= aggregate.getGroupCount()) {
          return false;
        }
        at = aggregate.getGroupSet().nth(at);
        node = resolve(aggregate.getInput());
        continue;
      }
      if (node instanceof org.apache.calcite.rel.core.Join join) {
        int left = join.getLeft().getRowType().getFieldCount();
        node = resolve(at < left ? join.getLeft() : join.getRight());
        at = at < left ? at : at - left;
        continue;
      }
      break;
    }
    if (!(node instanceof org.apache.calcite.rel.core.TableScan scan)) {
      return false;
    }
    ChalkTable parent = scan.getTable().unwrap(ChalkTable.class);
    return parent != null
        && parent.schemaName().equalsIgnoreCase(schema)
        && parent.tableName().equalsIgnoreCase(table)
        && tableColumn(at, scan.getTable()) == parentColumn;
  }

  /**
   * Whether this column of {@code rel} is still one row's worth — walked, for V169's reason one
   * handler along.
   *
   * <p>{@code RelMetadataQuery.areColumnsUnique} answers <b>false</b> rather than null for a
   * {@code RelSubset} holding a join: Calcite's subset handler enumerates {@code Aggregate},
   * {@code Filter}, {@code Values}, {@code Sort}, {@code TableScan} and {@code Project}, and a
   * subset whose rels are none of those falls out of the loop at "not unique". Under a prepare-time
   * binding a parent side is {@code Project(Filter(Scan))} and the answer is the declared key's; a
   * context with an open half makes it {@code Project(Filter(Join(Join(Scan, markers))))}, and the
   * filter's own input is then a subset holding a join alone — so the answer is {@code false} for
   * every parent, however unique its key is (F55).
   *
   * <p>The walk is therefore the same one {@link #isOrigin} makes, asking of each node what would
   * make it lose a key's uniqueness: a projection has to carry the key as a plain reference, and a
   * join has to have the key on a side the other side cannot multiply — which for a membership
   * marker is a list already made distinct, and for a chained {@code through} is the grandparent's
   * own declared key. A side the join makes NULLs on is declined, because two unmatched rows carry
   * the same NULL. The metadata has the last word at the leaf, where there is no subset to confuse
   * it.
   */
  private static boolean keyStaysUnique(RelMetadataQuery mq, RelNode rel, int column, int depth) {
    if (depth > 8) {
      return false;
    }
    RelNode node = resolve(rel);
    int at = column;
    for (int round = 0; round < 8; round++) {
      if (node instanceof org.apache.calcite.rel.core.Project project) {
        if (!(project.getProjects().get(at) instanceof RexInputRef ref)) {
          return false;
        }
        at = ref.getIndex();
        node = resolve(project.getInput());
        continue;
      }
      if (node instanceof org.apache.calcite.rel.core.Filter
          || node instanceof org.apache.calcite.rel.core.Sort) {
        node = resolve(((org.apache.calcite.rel.SingleRel) node).getInput());
        continue;
      }
      if (node instanceof org.apache.calcite.rel.core.Join join) {
        JoinInfo joined = join.analyzeCondition();
        if (!joined.isEqui() || joined.leftKeys.isEmpty()) {
          return false;
        }
        int width = join.getLeft().getRowType().getFieldCount();
        boolean fromLeft = at < width;
        JoinRelType type = join.getJoinType();
        if (fromLeft ? type.generatesNullsOnLeft() : type.generatesNullsOnRight()) {
          return false;
        }
        RelNode other = resolve(fromLeft ? join.getRight() : join.getLeft());
        List<Integer> otherKeys = fromLeft ? joined.rightKeys : joined.leftKeys;
        // One key is the walk's own question again — the other side may be a leaf the metadata
        // cannot be asked about over a subset. Several is a membership over a composite list, whose
        // deduplication the metadata answers for directly.
        boolean unique =
            otherKeys.size() == 1
                ? keyStaysUnique(mq, other, otherKeys.get(0), depth + 1)
                : Boolean.TRUE.equals(
                    mq.areColumnsUnique(other, ImmutableBitSet.of(otherKeys)));
        if (!unique) {
          return false;
        }
        node = resolve(fromLeft ? join.getLeft() : join.getRight());
        at = fromLeft ? at : at - width;
        continue;
      }
      break;
    }
    return Boolean.TRUE.equals(mq.areColumnsUnique(node, ImmutableBitSet.of(at)));
  }

  /** A scanned column as the table's descriptor numbers it, through a column-pruning wrapper. */
  private static int tableColumn(int column, org.apache.calcite.plan.RelOptTable table) {
    ProjectedRelOptTable projected = table.unwrap(ProjectedRelOptTable.class);
    if (projected == null || column < 0 || column >= projected.projection().size()) {
      return column;
    }
    return projected.projection().get(column);
  }

  /**
   * The join's condition restated over the driven row: the parent's columns first, then the
   * child's. The lookup join reads its keys from this, so it has to say the same equality the
   * written join did, the other way round.
   */
  private static RexNode driven(
      LogicalJoin join, CrossSourceJoinRule.LookupSide side, JoinInfo info, RexBuilder rex) {
    int childWidth = join.getLeft().getRowType().getFieldCount();
    int parentWidth = join.getRight().getRowType().getFieldCount();
    return join.getCondition()
        .accept(
            new RexShuttle() {
              @Override
              public RexNode visitInputRef(RexInputRef ref) {
                int index = ref.getIndex();
                // The condition is one equality and nothing else — a residual could not be
                // evaluated after the call — so every reference into the child is the key, and
                // where F54 pruned the lookup row the key sits where the pruning put it.
                int moved =
                    index < childWidth
                        ? side.drivenKey(index) + parentWidth
                        : index - childWidth;
                return new RexInputRef(moved, ref.getType());
              }
            });
  }

  /**
   * The driven join's row put back the way the tree above expects it: the child's columns, and for
   * an {@code INNER} join the parent's after them. A semi-join produced the child's alone, so the
   * driving side's columns are dropped — which is what {@code ContextKeySetRule} does with the
   * list's.
   */
  private static RelNode asWritten(
      RelNode joined, LogicalJoin join, CrossSourceJoinRule.LookupSide side, RexBuilder rex) {
    int childWidth = join.getLeft().getRowType().getFieldCount();
    int parentWidth = join.getRight().getRowType().getFieldCount();
    int width = join.getRowType().getFieldCount();
    List<RelDataTypeField> fields = joined.getRowType().getFieldList();
    // F54: where the child's projection could not go to the source, this is where it goes instead —
    // above the join, over the pruned row the source returned, which is both what §3.8 asks of a
    // mask and what a lookup join's own contract requires of its lookup side.
    List<RexNode> restore = side.restore(rex);

    List<RexNode> projects = new ArrayList<>(width);
    for (int i = 0; i < childWidth; i++) {
      projects.add(
          restore == null
              ? rex.makeInputRef(fields.get(parentWidth + i).getType(), parentWidth + i)
              : shift(restore.get(i), parentWidth));
    }
    if (width > childWidth) {
      for (int i = 0; i < parentWidth; i++) {
        projects.add(rex.makeInputRef(fields.get(i).getType(), i));
      }
    }
    return ChalkProject.create(joined, projects, join.getRowType());
  }

  /** An expression over the lookup side's own row, restated over the driven row it sits in. */
  private static RexNode shift(RexNode node, int by) {
    return node.accept(
        new RexShuttle() {
          @Override
          public RexNode visitInputRef(RexInputRef ref) {
            return new RexInputRef(ref.getIndex() + by, ref.getType());
          }
        });
  }

  /**
   * A subset resolved to a rel the metadata handlers can answer about: column origins over a
   * {@code RelSubset} come back empty rather than unknown, and an empty answer is not the same as
   * "no origin".
   */
  private static RelNode resolve(RelNode rel) {
    if (rel instanceof org.apache.calcite.plan.volcano.RelSubset subset) {
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
