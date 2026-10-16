package chalk.planner.plan;

import com.google.common.collect.ImmutableList;
import java.util.List;
import org.apache.calcite.sql.SqlFunction;
import org.apache.calcite.sql.SqlFunctionCategory;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlOperatorTable;
import org.apache.calcite.sql.fun.SqlLibrary;
import org.apache.calcite.sql.fun.SqlLibraryOperatorTableFactory;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.type.OperandTypes;
import org.apache.calcite.sql.type.ReturnTypes;
import org.apache.calcite.sql.type.SqlTypeFamily;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.sql.util.SqlOperatorTables;

/**
 * The functions Chalk adds to the standard operator table (D52,
 * {@code 13-window-functions.md} §2 and §3).
 *
 * <p>Three of Chalk's own. {@code TIME_BUCKET(INTERVAL size, value [, origin])} is the tumbling-window
 * function: the start of the bucket of width {@code size} that contains {@code value}, with buckets
 * aligned to {@code origin} — 1970-01-01T00:00:00 when it is not given. PostgreSQL's {@code date_bin}
 * semantics, and the same thing Calcite's own {@code TUMBLE} table function computes row by row,
 * which is why {@link chalk.planner.plan.rules.ChalkTumbleRule} can rewrite one into the other.
 */
public final class ChalkOperatorTable {
  private ChalkOperatorTable() {}

  /**
   * {@code TIME_BUCKET(INTERVAL size, DATE | TIMESTAMP | TIMESTAMP_TZ value [, origin])}, returning
   * the value's own type. The interval comes first so the reading matches Calcite's {@code TUMBLE}
   * and PostgreSQL's {@code date_bin}, both of which put the width before the value.
   */
  public static final SqlFunction TIME_BUCKET =
      new SqlFunction(
          "TIME_BUCKET",
          SqlKind.OTHER_FUNCTION,
          ReturnTypes.ARG1_NULLABLE,
          null,
          OperandTypes.or(
              OperandTypes.family(SqlTypeFamily.DATETIME_INTERVAL, SqlTypeFamily.DATETIME),
              OperandTypes.family(
                  SqlTypeFamily.DATETIME_INTERVAL, SqlTypeFamily.DATETIME, SqlTypeFamily.DATETIME)),
          SqlFunctionCategory.TIMEDATE);

  /**
   * {@code FINGERPRINT(STRING value, STRING key)}, returning 32 lower-case hex characters — the
   * first 16 bytes of the HMAC-SHA-256 of the value's UTF-8 bytes keyed with the key's
   * (16-entitlements.md §4). NULL in, NULL out.
   *
   * <p>It is a <em>stable pseudonym</em>, which is what makes it worth shipping: equal values
   * fingerprint equally under one key, so an equality join on the token joins masked rows across
   * tenancies without either value being disclosed. No dialect has it, so it never pushes (§3.8).
   */
  public static final SqlFunction FINGERPRINT =
      new SqlFunction(
          "FINGERPRINT",
          SqlKind.OTHER_FUNCTION,
          ReturnTypes.explicit(SqlTypeName.VARCHAR, 32)
              .andThen(org.apache.calcite.sql.type.SqlTypeTransforms.TO_NULLABLE),
          null,
          OperandTypes.STRING_STRING,
          SqlFunctionCategory.STRING);

  /**
   * {@code PRESENT(value)}, returning whether there is a value at all: not NULL, and for a string
   * not empty. Never NULL itself, which is the point — it is what a consumer asks when a constant
   * mask has hidden whether the column held anything (§1).
   */
  public static final SqlFunction PRESENT =
      new SqlFunction(
          "PRESENT",
          SqlKind.OTHER_FUNCTION,
          ReturnTypes.BOOLEAN_NOT_NULL,
          null,
          OperandTypes.ANY,
          SqlFunctionCategory.SYSTEM);

  /**
   * {@code ILIKE} and {@code NOT ILIKE}, always (D316). Calcite keeps them in its POSTGRESQL library,
   * but Chalk defines what ILIKE means itself — LIKE with both sides folded as LOWER folds — so it is
   * not PostgreSQL's to lend, and asking for that library would bring every other PostgreSQL function
   * with it (and refuse a catalog whose user functions share a name with one).
   */
  /**
   * {@code LEAST(a, …)} and {@code GREATEST(a, …)} over any comparable kind (D337, design 63 §6).
   * NULL arguments are skipped, as PostgreSQL and DuckDB skip them: the result is NULL only when every
   * argument is, so it is nullable only when every argument is. The planner writes each as the
   * comparisons it stands for ({@code chalk.planner.ir.ExtremumConvertlet}), so the executors need
   * nothing of their own and a source that runs a {@code CASE} runs it.
   */
  public static final SqlFunction LEAST = extremum("LEAST", SqlKind.LEAST);

