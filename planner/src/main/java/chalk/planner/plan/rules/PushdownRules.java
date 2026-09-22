package chalk.planner.plan.rules;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.PushdownGate;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SourceConvention;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.plan.rel.ChalkFilter;
import chalk.planner.plan.rel.ChalkTableScan;
import chalk.planner.plan.rel.SourceRels;
import chalk.planner.plan.rel.SourceScan;
import chalk.planner.plan.rel.SourceToLocalConverter;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.plan.RelOptRule;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.convert.ConverterRule;
import org.apache.calcite.rel.core.AggregateCall;
import org.apache.calcite.rel.logical.LogicalAggregate;
import org.apache.calcite.rel.logical.LogicalFilter;
import org.apache.calcite.rel.logical.LogicalJoin;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rel.logical.LogicalTableScan;
import org.apache.calcite.rel.logical.LogicalSort;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexDynamicParam;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexUtil;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * One rule set, parameterised by the descriptor (D83). Nothing here is per source: every rule reads
 * a {@link PushdownGate} built from the {@link SourceConvention} it converts into, so a third-party
 * adapter changes what is pushed by changing its descriptor and never touches the planner.
 *
 * <p>Calcite's JDBC adapter is the shape: a converter rule per operator from {@code Convention.NONE}
 * into the source's convention, plus the boundary converter out of it. The one addition is {@link
 * ScanRule}, which offers the leaf in the source convention <em>as an alternative</em> to the local
 * scan rather than instead of it — every {@code REMOTE} source must still be scannable, because that
 * is what {@code PUSHDOWN_LEVEL_NONE} reads and what the I4 oracle compares against.
 */
public final class PushdownRules {
  private PushdownRules() {}

  /**
   * Every rule for one source, in a fixed order. Empty when the level forbids pushdown entirely:
   * at {@code NONE} a remote source is read by its scan path and nothing else exists.
   */
  public static List<RelOptRule> forSource(SourceConvention convention, PushdownPolicy policy) {
    PushdownGate gate = policy.gateFor(convention);
    if (!policy.allowsRemotePushdown()) {
      return ImmutableList.of();
    }

    List<RelOptRule> rules = new ArrayList<>();
    rules.add(new ScanRule(convention, gate, policy));
    rules.add(new BoundaryRule(convention));

    // FILTERS_ONLY is predicates and projection pruning; PROJECTION_ONLY is pruning alone
    // (§2, D87). Anything above that needs FULL.
    if (policy.allowsProjectionIntoRemote()) {
      rules.add(new ProjectRule(convention, gate, policy));
    }
    if (policy.allowsFilterIntoRemote()) {
      rules.add(new FilterRule(convention, gate, policy));
    }
    if (policy.allowsFullRemotePushdown()) {
      rules.add(new AggregateRule(convention, gate, policy));
      rules.add(new SortRule(convention, gate, policy));
      rules.add(new JoinRule(convention, gate, policy));
    }
    return ImmutableList.copyOf(rules);
  }

  /** The stable names of the rule classes, for {@code PlannerConfig.configHash}. */
  public static List<String> ruleClassNames() {
    return ImmutableList.of(
        "PushScanRule",
        "PushBoundaryRule",
        "PushProjectRule",
        "PushFilterRule",
        "PushAggregateRule",
        "PushSortRule",
        "PushJoinRule");
  }

  /** The base every rule here shares: the convention it pushes into and the gate that decides. */
  private abstract static class Base extends ConverterRule {
    final SourceConvention convention;
    final PushdownGate gate;
    final PushdownPolicy policy;

    Base(Config config, SourceConvention convention, PushdownGate gate, PushdownPolicy policy) {
      super(config);
      this.convention = convention;
      this.gate = gate;
      this.policy = policy;
    }

    /** The estimated rows of a subtree, for {@code max_pushdown_rows}. */
    static double rowsOf(RelNode rel) {
      RelMetadataQuery mq = rel.getCluster().getMetadataQuery();
      Double rows = mq.getRowCount(rel);
      return rows == null ? Double.MAX_VALUE : rows;
    }

    static List<RelDataType> typesOf(RelNode rel) {
      List<RelDataType> types = new ArrayList<>(rel.getRowType().getFieldCount());
      for (RelDataTypeField field : rel.getRowType().getFieldList()) {
        types.add(field.getType());
      }
      return types;
    }
  }

