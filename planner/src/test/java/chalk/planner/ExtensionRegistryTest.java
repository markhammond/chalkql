package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.entitlement.EntitlementExtension;
import chalk.planner.ext.PlanExtensionContext;
import chalk.planner.ext.PlanExtensionHandler;
import chalk.planner.ext.PlanExtensionRegistry;
import chalk.planner.rpc.PlannerServiceImpl;
import chalk.planner.rpc.v1.EntitlementsExplain;
import chalk.planner.rpc.v1.EntitlementsOptions;
import chalk.planner.rpc.v1.EntitlementsReport;
import chalk.planner.rpc.v1.PlanRequest;
import chalk.planner.rpc.v1.PlanResponse;
import chalk.planner.rpc.v1.PlannerOptions;
import com.google.protobuf.Any;
import com.google.protobuf.Message;
import java.util.ArrayList;
import java.util.List;
import org.checkerframework.checker.nullness.qual.Nullable;
import org.junit.jupiter.api.Test;

/**
 * The extension registry (step 26c, docs/design/29-entitlements-as-a-wrapper.md §1 and §5, D212): a
 * handler per type URL, an unknown one refused rather than ignored, and a handler that sees exactly
 * its own messages.
 */
final class ExtensionRegistryTest {

  /**
   * The one unsafe direction, closed. A host that attaches a policy and gets a plan back that
   * ignored it would have a policy that is not enforced, so an extension no handler is registered
   * for is a refusal — and the message names the type URL, so the host can see which.
   */
  @Test
  void an_extension_with_no_handler_is_refused_naming_the_type_url() throws Exception {
    CatalogRegistry catalogs = new CatalogRegistry();
    catalogs.register(TestCatalogs.corpus());
    PlannerServiceImpl service = new PlannerServiceImpl(catalogs, PlanExtensionRegistry.of());

    PlanRequest request =
        withExtension(EntitlementsOptions.getDefaultInstance()).toBuilder()
            .setSql("SELECT symbol FROM bars")
            .setContextId(TestCatalogs.CONTEXT_ID)
            .setCatalogEpoch(TestCatalogs.EPOCH)
            .build();

    assertThatThrownBy(() -> service.planOrThrow(request))
        .isInstanceOf(PlanExtensionRegistry.UnknownExtensionException.class)
        .hasMessageContaining("type.googleapis.com/chalk.v1.EntitlementsOptions")
        .hasMessageContaining("refused rather than ignored");
  }

  /** The refusal is INVALID_REQUEST, so a client can tell it from a broken statement. */
  @Test
  void the_refusal_is_invalid_request() {
    PlanExtensionRegistry registry = PlanExtensionRegistry.of();
    Throwable refusal =
        org.assertj.core.api.Assertions.catchThrowable(
            () -> registry.bind(withExtension(EntitlementsOptions.getDefaultInstance())));

    io.grpc.StatusRuntimeException status = chalk.planner.rpc.PlanErrors.toStatus(refusal);
    assertThat(status.getStatus().getCode()).isEqualTo(io.grpc.Status.Code.INVALID_ARGUMENT);
    assertThat(chalk.planner.rpc.PlanErrors.TRAILER).isNotNull();
  }

  /** A handler is given its own message, and nothing else's. */
  @Test
  void a_handler_receives_exactly_its_own_message() {
    Recording mine = new Recording("chalk.v1.EntitlementsOptions");
    Recording other = new Recording("chalk.v1.EntitlementsReport");
    PlanExtensionRegistry registry = PlanExtensionRegistry.of(mine, other);

    registry.bind(withExtension(EntitlementsOptions.newBuilder().setExplain(true).build()));

    assertThat(mine.seen).hasSize(1);
    assertThat(mine.seen.get(0)).isNotNull();
    // Hook 1 runs for every registered handler; the one the request said nothing to gets null.
    assertThat(other.seen).containsExactly((Any) null);
  }

  /** Two messages of the same type would let the second win silently, so they are refused. */
  @Test
  void two_extensions_of_one_type_are_refused() {
    PlanExtensionRegistry registry = PlanExtensionRegistry.of(new EntitlementExtension());
    PlanRequest request =
        withExtension(EntitlementsOptions.getDefaultInstance()).toBuilder()
            .addExtensions(Any.pack(EntitlementsOptions.getDefaultInstance()))
            .build();

    assertThatThrownBy(() -> registry.bind(request))
        .isInstanceOf(PlanExtensionRegistry.UnknownExtensionException.class)
        .hasMessageContaining("more than one");
  }

  /** The sidecar's own set is the entitlement pass, and it is registered for its options message. */
  @Test
  void the_default_registry_holds_the_entitlement_handler() {
    assertThat(PlanExtensionRegistry.defaults().messageNames())
        .containsExactly(EntitlementExtension.OPTIONS);
  }

  /**
   * The response slot carries the report only when the pass ran (§5). A plan over a catalog with no
   * entitlement writes nothing at all, which is what "no bytes when unused" means on this side.
   */
  @Test
  void the_response_slot_is_empty_when_the_pass_did_not_run() throws Exception {
    CatalogRegistry catalogs = new CatalogRegistry();
    catalogs.register(TestCatalogs.corpus());
    PlanResponse response =
        new PlannerServiceImpl(catalogs).planOrThrow(
            PlanRequest.newBuilder()
                .setSql("SELECT symbol FROM bars")
                .setContextId(TestCatalogs.CONTEXT_ID)
                .setCatalogEpoch(TestCatalogs.EPOCH)
                .setOptions(PlannerOptions.getDefaultInstance())
                .build());

    assertThat(response.getExtensionsList()).isEmpty();
  }

  /** The entitlement handler writes both messages when a caller asked for the explanation. */
  @Test
  void the_entitlement_handler_writes_the_report_and_the_explanation() {
    EntitlementExtension handler = new EntitlementExtension();
    PlanExtensionContext context = PlanExtensionRegistry.of(handler).bind(PlanRequest.getDefaultInstance());

    // No planning result on the slate, so the feature did not run and says nothing.
    assertThat(handler.onResponse(context)).isEmpty();
    assertThat(Any.pack(EntitlementsReport.getDefaultInstance()).getTypeUrl())
        .isEqualTo("type.googleapis.com/chalk.v1.EntitlementsReport");
    assertThat(Any.pack(EntitlementsExplain.getDefaultInstance()).getTypeUrl())
        .isEqualTo("type.googleapis.com/chalk.v1.EntitlementsExplain");
  }

  private static PlanRequest withExtension(Message message) {
    return PlanRequest.newBuilder().addExtensions(Any.pack(message)).build();
  }

  /** A handler that records what hook 1 handed it. */
  private static final class Recording implements PlanExtensionHandler {
    private final String name;
    private final List<@Nullable Any> seen = new ArrayList<>();

    Recording(String name) {
      this.name = name;
    }

    @Override
    public String messageName() {
      return name;
    }

    @Override
    public void beforeConversion(@Nullable Any message, PlanExtensionContext context) {
      seen.add(message);
    }
  }
}