  /** See {@link #LEAST}. */
  public static final SqlFunction GREATEST = extremum("GREATEST", SqlKind.GREATEST);

  private static SqlFunction extremum(String name, SqlKind kind) {
    return new SqlFunction(
        name,
        kind,
        binding -> {
          java.util.List<org.apache.calcite.rel.type.RelDataType> types =
              binding.collectOperandTypes();
          org.apache.calcite.rel.type.RelDataType common =
              binding.getTypeFactory().leastRestrictive(types);
          if (common == null) {
            throw binding.newError(
                org.apache.calcite.util.Static.RESOURCE.typeNotComparableNear(
                    String.valueOf(types.get(0)), String.valueOf(types.get(types.size() - 1))));
          }
          boolean everyNullable =
              types.stream().allMatch(org.apache.calcite.rel.type.RelDataType::isNullable);
          return binding.getTypeFactory().createTypeWithNullability(common, everyNullable);
        },
        org.apache.calcite.sql.type.InferTypes.FIRST_KNOWN,
        OperandTypes.SAME_VARIADIC,
        SqlFunctionCategory.SYSTEM);
  }

  private static final ImmutableList<org.apache.calcite.sql.SqlOperator> OWN =
      ImmutableList.of(
          LEAST,
          GREATEST,
          TIME_BUCKET,
          FINGERPRINT,
          PRESENT,
          org.apache.calcite.sql.fun.SqlLibraryOperators.ILIKE,
          org.apache.calcite.sql.fun.SqlLibraryOperators.NOT_ILIKE);

  /** The precision of {@code CURRENT_TIMESTAMP} and {@code LOCALTIMESTAMP} when none is written. */
  public static final int DEFAULT_TIMESTAMP_PRECISION = 6;

  /**
   * {@code CURRENT_TIMESTAMP} and {@code LOCALTIMESTAMP} as ChalkQL types them (D337): a timestamp
   * of precision 6 when none is written, the SQL standard's default and PostgreSQL's, rather than
   * Calcite's 0, which truncates to the second; and any precision up to the type system's 9 when
   * one is. Both read the execution's one instant as a UTC wall clock: ChalkQL has no session time
   * zone, so the two are the same value.
   */
  private static final class StandardTimestamp
      extends org.apache.calcite.sql.fun.SqlAbstractTimeFunction {
    StandardTimestamp(String name) {
      super(name, SqlTypeName.TIMESTAMP);
    }

    @Override
    public org.apache.calcite.rel.type.RelDataType inferReturnType(
        org.apache.calcite.sql.SqlOperatorBinding binding) {
      int precision = DEFAULT_TIMESTAMP_PRECISION;
      if (binding.getOperandCount() == 1) {
        Integer written = binding.getOperandLiteralValue(0, Integer.class);
        int max = binding.getTypeFactory().getTypeSystem().getMaxPrecision(SqlTypeName.TIMESTAMP);
        if (written == null || written < 0 || written > max) {
          throw binding.newError(
              org.apache.calcite.util.Static.RESOURCE.argumentMustBeValidPrecision(
                  getName(), 0, max));
        }
        precision = written;
      }
      return binding.getTypeFactory().createSqlType(SqlTypeName.TIMESTAMP, precision);
    }
  }

  /** {@code CURRENT_TIMESTAMP}, as {@link StandardTimestamp} types it. */
  public static final SqlFunction CURRENT_TIMESTAMP = new StandardTimestamp("CURRENT_TIMESTAMP");

