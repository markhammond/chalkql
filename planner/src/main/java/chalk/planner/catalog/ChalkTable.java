package chalk.planner.catalog;

import chalk.ir.v1.Column;
import chalk.ir.v1.ColumnStatistics;
import chalk.ir.v1.CostProfile;
import chalk.ir.v1.ForeignKey;
import chalk.ir.v1.Index;
import chalk.ir.v1.KeyOrder;
import chalk.ir.v1.RowCountKind;
import chalk.ir.v1.Schema;
import chalk.ir.v1.SourceKind;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableCollation;
import chalk.planner.types.TypeMapper;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.DataContext;
import org.apache.calcite.linq4j.Enumerable;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollations;
import org.apache.calcite.rel.RelFieldCollation;
import org.apache.calcite.rel.RelReferentialConstraint;
import org.apache.calcite.rel.RelReferentialConstraintImpl;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.schema.ScannableTable;
import org.apache.calcite.schema.Statistic;
import org.apache.calcite.schema.Statistics;
import org.apache.calcite.schema.TranslatableTable;
import org.apache.calcite.schema.impl.AbstractTable;
import org.apache.calcite.util.ImmutableBitSet;
import org.apache.calcite.util.mapping.IntPair;

/**
 * A table the client owns, as the planner sees it. Named {@code ChalkTable} rather than {@code
 * PocoTable} because the planner is source-kind agnostic (D24): the same class serves POCO
 * collections, ADO.NET schemas and anything a third party registers.
 *
 * <p>{@link #scan} throwing is deliberate and load-bearing (rev 3 §6 M1): it guarantees nobody
 * accidentally executes through Calcite. That stays true forever.
 */
public final class ChalkTable extends AbstractTable implements ScannableTable, TranslatableTable {
  private final Table descriptor;
  private final Schema schema;

  /**
   * The columns whose <em>disclosed</em> value can be NULL where the catalog declares the stored one
   * NOT NULL (16-entitlements.md §1, §3.11; F58). Empty for every unentitled table and for every
   * entitled one no rule of which can withhold or mask a NOT NULL column to NULL, which is what
   * makes {@link #getRowType} identical to {@link #declaredRowType} in those cases.
   */
  private final ImmutableBitSet widened;

  /**
   * Single-entry row-type cache. Calcite hands us the request's type factory, and {@code
   * RelOptTableImpl} asks once per planning run, so one slot is all that pays for itself; keying a
   * map on factories would retain them across requests for nothing.
   */
  private volatile RelDataTypeFactory cachedFactory;

  private volatile RelDataType cachedRowType;

  /** The declared row type under {@link #cachedFactory}; the same slot, for the same reason. */
  private volatile RelDataType cachedDeclaredRowType;

  public ChalkTable(Table descriptor, Schema schema) {
    this(descriptor, schema, ImmutableBitSet.of());
  }

  /**
   * The same table as a statement is written over: the catalog's declaration, with every column
   * {@code widened} names made nullable because some rule of this table's entitlement can hand a
   * principal a stand-in in its place (§1, D161; F58).
   */
  public ChalkTable(Table descriptor, Schema schema, ImmutableBitSet widened) {
    this.descriptor = descriptor;
    this.schema = schema;
    this.widened = widened;
  }

  public Table descriptor() {
    return descriptor;
  }

  public String sourceId() {
    return schema.getSourceId();
  }

  public SourceKind sourceKind() {
    return schema.getKind();
  }

  public String schemaName() {
    return schema.getName();
  }

  public String tableName() {
    return descriptor.getName();
  }

  /** The declared indexes, in declaration order. Empty for a table that declares none. */
  public List<Index> indexes() {
    return descriptor.getIndexesList();
  }

  /** What the source says about one column's values (D36). Never null; may say nothing. */
  public ColumnStatistics statistics(int column) {
    return column >= 0 && column < descriptor.getColumnsCount()
        ? descriptor.getColumns(column).getStatistics()
        : ColumnStatistics.getDefaultInstance();
  }

  public Column column(int index) {
    return descriptor.getColumns(index);
  }

  /** Rows, or null when the source says it does not know. */
  public Double rowCount() {
    if (descriptor.getRowCountKind() == RowCountKind.ROW_COUNT_KIND_UNKNOWN) {
      return null;
    }
    return descriptor.getRowCount() < 0 ? null : (double) descriptor.getRowCount();
  }

