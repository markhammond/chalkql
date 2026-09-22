using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Chalk.Catalog;
using Chalk.Ir;
using ClrType = System.Type;

namespace Chalk.Client;

/// <summary>
/// Turns the values a host binds into the flat list of parameter values the compiled plan wants, one
/// per rendered <c>?</c> (docs/design/04-client.md §7.3, §7.4).
///
/// <para>
/// Every mismatch is an <see cref="ArgumentException"/> naming the parameter, raised before anything
/// executes — a wrong parameter should never surface as a mid-stream conversion error.
/// </para>
/// </summary>
internal static class ParameterBinder
{
    private static readonly ConcurrentDictionary<ClrType, Func<object, string, object?>[]> Accessors = new();

    /// <summary>
    /// A value is a *list* when it is enumerable but not one of the types SQL treats as a scalar.
    /// A string is a scalar: binding <c>"BTCUSDT"</c> to <c>IN @symbols</c> means one symbol, not
    /// seven characters (§7.4).
    /// </summary>
    public static bool IsList(object? value) =>
        value is IEnumerable and not string and not byte[] and not IDictionary
        && value is not ReadOnlyMemory<byte>;

    /// <summary>Resolves the values for each logical parameter, in parameter order.</summary>
    public static object?[] ResolvePositional(
        IReadOnlyList<ParameterDescriptor> parameters, IReadOnlyList<object?>? values, ParameterStyle style)
    {
        if (parameters.Count == 0 && values is null || values?.Count == 0)
            return [];

        values ??= [];
        if (values.Count != parameters.Count)
        {
            throw new ArgumentException(
                style == ParameterStyle.Ordinal
                    ? $"the statement has {parameters.Count} ordinal parameters (up to "
                      + $"${parameters.Count}) but {values.Count} values were bound"
                    : $"the statement has {parameters.Count} '?' placeholders but {values.Count} values were bound");
        }

        return [.. values];
    }

    /// <summary>Resolves <c>@name</c> parameters from a dictionary.</summary>
    public static object?[] ResolveNamed(
        IReadOnlyList<ParameterDescriptor> parameters, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var lookup = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            lookup[key.TrimStart('@')] = value;
        }

        var resolved = new object?[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            var name = parameters[i].Name!;
            if (!lookup.Remove(name, out var value))
            {
                throw new ArgumentException(
                    $"no value was bound for parameter @{name}; the statement needs "
                    + string.Join(", ", parameters.Select(p => "@" + p.Name)));
            }

            resolved[i] = value;
        }

        if (lookup.Count > 0)
        {
            throw new ArgumentException(
                $"these values were bound but the statement has no such parameter: "
                + string.Join(", ", lookup.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(k => "@" + k)));
        }

