package chalk.planner.plan;

import chalk.ir.v1.ColumnStatistics;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.plan.rel.ChalkIndexLookup;
import chalk.planner.plan.rel.ChalkTableScan;
import com.google.common.collect.ImmutableList;
import java.util.List;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollationTraitDef;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.metadata.BuiltInMetadata;
import org.apache.calcite.rel.metadata.ChainedRelMetadataProvider;
import org.apache.calcite.rel.metadata.DefaultRelMetadataProvider;
import org.apache.calcite.rel.metadata.MetadataDef;
import org.apache.calcite.rel.metadata.MetadataHandler;
import org.apache.calcite.rel.metadata.ReflectiveRelMetadataProvider;
import org.apache.calcite.rel.metadata.RelColumnOrigin;
import org.apache.calcite.rel.metadata.RelMdUtil;
import org.apache.calcite.rel.metadata.RelMetadataProvider;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.util.ImmutableBitSet;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The metadata handlers Chalk's own rels need, chained in front of Calcite's defaults
 * ({@code 11-m2-index-support.md} §4).
 *
 * <p>ADR 0001 left this chain out of M1 on the grounds that an empty one is behaviourally identical
 * to no chain. It is no longer empty, and one handler in particular is what makes the milestone
 * work: without a {@code Selectivity} handler on {@link ChalkTableScan}, every predicate would be
 * costed by {@code RelMdUtil.guessSelectivity} and an index lookup would win or lose by the shape of
 * the predicate rather than by how many rows it actually selects.
 *
 * <p>Registered on the cluster, which sets the thread's Janino provider; the provider instance is a
 * constant so the generated dispatch is compiled once for the process.
 */
public final class ChalkRelMetadata {
  private ChalkRelMetadata() {}

  /** Chalk's handlers first, Calcite's defaults behind them. */
  public static final RelMetadataProvider SOURCE =
      ChainedRelMetadataProvider.of(
          ImmutableList.of(
              ReflectiveRelMetadataProvider.reflectiveSource(
                  new RowCount(), BuiltInMetadata.RowCount.Handler.class),
              ReflectiveRelMetadataProvider.reflectiveSource(
                  new Selectivity(), BuiltInMetadata.Selectivity.Handler.class),
              ReflectiveRelMetadataProvider.reflectiveSource(
                  new DistinctRowCount(), BuiltInMetadata.DistinctRowCount.Handler.class),
              ReflectiveRelMetadataProvider.reflectiveSource(
                  new Collation(), BuiltInMetadata.Collation.Handler.class),
              ReflectiveRelMetadataProvider.reflectiveSource(
                  new ColumnUniqueness(), BuiltInMetadata.ColumnUniqueness.Handler.class),
              ReflectiveRelMetadataProvider.reflectiveSource(
                  new ColumnOrigin(), BuiltInMetadata.ColumnOrigin.Handler.class),
              ReflectiveRelMetadataProvider.reflectiveSource(
                  new Predicates(), BuiltInMetadata.Predicates.Handler.class),
              DefaultRelMetadataProvider.INSTANCE));

  /**
   * Where an index lookup's columns come from (step 26, 16-entitlements.md §3.10).
   *
   * <p>{@link ChalkIndexLookup} is an {@code AbstractRelNode} rather than a {@code TableScan} — the
   * index is its input — so Calcite's default handler answers "unknown" for it, and the taint check
   * fails a node it does not know. This is the handler that makes an index lookup a node it knows:
   * output column i is table column {@code projection().get(i)} of the lookup's own table, exactly
   * as it is for a scan.
   */
  public static final class ColumnOrigin implements MetadataHandler<BuiltInMetadata.ColumnOrigin> {
    @Override
    public MetadataDef<BuiltInMetadata.ColumnOrigin> getDef() {
      return BuiltInMetadata.ColumnOrigin.DEF;
    }