  /**
   * The costs this table is planned with (D38): its own profile over its schema's over the
   * planner's defaults, field by field. Zero means "inherit", which is what an unset field is.
   */
  public CostProfile costProfile() {
    return CostProfile.newBuilder()
        .setScanRowCost(
            inherit(
                descriptor.getCostProfile().getScanRowCost(),
                schema.getCostProfile().getScanRowCost()))
        .setLookupSeekCost(
            inherit(
                descriptor.getCostProfile().getLookupSeekCost(),
                schema.getCostProfile().getLookupSeekCost()))
        .setLookupRowCost(
            inherit(
                descriptor.getCostProfile().getLookupRowCost(),
                schema.getCostProfile().getLookupRowCost()))
        .setRemoteCallCost(
            inherit(
                descriptor.getCostProfile().getRemoteCallCost(),
                schema.getCostProfile().getRemoteCallCost()))
        .setRemoteRowCost(
            inherit(
                descriptor.getCostProfile().getRemoteRowCost(),
                schema.getCostProfile().getRemoteRowCost()))
        .build();
  }

  private static double inherit(double table, double schema) {
    return table != 0 ? table : schema;
  }

  /**
   * V-M2-1: {@code toRel} returns a rel in {@code ChalkConvention.LOCAL} directly, so the leaf of
   * every tree is a Chalk rel from the start rather than a {@code LogicalTableScan} a converter rule
   * has to rewrite. {@link chalk.planner.plan.rules.ChalkTableScanRule} stays for the
   * {@code LogicalTableScan}s other paths still create — {@code RelFieldTrimmer} makes them.
   */
  @Override
  public RelNode toRel(RelOptTable.ToRelContext context, RelOptTable relOptTable) {
    // A partitioned table is the union of its partitions, each of which is a real table in its own
    // source (D106). The expansion happens here, before there is an optimiser, so that pruning and
    // pushdown both see ordinary scans of ordinary tables.
    if (descriptor.hasPartitioning()) {
      return partitionedScan(context, relOptTable);
    }

    // A table that can be queried has two futures — the client scans it, or the source runs a query
    // over it — and which one it gets is the request's pushdown level, not the catalog's business.
    // So it starts logical and the two converter rules compete; exactly one of them is registered
    // for any given request (D83, PushdownRules).
    if (takesQueries()) {
      return org.apache.calcite.rel.logical.LogicalTableScan.create(
          context.getCluster(), relOptTable, com.google.common.collect.ImmutableList.of());
    }
    return chalk.planner.plan.rel.ChalkTableScan.create(context.getCluster(), relOptTable);
  }

  /** Whether this table carries a row-and-column entitlement (step 26, 16-entitlements.md §1). */
  public boolean isEntitled() {
    return descriptor.hasEntitlement();
  }

  /**
   * Whether the host trusts this source's own row security (D156), in which case the entitlement
   * pass skips {@code Filter_R} for its tables and nothing else — the disclosures are still Chalk's.
   */
  public boolean trustsSourceRowSecurity() {
    return schema.getTrustSourceRowSecurity();
  }

  /** Whether this table's rows live in several physical tables (D106). */
  public boolean isPartitioned() {
    return descriptor.hasPartitioning();
  }

  /** The partitioning descriptor, or the default instance when there is none. */
  public chalk.ir.v1.Partitioning partitioning() {
    return descriptor.getPartitioning();
  }

  /**
   * The union of this table's partitions, each a scan of the physical table it names, together with
   * the partition value each holds so {@code PartitionPruneRule} can drop the ones a predicate
   * cannot match.
   *
   * <p>A partition is resolved as {@code <source's schema>.<table>} against the same
   * {@code RelOptSchema} the logical table came from, which is why a partition may live in another
   * source at all: nothing here knows or cares which one, and the per-source pushdown rules
   * convert each branch on its own.
   */
  private RelNode partitionedScan(RelOptTable.ToRelContext context, RelOptTable relOptTable) {
    chalk.ir.v1.Partitioning partitioning = descriptor.getPartitioning();
    List<RelNode> inputs = new ArrayList<>(partitioning.getPartitionsCount());
    List<org.apache.calcite.rex.RexNode> values =
        new ArrayList<>(partitioning.getPartitionsCount());
    org.apache.calcite.rex.RexBuilder rex = context.getCluster().getRexBuilder();

    for (chalk.ir.v1.Partition partition : partitioning.getPartitionsList()) {
      RelOptTable physical = partitionTable(relOptTable, partition);
      inputs.add(physical.toRel(context));
      values.add(
          partition.hasValue()
              ? chalk.planner.ir.IrLiterals.literal(rex, partition.getValue(), relOptTable
                  .getRowType()
                  .getFieldList()
                  .get(partitioning.getPartitionColumn())
                  .getType())
              : null);
    }

    return chalk.planner.plan.rel.ChalkPartitionedScan.logical(
        context.getCluster(),
        inputs,
        values,
        partitioning.getPartitionColumn(),
        relOptTable.getRowType(),
        tableName());
  }