        return resolved;
    }

    /// <summary>
    /// Resolves <c>@name</c> parameters from the public readable properties of an anonymous object or
    /// POCO, Dapper-style. Accessors are compiled once per CLR type and cached.
    /// </summary>
    public static object?[] ResolveNamed(IReadOnlyList<ParameterDescriptor> parameters, object values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values is IReadOnlyDictionary<string, object?> dictionary)
        {
            return ResolveNamed(parameters, dictionary);
        }

        var type = values.GetType();
        var accessors = Accessors.GetOrAdd(type, Compile);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();

        var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < properties.Length; i++)
        {
            bag[properties[i].Name] = accessors[i](values, properties[i].Name);
        }

        return ResolveNamed(parameters, bag);
    }

    private static Func<object, string, object?>[] Compile(ClrType type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();

        var accessors = new Func<object, string, object?>[properties.Length];
        for (var i = 0; i < properties.Length; i++)
        {
            var instance = Expression.Parameter(typeof(object), "instance");
            var unusedName = Expression.Parameter(typeof(string), "name");
            var body = Expression.Convert(
                Expression.Property(Expression.Convert(instance, type), properties[i]),
                typeof(object));
            accessors[i] = Expression
                .Lambda<Func<object, string, object?>>(body, instance, unusedName)
                .Compile();
        }

        return accessors;
    }

    /// <summary>
    /// What the caller expects each rendered <c>?</c> to be worth, for planning only (D284).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The container is resolved as an execution's values are — an <see cref="IReadOnlyList{T}"/> by
    /// ordinal, a dictionary or a POCO by name — with one convention of its own: it is
    /// <em>sparse</em>. A parameter the caller said nothing about is estimated exactly as it always
    /// was, so <c>null</c> here is "no hint" where at execution it is SQL NULL, and
    /// <see cref="DBNull.Value"/> is how a caller says it expects NULL. A planning API needs three
    /// states where a binding API needs two.
    /// </para>
    /// <para>
    /// A name the statement does not have is refused rather than ignored: a misspelt hint that
    /// quietly became "no hint" would leave a caller wondering why the plan never moved. A
    /// list-valued hint is refused too — the plan's shape depends on the list's <em>length</em>,
    /// which is not something a representative value can stand in for.
    /// </para>
    /// <para>
    /// A named parameter that occurs three times is one hint and three rendered placeholders, so the
    /// hint is expanded to every occurrence: the planner numbers parameters by the <c>?</c> it sees.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ParameterValueHint> ResolveHints(
        IReadOnlyList<ParameterDescriptor> parameters,
        IReadOnlyList<PlaceholderSlot> slots,
        object? container)
    {
        if (container is null || parameters.Count == 0)
        {
            return [];
        }

        var byParameter = SparseValues(parameters, container);
        if (byParameter is null)
        {
            return [];
        }

        var hints = new List<ParameterValueHint>();
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.ParameterIndex < 0 || slot.ParameterIndex >= byParameter.Length)
            {
                continue;
            }

            var value = byParameter[slot.ParameterIndex];
            if (value is null)
            {
                continue;
            }

            hints.Add(new ParameterValueHint
            {
                Ordinal = i,
                Value = value is DBNull
                    ? null
                    : ContextValues.ToLiteral(
                        value, $"the value hint for parameter {parameters[slot.ParameterIndex]}"),
            });
        }

        return hints;
    }

    /// <summary>
    /// The hint for each logical parameter, or null where the caller said nothing. Null for a
    /// container that hints nothing at all.
    /// </summary>
    private static object?[]? SparseValues(
        IReadOnlyList<ParameterDescriptor> parameters, object container)
    {
        var resolved = new object?[parameters.Count];
        if (container is IReadOnlyList<object?> ordinals)
        {
            if (ordinals.Count > parameters.Count)
            {
                throw new ArgumentException(
                    $"{ordinals.Count} parameter value hints were given and the statement has "
                    + $"{parameters.Count} parameters. Hints are sparse — leave an entry null to "
                    + "hint nothing about it — but there is no parameter for the extra ones.");
            }

            for (var i = 0; i < ordinals.Count; i++)
            {
                resolved[i] = Hinted(ordinals[i], parameters[i]);
            }

            return resolved;
        }

        var bag = Bag(container);
        for (var i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].Name is { } name && bag.Remove(name, out var value))
            {
                resolved[i] = Hinted(value, parameters[i]);
            }
        }

        if (bag.Count > 0)
        {
            throw new ArgumentException(
                "these parameter value hints were given but the statement has no such parameter: "
                + string.Join(", ", bag.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(k => "@" + k))
                + ". A hint is optional, but a hint for a parameter that is not there is a mistake "
                + "rather than nothing said.");
        }

        return resolved;
    }

    /// <summary>One hinted value, refused by name when it is a list.</summary>
    private static object? Hinted(object? value, ParameterDescriptor parameter)
    {
        if (value is null)
        {
            return null;
        }

        if (IsList(value))
        {
            throw new ArgumentException(
                $"a list was given as the value hint for parameter {parameter}. A list parameter is "
                + "expanded into one placeholder per element, so the plan's shape depends on the "
                + "list's length rather than on a representative value, and a hint cannot say "
                + "anything useful about it (docs/design/04-client.md §7.4).");
        }

        return value;
    }

    /// <summary>The container's entries by name: a dictionary as it stands, a POCO by reflection.</summary>
    private static Dictionary<string, object?> Bag(object container)
    {
        var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (container is IReadOnlyDictionary<string, object?> dictionary)
        {
            foreach (var (key, value) in dictionary)
            {
                bag[key.TrimStart('@')] = value;
            }

            return bag;
        }

        var type = container.GetType();
        var accessors = Accessors.GetOrAdd(type, Compile);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();
        for (var i = 0; i < properties.Length; i++)
        {
            bag[properties[i].Name] = accessors[i](container, properties[i].Name);
        }

        return bag;
    }

    /// <summary>
    /// The shape of an execution: the list length for each list-valued parameter, and
    /// <see cref="PlaceholderSlot.Scalar"/> for the rest.
    /// </summary>
    public static int[] ShapeOf(IReadOnlyList<ParameterDescriptor> parameters, object?[] values)
    {
        if (parameters.Count == 0) return [];

        var shape = new int[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            if (!IsList(values[i]))
            {
                shape[i] = PlaceholderSlot.Scalar;
                continue;
            }

            if (!parameters[i].AcceptsList)
            {
                throw new ArgumentException(
                    $"a list was bound to parameter {parameters[i]}, but it is not in an IN position. "
                    + "Only a parameter whose every occurrence directly follows IN or NOT IN can be "
                    + "expanded into a list (docs/design/04-client.md §7.4).");
            }

            shape[i] = ((IEnumerable)values[i]!).Cast<object?>().Count();
        }

        return shape;
    }

    /// <summary>
    /// Flattens the bound values into one value per rendered <c>?</c>, converting each to the type the
    /// planner inferred for that placeholder.
    /// </summary>
    public static object?[] Flatten(
        IReadOnlyList<ParameterDescriptor> parameters,
        object?[] values,
        IReadOnlyList<PlaceholderSlot> slots,
        IReadOnlyList<ChalkType> placeholderTypes)
    {
        if (placeholderTypes.Count != slots.Count)
        {
            // The planner infers one type per `?`. Fewer means the plan and the SQL disagree, which
            // is a contract bug and must not be papered over with a guessed type.
            throw new InvalidOperationException(
                $"the plan declares {placeholderTypes.Count} parameter types but the rewritten SQL has "
                + $"{slots.Count} placeholders");
        }

        if (slots.Count == 0)
        {
            return [];
        }

        var elements = new List<object?>?[parameters.Count];
        var flat = new object?[slots.Count];

        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var parameter = parameters[slot.ParameterIndex];
            var type = placeholderTypes[i];

            object? value;
            switch (slot.ElementIndex)
            {
                case PlaceholderSlot.EmptyListNull:
                    // The NULL that makes `x IN (NULL) IS FALSE` yield FALSE for an empty list.
                    value = null;
                    break;

                case PlaceholderSlot.Scalar:
                    value = values[slot.ParameterIndex];
                    break;

                default:
                    elements[slot.ParameterIndex] ??=
                        [.. ((IEnumerable)values[slot.ParameterIndex]!).Cast<object?>()];
                    value = elements[slot.ParameterIndex]![slot.ElementIndex];
                    break;
            }

            flat[i] = Convert(value, type, parameter);
        }

        return flat;
    }

    /// <summary>
    /// The reverse of §7.2 plus safe widening: an <c>int</c> binds to an I64 parameter, a
    /// <c>float</c> to FP64, a <c>DateTime</c> to TIMESTAMP(9). Anything else names the parameter.
    /// </summary>
    private static object? Convert(object? value, ChalkType type, ParameterDescriptor parameter)
    {
        if (value is null)
        {
            return type.Nullable
                ? null
                : throw new ArgumentException(
                    $"null was bound to parameter {parameter}, whose inferred type {type} is not nullable");
        }

        try
        {
            return type.Kind switch
            {
                TypeKind.Bool => System.Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                TypeKind.I8 => System.Convert.ToSByte(value, CultureInfo.InvariantCulture),
                TypeKind.I16 => System.Convert.ToInt16(value, CultureInfo.InvariantCulture),
                TypeKind.I32 => System.Convert.ToInt32(value, CultureInfo.InvariantCulture),
                TypeKind.I64 => System.Convert.ToInt64(value, CultureInfo.InvariantCulture),
                TypeKind.Fp32 => System.Convert.ToSingle(value, CultureInfo.InvariantCulture),
                TypeKind.Fp64 => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
                TypeKind.String => value as string ?? System.Convert.ToString(value, CultureInfo.InvariantCulture),
                TypeKind.Binary => AsBytes(value),
                TypeKind.Date => AsDate(value),
                TypeKind.Time => AsTime(value),
                TypeKind.Timestamp => AsTimestamp(value),
                TypeKind.TimestampTz => AsTimestampTz(value),
                TypeKind.Decimal => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                TypeKind.Uuid => value is Guid guid ? guid : Guid.Parse((string)value, CultureInfo.InvariantCulture),
                TypeKind.IntervalDay => value is TimeSpan span ? span : TimeSpan.Parse((string)value, CultureInfo.InvariantCulture),
                TypeKind.IntervalYear => System.Convert.ToInt32(value, CultureInfo.InvariantCulture),
                _ => throw new ArgumentException(
                    $"parameter {parameter} has inferred type {type}, which cannot be bound from a "
                    + $"{value.GetType().Name}"),
            };
        }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
        {
            throw new ArgumentException(
                $"the value bound to parameter {parameter} ({value.GetType().Name}) does not fit its "
                + $"inferred type {type}",
                e);
        }
    }

    private static byte[] AsBytes(object value) => value switch
    {
        byte[] bytes => bytes,
        ReadOnlyMemory<byte> memory => memory.ToArray(),
        _ => throw new InvalidCastException($"cannot bind a {value.GetType().Name} to a BINARY parameter"),
    };

    private static DateOnly AsDate(object value) => value switch
    {
        DateOnly date => date,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        string text => DateOnly.Parse(text, CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException($"cannot bind a {value.GetType().Name} to a DATE parameter"),
    };

    private static TimeOnly AsTime(object value) => value switch
    {
        TimeOnly time => time,
        TimeSpan span => TimeOnly.FromTimeSpan(span),
        DateTime dateTime => TimeOnly.FromDateTime(dateTime),
        string text => TimeOnly.Parse(text, CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException($"cannot bind a {value.GetType().Name} to a TIME parameter"),
    };

    private static DateTime AsTimestamp(object value) => value switch
    {
        DateTime dateTime => dateTime,
        DateOnly date => date.ToDateTime(TimeOnly.MinValue),
        DateTimeOffset offset => offset.UtcDateTime,
        string text => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None),
        _ => throw new InvalidCastException($"cannot bind a {value.GetType().Name} to a TIMESTAMP parameter"),
    };

    private static DateTimeOffset AsTimestampTz(object value) => value switch
    {
        DateTimeOffset offset => offset.ToUniversalTime(),
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).ToUniversalTime(),
        _ => throw new InvalidCastException(
            $"cannot bind a {value.GetType().Name} to a TIMESTAMP_TZ parameter"),
    };
}
