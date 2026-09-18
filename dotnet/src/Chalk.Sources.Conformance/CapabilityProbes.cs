using Chalk.Catalog;
using Chalk.Ir;

namespace Chalk.Sources.Conformance;

/// <summary>
/// D88 (a): every capability the descriptor declares, run on and off, and the two answers compared.
/// </summary>
/// <remarks>
/// "On" is the predicate inside the query the source runs; "off" is the same predicate evaluated by
/// the kit over the source's own scan. A false positive — a row the source returned that the
/// predicate excludes — and a false negative — a row it excluded that the predicate keeps — both
/// fail the run naming the shape and the row, because both are wrong answers a host would never
/// see as errors.
/// </remarks>
internal static class CapabilityProbes
{
    public static async Task<IReadOnlyList<ConformanceFinding>> RunAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var findings = new List<ConformanceFinding>();
        var capabilities = context.Capabilities;

        if (capabilities.QueryLanguage != QueryLanguage.Sql)
        {
            findings.Add(new ConformanceFinding
            {
                Subject = "capability comparisons",
                Declared = capabilities.QueryLanguage.ToString(),
                Observed = "the kit writes SQL, so it can only compare a SQL source's answers",
                Outcome = ConformanceOutcome.Skipped,
                Advice = "An IR source's capabilities are checked by the property test instead.",
            });

            // D273: a conditional is the one capability that is on unless the descriptor says
            // otherwise, so an IR adapter that cannot interpret an `IfThen` will be handed one
            // without ever having claimed it could. Every probe below is SQL text and there is no
            // IR twin of them to send instead, so the kit names the claim rather than testing it.
            if (capabilities.SupportsCase)
            {
                findings.Add(new ConformanceFinding
                {
                    Subject = "SupportsCase",
                    Declared = "the source interprets an IfThen in a pushed plan",
                    Observed = "unverified: this kit probes with SQL text and this source takes IR",
                    Outcome = ConformanceOutcome.Skipped,
                    Advice = "SupportsCase is true unless a descriptor says otherwise (D273). An "
                        + "adapter that does not implement Expr.IfThen must set SupportsCase = "
                        + "false, or it will be sent one.",
                });
            }

            return findings;
        }

        var text = context.Column("text_value");
        var number = context.Column("int_value");

        foreach (var shape in capabilities.PushablePredicates)
        {
            var probe = ProbeFor(shape, context, text, number);
            if (probe is null)
            {
                findings.Add(new ConformanceFinding
                {
                    Subject = $"PredicateShape.{shape}",
                    Declared = "pushable",
                    Observed = "the kit has no probe for this shape",
                    Outcome = ConformanceOutcome.Skipped,
                });
                continue;
            }

            findings.Add(await CompareAsync(context, $"PredicateShape.{shape}", probe.Value, ct)
                .ConfigureAwait(false));
        }

        if (capabilities.SupportsProject)
        {
            findings.Add(await ProjectionAsync(context, ct).ConfigureAwait(false));
        }

        // D273. A searched conditional with three arms, one of them for NULL, and an ELSE: what a
        // sanitiser is made of, and the shape the pushdown gate now admits. It is judged as a
        // predicate shape is — the rows the source answers against the rows the same rule picks
        // here — because a dialect that spells CASE and means something else by it is exactly the
        // silent wrong answer this kit exists to find.
        if (capabilities.SupportsCase)
        {
            var numberColumn = context.ColumnIndex("int_value");
            findings.Add(await CompareAsync(
                context,
                "SupportsCase",
                new Probe(
                    $"CASE WHEN {number} IS NULL THEN 0 WHEN {number} > 0 THEN 1 ELSE 2 END = 1",
                    row => row[numberColumn] is long v && v > 0),
                ct,
                "Set SupportsCase = false.").ConfigureAwait(false));
        }

        if (capabilities.SupportsLimit)
        {
            findings.Add(await LimitAsync(context, ct).ConfigureAwait(false));
        }