  /** The physical table one partition names, resolved through the catalog the logical table is in. */
  private RelOptTable partitionTable(RelOptTable logical, chalk.ir.v1.Partition partition) {
    org.apache.calcite.plan.RelOptSchema schema = logical.getRelOptSchema();
    if (schema == null) {
      throw new InvalidCatalogException(
          "tables." + tableName(),
          "a partitioned table can only be expanded through a catalog, and this one has none.");
    }
    String schemaName = partition.getSourceId();
    RelOptTable found = schema.getTableForMember(ImmutableList.of(schemaName, partition.getTable()));
    if (found == null) {
      throw new InvalidCatalogException(
          "tables." + tableName(),
          "partition '"
              + schemaName
              + "."
              + partition.getTable()
              + "' names a table this catalog does not have.");
    }
    return found;
  }

  /** Whether this table's source accepts a query at all, rather than only a scan (D82). */
  public boolean takesQueries() {
    return schema.getKind() == SourceKind.SOURCE_KIND_REMOTE
        && (schema.getCapabilities().getQueryLanguage()
                == chalk.ir.v1.QueryLanguage.QUERY_LANGUAGE_SQL
            || schema.getCapabilities().getQueryLanguage()
                == chalk.ir.v1.QueryLanguage.QUERY_LANGUAGE_IR);
  }

  /**
   * The row type a statement over this table is written against: the catalog's declaration, with a
   * column any rule of the entitlement can withhold or mask to NULL made nullable (§1, D161; F58).
   *
   * <p>It is what the validator, the converter and therefore every expression the statement writes
   * see, which is the whole point: a predicate over a column that is going to arrive as a stand-in
   * must be simplified under the stand-in's nullability and not the stored value's. The stored
   * value's own type is {@link #declaredRowType}, and that is what the scan beneath the entitled
   * leaf reads and what the {@code Read} carries to the source.
   */
  @Override
  public RelDataType getRowType(RelDataTypeFactory typeFactory) {
    RelDataType declared = declaredRowType(typeFactory);
    if (widened.isEmpty()) {
      return declared;
    }
    RelDataType cached = cachedRowType;
    if (cached != null && cachedFactory == typeFactory) {
      return cached;
    }
    RelDataTypeFactory.Builder builder = typeFactory.builder();
    for (int i = 0; i < declared.getFieldCount(); i++) {
      RelDataTypeField field = declared.getFieldList().get(i);
      builder.add(
          field.getName(),
          widened.get(i)
              ? chalk.planner.types.TypeMapper.nullable(typeFactory, field.getType())
              : field.getType());
    }
    RelDataType rowType = builder.build();
    cachedRowType = rowType;
    cachedFactory = typeFactory;
    return rowType;
  }

  /** The row type the catalog declares: what the source stores, before any entitlement (F58). */
  public RelDataType declaredRowType(RelDataTypeFactory typeFactory) {
    RelDataType cached = cachedDeclaredRowType;
    if (cached != null && cachedFactory == typeFactory) {
      return cached;
    }
    RelDataType rowType = new TypeMapper(typeFactory).toCalciteRow(descriptor.getColumnsList());
    cachedDeclaredRowType = rowType;
    cachedRowType = null;
    cachedFactory = typeFactory;
    return rowType;
  }

  /** The columns {@link #getRowType} widens over {@link #declaredRowType}; empty for most tables. */
  public ImmutableBitSet widenedColumns() {
    return widened;
  }

  /** The declared referential constraints, in declaration order. Empty when none is declared (F14). */
  public List<ForeignKey> foreignKeys() {
    return descriptor.getForeignKeysList();
  }

