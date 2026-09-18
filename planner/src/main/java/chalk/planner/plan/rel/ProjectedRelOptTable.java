package chalk.planner.plan.rel;

import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.linq4j.tree.Expression;
import org.apache.calcite.plan.RelOptSchema;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelDistribution;
import org.apache.calcite.rel.RelFieldCollation;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelReferentialConstraint;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.schema.ColumnStrategy;
import org.apache.calcite.util.ImmutableBitSet;
import org.apache.calcite.util.ImmutableIntList;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A {@link RelOptTable} view of a projected subset of another table's columns, with the statistics
 * remapped onto the projected positions.
 *
 * <p>This exists so that a pruned {@code ChalkTableScan} keeps *sound* metadata. Calcite's default
 * handlers ask the {@code RelOptTable} directly — {@code RelMdColumnUniqueness} calls {@link
 * #isKey}, {@code RelMdCollation} calls {@link #getCollationList} — with indexes into the scan's own
 * row. Handing them the underlying table would silently answer questions about the wrong columns,
 * which is the kind of bug that shows up much later as a wrong answer.
 */
public final class ProjectedRelOptTable implements RelOptTable {
  private final RelOptTable delegate;
  private final ImmutableIntList projection;
  private final RelDataType rowType;
  private final ImmutableList<ImmutableBitSet> keys;
  private final ImmutableList<RelCollation> collations;

  private ProjectedRelOptTable(
      RelOptTable delegate,
      ImmutableIntList projection,
      RelDataType rowType,
      ImmutableList<ImmutableBitSet> keys,
      ImmutableList<RelCollation> collations) {
    this.delegate = delegate;
    this.projection = projection;
    this.rowType = rowType;
    this.keys = keys;
    this.collations = collations;
  }

  /** Builds the projected view, or returns {@code delegate} when the projection is the identity. */
  public static RelOptTable of(RelOptTable delegate, ImmutableIntList projection) {
    if (isIdentity(delegate.getRowType(), projection)) {
      return delegate;
    }

    List<RelDataTypeField> fields = delegate.getRowType().getFieldList();
    List<RelDataTypeField> projected = new ArrayList<>(projection.size());
    for (int i = 0; i < projection.size(); i++) {
      RelDataTypeField field = fields.get(projection.get(i));
      projected.add(
          new org.apache.calcite.rel.type.RelDataTypeFieldImpl(field.getName(), i, field.getType()));
    }
    RelDataType rowType =
        new org.apache.calcite.rel.type.RelRecordType(
            org.apache.calcite.rel.type.StructKind.FULLY_QUALIFIED, projected, false);

    // A key survives only if every one of its columns is projected; a collation survives as the
    // longest prefix whose columns are all projected, since a prefix of an ordering is an ordering.
    int[] positionOf = new int[fields.size()];
    java.util.Arrays.fill(positionOf, -1);
    for (int i = 0; i < projection.size(); i++) {
      positionOf[projection.get(i)] = i;
    }

    ImmutableList.Builder<ImmutableBitSet> keys = ImmutableList.builder();
    for (ImmutableBitSet key : delegate.getKeys()) {
      ImmutableBitSet.Builder mapped = ImmutableBitSet.builder();
      boolean complete = true;
      for (int column : key) {
        if (positionOf[column] < 0) {
          complete = false;
          break;
        }
        mapped.set(positionOf[column]);
      }
      if (complete) {
        keys.add(mapped.build());
      }
    }

    ImmutableList.Builder<RelCollation> collations = ImmutableList.builder();
    for (RelCollation collation : delegate.getCollationList()) {
      List<RelFieldCollation> prefix = new ArrayList<>();
      for (RelFieldCollation field : collation.getFieldCollations()) {
        int position = positionOf[field.getFieldIndex()];
        if (position < 0) {
          break;
        }
        prefix.add(field.withFieldIndex(position));
      }
      if (!prefix.isEmpty()) {
        collations.add(RelCollations.of(prefix));
      }
    }

    return new ProjectedRelOptTable(
        delegate, projection, rowType, keys.build(), collations.build());
  }

  private static boolean isIdentity(RelDataType rowType, ImmutableIntList projection) {
    if (projection.size() != rowType.getFieldCount()) {
      return false;
    }
    for (int i = 0; i < projection.size(); i++) {
      if (projection.get(i) != i) {
        return false;
      }
    }
    return true;
  }

  /** The underlying table's column indexes, in this table's column order. */
  public ImmutableIntList projection() {
    return projection;
  }

  public RelOptTable unprojected() {
    return delegate;
  }

  @Override
  public List<String> getQualifiedName() {
    return delegate.getQualifiedName();
  }

  @Override
  public double getRowCount() {
    return delegate.getRowCount();
  }

  @Override
  public RelDataType getRowType() {
    return rowType;
  }

  @Override
  public @Nullable RelOptSchema getRelOptSchema() {
    return delegate.getRelOptSchema();
  }

  @Override
  public RelNode toRel(ToRelContext context) {
    return ChalkTableScan.create(context.getCluster(), this);
  }

  @Override
  public @Nullable List<RelCollation> getCollationList() {
    return collations;
  }

  @Override
  public @Nullable RelDistribution getDistribution() {
    return org.apache.calcite.rel.RelDistributions.ANY;
  }

  @Override
  public boolean isKey(ImmutableBitSet columns) {
    for (ImmutableBitSet key : keys) {
      if (columns.contains(key)) {
        return true;
      }
    }
    return false;
  }

  @Override
  public @Nullable List<ImmutableBitSet> getKeys() {
    return keys;
  }

  @Override
  public @Nullable List<RelReferentialConstraint> getReferentialConstraints() {
    return ImmutableList.of();
  }

  @SuppressWarnings("rawtypes")
  @Override
  public @Nullable Expression getExpression(Class clazz) {
    return delegate.getExpression(clazz);
  }

  @Override
  public RelOptTable extend(List<RelDataTypeField> extendedFields) {
    throw new UnsupportedOperationException("a projected Chalk table cannot be extended");
  }

  @Override
  public List<ColumnStrategy> getColumnStrategies() {
    List<ColumnStrategy> all = delegate.getColumnStrategies();
    List<ColumnStrategy> projected = new ArrayList<>(projection.size());
    for (int column : projection) {
      projected.add(all.get(column));
    }
    return projected;
  }

  @Override
  public <C> @Nullable C unwrap(Class<C> clazz) {
    return clazz.isInstance(this) ? clazz.cast(this) : delegate.unwrap(clazz);
  }
}
