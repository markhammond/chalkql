using System.Linq.Expressions;
using System.Reflection;
using Apache.Arrow.Types;
using Chalk.Catalog;
using TypeKind = Chalk.Ir.TypeKind;

namespace Chalk.Sources.Poco;

/// <summary>The physical array a column is extracted into. One member per family of Arrow encoders.</summary>
internal enum PocoStorageKind
{
    Bool,
    Int8,
    Int16,
    Int32,
    Int64,
    Float32,
    Float64,
    Decimal,
    String,

    /// <summary>STRING from a <c>Utf8String</c> member: bytes copied, never transcoded (D145).</summary>
    Utf8,

    Binary,
    Date32,
    Time64,
    Timestamp,
    Duration,
    Uuid,

    /// <summary>A list of one of the kinds above, one level deep (D58).</summary>
    List,
}

/// <summary>What inference decided for one column: its logical type and how a row's value reaches storage.</summary>
internal sealed class PocoColumnPlan
{
    public required ChalkType Type { get; init; }

    public required PocoStorageKind Storage { get; init; }

    /// <summary>Maps a non-null value expression of the member's CLR type to the storage element type.</summary>
    public required Func<Expression, Expression> ToStorage { get; init; }

    /// <summary>
    /// A LIST column's element plan, and null for every other kind. Its
    /// <see cref="ToStorage"/> is written against the element's own CLR type: the child array of a
    /// list is a column of the element type, built by the same encoder table (D58).
    /// </summary>
    public PocoColumnPlan? Element { get; init; }

    /// <summary>A LIST column's CLR element type — the <c>T</c> of <c>T[]</c>.</summary>
    public Type? ElementClrType { get; init; }
}

/// <summary>
/// The CLR → <see cref="ChalkType"/> table of <c>docs/design/04-client.md</c> §5.2 (D16), together
/// with the expression each row uses to reach its storage slot. Both halves live here so a type can
/// never be described one way in the catalog and written another way into a batch.
/// </summary>
internal static class PocoTypeMapping
{
    private const int DefaultDecimalPrecision = 28;

    private const int UnsignedLongPrecision = 20;

    /// <summary>
    /// Plans one column. <paramref name="clrType"/> may be a <see cref="Nullable{T}"/>; the returned
    /// conversion is written against the unwrapped type, because the chunk writer only calls it on
    /// the branch where a value is present.
    /// </summary>
    public static PocoColumnPlan Plan(
        Type clrType,
        bool nullable,
        ChalkColumnAttribute? attribute,
        ChalkType? requested,
        int defaultDecimalScale,
        string what)
    {
        var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;
        var inferred = Infer(underlying, clrType, attribute, defaultDecimalScale, nullable, what);

        var type = inferred;
        if (requested is { } explicitType)
        {
            if (explicitType.Kind != inferred.Kind)
            {
                throw new CatalogValidationException(
                    what,
                    $"the declared type {explicitType} does not match {inferred}, which is what {Describe(clrType)} maps to. "
                    + "A type override may change nullability, precision and scale only; use Column(name, projection) "
                    + "with a converting projection to change the kind.");
            }

            type = explicitType;
        }

        if (type.Kind == TypeKind.List)
        {
            // A list member's storage is IReadOnlyList<TElement>, and the element carries its own
            // plan: PocoListColumn builds the child array through the same encoder table (D58).
            var element = ElementTypeOf(underlying)!;
            var elementNullable = type.Element!.Value.Nullable;
            return new PocoColumnPlan
            {
                Type = type,
                Storage = PocoStorageKind.List,
                ToStorage = AsReadOnlyList(underlying, element),
                ElementClrType = element,
                Element = new PocoColumnPlan
                {
                    Type = type.Element.Value,
                    Storage = StorageOf(
                        type.Element.Value.Kind, Nullable.GetUnderlyingType(element) ?? element),
                    ToStorage = Conversion(
                        Nullable.GetUnderlyingType(element) ?? element,
                        type.Element.Value,
                        attribute,
                        what),
                },
            };
        }

        return new PocoColumnPlan
        {
            Type = type,
            Storage = StorageOf(type.Kind, underlying),
            ToStorage = Conversion(underlying, type, attribute, what),
        };
    }