  /**
   * Real statistics from the outset — this is Chalk's reason to exist (rev 3 §0). Argument order is
   * (rowCount, keys, referentialConstraints, collations); rev 3's snippet had the last two swapped.
   *
   * <p>Since step 20 the referential constraints are the declared foreign keys rather than an empty
   * list (F14). Calcite's own join-elimination rules read {@code areColumnsUnique} rather than these
   * (verified against 1.42), so what actually consumes them is Chalk's join {@code RowCount} handler
   * — but they are the standard place for the fact and cost nothing to publish.
   */
  @Override
  public Statistic getStatistic() {
    Double rowCount = descriptor.getRowCount() < 0 ? null : (double) descriptor.getRowCount();

    List<ImmutableBitSet> keys = new ArrayList<>(descriptor.getUniqueKeysCount());
    for (chalk.ir.v1.UniqueKey key : descriptor.getUniqueKeysList()) {
      ImmutableBitSet.Builder bits = ImmutableBitSet.builder();
      key.getColumnsList().forEach(bits::set);
      keys.add(bits.build());
    }

    List<RelCollation> collations = new ArrayList<>(descriptor.getCollationsCount());
    for (TableCollation collation : descriptor.getCollationsList()) {
      collations.add(toRelCollation(collation));
    }

    List<RelReferentialConstraint> constraints =
        new ArrayList<>(descriptor.getForeignKeysCount());
    for (ForeignKey key : descriptor.getForeignKeysList()) {
      List<IntPair> pairs = new ArrayList<>(key.getColumnsCount());
      for (int c = 0; c < key.getColumnsCount() && c < key.getParentColumnsCount(); c++) {
        pairs.add(IntPair.of(key.getColumns(c), key.getParentColumns(c)));
      }
      constraints.add(
          RelReferentialConstraintImpl.of(
              ImmutableList.of(schema.getName(), descriptor.getName()),
              ImmutableList.of(schema.getName(), key.getParentTable()),
              ImmutableList.copyOf(pairs)));
    }

    return Statistics.of(
        rowCount,
        ImmutableList.copyOf(keys),
        ImmutableList.copyOf(constraints),
        ImmutableList.copyOf(collations));
  }

  /**
   * Both the direction and the null direction are explicit. Calcite compares collations with {@link
   * RelFieldCollation#equals}, which includes the null direction, so a collation declared with
   * {@code UNSPECIFIED} would never satisfy an {@code ORDER BY} (V3).
   */
  public static RelCollation toRelCollation(TableCollation collation) {
    List<RelFieldCollation> fields = new ArrayList<>(collation.getKeysCount());
    for (KeyOrder key : collation.getKeysList()) {
      fields.add(toFieldCollation(key.getColumn(), key.getDirection()));
    }
    return RelCollations.of(fields);
  }

  /** Maps an IR sort direction onto Calcite's (direction, null direction) pair. */
  public static RelFieldCollation toFieldCollation(
      int fieldIndex, chalk.ir.v1.SortDirection direction) {
    return switch (direction) {
      case SORT_DIRECTION_ASC_NULLS_FIRST ->
          new RelFieldCollation(
              fieldIndex,
              RelFieldCollation.Direction.ASCENDING,
              RelFieldCollation.NullDirection.FIRST);
      case SORT_DIRECTION_ASC_NULLS_LAST ->
          new RelFieldCollation(
              fieldIndex,
              RelFieldCollation.Direction.ASCENDING,
              RelFieldCollation.NullDirection.LAST);
      case SORT_DIRECTION_DESC_NULLS_FIRST ->
          new RelFieldCollation(
              fieldIndex,
              RelFieldCollation.Direction.DESCENDING,
              RelFieldCollation.NullDirection.FIRST);
      case SORT_DIRECTION_DESC_NULLS_LAST ->
          new RelFieldCollation(
              fieldIndex,
              RelFieldCollation.Direction.DESCENDING,
              RelFieldCollation.NullDirection.LAST);
      case SORT_DIRECTION_UNSPECIFIED, UNRECOGNIZED ->
          throw new InvalidCatalogException(
              "collation", "a sort direction is unspecified; both direction and null direction "
                  + "must be explicit or the collation can never satisfy an ORDER BY");
    };
  }

  /** Maps a Calcite field collation back to an IR sort direction. */
  public static chalk.ir.v1.SortDirection toIrDirection(RelFieldCollation collation) {
    boolean ascending = collation.getDirection().isDescending() == false;
    boolean nullsFirst =
        switch (collation.nullDirection) {
          case FIRST -> true;
          case LAST -> false;
          // Calcite leaves UNSPECIFIED on collations it derives itself; SQL's default under
          // NullCollation.HIGH is NULLS LAST for ASC and NULLS FIRST for DESC (03-planner.md §4.1).
          case UNSPECIFIED -> !ascending;
        };
    if (ascending) {
      return nullsFirst
          ? chalk.ir.v1.SortDirection.SORT_DIRECTION_ASC_NULLS_FIRST
          : chalk.ir.v1.SortDirection.SORT_DIRECTION_ASC_NULLS_LAST;
    }
    return nullsFirst
        ? chalk.ir.v1.SortDirection.SORT_DIRECTION_DESC_NULLS_FIRST
        : chalk.ir.v1.SortDirection.SORT_DIRECTION_DESC_NULLS_LAST;
  }

  @Override
  public Enumerable<Object[]> scan(DataContext root) {
    throw new UnsupportedOperationException(
        "chalk-planner never executes; execution is client-side");
  }
}