  /**
   * The leaf: a {@code ChalkTableScan} of one of this source's tables becomes a {@code SourceScan}.
   * The local alternative stays in the same {@code RelSet}, so cost chooses between "the client
   * reads the table" and "the source runs a query" — and at {@code PUSHDOWN_LEVEL_NONE} this rule is
   * not registered at all, which is what makes the reference configuration a different execution
   * rather than the same plan with a flag.
   */
  private static final class ScanRule extends Base {
    ScanRule(SourceConvention convention, PushdownGate gate, PushdownPolicy policy) {
      super(
          Config.INSTANCE
              .withConversion(
                  LogicalTableScan.class,
                  Convention.NONE,
                  convention,
                  "PushScanRule:" + convention.sourceId()),
          convention,
          gate,
          policy);
    }

    @Override
    public @Nullable RelNode convert(RelNode rel) {
      LogicalTableScan scan = (LogicalTableScan) rel;
      ChalkTable table = scan.getTable().unwrap(ChalkTable.class);
      if (table == null || !table.sourceId().equals(convention.sourceId())) {
        return null;
      }
      // No row ceiling here. `max_pushdown_rows` bounds what a *pushed subtree* hands back, and a
      // bare scan is not a subtree anyone can decline: with pushdown on this is the only rule that
      // implements the leaf, so refusing it would make the table unplannable rather than
      // conservative. The ceiling stops the operators above it, which is corpus query 10's shape.
      return SourceScan.create(
          scan.getCluster(),
          scan.getTable(),
          convention);
    }
  }

  /**
   * The boundary out of the source convention (D83) — this is what becomes {@code RemoteQuery}.
   *
   * <p>A plain rule rather than a {@code ConverterRule}: it must offer a boundary above <em>every</em>
   * pushed rel, not only the leaf, because where the boundary sits is exactly the "push more or push
   * less" decision the cost model makes. One alternative per level, and the cheapest wins.
   */
  private static final class BoundaryRule
      extends org.apache.calcite.plan.RelRule<ChalkRuleConfig> {
    BoundaryRule(SourceConvention convention) {
      this(config(convention), convention);
    }

    private BoundaryRule(ChalkRuleConfig config, SourceConvention convention) {
      super(config);
      this.convention = convention;
    }

    private static ChalkRuleConfig config(SourceConvention convention) {
      return ChalkRuleConfig.of(
          "PushBoundaryRule:" + convention.sourceId(),
          b -> b.operand(chalk.planner.plan.rel.SourceRel.class).anyInputs(),
          config -> new BoundaryRule(config, convention));
    }

    private final SourceConvention convention;

    @Override
    public void onMatch(org.apache.calcite.plan.RelOptRuleCall call) {
      RelNode pushed = call.rel(0);
      if (!convention.equals(pushed.getTraitSet().getConvention())) {
        return;
      }
      call.transformTo(SourceToLocalConverter.create(pushed));

    }
  }

  /** {@code LogicalProject} → {@code SourceProject}, when every expression is pushable. */
  private static final class ProjectRule extends Base {
    ProjectRule(SourceConvention convention, PushdownGate gate, PushdownPolicy policy) {
      super(
          Config.INSTANCE
              .withConversion(
                  LogicalProject.class,
                  Convention.NONE,
                  convention,
                  "PushProjectRule:" + convention.sourceId()),
          convention,
          gate,
          policy);
    }

    @Override
    public @Nullable RelNode convert(RelNode rel) {
      LogicalProject project = (LogicalProject) rel;
      if (!gate.supportsProject() || !gate.allPushable(project.getProjects())) {
        return null;
      }
      // A mask never travels unless both sides opted in (D153, D200, §3.8). This is the projection
      // half; the same check on the pushed subtree is what keeps a mask out of `pushed_plan` too,
      // since the boundary renders one subtree into both.
      chalk.planner.plan.MaskLocality.Redacted withheld =
          chalk.planner.plan.MaskLocality.of(project.getInput());
      if (withheld != null && !withheld.pushable(project.getProjects(), gate)) {
        return null;
      }
      if (!gate.withinRowCeiling(rowsOf(project))) {
        return null;
      }
      RelNode input = ChalkInputs.intoSource(project.getInput(), convention);
      RelTraitSet traits = project.getCluster().traitSetOf(convention);
      return new SourceRels.SourceProject(
          project.getCluster(), traits, input, project.getProjects(), project.getRowType());
    }
  }

