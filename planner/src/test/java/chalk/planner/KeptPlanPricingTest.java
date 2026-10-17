package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import chalk.ir.IrVersion;
import chalk.ir.v1.RowCountKind;
import chalk.planner.InvalidArgumentException;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.rpc.PlannerServiceImpl;
import chalk.planner.rpc.v1.PlanRequest;
import chalk.planner.rpc.v1.PlanResponse;
import chalk.planner.rpc.v1.PlannerOptions;
import chalk.planner.rpc.v1.RegisterCatalogRequest;
import chalk.planner.rpc.v1.RegisterStatisticsRequest;
import chalk.planner.rpc.v1.TableStatistics;
import java.nio.file.Files;
import org.junit.jupiter.api.Test;

/**
 * Plans kept to be priced on newer numbers (design 70 §3): a request that asks for prices is told its
 * plan's cost, and the plan is kept; a later request that names the kept plan is told its cost on that
 * request's numbers, so a plan made on fewer rows is priced on more, with nothing in it rebuilt. A
 * request with a margin is sent a new plan only when it saves that much on the kept one.
 */
class KeptPlanPricingTest {
  private static final String SQL = "SELECT symbol, SUM(volume) AS volume FROM bars GROUP BY symbol";

  /**
   * A join whose plan turns over when the tables trade sizes: made on the corpus's numbers, then
   * planned again with few bars and many symbols, where the new plan saves about two fifths of what
   * the kept one costs on those numbers.
   */
  private static final String JOIN =
      "SELECT b.symbol, b.ts, s.base FROM bars b JOIN symbols s ON b.symbol = s.symbol";

  @Test
  void a_kept_plan_is_priced_on_the_numbers_of_a_later_request() throws Exception {
    CatalogRegistry catalogs = catalogs();
    PlannerServiceImpl service = new PlannerServiceImpl(catalogs);

    PlanResponse first = service.planOrThrow(request().setPricePlans(true).build());
    long digest = first.getPlan().getPlanDigest();
    assertThat(first.hasPlanCost()).isTrue();
    assertThat(first.hasKnownPlanCost()).isFalse();

    // On the same numbers, the kept plan costs what it cost when it was made.
    PlanResponse same =
        service.planOrThrow(request().setPricePlans(true).setKnownPlanDigest(digest).build());
    assertThat(same.getKnownPlanCost()).isEqualTo(first.getPlanCost());

    // A hundred times the rows: the kept plan, made on the first numbers, is priced on these.
    catalogs.registerStatistics(rows("s2", 100 * BARS));
    PlanResponse more =
        service.planOrThrow(request().setPricePlans(true).setKnownPlanDigest(digest).build());
    assertThat(more.hasKnownPlanCost()).isTrue();
    assertThat(more.getKnownPlanCost()).isGreaterThan(first.getPlanCost());
    assertThat(more.getPlanCost()).isLessThanOrEqualTo(more.getKnownPlanCost());
  }

  @Test
  void a_plan_that_saves_less_than_the_margin_is_held_back() throws Exception {
    CatalogRegistry catalogs = catalogs();
    PlannerServiceImpl service = new PlannerServiceImpl(catalogs);
    long kept = service.planOrThrow(request(JOIN).setPricePlans(true).build()).getPlan().getPlanDigest();

    catalogs.registerStatistics(numbers("s2", 10, 10_000_000));
    PlanResponse held =
        service.planOrThrow(
            request(JOIN).setPricePlans(true).setKnownPlanDigest(kept).setCheaperBy(0.5).build());

    assertThat(held.getPlanHeld()).isTrue();
    assertThat(held.getPlanUnchanged()).isFalse();
    assertThat(held.hasPlan()).isFalse();
    assertThat(held.getPlanText()).isEmpty();
    // The two costs say why: the new plan saves something, and less than half.
    assertThat(held.getPlanCost()).isLessThan(held.getKnownPlanCost());
    assertThat(held.getPlanCost()).isGreaterThanOrEqualTo(held.getKnownPlanCost() * 0.5);
  }

  @Test
  void a_plan_that_saves_the_margin_is_sent_and_kept() throws Exception {
    CatalogRegistry catalogs = catalogs();
    PlannerServiceImpl service = new PlannerServiceImpl(catalogs);
    long kept = service.planOrThrow(request(JOIN).setPricePlans(true).build()).getPlan().getPlanDigest();

    catalogs.registerStatistics(numbers("s2", 10, 10_000_000));
    PlanResponse sent =
        service.planOrThrow(
            request(JOIN).setPricePlans(true).setKnownPlanDigest(kept).setCheaperBy(0.25).build());

    assertThat(sent.getPlanHeld()).isFalse();
    assertThat(sent.getPlan().getPlanDigest()).isNotEqualTo(kept);
    assertThat(sent.getPlanCost()).isLessThan(sent.getKnownPlanCost() * 0.75);

    // The plan sent is kept in turn: the next request that names it is told its cost.
    PlanResponse next =
        service.planOrThrow(
            request(JOIN)
                .setPricePlans(true)
                .setKnownPlanDigest(sent.getPlan().getPlanDigest())
                .setCheaperBy(0.25)
                .build());
    assertThat(next.getPlanUnchanged()).isTrue();
    assertThat(next.getKnownPlanCost()).isEqualTo(sent.getPlanCost());
  }

