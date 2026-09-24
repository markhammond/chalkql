using System.Reflection;
using Chalk.Ir;
using Type = System.Type;

namespace Chalk.Catalog;

/// <summary>
/// The one reading of a CLR record as a <c>COMPOSITE</c> (D294, ADR 0077), shared by the declaration —
/// <see cref="FunctionBuilder"/> inferring a result type — and the engine's binding check, which
/// infers the registered delegate's result the same way and compares the two.
/// </summary>
/// <remarks>
/// <para>
/// A candidate is a class or struct outside the Tier 1 set and outside <c>System</c>: the fields are
/// its public readable instance properties, in declaration order — a positional record's constructor
/// order when a constructor's parameters match the properties, metadata order otherwise — named as
/// declared. Each is a Tier 1 type: a value type is a non-nullable field and its <c>Nullable</c> form a
/// nullable one; <c>Utf8String</c> is a non-nullable STRING and <c>string</c> a nullable one.
/// </para>
/// <para>
/// Reflection runs here, once per declaration and once per binding, and never per row: the engine
/// compiles one accessor per field from the properties this returns (D294).
/// </para>
/// </remarks>
internal static class CompositeInference
{
    /// <summary>The full name of <c>Chalk.Utf8String</c>, which lives in a package this one cannot see.</summary>
    internal const string Utf8StringName = "Chalk.Utf8String";

    /// <summary>
    /// Whether <paramref name="type"/> (a <c>Nullable</c> form unwrapped) is read as a composite: a
    /// class or a struct that is not a Tier 1 type, not <c>Utf8String</c>, and not one of the
    /// platform's own types — a <c>decimal</c> or a <c>DateTime</c> is a value with no fields, not a
    /// record.
    /// </summary>
    public static bool IsCandidate(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (ScalarOf(underlying) is not null || IsPlatformType(underlying))
        {
            return false;
        }

        return underlying.IsValueType || underlying.IsClass;
    }

    /// <summary>
    /// The composite <paramref name="type"/> stands for, nullable when <paramref name="nullable"/> says so
    /// or when the type is a <c>Nullable</c> of one.
    /// </summary>
    /// <exception cref="CatalogValidationException">
    /// A property is not a Tier 1 type, the type has no readable property, or two property names are
    /// equal ignoring case — each naming the property and its type.
    /// </exception>
    public static ChalkType Infer(Type type, bool nullable, string path) =>
        TryInfer(type, nullable, out var inferred, out var refusal)
            ? inferred
            : throw new CatalogValidationException(path, refusal!);

    /// <summary>
    /// The same, answering the refusal rather than throwing it — which is how the engine's binding
    /// check names what is wrong with a registered type in its own words.
    /// </summary>
    public static bool TryInfer(Type type, bool nullable, out ChalkType inferred, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(type);
        inferred = default;
        refusal = null;
        var underlying = Nullable.GetUnderlyingType(type);
        var record = underlying ?? type;
        var properties = Properties(record);
        if (properties.Count == 0)
        {
            refusal = $"{record.Name} has no public readable instance property, so it reads as a "
                + "composite of no fields; a composite value has at least one";
            return false;
        }

        var names = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        var fields = new CompositeField[properties.Count];
        for (var i = 0; i < properties.Count; i++)
        {
            var property = properties[i];
            if (names.TryGetValue(property.Name, out var earlier))
            {
                refusal = $"{record.Name} has properties '{earlier.Name}' and '{property.Name}', which "
                    + "are the same field name ignoring case; SQL resolves a field by name ignoring "
                    + "case, so the two would be one field";
                return false;
            }

            names.Add(property.Name, property);
            if (FieldType(property.PropertyType) is not { } field)
            {
                refusal = $"property '{property.Name}' of {record.Name} is "
                    + $"{Describe(property.PropertyType)}, which no composite field can be: a field is "
                    + "bool, sbyte, short, int, long, float, double, string or Utf8String, or a nullable "
                    + "form of one, and a composite value is one level deep";
                return false;
            }

            fields[i] = new CompositeField(property.Name, field);
        }

        inferred = ChalkType.Composite(fields, nullable || underlying is not null);
        return true;
    }

