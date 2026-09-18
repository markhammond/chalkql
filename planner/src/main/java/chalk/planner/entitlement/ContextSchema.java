package chalk.planner.entitlement;

import com.google.common.collect.ImmutableMap;
import java.util.LinkedHashMap;
import java.util.Map;
import org.apache.calcite.schema.Table;
import org.apache.calcite.schema.impl.AbstractSchema;

/**
 * The per-request {@code ctx} schema: the context's relations, and the lists too large to fold
 * (docs/design/16-entitlements.md §2).
 *
 * <p>Per request rather than per catalog, because the context is per request — which is ADR 0025's
 * first finding in its smallest form: a {@code ChalkTable} is built once with the catalog and shared,
 * and nothing about one principal's bindings may be.
 */
public final class ContextSchema extends AbstractSchema {
  private final ImmutableMap<String, Table> tables;

  private ContextSchema(ImmutableMap<String, Table> tables) {
    this.tables = tables;
  }

  /** The schema for one bound context, or null when nothing in it stays a relation. */
  public static ContextSchema of(BoundContext context) {
    if (context.isEmpty()) {
      return null;
    }
    // LinkedHashMap first, so iteration order follows the host's binding order rather than hashing
    // (03-planner.md §6, determinism).
    Map<String, Table> byName = new LinkedHashMap<>();
    for (BoundContext.Relation relation : context.unfolded()) {
      byName.put(relation.name(), new ContextTable(relation));
    }
    return byName.isEmpty() ? null : new ContextSchema(ImmutableMap.copyOf(byName));
  }

  @Override
  protected Map<String, Table> getTableMap() {
    return tables;
  }
}
