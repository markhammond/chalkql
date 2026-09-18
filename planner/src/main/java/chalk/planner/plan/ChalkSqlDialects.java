package chalk.planner.plan;

import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.sql.SqlAbstractDateTimeLiteral;
import org.apache.calcite.sql.SqlBasicTypeNameSpec;
import org.apache.calcite.sql.SqlBinaryOperator;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlDataTypeSpec;
import org.apache.calcite.sql.SqlDialect;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlSyntax;
import org.apache.calcite.sql.SqlWriter;
import org.apache.calcite.sql.dialect.DuckDBSqlDialect;
import org.apache.calcite.sql.dialect.SqliteSqlDialect;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.type.InferTypes;
import org.apache.calcite.sql.type.OperandTypes;
import org.apache.calcite.sql.type.ReturnTypes;
import org.apache.calcite.sql.type.SqlTypeUtil;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The two dialects Chalk defines (§1, V33).
 *
 * <p>V33 asked which {@code SqlDialect}s Calcite 1.42 ships and what Chalk's SQLite and DuckDB
 * dialects would have to be derived from. The answer, read off the 1.42 jar: Calcite ships
 * <b>39</b> dialects and both {@code SqliteSqlDialect} and {@code DuckDBSqlDialect} are among them,
 * so neither has to be written from scratch. What each still needs is one override, and they are
 * not the same override:
 *
 * <ul>
 *   <li>{@code SqliteSqlDialect} already spells a fetch as {@code LIMIT … OFFSET …}. Chalk's
 *       subclass exists only to carry the profile's context and to be named in a plan.
 *   <li>{@code DuckDBSqlDialect} does <b>not</b> override {@code unparseOffsetFetch}, so it inherits
 *       the ANSI {@code OFFSET n ROWS FETCH NEXT m ROWS ONLY}. DuckDB's own documented spelling is
 *       {@code LIMIT … OFFSET …}, and that is what Chalk's subclass emits.
 * </ul>
 */
public final class ChalkSqlDialects {
  private ChalkSqlDialects() {}

  /**
   * DuckDB's integer division, {@code //} (F27, ADR 0027). Reached only through {@link
   * SourceDialects#integerDivision}, which substitutes it for a {@code DIVIDE} whose result type is
   * an integer kind when the target is DuckDB.
   *
   * <p>DuckDB is the one dialect Chalk pushes to whose {@code /} is <b>real</b> division: measured
   * on 1.5.1, {@code 5 / 2} is {@code 2.5} and {@code -7 / 3} is {@code -2.3333333333333335}, both
   * DOUBLE, where SQLite, PostgreSQL and Chalk itself answer {@code 2} and {@code -2}. Its
   * {@code //} is the operator that means what they mean: {@code -7 // 3} is {@code -2} and
   * {@code 7 // -3} is {@code -2}, so it truncates towards zero as SQL and Chalk do, and it answers
   * in the operands' own type ({@code INTEGER // INTEGER} is INTEGER, {@code BIGINT // BIGINT} is
   * BIGINT).
   *
   * <p>The precedence is {@code /}'s, which is also {@code //}'s in DuckDB — measured:
   * {@code 1 + 6 // 3} is 3 and {@code 12 // 3 * 2} is 8, so it binds like {@code *} and
   * associates left. It is declared here anyway rather than relied upon, because
   * {@link SqlSyntax#BINARY} groups the <em>operands</em> by it.
   */
  public static final SqlBinaryOperator INTEGER_DIVIDE =
      new SqlBinaryOperator(
          "//",
          SqlKind.OTHER,
          60,
          true,
          ReturnTypes.ARG0_NULLABLE,
          InferTypes.FIRST_KNOWN,
          OperandTypes.NUMERIC_NUMERIC);

