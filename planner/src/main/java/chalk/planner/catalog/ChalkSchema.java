package chalk.planner.catalog;

import chalk.ir.v1.Schema;
import chalk.ir.v1.Table;
import com.google.common.collect.ImmutableMap;
import java.util.LinkedHashMap;
import java.util.Map;
import org.apache.calcite.schema.impl.AbstractSchema;

/** One registered source's tables. Immutable for the life of a catalog epoch. */
public final class ChalkSchema extends AbstractSchema {
  private final Schema descriptor;
  private final ImmutableMap<String, org.apache.calcite.schema.Table> tables;

  public ChalkSchema(Schema descriptor) {
    this(descriptor, Map.of());
  }

  /**
   * The same schema as a statement is written over: each table with the columns {@code widened}
   * names made nullable, because some rule of its entitlement can hand a principal a stand-in in
   * their place (16-entitlements.md §1, D161; F58). An empty map is the declared catalog.
   *
   * @param widened the widened columns of each entitled table, by table name
   */
  public ChalkSchema(
      Schema descriptor, Map<String, org.apache.calcite.util.ImmutableBitSet> widened) {
    this.descriptor = descriptor;
    // LinkedHashMap first so the map is built in declared order and nothing about iteration order
    // depends on hashing (03-planner.md §6, determinism rules).
    Map<String, org.apache.calcite.schema.Table> byName = new LinkedHashMap<>();
    for (Table table : descriptor.getTablesList()) {
      byName.put(
          table.getName(),
          new ChalkTable(
              table,
              descriptor,
              widened.getOrDefault(
                  table.getName(), org.apache.calcite.util.ImmutableBitSet.of())));
    }
    this.tables = ImmutableMap.copyOf(byName);
  }

  public Schema descriptor() {
    return descriptor;
  }

  public String name() {
    return descriptor.getName();
  }

  @Override
  protected Map<String, org.apache.calcite.schema.Table> getTableMap() {
    return tables;
  }
}
