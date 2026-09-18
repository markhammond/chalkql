package chalk.planner.ext;

import com.google.protobuf.Any;
import com.google.protobuf.Message;
import java.util.List;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * One feature's handler on the sidecar (step 26c, docs/design/29-entitlements-as-a-wrapper.md §1,
 * D212).
 *
 * <p>A request is the core plus independent extensions, each addressed by the type URL of the
 * message it carries in {@code PlanRequest.extensions}. Reasoning about a feature is reasoning about
 * its handler: it sees its own message and the pipeline's three hooks, and nothing else on the
 * sidecar has to know the feature exists.
 *
 * <p>The three hooks are the points a feature can want. {@link #beforeConversion} runs once the
 * request has been read and before the statement is planned — where a handler turns its message into
 * whatever the pipeline needs. {@link #afterConversion} runs once the physical plan and the IR plan
 * exist. {@link #onResponse} says what goes back in {@code PlanResponse.extensions}, and runs for
 * every registered handler whether or not the request carried its message, because whether a feature
 * *ran* is the feature's own question: the entitlement pass is installed by the catalog, so a
 * request that attached no options can still have run it.
 */
public interface PlanExtensionHandler {

  /**
   * The fully-qualified name of the message this handler is registered for — {@code
   * chalk.v1.EntitlementsOptions}. The registry matches it against the type URL's last segment, so a
   * request may spell the URL with any prefix protobuf accepts.
   */
  String messageName();

  /**
   * Hook 1: the request's own message, or {@code null} when this request attached none. Runs before
   * the statement is planned.
   */
  void beforeConversion(@Nullable Any message, PlanExtensionContext context);

  /** Hook 2: the physical plan and the IR plan now exist on the context. */
  default void afterConversion(PlanExtensionContext context) {}

  /**
   * Hook 3: what this handler writes into {@code PlanResponse.extensions}. Empty when the feature
   * did not run, which is what "no bytes when unused" means on the response side.
   */
  default List<Message> onResponse(PlanExtensionContext context) {
    return List.of();
  }
}