  /**
   * A cast to a character type, without the character set.
   *
   * <p>Calcite's default spells one as {@code CAST(x AS VARCHAR CHARACTER SET "UTF-8")}, which is
   * SQL:2011 and which none of the engines Chalk pushes to parses. Chalk has one encoding — UTF-8,
   * end to end — so the clause carries no information any source could act on, and the precision
   * goes with it: a source that stores the column already knows how wide it is, and a cast that
   * declared a width would truncate where Chalk does not.
   *
   * <p>It is reached most often by an entitlement's placeholder, which is a typed NULL of a string
   * column (D161), and by any statement that casts to a string.
   */
  private static @Nullable SqlNode castSpec(SqlDialect dialect, RelDataType type) {
    if (!SqlTypeUtil.inCharFamily(type)) {
      return null;
    }
    return new SqlDataTypeSpec(
        new SqlBasicTypeNameSpec(type.getSqlTypeName(), SqlParserPos.ZERO), SqlParserPos.ZERO);
  }

  /**
   * SQLite, with temporal literals spelled the way SQLite reads them.
   *
   * <p>Calcite's own {@code SqliteSqlDialect} already handles the fetch clause and the join types.
   * What it does not handle is that <b>SQLite has no temporal literal syntax at all</b>:
   * {@code DATE '1995-01-01'} is a parse error there, not a date. SQLite's documented answer is to
   * store and compare temporal values as ISO-8601 text, which is what every mainstream driver
   * writes — Microsoft.Data.Sqlite stores a {@code DateTime} as {@code '1995-06-01 00:00:00'} — and
   * text comparison of ISO-8601 is chronological, so a range predicate over such a column is exact.
   *
   * <p>That is an assumption about the <em>data</em>, not only about the engine, and it is one the
   * conformance kit's temporal probe checks: a database that stores dates as Julian day numbers or
   * Unix epochs will fail it, and its host should not declare range predicates pushable.
   */
  public static final class Sqlite extends SqliteSqlDialect {
    public Sqlite(Context context) {
      super(context);
    }

    @Override
    public void unparseDateTimeLiteral(
        SqlWriter writer, SqlAbstractDateTimeLiteral literal, int leftPrec, int rightPrec) {
      writer.literal("'" + literal.toFormattedString() + "'");
    }

    /**
     * {@code MOD(a, b)} is spelled {@code a % b} (V53, ADR 0024).
     *
     * <p>SQLite has a {@code mod()} function, and it is the wrong one: it is one of the optional
     * <em>floating-point</em> math functions, so it answers in REAL and computes through a double.
     * Measured on the pinned e_sqlite3 3.53.3, and the same on 3.49.1:
     * {@code typeof(mod(10, 7))} is {@code 'real'}, and
     * {@code mod(9223372036854775807, 7)} is <b>1</b> where the answer is 0 — the dividend does not
     * survive the double. Pushing {@code MOD} as a call therefore returned a Double for a column
     * Chalk had declared I32 (the scan contract caught that) and would have returned a wrong
     * <em>value</em> for a wide BIGINT (nothing would have caught that).
     *
     * <p>SQLite's {@code %} operator is the right one on all three counts: it casts its operands to
     * INTEGER, answers in INTEGER, is exact at 64 bits ({@code 9223372036854775807 % 7} is 0), and
     * takes the sign of the dividend as SQL and Chalk do.
     *
     * <p><b>The parentheses are not decoration (V54).</b> Calcite's own dialects substitute a
     * binary operator for {@code MOD} by handing the caller's {@code leftPrec}/{@code rightPrec}
     * straight to {@link SqlSyntax#BINARY} — but {@code SqlCall.unparse} has already decided, from
     * <em>{@code MOD}'s</em> precedence, that no parentheses are needed, and {@code MOD} binds like
     * an atom while {@code %} binds like {@code *}. Written that way,
     * {@code MOD(a, 7) * MOD(b, 3)} comes out as {@code a % 7 * b % 3}, which SQLite reads
     * left-associatively as {@code ((a % 7) * b) % 3} — a silently wrong answer, and exactly the
     * hazard §5 D exists to catch. It was caught here, by the golden in
     * {@code corpus/plans/m7-pushdown/15_sqlite_operator_substitution.sql}, which is what that
     * golden is for. Bracketing the substituted call is unconditional and cheap, and it makes the
     * emitted SQL independent of how the surrounding operators bind.
     */
    @Override
    public void unparseCall(SqlWriter writer, SqlCall call, int leftPrec, int rightPrec) {
      if (call.getKind() == SqlKind.MOD) {
        SqlWriter.Frame frame = writer.startList("(", ")");
        SqlSyntax.BINARY.unparse(writer, SqlStdOperatorTable.PERCENT_REMAINDER, call, 0, 0);
        writer.endList(frame);
        return;
      }

      super.unparseCall(writer, call, leftPrec, rightPrec);
    }

