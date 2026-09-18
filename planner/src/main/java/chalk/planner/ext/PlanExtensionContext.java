package chalk.planner.ext;

import chalk.planner.rpc.v1.PlanRequest;
import java.util.LinkedHashMap;
import java.util.Map;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * One request's extension state (step 26c, D212): the request itself, and a typed slate the core and
 * the handlers put values on for each other.
 *
 * <p>The slate is how the sidecar stays free of any one feature's vocabulary. The core puts what it
 * has — the bound values it read from {@code PlanRequest.context}, the planning result once it
 * exists — under the type that names it; a handler puts what it contributes, and whatever needs that
 * contribution asks for it by type. Nothing here knows what an entitlement is.
 *
 * <p>Not thread-safe and not meant to be: one instance belongs to one {@code Plan} call.
 */
public final class PlanExtensionContext {
  private final PlanRequest request;
  private final Map<Class<?>, Object> slate = new LinkedHashMap<>();

  PlanExtensionContext(PlanRequest request) {
    this.request = request;
  }

  /** The request as it arrived, for a handler that reads a core field of it. */
  public PlanRequest request() {
    return request;
  }

  /** Puts a value on the slate under the type that names it, replacing any previous one. */
  public <T> void put(Class<T> key, T value) {
    slate.put(key, value);
  }

  /** The value put under {@code key}, or {@code null} when nothing put one. */
  public <T> @Nullable T get(Class<T> key) {
    return key.cast(slate.get(key));
  }

  /** The value put under {@code key}, or {@code fallback} when nothing put one. */
  public <T> T getOrDefault(Class<T> key, T fallback) {
    T value = get(key);
    return value == null ? fallback : value;
  }
}
