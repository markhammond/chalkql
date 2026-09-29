using Chalk.Catalog;
using Chalk.Execution;

namespace Chalk.Client;

/// <summary>
/// One engine's rule for a value a host binds (D317, D323): its <see cref="BindingConverter"/>s,
/// then <see cref="ParameterValues.Exact"/>. Applied to a statement's parameters and to every context
/// value whose type is known, so the two are held to one rule.
/// </summary>
internal sealed class ValueBinding
{
    private readonly Dictionary<Type, BindingConverter> _converters;

    /// <summary>No converters: the rule alone.</summary>
    public static ValueBinding None { get; } = new([]);

    public ValueBinding(IReadOnlyList<BindingConverter> converters)
    {
        ArgumentNullException.ThrowIfNull(converters);
        _converters = [];
        foreach (var converter in converters)
        {
            if (converter is null)
            {
                throw new ArgumentException("BindingConverters holds a null entry.", nameof(converters));
            }

            var type = converter.ValueType;
            if (type.IsInterface || type.IsAbstract || Nullable.GetUnderlyingType(type) is not null)
            {
                throw new ArgumentException(
                    $"{converter} converts {type.Name}, which no bound value's runtime type ever is: a "
                    + "converter is chosen by the value's exact type, so it must be a concrete type, "
                    + "and a boxed nullable is its underlying type.",
                    nameof(converters));
            }

            if (!_converters.TryAdd(type, converter))
            {
                throw new ArgumentException(
                    $"{_converters[type]} and {converter} both convert {type.Name}; a value's type "
                    + "chooses one converter, so register one.",
                    nameof(converters));
            }
        }
    }

    /// <summary>
    /// What a context keeps for <paramref name="value"/>: what a converter made of it, or the value
    /// itself once the exact rule has passed it — so a context no converter touched keeps the values
    /// the host gave it, and a converter runs once however often the context is encoded.
    /// </summary>
    public object? Checked(object? value, ChalkType type, string what)
    {
        if (value is null or DBNull)
        {
            return value;
        }

        var bound = Bind(value, type, what);
        return _converters.ContainsKey(value.GetType()) ? bound : value;
    }

    /// <summary>
    /// <paramref name="value"/> as the CLR value <paramref name="type"/> binds from, or null for SQL
    /// NULL. <paramref name="what"/> names it in a refusal: <c>Parameter @amount</c>,
    /// <c>Context scalar 'tenant'</c>.
    /// </summary>
    public object? Bind(object? value, ChalkType type, string what)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        if (!_converters.TryGetValue(value.GetType(), out var converter))
        {
            return ParameterValues.Exact(value, type, what);
        }

        bool handled;
        object? converted;
        try
        {
            handled = converter.TryConvertValue(value, type, out converted);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw new ArgumentException(
                $"{what} is {type}, and {converter} threw converting the {value.GetType().Name} bound to "
                + $"it: {failure.GetType().Name} (D323).",
                "parameters",
                failure);
        }

        if (!handled)
        {
            return ParameterValues.Exact(value, type, what);
        }

        if (converted is null or DBNull)
        {
            return type.Nullable
                ? null
                : throw new ArgumentException(
                    $"{what} is {type}, which is not nullable, and {converter} returned NULL for the "
                    + $"{value.GetType().Name} bound to it (D323).",
                    "parameters");
        }

        return ParameterValues.Exact(converted, type, what, converter.ToString());
    }
}
