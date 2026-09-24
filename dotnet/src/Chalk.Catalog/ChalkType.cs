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

    // The same reason again: a field holds a ChalkType, so the fields live behind an array, compared
    // element by element in Equals below (D291).
    private readonly ChalkField[]? _fields;

    /// <summary>
    /// A <see cref="TypeKind.Struct"/>'s fields, in declared order, and empty for every other kind.
    /// Every field is a scalar — a struct is exactly one level deep.
    /// </summary>
    public IReadOnlyList<ChalkField> Fields
    {
        get => _fields ?? [];
        init => _fields = value is { Count: > 0 } fields ? [.. fields] : null;
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

    /// <summary>
    /// A struct of <paramref name="fields"/>, in order (D291). Each field carries its own nullability;
    /// <paramref name="nullable"/> is whether the struct itself may be NULL, which reads as NULL in
    /// every field.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// There is no field, a field has no name, two names are equal ignoring case, or a field is itself
    /// a LIST or a STRUCT.
    /// </exception>
    public static ChalkType Struct(IEnumerable<ChalkField> fields, bool nullable = false)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var list = fields.ToArray();
        if (list.Length == 0)
        {
            throw new ArgumentException("a STRUCT has at least one field.", nameof(fields));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in list)
        {
            if (string.IsNullOrEmpty(field.Name))
            {
                throw new ArgumentException("a STRUCT field has no name.", nameof(fields));
            }

            if (!names.Add(field.Name))
            {
                throw new ArgumentException(
                    $"field name '{field.Name}' is used twice ignoring case; SQL resolves a field by "
                    + "name ignoring case, so the two would be one field.",
                    nameof(fields));
            }

            if (field.Type.Kind is TypeKind.List or TypeKind.Struct)
            {
                throw new ArgumentException(
                    $"field '{field.Name}' is a {field.Type.Kind.ToString().ToUpperInvariant()}; a "
                    + "STRUCT is one level deep and its fields are scalars.",
                    nameof(fields));
            }
        }

        return new ChalkType(TypeKind.Struct, nullable) { Fields = list };
    }

    /// <summary>The same, non-nullable as a whole.</summary>
    public static ChalkType Struct(params ChalkField[] fields) => Struct((IEnumerable<ChalkField>)fields);

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

        foreach (var field in Fields)
        {
            type.Fields.Add(new Ir.Field { Name = field.Name, Type = field.Type.ToProto() });
        }

        return type;
    }

    /// <summary>Reads an IR type message.</summary>
    public static ChalkType FromProto(Ir.Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var mapped = new ChalkType(type.Kind, type.Nullable, (int)type.Precision, (int)type.Scale);
        if (type.Element is not null)
        {
            mapped = mapped with { Element = FromProto(type.Element) };
        }

        return type.Fields.Count == 0
            ? mapped
            : mapped with
            {
                Fields = [.. type.Fields.Select(f => new ChalkField(f.Name, FromProto(f.Type)))],
            };
    }

    /// <summary>Value equality, element and fields included.</summary>
    public bool Equals(ChalkType other) =>
        Kind == other.Kind
        && Nullable == other.Nullable
        && Precision == other.Precision
        && Scale == other.Scale
        && EqualityComparer<ChalkType?>.Default.Equals(Element, other.Element)
        && Fields.SequenceEqual(other.Fields);

    public override int GetHashCode()
    {
        var hash = HashCode.Combine(Kind, Nullable, Precision, Scale, Element);
        foreach (var field in Fields)
        {
            hash = HashCode.Combine(hash, field);
        }

        return hash;
    }
}

/// <summary>
/// One field of a <see cref="TypeKind.Struct"/> (D291): its name as declared, and its type — a scalar,
/// with its own nullability. A value, compared by value, as the type it belongs to is.
/// </summary>
/// <remarks>
/// A plain value type rather than a positional record: <see cref="ChalkType"/>, <c>KeyOrder</c> and
/// <c>SqlPosition</c> are the public API's only records (D26), and a field needs no more than a name,
/// a type and equality.
/// </remarks>
public readonly struct ChalkField : IEquatable<ChalkField>
{
    /// <summary>A field called <paramref name="name"/> of type <paramref name="type"/>.</summary>
    public ChalkField(string name, ChalkType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Type = type;
    }

    /// <summary>The field's name, as declared. SQL matches it ignoring case.</summary>
    public string Name { get; }

    /// <summary>The field's type; never a LIST or a STRUCT.</summary>
    public ChalkType Type { get; }

    public static bool operator ==(ChalkField left, ChalkField right) => left.Equals(right);

    public static bool operator !=(ChalkField left, ChalkField right) => !left.Equals(right);

    /// <summary>Equal when the names are the same, case included, and the types are equal.</summary>
    public bool Equals(ChalkField other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal) && Type.Equals(other.Type);

    public override bool Equals(object? obj) => obj is ChalkField other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Name, Type);

    /// <summary>Human-readable form, e.g. <c>category:STRING</c>.</summary>
    public override string ToString() => $"{Name}:{Type}";
}