  @Test
  void a_plan_on_unmoved_numbers_is_unchanged_whatever_the_margin() throws Exception {
    PlannerServiceImpl service = new PlannerServiceImpl(catalogs());
    long kept = service.planOrThrow(request(JOIN).setPricePlans(true).build()).getPlan().getPlanDigest();

    PlanResponse same =
        service.planOrThrow(
            request(JOIN).setPricePlans(true).setKnownPlanDigest(kept).setCheaperBy(0.5).build());

    assertThat(same.getPlanUnchanged()).isTrue();
    assertThat(same.getPlanHeld()).isFalse();
  }

  @Test
  void a_margin_against_a_plan_no_longer_kept_sends_the_plan() throws Exception {
    CatalogRegistry catalogs = catalogs();
    PlannerServiceImpl service = new PlannerServiceImpl(catalogs);
    // Planned without prices, as by a planner that has since restarted: nothing was kept.
    long unkept = service.planOrThrow(request(JOIN).build()).getPlan().getPlanDigest();

    catalogs.registerStatistics(numbers("s2", 10, 10_000_000));
    PlanResponse sent =
        service.planOrThrow(
            request(JOIN).setPricePlans(true).setKnownPlanDigest(unkept).setCheaperBy(0.9).build());

    assertThat(sent.getPlanHeld()).isFalse();
    assertThat(sent.hasKnownPlanCost()).isFalse();
    assertThat(sent.getPlan().getPlanDigest()).isNotEqualTo(unkept);
  }

  @Test
  void a_margin_that_is_not_a_fraction_below_one_is_refused() {
    PlannerServiceImpl service = new PlannerServiceImpl(catalogs());
    for (double margin : new double[] {1.0, 1.5, -0.1, Double.NaN}) {
      assertThatThrownBy(
              () ->
                  service.planOrThrow(
                      request(JOIN).setPricePlans(true).setCheaperBy(margin).build()))
          .isInstanceOf(InvalidArgumentException.class)
          .hasMessageContaining("cheaper_by is");
    }
  }

  @Test
  void a_margin_without_prices_is_refused() {
    PlannerServiceImpl service = new PlannerServiceImpl(catalogs());

    assertThatThrownBy(() -> service.planOrThrow(request(JOIN).setCheaperBy(0.25).build()))
        .isInstanceOf(InvalidArgumentException.class)
        .hasMessageContaining("needs price_plans");
  }

  @Test
  void a_plan_that_was_not_kept_is_not_priced() throws Exception {
    PlannerServiceImpl service = new PlannerServiceImpl(catalogs());
    PlanResponse unkept = service.planOrThrow(request().build());

    PlanResponse named =
        service.planOrThrow(
            request().setPricePlans(true).setKnownPlanDigest(unkept.getPlan().getPlanDigest()).build());

    assertThat(named.hasPlanCost()).isTrue();
    assertThat(named.hasKnownPlanCost()).isFalse();
  }

  @Test
  void a_request_that_asks_for_no_prices_is_told_none() throws Exception {
    PlanResponse response = new PlannerServiceImpl(catalogs()).planOrThrow(request().build());

    assertThat(response.hasPlanCost()).isFalse();
    assertThat(response.hasKnownPlanCost()).isFalse();
  }

  private static final String INSTANCE = "01JPRICINGINSTANCEXXXXXXXX";

  /** The rows the corpus gives {@code bars}. */
  private static final long BARS = 100_800;

  /** The rows the corpus gives {@code symbols}. */
  private static final long SYMBOLS = 5;

  /**
   * The corpus under a named shape version, as an engine registers it, so its numbers arrive on
   * statistics versions (D271 (c)); and the first of them.
   */
  private static CatalogRegistry catalogs() {
    assumeTrue(
        Files.exists(CorpusQueries.corpusDir().resolve("schemas/corpus.binpb")),
        "the recorded corpus catalog");
    CatalogRegistry catalogs = new CatalogRegistry();
    catalogs.register(
        RegisterCatalogRequest.newBuilder()
            .setInstanceId(INSTANCE)
            .setShapeVersion("v1")
            .setCatalog(TestCatalogs.corpus())
            .build());
    catalogs.registerStatistics(numbers("s1", BARS, SYMBOLS));
    return catalogs;
  }

  private static RegisterStatisticsRequest rows(String version, long rows) {
    return numbers(version, rows, SYMBOLS);
  }

  /** A statistics version with these rows in {@code bars} and {@code symbols}. */
  private static RegisterStatisticsRequest numbers(String version, long bars, long symbols) {
    return RegisterStatisticsRequest.newBuilder()
        .setInstanceId(INSTANCE)
        .setStatisticsVersion(version)
        .addTables(table("bars", bars))
        .addTables(table("symbols", symbols))
        .build();
  }

  private static TableStatistics table(String name, long rows) {
    return TableStatistics.newBuilder()
        .setSchema("main")
        .setTable(name)
        .setRowCount(rows)
        .setRowCountKind(RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  private static PlanRequest.Builder request() {
    return request(SQL);
  }

  private static PlanRequest.Builder request(String sql) {
    return PlanRequest.newBuilder()
        .setSql(sql)
        .setContextId(TestCatalogs.CONTEXT_ID)
        .setInstanceId(INSTANCE)
        .setShapeVersion("v1")
        .setCatalogEpoch(TestCatalogs.EPOCH)
        .setClientIrVersion(IrVersion.CURRENT)
        .setOptions(
            PlannerOptions.newBuilder()
                .setPushdown(chalk.planner.rpc.v1.PushdownLevel.PUSHDOWN_LEVEL_FULL));
  }
}
