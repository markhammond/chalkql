using System.Collections;
using System.Reflection;
using Akade.IndexedSet;

namespace Chalk.Sources.Akade;

/// <summary>
/// The one statistic an Akade index knows exactly and Chalk would otherwise have to guess: how many
/// distinct keys it holds (D280).
/// </summary>
/// <remarks>
/// <para>
/// Read once, when a snapshot's index is built, through the same private registry discovery already
/// reads the index topology from — and read only where the structure holds the number itself. A hash
/// index is a dictionary of keys, so its key count is its distinct count; a range index holds its
/// keys in a heap beside a list of rows, and counting distinct keys there would mean walking the
/// whole index, so it is left unknown.
/// </para>
/// <para>
/// Unknown is a legitimate answer and the only alternative to an exact one: the planner divides by
/// this number, and <c>PocoIndexConformance.Verify</c> refuses an estimate.
/// </para>
/// </remarks>
internal static class AkadePhysicalStatistics
{
    /// <summary>
    /// The distinct keys of the Akade index filed under <paramref name="akadeIndexName"/>, or null
    /// when the structure does not hold the number.
    /// </summary>
    public static long? DistinctKeys<T>(IndexedSet<T> set, string akadeIndexName)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentException.ThrowIfNullOrWhiteSpace(akadeIndexName);

        try
        {
            var physical = Registry(set)?[akadeIndexName];
            if (physical is null)
            {
                return null;
            }

            var name = physical.GetType().Name;

            // UniqueIndex<T, TKey> keeps a Dictionary<TKey, T>: one entry per key.
            if (name.StartsWith("UniqueIndex", StringComparison.Ordinal))
            {
                return Field(physical, "_data") is IDictionary unique ? unique.Count : null;
            }

            // NonUniqueIndex<T, TKey> keeps a Lookup<TKey, T> whose own _values dictionary is keyed
            // by the key; the lookup's Count is the number of rows, not of keys.
            if (name.StartsWith("NonUniqueIndex", StringComparison.Ordinal))
            {
                return Field(physical, "_data") is { } lookup
                    && Field(lookup, "_values") is IDictionary values
                    ? values.Count
                    : null;
            }

            return null;
        }
        catch (NotSupportedException)
        {
            // The registry moved. Discovery itself fails closed on that; a statistic does not need
            // to, because unknown is a legitimate answer.
            return null;
        }
    }

    private static IDictionary? Registry<T>(IndexedSet<T> set)
    {
        foreach (var field in Fields(set.GetType()))
        {
            if (string.Equals(field.Name, "_indices", StringComparison.Ordinal))
            {
                return field.GetValue(set) as IDictionary;
            }
        }

        return null;
    }

    private static object? Field(object instance, string name)
    {
        foreach (var field in Fields(instance.GetType()))
        {
            if (string.Equals(field.Name, name, StringComparison.Ordinal))
            {
                return field.GetValue(instance);
            }
        }

        return null;
    }

    private static IEnumerable<FieldInfo> Fields(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var field in current.GetFields(
                         BindingFlags.Instance |
                         BindingFlags.NonPublic |
                         BindingFlags.Public |
                         BindingFlags.DeclaredOnly))
            {
                yield return field;
            }
        }
    }
}
