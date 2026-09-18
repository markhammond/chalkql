package chalk.planner.plan;

import chalk.ir.v1.SqlConformance;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.config.CalciteConnectionConfig;
import org.apache.calcite.config.CalciteConnectionConfigImpl;
import org.apache.calcite.config.CalciteConnectionProperty;
import org.apache.calcite.config.Lex;
import org.apache.calcite.config.NullCollation;
import org.apache.calcite.sql.fun.SqlLibrary;
import org.apache.calcite.sql.parser.SqlParser;
import org.apache.calcite.sql.parser.babel.SqlBabelParserImpl;
import org.apache.calcite.sql.validate.SqlConformanceEnum;
import org.apache.calcite.sql.validate.SqlValidator;
import org.apache.calcite.sql2rel.SqlToRelConverter;

/**
 * The pinned parser, validator and converter configuration (docs/design/03-planner.md §4.1). Every
 * value here is part of {@code PlannerConfig.configHash()}: changing one changes plans, so it must
 * change the hash that the client's plan cache keys on.
 *
 * <p>The one exception is the conformance level, which is a per-request option (D34, ADR 0014) like
 * the pushdown level: it is passed in rather than pinned, and only its default appears in
 * {@link #summary()}.
 */
public final class SqlConfigs {
  /**
   * Standard SQL, which is what a statement gets when the request says nothing. This is the D34
   * amendment to D15's pinned {@code LENIENT}: {@code !=}, {@code %}, GROUP BY on a SELECT alias,
   * {@code OFFSET start LIMIT count} and {@code LIMIT start, count} are rejected here and accepted
   * under {@code LENIENT} and {@code BABEL}, which a caller may now ask for per statement.
   */
  public static final SqlConformanceEnum DEFAULT_CONFORMANCE = SqlConformanceEnum.DEFAULT;

  private SqlConfigs() {}

  /**
   * {@link Lex#MYSQL_ANSI}: double quotes quote, unquoted identifiers keep their case, matching is
   * case-insensitive (D15). Quoting and case sensitivity are not conformance's business and stay
   * pinned whatever dialect a request asks for.
   *
   * <p><b>{@code BABEL}, and only {@code BABEL}, is parsed by Calcite's Babel parser</b> (D259, ADR
   * 0039 §2). That is where the syntax the reference marks as Babel-only lives — PostgreSQL's {@code
   * ::} cast, {@code SELECT * EXCLUDE (col)}, {@code SELECT * REPLACE (expr AS col)} — none of which
   * the core parser accepts at any conformance level. Every other level, {@code DEFAULT} included,
   * keeps the core parser and parses exactly as it did before, so no existing plan moves.
   *
   * <p>{@code Lex.MYSQL_ANSI} is kept for the Babel parser rather than swapped for a Lex of its own,
   * and that is a measured choice, not an assumption: {@link Lex} sets quoting, casing and case
   * sensitivity on the {@code SqlParser.Config}, and the parser factory sets the grammar. They are
   * independent, and {@code BabelParserTest} asserts that identifiers quote, keep their case and
   * match case-insensitively under {@code BABEL} exactly as they do under {@code DEFAULT}. Giving
   * Babel its own Lex would have changed identifier behaviour with the dialect, which D15 says is
   * not conformance's business.
   */
  public static SqlParser.Config parser(SqlConformanceEnum conformance) {
    SqlParser.Config config = SqlParser.config().withLex(Lex.MYSQL_ANSI).withConformance(conformance);
    return conformance == SqlConformanceEnum.BABEL
        ? config.withParserFactory(SqlBabelParserImpl.FACTORY)
        : config;
  }

  /**
   * {@link NullCollation#HIGH} means ASC sorts NULLs last and DESC sorts them first, which is what
   * the catalog's declared collations say, so a plain {@code ORDER BY ts} can be satisfied by the
   * declared order rather than needing a sort.
   */
  public static SqlValidator.Config validator(SqlConformanceEnum conformance) {
    return SqlValidator.Config.DEFAULT
        .withDefaultNullCollation(NullCollation.HIGH)
        .withIdentifierExpansion(true)
        .withTypeCoercionEnabled(true)
        .withConformance(conformance);
  }

  /**
   * The request's dialect, one for one with Calcite's own enum.
   *
   * <p>Reads the raw number rather than the generated constant, because a client built against a
   * newer proto can send a value this planner has never heard of; proto3 turns that into {@code
   * UNRECOGNIZED}, and the honest answer is to name the number rather than silently plan something
   * else. The resulting {@link IllegalArgumentException} becomes {@code INVALID_ARGUMENT}.
   */
  public static SqlConformanceEnum conformance(int value) {
    return switch (value) {
      case 0, 1 -> SqlConformanceEnum.DEFAULT; // UNSPECIFIED means DEFAULT.
      case 2 -> SqlConformanceEnum.LENIENT;
      case 3 -> SqlConformanceEnum.BABEL;
      case 4 -> SqlConformanceEnum.STRICT_92;
      case 5 -> SqlConformanceEnum.STRICT_99;
      case 6 -> SqlConformanceEnum.PRAGMATIC_99;
      case 7 -> SqlConformanceEnum.STRICT_2003;
      case 8 -> SqlConformanceEnum.PRAGMATIC_2003;
      case 9 -> SqlConformanceEnum.MYSQL_5;
      case 10 -> SqlConformanceEnum.ORACLE_10;
      case 11 -> SqlConformanceEnum.ORACLE_12;
      case 12 -> SqlConformanceEnum.SQL_SERVER_2008;
      case 13 -> SqlConformanceEnum.PRESTO;
      case 14 -> SqlConformanceEnum.BIG_QUERY;
      default ->
          throw new IllegalArgumentException(
              "PlannerOptions.conformance "
                  + value
                  + " is not a SqlConformance this planner knows; it serves "
                  + SqlConformance.SQL_CONFORMANCE_UNSPECIFIED.getNumber()
                  + ".."
                  + SqlConformance.SQL_CONFORMANCE_BIG_QUERY.getNumber()
                  + ".");
    };
  }