    public java.util.@Nullable Set<RelColumnOrigin> getColumnOrigins(
        ChalkIndexLookup rel, RelMetadataQuery mq, int column) {
      if (column < 0 || column >= rel.projection().size()) {
        return null;
      }
      return java.util.Set.of(
          new RelColumnOrigin(rel.getTable(), rel.projection().get(column), false));
    }

    /**
     * The source boundary is a pass-through by position: {@code RemoteQuery} returns the pushed
     * subtree's own row, and the columns beneath it are still the columns of the table the subtree
     * reads. Without this, {@code getColumnOrigins} answers "unknown" for everything a source runs
     * and the taint check's clauses 2 and 4 pass vacuously over every pushed plan (§3.10) — the one
     * place the check most needs to see through.
     */
    public java.util.@Nullable Set<RelColumnOrigin> getColumnOrigins(
        chalk.planner.plan.rel.SourceToLocalConverter rel, RelMetadataQuery mq, int column) {
      return mq.getColumnOrigins(rel.getInput(), column);
    }

    /** Offset, limit and top-N drop and reorder rows; the columns are the input's, by position. */
    public java.util.@Nullable Set<RelColumnOrigin> getColumnOrigins(
        chalk.planner.plan.rel.ChalkLimit rel, RelMetadataQuery mq, int column) {
      return mq.getColumnOrigins(rel.getInput(), column);
    }

    public java.util.@Nullable Set<RelColumnOrigin> getColumnOrigins(
        chalk.planner.plan.rel.ChalkTopN rel, RelMetadataQuery mq, int column) {
      return mq.getColumnOrigins(rel.getInput(), column);
    }

    /**
     * An aggregate's group key is the input column its <em>group set</em> names, and not the one at
     * the output position (CALCITE-4250).
     *
     * <p>Calcite's own handler read {@code getColumnOrigins(input, iOutputColumn)} for a group key,
     * which is right only while the group set is a prefix of the input row — and
     * {@code AGGREGATE_PROJECT_MERGE} is a rule whose whole purpose is to make it something else.
     * The reported symptom is an origin naming a different column of the same table, which for the
     * taint check means asking whether the <em>wrong</em> column is population-only (clause 2) or
     * withheld (clause 4): a plan that reads a withheld column passes, or a correct one is refused.
     * Chalk therefore answers for {@code Aggregate} itself rather than inheriting the answer,
     * whichever version the sidecar is pinned to.
     *
     * <p>A measure is derived from its arguments and from nothing else — its {@code FILTER} decides
     * which rows are counted rather than what the value is made of, which is how the report and the
     * client's own invariant judge an aggregate too.
     */
    public java.util.@Nullable Set<RelColumnOrigin> getColumnOrigins(
        org.apache.calcite.rel.core.Aggregate rel, RelMetadataQuery mq, int column) {
      if (column < 0 || column >= rel.getRowType().getFieldCount()) {
        return null;
      }
      if (column < rel.getGroupCount()) {
        return mq.getColumnOrigins(rel.getInput(), rel.getGroupSet().asList().get(column));
      }

      org.apache.calcite.rel.core.AggregateCall call =
          rel.getAggCallList().get(column - rel.getGroupCount());
      java.util.Set<RelColumnOrigin> origins = new java.util.LinkedHashSet<>();
      for (int argument : call.getArgList()) {
        java.util.Set<RelColumnOrigin> of = mq.getColumnOrigins(rel.getInput(), argument);
        if (of == null) {
          return null;
        }
        for (RelColumnOrigin origin : of) {
          origins.add(
              new RelColumnOrigin(origin.getOriginTable(), origin.getOriginColumnOrdinal(), true));
        }
      }
      return origins;
    }