    /// <summary>
    /// The element type of a list-shaped member, or null when the member is not one. The four shapes
    /// D58 names: <c>T[]</c>, <c>List&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c> and
    /// <c>ImmutableArray&lt;T&gt;</c>. <c>byte[]</c> is deliberately absent — it is BINARY.
    /// </summary>
    internal static Type? ElementTypeOf(Type type)
    {
        if (type == typeof(byte[]) || type == typeof(string))
        {
            return null;
        }

        if (type.IsArray && type.GetArrayRank() == 1)
        {
            return type.GetElementType();
        }

        if (!type.IsGenericType)
        {
            return null;
        }

        var definition = type.GetGenericTypeDefinition();
        if (definition == typeof(List<>)
            || definition == typeof(IReadOnlyList<>)
            || definition == typeof(System.Collections.Immutable.ImmutableArray<>))
        {
            return type.GetGenericArguments()[0];
        }

        return null;
    }

    /// <summary>
    /// The member as an <see cref="IReadOnlyList{T}"/>. An <c>ImmutableArray&lt;T&gt;</c> is
    /// unwrapped to the array it wraps rather than cast, which both avoids boxing the struct on
    /// every row and turns a <c>default</c> value into the NULL it means.
    /// </summary>
    private static Func<Expression, Expression> AsReadOnlyList(Type member, Type element)
    {
        var target = typeof(IReadOnlyList<>).MakeGenericType(element);
        if (member.IsGenericType
            && member.GetGenericTypeDefinition()
                == typeof(System.Collections.Immutable.ImmutableArray<>))
        {
            var asArray =
                typeof(System.Runtime.InteropServices.ImmutableCollectionsMarshal)
                    .GetMethod(
                        nameof(System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray))!
                    .MakeGenericMethod(element);
            return value => Expression.Convert(Expression.Call(asArray, value), target);
        }

        return value => value.Type == target ? value : Expression.Convert(value, target);
    }

    /// <summary>The logical type alone — used where only the catalog shape matters.</summary>
    private static ChalkType Infer(
        Type underlying,
        Type declared,
        ChalkColumnAttribute? attribute,
        int defaultDecimalScale,
        bool nullable,
        string what)
    {
        if (underlying.IsEnum)
        {
            return attribute?.AsInteger == true
                ? Infer(underlying.GetEnumUnderlyingType(), declared, attribute, defaultDecimalScale, nullable, what)
                : ChalkType.String(nullable);
        }

        if (underlying == typeof(bool))
        {
            return ChalkType.Bool(nullable);
        }

        if (underlying == typeof(sbyte))
        {
            return ChalkType.Int8(nullable);
        }

        // SQL has no unsigned integers, so every unsigned type widens into the next signed one (D16).
        if (underlying == typeof(short) || underlying == typeof(byte))
        {
            return ChalkType.Int16(nullable);
        }

        if (underlying == typeof(int) || underlying == typeof(ushort))
        {
            return ChalkType.Int32(nullable);
        }

        if (underlying == typeof(long) || underlying == typeof(uint))
        {
            return ChalkType.Int64(nullable);
        }

        if (underlying == typeof(ulong))
        {
            // ulong exceeds I64, and DECIMAL(20,0) is the smallest exact type that holds all of it.
            return ChalkType.Decimal(UnsignedLongPrecision, 0, nullable);
        }

        if (underlying == typeof(float))
        {
            return ChalkType.Float32(nullable);
        }

        if (underlying == typeof(double))
        {
            return ChalkType.Float64(nullable);
        }

        if (underlying == typeof(decimal))
        {
            var scale = attribute is { Scale: >= 0 } ? attribute.Scale : defaultDecimalScale;
            var precision = attribute is { Precision: >= 0 } ? attribute.Precision : DefaultDecimalPrecision;
            return ChalkType.Decimal(precision, scale, nullable);
        }

        if (underlying == typeof(string) || underlying == typeof(char))
        {
            return ChalkType.String(nullable);
        }

        // D145: the marker type for text a host already holds as UTF-8. It maps to STRING like a
        // `string` does — the difference is only in how the chunk writer reaches the bytes — and it
        // is binary-collated: byte order is the only order the source can verify without decoding,
        // which is what keys, indexes and the D17 verification over it compare in.
        if (underlying == typeof(Utf8String))
        {
            return ChalkType.String(nullable);
        }

        if (underlying == typeof(byte[]) || underlying == typeof(ReadOnlyMemory<byte>))
        {
            return ChalkType.Binary(nullable);
        }

        if (underlying == typeof(DateTime))
        {
            // Kind is ignored: a DateTime is a zone-less wall clock here, and ticks × 100 is lossless at ns (D16).
            return ChalkType.Timestamp(Precision(attribute, 9), nullable);
        }

        if (underlying == typeof(DateTimeOffset))
        {
            return ChalkType.TimestampTz(Precision(attribute, 9), nullable);
        }

        if (underlying == typeof(DateOnly))
        {
            return ChalkType.Date(nullable);
        }

        if (underlying == typeof(TimeOnly))
        {
            return ChalkType.Time(6, nullable);
        }

        if (underlying == typeof(TimeSpan))
        {
            return ChalkType.IntervalDay(nullable);
        }

        if (underlying == typeof(Guid))
        {
            return ChalkType.Uuid(nullable);
        }

        // A list member (D58). Its element is inferred by the same rules, one level deep: a list of
        // lists has no IR type and says so here rather than at plan time.
        if (ElementTypeOf(underlying) is { } element)
        {
            var elementUnderlying = Nullable.GetUnderlyingType(element) ?? element;
            if (ElementTypeOf(elementUnderlying) is not null)
            {
                throw new CatalogValidationException(
                    what,
                    $"{Describe(declared)} is a list of lists; v1 lists are exactly one level deep "
                    + "(docs/design/14-windows-ii.md §5).");
            }

            var elementNullable = !element.IsValueType || Nullable.GetUnderlyingType(element) is not null;
            var elementType =
                Infer(elementUnderlying, element, attribute, defaultDecimalScale, elementNullable, what);

            // A list member may itself be null (a null array, or a default ImmutableArray), whatever
            // the declaration said about the member's own nullability.
            return ChalkType.List(elementType, nullable: true);
        }

        throw new CatalogValidationException(
            what,
            $"{Describe(declared)} has no mapping in the CLR type table (docs/design/04-client.md §5.2). "
            + "Map it with Column(name, row => …) to project it into a supported type, or leave it out with "
            + "Ignore(row => …) or [ChalkIgnore].");
    }

