package chalk.planner.catalog;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.Column;
import chalk.ir.v1.Schema;
import chalk.ir.v1.Table;
import chalk.planner.rpc.v1.ColumnStatisticsEntry;
import chalk.planner.rpc.v1.RegisterCatalogRequest;
import chalk.planner.rpc.v1.RegisterStatisticsRequest;
import chalk.planner.rpc.v1.TableStatistics;
import java.time.Duration;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Optional;
import java.util.concurrent.ConcurrentHashMap;
import java.util.function.LongSupplier;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The only mutable state shared between requests: immutable catalog snapshots, grouped by engine
 * instance and keyed by <em>shape version</em> (D271, docs/design/44-catalog-registration.md §5).
 *
 * <p>Per instance the registry keeps the current shape and a bounded number of predecessors, because
 * a plan that started under the previous version and a narrowing that continues from an earlier plan
 * both need theirs for a while; beyond the bound the least recently used goes, and an instance with
 * no request for an idle period is evicted whole. <b>Eviction is memory policy, never correctness
 * policy</b> — a miss is answered by name with {@code UNKNOWN_CATALOG_VERSION} and the engine
 * registers the version again and retries once, so nothing an eviction does is visible to a host.
 *
 * <p>Statistics live beside the shapes, at their own version (D271 (c)): a shape is joined with the
 * newest statistics the instance has published, once per (shape version, statistics version) pair
 * and never per request.
 */
public final class CatalogRegistry {
  /**
   * Shape versions kept per instance: the current one and three predecessors. Three because a
   * predecessor is only wanted for as long as requests that named it are still arriving — an
   * in-flight plan, a narrowing continuing from an earlier plan — and a refresh cadence fast enough
   * to outrun four versions of in-flight work is one where re-registering is the cheaper answer
   * anyway. A miss costs one extra round trip and never an error a host sees.
   */
  public static final int DEFAULT_VERSION_LIMIT = 4;

  /**
   * How long an instance may go without a request before it is evicted whole. Thirty minutes is well
   * past any prepare-and-execute cycle and well short of leaking a disconnected engine's catalog for
   * the life of the sidecar; it is the same shape of bound as the planning session registry's
   * (D245), and like that one it is recoverable — the engine re-registers.
   */
  public static final Duration DEFAULT_IDLE = Duration.ofMinutes(30);

  private final ConcurrentHashMap<String, Instance> byInstance = new ConcurrentHashMap<>();
  private final int versionLimit;
  private final long idleNanos;
  private final LongSupplier clock;

  public CatalogRegistry() {
    this(DEFAULT_VERSION_LIMIT, DEFAULT_IDLE);
  }

  /** With the bounds the sidecar's arguments asked for (D271 (e)), or a test's own. */
  public CatalogRegistry(int versionLimit, Duration idle) {
    this(versionLimit, idle, System::nanoTime);
  }

  /** The same, with the clock a test drives idle eviction by: nanoseconds, monotonic. */
  public CatalogRegistry(int versionLimit, Duration idle, LongSupplier clock) {
    if (versionLimit < 1) {
      throw new IllegalArgumentException("versionLimit must be at least 1, got " + versionLimit);
    }
    if (idle.isNegative() || idle.isZero()) {
      throw new IllegalArgumentException("idle must be positive, got " + idle);
    }
    this.versionLimit = versionLimit;
    this.idleNanos = idle.toNanos();
    this.clock = clock;
  }

  /** What one registration installed, and whether the version was new to this planner. */
  public record Registration(
      RegisteredCatalog catalog, String instanceId, String shapeVersion, boolean installed) {}

  /**
   * Validates, assembles and installs a catalog under its own context id and epoch. The spelling a
   * client written before D271 produces, and the one a test reaches for.
   */
  public RegisteredCatalog register(CatalogContext descriptor) {
    return register(RegisterCatalogRequest.newBuilder().setCatalog(descriptor).build()).catalog();
  }

  /**
   * Installs one shape version, full or as a delta over a version this registry still holds (D271
   * (d)). Idempotent for a version already held: the held catalog is returned and nothing is rebuilt.
   */
  public Registration register(RegisterCatalogRequest request) {
    evictIdle();
    String instanceId = instanceOf(request);
    String shapeVersion = shapeVersionOf(request);
    Instance instance = byInstance.computeIfAbsent(instanceId, ignored -> new Instance(versionLimit));
    return instance.install(instanceId, shapeVersion, request, clock.getAsLong());
  }

