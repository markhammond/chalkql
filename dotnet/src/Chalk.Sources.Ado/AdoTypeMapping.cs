using System.Data;
using System.Data.Common;
using System.Globalization;
using Chalk.Catalog;
using Chalk.Ir;

namespace Chalk.Sources.Ado;

/// <summary>
/// SQL type names and CLR types to Chalk types, and back (§3). Two directions, both needed:
/// discovery reads the source's declared column types, and the reader checks each value's CLR type
/// against the column Chalk promised.
/// </summary>
/// <remarks>
/// <para>
/// The mapping is by <em>declared type name</em> rather than by the provider's own type enum,
/// because that is the only thing every provider agrees on: <c>GetSchema("Columns")</c> reports a
/// name, an information-schema query reports a name, and SQLite reports the name the DDL used and
/// nothing else. Names are matched case-insensitively with any parameters stripped, so
/// <c>DECIMAL(15, 2)</c>, <c>numeric(15,2)</c> and <c>NUMERIC</c> all land in the same place.
/// </para>
/// <para>
/// SQLite's dynamic typing is the reason the reader checks: the declared type is a claim about the
/// column and a value can still arrive as anything. A mismatch is a
/// <see cref="SourceContractException"/> naming the column and both types, never a silent
/// conversion.
/// </para>
/// </remarks>
public static class AdoTypeMapping
{
    /// <summary>
    /// The Chalk type for a declared SQL type name, or null when nothing maps — which registration
    /// reports rather than guessing at.
    /// </summary>
    /// <param name="sqlType">The declared type, with or without parameters.</param>
    /// <param name="nullable">Whether the column admits NULL.</param>
    /// <param name="precision">Declared numeric precision, or 0.</param>
    /// <param name="scale">Declared numeric scale, or 0.</param>
    public static ChalkType? FromSqlType(string sqlType, bool nullable, int precision = 0, int scale = 0)
    {
        ArgumentNullException.ThrowIfNull(sqlType);
        var (name, declaredPrecision, declaredScale) = Parse(sqlType);
        precision = precision > 0 ? precision : declaredPrecision;
        scale = scale > 0 ? scale : declaredScale;

        return name switch
        {
            "BOOL" or "BOOLEAN" or "BIT" => ChalkType.Bool(nullable),

            "TINYINT" or "INT1" => ChalkType.Int8(nullable),
            "SMALLINT" or "INT2" or "SHORT" => ChalkType.Int16(nullable),
            "INT" or "INTEGER" or "INT4" or "MEDIUMINT" or "SERIAL" =>
                ChalkType.Int32(nullable),
            "BIGINT" or "INT8" or "LONG" or "BIGSERIAL" or "HUGEINT" =>
                ChalkType.Int64(nullable),

            "REAL" or "FLOAT4" => ChalkType.Float32(nullable),
            "DOUBLE" or "DOUBLE PRECISION" or "FLOAT" or "FLOAT8" =>
                ChalkType.Float64(nullable),

            "DECIMAL" or "NUMERIC" or "DEC" or "MONEY" =>
                ChalkType.Decimal(precision > 0 ? precision : 28, scale > 0 ? scale : 10, nullable),

            "CHAR" or "VARCHAR" or "TEXT" or "NCHAR" or "NVARCHAR" or "NTEXT" or "STRING"
                or "CLOB" or "CHARACTER VARYING" or "CHARACTER" or "UUID_TEXT" =>
                ChalkType.String(nullable),

            "BLOB" or "BYTEA" or "BINARY" or "VARBINARY" or "IMAGE" =>
                ChalkType.Binary(nullable),

            "DATE" => ChalkType.Date(nullable),
            "TIME" or "TIME WITHOUT TIME ZONE" => ChalkType.Time(TimePrecision(precision), nullable),
            "TIMESTAMP" or "DATETIME" or "DATETIME2" or "TIMESTAMP WITHOUT TIME ZONE" =>
                ChalkType.Timestamp(TimePrecision(precision), nullable),
            "TIMESTAMPTZ" or "TIMESTAMP WITH TIME ZONE" or "DATETIMEOFFSET" =>
                ChalkType.TimestampTz(TimePrecision(precision), nullable),

            "UUID" or "UNIQUEIDENTIFIER" or "GUID" => ChalkType.Uuid(nullable),

            _ => null,
        };
    }

