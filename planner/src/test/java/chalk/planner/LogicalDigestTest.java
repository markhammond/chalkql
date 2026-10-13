package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.IrVersion;
import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.Column;
import chalk.ir.v1.RowCountKind;
import chalk.ir.v1.Schema;
import chalk.ir.v1.Table;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.LogicalDigest;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.rpc.PlannerServiceImpl;
import chalk.planner.rpc.v1.PlanRequest;
import chalk.planner.rpc.v1.PlanResponse;
import chalk.planner.rpc.v1.PlannerOptions;
import chalk.planner.rpc.v1.PushdownLevel;
import java.util.LinkedHashSet;
import java.util.Set;
import java.util.stream.Stream;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * The logical digest (D342, docs/design/67-plans-and-statistics.md §4): a statement's identity,
 * which statistics do not move, and which everything else that moves the plan digest does.
 *
 * <p>Statistics are moved the way a host moves them: the same catalog, once as the corpus has it
 * and once as a host has it before its first load, with every table empty and no column measured.
 */
class LogicalDigestTest {
  private static final String EMPTY = "corpus-empty";

  private static PlannerServiceImpl service;
  private static RegisteredCatalog loaded;
  private static RegisteredCatalog empty;

  @BeforeAll
  static void setUp() {
    CatalogRegistry catalogs = new CatalogRegistry();
    loaded = catalogs.register(TestCatalogs.corpus());
    empty = catalogs.register(empty(TestCatalogs.corpus()));
    service = new PlannerServiceImpl(catalogs);
  }