  /**
   * Installs one statistics version (D271 (c)), merged over whatever the instance holds. A message
   * for an instance this registry does not hold is kept all the same: statistics may legitimately
   * arrive before the shape they belong to, and dropping them would leave the planner costing from
   * stale numbers until the next refresh moved them again.
   */
  public void registerStatistics(RegisterStatisticsRequest request) {
    evictIdle();
    String instanceId = request.getInstanceId();
    if (instanceId.isEmpty()) {
      throw new IllegalArgumentException(
          "RegisterStatistics names no instance_id; statistics belong to one engine instance"
              + " (docs/design/44-catalog-registration.md §3).");
    }
    Instance instance = byInstance.computeIfAbsent(instanceId, ignored -> new Instance(versionLimit));
    instance.installStatistics(request, clock.getAsLong());
  }

  /** The newest shape registered for this instance, if any. */
  public Optional<RegisteredCatalog> find(String instanceId) {
    return find(instanceId, "");
  }

  /**
   * The catalog this instance registered under {@code shapeVersion}, joined with the newest
   * statistics it has published. An empty version asks for the newest shape, which is what a request
   * from a client written before D271 means.
   */
  public Optional<RegisteredCatalog> find(String instanceId, String shapeVersion) {
    evictIdle();
    Instance instance = byInstance.get(instanceId);
    if (instance == null) {
      return Optional.empty();
    }
    return instance.resolve(shapeVersion, clock.getAsLong());
  }

  /** Removes an instance and every version of it. */
  public void invalidate(String instanceId) {
    byInstance.remove(instanceId);
  }

  /** How many engine instances this registry holds. */
  public int size() {
    return byInstance.size();
  }

  /** How many shape versions it holds for one instance; zero for an instance it does not hold. */
  public int versions(String instanceId) {
    Instance instance = byInstance.get(instanceId);
    return instance == null ? 0 : instance.versionCount();
  }

  /** The statistics version this instance last published, or empty for one that published none. */
  public String statisticsVersion(String instanceId) {
    Instance instance = byInstance.get(instanceId);
    return instance == null ? "" : instance.statisticsVersion();
  }

  private static String instanceOf(RegisterCatalogRequest request) {
    return request.getInstanceId().isEmpty()
        ? request.getCatalog().getContextId()
        : request.getInstanceId();
  }

  private static String shapeVersionOf(RegisterCatalogRequest request) {
    return request.getShapeVersion().isEmpty()
        ? Long.toString(request.getCatalog().getEpoch())
        : request.getShapeVersion();
  }

  /** Drops every instance nothing has asked about for the idle period. */
  private void evictIdle() {
    long now = clock.getAsLong();
    for (Map.Entry<String, Instance> entry : byInstance.entrySet()) {
      if (now - entry.getValue().touchedNanos() > idleNanos) {
        byInstance.remove(entry.getKey(), entry.getValue());
      }
    }
  }

  /**
   * One engine instance's shapes and statistics. Registrations are rare and plans are frequent, so
   * the lock is uncontended in the common case and the expensive part — assembling Calcite schemas —
   * happens once per (shape version, statistics version) pair behind it.
   */
  private static final class Instance {
    private final LinkedHashMap<String, Shape> shapes;
    private volatile long touchedNanos = System.nanoTime();
    private String newest = "";
    private String statisticsVersion = "";
    private Map<String, TableStatistics> statistics = Map.of();

    Instance(int versionLimit) {
      this.shapes =
          new LinkedHashMap<>(versionLimit * 2, 0.75f, true) {
            private static final long serialVersionUID = 1L;

            @Override
            protected boolean removeEldestEntry(Map.Entry<String, Shape> eldest) {
              return size() > versionLimit;
            }
          };
    }

    long touchedNanos() {
      return touchedNanos;
    }

    synchronized int versionCount() {
      return shapes.size();
    }

    synchronized String statisticsVersion() {
      return statisticsVersion;
    }

