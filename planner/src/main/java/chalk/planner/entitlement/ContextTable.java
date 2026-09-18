package chalk.planner.entitlement;

import chalk.planner.types.TypeMapper;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.schema.Statistic;
import org.apache.calcite.schema.Statistics;
import org.apache.calcite.schema.impl.AbstractTable;

/**
 * One of the execution context's relations, as a table the validator can resolve
 * (docs/design/16-entitlements.md §2 and §4).
 *
 * <p>It exists for the two bindings the planner cannot fold into literals: a relation the host bound
 * as a table, and a list too large for the fold ceiling. Its <em>rows</em> never reach the plan —
 * only its name and its row type do, which is what keeps ten thousand tenancy identifiers out of the
 * digest — and the executor materialises the host's own binding at execution start.
 */
public final class ContextTable extends AbstractTable {
  private final BoundContext.Relation relation;

  ContextTable(BoundContext.Relation relation) {
    this.relation = relation;
  }

  /** The name the host bound it under, which is what the executor looks it up by. */
  public String contextName() {
    return relation.name();
  }

  public BoundContext.Relation relation() {
    return relation;
  }

  @Override
  public RelDataType getRowType(RelDataTypeFactory typeFactory) {
    return new TypeMapper(typeFactory).toCalciteRowType(relation.rowType());
  }

  /**
   * The row count is the binding's own, which the host sent: an estimate that is exact under
   * prepare-time binding and the host's claim otherwise. Nothing else is claimed — no keys, no
   * collation — because a context relation is a set the host handed over and not a table anyone
   * declared constraints on.
   */
  @Override
  public Statistic getStatistic() {
    return Statistics.of(relation.rows().size(), java.util.List.of());
  }
}
