package chalk.planner.plan;

import chalk.ir.v1.DialectProfile;
import chalk.ir.v1.Schema;
import chalk.ir.v1.SourceCapabilities;
import chalk.planner.plan.rel.SourceRel;
import org.apache.calcite.plan.Convention;
import org.apache.calcite.plan.RelTraitSet;

/**
 * One calling convention per remote source (D83). Calcite's JDBC adapter is the template: {@code
 * JdbcConvention} exists once per data source, {@code JdbcRules} converts logical rels into it, and
 * {@code JdbcToEnumerableConverter} sits at the boundary. Here the boundary is {@link
 * chalk.planner.plan.rel.SourceToLocalConverter}, which becomes the IR's {@code RemoteQuery}.
 *
 * <p>The convention carries the source's descriptor, so every rule that decides whether a rel may
 * enter this convention reads the capabilities from the trait it is converting to and needs no
 * per-source code of its own. A cross-source join therefore cannot enter either source's convention
 * and stays local (M4; M5 improves it).
 *
 * <p><b>Identity is the equality.</b> One instance per source per {@link
 * chalk.planner.catalog.RegisteredCatalog}, built with the catalog and shared by every request
 * against it. Two catalogs that both have a source called {@code crm} may describe it differently,
 * and {@code ConventionTraitDef} interns conventions for the life of the process — so equality by
 * source id would let one catalog's descriptor answer another catalog's questions. The name still
 * reads {@code CHALK_SOURCE_<id>}, because that is what a plan text should say.
 */
public final class SourceConvention extends Convention.Impl {
  private final String sourceId;
  private final SourceCapabilities capabilities;
  private final DialectProfile profile;
  private final String schemaName;

  private SourceConvention(Schema schema) {
    super("CHALK_SOURCE_" + schema.getSourceId(), SourceRel.class);
    this.sourceId = schema.getSourceId();
    this.capabilities = schema.getCapabilities();
    this.profile = schema.getDialectProfile();
    this.schemaName = schema.getName();
  }

  /** The convention for {@code schema}, or null when that schema takes no queries at all. */
  public static SourceConvention of(Schema schema) {
    return switch (schema.getCapabilities().getQueryLanguage()) {
      case QUERY_LANGUAGE_SQL, QUERY_LANGUAGE_IR -> new SourceConvention(schema);
      default -> null;
    };
  }

  public String sourceId() {
    return sourceId;
  }

  public String schemaName() {
    return schemaName;
  }

  /** What this source says it can do. Never null; an all-false descriptor pushes nothing. */
  public SourceCapabilities capabilities() {
    return capabilities;
  }

  /** How this source spells and evaluates SQL. Never null. */
  public DialectProfile profile() {
    return profile;
  }

  public boolean isSql() {
    return capabilities.getQueryLanguage() == chalk.ir.v1.QueryLanguage.QUERY_LANGUAGE_SQL;
  }

  /**
   * Never: a subtree in this convention is handed to the source as one query, so an abstract
   * converter inside it would have nothing to run it. Ordering that the source cannot provide is
   * enforced above the boundary, in {@code CHALK_LOCAL}.
   */
  @Override
  public boolean useAbstractConvertersForConversion(RelTraitSet fromTraits, RelTraitSet toTraits) {
    return false;
  }

}
