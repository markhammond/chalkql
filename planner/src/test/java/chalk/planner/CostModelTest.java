package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import chalk.planner.plan.ChalkRelMetadata;
import chalk.planner.plan.ChalkSelectivity;
import chalk.planner.plan.CostModel;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import chalk.ir.v1.Index;
import chalk.planner.plan.IndexMatcher;
import chalk.planner.plan.rel.ChalkFilter;
import chalk.planner.plan.rel.ChalkIndexLookup;
import chalk.planner.plan.rel.ChalkTableScan;
import java.nio.file.Files;
import java.util.List;
import org.apache.calcite.jdbc.JavaTypeFactoryImpl;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.util.ImmutableBitSet;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * The single most common Calcite mistake is a rule that fires and a plan that is then ignored
 * because the cost metadata is missing (rev 3 §6 M2, Risks). So the cost is tested directly:
 * {@code mq.getRowCount(indexLookup)} must be materially below the scan's for a selective predicate,
 * and at or above it for one that is not.
 */
class CostModelTest {

  @BeforeAll
  static void requireRecordedCatalog() {
    assumeTrue(
        Files.exists(CorpusQueries.corpusDir().resolve("schemas/corpus.binpb")),
        "the recorded corpus catalog carries the statistics the cost model reads");
  }

  @Test
  void an_equality_on_an_indexed_column_costs_less_than_the_scan_it_replaces() {
    Planned planned = plan("SELECT * FROM bars WHERE symbol = 'BTCUSDT'");

    // One symbol of five: the distinct count is exact, so this is a measurement, not a guess.
    assertThat(planned.lookupRows()).isEqualTo(100_800 / 5.0);
    assertThat(planned.lookupRows()).isLessThan(planned.scanRows());
    assertThat(planned.lookup().selectivity().guessed()).isFalse();
  }

  @Test
  void an_equality_prefix_with_a_range_costs_less_still() {
    Planned planned =
        plan("SELECT * FROM bars WHERE symbol = 'BTCUSDT' AND ts >= TIMESTAMP '2026-01-14 00:00:00'");

    assertThat(planned.lookupRows()).isLessThan(planned.scanRows() / 10);
    assertThat(planned.lookup().selectivity().guessed()).isFalse();
  }

  @Test
  void a_unique_equality_on_the_whole_key_is_one_row() {
    Planned planned =
        plan("SELECT * FROM bars WHERE symbol = 'BTCUSDT' AND ts = TIMESTAMP '2026-01-03 00:00:00'");

    assertThat(planned.lookup().index().getUnique()).isTrue();
    assertThat(planned.lookupRows()).isEqualTo(1.0);
  }

  @Test
  void a_range_that_selects_most_of_the_table_costs_more_than_the_scan() {
    // ~93 % of the 14 days of bars. The selectivity handler is what makes this a scan; if it were
    // not consulted, RelMdUtil.guessSelectivity would call it 0.25 and the lookup would win.
    Planned planned = plan("SELECT COUNT(*) FROM bars WHERE ts >= TIMESTAMP '2026-01-02 00:00:00'");

    assertThat(planned.lookupRows()).isGreaterThanOrEqualTo(planned.scanRows() * 0.9);
    assertThat(planned.physical().toString()).doesNotContain("ChalkIndexLookup");
  }

  @Test
  void the_same_range_seven_percent_in_costs_less_than_the_scan() {
    Planned planned = plan("SELECT COUNT(*) FROM bars WHERE ts >= TIMESTAMP '2026-01-14 00:00:00'");

    assertThat(planned.lookupRows()).isLessThan(planned.scanRows() * 0.1);
    assertThat(planned.physical().toString()).contains("ChalkIndexLookup");
  }

  @Test
  void a_lookup_reports_the_index_key_order_and_what_the_pinned_prefix_implies() {
    Planned planned =
        plan("SELECT * FROM bars WHERE symbol = 'BTCUSDT' AND ts >= TIMESTAMP '2026-01-14 00:00:00'");

    RelMetadataQuery mq = planned.mq();
    List<RelCollation> collations = mq.collations(planned.lookup());

    // The key order, the suffix left when the pinned column is dropped, and the interleaving that
    // puts the pinned column last — which is the table's own declared collation.
    assertThat(collations.stream().map(Object::toString))
        .containsExactly("[0, 1]", "[1]", "[1, 0]");
  }