    /**
     * A partitioned table is the union of its partitions (D106), so column i comes from column i of
     * every branch — the same shape a set operation has, and an entitled partitioned table needs it
     * for the same reason.
     */
    public java.util.@Nullable Set<RelColumnOrigin> getColumnOrigins(
        chalk.planner.plan.rel.ChalkPartitionedScan rel, RelMetadataQuery mq, int column) {
      java.util.Set<RelColumnOrigin> origins = new java.util.LinkedHashSet<>();
      for (org.apache.calcite.rel.RelNode input : rel.getInputs()) {
        java.util.Set<RelColumnOrigin> branch = mq.getColumnOrigins(input, column);
        if (branch == null) {
          return null;
        }
        origins.addAll(branch);
      }
      return origins;
    }
  }

  /**
   * What an index lookup's rows are known to satisfy (step 26, §3.10 clause 3).
   *
   * <p>A lookup <em>is</em> the predicate: its ranges are what the optimiser turned the filter into,
   * so a plan whose tenancy filter became a lookup still enforces it, and clause 3 has to be able to
   * see that. Each range becomes a conjunction of bounds on the index's leading key columns, and the
   * ranges are OR-ed; a lookup with no range constrains nothing and says so.
   */
  public static final class Predicates implements MetadataHandler<BuiltInMetadata.Predicates> {
    @Override
    public MetadataDef<BuiltInMetadata.Predicates> getDef() {
      return BuiltInMetadata.Predicates.DEF;
    }

    /**
     * The boundary's pulled-up predicates are the pushed subtree's (§3.10 clause 3). A row predicate
     * the source is running is still being enforced, and a walk that stopped at the boundary would
     * refuse every correct plan that pushed one — which is every entitled plan over a remote table.
     */
    public org.apache.calcite.plan.RelOptPredicateList getPredicates(
        chalk.planner.plan.rel.SourceToLocalConverter rel, RelMetadataQuery mq) {
      return mq.getPulledUpPredicates(rel.getInput());
    }

    /** Offset, limit and top-N drop rows and change none, so what held below holds above. */
    public org.apache.calcite.plan.RelOptPredicateList getPredicates(
        chalk.planner.plan.rel.ChalkLimit rel, RelMetadataQuery mq) {
      return mq.getPulledUpPredicates(rel.getInput());
    }

    public org.apache.calcite.plan.RelOptPredicateList getPredicates(
        chalk.planner.plan.rel.ChalkTopN rel, RelMetadataQuery mq) {
      return mq.getPulledUpPredicates(rel.getInput());
    }

    public org.apache.calcite.plan.RelOptPredicateList getPredicates(
        ChalkIndexLookup rel, RelMetadataQuery mq) {
      RexBuilder rexBuilder = rel.getCluster().getRexBuilder();
      List<RexNode> disjuncts = new java.util.ArrayList<>();
      for (chalk.planner.plan.IndexMatcher.Range range : rel.ranges()) {
        List<RexNode> bounds = new java.util.ArrayList<>();
        for (int i = 0; i < range.lower().size(); i++) {
          RexNode field = keyRef(rel, i, rexBuilder);
          if (field == null) {
            return org.apache.calcite.plan.RelOptPredicateList.EMPTY;
          }
          bounds.add(
              rexBuilder.makeCall(
                  range.lowerInclusive()
                      ? SqlStdOperatorTable.GREATER_THAN_OR_EQUAL
                      : SqlStdOperatorTable.GREATER_THAN,
                  field,
                  range.lower().get(i)));
        }
        for (int i = 0; i < range.upper().size(); i++) {
          RexNode field = keyRef(rel, i, rexBuilder);
          if (field == null) {
            return org.apache.calcite.plan.RelOptPredicateList.EMPTY;
          }
          bounds.add(
              rexBuilder.makeCall(
                  range.upperInclusive()
                      ? SqlStdOperatorTable.LESS_THAN_OR_EQUAL
                      : SqlStdOperatorTable.LESS_THAN,
                  field,
                  range.upper().get(i)));
        }
        if (bounds.isEmpty()) {
          return org.apache.calcite.plan.RelOptPredicateList.EMPTY;
        }
        disjuncts.add(org.apache.calcite.rex.RexUtil.composeConjunction(rexBuilder, bounds));
      }
      if (disjuncts.isEmpty()) {
        return org.apache.calcite.plan.RelOptPredicateList.EMPTY;
      }
      return org.apache.calcite.plan.RelOptPredicateList.of(
          rexBuilder,
          ImmutableList.of(org.apache.calcite.rex.RexUtil.composeDisjunction(rexBuilder, disjuncts)));
    }

