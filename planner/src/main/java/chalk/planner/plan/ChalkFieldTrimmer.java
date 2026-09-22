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
 * <p>{@code RelFieldTrimmer}'s two {@code trimChild} overloads each union the field indexes of
 * every collation their input reports into the columns the parent asked for, so that a trimmed
 * input does not lose an ordering something above might have used. For Chalk that keeps the
 * <em>declared</em> collation of every table in every scan: {@code SELECT symbol, volume FROM bars
 * WHERE volume < 0} read {@code ts} as well, because the table declares {@code (ts, symbol)} and
 * the filter's child was trimmed through that union — a third of a two-column scan, read for a
 * column the query never mentions.
 *
 * <p>Dropping the union is sound here because nothing above can order by a column nothing above
 * reads. Every operator that wants an ordering names the columns it wants it on, and the trimmer
 * already keeps those: {@code trimFields(Sort)} adds the sort's own keys before trimming its child,
 * a join adds its keys, and a filter adds the columns its condition reads. What the union added on
 * top of that was an ordering over columns no parent had asked for, which no rule could have
 * matched on. A collation over columns that *are* kept still survives, because {@link
 * chalk.planner.plan.rel.ProjectedRelOptTable} carries the longest projected prefix of every
 * declared collation onto the pruned scan.
 *
 * <p>Both overloads are overridden. They are not one method and one delegation in Calcite: the
 * four-argument one is what {@code Aggregate}, {@code Filter}, {@code Project}, {@code Sort},
 * {@code SetOp} and the rest trim their inputs with, and the six-argument one is what {@code
 * trimFields(Join)} trims <em>each side</em> with — so overriding only the first left every scan
 * under a join keeping its ordering columns, which is most of the corpus.
 */
public final class ChalkFieldTrimmer extends RelFieldTrimmer {
  public ChalkFieldTrimmer(RelBuilder relBuilder) {
    super(null, relBuilder);
  }

  /**
   * The columns the parent asked for, plus the ones a correlation variable of the parent reads out
   * of this input — references into the child's row that no {@code RexInputRef} of the parent
   * names. No collation union.
   *
   * <p>The window {@code [startIndex, endIndex)} is the parent's field range this input occupies,
   * which is how a join says which of its two sides a correlated reference belongs to; a field at
   * index {@code i} inside it is the input's own field {@code i - startIndex}. The four-argument
   * caller has one input and passes the whole range, which is Calcite's own unbounded behaviour
   * there.
   */
  private TrimResult trimOneChild(
      RelNode rel,
      RelNode input,
      int startIndex,
      int endIndex,
      ImmutableBitSet fieldsUsed,
      Set<RelDataTypeField> extraFields) {
    ImmutableBitSet.Builder used = fieldsUsed.rebuild();
    for (CorrelationId correlation : rel.getVariablesSet()) {
      rel.accept(
          new CorrelationReferenceFinder() {
            @Override
            protected RexNode handle(RexFieldAccess fieldAccess) {
              RexCorrelVariable variable = (RexCorrelVariable) fieldAccess.getReferenceExpr();
              int index = fieldAccess.getField().getIndex();
              if (variable.id.equals(correlation) && index >= startIndex && index < endIndex) {
                used.set(index - startIndex);
              }
              return fieldAccess;
            }
          });
    }

    return dispatchTrimFields(input, used.build(), extraFields);
  }

  /** The single-input overload: {@code Aggregate}, {@code Filter}, {@code Project}, and the rest. */
  @Override
  protected TrimResult trimChild(
      RelNode rel, RelNode input, ImmutableBitSet fieldsUsed, Set<RelDataTypeField> extraFields) {
    return trimOneChild(rel, input, 0, Integer.MAX_VALUE, fieldsUsed, extraFields);
  }

  /** The windowed overload, which only {@code trimFields(Join)} calls, once per side. */
  @Override
  protected TrimResult trimChild(
      RelNode rel,
      RelNode input,
      int startIndex,
      int endIndex,
      ImmutableBitSet fieldsUsed,
      Set<RelDataTypeField> extraFields) {
    return trimOneChild(rel, input, startIndex, endIndex, fieldsUsed, extraFields);
  }
}