  @Test
  void a_unique_index_makes_its_key_columns_unique() {
    Planned planned = plan("SELECT * FROM bars WHERE symbol = 'BTCUSDT'");

    assertThat(planned.mq().areColumnsUnique(planned.lookup(), ImmutableBitSet.of(0, 1))).isTrue();
    assertThat(planned.mq().areColumnsUnique(planned.lookup(), ImmutableBitSet.of(5))).isNotEqualTo(Boolean.TRUE);
  }

  @Test
  void distinct_row_counts_come_from_the_catalog() {
    Planned planned = plan("SELECT * FROM bars WHERE symbol = 'BTCUSDT'");

    // symbol is an index key column, so its distinct count is exact.
    assertThat(planned.mq().getDistinctRowCount(planned.scan(), ImmutableBitSet.of(0), null))
        .isEqualTo(5.0);
  }

  /**
   * ADR 0018's first fix. Calcite's distinct-aggregate expansion joins the per-column aggregates on
   * the grouping keys and writes those equalities null-safely; before this they were guessed at a
   * quarter each rather than at the seventh an equality gets, and the guesses compounded.
   */
  @Test
  void a_null_safe_equality_join_key_is_as_selective_as_the_equality_it_is() {
    RelDataTypeFactory types = new JavaTypeFactoryImpl(chalk.planner.types.ChalkTypeSystem.INSTANCE);
    RexBuilder rex = new RexBuilder(types);
    RelDataType key = types.createTypeWithNullability(types.createSqlType(SqlTypeName.BIGINT), true);
    RexNode left = rex.makeInputRef(key, 0);
    RexNode right = rex.makeInputRef(key, 1);

    RexNode equality = rex.makeCall(SqlStdOperatorTable.EQUALS, left, right);
    RexNode nullSafe = rex.makeCall(SqlStdOperatorTable.IS_NOT_DISTINCT_FROM, left, right);

    assertThat(ChalkSelectivity.ofJoinCondition(nullSafe, rex))
        .isEqualTo(ChalkSelectivity.ofJoinCondition(equality, rex))
        .isEqualTo(0.15);

    // Two keys, one written each way — the shape the expansion writes for a two-column grouping.
    RexNode second =
        rex.makeCall(
            SqlStdOperatorTable.IS_NOT_DISTINCT_FROM,
            rex.makeInputRef(key, 2),
            rex.makeInputRef(key, 3));
    RexNode mixed = rex.makeCall(SqlStdOperatorTable.AND, equality, second);
    assertThat(ChalkSelectivity.ofJoinCondition(mixed, rex)).isEqualTo(0.15 * 0.15);

    // Everything else is still Calcite's own guess, untouched.
    RexNode range = rex.makeCall(SqlStdOperatorTable.LESS_THAN, left, right);
    assertThat(ChalkSelectivity.ofJoinCondition(range, rex)).isEqualTo(0.5);
    assertThat(ChalkSelectivity.ofJoinCondition(null, rex)).isEqualTo(1.0);
  }

  /** The same rule on a scan predicate, where real statistics stand in for the guess. */
  @Test
  void a_null_safe_equality_on_a_column_uses_the_columns_statistics() {
    ChalkTableScan scan = plan("SELECT * FROM bars WHERE symbol = 'BTCUSDT'").scan();
    RexBuilder rex = scan.getCluster().getRexBuilder();
    RexNode nullSafe =
        rex.makeCall(
            SqlStdOperatorTable.IS_NOT_DISTINCT_FROM,
            rex.makeInputRef(scan.getRowType().getFieldList().get(0).getType(), 0),
            rex.makeLiteral("BTCUSDT"));

    // One symbol of five, the same measurement `symbol = 'BTCUSDT'` gets — not a guess.
    ChalkSelectivity.Estimate estimate =
        ChalkSelectivity.of(nullSafe, scan.chalkTable(), scan.projection(), rex);
    assertThat(estimate.value()).isEqualTo(1 / 5.0);
    assertThat(estimate.guessed()).isFalse();
  }

  @Test
  void the_defaults_make_a_lookup_win_below_half_the_table_and_lose_above_it() {
    // The claim 11-m2-index-support.md §4 makes about the numbers, checked against the numbers.
    CostModel model = CostModel.defaults();
    double rows = 100_800;

    double scanAndFilter = model.scan(rows, 1.0) + rows;
    assertThat(model.lookup(1, rows * 0.45)).isLessThan(scanAndFilter);
    assertThat(model.lookup(1, rows * 0.55)).isGreaterThan(scanAndFilter);
  }

