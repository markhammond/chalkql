package chalk.planner.catalog;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.Schema;
import org.apache.calcite.jdbc.CalciteSchema;
import org.apache.calcite.schema.SchemaPlus;
import org.apache.calcite.tools.Frameworks;

/**
 * One validated catalog, assembled into Calcite schemas once and shared by every request that names
 * its (context_id, epoch). Nothing here is mutable, so sharing across concurrent plans is safe.
 */
public final class RegisteredCatalog {
  private final CatalogContext descriptor;
  private final SchemaPlus rootSchema;
  private final SchemaPlus defaultSchema;
  private final java.util.List<ChalkSchema> schemas;

  /**
   * The same catalog as the <b>statement</b> is written over, one tree per {@code PlaceholderPolicy}:
   * every column an entitlement can withhold or mask to NULL is nullable there, whatever the source
   * stores (F58, {@link chalk.planner.entitlement.DisclosedNullability}). Null for a catalog that
   * carries no entitlement or widens nothing, in which case the declared tree is the only one there
   * is and nothing here allocates.
   */
  private final java.util.Map<chalk.planner.rpc.v1.PlaceholderPolicy, Disclosed> disclosed;

  /** One policy's schemas, and the root and default schema built over them. */
  private record Disclosed(
      java.util.List<ChalkSchema> schemas, SchemaPlus rootSchema, SchemaPlus defaultSchema) {}

  private final UserFunctions functions;

  private RegisteredCatalog(
      CatalogContext descriptor,
      SchemaPlus rootSchema,
      SchemaPlus defaultSchema,
      java.util.List<ChalkSchema> schemas) {
    this.descriptor = descriptor;
    this.rootSchema = rootSchema;
    this.defaultSchema = defaultSchema;
    this.schemas = java.util.List.copyOf(schemas);
    this.sourceConventions = conventionsOf(descriptor);
    this.functions = UserFunctions.of(descriptor);
    this.hasEntitlements = anyEntitlement(descriptor);
    for (UserFunction function : functions.declarations()) {
      if (function.isSqlBodied()) {
        chalk.planner.ir.SqlBodyInliner.check(function);
      }
    }

    // §1: every entitlement's SQL is type-checked once, here, through a throwaway cluster over the
    // schemas just built — so a rule condition that is not boolean and a mask of the wrong type are
    // refused by a message naming the column, rather than becoming a plan (F42). Only when there is
    // an entitlement: §0's zero-cost property covers registration too.
    //
    // The same reading answers which columns are nullable in the row type a statement sees (F58),
    // and the schemas a statement is planned over are built from it — once per catalog and per
    // policy, never per request.
    chalk.planner.entitlement.DisclosedNullability nullability =
        hasEntitlements
            ? chalk.planner.entitlement.EntitlementRegistration.check(
                descriptor, defaultSchema, functions)
            : chalk.planner.entitlement.DisclosedNullability.NONE;
    this.disclosed = disclosedTrees(descriptor, nullability);
  }

  /**
   * One schema tree per placeholder policy that widens anything, over the same descriptors
   * (F58). A policy that widens nothing gets no entry and falls back to the declared tree, which is
   * §0's zero-cost property: a catalog with no entitlement, and an entitled one whose columns are
   * all nullable already or never withheld, builds exactly what it built before.
   */
  private static java.util.Map<chalk.planner.rpc.v1.PlaceholderPolicy, Disclosed> disclosedTrees(
      CatalogContext descriptor, chalk.planner.entitlement.DisclosedNullability nullability) {
    if (nullability.isEmpty()) {
      return java.util.Map.of();
    }
    java.util.Map<chalk.planner.rpc.v1.PlaceholderPolicy, Disclosed> trees =
        new java.util.EnumMap<>(chalk.planner.rpc.v1.PlaceholderPolicy.class);
    for (chalk.planner.rpc.v1.PlaceholderPolicy policy :
        java.util.List.of(
            chalk.planner.rpc.v1.PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL,
            chalk.planner.rpc.v1.PlaceholderPolicy.PLACEHOLDER_POLICY_AS_EMPTY)) {
      if (nullability.byPolicy(policy).isEmpty()) {
        continue;
      }
      java.util.List<ChalkSchema> built = new java.util.ArrayList<>(descriptor.getSchemasCount());
      for (Schema schema : descriptor.getSchemasList()) {
        built.add(new ChalkSchema(schema, nullability.ofSchema(policy, schema.getName())));
      }
      SchemaPlus root = Frameworks.createRootSchema(true);
      SchemaPlus first = null;
      for (ChalkSchema schema : built) {
        SchemaPlus added = root.add(schema.name(), schema);
        if (first == null) {
          first = added;
        }
      }
      trees.put(policy, new Disclosed(java.util.List.copyOf(built), root, first));
    }
    return trees;
  }

