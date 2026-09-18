using System.Text;

namespace Chalk.Sources.Conformance;

/// <summary>
/// F16(e): every declared unique key and unique index of a table, checked against the data.
/// </summary>
/// <remarks>
/// <para>
/// This probe exists because of what step 21's second preliminary switched on. Calcite's
/// <c>PROJECT_JOIN_REMOVE</c> and <c>AGGREGATE_JOIN_REMOVE</c> delete a join outright when the other
/// side is unique on the join key — so a unique key that is not unique does not slow a query down,
/// it deletes a join that was multiplying rows and silently returns too few of them. The POCO source
/// verifies its own unique keys at <c>Build()</c> (D17); a remote source's are the database's word,
/// and this is where that word is checked.
/// </para>
/// <para>
/// The check is the design's: <c>GROUP BY key HAVING COUNT(*) &gt; 1</c>, run through the source's
/// own scan path rather than as a pushed query, because the scan is the path that has no capability
/// to be wrong about.
/// </para>
/// </remarks>
internal static class UniquenessProbe
{
    public static Task<IReadOnlyList<ConformanceFinding>> RunAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var findings = new List<ConformanceFinding>();
        var table = context.Table;

        foreach (var key in table.UniqueKeys)
        {
            findings.Add(Check(context, "unique key", key.Columns));
        }

        foreach (var index in table.Indexes.Where(i => i.Unique))
        {
            findings.Add(Check(context, $"unique index '{index.Name}'", index.Columns));
        }

        if (findings.Count == 0)
        {
            findings.Add(new ConformanceFinding
            {
                Subject = "uniqueness",
                Declared = "no unique key or unique index",
                Observed = "nothing to check",
                Outcome = ConformanceOutcome.Skipped,
                Advice = "A table that declares no uniqueness cannot have a join removed on it, "
                    + "which is safe but leaves the planner less to work with.",
            });
        }

        return Task.FromResult<IReadOnlyList<ConformanceFinding>>(findings);
    }

    /// <summary>
    /// One declared key, over the rows the scan produced. <c>GROUP BY … HAVING COUNT(*) &gt; 1</c>,
    /// evaluated here: a key containing a NULL is skipped, because SQL uniqueness says nothing about
    /// those and neither does Calcite's <c>areColumnsUnique</c>.
    /// </summary>
    private static ConformanceFinding Check(
        ConformanceContext context, string subject, IReadOnlyList<int> columns)
    {
        var names = Names(context, columns);
        if (columns.Count == 0)
        {
            return new ConformanceFinding
            {
                Subject = subject,
                Declared = "unique",
                Observed = "the key names no columns",
                Outcome = ConformanceOutcome.Fail,
            };
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var row = 0; row < context.Rows.Count; row++)
        {
            var values = context.Rows[row];
            var key = new StringBuilder();
            var complete = true;
            foreach (var column in columns)
            {
                if (column < 0 || column >= values.Length || values[column] is null)
                {
                    complete = false;
                    break;
                }

                key.Append(ConformanceValues.Text(values[column])).Append('');
            }

            if (!complete)
            {
                continue;
            }

            if (seen.TryGetValue(key.ToString(), out var first))
            {
                return new ConformanceFinding
                {
                    Subject = $"{subject} ({names})",
                    Declared = "unique",
                    Observed = $"rows {first} and {row} of the scan share the key "
                        + key.ToString().Replace('', '|'),
                    Outcome = ConformanceOutcome.Fail,
                    Advice = "Remove the declaration. Calcite's join-removal rules delete a join "
                        + "outright on the strength of a unique key, so a false one returns too "
                        + "few rows rather than too many (F16).",
                };
            }

            seen[key.ToString()] = row;
        }

        return new ConformanceFinding
        {
            Subject = $"{subject} ({names})",
            Declared = "unique",
            Observed = $"{seen.Count} distinct non-NULL keys over {context.Rows.Count} rows",
            Outcome = ConformanceOutcome.Pass,
        };
    }

    private static string Names(ConformanceContext context, IReadOnlyList<int> columns) =>
        string.Join(
            ", ",
            columns.Select(c => c >= 0 && c < context.Table.Columns.Count
                ? context.Table.Columns[c].Name
                : "?"));
}
