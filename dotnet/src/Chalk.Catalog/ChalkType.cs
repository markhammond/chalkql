using Chalk.Ir;

namespace Chalk.Catalog;

/// <summary>
/// A logical column or expression type. The one <c>readonly record struct</c> in the public API
/// (D26): it is a value, not a surface that grows. Semantics per <c>docs/design/02-ir.md</c> §3.
/// </summary>
/// <param name="Kind">The type's kind.</param>
/// <param name="Nullable">The only nullability channel in the IR.</param>
/// <param name="Precision">DECIMAL total digits; TIMESTAMP / TIMESTAMP_TZ / TIME fractional-second digits. Zero elsewhere.</param>
/// <param name="Scale">DECIMAL only.</param>
public readonly record struct ChalkType(TypeKind Kind, bool Nullable, int Precision = 0, int Scale = 0)
{
    // A struct cannot hold itself, and Nullable<ChalkType> is a struct too, so the element lives
    // behind a one-element array. Nothing outside sees it: Element is the property, and Equals and
    // GetHashCode below compare the element by value rather than by reference (D58).
    private readonly ChalkType[]? _element;

    /// <summary>
    /// A <see cref="TypeKind.List"/>'s element type, and null for every other kind. The element is
    /// never itself a list — v1 lists are exactly one level deep.
    /// </summary>
    public ChalkType? Element
    {
        get => _element is null ? null : _element[0];
        init => _element = value is { } element ? [element] : null;
    }

    public static ChalkType Bool(bool nullable = false) => new(TypeKind.Bool, nullable);

    public static ChalkType Int8(bool nullable = false) => new(TypeKind.I8, nullable);

    public static ChalkType Int16(bool nullable = false) => new(TypeKind.I16, nullable);

    public static ChalkType Int32(bool nullable = false) => new(TypeKind.I32, nullable);

    public static ChalkType Int64(bool nullable = false) => new(TypeKind.I64, nullable);

    public static ChalkType Float32(bool nullable = false) => new(TypeKind.Fp32, nullable);

    public static ChalkType Float64(bool nullable = false) => new(TypeKind.Fp64, nullable);

    public static ChalkType String(bool nullable = false) => new(TypeKind.String, nullable);

    public static ChalkType Binary(bool nullable = false) => new(TypeKind.Binary, nullable);

    public static ChalkType Date(bool nullable = false) => new(TypeKind.Date, nullable);

    public static ChalkType Time(int precision = 6, bool nullable = false) =>
        new(TypeKind.Time, nullable, precision);

    public static ChalkType Timestamp(int precision = 9, bool nullable = false) =>
        new(TypeKind.Timestamp, nullable, precision);

    public static ChalkType TimestampTz(int precision = 9, bool nullable = false) =>
        new(TypeKind.TimestampTz, nullable, precision);

    public static ChalkType Decimal(int precision = 28, int scale = 10, bool nullable = false) =>
        new(TypeKind.Decimal, nullable, precision, scale);

    public static ChalkType Uuid(bool nullable = false) => new(TypeKind.Uuid, nullable);

    public static ChalkType IntervalDay(bool nullable = false) => new(TypeKind.IntervalDay, nullable);

    public static ChalkType IntervalYear(bool nullable = false) => new(TypeKind.IntervalYear, nullable);

    /// <summary>
    /// A list of <paramref name="element"/> (D58). The element carries its own nullability — whether
    /// the list may hold NULLs — and <paramref name="nullable"/> is whether the list itself may be.
    /// </summary>
    /// <exception cref="ArgumentException">The element is itself a list.</exception>
    public static ChalkType List(ChalkType element, bool nullable = false)
    {
        if (element.Kind == TypeKind.List)
        {
            throw new ArgumentException(
                "a LIST's element may not itself be a LIST: v1 lists are one level deep "
                + "(docs/design/14-windows-ii.md §5).",
                nameof(element));
        }

        return new ChalkType(TypeKind.List, nullable) { Element = element };
    }

    /// <summary>The same type, nullable or not.</summary>
    public ChalkType WithNullable(bool nullable) => this with { Nullable = nullable };

    /// <summary>Human-readable form, e.g. <c>DECIMAL(28,10)?</c>.</summary>
    public override string ToString() => IrTypes.Describe(ToProto());

    /// <summary>The IR message for this type.</summary>
    public Ir.Type ToProto()
    {
        var type = new Ir.Type
        {
            Kind = Kind,
            Nullable = Nullable,
            Precision = (uint)Precision,
            Scale = (uint)Scale,
        };

        if (Element is { } element)
        {
            type.Element = element.ToProto();
        }

        return type;
    }

    /// <summary>Reads an IR type message.</summary>
    public static ChalkType FromProto(Ir.Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var mapped = new ChalkType(type.Kind, type.Nullable, (int)type.Precision, (int)type.Scale);
        return type.Element is null ? mapped : mapped with { Element = FromProto(type.Element) };
    }

    /// <summary>Value equality, element included.</summary>
    public bool Equals(ChalkType other) =>
        Kind == other.Kind
        && Nullable == other.Nullable
        && Precision == other.Precision
        && Scale == other.Scale
        && EqualityComparer<ChalkType?>.Default.Equals(Element, other.Element);

    public override int GetHashCode() =>
        HashCode.Combine(Kind, Nullable, Precision, Scale, Element);
}
