package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.rpc.PlannerServiceImpl;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.PlanRequest;
import chalk.planner.rpc.v1.PlanResponse;
import chalk.planner.rpc.v1.PlannerOptions;
import chalk.planner.rpc.v1.RequestContext;
import org.junit.jupiter.api.Test;

/**
 * Incremental narrowing at the sidecar (docs/design/16-entitlements.md §2.1, D233): the hint, the
 * miss, and the one thing both have to agree on.
 *
 * <p>The request carries the whole union of the bindings whether or not {@code narrow_from} names a
 * plan this sidecar still holds, so what is asserted here is that the two paths are the same plan —
 * the hint is an efficiency and nothing else — and that the stage list says honestly which one ran.
 */
class NarrowingTest {
  private static PlanRequest.Builder request(String sql, RequestContext context) {
    return PlanRequest.newBuilder()
        .setSql(sql)
        .setContextId(TenancyCatalogs.CONTEXT_ID)
        .setCatalogEpoch(TenancyCatalogs.EPOCH)
        .setOptions(PlannerOptions.getDefaultInstance())
        .setContext(context);
  }

  private static PlannerServiceImpl service() {
    CatalogRegistry catalogs = new CatalogRegistry();
    catalogs.register(TenancyCatalogs.catalog());
    return new PlannerServiceImpl(catalogs);
  }

  /** The union of a shape and the manager's own grants: what a narrowing of the base is prepared with. */
  private static RequestContext union() {
    return TenancyCatalogs.manager(1);
  }

  @Test
  void a_narrowing_that_hits_is_the_plan_preparing_with_the_union_gives() throws Exception {
    PlannerServiceImpl service = service();

    PlanResponse base =
        service.planOrThrow(request("SELECT * FROM members", TenancyCatalogs.shape()).build());
    PlanResponse narrowed =
        service.planOrThrow(
            request("SELECT * FROM members", union())
                .setNarrowFrom(base.getPlan().getPlanDigest())
                .build());
    PlanResponse fromScratch =
        service.planOrThrow(request("SELECT * FROM members", union()).build());

    // The hint was taken, and it says so where the stages it skipped would have been.
    assertThat(narrowed.getStats().getStagesList())
        .first(org.assertj.core.api.InstanceOfAssertFactories.STRING)
        .startsWith(PlannerPipeline.STAGE_NARROWED_FROM);
    assertThat(narrowed.getStats().getStagesList()).doesNotContain(PlannerPipeline.STAGE_PARSE);
    assertThat(fromScratch.getStats().getStagesList())
        .startsWith(PlannerPipeline.STAGE_PARSE)
        .doesNotContain(PlannerPipeline.STAGE_NARROWED_FROM);

    // And the plan is the same plan, byte for byte and digest for digest, which is the whole claim.
    assertThat(narrowed.getPlan().getPlanDigest()).isEqualTo(fromScratch.getPlan().getPlanDigest());
    assertThat(narrowed.getPlan()).isEqualTo(fromScratch.getPlan());
    assertThat(narrowed.getPlan().getPlanDigest()).isNotEqualTo(base.getPlan().getPlanDigest());
  }

  @Test
  void a_hint_naming_a_plan_this_sidecar_never_made_is_declined_and_plans_from_sql()
      throws Exception {
    PlannerServiceImpl service = service();

    PlanResponse declined =
        service.planOrThrow(
            request("SELECT * FROM members", union()).setNarrowFrom(0xdeadbeefL).build());
    PlanResponse fromScratch =
        service.planOrThrow(request("SELECT * FROM members", union()).build());

    assertThat(declined.getStats().getStagesList())
        .startsWith(PlannerPipeline.STAGE_PARSE)
        .doesNotContain(PlannerPipeline.STAGE_NARROWED_FROM);
    assertThat(declined.getPlan()).isEqualTo(fromScratch.getPlan());
  }