  /**
   * {@code CURRENT_TIME} and {@code LOCALTIME} as ChalkQL types them (F191): the time of day of the
   * execution's one instant, read as a UTC wall clock as {@code CURRENT_TIMESTAMP} reads it, so the
   * two are the same value. A time of precision 6 when none is written, as a timestamp's is (D337),
   * rather than the standard's 0, which truncates to the second; and any precision up to TIME's 6
   * when one is. The planner writes each as {@code CAST(CURRENT_TIMESTAMP(p) AS TIME(p))}
   * ({@code chalk.planner.ir.TimeOfDayConvertlet}), so the executors need nothing of their own and
   * no source is sent either.
   */
  private static final class StandardTime extends org.apache.calcite.sql.fun.SqlAbstractTimeFunction {
    StandardTime(String name) {
      super(name, SqlTypeName.TIME);
    }

    @Override
    public org.apache.calcite.rel.type.RelDataType inferReturnType(
        org.apache.calcite.sql.SqlOperatorBinding binding) {
      int precision = DEFAULT_TIMESTAMP_PRECISION;
      if (binding.getOperandCount() == 1) {
        Integer written = binding.getOperandLiteralValue(0, Integer.class);
        int max = binding.getTypeFactory().getTypeSystem().getMaxPrecision(SqlTypeName.TIME);
        if (written == null || written < 0 || written > max) {
          throw binding.newError(
              org.apache.calcite.util.Static.RESOURCE.argumentMustBeValidPrecision(
                  getName(), 0, max));
        }
        precision = written;
      }
      return binding.getTypeFactory().createSqlType(SqlTypeName.TIME, precision);
    }
  }

  /** {@code CURRENT_TIME}, as {@link StandardTime} types it. */
  public static final SqlFunction CURRENT_TIME = new StandardTime("CURRENT_TIME");

  /** {@code LOCALTIME}: the same, as {@code LOCALTIMESTAMP} is {@code CURRENT_TIMESTAMP}. */
  public static final SqlFunction LOCALTIME = new StandardTime("LOCALTIME");

  /**
   * Chained before the standard table, so a statement resolves these rather than Calcite's: a
   * chain answers a name with every table's operator in order, and the first is the one used.
   */
  private static final ImmutableList<org.apache.calcite.sql.SqlOperator> TIME =
      ImmutableList.of(
          CURRENT_TIMESTAMP, new StandardTimestamp("LOCALTIMESTAMP"), CURRENT_TIME, LOCALTIME);

  /** Just Chalk's own operators, for the clash check a user function is registered through. */
  public static SqlOperatorTable chalkOperators() {
    return SqlOperatorTables.of(OWN);
  }

  /** The standard table with Chalk's functions chained after it. */
  public static SqlOperatorTable instance() {
    return instance(ImmutableList.of());
  }

  /**
   * The same, plus the dialect libraries this request asked for (D60,
   * {@code 14-windows-ii.md} §6). {@code STRING_AGG} and {@code ARRAY_AGG} live in POSTGRESQL,
   * {@code ARRAY_TO_STRING} in BIG_QUERY, and a host that wants them says so per statement rather
   * than the planner deciding for everyone.
   *
   * <p>The libraries come last, so a dialect can never shadow a standard operator: the chain returns
   * the first table that resolves a name.
   */
  public static SqlOperatorTable instance(List<SqlLibrary> libraries) {
    return instance(libraries, chalk.planner.catalog.UserFunctions.EMPTY);
  }

  /**
   * The same, plus the functions the catalog declares (D77). They come <em>last</em>, after the
   * standard table, Chalk's own and the request's libraries, so a built-in always wins — which is
   * safe only because {@code UserFunctions} refuses a clash at registration rather than shadowing
   * one here.
   */
  public static SqlOperatorTable instance(
      List<SqlLibrary> libraries, chalk.planner.catalog.UserFunctions functions) {
    SqlOperatorTable table =
        SqlOperatorTables.chain(
            SqlOperatorTables.of(TIME),
            SqlStdOperatorTable.instance(),
            SqlOperatorTables.of(OWN));
    if (!libraries.isEmpty()) {
      table =
          SqlOperatorTables.chain(
              table, SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(libraries));
    }
    if (!functions.isEmpty()) {
      table = SqlOperatorTables.chain(table, functions.operatorTable());
    }
    return table;
  }

  /**
   * A stable summary for {@code PlannerConfig.configHash()}. The libraries are <em>not</em> part of
   * it: like the conformance level they are a per-request option, and a client that asks for one
   * puts it in its own cache key (D60).
   */
  public static String summary() {
    return "operators=std+TIME_BUCKET+FINGERPRINT+PRESENT+ILIKE";
  }
}
