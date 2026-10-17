package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import chalk.ir.IrVersion;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.rpc.PlannerServiceImpl;
import chalk.planner.rpc.v1.PlanRequest;
import chalk.planner.rpc.v1.PlanResponse;
import chalk.planner.rpc.v1.PlannerOptions;
import java.nio.file.Files;
import org.junit.jupiter.api.Test;

/**
 * A request that names the plan its caller holds (D349): a planner that comes to that plan says so
 * and leaves the plan out, and one that comes to another sends it, as for any request.
 */
class KnownPlanDigestTest {
  private static final String SQL = "SELECT symbol, SUM(volume) AS volume FROM bars GROUP BY symbol";

  @Test
  void a_plan_the_caller_holds_is_answered_unchanged_and_not_sent() throws Exception {
    PlannerServiceImpl service = service();
    PlanResponse first = service.planOrThrow(request().build());
    long digest = first.getPlan().getPlanDigest();

    PlanResponse again = service.planOrThrow(request().setKnownPlanDigest(digest).build());

    assertThat(again.getPlanUnchanged()).isTrue();
    assertThat(again.hasPlan()).isFalse();
    assertThat(again.getPlanText()).isEmpty();
    assertThat(again.getLogicalDigest()).isEqualTo(first.getLogicalDigest());
  }

  @Test
  void another_plan_is_sent_whole() throws Exception {
    PlannerServiceImpl service = service();
    PlanResponse first = service.planOrThrow(request().build());

    PlanResponse other =
        service.planOrThrow(
            request().setKnownPlanDigest(first.getPlan().getPlanDigest() ^ 1L).build());

    assertThat(other.getPlanUnchanged()).isFalse();
    assertThat(other.getPlan()).isEqualTo(first.getPlan());
  }

  @Test
  void a_request_that_names_none_is_sent_the_plan() throws Exception {
    PlanResponse response = service().planOrThrow(request().build());

    assertThat(response.getPlanUnchanged()).isFalse();
    assertThat(response.hasPlan()).isTrue();
  }

  private static PlannerServiceImpl service() {
    assumeTrue(
        Files.exists(CorpusQueries.corpusDir().resolve("schemas/corpus.binpb")),
        "the recorded corpus catalog");
    CatalogRegistry catalogs = new CatalogRegistry();
    catalogs.register(TestCatalogs.corpus());
    return new PlannerServiceImpl(catalogs);
  }

  private static PlanRequest.Builder request() {
    return PlanRequest.newBuilder()
        .setSql(SQL)
        .setContextId(TestCatalogs.CONTEXT_ID)
        .setCatalogEpoch(TestCatalogs.EPOCH)
        .setClientIrVersion(IrVersion.CURRENT)
        .setOptions(
            PlannerOptions.newBuilder()
                .setPushdown(chalk.planner.rpc.v1.PushdownLevel.PUSHDOWN_LEVEL_FULL)
                .setIncludePlanText(true));
  }
}