    private static int Precision(ChalkColumnAttribute? attribute, int fallback) =>
        attribute is { Precision: >= 0 } ? attribute.Precision : fallback;

    /// <summary>
    /// The storage a kind uses. STRING has two: a <c>Utf8String</c> member is copied as bytes
    /// (D145), and everything else transcodes from UTF-16.
    /// </summary>
    private static PocoStorageKind StorageOf(TypeKind kind, Type underlying) => kind switch
    {
        TypeKind.String when underlying == typeof(Utf8String) => PocoStorageKind.Utf8,
        TypeKind.Bool => PocoStorageKind.Bool,
        TypeKind.I8 => PocoStorageKind.Int8,
        TypeKind.I16 => PocoStorageKind.Int16,
        TypeKind.I32 => PocoStorageKind.Int32,
        TypeKind.I64 => PocoStorageKind.Int64,
        TypeKind.Fp32 => PocoStorageKind.Float32,
        TypeKind.Fp64 => PocoStorageKind.Float64,
        TypeKind.Decimal => PocoStorageKind.Decimal,
        TypeKind.String => PocoStorageKind.String,
        TypeKind.Binary => PocoStorageKind.Binary,
        TypeKind.Date => PocoStorageKind.Date32,
        TypeKind.Time => PocoStorageKind.Time64,
        TypeKind.Timestamp or TypeKind.TimestampTz => PocoStorageKind.Timestamp,
        TypeKind.IntervalDay => PocoStorageKind.Duration,
        TypeKind.Uuid => PocoStorageKind.Uuid,
        _ => throw new UnsupportedFeatureException(
            $"POCO column of kind {kind}",
            "The POCO source writes only the kinds in docs/design/04-client.md §5.2."),
    };

