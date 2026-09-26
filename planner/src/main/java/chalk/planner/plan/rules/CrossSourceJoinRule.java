package chalk.planner.plan.rules;

import chalk.ir.v1.JoinStrategy;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.ChalkKeySet;
import chalk.planner.plan.JoinPolicy;
import chalk.planner.plan.PushdownGate;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.RowCountProvenance;
import chalk.planner.plan.SourceConvention;
import chalk.planner.plan.rel.ChalkAdaptiveJoin;
import chalk.planner.plan.rel.ChalkLookupJoin;
import chalk.planner.plan.rel.ChalkProject;
import chalk.planner.plan.rel.SourceRels;
import chalk.planner.plan.rel.SourceScan;
import chalk.planner.plan.rel.SourceToLocalConverter;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.plan.volcano.RelSubset;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.JoinInfo;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.logical.LogicalFilter;
import org.apache.calcite.rel.logical.LogicalJoin;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rel.logical.LogicalTableScan;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.sql.SqlOperator;
import org.apache.calcite.util.ImmutableBitSet;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The strategies a join across two sources has (D103, {@code 20-m5-federation.md} §1), enumerated
 * from the policy and left to cost.
 *
 * <p>{@code LOCAL} needs no rule: {@code ChalkHashJoinRule} already produces a hash join over two
 * boundaries, and it is the fallback that always exists. What this rule adds are the three that ask
 * one source about the other's keys:
 *
 * <ul>
 *   <li><b>LOOKUP</b> — a {@link ChalkLookupJoin} whose right input is the lookup source's query
 *       with a key set in its predicate. The driving side streams and the source is asked once per
 *       batch of at most {@code max_in_list} distinct keys.
 *   <li><b>BROADCAST</b> — the same node with the key set spelled as {@code VALUES} rows and a
 *       single call carrying the whole small side, for a source that declares
 *       {@code supports_values_join}.
 *   <li><b>ADAPTIVE</b> — a {@link ChalkAdaptiveJoin} carrying both alternatives, chosen at
 *       execution from the small side's <em>measured</em> distinct keys. Emitted whenever the small
 *       side's estimate is not itself a measured statistic, which is the common case and therefore
 *       the default (D97).
 * </ul>
 *
 * <p>A plain {@code RelRule} rather than a {@code ConverterRule} because it produces several
 * alternatives from one match, and a converter rule may only return one.
 *
 * <p>What it refuses, and why each refusal is a fallback to {@code LOCAL} rather than an error:
 * more than one equality key (no portable {@code IN}-list spelling for a tuple), a lookup side that
 * is not a scan of one table with an optional filter and projection (the key set has to be put
 * somewhere the source will evaluate it), a lookup side carrying a dynamic parameter (the key set
 * would no longer be the only placeholder in the generated SQL), a join type the lookup shape
 * cannot express, and a source whose descriptor does not accept {@code IN} lists.
 */