  /** One planning run, with both alternatives in hand for comparison. */
  // ---- F14: join cardinality from uniqueness, foreign keys and distinct counts ----

  /**
   * The number that motivated F14. {@code m4-window/20} joins two aggregates of {@code lineitem}
   * on {@code l_orderkey}, which each aggregate makes unique; Calcite's guess multiplied 14 986 by
   * 14 986 and by 0.15 and reported 3.4 × 10^7 for the first join and 7.6 × 10^10 for the second.
   */
  @Test
  void a_join_on_keys_unique_on_both_sides_cannot_exceed_either_input() {
    PlannedJoin planned =
        joinFor(
            "SELECT l_orderkey, COUNT(DISTINCT l_partkey) AS parts, "
                + "COUNT(DISTINCT l_suppkey) AS suppliers, SUM(l_quantity) AS quantity "
                + "FROM lineitem GROUP BY l_orderkey ORDER BY l_orderkey");

    double left = planned.mq().getRowCount(planned.join().getLeft());
    double right = planned.mq().getRowCount(planned.join().getRight());
    assertThat(planned.rows()).isLessThanOrEqualTo(Math.max(left, right));
    assertThat(planned.rows()).isLessThanOrEqualTo(15_000.0);
  }

  /**
   * A declared foreign key plus a unique parent key is not a bound but a measurement: every
   * {@code lineitem} row matches exactly one {@code orders} row, so the join is the child's rows.
   */
  @Test
  void a_foreign_key_to_a_unique_parent_key_produces_the_childs_rows() {
    PlannedJoin planned =
        joinFor("SELECT l_orderkey FROM lineitem JOIN orders ON l_orderkey = o_orderkey");

    double child = planned.mq().getRowCount(planned.join().getLeft());
    assertThat(planned.rows()).isEqualTo(child);
  }

  /**
   * Uniqueness on one side without a foreign key is only an upper bound, so the estimate is the
   * smaller of that bound and what the distinct counts say — never the product of the inputs.
   */
  @Test
  void a_unique_key_on_one_side_bounds_the_join_by_the_other_side() {
    PlannedJoin planned =
        joinFor("SELECT e.id FROM events e JOIN symbols s ON e.symbol = s.symbol");

    double left = planned.mq().getRowCount(planned.join().getLeft());
    double right = planned.mq().getRowCount(planned.join().getRight());
    assertThat(planned.rows()).isLessThanOrEqualTo(Math.max(left, right));
    assertThat(planned.rows()).isLessThan(left * right);
  }

  /**
   * Neither side unique and no foreign key between them: {@code left × right / max(ndv, ndv)}, from
   * the distinct counts the catalog carries. Five symbols on both sides, so a fifth of the product.
   */
  @Test
  void without_uniqueness_or_a_foreign_key_the_distinct_counts_decide() {
    PlannedJoin planned =
        joinFor("SELECT b.ts FROM bars b JOIN funding f ON b.symbol = f.symbol");

    double left = planned.mq().getRowCount(planned.join().getLeft());
    double right = planned.mq().getRowCount(planned.join().getRight());
    assertThat(planned.rows()).isEqualTo((left * right) / 5.0);
  }

  /** No equi-key at all, so Calcite's own guess stands — the last branch, unchanged. */
  @Test
  void a_join_with_no_equi_key_keeps_calcites_guess() {
    PlannedJoin planned =
        joinFor("SELECT b.ts FROM bars_small b JOIN funding f ON b.ts > f.ts");

    double left = planned.mq().getRowCount(planned.join().getLeft());
    double right = planned.mq().getRowCount(planned.join().getRight());
    assertThat(planned.rows()).isEqualTo(left * right * 0.5);
  }

  /** A join condition's selectivity is the same number the row count is built from. */
  @Test
  void the_selectivity_handler_agrees_with_the_row_count_handler() {
    PlannedJoin planned =
        joinFor("SELECT b.ts FROM bars b JOIN funding f ON b.symbol = f.symbol");

    double selectivity =
        planned.mq().getSelectivity(planned.join(), planned.join().getCondition());
    assertThat(selectivity).isEqualTo(1 / 5.0);
  }