    synchronized Registration install(
        String instanceId, String shapeVersion, RegisterCatalogRequest request, long now) {
      touchedNanos = now;
      Shape held = shapes.get(shapeVersion);
      if (held != null && !request.getShapeVersion().isEmpty()) {
        // Idempotent for a *minted* version: a ULID is unique to the catalog it was minted for, so a
        // second registration of it carries what is already installed and there is nothing to rebuild.
        //
        // A registration that names no version is addressed by the catalog's epoch instead, and an
        // epoch is not unique to a catalog — two catalogs of one host may both be epoch 1. Such a
        // registration therefore replaces, which is what RegisterCatalog meant before D271 and what
        // every caller that names no version still means by it.
        return new Registration(built(held), instanceId, shapeVersion, false);
      }

      CatalogContext descriptor;
      if (request.getBaseShapeVersion().isEmpty()) {
        descriptor = request.getCatalog();
      } else {
        Shape base = shapes.get(request.getBaseShapeVersion());
        if (base == null) {
          throw new chalk.planner.rpc.PlanErrors.UnknownCatalogVersionException(
              instanceId,
              request.getBaseShapeVersion(),
              "the base of a catalog delta. Register the catalog whole.");
        }
        descriptor = applyDelta(base.descriptor, request);
      }

      // A shape registered under a minted version carries no numbers — they travel on their own
      // version (D271 (c)) — so it is joined with whatever statistics the instance has published. A
      // registration that names no version is the whole catalog, its own numbers included, and
      // nothing published afterwards may overwrite them: that is what RegisterCatalog meant before
      // D271 and it still means it.
      Shape shape = new Shape(descriptor, !request.getShapeVersion().isEmpty());
      // Built here rather than lazily, so an invalid catalog — a delta that left a foreign key
      // pointing nowhere included — is refused by the registration that carries it and not by the
      // first plan that happens to name it.
      shape.built = RegisteredCatalog.of(join(shape, statistics));
      shape.builtStatisticsVersion = statisticsVersion;
      shapes.put(shapeVersion, shape);
      newest = shapeVersion;
      return new Registration(shape.built, instanceId, shapeVersion, true);
    }

    synchronized void installStatistics(RegisterStatisticsRequest request, long now) {
      touchedNanos = now;
      String version = request.getStatisticsVersion();
      if (!version.isEmpty()
          && !statisticsVersion.isEmpty()
          && version.compareTo(statisticsVersion) <= 0) {
        // A version at or before the one held: minted ULIDs are time-ordered, so this is a message
        // that arrived out of order and the newer numbers are the ones to keep.
        return;
      }

      Map<String, TableStatistics> merged = new HashMap<>(statistics);
      for (TableStatistics table : request.getTablesList()) {
        merged.put(key(table.getSchema(), table.getTable()), table);
      }
      statistics = Map.copyOf(merged);
      statisticsVersion = version;
      // Every built catalog now holds statistics one version behind; they are rebuilt on demand,
      // which costs the first plan against each surviving shape and nothing at all for the ones no
      // plan names again.
    }

    synchronized Optional<RegisteredCatalog> resolve(String shapeVersion, long now) {
      touchedNanos = now;
      String wanted = shapeVersion.isEmpty() ? newest : shapeVersion;
      Shape shape = wanted.isEmpty() ? null : shapes.get(wanted);
      return shape == null ? Optional.empty() : Optional.of(built(shape));
    }

    private RegisteredCatalog built(Shape shape) {
      if (shape.built != null && statisticsVersion.equals(shape.builtStatisticsVersion)) {
        return shape.built;
      }
      shape.built = RegisteredCatalog.of(join(shape, statistics));
      shape.builtStatisticsVersion = statisticsVersion;
      return shape.built;
    }

    private static CatalogContext join(Shape shape, Map<String, TableStatistics> statistics) {
      return shape.joinsStatistics ? withStatistics(shape.descriptor, statistics) : shape.descriptor;
    }
  }

  /** One registered shape, and the catalog it was last joined with statistics into. */
  private static final class Shape {
    final CatalogContext descriptor;

    /**
     * Whether this shape's numbers arrive on the statistics version (D271 (c)) or are the ones its
     * own descriptor carried. True for every registration that named a version, which is every
     * registration an engine makes.
     */
    final boolean joinsStatistics;

    @Nullable RegisteredCatalog built;
    String builtStatisticsVersion = "";

    Shape(CatalogContext descriptor, boolean joinsStatistics) {
      this.descriptor = descriptor;
      this.joinsStatistics = joinsStatistics;
    }
  }

  private static String key(String schema, String table) {
    return schema.toLowerCase(Locale.ROOT) + "." + table.toLowerCase(Locale.ROOT);
  }