    /// <summary>The expression that turns a present value of <paramref name="underlying"/> into its storage element.</summary>
    private static Func<Expression, Expression> Conversion(
        Type underlying, ChalkType type, ChalkColumnAttribute? attribute, string what)
    {
        if (underlying.IsEnum)
        {
            return attribute?.AsInteger == true
                ? EnumAsInteger(underlying, type, attribute, what)
                : EnumAsName(underlying);
        }

        return type.Kind switch
        {
            TypeKind.Bool => value => Expression.Condition(
                value, Expression.Constant((byte)1), Expression.Constant((byte)0)),
            TypeKind.I8 => value => value,
            TypeKind.I16 => Widen(typeof(short)),
            TypeKind.I32 => Widen(typeof(int)),
            TypeKind.I64 => Widen(typeof(long)),
            TypeKind.Fp32 or TypeKind.Fp64 => value => value,
            TypeKind.Decimal => Widen(typeof(decimal)),
            TypeKind.String => underlying == typeof(char)
                ? value => Expression.Call(CharToStringMethod, value)
                : value => value, // string and Utf8String both already are their storage element
            TypeKind.Binary => underlying == typeof(byte[])
                ? value => Expression.New(ReadOnlyMemoryFromArray, value)
                : value => value,
            // The temporal lanes through ClrStorage, which the engine's lane codec shares for a
            // delegate's arguments and answers (D298): one conversion per kind, not two.
            TypeKind.Date => value => Expression.Call(DaysOfMethod, value),
            TypeKind.Time => value => Expression.Call(TimeMicrosMethod, value),
            TypeKind.Timestamp => value => TicksToUnit(
                Expression.Property(value, nameof(DateTime.Ticks)), type.Precision),
            TypeKind.TimestampTz => value => TicksToUnit(
                Expression.Property(value, nameof(DateTimeOffset.UtcTicks)), type.Precision),
            TypeKind.IntervalDay => value => Expression.Call(IntervalMicrosMethod, value),
            TypeKind.Uuid => value => value,
            _ => throw new UnsupportedFeatureException(
                $"POCO column of kind {type.Kind}",
                "The POCO source writes only the kinds in docs/design/04-client.md §5.2."),
        };
    }

    /// <summary>An identity when the CLR type already is the storage type, and a widening conversion otherwise.</summary>
    private static Func<Expression, Expression> Widen(Type storage) =>
        value => value.Type == storage ? value : Expression.Convert(value, storage);

    /// <summary>
    /// A .NET tick is 100 ns, so nanoseconds are exact and coarser units divide. Truncation only
    /// happens when a host asks for a precision below the source's, which is its own choice.
    /// </summary>
    private static Expression TicksToUnit(Expression ticks, int precision) =>
        Expression.Call(UnitsOfMethod, ticks, Expression.Constant(precision));

    /// <summary>
    /// <c>Enum.GetName&lt;TEnum&gt;</c> returns the cached name without allocating for a declared
    /// value; the coalesce covers combined <c>[Flags]</c> values and cast integers, which have no name.
    /// </summary>
    private static Func<Expression, Expression> EnumAsName(Type enumType)
    {
        var getName = typeof(Enum)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(Enum.GetName) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(enumType);

        return value => Expression.Coalesce(
            Expression.Call(getName, value),
            Expression.Call(
                EnumFallbackMethod,
                Expression.Convert(Expression.Convert(value, enumType.GetEnumUnderlyingType()), typeof(long))));
    }

    private static Func<Expression, Expression> EnumAsInteger(
        Type enumType, ChalkType type, ChalkColumnAttribute? attribute, string what)
    {
        var underlying = enumType.GetEnumUnderlyingType();
        var inner = Conversion(underlying, type, attribute, what);
        return value => inner(Expression.Convert(value, underlying));
    }

    private static string Describe(Type type) =>
        Nullable.GetUnderlyingType(type) is { } inner ? $"{inner.Name}?" : type.Name;

    private static readonly MethodInfo CharToStringMethod =
        typeof(PocoConvert).GetMethod(nameof(PocoConvert.CharToString))!;

    private static readonly MethodInfo EnumFallbackMethod =
        typeof(PocoConvert).GetMethod(nameof(PocoConvert.EnumFallback))!;

    private static readonly ConstructorInfo ReadOnlyMemoryFromArray =
        typeof(ReadOnlyMemory<byte>).GetConstructor([typeof(byte[])])!;

    private static readonly MethodInfo DaysOfMethod =
        typeof(ClrStorage).GetMethod(nameof(ClrStorage.DaysOf), [typeof(DateOnly)])!;

    private static readonly MethodInfo TimeMicrosMethod =
        typeof(ClrStorage).GetMethod(nameof(ClrStorage.MicrosOf), [typeof(TimeOnly)])!;

    private static readonly MethodInfo IntervalMicrosMethod =
        typeof(ClrStorage).GetMethod(nameof(ClrStorage.MicrosOf), [typeof(TimeSpan)])!;

    private static readonly MethodInfo UnitsOfMethod =
        typeof(ClrStorage).GetMethod(nameof(ClrStorage.UnitsOf), [typeof(long), typeof(int)])!;
}
