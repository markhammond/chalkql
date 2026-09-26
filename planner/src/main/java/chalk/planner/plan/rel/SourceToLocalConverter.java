package chalk.planner.plan.rel;

import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.CostModel;
import chalk.planner.plan.SourceConvention;
import java.util.List;
import org.apache.calcite.plan.DeriveMode;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelCollationTraitDef;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.SingleRel;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The boundary (D83): everything below is one query the source runs, everything above is Chalk's.
 * Calcite's {@code JdbcToEnumerableConverter} is the model. {@code RelToIr} turns this node into the
 * IR's {@code RemoteQuery} — with the generated SQL, the pushed subtree as IR, or both.
 *
 * <p>Its cost is what makes the whole pushdown model work: {@code remote_call_cost + rows ×
 * remote_row_cost} from the source's own cost profile (D38). Pushing a filter into the source is
 * free at the filter (see {@link SourceRels}); what it buys is a smaller {@code rows} here. Pushing
 * work that does not reduce rows therefore neither wins nor loses, and pushing work that does win
 * wins by exactly the rows it saves.
 */
public final class SourceToLocalConverter extends SingleRel implements ChalkRel {

  private SourceToLocalConverter(RelOptCluster cluster, RelTraitSet traits, RelNode input) {
    super(cluster, traits, input);
  }

  /**
   * The converter over {@code input}, carrying whatever ordering the pushed subtree delivers: if a
   * sort was pushed, the rows arrive sorted and an {@code ORDER BY} above needs no local sort.
   */
  public static SourceToLocalConverter create(RelNode input) {
    RelOptCluster cluster = input.getCluster();
    RelTraitSet traits =
        cluster
            .traitSetOf(ChalkConvention.LOCAL)
            .replaceIf(
                RelCollationTraitDef.INSTANCE, () -> input.getTraitSet().getCollation());
    return new SourceToLocalConverter(cluster, traits, input);
  }

  /** The source this subtree is handed to. */
  public SourceConvention sourceConvention() {
    return (SourceConvention) getInput().getTraitSet().getConvention();
  }

  public String sourceId() {
    return sourceConvention().sourceId();
  }

  @Override
  public RelNode copy(RelTraitSet traitSet, List<RelNode> inputs) {
    return new SourceToLocalConverter(getCluster(), traitSet, inputs.get(0));
  }

  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double rows = mq.getRowCount(getInput());
    CostModel costs = CostModel.of(profileOf(getInput()));
    double work = costs.remoteCallCost() + (rows * costs.remoteRowCost());
    RelOptCost cost = planner.getCostFactory().makeCost(work, work, 0);
    if (!carriesRequiredPredicate()) {
      // F147: a PUSHDOWN_REQUIRED table's boundary that does not ask the source for the table's own
      // row predicate is a plan the guard will refuse. Charging it here, rather than only refusing
      // it there, makes cost prefer a boundary that carries the predicate wherever one exists — a
      // key set looked up from the context, say — and leaves the refusal for the case where none does.
      cost = cost.plus(planner.getCostFactory().makeHugeCost());
    }
    return cost;
  }

  /**
   * Whether this boundary is one {@code PUSHDOWN_REQUIRED} allows: every boundary is, unless the
   * entitled table beneath it requires its row predicate pushed and the source's side does not carry
   * it. Decided once per instance, whose input is fixed for its life.
   */
  private boolean carriesRequiredPredicate() {
    Boolean known = carriesRequired;
    if (known == null) {
      known =
          requiredScanOf(getInput()) == null
              || chalk.planner.entitlement.DisclosureReport.boundaryCarriesRowPredicate(
                  getInput(), getCluster().getRexBuilder());
      carriesRequired = known;
    }
    return known;
  }

  /** The entitled scan under {@code rel} whose table requires its row predicate pushed, or null. */
  private static @Nullable SourceScan requiredScanOf(RelNode rel) {
    RelNode node = rel;
    if (node instanceof org.apache.calcite.plan.volcano.RelSubset subset) {
      RelNode best = subset.getBest();
      node = best != null ? best : subset.getOriginal();
    }
    if (node instanceof SourceScan scan) {
      return scan.chalkTable().descriptor().getEntitlement().getEnforcement()
              == chalk.ir.v1.Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED
          ? scan
          : null;
    }
    for (RelNode input : node.getInputs()) {
      SourceScan found = requiredScanOf(input);
      if (found != null) {
        return found;
      }
    }
    return null;
  }

  private transient @Nullable Boolean carriesRequired;

  /**
   * The cost profile of the table at the bottom of the pushed subtree, so a per-table
   * {@code remote_call_cost} reaches the boundary that actually pays it. Falls back to the
   * planner's defaults when the subtree has more than one leaf (a pushed join): the first one wins,
   * which is deterministic and good enough to order alternatives by.
   */
  private static chalk.ir.v1.CostProfile profileOf(RelNode rel) {
    if (rel instanceof SourceScan scan) {
      return scan.chalkTable().costProfile();
    }
    for (RelNode input : rel.getInputs()) {
      chalk.ir.v1.CostProfile found = profileOf(input);
      if (found != null) {
        return found;
      }
    }
    return chalk.ir.v1.CostProfile.getDefaultInstance();
  }

  /**
   * Nothing is passed down and nothing is derived: the subtree below is the source's, and a trait
   * Chalk requires above the boundary cannot be turned into a requirement on a query that is
   * already fixed. What the source <em>does</em> deliver is read once, in {@link #create}, and
   * carried as this node's own collation. {@link ChalkRel}'s defaults already decline both hooks;
   * the derive mode is what stops the top-down optimiser asking.
   */
  @Override
  public DeriveMode getDeriveMode() {
    return DeriveMode.PROHIBITED;
  }
}