    @Override
    public @Nullable SqlNode getCastSpec(RelDataType type) {
      SqlNode chalk = castSpec(this, type);
      return chalk != null ? chalk : super.getCastSpec(type);
    }

    @Override
    public String toString() {
      return "ChalkSqliteSqlDialect";
    }
  }

  /** DuckDB, with the fetch spelled the way DuckDB documents it. */
  public static final class DuckDb extends DuckDBSqlDialect {
    public DuckDb(Context context) {
      super(context);
    }

    /**
     * {@code LIMIT count OFFSET start}. Calcite's own {@code DuckDBSqlDialect} inherits the ANSI
     * {@code FETCH NEXT n ROWS ONLY} from {@code SqlDialect}; this is the same clause DuckDB
     * actually documents, and the same one {@code SqliteSqlDialect} emits, so the two dialects'
     * goldens differ only where the engines differ.
     */
    @Override
    public void unparseOffsetFetch(SqlWriter writer, @Nullable SqlNode offset, @Nullable SqlNode fetch) {
      unparseFetchUsingLimit(writer, offset, fetch);
    }

    /**
     * An integer {@code DIVIDE} is spelled {@code a // b}, bracketed (F27, ADR 0027).
     *
     * <p>The substitution itself is {@link SourceDialects#integerDivision}'s, made on the pushed
     * subtree where the result type is still known; by the time a {@code SqlCall} reaches a writer
     * there is no type on it to decide from. What is left here is the same job {@link Sqlite}'s
     * {@code MOD} override does: emit the substitute, and bracket it.
     *
     * <p><b>The parentheses are V54's, for V54's reason.</b> {@link SqlSyntax#BINARY} groups the
     * <em>operands</em> by the operator it is handed, and {@code SqlCall.unparse} has already
     * decided about the call's own parentheses from that operator's precedence. Bracketing the
     * substituted call unconditionally makes the emitted SQL independent of both — of how the
     * surrounding operators bind, and of whether a future DuckDB gives {@code //} a precedence it
     * does not have today. It costs one pair of parentheses that a reader can see in the golden.
     */
    @Override
    public void unparseCall(SqlWriter writer, SqlCall call, int leftPrec, int rightPrec) {
      if (call.getOperator() == INTEGER_DIVIDE) {
        SqlWriter.Frame frame = writer.startList("(", ")");
        SqlSyntax.BINARY.unparse(writer, INTEGER_DIVIDE, call, 0, 0);
        writer.endList(frame);
        return;
      }

      super.unparseCall(writer, call, leftPrec, rightPrec);
    }

    @Override
    public @Nullable SqlNode getCastSpec(RelDataType type) {
      SqlNode chalk = castSpec(this, type);
      return chalk != null ? chalk : super.getCastSpec(type);
    }

    @Override
    public String toString() {
      return "ChalkDuckDbSqlDialect";
    }
  }

  /** PostgreSQL, with the same cast spelling: it does not take a character set in a cast either. */
  public static final class Postgres extends org.apache.calcite.sql.dialect.PostgresqlSqlDialect {
    public Postgres(Context context) {
      super(context);
    }

    @Override
    public @Nullable SqlNode getCastSpec(RelDataType type) {
      SqlNode chalk = castSpec(this, type);
      return chalk != null ? chalk : super.getCastSpec(type);
    }

    @Override
    public String toString() {
      return "ChalkPostgresqlSqlDialect";
    }
  }

  /** Whether a dialect is one Chalk defined, for diagnostics. */
  public static boolean isChalkDefined(SqlDialect dialect) {
    return dialect instanceof Sqlite || dialect instanceof DuckDb;
  }
}