  /** The catalog before its first load: every table empty, no column measured. */
  private static CatalogContext empty(CatalogContext catalog) {
    CatalogContext.Builder builder = catalog.toBuilder().setContextId(EMPTY);
    for (Schema.Builder schema : builder.getSchemasBuilderList()) {
      for (Table.Builder table : schema.getTablesBuilderList()) {
        table.setRowCount(0).setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT);
        for (Column.Builder column : table.getColumnsBuilderList()) {
          column.clearStatistics();
        }
      }
    }
    return builder.build();
  }

  private static PlanResponse plan(String sql, String contextId) throws Exception {
    return service.planOrThrow(
        PlanRequest.newBuilder()
            .setSql(sql)
            .setContextId(contextId)
            .setCatalogEpoch(TestCatalogs.EPOCH)
            .setClientIrVersion(IrVersion.CURRENT)
            .setOptions(
                PlannerOptions.newBuilder()
                    .setPushdown(PushdownLevel.PUSHDOWN_LEVEL_FULL)
                    .setIncludePlanText(true))
            .build());
  }

  private static PlanResponse plan(String sql) throws Exception {
    return plan(sql, TestCatalogs.CONTEXT_ID);
  }

  /** The plan text's logical section. */
  private static String logicalText(PlanResponse response) {
    String text = response.getPlanText();
    return text.substring(text.indexOf("-- logical\n"), text.indexOf("-- physical\n"));
  }

  @Test
  void a_load_moves_the_plan_digest_and_not_the_logical_digest() throws Exception {
    String sql =
        "SELECT b.symbol, b.high, s.base FROM bars b JOIN symbols s ON b.symbol = s.symbol"
            + " WHERE b.high > 100";

    PlanResponse before = plan(sql, EMPTY);
    PlanResponse after = plan(sql);

    assertThat(after.getPlan().getPlanDigest())
        .as("the statistics chose other operators")
        .isNotEqualTo(before.getPlan().getPlanDigest());
    assertThat(after.getLogicalDigest()).isNotZero().isEqualTo(before.getLogicalDigest());
  }

  /**
   * The strategy for a {@code DISTINCT} aggregate reads statistics before cost does (D54), so the
   * plan text's logical section moves with them. The digest is taken before it.
   */
  @Test
  void a_rewrite_that_reads_statistics_moves_the_logical_text_and_not_the_digest()
      throws Exception {
    String sql = "SELECT symbol, COUNT(DISTINCT high) AS highs FROM bars GROUP BY symbol";

    PlanResponse before = plan(sql, EMPTY);
    PlanResponse after = plan(sql);

    assertThat(logicalText(after))
        .as("the rewrite chose another strategy")
        .isNotEqualTo(logicalText(before));
    assertThat(after.getLogicalDigest()).isEqualTo(before.getLogicalDigest());
  }

  @Test
  void spacing_and_keyword_case_do_not_move_it() throws Exception {
    assertThat(plan("SELECT symbol, ts FROM bars WHERE symbol = 'BTCUSDT'").getLogicalDigest())
        .isEqualTo(
            plan("select   symbol ,\n  ts from BARS where symbol='BTCUSDT'").getLogicalDigest());
  }

  /** Each of these moves the plan digest too: a different result, or a result named differently. */
  @Test
  void values_aliases_identifier_case_windows_and_tables_move_it() throws Exception {
    long statement =
        plan("SELECT symbol, high FROM bars WHERE symbol = 'BTCUSDT'").getLogicalDigest();

    assertThat(plan("SELECT symbol, high FROM bars WHERE symbol = 'ETHUSDT'").getLogicalDigest())
        .isNotEqualTo(statement);
    assertThat(
            plan("SELECT symbol, high AS top FROM bars WHERE symbol = 'BTCUSDT'")
                .getLogicalDigest())
        .isNotEqualTo(statement);
    assertThat(plan("SELECT SYMBOL, high FROM bars WHERE symbol = 'BTCUSDT'").getLogicalDigest())
        .isNotEqualTo(statement);
    assertThat(
            plan("SELECT symbol, high FROM bars_small WHERE symbol = 'BTCUSDT'")
                .getLogicalDigest())
        .isNotEqualTo(statement);

    String window =
        "SELECT symbol, SUM(high) OVER (PARTITION BY symbol ORDER BY ts ROWS %d PRECEDING) AS s"
            + " FROM bars";
    assertThat(plan(String.format(window, 2)).getLogicalDigest())
        .isNotEqualTo(plan(String.format(window, 3)).getLogicalDigest());
  }

  /**
   * The milestones the corpus catalog plans, as {@code CorrelateFreeTest} takes them: the join
   * corpus among them, whose largest statements have their joins ordered by statistics (D44).
   */
  static Stream<CorpusQueries.Query> queries() {
    return Stream.of(
            CorpusQueries.all(),
            CorpusQueries.m2(),
            CorpusQueries.m3(),
            CorpusQueries.m4(),
            CorpusQueries.m5())
        .flatMap(java.util.List::stream);
  }

  /**
   * Every corpus statement: one logical digest however often it is planned, and the same one before
   * a load and after it.
   */
  @ParameterizedTest(name = "{0}")
  @MethodSource("queries")
  void every_corpus_statement_has_one_logical_digest_whatever_the_statistics(
      CorpusQueries.Query query) throws Exception {
    Set<Long> digests = new LinkedHashSet<>();
    for (int i = 0; i < 3; i++) {
      digests.add(digest(loaded, query));
    }
    digests.add(digest(empty, query));

    assertThat(digests).as("%s, three times loaded and once empty", query.name()).hasSize(1);
  }

  private static long digest(RegisteredCatalog catalog, CorpusQueries.Query query)
      throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            catalog, PushdownPolicy.full(), query.conformance(), query.libraries())) {
      return pipeline.plan(query.sql(), false).logicalDigest();
    }
  }

  /** The text the digest is taken over writes each node's row type, so a column's type is in it. */
  @Test
  void the_text_carries_each_nodes_row_type() {
    org.apache.calcite.rel.RelNode logical = new CorpusPlanner().logical("SELECT volume FROM bars");

    assertThat(LogicalDigest.text(logical)).contains("BIGINT NOT NULL volume");
  }
}