  /**
   * {@code LogicalFilter} → {@code SourceFilter} for the conjuncts whose shape the source declares,
   * and a local filter above the boundary for the rest (docs/design/16-entitlements.md §3.7).
   *
   * <p>Whole-or-nothing was the M4 shape, and it makes one unpushable conjunct hold back every
   * other conjunct of the same {@code WHERE}: a client-bodied user function beside a tenancy
   * predicate would fetch the whole table. Splitting is better planning for every statement,
   * entitled or not, and it is what makes an entitlement's row predicate reach the source when the
   * developer's own predicate cannot.
   *
   * <p>The residual is emitted as the local {@code ChalkFilter} the ordinary rule would build
   * rather than as a {@code LogicalFilter}, because the Volcano set already holds a logical member
   * for this node and {@code FILTER_MERGE} would be free to run the two conditions back together.
   * The shape is the same either way — one local filter over the boundary over the pushed filter —
   * and the alternative is costed beside the unsplit one, so the split is taken only when it wins.
   */
  private static final class FilterRule extends Base {
    FilterRule(SourceConvention convention, PushdownGate gate, PushdownPolicy policy) {
      super(
          Config.INSTANCE
              .withConversion(
                  LogicalFilter.class,
                  Convention.NONE,
                  convention,
                  "PushFilterRule:" + convention.sourceId()),
          convention,
          gate,
          policy);
    }

    @Override
    public @Nullable RelNode convert(RelNode rel) {
      LogicalFilter filter = (LogicalFilter) rel;
      RelNode input = ChalkInputs.intoSource(filter.getInput(), convention);
      RelTraitSet traits = filter.getCluster().traitSetOf(convention);

      // Under LOCAL enforcement (D156) the source receives a projected scan and no predicate at
      // all, so the tenant set never appears in remote query text.
      chalk.planner.plan.MaskLocality.Redacted withheld =
          chalk.planner.plan.MaskLocality.of(filter.getInput());
      if (withheld != null && withheld.isLocalOnly()) {
        return null;
      }

      // The whole filter, when the gate takes all of it: byte-identical to what this rule produced
      // before the residual split, condition and row-count estimate included.
      if (gate.canPushFilter(filter.getCondition())
          && (withheld == null || withheld.pushable(filter.getCondition(), gate))) {
        if (!gate.withinRowCeiling(rowsOf(filter))) {
          return null;
        }
        return new SourceRels.SourceFilter(
            filter.getCluster(), traits, input, filter.getCondition());
      }

      // Otherwise the conjuncts the gate accepts still go, and the rest stays here.
      List<RexNode> pushable = new ArrayList<>();
      List<RexNode> residual = new ArrayList<>();
      for (RexNode conjunct : RelOptUtil.conjunctions(filter.getCondition())) {
        // A conjunct reading a column this leaf does not disclose plainly is the mask, and it is
        // evaluated here over the rows the pushed row predicate returned (§3.8).
        boolean local =
            !gate.canPushFilter(conjunct)
                || (withheld != null && !withheld.pushable(conjunct, gate));
        (local ? residual : pushable).add(conjunct);
      }
      if (pushable.isEmpty() || residual.isEmpty()) {
        return null;
      }
      RexBuilder rexBuilder = filter.getCluster().getRexBuilder();
      RexNode pushedCondition = RexUtil.composeConjunction(rexBuilder, pushable);
      // Several conjuncts need the AND shape itself, which a source may not declare.
      if (!gate.canPushFilter(pushedCondition)) {
        return null;
      }
      SourceRels.SourceFilter pushed =
          new SourceRels.SourceFilter(filter.getCluster(), traits, input, pushedCondition);
      // The ceiling bounds what the source hands back, which is now the *pushed* part alone.
      if (!gate.withinRowCeiling(rowsOf(pushed))) {
        return null;
      }
      return ChalkFilter.create(
          SourceToLocalConverter.create(pushed),
          RexUtil.composeConjunction(rexBuilder, residual));
    }
  }

  /**
   * {@code LogicalAggregate} → {@code SourceAggregate}: {@code supports_group_by}, plain-column
   * keys, and every measure declared (§2). A {@code DISTINCT} measure needs {@code
   * supports_distinct} on top, which the gate folds in.
   */
  private static final class AggregateRule extends Base {
    AggregateRule(SourceConvention convention, PushdownGate gate, PushdownPolicy policy) {
      super(
          Config.INSTANCE
              .withConversion(
                  LogicalAggregate.class,
                  Convention.NONE,
                  convention,
                  "PushAggregateRule:" + convention.sourceId()),
          convention,
          gate,
          policy);
    }