  /**
   * The base with this delta's schemas and tables applied (D271 (d)). The catalog's own properties
   * come from the delta whole; a schema the delta names contributes its properties and its tables,
   * replacing the base's tables of the same name and keeping the rest in the base's order; a schema
   * it does not name is the base's, untouched.
   */
  private static CatalogContext applyDelta(CatalogContext base, RegisterCatalogRequest request) {
    CatalogContext delta = request.getCatalog();
    java.util.Set<String> removed = new java.util.HashSet<>();
    for (String name : request.getRemovedTablesList()) {
      removed.add(name.toLowerCase(Locale.ROOT));
    }

    Map<String, Schema> changed = new LinkedHashMap<>();
    for (Schema schema : delta.getSchemasList()) {
      changed.put(schemaKey(schema), schema);
    }

    CatalogContext.Builder merged = delta.toBuilder().clearSchemas();
    for (Schema schema : base.getSchemasList()) {
      Schema replacement = changed.remove(schemaKey(schema));
      merged.addSchemas(replacement == null ? prune(schema, removed) : merge(schema, replacement, removed));
    }
    // A schema the base did not have: the delta carries it whole, which is how a source is added.
    for (Schema schema : changed.values()) {
      merged.addSchemas(prune(schema, removed));
    }
    return merged.build();
  }

  private static String schemaKey(Schema schema) {
    return schema.getSourceId() + " " + schema.getName().toLowerCase(Locale.ROOT);
  }

  /** The delta's schema, carrying the base's tables that it neither replaced nor removed. */
  private static Schema merge(Schema base, Schema delta, java.util.Set<String> removed) {
    Map<String, Table> replacements = new LinkedHashMap<>();
    for (Table table : delta.getTablesList()) {
      replacements.put(table.getName().toLowerCase(Locale.ROOT), table);
    }

    List<Table> tables = new ArrayList<>(base.getTablesCount() + delta.getTablesCount());
    for (Table table : base.getTablesList()) {
      String name = table.getName().toLowerCase(Locale.ROOT);
      if (removed.contains(qualified(delta.getName(), name))) {
        continue;
      }
      Table replacement = replacements.remove(name);
      tables.add(replacement == null ? table : replacement);
    }
    tables.addAll(replacements.values());
    return delta.toBuilder().clearTables().addAllTables(tables).build();
  }

  /** A schema the delta did not name, with any of its tables the delta removed struck out. */
  private static Schema prune(Schema schema, java.util.Set<String> removed) {
    if (removed.isEmpty()) {
      return schema;
    }
    List<Table> kept = new ArrayList<>(schema.getTablesCount());
    for (Table table : schema.getTablesList()) {
      if (!removed.contains(qualified(schema.getName(), table.getName().toLowerCase(Locale.ROOT)))) {
        kept.add(table);
      }
    }
    return kept.size() == schema.getTablesCount()
        ? schema
        : schema.toBuilder().clearTables().addAllTables(kept).build();
  }

  private static String qualified(String schema, String table) {
    return schema.toLowerCase(Locale.ROOT) + "." + table;
  }

  /**
   * The shape with the instance's published statistics joined into it (D271 (c)). A table named in
   * the statistics takes its row count, its kind and its columns' statistics from there — a column
   * the message does not name has none, because a published table's numbers are complete; a table
   * the message never named keeps whatever its own descriptor carried.
   */
  static CatalogContext withStatistics(
      CatalogContext descriptor, Map<String, TableStatistics> statistics) {
    if (statistics.isEmpty()) {
      return descriptor;
    }

    CatalogContext.Builder merged = descriptor.toBuilder();
    for (int s = 0; s < merged.getSchemasCount(); s++) {
      Schema.Builder schema = merged.getSchemasBuilder(s);
      for (int t = 0; t < schema.getTablesCount(); t++) {
        Table.Builder table = schema.getTablesBuilder(t);
        TableStatistics published = statistics.get(key(schema.getName(), table.getName()));
        if (published == null) {
          continue;
        }
        table.setRowCount(published.getRowCount()).setRowCountKind(published.getRowCountKind());
        Map<String, chalk.ir.v1.ColumnStatistics> columns = new HashMap<>();
        for (ColumnStatisticsEntry entry : published.getColumnsList()) {
          columns.put(entry.getColumn().toLowerCase(Locale.ROOT), entry.getStatistics());
        }
        for (int c = 0; c < table.getColumnsCount(); c++) {
          Column.Builder column = table.getColumnsBuilder(c);
          chalk.ir.v1.ColumnStatistics own = columns.get(column.getName().toLowerCase(Locale.ROOT));
          if (own == null) {
            column.clearStatistics();
          } else {
            column.setStatistics(own);
          }
        }
      }
    }
    return merged.build();
  }
}
