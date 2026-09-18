package chalk.planner.plan;

import chalk.ir.v1.DialectProfile;
import chalk.ir.v1.IdentifierCasing;
import chalk.ir.v1.IdentifierQuoting;
import chalk.planner.types.ChalkTypeSystem;
import java.lang.reflect.Constructor;
import java.util.ArrayList;
import java.util.EnumSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import org.apache.calcite.avatica.util.Casing;
import org.apache.calcite.config.NullCollation;
import org.apache.calcite.sql.SqlDialect;
import org.apache.calcite.sql.SqlOperator;
import org.apache.calcite.sql.dialect.AnsiSqlDialect;
import org.apache.calcite.sql.dialect.PostgresqlSqlDialect;
import org.apache.calcite.sql.validate.SqlConformanceEnum;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The {@code SqlDialect} a source's SQL is generated with (D82, §2), built from its {@link
 * DialectProfile}.
 *
 * <p>V33's answer, verified against the 1.42 jar rather than assumed: Calcite ships <b>39</b>
 * dialects, and {@code SqliteSqlDialect} and {@code DuckDBSqlDialect} are both among them — the
 * design expected Chalk to write those two from scratch and it does not have to. What Chalk defines
 * is two thin subclasses ({@link ChalkSqlDialects}) and the {@code SqlDialect.Context} every dialect
 * is constructed with, so the profile's identifier quoting, casing, case sensitivity, conformance
 * and null placement reach the generated SQL rather than the dialect's own defaults. That is the
 * whole of "the profile's fields override the preset".
 *
 * <p>D249 (design 31, ADR 0032) widens {@link #of} beyond the four tuned presets: a name that is one
 * of Calcite's own {@code SqlDialect.DatabaseProduct} constants gets that product's stock dialect
 * class, over the same per-profile {@link SqlDialect.Context} the tuned ones are built from. {@link
 * #presets()} is the same resolution run over every {@code DatabaseProduct}, for {@code GetInfo}
 * (D248) rather than for one profile.
 */
public final class SourceDialects {
  private SourceDialects() {}

  /** The tuned presets' own {@code DatabaseProduct}s, so {@link #presets()} does not list them twice. */
  private static final Set<SqlDialect.DatabaseProduct> TUNED_PRODUCTS =
      EnumSet.of(
          SqlDialect.DatabaseProduct.SQLITE,
          SqlDialect.DatabaseProduct.DUCKDB,
          SqlDialect.DatabaseProduct.POSTGRESQL);

  /** The dialect for one source. Never null; an unknown name is ANSI. */
  public static SqlDialect of(DialectProfile profile) {
    SqlDialect.Context context = contextFor(profile);
    String name = profile.getDialect().toLowerCase(Locale.ROOT);
    return switch (name) {
      case "sqlite" -> new ChalkSqlDialects.Sqlite(context.withDatabaseProduct(
          SqlDialect.DatabaseProduct.SQLITE));
      case "duckdb" -> new ChalkSqlDialects.DuckDb(context.withDatabaseProduct(
          SqlDialect.DatabaseProduct.DUCKDB));
      case "postgresql", "postgres" -> new ChalkSqlDialects.Postgres(context.withDatabaseProduct(
          SqlDialect.DatabaseProduct.POSTGRESQL));
      case "ansi" -> new AnsiSqlDialect(context);
      default -> {
        SqlDialect.DatabaseProduct product = parseProduct(name);
        SqlDialect stock = product == null ? null : stockDialect(product, context);
        yield stock != null ? stock : new AnsiSqlDialect(context);
      }
    };
  }

  /**
   * Every preset {@link #of} accepts, for {@code GetInfo} (D248) rather than for one profile: the
   * four tuned ones, then every other {@code DatabaseProduct} {@link #of} can actually build —
   * {@link #stockDialect} is the same resolution {@code of}'s {@code default} arm runs, so this list
   * and what {@code of} does for a name in it can never disagree. Computed once: nothing here reads
   * a profile, and Calcite's own dialect constructors run to answer the constructibility question,
   * so a lazily-cached list beats redoing that on every {@code GetInfo} call.
   */
  public static List<DialectPreset> presets() {
    return PRESETS;
  }

  private static final List<DialectPreset> PRESETS = computePresets();

  private static List<DialectPreset> computePresets() {
    List<DialectPreset> presets = new ArrayList<>();
    presets.add(new DialectPreset("sqlite", "SQLITE", true, List.of()));
    presets.add(new DialectPreset("duckdb", "DUCKDB", true, List.of()));
    presets.add(new DialectPreset("postgresql", "POSTGRESQL", true, List.of("postgres")));
    presets.add(new DialectPreset("ansi", "", true, List.of()));
    for (SqlDialect.DatabaseProduct product : SqlDialect.DatabaseProduct.values()) {
      // The tuned three are already listed above, under the name the profile actually spells; an
      // untuned product's own preset name is its enum constant, lower case ("big_query" for
      // BIG_QUERY, D249's example), never a spelling a host would have to guess.
      if (TUNED_PRODUCTS.contains(product)) {
        continue;
      }
      // JETHRO excluded here (V196, ADR 0032): Calcite's own default factory for it throws rather
      // than returning an instance to build a preset from, so it is not a name this sidecar accepts
      // — the same as any other name stockDialect cannot resolve.
      if (stockDialect(product, SqlDialect.EMPTY_CONTEXT) == null) {
        continue;
      }
      presets.add(
          new DialectPreset(product.name().toLowerCase(Locale.ROOT), product.name(), false, List.of()));
    }
    return List.copyOf(presets);
  }

  /** One name {@link #of} accepts, and what {@code GetInfo} (D248) says about it. */
  public record DialectPreset(String name, String databaseProduct, boolean tuned, List<String> aliases) {}

  /**
   * Whether {@code name} is one of the presets {@link #presets()} lists — a tuned preset (aliases
   * included), {@code ansi}, or a {@code DatabaseProduct} this build can actually construct — rather
   * than a name {@link #of} would silently plan as ANSI (D256). Empty is not covered here: it is
   * ANSI by its own rule (D249) and untouched by D256, so a caller checking a profile's {@code
   * dialect} decides separately what to do with an empty one.
   */
  public static boolean isAccepted(String name) {
    String lower = name.toLowerCase(Locale.ROOT);
    if (lower.equals("sqlite")
        || lower.equals("duckdb")
        || lower.equals("postgresql")
        || lower.equals("postgres")
        || lower.equals("ansi")) {
      return true;
    }
    SqlDialect.DatabaseProduct product = parseProduct(lower);
    return product != null && stockDialect(product, SqlDialect.EMPTY_CONTEXT) != null;
  }

  /** The accepted preset names, in {@link #presets()}'s order — for a message naming what would
   * have worked (D256). */
  public static List<String> presetNames() {
    return PRESETS.stream().map(DialectPreset::name).toList();
  }

  /**
   * {@code name} as a {@code DatabaseProduct} constant — case-insensitively, {@code -} and {@code _}
   * both accepted (D249, so {@code "BIG-QUERY"} and {@code "big_query"} both reach {@code
   * BIG_QUERY}) — or null when it is neither.
   */
  private static SqlDialect.@Nullable DatabaseProduct parseProduct(String name) {
    try {
      return SqlDialect.DatabaseProduct.valueOf(name.toUpperCase(Locale.ROOT).replace('-', '_'));
    } catch (IllegalArgumentException notAProduct) {
      return null;
    }
  }

  /**
   * Calcite's stock dialect for {@code product}, built over {@code context} exactly as the tuned
   * presets are — or, when that product's dialect class has no public {@code (Context)}
   * constructor, Calcite's own context-free default for it; or null when even that cannot be
   * produced.
   *
   * <p>V195 (ADR 0032), verified on the pinned snapshot rather than assumed as D249 asked: Calcite
   * has 40 {@code DatabaseProduct} constants, and every one but {@code JETHRO} has a dialect class
   * with a public {@code (Context)} constructor — {@code UNKNOWN} and {@code SQLSTREAM} included,
   * both by the bare {@code SqlDialect} class itself, which has one — so the context-free fallback
   * this method still carries is untested by anything this sidecar ships today; it stays because a
   * future Calcite upgrade is exactly the kind of change that would start needing it, silently, if
   * it were not here.
   *
   * <p>V196 (ADR 0032): {@code JETHRO.getDialect()} itself throws {@code RuntimeException("Jethro
   * does not support simple creation")} — Calcite's own comment on {@code JethroDataSqlDialect} says
   * a real one needs a live connection's {@code JethroInfoCache}, which nothing here has — so this
   * method returns null for it before ever reaching the constructor question, and {@link #of} and
   * {@link #presets()} both treat {@code "jethro"} as a name they do not accept, the same as any
   * other unmatched name.
   */
  private static @Nullable SqlDialect stockDialect(
      SqlDialect.DatabaseProduct product, SqlDialect.Context context) {
    SqlDialect sample;
    try {
      sample = product.getDialect();
    } catch (RuntimeException calciteRefuses) {
      return null;
    }
    Class<? extends SqlDialect> dialectClass = sample.getClass();
    try {
      Constructor<? extends SqlDialect> constructor = dialectClass.getConstructor(SqlDialect.Context.class);
      return constructor.newInstance(context.withDatabaseProduct(product));
    } catch (ReflectiveOperationException noPublicContextConstructor) {
      return sample;
    }
  }

  /**
   * The operator this dialect spells an <em>integer</em> division with, or null where {@code /}
   * over two integers already is one (F27, ADR 0027).
   *
   * <p>Measured on 2026-09-11: SQLite 3.53.3 and PostgreSQL 16 both answer {@code -7 / 3 = -2} in
   * an integer type, truncating towards zero exactly as Chalk does, so nothing is substituted for
   * them and nothing about their generated SQL changes. DuckDB 1.5.1 answers
   * {@code -2.3333333333333335} in a DOUBLE — its {@code /} is real division whatever the operands
   * are — and {@link ChalkSqlDialects#INTEGER_DIVIDE} is the operator that means there what
   * {@code /} means everywhere else.
   *
   * <p>Substituting on the <em>result type</em> rather than on the operand types is the point: it
   * is the type Chalk declared for the column the expression produces, so the substitution happens
   * exactly where a DOUBLE would otherwise have to satisfy it — in a projection that reaches the
   * client, and in a predicate that never does, where the wrong answer would have been silent.
   */
  /**
   * Whether this dialect's {@code SUM} over an integer answers in a wider type than its argument
   * (F57): DuckDB sums any integer into a {@code HUGEINT}, and PostgreSQL sums a 32-bit integer
   * into a 64-bit one and a 64-bit one into a {@code NUMERIC}. The scan contract reads a column by
   * the type the plan declares, so the writer casts such a sum back to the aggregate's own type
   * where this is true; SQLite and the ANSI rendering keep the argument's type and need nothing.
   */
  public static boolean widensIntegerSums(SqlDialect dialect) {
    return dialect instanceof ChalkSqlDialects.DuckDb
        || dialect instanceof ChalkSqlDialects.Postgres;
  }

  public static @Nullable SqlOperator integerDivision(SqlDialect dialect) {
    return dialect instanceof ChalkSqlDialects.DuckDb ? ChalkSqlDialects.INTEGER_DIVIDE : null;
  }

  /**
   * The context every dialect is built from: the profile's own answers where it has one, and
   * Calcite's ANSI defaults where it does not. The type system is Chalk's, so a cast the converter
   * emits is spelled with the precision Chalk's IR carries rather than the dialect's default.
   */
  private static SqlDialect.Context contextFor(DialectProfile profile) {
    SqlDialect.Context context =
        SqlDialect.EMPTY_CONTEXT
            .withDatabaseProduct(SqlDialect.DatabaseProduct.UNKNOWN)
            .withIdentifierQuoteString(quote(profile.getQuoting()))
            .withQuotedCasing(casing(profile.getQuotedCasing()))
            .withUnquotedCasing(casing(profile.getUnquotedCasing()))
            .withCaseSensitive(profile.getCaseSensitiveIdentifiers())
            .withNullCollation(nullCollation(profile))
            .withDataTypeSystem(ChalkTypeSystem.INSTANCE);
    SqlConformanceEnum conformance = conformance(profile);
    return conformance == null ? context : context.withConformance(conformance);
  }

  /**
   * The quote character. Null — Calcite's "no quoting at all" — only when the profile says so
   * explicitly; an unspecified quoting on a SQL source is refused by the catalog validator, so
   * reaching the default here means an IR source whose SQL is never generated.
   */
  private static String quote(IdentifierQuoting quoting) {
    return switch (quoting) {
      case IDENTIFIER_QUOTING_NONE -> null;
      case IDENTIFIER_QUOTING_BACK_TICK -> "`";
      case IDENTIFIER_QUOTING_BRACKET -> "[";
      default -> "\"";
    };
  }

  private static Casing casing(IdentifierCasing casing) {
    return switch (casing) {
      case IDENTIFIER_CASING_TO_UPPER -> Casing.TO_UPPER;
      case IDENTIFIER_CASING_TO_LOWER -> Casing.TO_LOWER;
      default -> Casing.UNCHANGED;
    };
  }

  /**
   * Where the source puts NULLs without an explicit clause. Only consulted when the source has no
   * clause to be explicit with — {@link PushdownGate} has already refused a sort whose placement
   * would not be honoured — but the dialect needs it to decide whether to emit one at all.
   */
  private static NullCollation nullCollation(DialectProfile profile) {
    return switch (profile.getDefaultNullCollation()) {
      case NULL_COLLATION_LOW -> NullCollation.LOW;
      case NULL_COLLATION_FIRST -> NullCollation.FIRST;
      case NULL_COLLATION_LAST -> NullCollation.LAST;
      default -> NullCollation.HIGH;
    };
  }

  /** What SQL the source accepts. Null keeps the dialect's own default conformance. */
  private static SqlConformanceEnum conformance(DialectProfile profile) {
    return switch (profile.getConformance()) {
      case SQL_CONFORMANCE_DEFAULT -> SqlConformanceEnum.DEFAULT;
      case SQL_CONFORMANCE_LENIENT -> SqlConformanceEnum.LENIENT;
      case SQL_CONFORMANCE_BABEL -> SqlConformanceEnum.BABEL;
      case SQL_CONFORMANCE_STRICT_92 -> SqlConformanceEnum.STRICT_92;
      case SQL_CONFORMANCE_STRICT_99 -> SqlConformanceEnum.STRICT_99;
      case SQL_CONFORMANCE_PRAGMATIC_99 -> SqlConformanceEnum.PRAGMATIC_99;
      case SQL_CONFORMANCE_STRICT_2003 -> SqlConformanceEnum.STRICT_2003;
      case SQL_CONFORMANCE_PRAGMATIC_2003 -> SqlConformanceEnum.PRAGMATIC_2003;
      case SQL_CONFORMANCE_MYSQL_5 -> SqlConformanceEnum.MYSQL_5;
      case SQL_CONFORMANCE_ORACLE_10 -> SqlConformanceEnum.ORACLE_10;
      case SQL_CONFORMANCE_ORACLE_12 -> SqlConformanceEnum.ORACLE_12;
      case SQL_CONFORMANCE_SQL_SERVER_2008 -> SqlConformanceEnum.SQL_SERVER_2008;
      case SQL_CONFORMANCE_PRESTO -> SqlConformanceEnum.PRESTO;
      case SQL_CONFORMANCE_BIG_QUERY -> SqlConformanceEnum.BIG_QUERY;
      default -> null;
    };
  }
}