    @Override
    public @Nullable RelNode convert(RelNode rel) {
      LogicalAggregate aggregate = (LogicalAggregate) rel;
      if (!gate.supportsGroupBy()) {
        return null;
      }
      // Grouping sets are the executor's business and no source is asked for them (A13).
      if (aggregate.getGroupSets().size() != 1) {
        return null;
      }
      List<RelDataType> inputTypes = typesOf(aggregate.getInput());
      if (!gate.canGroupBy(aggregate.getGroupSet().asList(), inputTypes)) {
        return null;
      }
      for (AggregateCall call : aggregate.getAggCallList()) {
        if (!gate.canPushAggregate(call, inputTypes)) {
          return null;
        }
      }
      if (!gate.withinRowCeiling(rowsOf(aggregate))) {
        return null;
      }
      RelNode input = ChalkInputs.intoSource(aggregate.getInput(), convention);
      RelTraitSet traits = aggregate.getCluster().traitSetOf(convention);
      return new SourceRels.SourceAggregate(
          aggregate.getCluster(),
          traits,
          input,
          aggregate.getGroupSet(),
          ImmutableList.of(aggregate.getGroupSet()),
          aggregate.getAggCallList());
    }
  }

  /**
   * {@code LogicalSort} → {@code SourceSort}. One rule covers what the design calls
   * {@code PushSortRule}, {@code PushLimitRule} and {@code PushOffsetRule}, because Calcite carries
   * all three on one node: a bare {@code LIMIT} is a {@code Sort} with an empty collation. The gate
   * answers each part separately, so a source that fetches but does not sort still gets its
   * {@code LIMIT}, and one that sorts but will not honour the plan's null placement gets neither.
   *
   * <p>A bound that is a <b>parameter</b> travels where a literal one does (D288). It is carried in
   * the pushed text as its own {@code ?}, and the executor writes the value bound at execution into
   * the text before the query is sent, so nothing here has to know which dialects would take a
   * parameter in a {@code LIMIT}. The capability gates are the same ones a literal answers to, and
   * the bound is exempt from {@code supports_parameters} because it never reaches the provider as
   * a parameter. Three shapes are still declined, each for a reason of its own:
   *
   * <ul>
   *   <li>a source whose query language is IR: there is no text to write a number into, and the
   *       pushed plan's bound would be a parameter the source has no way to resolve;
   *   <li>a bound scalar the entitlement rewrite put there ({@code BoundParam}): a bound is the
   *       statement's own parameter, and a context value is not the statement's to read here;
   *   <li>a bound whose parameter the pushed subtree <em>also</em> mentions somewhere else. Then
   *       one placeholder is rendered and another, standing for the same value, is bound, and
   *       which is which is decided by counting placeholders in a text this rule has not seen.
   *       Declining is the honest answer and costs one local fetch.
   * </ul>
   */
  private static final class SortRule extends Base {
    SortRule(SourceConvention convention, PushdownGate gate, PushdownPolicy policy) {
      super(
          Config.INSTANCE
              .withConversion(
                  LogicalSort.class,
                  Convention.NONE,
                  convention,
                  "PushSortRule:" + convention.sourceId()),
          convention,
          gate,
          policy);
    }

    @Override
    public @Nullable RelNode convert(RelNode rel) {
      LogicalSort sort = (LogicalSort) rel;
      boolean sorts = !sort.getCollation().getFieldCollations().isEmpty();
      boolean fetches = sort.fetch != null;
      boolean offsets = sort.offset != null;

      if (sorts && !gate.canPushSort(sort.getCollation(), typesOf(sort.getInput()))) {
        return null;
      }
      if (fetches && !gate.supportsLimit()) {
        return null;
      }
      if (offsets && !gate.supportsOffset()) {
        return null;
      }
      if (!sorts && !fetches && !offsets) {
        return null;
      }
      if (!pushableBound(sort, sort.fetch) || !pushableBound(sort, sort.offset)) {
        return null;
      }
      if (!gate.withinRowCeiling(rowsOf(sort))) {
        return null;
      }

      RelNode input = ChalkInputs.intoSource(sort.getInput(), convention);
      RelTraitSet traits =
          sort.getCluster().traitSetOf(convention).replace(sort.getCollation());
      return new SourceRels.SourceSort(
          sort.getCluster(), traits, input, sort.getCollation(), sort.offset, sort.fetch);
    }