  /**
   * A narrowing of a narrowing: the second one starts from the tree the first one was served out of,
   * and lands on the plan the whole union gives.
   */
  @Test
  void two_steps_land_where_one_prepare_with_the_union_does() throws Exception {
    PlannerServiceImpl service = service();

    PlanResponse base =
        service.planOrThrow(request("SELECT * FROM members", TenancyCatalogs.shape()).build());
    PlanResponse first =
        service.planOrThrow(
            request("SELECT * FROM members", TenancyCatalogs.managerWithAgentShape(1))
                .setNarrowFrom(base.getPlan().getPlanDigest())
                .build());
    PlanResponse second =
        service.planOrThrow(
            request("SELECT * FROM members", union())
                .setNarrowFrom(first.getPlan().getPlanDigest())
                .build());

    assertThat(first.getStats().getStagesList().get(0))
        .startsWith(PlannerPipeline.STAGE_NARROWED_FROM);
    assertThat(second.getStats().getStagesList().get(0))
        .startsWith(PlannerPipeline.STAGE_NARROWED_FROM);
    assertThat(second.getPlan())
        .isEqualTo(service.planOrThrow(request("SELECT * FROM members", union()).build()).getPlan());
  }

  /**
   * A leased entry is given back whether the narrowing succeeded or not: a refusal must not leave
   * the base unreachable until it is evicted, and one request at a time is inside a pipeline.
   */
  @Test
  void a_leased_entry_is_given_back_and_only_one_request_holds_it() throws Exception {
    CatalogRegistry catalogs = new CatalogRegistry();
    chalk.planner.catalog.RegisteredCatalog catalog = catalogs.register(TenancyCatalogs.catalog());
    chalk.planner.entitlement.BoundContext context =
        chalk.planner.entitlement.BoundContext.of(TenancyCatalogs.shape());
    chalk.planner.plan.NarrowCache cache = new chalk.planner.plan.NarrowCache(4);
    PlannerPipeline pipeline =
        PlannerPipeline.create(
            catalog,
            chalk.planner.plan.PushdownPolicy.full(),
            chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE,
            java.util.List.of(),
            chalk.planner.plan.JoinPolicy.DEFAULT,
            context,
            chalk.planner.entitlement.PolicyOptions.DEFAULTS);
    PlannerPipeline.Front front = pipeline.front("SELECT * FROM members");

    assertThat(cache.retain(7L, pipeline, front, "fingerprint", context)).isTrue();
    assertThat(cache.retain(7L, pipeline, front, "fingerprint", context)).isFalse();

    chalk.planner.plan.NarrowCache.Entry leased = cache.lease(7L, "fingerprint", context);
    assertThat(leased).isNotNull();
    assertThat(cache.lease(7L, "fingerprint", context)).isNull();
    assertThat(cache.lease(7L, "another fingerprint", context)).isNull();

    // The path a refused narrowing takes: given back, naming no plan of its own.
    cache.release(leased, 0L);
    assertThat(cache.lease(7L, "fingerprint", context)).isNotNull();
    assertThat(cache.size()).isEqualTo(1);
    cache.clear();
    assertThat(cache.size()).isZero();
  }

  /**
   * A list that stays a relation because it is above the fold ceiling is one the retained {@code ctx}
   * schema cannot answer for — its statistic is the binding's own row count — so the hint is declined
   * and the statement is converted again. Correct either way; the point is that it is correct.
   */
  @Test
  void a_narrowing_whose_list_stays_a_relation_is_declined_rather_than_answered_wrongly()
      throws Exception {
    PlannerServiceImpl service = service();

    PlanResponse base =
        service.planOrThrow(request("SELECT * FROM members", TenancyCatalogs.shape()).build());

    int[] many = new int[80];
    for (int i = 0; i < many.length; i++) {
      many[i] = i + 1;
    }
    RequestContext big = TenancyCatalogs.manager(many);
    PlanResponse narrowed =
        service.planOrThrow(
            request("SELECT * FROM members", big)
                .setNarrowFrom(base.getPlan().getPlanDigest())
                .build());

    assertThat(narrowed.getStats().getStagesList())
        .doesNotContain(PlannerPipeline.STAGE_NARROWED_FROM);
    assertThat(narrowed.getPlan())
        .isEqualTo(service.planOrThrow(request("SELECT * FROM members", big).build()).getPlan());
    assertThat(ContextRelationValue.getDefaultInstance().getShape()).isFalse();
  }
}
