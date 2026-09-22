package chalk.planner.plan;

import java.util.Set;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.CorrelationId;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.rex.RexCorrelVariable;
import org.apache.calcite.rex.RexFieldAccess;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql2rel.CorrelationReferenceFinder;
import org.apache.calcite.sql2rel.RelFieldTrimmer;
import org.apache.calcite.tools.RelBuilder;
import org.apache.calcite.util.ImmutableBitSet;

/**
 * Calcite's field trimmer, without the rule that a column defining an input's collation is never
 * discarded (F114).
 *
 * <p>{@code RelFieldTrimmer.trimChild} unions the field indexes of every collation its input
 * reports into the columns the parent asked for, so that a trimmed input does not lose an ordering
 * something above might have used. For Chalk that keeps the *declared* collation of every table in
 * every scan: {@code SELECT symbol, volume FROM bars WHERE volume < 0} read {@code ts} as well,
 * because the table declares {@code (ts, symbol)} and the filter's child was trimmed through that
 * union — a third of a two-column scan, read for a column the query never mentions.
 *
 * <p>Dropping the union is sound here because nothing above can order by a column nothing above
 * reads. Every operator that wants an ordering names the columns it wants it on, and the trimmer
 * already keeps those: {@code trimFields(Sort)} adds the sort's own keys before trimming its child,
 * a join adds its keys, and a filter adds the columns its condition reads. What the union added on
 * top of that was an ordering over columns no parent had asked for, which no rule could have
 * matched on. A collation over columns that *are* kept still survives, because {@link
 * chalk.planner.plan.rel.ProjectedRelOptTable} carries the longest projected prefix of every
 * declared collation onto the pruned scan.
 */
public final class ChalkFieldTrimmer extends RelFieldTrimmer {
  public ChalkFieldTrimmer(RelBuilder relBuilder) {
    super(null, relBuilder);
  }

  /**
   * The same as the inherited one but for the collation union: the columns the parent asked for,
   * plus the ones a correlation variable of this node reads, which are references into the child's
   * row that no {@code RexInputRef} of the parent names.
   */
  @Override
  protected TrimResult trimChild(
      RelNode rel, RelNode input, ImmutableBitSet fieldsUsed, Set<RelDataTypeField> extraFields) {
    ImmutableBitSet.Builder used = fieldsUsed.rebuild();
    for (CorrelationId correlation : rel.getVariablesSet()) {
      rel.accept(
          new CorrelationReferenceFinder() {
            @Override
            protected RexNode handle(RexFieldAccess fieldAccess) {
              RexCorrelVariable variable = (RexCorrelVariable) fieldAccess.getReferenceExpr();
              if (variable.id.equals(correlation)) {
                used.set(fieldAccess.getField().getIndex());
              }
              return fieldAccess;
            }
          });
    }

    return dispatchTrimFields(input, used.build(), extraFields);
  }
}