    /** The lookup's own output position of the index's i-th key column, as a reference. */
    private static @Nullable RexNode keyRef(ChalkIndexLookup rel, int key, RexBuilder rexBuilder) {
      if (key >= rel.index().getColumnsCount()) {
        return null;
      }
      int position = rel.projection().indexOf(rel.index().getColumns(key));
      if (position < 0) {
        return null;
      }
      return rexBuilder.makeInputRef(rel, position);
    }
  }

  /** Σ range selectivities × table rows, capped at the table; 1 per range for a unique equality. */
  public static final class RowCount implements MetadataHandler<BuiltInMetadata.RowCount> {
    @Override
    public MetadataDef<BuiltInMetadata.RowCount> getDef() {
      return BuiltInMetadata.RowCount.DEF;
    }

    public @Nullable Double getRowCount(ChalkIndexLookup rel, RelMetadataQuery mq) {
      return rel.estimateRowCount(mq);
    }

    /**
     * A join's rows from uniqueness, foreign keys and distinct counts rather than from a guessed
     * selectivity times the product of the inputs (F14, {@link JoinCardinality}). This handler
     * always answers — its last branch is Calcite's own number — so the chain never falls through.
     */
    public @Nullable Double getRowCount(Join rel, RelMetadataQuery mq) {
      return JoinCardinality.rowCount(rel, mq);
    }
  }

  /**
   * How selective a predicate is over a Chalk table, from its declared statistics. Registered for
   * the scan and for the lookup so that {@code Filter(Scan)} and {@code Filter(IndexLookup)} are
   * costed with the same numbers — the comparison would be meaningless otherwise.
   */
  public static final class Selectivity implements MetadataHandler<BuiltInMetadata.Selectivity> {
    @Override
    public MetadataDef<BuiltInMetadata.Selectivity> getDef() {
      return BuiltInMetadata.Selectivity.DEF;
    }

    public @Nullable Double getSelectivity(
        ChalkTableScan rel, RelMetadataQuery mq, @Nullable RexNode predicate) {
      return ChalkSelectivity.of(
              predicate, rel.chalkTable(), rel.projection(), rel.getCluster().getRexBuilder())
          .value();
    }

    public @Nullable Double getSelectivity(
        ChalkIndexLookup rel, RelMetadataQuery mq, @Nullable RexNode predicate) {
      return ChalkSelectivity.of(
              predicate, rel.chalkTable(), rel.projection(), rel.getCluster().getRexBuilder())
          .value();
    }

    /**
     * A join condition, where an {@code IS NOT DISTINCT FROM} key counts as the equality it is
     * (ADR 0018) and an equi-key is estimated from the distinct counts on the two sides rather than
     * guessed at 0.15 (F14). The semi-join branch is Calcite's own, reproduced rather than delegated
     * so that this handler always answers and the chain never has to fall through it.
     */
    public @Nullable Double getSelectivity(
        Join rel, RelMetadataQuery mq, @Nullable RexNode predicate) {
      RexBuilder rexBuilder = rel.getCluster().getRexBuilder();
      if (rel.isSemiJoin()) {
        RexNode selectivity = RelMdUtil.makeSemiJoinSelectivityRexNode(mq, rel);
        if (predicate != null) {
          selectivity =
              rexBuilder.makeCall(SqlStdOperatorTable.AND, selectivity, predicate);
        }

        return mq.getSelectivity(rel.getLeft(), selectivity);
      }

      return JoinCardinality.selectivity(
          rel, mq, ChalkSelectivity.nullSafeEqualitiesAsEqualities(predicate, rexBuilder));
    }
  }