  private record PlannedJoin(Join join, RelMetadataQuery mq) {
    double rows() {
      return mq.getRowCount(join);
    }
  }

  /**
   * Plans {@code sql} with nothing pushed down — the shape is then the plain one — and hands back
   * the outermost join with a metadata query over Chalk's own chain.
   */
  private static PlannedJoin joinFor(String sql) {
    CorpusPlanner planner = new CorpusPlanner();
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            planner.catalog(), PushdownPolicy.none(), SqlConfigs.DEFAULT_CONFORMANCE)) {
      RelNode physical = pipeline.plan(sql, true).physical();
      Join join = find(physical, Join.class);
      assertThat(join).as("a join for: " + sql).isNotNull();

      join.getCluster().setMetadataProvider(ChalkRelMetadata.SOURCE);
      join.getCluster().invalidateMetadataQuery();
      return new PlannedJoin(join, RelMetadataQuery.instance());
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException("planning failed for: " + sql, e);
    }
  }

  private record Planned(RelNode physical, ChalkTableScan scan, ChalkIndexLookup lookup, RelMetadataQuery mq) {
    double scanRows() {
      return mq.getRowCount(scan);
    }

    double lookupRows() {
      return mq.getRowCount(lookup);
    }
  }

  /**
   * Plans the query twice: once at {@code FULL}, which is the choice under test, and once at
   * {@code NONE}, which always gives the {@code Filter(Scan)} shape. The lookup alternative is then
   * built from that scan and that condition through the same matcher the rule uses, on the same
   * cluster — so the two row counts being compared are the ones the optimiser saw.
   */
  private static Planned plan(String sql) {
    CorpusPlanner planner = new CorpusPlanner();
    try (PlannerPipeline chosen =
            PlannerPipeline.create(planner.catalog(), PushdownPolicy.full(), SqlConfigs.DEFAULT_CONFORMANCE);
        PlannerPipeline reference =
            PlannerPipeline.create(planner.catalog(), PushdownPolicy.none(), SqlConfigs.DEFAULT_CONFORMANCE)) {
      RelNode physical = chosen.plan(sql, true).physical();
      RelNode unpushed = reference.plan(sql, true).physical();

      ChalkFilter filter = find(unpushed, ChalkFilter.class);
      assertThat(filter).as("a Filter(Scan) at NONE for: " + sql).isNotNull();
      ChalkTableScan scan = find(filter, ChalkTableScan.class);
      assertThat(scan).as("a scan under it").isNotNull();

      scan.getCluster().setMetadataProvider(ChalkRelMetadata.SOURCE);
      scan.getCluster().invalidateMetadataQuery();

      ChalkIndexLookup lookup = lookupFor(scan, filter);
      assertThat(lookup).as("an index alternative for: " + sql).isNotNull();

      return new Planned(physical, scan, lookup, RelMetadataQuery.instance());
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException("planning failed for: " + sql, e);
    }
  }

  /** The lookup the rule would build: the first index the matcher can use. */
  private static ChalkIndexLookup lookupFor(ChalkTableScan scan, ChalkFilter filter) {
    List<Integer> projection = scan.projection();
    for (Index index : scan.chalkTable().indexes()) {
      List<Integer> keyFields = new java.util.ArrayList<>(index.getColumnsCount());
      for (int column : index.getColumnsList()) {
        keyFields.add(projection.indexOf(column));
      }

      IndexMatcher.Result result =
          IndexMatcher.split(
              filter.getCondition(),
              keyFields,
              scan.getRowType(),
              index.getKind() == chalk.ir.v1.IndexKind.INDEX_KIND_HASH,
              scan.getCluster().getRexBuilder());
      if (!result.matched()) {
        continue;
      }

      return ChalkIndexLookup.create(
          scan,
          index,
          result.ranges(),
          chalk.planner.plan.ChalkSelectivity.of(
              result.residual() == null ? filter.getCondition() : null,
              scan.chalkTable(),
              projection,
              scan.getCluster().getRexBuilder()));
    }

    return null;
  }

  private static <T extends RelNode> T find(RelNode root, Class<T> type) {
    if (type.isInstance(root)) {
      return type.cast(root);
    }

    for (RelNode input : root.getInputs()) {
      T found = find(input, type);
      if (found != null) {
        return found;
      }
    }

    return null;
  }
}
