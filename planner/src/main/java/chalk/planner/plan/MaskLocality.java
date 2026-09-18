package chalk.planner.plan;

import chalk.ir.v1.Enforcement;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.entitlement.Disclosed;
import chalk.planner.entitlement.DisclosureMap;
import chalk.planner.entitlement.EntitledRelOptTable;
import chalk.planner.plan.rel.ProjectedRelOptTable;
import java.util.List;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Filter;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexVisitorImpl;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Masks stay local, and a {@code LOCAL} table's predicates stay local
 * (docs/design/16-entitlements.md §3.7, §3.8; D153, D156, D200).
 *
 * <p>A mask lives in {@code Project_D} and only there, but {@code Project_D} is an ordinary
 * projection that {@code PROJECT_MERGE} may fold together with the statement's own, so locality
 * cannot be a property of a node. It is a property of the <em>column at this leaf</em>, applied
 * where pushdown is decided: the pass hands the disclosure map to the gate through a wrapper on the
 * entitled scan's {@code RelOptTable}, and the pushdown rules ask this class what that wrapper says
 * before they let an expression into the source's convention.
 *
 * <p>The rule is narrow on purpose. A <em>bare reference</em> to a redacted column always pushes —
 * the source has to return the raw value for the mask to be applied to it — and so does any
 * expression over columns this principal sees in full: a manager's {@code UPPER(first_name) = 'T'}
 * is an ordinary predicate on an ordinary column, and losing it would make an entitled catalog
 * slower for the principals it discloses most to. What never pushes is an expression that is not a
 * bare reference and that <em>reads</em> a column this leaf does not disclose plainly: an agent's
 * {@code first_name LIKE 'T%'} becomes {@code SUBSTRING(first_name, 1, 1) LIKE 'T%'} once the
 * optimiser transposes it through {@code Project_D}, and that expression is the mask. It is
 * evaluated locally, over the tenancy's rows the pushed row predicate already returned.
 *
 * <p>Both flags, or neither (D153): a mask travels only where the table declares {@code push_masks}
 * <em>and</em> the source declares {@code supports_mask_pushdown}. A population-only column is not
 * redacted here — it leaves the leaf raw and tainted on purpose, so the source may read it, which is
 * what lets a permitted aggregate push with its guard staying local (§3.6).
 */
public final class MaskLocality {
  private MaskLocality() {}

  /**
   * What the entitled leaf under {@code input} withholds, by that input's own column positions, or
   * null when there is no entitled leaf to ask — in which case nothing here constrains anything.
   *
   * <p>The walk composes through the nodes that stand between a rule and the leaf it is about to
   * push: filters, which renumber nothing, and projections, which do. A projection's output is
   * redacted exactly when what it reads is — which is the same rule the report takes upward, and it
   * is what makes {@code Project_D} itself the first thing this answers about. The walk stops at
   * anything with more than one input: a join's row is not one leaf's.
   */
  public static @Nullable Redacted of(RelNode input) {
    boolean[] redacted = redactedOf(input);
    RelOptTable table = leafTableOf(input);
    if (redacted == null || table == null) {
      return null;
    }
    boolean any = false;
    for (boolean one : redacted) {
      any |= one;
    }

    ChalkTable chalkTable = table.unwrap(ChalkTable.class);
    boolean pushMasks =
        chalkTable != null && chalkTable.descriptor().getEntitlement().getPushMasks();
    Enforcement enforcement =
        chalkTable == null
            ? Enforcement.ENFORCEMENT_UNSPECIFIED
            : chalkTable.descriptor().getEntitlement().getEnforcement();
    return new Redacted(redacted, any, pushMasks, enforcement);
  }