public final class CrossSourceJoinRule
    extends org.apache.calcite.plan.RelRule<ChalkRuleConfig> {

  private final List<SourceConvention> sources;
  private final PushdownPolicy pushdown;
  private final JoinPolicy policy;

  private CrossSourceJoinRule(
      ChalkRuleConfig config,
      List<SourceConvention> sources,
      PushdownPolicy pushdown,
      JoinPolicy policy) {
    super(config);
    this.sources = ImmutableList.copyOf(sources);
    this.pushdown = pushdown;
    this.policy = policy;
  }

  private static ChalkRuleConfig config(
      List<SourceConvention> sources, PushdownPolicy pushdown, JoinPolicy policy) {
    return ChalkRuleConfig.of(
        "CrossSourceJoinRule",
        b ->
            b.operand(LogicalJoin.class)
                .trait(org.apache.calcite.plan.Convention.NONE)
                .anyInputs(),
        config -> new CrossSourceJoinRule(config, sources, pushdown, policy));
  }

  /** The rule for this catalog's sources, or none when nothing could be pushed anyway. */
  public static @Nullable CrossSourceJoinRule create(
      List<SourceConvention> sources, PushdownPolicy pushdown, JoinPolicy policy) {
    if (sources.isEmpty() || !pushdown.allowsFullRemotePushdown()) {
      return null;
    }
    return new CrossSourceJoinRule(config(sources, pushdown, policy), sources, pushdown, policy);
  }

  @Override
  public void onMatch(RelOptRuleCall call) {
    LogicalJoin join = call.rel(0);
    if (!join.getVariablesSet().isEmpty()) {
      return;
    }

    String left = singleSourceOf(join.getLeft());
    String right = singleSourceOf(join.getRight());
    if (left == null || right == null || left.equals(right)) {
      return; // same source, or a shape whose sources cannot be read off: PushdownRules' business
    }

    // V50: a null-safe equality reads back as a plain key, and a lookup call binds plain values.
    if (ChalkInputs.hasNullSafeEquality(join)) {
      return;
    }

    JoinInfo info = join.analyzeCondition();
    if (info.leftKeys.size() != 1) {
      // A tuple key set has no IN-list spelling every dialect accepts. LOCAL still applies.
      return;
    }
    if (!info.isEqui()) {
      // A residual over the joined row would have to be evaluated after the call, and a LookupJoin
      // carries only its equality in this milestone (ADR 0022). LOCAL evaluates it as it always has.
      return;
    }

    RelMetadataQuery mq = call.getMetadataQuery();
    double leftRows = rowsOf(mq, join.getLeft());
    double rightRows = rowsOf(mq, join.getRight());

    // Driving the *smaller* side is the whole point: the keys travel, the rows do not. So the
    // smaller side is offered as the driving one first — but both orientations are offered, because
    // "smaller" is only a preference and the other side may be the only one that can be looked up
    // at all. A local source takes no queries, and a join with one of those in it has exactly one
    // orientation that works; leaving it to cost alone would silently mean LOCAL.
    boolean smallerOnLeft = leftRows <= rightRows;
    boolean commutable = join.getJoinType() == JoinRelType.INNER;

    if (smallerOnLeft || !commutable) {
      emit(call, join, left, right, /* commuted= */ false, mq);
      if (commutable) {
        emit(call, join, right, left, /* commuted= */ true, mq);
      }
    } else {
      emit(call, join, right, left, /* commuted= */ true, mq);
      emit(call, join, left, right, /* commuted= */ false, mq);
    }
  }

  /**
   * Every strategy the policy allows for this ordered pair, as alternatives in the same
   * {@code RelSet}. {@code drivingSource} drives; {@code lookupSource} is asked.
   */
  private void emit(
      RelOptRuleCall call,
      LogicalJoin join,
      String drivingSource,
      String lookupSource,
      boolean commuted,
      RelMetadataQuery mq) {
    SourceConvention lookupConvention = conventionOf(lookupSource);
    if (lookupConvention == null) {
      return; // a LOCAL source takes no queries, so there is nothing to look anything up in
    }

    java.util.Set<JoinStrategy> allowed = policy.allowed(drivingSource, lookupSource);
    JoinStrategy preferred = policy.preferred(drivingSource, lookupSource);
    if (preferred != JoinStrategy.JOIN_STRATEGY_UNSPECIFIED) {
      allowed = java.util.EnumSet.of(preferred, JoinStrategy.JOIN_STRATEGY_LOCAL);
    }

    Orientation oriented = commuted ? Orientation.commute(join) : Orientation.asWritten(join);
    if (oriented == null) {
      return;
    }

    PushdownGate gate = pushdown.gateFor(lookupConvention);
    int maxInList = gate.maxInList();
    if (maxInList <= 0) {
      return; // the descriptor says no IN list is ever pushed; LOCAL it is
    }

    LookupSide side = LookupSide.of(oriented.lookupInput(), lookupConvention, gate);
    if (side == null) {
      return;
    }

    boolean adaptive =
        allowed.contains(JoinStrategy.JOIN_STRATEGY_ADAPTIVE)
            && !RowCountProvenance.isMeasured(oriented.drivingInput());

    if (allowed.contains(JoinStrategy.JOIN_STRATEGY_LOOKUP) && !adaptive) {
      RelNode lookup =
          side.build(
              oriented.lookupKey(), ChalkKeySet.KEY_SET_IN, maxInList, join.getCluster().getRexBuilder());
      if (lookup != null) {
        transform(
            call,
            oriented,
            ChalkLookupJoin.create(
                ChalkInputs.unordered(oriented.drivingInput()),
                lookup,
                oriented.condition(),
                join.getVariablesSet(),
                oriented.joinType(),
                maxInList,
                JoinStrategy.JOIN_STRATEGY_LOOKUP));
      }
    }

    // Broadcast is offered on the same terms as lookup, and for the same reason: shipping the small
    // side in one call is a bet on how small it is, and a bet taken on a guessed estimate is exactly
    // what adaptive execution exists to avoid. On a guess the adaptive join is offered instead, and
    // it decides from a count of the rows in hand.
    if (allowed.contains(JoinStrategy.JOIN_STRATEGY_BROADCAST) && !adaptive && gate.supportsValuesJoin()) {
      long ceiling = policy.broadcastMaxRows(drivingSource, lookupSource);
      double small = rowsOf(mq, oriented.drivingInput());
      if (small <= ceiling) {
        RelNode lookup =
            side.build(
                oriented.lookupKey(),
                ChalkKeySet.KEY_SET_ROWS,
                (int) Math.min(Integer.MAX_VALUE, ceiling),
                join.getCluster().getRexBuilder());
        if (lookup != null) {
          transform(
              call,
              oriented,
              ChalkLookupJoin.create(
                  ChalkInputs.unordered(oriented.drivingInput()),
                  lookup,
                  oriented.condition(),
                  join.getVariablesSet(),
                  oriented.joinType(),
                  (int) Math.min(Integer.MAX_VALUE, ceiling),
                  JoinStrategy.JOIN_STRATEGY_BROADCAST));
        }
      }
    }

    if (adaptive) {
      RelNode lookup =
          side.build(
              oriented.lookupKey(), ChalkKeySet.KEY_SET_IN, maxInList, join.getCluster().getRexBuilder());
      if (lookup != null) {
        long maxKeys = (long) maxInList * policy.lookupMaxCalls();
        transform(
            call,
            oriented,
            ChalkAdaptiveJoin.create(
                ChalkInputs.unordered(oriented.drivingInput()),
                ChalkInputs.unordered(oriented.lookupInput()),
                oriented.condition(),
                join.getVariablesSet(),
                oriented.joinType(),
                lookup,
                (int) Math.min(Integer.MAX_VALUE, maxKeys),
                maxInList));
      }
    }
  }

  /** Registers the alternative, restoring the written column order when the join was commuted. */
  private static void transform(RelOptRuleCall call, Orientation oriented, RelNode joined) {
    call.transformTo(oriented.restore(joined));
  }

  private static double rowsOf(RelMetadataQuery mq, RelNode rel) {
    Double rows = mq.getRowCount(rel);
    return rows == null ? Double.MAX_VALUE : rows;
  }

  private @Nullable SourceConvention conventionOf(String sourceId) {
    for (SourceConvention convention : sources) {
      if (convention.sourceId().equals(sourceId)) {
        return convention;
      }
    }
    return null;
  }

  /**
   * The one source every table under {@code rel} belongs to, or null when there is not exactly one.
   * The same reading {@code PushdownRules.JoinRule} does, and for the same reason: asking the tables
   * gives a clean answer where asking whether a subset converts gives an unsatisfiable requirement.
   */
  static @Nullable String singleSourceOf(RelNode rel) {
    List<String> sources = new ArrayList<>(2);
    collect(rel, sources);
    if (sources.isEmpty()) {
      return null;
    }
    String first = sources.get(0);
    for (String sourceId : sources) {
      if (!sourceId.equals(first)) {
        return null;
      }
    }
    return first.isEmpty() ? null : first;
  }

  /**
   * Every source a table under {@code rel} belongs to, in the order met, with the empty id for a
   * relation that belongs to none — a context relation the executor materialises from the host's
   * binding. What a pair rule is asked about when a driving side is not one source's (F139, F55).
   */
  static java.util.Set<String> sourcesOf(RelNode rel) {
    List<String> sources = new ArrayList<>(2);
    collect(rel, sources);
    return new java.util.LinkedHashSet<>(sources);
  }

  private static void collect(RelNode rel, List<String> into) {
    if (rel instanceof RelSubset subset) {
      RelNode best = subset.getBest();
      collect(best != null ? best : subset.getOriginal(), into);
      return;
    }
    if (rel instanceof org.apache.calcite.rel.core.TableScan scan) {
      ChalkTable table = scan.getTable().unwrap(ChalkTable.class);
      into.add(table == null ? "" : table.sourceId());
      return;
    }
    for (RelNode input : rel.getInputs()) {
      collect(input, into);
    }
  }

  // ------------------------------------------------------------------ orientation

  /**
   * The join as the lookup shape needs it — driving on the left, lookup on the right — plus how to
   * put the output columns back if that meant commuting it.
   */
  private static final class Orientation {
    private final LogicalJoin join;
    private final boolean commuted;
    private final RexNode condition;
    private final int drivingKey;
    private final int lookupKey;

    private Orientation(
        LogicalJoin join, boolean commuted, RexNode condition, int drivingKey, int lookupKey) {
      this.join = join;
      this.commuted = commuted;
      this.condition = condition;
      this.drivingKey = drivingKey;
      this.lookupKey = lookupKey;
    }

    static @Nullable Orientation asWritten(LogicalJoin join) {
      JoinInfo info = join.analyzeCondition();
      if (!ChalkInputs.isExpressible(join.getJoinType()) || !isLookupType(join.getJoinType())) {
        return null;
      }
      return new Orientation(
          join, false, join.getCondition(), info.leftKeys.get(0), info.rightKeys.get(0));
    }

    /**
     * The same join with its inputs exchanged. Only an {@code INNER} join commutes into one of the
     * four types a lookup can express, so nothing else is offered here — an outer join whose small
     * side is on the right stays {@code LOCAL}, which is correct and one plan worse.
     */
    static @Nullable Orientation commute(LogicalJoin join) {
      if (join.getJoinType() != JoinRelType.INNER) {
        return null;
      }
      JoinInfo info = join.analyzeCondition();
      int leftCount = join.getLeft().getRowType().getFieldCount();
      int rightCount = join.getRight().getRowType().getFieldCount();
      RexNode swapped =
          join.getCondition()
              .accept(
                  new RexShuttle() {
                    @Override
                    public RexNode visitInputRef(RexInputRef ref) {
                      int index = ref.getIndex();
                      int moved = index < leftCount ? index + rightCount : index - leftCount;
                      return new RexInputRef(moved, ref.getType());
                    }
                  });
      return new Orientation(
          join, true, swapped, info.rightKeys.get(0), info.leftKeys.get(0));
    }

    private static boolean isLookupType(JoinRelType type) {
      return type == JoinRelType.INNER
          || type == JoinRelType.LEFT
          || type == JoinRelType.SEMI
          || type == JoinRelType.ANTI;
    }

    RelNode drivingInput() {
      return commuted ? join.getRight() : join.getLeft();
    }

    RelNode lookupInput() {
      return commuted ? join.getLeft() : join.getRight();
    }

    RexNode condition() {
      return condition;
    }

    JoinRelType joinType() {
      return join.getJoinType();
    }

    /** The lookup key, as an index into the lookup input's own row. */
    int lookupKey() {
      return lookupKey;
    }

    int drivingKey() {
      return drivingKey;
    }

    /**
     * The joined rel with the written column order restored. A commuted join outputs the right
     * input's fields first, so a projection puts them back — which is exactly what Calcite's own
     * {@code JoinCommuteRule} does, written here because the rule builds physical rels directly.
     */
    RelNode restore(RelNode joined) {
      if (!commuted) {
        return joined;
      }
      int leftCount = join.getLeft().getRowType().getFieldCount();
      int rightCount = join.getRight().getRowType().getFieldCount();
      RexBuilder rex = join.getCluster().getRexBuilder();
      List<RexNode> projects = new ArrayList<>(leftCount + rightCount);
      List<RelDataTypeField> fields = joined.getRowType().getFieldList();
      for (int i = 0; i < leftCount; i++) {
        projects.add(rex.makeInputRef(fields.get(rightCount + i).getType(), rightCount + i));
      }
      for (int i = 0; i < rightCount; i++) {
        projects.add(rex.makeInputRef(fields.get(i).getType(), i));
      }
      return ChalkProject.create(joined, projects, join.getRowType());
    }
  }

  // ------------------------------------------------------------------ the lookup side

  /**
   * The lookup input, taken apart far enough to put a key set into its predicate: a scan of one of
   * the lookup source's tables, optionally under a filter and a projection of plain column
   * references.
   *
   * <p>Rebuilt in the source convention by hand rather than left to {@code PushdownRules}, because
   * this subtree must be a <em>concrete</em> rel and not a {@code RelSubset}: an
   * {@code AdaptiveJoin} holds its lookup branch as a field, outside the inputs Volcano
   * materialises, and a lookup join must never have a right side cost could turn into a local scan
   * carrying a predicate nothing can bind.
   */
  static final class LookupSide {
    private final LogicalTableScan scan;
    private final SourceConvention convention;
    private final @Nullable RexNode filter;
    private final @Nullable List<RexNode> projects;
    private final RelDataType rowType;
    private final int[] outputToScan;

    /**
     * F54: where the projection is not a permutation, the scan columns it reads, in the order the
     * pushed row carries them, and where each of them lands in that row. Null where the side's own
     * row is what the source returns, which is every other case.
     */
    private final int @Nullable [] scanToPruned;

    private final @Nullable List<Integer> pruned;

    private LookupSide(
        LogicalTableScan scan,
        SourceConvention convention,
        @Nullable RexNode filter,
        @Nullable List<RexNode> projects,
        RelDataType rowType,
        int[] outputToScan,
        int @Nullable [] scanToPruned,
        @Nullable List<Integer> pruned) {
      this.scan = scan;
      this.convention = convention;
      this.filter = filter;
      this.projects = projects;
      this.rowType = rowType;
      this.outputToScan = outputToScan;
      this.scanToPruned = scanToPruned;
      this.pruned = pruned;
    }

    static @Nullable LookupSide of(RelNode rel, SourceConvention convention, PushdownGate gate) {
      RelNode node = unwrap(rel);
      List<RexNode> projects = null;
      RelDataType rowType = null;
      RexNode filter = null;

      // Peel projections and filters in whichever order they came in, and there are two: the
      // planner's own Project-over-Filter and the Filter-over-Project the Hep pre-pass leaves when
      // it pushes a predicate through a projection. A filter above a projection is stated over the
      // projected row, so it is mapped back to the scan's columns before it is composed.
      for (var round = 0; round < 6; round++) {
        if (node instanceof LogicalProject project) {
          if (filter != null) {
            filter = mapThrough(filter, project);
          }

          // A second projection composes with the first: the outer one's references are indexes
          // into this one's output, so following them once restates the whole thing over the row
          // below. The Hep pre-pass leaves this shape whenever it pushes a predicate through a
          // projection and then prunes columns, which is most of the time.
          projects = projects == null ? project.getProjects() : compose(projects, project);
          if (rowType == null) {
            rowType = project.getRowType();
          }

          node = unwrap(project.getInput());
          continue;
        }
        if (node instanceof LogicalFilter logical) {
          if (filter != null) {
            return null;
          }
          filter = logical.getCondition();
          node = unwrap(logical.getInput());
          continue;
        }
        break;
      }

      if (rowType == null) {
        rowType = node.getRowType();
      }

      if (!(node instanceof LogicalTableScan scan)) {
        return null;
      }
      ChalkTable table = scan.getTable().unwrap(ChalkTable.class);
      if (table == null || !table.sourceId().equals(convention.sourceId())) {
        return null;
      }
      if (filter != null && !gate.canPushFilter(filter)) {
        return null;
      }
      if (projects != null && !gate.supportsProject()) {
        return null;
      }

      // The key set must be the only placeholder in the generated SQL: the executor finds it by
      // position, and a second `?` from an unrelated parameter would move it (ADR 0022).
      if (hasDynamicParam(filter) || hasDynamicParam(projects)) {
        return null;
      }

      // Which column of the scan each output column reads, and -1 for one that reads none — a
      // constant, or any expression over the scan's own columns (F54). The key the set is pushed on
      // has to be a plain reference, and {@link #build} declines a key whose entry is -1; every
      // other column may be whatever the projection made of it.
      int[] outputToScan = new int[rowType.getFieldCount()];
      if (projects == null) {
        for (int i = 0; i < outputToScan.length; i++) {
          outputToScan[i] = i;
        }
      } else {
        for (int i = 0; i < projects.size(); i++) {
          outputToScan[i] =
              projects.get(i) instanceof RexInputRef ref ? ref.getIndex() : -1;
        }
      }

      // F54: a projection that makes something of the scan's columns — a principal's mask folded to
      // a constant is the case this exists for — cannot go to the source, because §3.8 keeps a
      // withheld expression in process and this is the one place that builds a pushed subtree
      // without asking the pushdown rules. What goes down is a permutation of the columns it reads,
      // so the source still returns a pruned row; the expressions are restated over that row and the
      // caller puts them above the join.
      if (projects == null || isPermutation(projects)) {
        return new LookupSide(
            scan, convention, filter, projects, rowType, outputToScan, null, null);
      }

      int[] scanToPruned = new int[scan.getRowType().getFieldCount()];
      java.util.Arrays.fill(scanToPruned, -1);
      List<Integer> pruned = new ArrayList<>();
      for (RexNode project : projects) {
        collectRefs(project, scanToPruned, pruned);
      }
      if (pruned.isEmpty()) {
        // Every column is a constant, so there is nothing for the key set to be matched on.
        return null;
      }
      return new LookupSide(
          scan, convention, filter, projects, rowType, outputToScan, scanToPruned, pruned);
    }

    /** The table this side scans, which is what a rule asks about the leaf under it. */
    org.apache.calcite.plan.RelOptTable scanTable() {
      return scan.getTable();
    }

    /**
     * Which column of the scanned table an output column of this side reads, or {@code -1} where
     * the key is out of range — the same mapping {@link #build} puts the key set on.
     */
    int scanColumn(int outputKey) {
      if (outputKey < 0 || outputKey >= outputToScan.length) {
        return -1;
      }
      return outputToScan[outputKey];
    }

    /**
     * The subtree, in the source convention and behind a boundary, with {@code column IN (key set)}
     * added as the last conjunct of its predicate — last so the placeholder it writes is the last
     * one in the generated SQL, which is what makes finding it a matter of counting rather than of
     * parsing.
     */
    @Nullable RelNode build(int outputKey, SqlOperator keySet, int maxKeys, RexBuilder rex) {
      return build(ImmutableList.of(outputKey), keySet, maxKeys, rex);
    }

    /**
     * The same over several key columns (F50): the key set matches the tuple, one placeholder
     * stands for the whole list of key rows, and the columns appear in the order the driving side
     * produces them so a bound row's positions line up with the source's.
     */
    @Nullable RelNode build(
        List<Integer> outputKeys, SqlOperator keySet, int maxKeys, RexBuilder rex) {
      List<RexNode> columns = new ArrayList<>(outputKeys.size());
      for (int outputKey : outputKeys) {
        if (outputKey < 0 || outputKey >= outputToScan.length) {
          return null;
        }
        int scanKey = outputToScan[outputKey];
        // A key the projection made something of is not a column the source can be asked for (F54).
        if (scanKey < 0) {
          return null;
        }
        RelDataTypeField keyField = scan.getRowType().getFieldList().get(scanKey);
        columns.add(rex.makeInputRef(keyField.getType(), scanKey));
      }

      RexNode match =
          rex.makeCall(
              rex.getTypeFactory().createSqlType(org.apache.calcite.sql.type.SqlTypeName.BOOLEAN),
              keySet,
              columns);

      RelTraitSet traits = scan.getCluster().traitSetOf(convention);
      RelNode pushed = SourceScan.create(scan.getCluster(), scan.getTable(), convention);
      RexNode condition =
          filter == null ? match : RexUtil.composeConjunction(rex, ImmutableList.of(filter, match));
      pushed = new SourceRels.SourceFilter(scan.getCluster(), traits, pushed, condition);
      if (projects == null) {
        return SourceToLocalConverter.create(pushed);
      }
      if (pruned == null) {
        pushed =
            new SourceRels.SourceProject(scan.getCluster(), traits, pushed, projects, rowType);
        return SourceToLocalConverter.create(pushed);
      }

      List<RexNode> pruning = new ArrayList<>(pruned.size());
      List<String> names = new ArrayList<>(pruned.size());
      for (int column : pruned) {
        RelDataTypeField field = scan.getRowType().getFieldList().get(column);
        pruning.add(rex.makeInputRef(field.getType(), column));
        names.add(field.getName());
      }
      RelDataType prunedType =
          rex.getTypeFactory()
              .createStructType(pruning.stream().map(RexNode::getType).toList(), names);
      pushed = new SourceRels.SourceProject(scan.getCluster(), traits, pushed, pruning, prunedType);
      return SourceToLocalConverter.create(pushed);
    }

    /**
     * Where the key this side is matched on sits in the row {@link #build} produces — its own
     * position, or, where the row was pruned for F54, the position the pruning gave it.
     */
    int drivenKey(int outputKey) {
      if (scanToPruned == null) {
        return outputKey;
      }
      return scanToPruned[outputToScan[outputKey]];
    }

    /**
     * The side's declared row, restated over the pruned row the source returns (F54), or null where
     * the two are the same. The caller applies it <em>above</em> the join, which is where a
     * withheld expression belongs and where a lookup join's contract requires it: a lookup side is
     * the query the source runs with the keys bound into it and nothing else.
     */
    @Nullable List<RexNode> restore(RexBuilder rex) {
      if (pruned == null || projects == null) {
        return null;
      }
      List<RexNode> restored = new ArrayList<>(projects.size());
      for (RexNode project : projects) {
        restored.add(remap(project, scanToPruned, rex));
      }
      return restored;
    }

    /** Whether every projection is a plain column reference. */
    private static boolean isPermutation(List<RexNode> projects) {
      for (RexNode node : projects) {
        if (!(node instanceof RexInputRef)) {
          return false;
        }
      }
      return true;
    }

    /** Every scan column an expression reads, in first-seen order, and where each lands. */
    private static void collectRefs(RexNode node, int[] at, List<Integer> read) {
      node.accept(
          new RexShuttle() {
            @Override
            public RexNode visitInputRef(RexInputRef ref) {
              if (at[ref.getIndex()] < 0) {
                at[ref.getIndex()] = read.size();
                read.add(ref.getIndex());
              }
              return ref;
            }
          });
    }

    /** The expression restated over the pruned row the source returns. */
    private static RexNode remap(RexNode node, int[] at, RexBuilder rex) {
      return node.accept(
          new RexShuttle() {
            @Override
            public RexNode visitInputRef(RexInputRef ref) {
              return rex.makeInputRef(ref.getType(), at[ref.getIndex()]);
            }
          });
    }

    /** The outer projection restated over the row the inner one reads. */
    private static List<RexNode> compose(List<RexNode> outer, LogicalProject inner) {
      List<RexNode> projects = inner.getProjects();
      RexShuttle through =
          new RexShuttle() {
            @Override
            public RexNode visitInputRef(RexInputRef ref) {
              return projects.get(ref.getIndex());
            }
          };
      List<RexNode> composed = new ArrayList<>(outer.size());
      for (RexNode node : outer) {
        composed.add(node.accept(through));
      }
      return composed;
    }

    /** A predicate over a projected row, restated over the row the projection reads. */
    private static RexNode mapThrough(RexNode condition, LogicalProject project) {
      List<RexNode> projects = project.getProjects();
      return condition.accept(
          new RexShuttle() {
            @Override
            public RexNode visitInputRef(RexInputRef ref) {
              return projects.get(ref.getIndex());
            }
          });
    }

    private static RelNode unwrap(RelNode rel) {
      if (rel instanceof RelSubset subset) {
        RelNode best = subset.getBest();
        return best != null ? best : subset.getOriginal();
      }
      return rel;
    }

    private static boolean hasDynamicParam(@Nullable RexNode node) {
      if (node == null) {
        return false;
      }
      boolean[] found = {false};
      node.accept(
          new RexShuttle() {
            @Override
            public RexNode visitDynamicParam(org.apache.calcite.rex.RexDynamicParam param) {
              found[0] = true;
              return param;
            }
          });
      return found[0];
    }

    private static boolean hasDynamicParam(@Nullable List<RexNode> nodes) {
      if (nodes == null) {
        return false;
      }
      for (RexNode node : nodes) {
        if (hasDynamicParam(node)) {
          return true;
        }
      }
      return false;
    }
  }

  /** Unused today; kept so the shape of a multi-key set is stated where it will be needed. */
  static ImmutableBitSet keysOf(JoinInfo info) {
    return ImmutableBitSet.of(info.leftKeys);
  }

  /** The convention every alternative this rule builds lands in. */
  static RelTraitSet localTraits(RelNode rel) {
    return rel.getCluster().traitSetOf(ChalkConvention.LOCAL);
  }
}