  /**
   * Distinct values of a column set, from {@code distinct_count}. A declared unique key answers
   * exactly; anything the source did not declare returns null, which is Calcite's "I do not know"
   * and lets its own estimate stand.
   */
  public static final class DistinctRowCount
      implements MetadataHandler<BuiltInMetadata.DistinctRowCount> {
    @Override
    public MetadataDef<BuiltInMetadata.DistinctRowCount> getDef() {
      return BuiltInMetadata.DistinctRowCount.DEF;
    }

    public @Nullable Double getDistinctRowCount(
        ChalkTableScan rel,
        RelMetadataQuery mq,
        ImmutableBitSet groupKey,
        @Nullable RexNode predicate) {
      return distinct(rel.chalkTable(), rel.getTable(), rel.projection(), mq, rel, groupKey, predicate);
    }

    public @Nullable Double getDistinctRowCount(
        ChalkIndexLookup rel,
        RelMetadataQuery mq,
        ImmutableBitSet groupKey,
        @Nullable RexNode predicate) {
      return distinct(rel.chalkTable(), rel.getTable(), rel.projection(), mq, rel, groupKey, predicate);
    }

    private static @Nullable Double distinct(
        ChalkTable table,
        RelOptTable relOptTable,
        List<Integer> fields,
        RelMetadataQuery mq,
        org.apache.calcite.rel.RelNode rel,
        ImmutableBitSet groupKey,
        @Nullable RexNode predicate) {
      double rows = mq.getRowCount(rel);
      if (groupKey.isEmpty()) {
        return 1.0;
      }

      if (relOptTable.isKey(groupKey)) {
        return rows;
      }

      double distinct = 1.0;
      for (int field : groupKey) {
        if (field < 0 || field >= fields.size()) {
          return null;
        }

        ColumnStatistics statistics = table.statistics(fields.get(field));
        if (statistics.getDistinctCount() <= 0) {
          return null;
        }

        distinct *= statistics.getDistinctCount();
        if (distinct >= rows) {
          break;
        }
      }

      double selectivity =
          predicate == null
              ? 1.0
              : ChalkSelectivity.of(
                      predicate, table, fields, rel.getCluster().getRexBuilder())
                  .value();
      return Math.max(1.0, Math.min(rows, distinct * selectivity));
    }
  }

  /** A lookup delivers the index's key order; the trait set is where that was decided. */
  public static final class Collation implements MetadataHandler<BuiltInMetadata.Collation> {
    @Override
    public MetadataDef<BuiltInMetadata.Collation> getDef() {
      return BuiltInMetadata.Collation.DEF;
    }

    public ImmutableList<RelCollation> collations(ChalkIndexLookup rel, RelMetadataQuery mq) {
      List<RelCollation> traits = rel.getTraitSet().getTraits(RelCollationTraitDef.INSTANCE);
      return traits == null ? ImmutableList.of() : ImmutableList.copyOf(traits);
    }
  }

  /** A unique index makes its key columns unique in the lookup's output. */
  public static final class ColumnUniqueness
      implements MetadataHandler<BuiltInMetadata.ColumnUniqueness> {
    @Override
    public MetadataDef<BuiltInMetadata.ColumnUniqueness> getDef() {
      return BuiltInMetadata.ColumnUniqueness.DEF;
    }

    public @Nullable Boolean areColumnsUnique(
        ChalkIndexLookup rel, RelMetadataQuery mq, ImmutableBitSet columns, boolean ignoreNulls) {
      ImmutableBitSet unique = rel.uniqueColumns();
      if (unique != null && columns.contains(unique)) {
        return true;
      }

      return rel.getTable().isKey(columns) ? Boolean.TRUE : null;
    }
  }
}