    /** Whether this bound is one this source can be handed. Absent is trivially yes. */
    private boolean pushableBound(LogicalSort sort, @Nullable RexNode bound) {
      if (bound == null || bound instanceof org.apache.calcite.rex.RexLiteral) {
        return true;
      }
      if (!(bound instanceof RexDynamicParam parameter)
          || parameter instanceof chalk.planner.entitlement.BoundParam
          || !convention.isSql()) {
        return false;
      }

      Set<Integer> elsewhere = new HashSet<>();
      collectParameters(sort.getInput(), elsewhere);
      return !elsewhere.contains(parameter.getIndex());
    }

    /**
     * Every dynamic parameter index in {@code rel}'s own expressions and its inputs'.
     *
     * <p>A {@code RelSubset} is read through its best or original member, the way {@code JoinRule}
     * reads one: this runs during conversion, where an input is a subset rather than a tree, and
     * the question — which of the statement's parameters this subtree mentions — is one every
     * member of a set answers the same way.
     */
    private static void collectParameters(RelNode rel, Set<Integer> into) {
      if (rel instanceof org.apache.calcite.plan.volcano.RelSubset subset) {
        RelNode best = subset.getBest();
        collectParameters(best != null ? best : subset.getOriginal(), into);
        return;
      }

      rel.accept(
          new org.apache.calcite.rex.RexShuttle() {
            @Override
            public RexNode visitDynamicParam(RexDynamicParam parameter) {
              into.add(parameter.getIndex());
              return parameter;
            }
          });
      for (RelNode input : rel.getInputs()) {
        collectParameters(input, into);
      }
    }
  }

  /**
   * {@code LogicalJoin} → {@code SourceJoin}, but only when <em>both</em> inputs belong to this
   * source (§2). A cross-source join has no convention that can hold it, so it stays local over two
   * boundary converters — correct, and unoptimised until M5's adaptive semi-join.
   */
  private static final class JoinRule extends Base {
    JoinRule(SourceConvention convention, PushdownGate gate, PushdownPolicy policy) {
      super(
          Config.INSTANCE
              .withConversion(
                  LogicalJoin.class,
                  Convention.NONE,
                  convention,
                  "PushJoinRule:" + convention.sourceId()),
          convention,
          gate,
          policy);
    }

    @Override
    public @Nullable RelNode convert(RelNode rel) {
      LogicalJoin join = (LogicalJoin) rel;
      if (!gate.canPushJoin(join.getJoinType())) {
        return null;
      }
      if (!join.getVariablesSet().isEmpty()) {
        return null; // a correlate never survives decorrelation (D67); belt and braces
      }
      if (!gate.pushable(join.getCondition())) {
        return null;
      }
      if (!belongsToThisSource(join.getLeft()) || !belongsToThisSource(join.getRight())) {
        return null;
      }
      if (!gate.withinRowCeiling(rowsOf(join))) {
        return null;
      }

      RelNode left = ChalkInputs.intoSource(join.getLeft(), convention);
      RelNode right = ChalkInputs.intoSource(join.getRight(), convention);
      RelTraitSet traits = join.getCluster().traitSetOf(convention);
      return new SourceRels.SourceJoin(
          join.getCluster(),
          traits,
          left,
          right,
          join.getCondition(),
          join.getVariablesSet(),
          join.getJoinType());
    }

    /**
     * Whether every table under {@code rel} is one of this source's. Reading the tables rather than
     * asking whether the input converts is what keeps a cross-source join out: {@code convert} on a
     * subset that holds no rel of this convention would produce an unsatisfiable requirement rather
     * than a clean refusal.
     */
    private boolean belongsToThisSource(RelNode rel) {
      List<String> sources = new ArrayList<>(2);
      collectSources(rel, sources);
      if (sources.isEmpty()) {
        return false;
      }
      for (String sourceId : sources) {
        if (!sourceId.equals(convention.sourceId())) {
          return false;
        }
      }
      return true;
    }

    private static void collectSources(RelNode rel, List<String> into) {
      if (rel instanceof org.apache.calcite.plan.volcano.RelSubset subset) {
        RelNode best = subset.getBest();
        collectSources(best != null ? best : subset.getOriginal(), into);
        return;
      }
      if (rel instanceof org.apache.calcite.rel.core.TableScan scan) {
        ChalkTable table = scan.getTable().unwrap(ChalkTable.class);
        into.add(table == null ? "" : table.sourceId());
        return;
      }
      for (RelNode input : rel.getInputs()) {
        collectSources(input, into);
      }
    }
  }

}