    /// <summary>
    /// Whether <paramref name="value"/> — as the reader handed it over — is a value of
    /// <paramref name="type"/>. What the reader checks per column, once per batch's first non-NULL
    /// value rather than per row.
    /// </summary>
    public static bool IsCompatible(object value, ChalkType type) => type.Kind switch
    {
        TypeKind.Bool => value is bool or byte or short or int or long,
        TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.I64 =>
            value is sbyte or byte or short or ushort or int or uint or long or ulong,
        TypeKind.Fp32 or TypeKind.Fp64 => value is float or double or decimal,
        TypeKind.Decimal => value is decimal or double or float or long or int,
        TypeKind.String => value is string,
        TypeKind.Binary => value is byte[] or ReadOnlyMemory<byte>,
        // DateOnly and TimeOnly are what a modern driver hands back for DATE and TIME — DuckDB.NET
        // does, and Npgsql does — so they belong here next to the older shapes rather than being a
        // contract error.
        TypeKind.Date => value is DateTime or DateOnly or DateTimeOffset or string,
        TypeKind.Timestamp => value is DateTime or DateTimeOffset or DateOnly or string,
        TypeKind.TimestampTz => value is DateTimeOffset or DateTime or string,
        TypeKind.Time => value is TimeSpan or TimeOnly or DateTime or string,
        TypeKind.Uuid => value is Guid or string or byte[],
        _ => false,
    };

    /// <summary>
    /// The <see cref="DbType"/> a bound parameter of this Chalk type gets. Providers vary in what
    /// they infer from a bare CLR value, and saying so explicitly is what stops a decimal being
    /// bound as a double.
    /// </summary>
    public static DbType ToDbType(ChalkType type) => type.Kind switch
    {
        TypeKind.Bool => DbType.Boolean,
        TypeKind.I8 => DbType.SByte,
        TypeKind.I16 => DbType.Int16,
        TypeKind.I32 => DbType.Int32,
        TypeKind.I64 => DbType.Int64,
        TypeKind.Fp32 => DbType.Single,
        TypeKind.Fp64 => DbType.Double,
        TypeKind.Decimal => DbType.Decimal,
        TypeKind.String => DbType.String,
        TypeKind.Binary => DbType.Binary,
        TypeKind.Date => DbType.Date,
        TypeKind.Time => DbType.Time,
        TypeKind.Timestamp => DbType.DateTime2,
        TypeKind.TimestampTz => DbType.DateTimeOffset,
        TypeKind.Uuid => DbType.Guid,
        _ => DbType.Object,
    };

    /// <summary>The type name, precision and scale of a declared type like <c>DECIMAL(15, 2)</c>.</summary>
    internal static (string Name, int Precision, int Scale) Parse(string sqlType)
    {
        var text = sqlType.Trim();
        var open = text.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return (Normalise(text), 0, 0);
        }

        var close = text.IndexOf(')', open);
        var name = Normalise(text[..open]);
        if (close < 0)
        {
            return (name, 0, 0);
        }

        var arguments = text[(open + 1)..close].Split(',');
        var precision = ParseInt(arguments[0]);
        var scale = arguments.Length > 1 ? ParseInt(arguments[1]) : 0;

        // `VARCHAR(80)` is a length, not a precision — only the numeric and temporal families read
        // the parameters, and a length would otherwise arrive as a DECIMAL precision.
        var suffix = text[(close + 1)..].Trim();
        return (
            suffix.Length == 0 ? name : Normalise(name + " " + suffix),
            precision,
            scale);
    }

    private static string Normalise(string name) =>
        string.Join(' ', name.Trim().ToUpperInvariant().Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static int ParseInt(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    /// <summary>
    /// A declared temporal precision, clamped to what the IR carries. Zero — which is what most
    /// providers report — means "the source did not say", and Chalk's own default applies (D16).
    /// </summary>
    private static int TimePrecision(int precision) => precision is > 0 and <= 9 ? precision : 9;

    /// <summary>The CLR type a provider column reports, for a mismatch message.</summary>
    internal static string Describe(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetFieldType(ordinal)?.Name ?? "unknown";
        }
        catch (NotSupportedException)
        {
            return "unknown";
        }
    }
}
