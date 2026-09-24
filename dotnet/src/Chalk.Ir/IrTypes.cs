using System.Globalization;

namespace Chalk.Ir;

/// <summary>Small helpers over the IR's <see cref="Type"/> message. See <c>docs/design/02-ir.md</c> §3.</summary>
public static class IrTypes
{
    /// <summary>True when the kind carries a meaningful <c>precision</c>.</summary>
    public static bool HasPrecision(TypeKind kind) =>
        kind is TypeKind.Decimal or TypeKind.Time or TypeKind.Timestamp or TypeKind.TimestampTz;

    /// <summary>True when the kind carries a meaningful <c>scale</c>.</summary>
    public static bool HasScale(TypeKind kind) => kind == TypeKind.Decimal;

    /// <summary>True for the exact and approximate numeric kinds.</summary>
    public static bool IsNumeric(TypeKind kind) => kind is TypeKind.I8 or TypeKind.I16 or TypeKind.I32
        or TypeKind.I64 or TypeKind.Fp32 or TypeKind.Fp64 or TypeKind.Decimal;

    /// <summary>True for the integer kinds.</summary>
    public static bool IsInteger(TypeKind kind) =>
        kind is TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.I64;

    /// <summary>True for DATE, TIME, TIMESTAMP and TIMESTAMP_TZ.</summary>
    public static bool IsTemporal(TypeKind kind) =>
        kind is TypeKind.Date or TypeKind.Time or TypeKind.Timestamp or TypeKind.TimestampTz;

    /// <summary>True for the two interval kinds.</summary>
    public static bool IsInterval(TypeKind kind) =>
        kind is TypeKind.IntervalDay or TypeKind.IntervalYear;

    /// <summary>
    /// True for the kinds a v1 <c>LIST</c> or <c>COMPOSITE</c> may hold, and a plan may compare, group
    /// or sort by: everything but those two non-scalar kinds, which is what makes both exactly one
    /// level deep (D58, D291).
    /// </summary>
    public static bool IsScalar(TypeKind kind) =>
        kind is not (TypeKind.List or TypeKind.Composite or TypeKind.Unspecified);

    /// <summary>True for the two non-scalar kinds, <c>LIST</c> and <c>COMPOSITE</c> (D58, D291).</summary>
    public static bool IsNonScalar(TypeKind kind) => kind is TypeKind.List or TypeKind.Composite;

    /// <summary>
    /// The number of time units per second at a TIMESTAMP / TIMESTAMP_TZ precision: precision 0–3
    /// counts milliseconds, 4–6 microseconds, 7–9 nanoseconds (<c>02-ir.md</c> §3).
    /// </summary>
    public static long TimestampUnitsPerSecond(uint precision) => precision switch
    {
        <= 3 => 1_000L,
        <= 6 => 1_000_000L,
        _ => 1_000_000_000L,
    };

    /// <summary>Human-readable type, e.g. <c>DECIMAL(28,10)?</c>. <c>?</c> marks nullable.</summary>
    public static string Describe(Type? type)
    {
        if (type is null)
        {
            return "<none>";
        }

        var name = type.Kind switch
        {
            TypeKind.Unspecified => "UNSPECIFIED",
            TypeKind.Bool => "BOOL",
            TypeKind.I8 => "I8",
            TypeKind.I16 => "I16",
            TypeKind.I32 => "I32",
            TypeKind.I64 => "I64",
            TypeKind.Fp32 => "FP32",
            TypeKind.Fp64 => "FP64",
            TypeKind.String => "STRING",
            TypeKind.Binary => "BINARY",
            TypeKind.Date => "DATE",
            TypeKind.Time => "TIME",
            TypeKind.Timestamp => "TIMESTAMP",
            TypeKind.TimestampTz => "TIMESTAMP_TZ",
            TypeKind.Decimal => "DECIMAL",
            TypeKind.Uuid => "UUID",
            TypeKind.IntervalDay => "INTERVAL_DAY",
            TypeKind.IntervalYear => "INTERVAL_YEAR",
            TypeKind.List => "LIST",
            TypeKind.Composite => "COMPOSITE",
            _ => $"KIND_{(int)type.Kind}",
        };

        var suffix = type.Kind switch
        {
            TypeKind.Decimal => string.Create(
                CultureInfo.InvariantCulture, $"({type.Precision},{type.Scale})"),
            TypeKind.Time or TypeKind.Timestamp or TypeKind.TimestampTz =>
                string.Create(CultureInfo.InvariantCulture, $"({type.Precision})"),
            TypeKind.List => "<" + Describe(type.Element) + ">",

            // `COMPOSITE(category STRING, confidence FP64)`: each field by name and type, as a
            // column is declared.
            TypeKind.Composite => "("
                + string.Join(", ", type.Fields.Select(f => $"{f.Name} {Describe(f.Type)}"))
                + ")",
            _ => string.Empty,
        };

        return name + suffix + (type.Nullable ? "?" : string.Empty);
    }

    /// <summary>Human-readable row type, e.g. <c>[symbol:STRING?, ts:TIMESTAMP(9)]</c>.</summary>
    public static string Describe(RowType? rowType) =>
        rowType is null
            ? "<none>"
            : "[" + string.Join(", ", rowType.Fields.Select(f => $"{f.Name}:{Describe(f.Type)}")) + "]";
}
