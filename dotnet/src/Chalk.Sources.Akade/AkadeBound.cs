using System.Globalization;
using Chalk.Sources;

namespace Chalk.Sources.Akade;

/// <summary>
/// One bound value of a Chalk range, as the key type the Akade index was built with.
/// </summary>
/// <remarks>
/// The CLR type a bound arrives as is not required to be the one the rows hold: a STRING bound may
/// be a <see cref="string"/> or a <see cref="Utf8String"/>, and an integer bound arrives at the
/// column's width rather than the member's. This runs once per lookup, never per row.
/// </remarks>
internal static class AkadeBound
{
    /// <summary>
    /// The bound as <typeparamref name="TValue"/>. Throws when the value is of a CLR type this key
    /// cannot consume at all, which is a plan the source was never able to serve.
    /// </summary>
    public static TValue Coerce<TValue>(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value is TValue exact)
        {
            return exact;
        }

        // The two spellings of a STRING value. Convert.ChangeType knows neither direction.
        if (typeof(TValue) == typeof(Utf8String) && value is string text)
        {
            return (TValue)(object)Utf8String.FromString(text);
        }

        if (typeof(TValue) == typeof(string) && value is Utf8String utf8)
        {
            return (TValue)(object)utf8.ToString();
        }

        try
        {
            var converted = Convert.ChangeType(value, typeof(TValue), CultureInfo.InvariantCulture);
            if (converted is TValue typed)
            {
                return typed;
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            // Fall through to the failure below.
        }

        throw new InvalidOperationException(
            $"Akade index key '{typeof(TValue).FullName ?? typeof(TValue).Name}' cannot consume a "
            + $"bound value of CLR type {value.GetType().FullName}.");
    }

    /// <summary>
    /// The component of a compound key at <paramref name="position"/>: the bound the range gave, or
    /// — for a component the range does not reach — the extreme the bound's inclusivity calls for
    /// (D280). An exclusive bound on a prefix excludes every key with that prefix, so the components
    /// below it take the <em>maximum</em>.
    /// </summary>
    public static TValue Component<TValue>(
        IReadOnlyList<object?> bounds,
        int position,
        bool useMaximum,
        TValue minimum,
        TValue maximum)
    {
        ArgumentNullException.ThrowIfNull(bounds);

        if (position >= bounds.Count)
        {
            return useMaximum ? maximum : minimum;
        }

        // A NULL bound never reaches here: the caller answers such a range with no rows at all,
        // because a SQL comparison with NULL is unknown.
        return Coerce<TValue>(bounds[position]!);
    }
}
