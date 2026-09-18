using System.Globalization;
using Apache.Arrow;
using Chalk.Client;
using Chalk.Ir;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// The printing and reading a chapter would otherwise repeat. Nothing here is Chalk API — it is the
/// tutorial's own scaffolding, kept out of the chapters so each of them is only the thing it shows.
/// </summary>
public static class Tutorial
{
    /// <summary>The banner a chapter opens with.</summary>
    public static void Chapter(int number, string title, string point)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"Chapter {number}. {title}");
        Console.WriteLine(new string('=', 78));
        Console.WriteLine(point);
    }

    /// <summary>A labelled step within a chapter.</summary>
    public static void Step(string text)
    {
        Console.WriteLine();
        Console.WriteLine("-- " + text);
    }

    /// <summary>The statement, as the host wrote it.</summary>
    public static void Sql(string sql)
    {
        Console.WriteLine();
        foreach (var line in sql.Trim().Split('\n'))
        {
            Console.WriteLine("    " + line.TrimEnd());
        }

        Console.WriteLine();
    }

    /// <summary>The plan, printed by <see cref="PlanPrinter"/> — the IR, not the planner's text.</summary>
    public static void Plan(string label, Plan plan)
    {
        Console.WriteLine(label + ":");
        foreach (var line in PlanPrinter.Print(plan).TrimEnd().Split('\n'))
        {
            Console.WriteLine("    " + line.TrimEnd());
        }
    }

    /// <summary>
    /// Every <c>RemoteQuery</c> in a plan: which source, in which dialect, and the SQL Chalk
    /// generated for it. This is the text the other side is asked to run.
    /// </summary>
    public static void RemoteQueries(Plan plan)
    {
        var remotes = PlanWalker.Rels(plan)
            .Where(r => r.KindCase == Rel.KindOneofCase.RemoteQuery)
            .ToList();

        if (remotes.Count == 0)
        {
            Console.WriteLine("    (no RemoteQuery: nothing was pushed, so the source is scanned)");
            return;
        }

        foreach (var rel in remotes)
        {
            Console.WriteLine(
                $"    {rel.RemoteQuery.SourceId} ({rel.RemoteQuery.Dialect}): {rel.RemoteQuery.QueryText}");
        }
    }

    /// <summary>The plan's node kinds, root first — the shape without the detail.</summary>
    public static string Kinds(Plan plan) =>
        string.Join(" ", PlanWalker.Rels(plan).Select(r => r.KindCase.ToString()));

    /// <summary>
    /// What an execution actually did. Counters only: no wall clock, because a tutorial that
    /// printed one would be inviting the reader to compare machines.
    /// </summary>
    public static void Counters(QueryExecution execution)
    {
        var stats = execution.Stats;
        Console.WriteLine(
            $"    {stats.RowsProduced} produced, {stats.RowsScanned} scanned, "
            + $"{stats.RemoteCalls} remote call(s), {stats.RowsFetched} rows fetched");

        foreach (var fetch in stats.SourceFetches.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"    {fetch.Key}: calls={fetch.Value.Calls} rows={fetch.Value.Rows}");
        }

        foreach (var decision in stats.AdaptiveDecisions)
        {
            Console.WriteLine($"    adaptive: {decision}");
        }
    }

    /// <summary>Any block of text, indented so it reads as output rather than as prose.</summary>
    public static void Block(string label, string text)
    {
        Console.WriteLine(label + ":");
        foreach (var line in text.TrimEnd().Split('\n'))
        {
            Console.WriteLine("    " + line.TrimEnd());
        }
    }

    /// <summary>
    /// Runs an execution to the end and prints its rows as a table. Batches are the caller's to
    /// dispose, which is what this does with each one as soon as it has been read.
    /// </summary>
    public static async Task<int> PrintAsync(QueryExecution execution, int maxRows = 12)
    {
        var names = execution.Schema.FieldsList.Select(f => f.Name).ToArray();
        var rows = new List<string[]>();
        var total = 0;

        await foreach (var batch in execution.Batches)
        {
            for (var row = 0; row < batch.Length; row++)
            {
                if (rows.Count < maxRows)
                {
                    rows.Add([.. Enumerable.Range(0, batch.ColumnCount).Select(c => Cell(batch.Column(c), row))]);
                }
            }

            total += batch.Length;
            batch.Dispose();
        }

        var widths = names.Select((n, i) => Math.Max(n.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        Console.WriteLine("    " + string.Join("  ", names.Select((n, i) => n.PadRight(widths[i]))).TrimEnd());
        Console.WriteLine("    " + string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
        {
            Console.WriteLine("    " + string.Join("  ", row.Select((v, i) => v.PadRight(widths[i]))).TrimEnd());
        }

        if (total > rows.Count)
        {
            Console.WriteLine($"    … {total - rows.Count} more rows");
        }

        return total;
    }

    /// <summary>Runs an execution to the end without printing anything, and returns the row count.</summary>
    public static async Task<int> DrainAsync(QueryExecution execution)
    {
        var total = 0;
        await foreach (var batch in execution.Batches)
        {
            total += batch.Length;
            batch.Dispose();
        }

        return total;
    }

    /// <summary>One cell, as text. Invariant culture, because the doc quotes this output.</summary>
    private static string Cell(IArrowArray array, int row)
    {
        if (array.IsNull(row))
        {
            return "NULL";
        }

        return array switch
        {
            BooleanArray a => a.GetValue(row)!.Value ? "true" : "false",
            Int32Array a => a.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
            Int64Array a => a.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
            DoubleArray a => a.GetValue(row)!.Value.ToString("0.####", CultureInfo.InvariantCulture),
            FloatArray a => a.GetValue(row)!.Value.ToString("0.####", CultureInfo.InvariantCulture),
            Decimal128Array a => a.GetValue(row)!.Value.ToString("0.######", CultureInfo.InvariantCulture),
            StringArray a => a.GetString(row),

            // Both Arrow UTF-8 layouts: a STRING column is views by default and classic when
            // the host asked for it through Output.Strings.
            StringViewArray a => a.GetString(row),
            Date32Array a => a.GetDateOnly(row)!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimestampArray a => a.GetTimestamp(row)!.Value.UtcDateTime
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            _ => array.GetType().Name,
        };
    }
}
