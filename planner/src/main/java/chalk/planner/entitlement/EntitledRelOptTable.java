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
 * A {@link RelOptTable} that is its delegate in every respect but one: it carries the disclosure map
 * the entitlement pass computed for <em>this</em> leaf occurrence
 * (docs/design/16-entitlements.md §3.8, §3.10).
 *
 * <p>This is the {@code ProjectedRelOptTable} trick applied to a different question. A node the pass
 * created has no identity after optimisation — filters merge, projections fold, a scan becomes an
 * index lookup — but every scan-derived rel copies the table handle it was built with, so a handle
 * that answers "what does this leaf disclose" reaches the gate (§3.8), the taint check (§3.10) and
 * {@code RelToIr} (the read's breadcrumbs) without any of them having to re-derive it.
 *
 * <p>Everything else delegates, {@link #unwrap} included, so a {@link chalk.planner.catalog.ChalkTable}
 * and a {@code ProjectedRelOptTable} are still found through it, and a metadata handler that asks the
 * table a question gets the same answer it would have got without the wrapper.
 *
 * <p>The row type is the one exception, and it is the same idea (F58): beneath the leaf the table is
 * the source's own, so this handle publishes the row type the <b>catalog declares</b>, while the
 * table the statement was resolved against publishes the row a statement <em>sees</em> — a column
 * any rule can withhold or mask to NULL nullable there, whatever the source stores (§1, D161).
 * {@code Project_D} is where the two meet. The two row types are the same object for every table
 * that widens nothing, which is every unentitled one.
 */
public final class EntitledRelOptTable implements RelOptTable {
  private final RelOptTable delegate;
  private final DisclosureMap disclosure;
  private final RelDataType rowType;

  private EntitledRelOptTable(
      RelOptTable delegate, DisclosureMap disclosure, RelDataType rowType) {
    this.delegate = delegate;
    this.disclosure = disclosure;
    this.rowType = rowType;
  }

  /** Wraps {@code delegate}, replacing any wrapper it already carries. */
  public static EntitledRelOptTable of(RelOptTable delegate, DisclosureMap disclosure) {
    return of(delegate, disclosure, delegate.getRowType());
  }

  /**
   * The same, publishing {@code rowType} — the table's declared row — rather than the delegate's
   * (F58). The widths agree by construction: only nullability differs.
   */
  public static EntitledRelOptTable of(
      RelOptTable delegate, DisclosureMap disclosure, RelDataType rowType) {
    RelOptTable inner =
        delegate instanceof EntitledRelOptTable entitled ? entitled.delegate : delegate;
    return new EntitledRelOptTable(inner, disclosure, rowType);
  }

  /** The map for this leaf occurrence, or null when {@code table} carries none. */
  public static @Nullable DisclosureMap disclosureOf(@Nullable RelOptTable table) {
    if (table == null) {
      return null;
    }
    EntitledRelOptTable entitled = table.unwrap(EntitledRelOptTable.class);
    return entitled == null ? null : entitled.disclosure;
  }

  public DisclosureMap disclosure() {
    return disclosure;
  }

  public RelOptTable unentitled() {
    return delegate;
  }

  /**
   * The delegate's name, and — for a table a <b>source can query</b> — a leading reserved segment
   * carrying this leaf's map (F95).
   *
   * <p>A {@code TableScan}'s digest is its qualified name and its row type and nothing else, so two
   * occurrences of one table that disclose differently are the same expression to the planner unless
   * something puts the map in it. For a table the client scans, something does: the converter makes
   * a {@link chalk.planner.plan.rel.ChalkTableScan}, whose {@code explainTerms} carries the map, and
   * the two occurrences separate there. A table that <em>takes queries</em> starts as a
   * {@code LogicalTableScan} instead, so that the two pushdown alternatives can compete for it
   * (D83), and a {@code LogicalTableScan} has nowhere to put the map at all. Measured over the
   * two-alias self-join of {@code m7-adversarial} 37 against a remote source: the two occurrences
   * unified, one {@code SourceScan} served both, and the pushed read of the organisation whose names
   * are masked carried the <em>other</em> occurrence's breadcrumbs — {@code FULL} on the wire for a
   * column the leaf masks. The rows were right; what a host and the client's own I-IR-E were told
   * about them was not, in the permissive direction.
   *
   * <p>So the handle does it, which is the device {@link CorrelationRelOptTable} already uses for the
   * same reason one convention down (ADR 0060 §2). It is the <b>first</b> segment and never the
   * last, because the last is the name a source knows the table by — what {@code SourceSql} writes
   * into the remote query and what {@code SelectStar} matches on — and everything that asks which
   * table this is asks the {@link chalk.planner.catalog.ChalkTable} through {@link #unwrap}, which is
   * untouched. It is added only where it is needed, so no plan over a table the client scans moves a
   * character.
   */
  @Override
  public List<String> getQualifiedName() {
    List<String> qualified = delegate.getQualifiedName();
    chalk.planner.catalog.ChalkTable table = unwrap(chalk.planner.catalog.ChalkTable.class);
    if (table == null || !table.takesQueries()) {
      return qualified;
    }
    List<String> marked = new java.util.ArrayList<>(qualified.size() + 1);
    marked.add(chalk.planner.ReservedNames.ENTITLED + ":" + disclosure.fingerprint());
    marked.addAll(qualified);
    return List.copyOf(marked);
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

  /**
   * A scan of this table, which is a scan of the delegate carrying this handle. Reached when a rule
   * or the field trimmer re-expands the table rather than copying an existing scan; the wrapper must
   * survive that, or the breadcrumbs would depend on which rules happened to fire.
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
    RelOptTable extended = delegate.extend(extendedFields);
    return new EntitledRelOptTable(extended, disclosure, extended.getRowType());
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