  /** Which of {@code rel}'s output columns are not disclosed plainly, or null when it is not a leaf's. */
  private static boolean @Nullable [] redactedOf(RelNode rel) {
    if (rel instanceof TableScan scan) {
      RelOptTable table = scan.getTable();
      DisclosureMap map = EntitledRelOptTable.disclosureOf(table);
      if (map == null) {
        return null;
      }
      ProjectedRelOptTable projected = table.unwrap(ProjectedRelOptTable.class);
      int width = scan.getRowType().getFieldCount();
      boolean[] redacted = new boolean[width];
      for (int i = 0; i < width; i++) {
        int ordinal =
            projected != null && i < projected.projection().size()
                ? projected.projection().get(i)
                : i;
        redacted[i] = map.of(ordinal).isRedactedFromPushdown();
      }
      return redacted;
    }
    if (rel instanceof Filter filter) {
      return redactedOf(filter.getInput());
    }
    if (rel instanceof org.apache.calcite.rel.core.Project project) {
      boolean[] in = redactedOf(project.getInput());
      if (in == null) {
        return null;
      }
      List<RexNode> exprs = project.getProjects();
      boolean[] out = new boolean[exprs.size()];
      for (int i = 0; i < out.length; i++) {
        out[i] = readsAny(exprs.get(i), in);
      }
      return out;
    }
    if (rel instanceof org.apache.calcite.plan.volcano.RelSubset subset) {
      RelNode best = subset.getBest();
      return best == null ? redactedOf(subset.getOriginal()) : redactedOf(best);
    }
    return null;
  }

  /** The entitled table the single-input chain below {@code rel} reads, or null. */
  private static @Nullable RelOptTable leafTableOf(RelNode rel) {
    RelNode node = rel;
    while (true) {
      if (node instanceof org.apache.calcite.plan.volcano.RelSubset subset) {
        RelNode best = subset.getBest();
        node = best == null ? subset.getOriginal() : best;
        continue;
      }
      if (node instanceof TableScan scan) {
        return EntitledRelOptTable.disclosureOf(scan.getTable()) == null ? null : scan.getTable();
      }
      if (node.getInputs().size() != 1) {
        return null;
      }
      node = node.getInput(0);
    }
  }

  private static boolean readsAny(RexNode expr, boolean[] redacted) {
    boolean[] found = {false};
    expr.accept(
        new RexVisitorImpl<@Nullable Void>(true) {
          @Override
          public @Nullable Void visitInputRef(RexInputRef ref) {
            if (ref.getIndex() < redacted.length && redacted[ref.getIndex()]) {
              found[0] = true;
            }
            return null;
          }
        });
    return found[0];
  }

  /** One entitled leaf's answer to the gate's two questions (§3.7, §3.8). */
  public static final class Redacted {
    private final boolean[] columns;
    private final boolean any;
    private final boolean pushMasks;
    private final Enforcement enforcement;

    private Redacted(boolean[] columns, boolean any, boolean pushMasks, Enforcement enforcement) {
      this.columns = columns;
      this.any = any;
      this.pushMasks = pushMasks;
      this.enforcement = enforcement;
    }

    /** {@code LOCAL} (D156): the source receives a projected scan and no predicate at all. */
    public boolean isLocalOnly() {
      return enforcement == Enforcement.ENFORCEMENT_LOCAL;
    }

    /**
     * Whether {@code expr} may enter the source's convention over this leaf, given what the source
     * declares.
     */
    public boolean pushable(RexNode expr, PushdownGate gate) {
      if (!any || expr instanceof RexInputRef) {
        return true;
      }
      if (pushMasks && gate.supportsMaskPushdown()) {
        return true;
      }
      return !readsWithheld(expr);
    }

    /** Whether every one of {@code exprs} may (the projection's question). */
    public boolean pushable(List<RexNode> exprs, PushdownGate gate) {
      for (RexNode expr : exprs) {
        if (!pushable(expr, gate)) {
          return false;
        }
      }
      return true;
    }

    private boolean readsWithheld(RexNode expr) {
      boolean[] found = {false};
      expr.accept(
          new RexVisitorImpl<@Nullable Void>(true) {
            @Override
            public @Nullable Void visitInputRef(RexInputRef ref) {
              if (ref.getIndex() < columns.length && columns[ref.getIndex()]) {
                found[0] = true;
              }
              return null;
            }
          });
      return found[0];
    }
  }
}