  /**
   * The dialect function libraries a request asked for (D60), one for one with Calcite's
   * {@code SqlLibrary}.
   *
   * <p>Reads the raw numbers rather than the generated constants, for the reason {@link
   * #conformance} does: a client built against a newer proto can send a value this planner has never
   * heard of, and the honest answer is to name the number. {@code STANDARD} is always present and is
   * dropped here, because {@code ChalkOperatorTable} chains the standard table first anyway.
   */
  public static List<SqlLibrary> libraries(List<Integer> values) {
    List<SqlLibrary> libraries = new ArrayList<>(values.size());
    for (int value : values) {
      SqlLibrary library =
          switch (value) {
            case 0, 1 -> null; // UNSPECIFIED and STANDARD: always available.
            case 2 -> SqlLibrary.BIG_QUERY;
            case 3 -> SqlLibrary.CALCITE;
            case 4 -> SqlLibrary.CLICKHOUSE;
            case 5 -> SqlLibrary.HIVE;
            case 6 -> SqlLibrary.MSSQL;
            case 7 -> SqlLibrary.MYSQL;
            case 8 -> SqlLibrary.ORACLE;
            case 9 -> SqlLibrary.POSTGRESQL;
            case 10 -> SqlLibrary.REDSHIFT;
            case 11 -> SqlLibrary.SNOWFLAKE;
            case 12 -> SqlLibrary.SPARK;
            case 13 -> SqlLibrary.SPATIAL;
            case 14 -> SqlLibrary.ALL;
            default ->
                throw new IllegalArgumentException(
                    "PlannerOptions.libraries "
                        + value
                        + " is not a SqlLibrary this planner knows; it serves "
                        + chalk.ir.v1.SqlLibrary.SQL_LIBRARY_UNSPECIFIED.getNumber()
                        + ".."
                        + chalk.ir.v1.SqlLibrary.SQL_LIBRARY_ALL.getNumber()
                        + ".");
          };
      if (library != null && !libraries.contains(library)) {
        libraries.add(library);
      }
    }

    return List.copyOf(libraries);
  }

  /**
   * {@code withExpand(false)} keeps subqueries as {@code RexSubQuery} so the Hep pre-pass owns their
   * removal. {@code withInSubQueryThreshold(MAX_VALUE)} keeps long {@code IN (?, ?, …)} lists — the
   * shape D29's list parameters expand to — as OR chains rather than semi-joins against VALUES,
   * which M1 could not plan.
   */
  public static SqlToRelConverter.Config sqlToRel() {
    return SqlToRelConverter.config()
        .withTrimUnusedFields(true)
        .withExpand(false)
        .withDecorrelationEnabled(true)
        .withInSubQueryThreshold(Integer.MAX_VALUE);
  }

  /**
   * The connection-level settings {@code PlannerImpl} reads, and the one that matters: <b>Calcite
   * 1.42 has two decorrelators</b>, and the one it runs by default is the older
   * {@code RelDecorrelator}. {@code TopDownGeneralDecorrelator} — the Neumann–Kemper algorithm — is
   * behind {@code TOPDOWN_GENERAL_DECORRELATION_ENABLED}, which {@code PlannerImpl.rel} reads from
   * <em>here</em> and not from the {@code SqlToRelConverter} config a caller passes (V26, ADR 0018).
   *
   * <p>Chalk turns it on. D67 says a correlate that survives decorrelation is a refusal, so the
   * planner should be running the strongest decorrelator it has; the general one also rewrites the
   * shapes the older one gives up on — a correlated aggregate on the inner side of a lateral join,
   * which the older one rewrites into a tree whose row type no longer matches and then throws about.
   */
  public static CalciteConnectionConfig connectionConfig() {
    return CONNECTION_CONFIG;
  }

  private static final CalciteConnectionConfig CONNECTION_CONFIG =
      new CalciteConnectionConfigImpl(new java.util.Properties())
          .set(CalciteConnectionProperty.TOPDOWN_GENERAL_DECORRELATION_ENABLED, "true");

  /**
   * A stable summary of the above, for {@code configHash}. The conformance named here is the
   * default; a request that asks for another one is not a different planner configuration, exactly
   * as a request that asks for another pushdown level is not (D34).
   */
  public static String summary() {
    return "lex=MYSQL_ANSI"
        + ";charset=" + org.apache.calcite.util.Util.getDefaultCharset().name()
        + ";conformance=" + DEFAULT_CONFORMANCE
        + ";nullCollation=HIGH"
        + ";identifierExpansion=true"
        + ";typeCoercion=true"
        + ";trimUnusedFields=true"
        + ";expand=false"
        + ";decorrelate=true;generalDecorrelation=true"
        + ";inSubQueryThreshold=" + Integer.MAX_VALUE;
  }
}
