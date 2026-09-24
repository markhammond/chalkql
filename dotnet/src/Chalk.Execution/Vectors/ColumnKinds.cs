using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Vectors;

/// <summary>
/// How a logical type is laid out in memory. Several logical kinds share a layout — DATE and
/// INTERVAL_YEAR are both 32-bit counts, every temporal but DATE is a 64-bit count — so kernels are
/// written once per layout and the logical <see cref="ChalkType"/> supplies the semantics.
/// </summary>
internal enum ColumnKind
{
    /// <summary>One byte per row inside the engine, bit-packed only at a batch boundary (§6.4).</summary>
    Boolean,
    Int8,
    Int16,
    Int32,
    Int64,
    Float,
    Double,

    /// <summary>32-bit offsets into a UTF-8 data buffer.</summary>
    Utf8,

    /// <summary>32-bit offsets into an opaque byte buffer.</summary>
    Binary,

    /// <summary>16-byte little-endian two's-complement unscaled integers.</summary>
    Decimal128,

    /// <summary>16 fixed bytes: UUID, in RFC 4122 order.</summary>
    Bytes16,

    /// <summary>32-bit offsets into a child column of the element's own layout (D58).</summary>
    List,
    
    StringView,

    /// <summary>
    /// One child column per field, each in the field's own layout, and one validity for the struct
    /// (D291): a LIST with N children and no offsets. The struct has no values of its own.
    /// </summary>
    Struct,
}

/// <summary>The layout of each logical type, and the small facts kernels ask about it.</summary>
internal static class ColumnKinds
{
    /// <summary>The storage layout a logical type uses.</summary>
    public static ColumnKind Of(TypeKind kind) => kind switch
    {
        TypeKind.Bool => ColumnKind.Boolean,
        TypeKind.I8 => ColumnKind.Int8,
        TypeKind.I16 => ColumnKind.Int16,
        TypeKind.I32 or TypeKind.Date or TypeKind.IntervalYear => ColumnKind.Int32,
        TypeKind.I64 or TypeKind.Time or TypeKind.Timestamp or TypeKind.TimestampTz
            or TypeKind.IntervalDay => ColumnKind.Int64,
        TypeKind.Fp32 => ColumnKind.Float,
        TypeKind.Fp64 => ColumnKind.Double,
        TypeKind.String => ColumnKind.Utf8,
        TypeKind.Binary => ColumnKind.Binary,
        TypeKind.Decimal => ColumnKind.Decimal128,
        TypeKind.Uuid => ColumnKind.Bytes16,
        TypeKind.List => ColumnKind.List,
        TypeKind.Struct => ColumnKind.Struct,
        _ => throw new UnsupportedFeatureException(
            $"type kind {kind}",
            "The execution engine has no memory layout for it; see docs/design/02-ir.md §3."),
    };

    /// <summary>The layout a logical type uses.</summary>
    public static ColumnKind Of(ChalkType type) => Of(type.Kind);

    /// <summary>Bytes per row, or zero for the two variable-length layouts.</summary>
    public static int Width(ColumnKind kind) => kind switch
    {
        ColumnKind.Boolean or ColumnKind.Int8 => 1,
        ColumnKind.Int16 => 2,
        ColumnKind.Int32 or ColumnKind.Float => 4,
        ColumnKind.Int64 or ColumnKind.Double => 8,
        ColumnKind.Decimal128 or ColumnKind.Bytes16 => 16,
        ColumnKind.StringView => 16,
        _ => 0,
    };

    /// <summary>True for the two layouts that carry an offsets buffer.</summary>
    public static bool IsVariableLength(ColumnKind kind) =>
        kind is ColumnKind.Utf8 or ColumnKind.Binary;

    /// <summary>True for kinds whose ordering and equality are IEEE 754, NaN rules and all.</summary>
    public static bool IsFloatingPoint(TypeKind kind) => kind is TypeKind.Fp32 or TypeKind.Fp64;

    /// <summary>
    /// Refuses a type the engine cannot compare, order or hash: the two composites. A list can be
    /// produced, carried, projected and indexed into (D58), and a struct produced by a function,
    /// carried and taken apart by field access (D291), and neither anything else.
    /// </summary>
    public static void RequireComparable(ChalkType type, string what)
    {
        if (type.Kind == TypeKind.List)
        {
            throw new UnsupportedFeatureException(
                $"{what} on a LIST",
                "v1 lists have no ordering or equality; they can be produced, projected and indexed "
                + "into (docs/design/14-windows-ii.md §5).");
        }

        if (type.Kind == TypeKind.Struct)
        {
            throw new UnsupportedFeatureException(
                $"{what} on a STRUCT",
                "a struct has no ordering or equality; it is produced by a user function, carried, "
                + "and taken apart by field access (docs/design/51-structured-function-results.md §1).");
        }
    }
}