        if (capabilities.SupportsGroupBy)
        {
            findings.Add(await GroupByAsync(context, ct).ConfigureAwait(false));
        }

        return findings;
    }

    /// <summary>One predicate: the SQL the source evaluates, and the same rule applied here.</summary>
    private readonly record struct Probe(string Sql, Func<object?[], bool> Expected);

    private static Probe? ProbeFor(
        PredicateShape shape, ConformanceContext context, string text, string number)
    {
        var textColumn = context.ColumnIndex("text_value");
        var numberColumn = context.ColumnIndex("int_value");

        return shape switch
        {
            PredicateShape.Eq => new Probe(
                $"{number} = 7",
                row => row[numberColumn] is long v && v == 7),
            PredicateShape.Range => new Probe(
                $"{number} > 0",
                row => row[numberColumn] is long v && v > 0),
            PredicateShape.In => new Probe(
                $"{number} IN (1, 3, 7, 100)",
                row => row[numberColumn] is long v && (v == 1 || v == 3 || v == 7 || v == 100)),
            PredicateShape.IsNull => new Probe(
                $"{number} IS NULL",
                row => row[numberColumn] is null),
            PredicateShape.Not => new Probe(
                $"NOT ({number} = 7)",
                row => row[numberColumn] is long v && v != 7),
            PredicateShape.And => new Probe(
                $"{number} > 0 AND {number} < 100",
                row => row[numberColumn] is long v && v is > 0 and < 100),
            PredicateShape.Or => new Probe(
                $"{number} = 7 OR {number} = 3",
                row => row[numberColumn] is long v && (v == 7 || v == 3)),
            PredicateShape.LikePrefix => new Probe(
                $"{text} LIKE 'alph%'",
                row => row[textColumn] is string s && s.StartsWith("alph", StringComparison.Ordinal)),
            PredicateShape.Like => new Probe(
                $"{text} LIKE '%lph%'",
                row => row[textColumn] is string s && s.Contains("lph", StringComparison.Ordinal)),
            _ => null,
        };
    }

    /// <param name="remedy">
    /// What a host does about a disagreement — which declaration to take back. The shapes are taken
    /// out of <c>PushablePredicates</c>; a conditional is turned off with <c>SupportsCase</c>.
    /// </param>
    private static async Task<ConformanceFinding> CompareAsync(
        ConformanceContext context,
        string subject,
        Probe probe,
        CancellationToken ct,
        string remedy = "Remove it from PushablePredicates.")
    {
        var expected = context.Rows.Where(probe.Expected).ToList();
        IReadOnlyList<object?[]> actual;
        try
        {
            actual = await context
                .QueryAsync(context.SelectIdsWhere(probe.Sql), context.IdOnly, ct)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return new ConformanceFinding
            {
                Subject = subject,
                Declared = "pushable",
                Observed = $"the source refused the query: {failure.Message}",
                Outcome = ConformanceOutcome.Fail,
                Advice = $"The query was: {context.SelectIdsWhere(probe.Sql)}",
            };
        }

        var idColumn = context.ColumnIndex("id");
        var expectedIds = Sorted(expected.Select(r => ConformanceValues.Text(r[idColumn])));
        var actualIds = Sorted(actual.Select(r => ConformanceValues.Text(r[0])));

        return expectedIds == actualIds
            ? new ConformanceFinding
            {
                Subject = subject,
                Declared = "pushable with Chalk's semantics",
                Observed = $"the same {actual.Count} rows the scan gives",
                Outcome = ConformanceOutcome.Pass,
            }
            : new ConformanceFinding
            {
                Subject = subject,
                Declared = $"rows {expectedIds}",
                Observed = $"rows {actualIds}",
                Outcome = ConformanceOutcome.Fail,
                Advice = $"The source evaluates this shape differently from Chalk. {remedy} The "
                    + $"query was: {context.SelectIdsWhere(probe.Sql)}",
            };
    }

    private static async Task<ConformanceFinding> ProjectionAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var sql = $"SELECT {context.Column("id")}, {context.Column("int_value")} FROM {context.TableName()}";
        IReadOnlyList<ColumnDescriptor> columns =
        [
            new() { Name = "id", Type = ChalkType.Int64() },
            new() { Name = "int_value", Type = ChalkType.Int64(nullable: true) },
        ];

        try
        {
            var rows = await context.QueryAsync(sql, columns, ct).ConfigureAwait(false);
            return rows.Count == context.Rows.Count
                ? new ConformanceFinding
                {
                    Subject = "SupportsProject",
                    Declared = "the source projects columns",
                    Observed = $"{rows.Count} rows of 2 columns, as the scan has",
                    Outcome = ConformanceOutcome.Pass,
                }
                : new ConformanceFinding
                {
                    Subject = "SupportsProject",
                    Declared = $"{context.Rows.Count} rows",
                    Observed = $"{rows.Count} rows",
                    Outcome = ConformanceOutcome.Fail,
                };
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return Refused("SupportsProject", sql, failure);
        }
    }

    private static async Task<ConformanceFinding> LimitAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var sql = $"SELECT {context.Column("id")} FROM {context.TableName()} LIMIT 3";
        try
        {
            var rows = await context.QueryAsync(sql, context.IdOnly, ct).ConfigureAwait(false);
            return rows.Count == 3
                ? new ConformanceFinding
                {
                    Subject = "SupportsLimit",
                    Declared = "the source honours LIMIT",
                    Observed = "3 rows for LIMIT 3",
                    Outcome = ConformanceOutcome.Pass,
                }
                : new ConformanceFinding
                {
                    Subject = "SupportsLimit",
                    Declared = "3 rows for LIMIT 3",
                    Observed = $"{rows.Count} rows",
                    Outcome = ConformanceOutcome.Fail,
                };
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return Refused("SupportsLimit", sql, failure);
        }
    }

    private static async Task<ConformanceFinding> GroupByAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var other = context.Column("text_other");
        // The alias is not decoration: the reader checks the driver's reported column names
        // against the ones the probe declares below, once per result set (F35), and a source that
        // named this column after its own aggregate would look like a column swap.
        var sql = $"SELECT COUNT(*) AS {context.Column("n")} FROM {context.TableName()} "
            + $"GROUP BY {other}";
        IReadOnlyList<ColumnDescriptor> columns = [new() { Name = "n", Type = ChalkType.Int64() }];

        var expected = context.Rows
            .GroupBy(r => ConformanceValues.Text(r[context.ColumnIndex("text_other")]))
            .Count();

        try
        {
            var rows = await context.QueryAsync(sql, columns, ct).ConfigureAwait(false);
            return rows.Count == expected
                ? new ConformanceFinding
                {
                    Subject = "SupportsGroupBy",
                    Declared = "the source groups as Chalk does",
                    Observed = $"{rows.Count} groups, as the scan has",
                    Outcome = ConformanceOutcome.Pass,
                }
                : new ConformanceFinding
                {
                    Subject = "SupportsGroupBy",
                    Declared = $"{expected} groups",
                    Observed = $"{rows.Count} groups",
                    Outcome = ConformanceOutcome.Fail,
                    Advice = "The source groups values Chalk sees as different into one group, or "
                        + "the other way round — most often a string collation difference.",
                };
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return Refused("SupportsGroupBy", sql, failure);
        }
    }

    private static ConformanceFinding Refused(string subject, string sql, Exception failure) => new()
    {
        Subject = subject,
        Declared = "supported",
        Observed = $"the source refused the query: {failure.Message}",
        Outcome = ConformanceOutcome.Fail,
        Advice = $"The query was: {sql}",
    };

    private static string Sorted(IEnumerable<string> values) =>
        string.Join(",", values.OrderBy(v => v, StringComparer.Ordinal));
}