    /// <summary>
    /// The properties a record's fields are, in field order: public, readable, instance, not an
    /// indexer. A positional record's primary constructor fixes the order when one constructor's
    /// parameters are exactly the properties; otherwise the order is the metadata's, base type first.
    /// </summary>
    public static IReadOnlyList<PropertyInfo> Properties(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
            .OrderBy(p => Depth(p.DeclaringType))
            .ThenBy(p => p.MetadataToken)
            .ToArray();

        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length != properties.Length || parameters.Length == 0)
            {
                continue;
            }

            var ordered = new PropertyInfo[parameters.Length];
            var matched = true;
            for (var i = 0; i < parameters.Length && matched; i++)
            {
                var property = properties.FirstOrDefault(p =>
                    string.Equals(p.Name, parameters[i].Name, StringComparison.Ordinal)
                    && p.PropertyType == parameters[i].ParameterType);
                matched = property is not null && !ordered.Contains(property);
                if (matched)
                {
                    ordered[i] = property!;
                }
            }

            if (matched)
            {
                return ordered;
            }
        }

        return properties;
    }

    /// <summary>
    /// The field type a property type spells, or null for one no field can be. Nullability follows
    /// the CLR: a value type is non-nullable and its <c>Nullable</c> form nullable; <c>string</c> is a
    /// reference and nullable; <c>Utf8String</c> is a value and not.
    /// </summary>
    public static ChalkType? FieldType(Type clr)
    {
        ArgumentNullException.ThrowIfNull(clr);
        var underlying = Nullable.GetUnderlyingType(clr);
        var scalar = ScalarOf(underlying ?? clr);
        if (scalar is not { } type)
        {
            return null;
        }

        var nullable = underlying is not null || clr == typeof(string);
        return type.WithNullable(nullable);
    }

    /// <summary>
    /// The declared type a Tier 1 CLR type stands for, non-nullable, or null for anything else. The
    /// set the declaration surface guesses at (D79) plus <c>Utf8String</c>, which is how a STRING is
    /// spelled without an allocation per row (D146).
    /// </summary>
    public static ChalkType? ScalarOf(Type clr)
    {
        ArgumentNullException.ThrowIfNull(clr);
        if (clr == typeof(bool))
        {
            return ChalkType.Bool();
        }

        if (clr == typeof(sbyte))
        {
            return ChalkType.Int8();
        }

        if (clr == typeof(short))
        {
            return ChalkType.Int16();
        }

        if (clr == typeof(int))
        {
            return ChalkType.Int32();
        }

        if (clr == typeof(long))
        {
            return ChalkType.Int64();
        }

        if (clr == typeof(float))
        {
            return ChalkType.Float32();
        }

        if (clr == typeof(double))
        {
            return ChalkType.Float64();
        }

        if (clr == typeof(string) || IsUtf8String(clr))
        {
            return ChalkType.String();
        }

        return null;
    }

    /// <summary>Whether <paramref name="clr"/> is <c>Chalk.Utf8String</c>, recognised by name.</summary>
    public static bool IsUtf8String(Type clr) =>
        clr.IsValueType && string.Equals(clr.FullName, Utf8StringName, StringComparison.Ordinal);

    /// <summary>A CLR type as a refusal names it: <c>Decimal</c>, <c>Nullable&lt;Guid&gt;</c>.</summary>
    public static string Describe(Type clr) =>
        Nullable.GetUnderlyingType(clr) is { } underlying ? $"Nullable<{underlying.Name}>" : clr.Name;

    /// <summary>
    /// The platform's own types — <c>decimal</c>, <c>DateTime</c>, <c>Guid</c>, a tuple, an array,
    /// an enum — which are values in their own right rather than records whose properties are fields.
    /// </summary>
    private static bool IsPlatformType(Type type) =>
        type.IsPrimitive
        || type.IsEnum
        || type.IsArray
        || type.IsPointer
        || type.IsInterface
        || type.IsAbstract
        || type.ContainsGenericParameters
        || type.Namespace is "System" || (type.Namespace?.StartsWith("System.", StringComparison.Ordinal) ?? false);

    /// <summary>How far down its hierarchy a type is, so a base type's properties come first.</summary>
    private static int Depth(Type? type)
    {
        var depth = 0;
        for (var t = type?.BaseType; t is not null; t = t.BaseType)
        {
            depth++;
        }

        return depth;
    }
}
