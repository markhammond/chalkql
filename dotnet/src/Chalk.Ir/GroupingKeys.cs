namespace Chalk.Ir;

/// <summary>
/// The keys an aggregate's output carries (D338). One grouping is a <c>GROUP BY</c>, and its keys
/// are the output's, in its order. More than one is <c>GROUPING SETS</c>, <c>ROLLUP</c> or
/// <c>CUBE</c>: the output's keys are every key any grouping names, once each, in ascending field
/// order, and a key a grouping does not name is NULL in that grouping's rows.
/// </summary>
public static class GroupingKeys
{
    /// <summary>The input field index of each of the output's keys, in output order.</summary>
    public static int[] Of(Aggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return aggregate.Groupings.Count == 1
            ? [.. aggregate.Groupings[0].Keys.Select(k => (int)k)]
            : [.. aggregate.Groupings.SelectMany(g => g.Keys).Select(k => (int)k).Distinct().Order()];
    }

    /// <summary>
    /// Whether every grouping names input field <paramref name="key"/>: only then is the output's
    /// key as nullable as the input's, rather than nullable.
    /// </summary>
    public static bool InEvery(Aggregate aggregate, int key)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return aggregate.Groupings.All(g => g.Keys.Contains((uint)key));
    }
}
