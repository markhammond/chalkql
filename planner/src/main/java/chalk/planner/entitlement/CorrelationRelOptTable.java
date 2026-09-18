package chalk.planner.entitlement;

import java.util.List;
import org.apache.calcite.linq4j.tree.Expression;
import org.apache.calcite.plan.RelOptSchema;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelDistribution;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelReferentialConstraint;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.schema.ColumnStrategy;
import org.apache.calcite.util.ImmutableBitSet;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A {@link RelOptTable} that is its delegate in every respect but one: it says this occurrence is
 * the <b>mechanism's own</b>, read for a correlation and never for the statement
 * (docs/design/38-existential-visibility.md §0, §2, §7; D265).
 *
 * <p>A declared path's key set scans the bridge and the endpoint <em>raw</em>. That is the decision:
 * the bridge contributes existence and nothing else, its own entitlement is never consulted — which
 * is what keeps the marketplace shape acyclic where the bridge is itself entitled through the target
 * — and the endpoint contributes the key its own predicate selects. Nothing of either table leaves
 * the chain: what comes out is a key that joins back to the target's own key, a match marker, and
 * the verdict ordinals, and the target's {@code Project_D} is still the only place a raw column of
 * the <em>target</em> is read.
 *
 * <p>Clause 1 of the taint check (§3.10) refuses an entitled scan that survived the rewrite
 * unwrapped, because nothing would sanitise it. This handle is how the mechanism says "I meant
 * this": it carries no {@link DisclosureMap}, so the report, the breadcrumbs and clauses 2 and 3 see
 * nothing here at all, and a statement's <em>own</em> occurrence of the same table is entitled as
 * that table's policy says, quite separately.
 *
 * <p>Everything delegates, {@link #unwrap} included, so a {@link chalk.planner.catalog.ChalkTable}
 * is still found through it — which is how clause 3 recognises the far side of the key set's join.
 */
public final class CorrelationRelOptTable implements RelOptTable {
  private final RelOptTable delegate;

  private CorrelationRelOptTable(RelOptTable delegate) {
    this.delegate = delegate;
  }

  /** Wraps {@code delegate}, unwrapping any entitled handle it already carries. */
  public static CorrelationRelOptTable of(RelOptTable delegate) {
    EntitledRelOptTable entitled = delegate.unwrap(EntitledRelOptTable.class);
    return new CorrelationRelOptTable(entitled == null ? delegate : entitled.unentitled());
  }

  /** Whether this handle is the mechanism's own rather than a statement's leaf. */
  public static boolean isCorrelation(@Nullable RelOptTable table) {
    return table != null && table.unwrap(CorrelationRelOptTable.class) != null;
  }

  /**
   * The delegate's name behind a reserved first segment, so that <b>no</b> scan of the mechanism's
   * can ever be the same expression as a scan of the statement's (F84).
   *
   * <p>A {@code TableScan}'s digest is its qualified name and its row type, and that is all: two
   * scans of one table whose row types agree are one node to both planners, which merges them into
   * one {@code RelSet} and then shares whatever is built over them. The entitled leaf and the
   * chain's raw scan are exactly that pair — the same table, and the same row type wherever the
   * leaf's rules widen nothing — so the statement could be served the chain's raw read and the
   * chain the statement's sanitised one. Both are wrong, and the first is a leak in shape. The
   * reserved segment makes the two digests differ by construction, which is the same device
   * {@code ChalkTableScan} already uses to keep two entitled occurrences apart by their disclosure
   * maps.
   *
   * <p>It is the <b>first</b> segment and never the last, because the last is the name a source
   * knows the table by — what {@code SourceSql} writes into the remote query and what
   * {@code SelectStar} matches on. Everything that asks which table this is asks the
   * {@link chalk.planner.catalog.ChalkTable} through {@link #unwrap}, which is untouched: the IR's
   * {@code TableRef}, the taint check's origins and the report all read the real name.
   */
  @Override
  public List<String> getQualifiedName() {
    List<String> qualified = delegate.getQualifiedName();
    List<String> marked = new java.util.ArrayList<>(qualified.size() + 1);
    marked.add(chalk.planner.ReservedNames.CORRELATION);
    marked.addAll(qualified);
    return List.copyOf(marked);
  }

  @Override
  public double getRowCount() {
    return delegate.getRowCount();
  }

  @Override
  public RelDataType getRowType() {
    return delegate.getRowType();
  }

  @Override
  public @Nullable RelOptSchema getRelOptSchema() {
    return delegate.getRelOptSchema();
  }

  /**
   * A scan of this table, which is a scan of the delegate carrying this handle. Reached when a rule
   * or the field trimmer re-expands the table rather than copying an existing scan; the wrapper must
   * survive that, or clause 1 would refuse a chain it had already accepted.
   */
  @Override
  public RelNode toRel(ToRelContext context) {
    return chalk.planner.plan.rel.ChalkTableScan.create(context.getCluster(), this);
  }

  @Override
  public @Nullable List<RelCollation> getCollationList() {
    return delegate.getCollationList();
  }

  @Override
  public @Nullable RelDistribution getDistribution() {
    return delegate.getDistribution();
  }

  @Override
  public boolean isKey(ImmutableBitSet columns) {
    return delegate.isKey(columns);
  }

  @Override
  public @Nullable List<ImmutableBitSet> getKeys() {
    return delegate.getKeys();
  }

  @Override
  public @Nullable List<RelReferentialConstraint> getReferentialConstraints() {
    return delegate.getReferentialConstraints();
  }

  @SuppressWarnings("rawtypes")
  @Override
  public @Nullable Expression getExpression(Class clazz) {
    return delegate.getExpression(clazz);
  }

  @Override
  public RelOptTable extend(List<RelDataTypeField> extendedFields) {
    return new CorrelationRelOptTable(delegate.extend(extendedFields));
  }

  @Override
  public List<ColumnStrategy> getColumnStrategies() {
    return delegate.getColumnStrategies();
  }

  @Override
  public <C> @Nullable C unwrap(Class<C> clazz) {
    return clazz.isInstance(this) ? clazz.cast(this) : delegate.unwrap(clazz);
  }
}
