using Apache.Arrow.Types;
using DuckDB.NET.Native;
using TypeKind = Chalk.Ir.TypeKind;

namespace Chalk.Sources.DuckDb;

/// <summary>
/// The type table of <c>docs/design/24-zero-gc.md</c> §6: which DuckDB logical types the native
/// reader copies into which Chalk kinds, how wide each is, and what a temporal one's unit is.
/// </summary>
/// <remarks>
/// <para>
/// A pair this table does not name sends the whole query to the <c>DbDataReader</c> path, decided
/// from the result's column types before the first chunk. That covers the composite types
/// (<c>LIST</c>, <c>STRUCT</c>, <c>MAP</c>, <c>ARRAY</c>, <c>UNION</c>), the ones with no Chalk
/// spelling (<c>ENUM</c>, <c>INTERVAL</c>, <c>BIT</c>, a bare <c>HUGEINT</c>), and any pair where
/// the declared Chalk type could not hold what DuckDB produced.
/// </para>
/// <para>
/// The unit facts are DuckDB's own, verified against the running library (ADR 0023): <c>DATE</c> is
/// days as INT32, <c>TIME</c> is microseconds as INT64, and the four timestamp types are seconds,
/// milliseconds, microseconds and nanoseconds since the epoch as INT64. <c>TIMESTAMP WITH TIME
/// ZONE</c> is microseconds UTC, exactly like <c>TIMESTAMP</c>.
/// </para>
/// </remarks>
internal static class DuckDbTypes
{
    /// <summary>Whether the native reader copies this DuckDB type into this Chalk kind.</summary>
    public static bool Handles(DuckDBType duckType, TypeKind kind) => kind switch
    {
        TypeKind.Bool => duckType == DuckDBType.Boolean,

        // Every integer width, including the unsigned ones, widened into the declared Chalk width.
        // A value that does not fit is refused rather than truncated: the copy is `checked`.
        TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.I64 =>
            duckType is DuckDBType.TinyInt or DuckDBType.SmallInt or DuckDBType.Integer
                or DuckDBType.BigInt or DuckDBType.UnsignedTinyInt or DuckDBType.UnsignedSmallInt
                or DuckDBType.UnsignedInteger or DuckDBType.UnsignedBigInt,

        TypeKind.Fp32 or TypeKind.Fp64 => duckType is DuckDBType.Float or DuckDBType.Double,
        TypeKind.Decimal => duckType == DuckDBType.Decimal,
        TypeKind.String => duckType == DuckDBType.Varchar,
        TypeKind.Binary => duckType == DuckDBType.Blob,
        TypeKind.Date => duckType == DuckDBType.Date,
        TypeKind.Time => duckType == DuckDBType.Time,
        TypeKind.Timestamp or TypeKind.TimestampTz =>
            duckType is DuckDBType.Timestamp or DuckDBType.TimestampS or DuckDBType.TimestampMs
                or DuckDBType.TimestampNs or DuckDBType.TimestampTz,
        TypeKind.Uuid => duckType == DuckDBType.Uuid,
        _ => false,
    };

    /// <summary>Bytes one staged value of a Chalk kind takes; 0 for the two variable-length kinds.</summary>
    public static int StagingWidth(TypeKind kind) => kind switch
    {
        TypeKind.Bool or TypeKind.I8 => 1,
        TypeKind.I16 => 2,
        TypeKind.I32 or TypeKind.Fp32 or TypeKind.Date => 4,
        TypeKind.I64 or TypeKind.Fp64 or TypeKind.Time or TypeKind.Timestamp
            or TypeKind.TimestampTz => 8,
        TypeKind.Decimal or TypeKind.Uuid => 16,
        _ => 0,
    };

    /// <summary>Bytes one row of a DuckDB vector of this type takes.</summary>
    public static int PhysicalWidth(DuckDBType duckType, DuckDBType decimalStorage) => duckType switch
    {
        DuckDBType.Boolean or DuckDBType.TinyInt or DuckDBType.UnsignedTinyInt => 1,
        DuckDBType.SmallInt or DuckDBType.UnsignedSmallInt => 2,
        DuckDBType.Integer or DuckDBType.UnsignedInteger or DuckDBType.Float
            or DuckDBType.Date => 4,
        DuckDBType.HugeInt or DuckDBType.UnsignedHugeInt or DuckDBType.Uuid
            or DuckDBType.Varchar or DuckDBType.Blob => 16,
        DuckDBType.Decimal => decimalStorage switch
        {
            DuckDBType.SmallInt => 2,
            DuckDBType.Integer => 4,
            DuckDBType.BigInt => 8,
            _ => 16,
        },
        _ => 8,
    };

    /// <summary>
    /// Multiplier and divisor taking a temporal value from DuckDB's unit to the Arrow column's, so
    /// the copy is one multiply and one divide and never a conversion through a CLR date type.
    /// </summary>
    public static (long Multiplier, long Divisor) Units(
        DuckDBType duckType, TypeKind kind, IArrowType arrowType)
    {
        var target = kind switch
        {
            TypeKind.Time => PerSecond(((Time64Type)arrowType).Unit),
            TypeKind.Timestamp or TypeKind.TimestampTz => PerSecond(((TimestampType)arrowType).Unit),
            _ => 0L,
        };
        if (target == 0)
        {
            return (1, 1);
        }

        var source = duckType switch
        {
            DuckDBType.TimestampS => 1L,
            DuckDBType.TimestampMs => 1_000L,
            DuckDBType.TimestampNs => 1_000_000_000L,
            // TIME, TIMESTAMP and TIMESTAMP WITH TIME ZONE are all microseconds.
            _ => 1_000_000L,
        };

        return target >= source ? (target / source, 1) : (1, source / target);
    }

    private static long PerSecond(TimeUnit unit) => unit switch
    {
        TimeUnit.Second => 1L,
        TimeUnit.Millisecond => 1_000L,
        TimeUnit.Microsecond => 1_000_000L,
        _ => 1_000_000_000L,
    };
}
