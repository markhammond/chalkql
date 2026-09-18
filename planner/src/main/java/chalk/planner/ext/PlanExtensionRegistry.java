package chalk.planner.ext;

import chalk.planner.rpc.v1.PlanRequest;
import com.google.protobuf.Any;
import com.google.protobuf.Message;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The handler per type URL (step 26c, docs/design/29-entitlements-as-a-wrapper.md §1, D212).
 *
 * <p>A request is the core plus independent extensions. This is where the sidecar says which it
 * knows: one handler per message name, resolved from the {@code Any}'s type URL. An extension no
 * handler is registered for is <em>refused</em> — {@code INVALID_REQUEST} naming the type URL —
 * never ignored, because ignoring an extension the host attached is the one unsafe direction: a
 * policy silently dropped is a policy not enforced.
 *
 * <p>Registration is explicit rather than discovered. {@link #defaults()} is the sidecar's set;
 * tests build their own, including an empty one, to see what the refusal looks like.
 */
public final class PlanExtensionRegistry {
  private final Map<String, PlanExtensionHandler> handlers;

  private PlanExtensionRegistry(Map<String, PlanExtensionHandler> handlers) {
    this.handlers = handlers;
  }

  /** A registry over exactly these handlers, in the order given. */
  public static PlanExtensionRegistry of(PlanExtensionHandler... handlers) {
    Map<String, PlanExtensionHandler> byName = new LinkedHashMap<>();
    for (PlanExtensionHandler handler : handlers) {
      PlanExtensionHandler previous = byName.put(handler.messageName(), handler);
      if (previous != null) {
        throw new IllegalStateException(
            "two handlers registered for '" + handler.messageName() + "'");
      }
    }
    return new PlanExtensionRegistry(Map.copyOf(byName));
  }

  /**
   * The sidecar's own set: the entitlement pass, registered for {@code chalk.v1.EntitlementsOptions}.
   * The one place a feature is wired in.
   */
  public static PlanExtensionRegistry defaults() {
    return of(new chalk.planner.entitlement.EntitlementExtension());
  }

  /** The message names this registry answers for, for a diagnostic. */
  public Set<String> messageNames() {
    return handlers.keySet();
  }

  /**
   * Reads the request's extensions, refusing any this registry has no handler for, and runs hook 1
   * for every registered handler — with its own message where the request carried one, and with
   * {@code null} where it did not.
   */
  public PlanExtensionContext bind(PlanRequest request) {
    Map<String, Any> attached = new LinkedHashMap<>();
    Set<String> duplicates = new LinkedHashSet<>();
    for (Any extension : request.getExtensionsList()) {
      String name = messageName(extension.getTypeUrl());
      if (!handlers.containsKey(name)) {
        throw new UnknownExtensionException(extension.getTypeUrl(), handlers.keySet());
      }
      if (attached.put(name, extension) != null) {
        duplicates.add(extension.getTypeUrl());
      }
    }
    if (!duplicates.isEmpty()) {
      throw new UnknownExtensionException(
          "PlanRequest.extensions carries more than one "
              + String.join(", ", duplicates)
              + ". One message per type URL: a second would silently win over the first.");
    }

    PlanExtensionContext context = new PlanExtensionContext(request);
    for (Map.Entry<String, PlanExtensionHandler> entry : handlers.entrySet()) {
      entry.getValue().beforeConversion(attached.get(entry.getKey()), context);
    }
    return context;
  }

  /** Hook 2, for every registered handler. */
  public void afterConversion(PlanExtensionContext context) {
    for (PlanExtensionHandler handler : handlers.values()) {
      handler.afterConversion(context);
    }
  }

  /**
   * Hook 3: what goes into {@code PlanResponse.extensions}, in registration order. A handler whose
   * feature did not run contributes nothing, so a plain plan's response carries an empty slot.
   */
  public List<Any> onResponse(PlanExtensionContext context) {
    List<Any> packed = new ArrayList<>();
    for (PlanExtensionHandler handler : handlers.values()) {
      for (Message message : handler.onResponse(context)) {
        packed.add(Any.pack(message));
      }
    }
    return packed;
  }

  /** The last segment of a type URL: {@code type.googleapis.com/chalk.v1.X} → {@code chalk.v1.X}. */
  private static String messageName(String typeUrl) {
    int slash = typeUrl.lastIndexOf('/');
    return slash < 0 ? typeUrl : typeUrl.substring(slash + 1);
  }

  /** An extension this sidecar has no handler for. Refused, never ignored. */
  public static final class UnknownExtensionException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    UnknownExtensionException(String typeUrl, Set<String> known) {
      super(
          "This planner has no handler for the request extension '"
              + typeUrl
              + "'. It is refused rather than ignored: an extension the host attached and the "
              + "planner dropped would be a policy not enforced. Registered: "
              + (known.isEmpty() ? "(none)" : String.join(", ", known))
              + ".");
    }

    private UnknownExtensionException(String message, @Nullable Throwable cause) {
      super(message, cause);
    }

    private UnknownExtensionException(String message) {
      super(message);
    }

    /** An extension whose bytes are not the message its type URL names. */
    public static UnknownExtensionException malformed(String typeUrl, Throwable cause) {
      return new UnknownExtensionException(
          "The request extension '" + typeUrl + "' does not parse as that message.", cause);
    }
  }

  /** For a handler that wants the request's own extension list without re-reading the registry. */
  public static @Nullable Any find(PlanRequest request, String name) {
    for (Any extension : request.getExtensionsList()) {
      if (messageName(extension.getTypeUrl()).equals(name)) {
        return extension;
      }
    }
    return null;
  }
}