  /**
   * The functions this catalog declares (D77), with their Calcite operators built once. Shared by
   * every request against the catalog, like the source conventions and for the same reason.
   */
  public UserFunctions functions() {
    return functions;
  }

  /**
   * The cross-source join policy the host's {@code ICrossSourceJoinPolicy} produced for this
   * catalog (D104). Travels with the catalog, so changing it moves the epoch — which is right: it
   * changes what every plan against this catalog looks like. A request may still merge its own over
   * it.
   */
  public chalk.ir.v1.CrossSourceJoinPolicy joinPolicy() {
    return descriptor.getJoinPolicy();
  }

  /** Validates the descriptor and builds the Calcite schemas. */
  public static RegisteredCatalog of(CatalogContext descriptor) {
    CatalogValidator.validate(descriptor);

    // `true` caches sub-schema and table lookups; the schemas are immutable so the cache is correct.
    java.util.List<ChalkSchema> schemas = new java.util.ArrayList<>(descriptor.getSchemasCount());
    for (Schema schema : descriptor.getSchemasList()) {
      schemas.add(new ChalkSchema(schema));
    }

    SchemaPlus root = Frameworks.createRootSchema(true);
    SchemaPlus first = null;
    for (ChalkSchema schema : schemas) {
      SchemaPlus added = root.add(schema.name(), schema);
      if (first == null) {
        first = added;
      }
    }
    // CatalogValidator has already rejected a catalog with no schemas.
    return new RegisteredCatalog(descriptor, root, first, schemas);
  }

  /**
   * The default schema of a fresh root holding this catalog's schemas plus one more, for the
   * per-request {@code ctx} schema of the entitlement design (step 26, 16-entitlements.md §2).
   *
   * <p>A fresh root rather than an addition to {@link #rootSchema()}: the registered root is shared
   * by every request against this catalog, and one principal's bindings may not appear in another's
   * name resolution. The {@link ChalkSchema} instances are reused because they are immutable and
   * hold no parent, so this is one root object per request rather than a rebuild of the catalog.
   * The default schema is the first, as it is for the registered root (A4), and Calcite resolves
   * every other name upward through the parent chain from it.
   */
  public SchemaPlus defaultSchemaWith(String name, org.apache.calcite.schema.Schema extra) {
    return defaultSchemaWith(schemas, name, extra);
  }

  /**
   * The same over the tree a statement under {@code policy} is written against (F58): the declared
   * one for a catalog that widens nothing, which is every catalog without an entitlement.
   */
  public SchemaPlus defaultSchemaWith(
      chalk.planner.rpc.v1.PlaceholderPolicy policy,
      String name,
      org.apache.calcite.schema.Schema extra) {
    Disclosed tree = disclosed.get(policy);
    return defaultSchemaWith(tree == null ? schemas : tree.schemas(), name, extra);
  }

  private static SchemaPlus defaultSchemaWith(
      java.util.List<ChalkSchema> over, String name, org.apache.calcite.schema.Schema extra) {
    SchemaPlus root = Frameworks.createRootSchema(true);
    SchemaPlus first = null;
    for (ChalkSchema schema : over) {
      SchemaPlus added = root.add(schema.name(), schema);
      if (first == null) {
        first = added;
      }
    }
    root.add(name, extra);
    return first;
  }

