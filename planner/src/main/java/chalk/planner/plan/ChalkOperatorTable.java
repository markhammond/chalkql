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

  /** Just Chalk's own operators, for the clash check a user function is registered through. */
  public static SqlOperatorTable chalkOperators() {
    return SqlOperatorTables.of(ImmutableList.of(TIME_BUCKET, FINGERPRINT, PRESENT));
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
            SqlStdOperatorTable.instance(),
            SqlOperatorTables.of(ImmutableList.of(TIME_BUCKET, FINGERPRINT, PRESENT)));
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
    return "operators=std+TIME_BUCKET+FINGERPRINT+PRESENT";
  }
}
