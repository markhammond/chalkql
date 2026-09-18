package chalk.planner.plan.rel;

import chalk.planner.catalog.UserFunction;
import chalk.planner.plan.ChalkConvention;
import chalk.planner.plan.CostModel;
import com.google.common.collect.ImmutableList;
import java.lang.reflect.Type;
import java.util.List;
import java.util.Set;
import org.apache.calcite.plan.DeriveMode;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCost;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.TableFunctionScan;
import org.apache.calcite.rel.metadata.RelColumnMapping;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexNode;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A client-bodied table function, as a leaf (D78, docs/design/17-user-defined-functions.md §2). The
 * host's implementation is called once per execution with the argument values and produces the rows
 * of the declared {@code RETURNS TABLE}; a SQL-bodied table function never gets here, because a
 * macro has already become an ordinary sub-query.
 */
public final class ChalkTableFunctionScan extends TableFunctionScan implements ChalkRel {
  private final UserFunction declaration;

  private ChalkTableFunctionScan(
      RelOptCluster cluster,
      RelTraitSet traits,
      RexNode call,
      RelDataType rowType,
      UserFunction declaration) {
    super(cluster, traits, ImmutableList.of(), call, null, rowType, null);
    this.declaration = declaration;
  }

  public static ChalkTableFunctionScan create(
      RelOptCluster cluster, RexNode call, RelDataType rowType, UserFunction declaration) {
    return new ChalkTableFunctionScan(
        cluster, cluster.traitSetOf(ChalkConvention.LOCAL), call, rowType, declaration);
  }

  public UserFunction declaration() {
    return declaration;
  }

  @Override
  public TableFunctionScan copy(
      RelTraitSet traitSet,
      List<RelNode> inputs,
      RexNode rexCall,
      @Nullable Type elementType,
      RelDataType rowType,
      @Nullable Set<RelColumnMapping> columnMappings) {
    return new ChalkTableFunctionScan(getCluster(), traitSet, rexCall, rowType, declaration);
  }

  /** A leaf: nothing below it can offer or be offered an ordering. */
  @Override
  public DeriveMode getDeriveMode() {
    return DeriveMode.PROHIBITED;
  }

  @Override
  public double estimateRowCount(RelMetadataQuery mq) {
    return CostModel.tableFunctionRows(declaration);
  }

  @Override
  public @Nullable RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq) {
    double rows = estimateRowCount(mq);
    return planner.getCostFactory().makeCost(rows, CostModel.tableFunctionCost(declaration, rows), 0);
  }
}