  public CatalogContext descriptor() {
    return descriptor;
  }

  /**
   * Whether any table in this catalog carries an entitlement (step 26, 16-entitlements.md §0).
   *
   * <p>Computed once at registration and read once per request. It is the whole of §0's "installed
   * only when the registered catalog carries an entitlement": a catalog without one never builds a
   * descriptor converter, never walks a tree looking for entitled leaves, and shows no entitlement
   * stage in the planner's stage list — which is what the zero-cost class asserts.
   */
  public boolean hasEntitlements() {
    return hasEntitlements;
  }

  private final boolean hasEntitlements;

  private static boolean anyEntitlement(CatalogContext descriptor) {
    for (Schema schema : descriptor.getSchemasList()) {
      for (chalk.ir.v1.Table table : schema.getTablesList()) {
        if (table.hasEntitlement()) {
          return true;
        }
      }
    }
    return false;
  }

  public String contextId() {
    return descriptor.getContextId();
  }

  public long epoch() {
    return descriptor.getEpoch();
  }

  public SchemaPlus rootSchema() {
    return rootSchema;
  }

  /**
   * The first schema in the context is the default one; the rest are addressed {@code schema.table}
   * (A4). Calcite resolves upward through the parent chain, so qualified names still work.
   */
  public SchemaPlus defaultSchema() {
    return defaultSchema;
  }

  /**
   * The default schema of the tree a statement under {@code policy} is written against (F58): every
   * column an entitlement can withhold or mask to NULL is nullable there, so the statement's own
   * expressions are simplified under the stand-in's nullability and not the stored value's.
   *
   * <p>{@link #defaultSchema()} stays the <b>declared</b> catalog, and is what the entitlement pass
   * converts each descriptor through: a mask is evaluated over the leaf's raw row (§1), so it is
   * type-checked and converted over the row the source stores, and the scan beneath the leaf reads
   * exactly that row.
   */
  public SchemaPlus defaultSchema(chalk.planner.rpc.v1.PlaceholderPolicy policy) {
    Disclosed tree = disclosed.get(policy);
    return tree == null ? defaultSchema : tree.defaultSchema();
  }

  /** The root of that same tree, for a caller that resolves names from the top (F58). */
  public SchemaPlus rootSchema(chalk.planner.rpc.v1.PlaceholderPolicy policy) {
    Disclosed tree = disclosed.get(policy);
    return tree == null ? rootSchema : tree.rootSchema();
  }

  public CalciteSchema calciteSchema() {
    return CalciteSchema.from(rootSchema);
  }

  /**
   * One {@link chalk.planner.plan.SourceConvention} per schema that takes queries (D83), in catalog
   * order. Empty for a catalog of nothing but local sources, which is every catalog before M4.
   */
  public java.util.List<chalk.planner.plan.SourceConvention> sourceConventions() {
    return sourceConventions;
  }

  /**
   * Built once with the catalog and shared by every request against it. One instance per source
   * matters: a Calcite trait set holds its traits by identity, so a rule built against one
   * {@code SourceConvention} object would never match a rel whose trait was an equal but distinct
   * one.
   */
  private final java.util.List<chalk.planner.plan.SourceConvention> sourceConventions;

  private static java.util.List<chalk.planner.plan.SourceConvention> conventionsOf(
      CatalogContext descriptor) {
    java.util.List<chalk.planner.plan.SourceConvention> conventions = new java.util.ArrayList<>();
    for (Schema schema : descriptor.getSchemasList()) {
      chalk.planner.plan.SourceConvention convention =
          chalk.planner.plan.SourceConvention.of(schema);
      if (convention != null) {
        conventions.add(convention);
      }
    }
    return java.util.List.copyOf(conventions);
  }
}
