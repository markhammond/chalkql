using System.Globalization;
using Chalk.Client;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The corpus in DuckDB's dialect (D28). Most of it needs no translation at all — DuckDB takes
/// <c>CHAR_LENGTH</c>, <c>SUBSTRING(x FROM a FOR b)</c>, <c>||</c>, <c>EXTRACT</c>,
/// <c>DATE '…' - INTERVAL '90' DAY</c> and quoted <c>"close"</c> as written. Two Calcite spellings
/// have to be translated, and the parameterised queries reach DuckDB in the expanded literal form,
/// which is the interesting half: it is the text D29's IN-list expansion produces, checked by an
/// engine that knows nothing about the rewriter.
/// </summary>
internal static partial class DuckDbDialect
{
    /// <summary>
    /// The SQL to run against DuckDB for a corpus query. Never null: every M1 corpus query turned
    /// out to be portable, which is itself worth knowing — the two that are not written in DuckDB's
    /// spelling are translated here, not skipped.
    /// </summary>
    public static string For(CorpusQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var bindings = DifferentialRunner.BindingsFor(query.Name);
        var sql = Limits(bindings is null ? query.Sql : Expand(query, bindings));

        return query.Name switch
        {
            // Step 22's three SQL bodies, written out the way the planner inlines them. That is the
            // point of the comparison: an inlined body is ordinary algebra, and DuckDB can check it.
            "01_pct_change_inlined" => sql.Replace(
                "pct_change(\"open\", \"close\")",
                "(\"close\" - \"open\") / \"open\"",
                StringComparison.Ordinal),
            "02_wavg_expanded" => sql.Replace(
                "wavg(\"close\", volume)",
                "SUM(\"close\" * volume) / SUM(volume)",
                StringComparison.Ordinal),
            "03_bars_for_macro" =>
                "SELECT symbol, ts, \"close\" FROM bars WHERE symbol = 'BTCUSDT' ORDER BY ts",
            // FLOOR(<datetime> TO <unit>) is Calcite's spelling of date_trunc.
            "09_group_by_hour" => sql.Replace(
                "FLOOR(ts TO HOUR)", "date_trunc('hour', ts)", StringComparison.Ordinal),
            "08_daily_open_close" => sql.Replace(
                "FLOOR(ts TO DAY)", "date_trunc('day', ts)", StringComparison.Ordinal),

            // SQL:2011 puts IGNORE NULLS after the argument list; DuckDB 1.5 puts it inside, after
            // the last argument. Same semantics, different place.
            "15_lag_ignore_nulls" => sql.Replace(
                "LAG(vwap, 1) IGNORE NULLS", "LAG(vwap, 1 IGNORE NULLS)", StringComparison.Ordinal),

            // Calcite's TUMBLE table function against DuckDB's time_bucket, which is the projection
            // Chalk rewrites it into (D52) and is what corpus query 17 writes by hand.
            "18_tumble" => """
                SELECT symbol, time_bucket(INTERVAL 5 MINUTE, ts) AS window_start, SUM(volume) AS v
                FROM bars_small
                GROUP BY symbol, time_bucket(INTERVAL 5 MINUTE, ts)
                ORDER BY symbol, window_start
                """,

            // SQL:2016's LISTAGG is DuckDB's string_agg, and the ordering goes inside the call
            // rather than into a WITHIN GROUP clause.
            "06_listagg" => sql.Replace(
                "LISTAGG(symbol, ',') WITHIN GROUP (ORDER BY symbol)",
                "string_agg(symbol, ',' ORDER BY symbol)",
                StringComparison.Ordinal),

            // The windowed holistic aggregates (D57). LISTAGG over a frame is DuckDB's string_agg
            // and ARRAY_AGG over a frame is its list(); both take a frame clause as written.
            "25_listagg_over_window" => sql.Replace(
                "LISTAGG(CAST(trade_count AS VARCHAR), ',')",
                "string_agg(CAST(trade_count AS VARCHAR), ',')",
                StringComparison.Ordinal),
            "26_array_agg_over_window" => sql.Replace(
                "ARRAY_AGG(volume)", "list(volume)", StringComparison.Ordinal),

            // DuckDB reserves CARDINALITY for MAPs; a list's length is len().
            "08_array_agg_round_trip" or "10_list_column" => sql.Replace(
                "CARDINALITY(", "len(", StringComparison.Ordinal),

            // DuckDB has LATERAL but not APPLY, which is the whole point of D68's conformance gate:
            // the syntax is one dialect's and the meaning is everybody's.
            "21_outer_apply_lenient" => sql
                .Replace("OUTER APPLY (", "LEFT JOIN LATERAL (", StringComparison.Ordinal)
                .Replace("LIMIT 1) b", "LIMIT 1) b ON TRUE", StringComparison.Ordinal),

            // Calcite spells an ASOF join `left [LEFT] ASOF JOIN right MATCH_CONDITION <cmp> ON
            // <equalities>`; DuckDB spells it `ASOF [LEFT] JOIN right ON <equalities> AND <cmp>`.
            // Same operator, same semantics, different word order.
            _ when sql.Contains("ASOF", StringComparison.Ordinal) => AsOf(sql),

            _ => sql,
        };
    }

    /// <summary>
    /// <c>OFFSET n ROWS FETCH NEXT m ROWS ONLY</c> is the SQL standard's and is what the corpus is
    /// written in; DuckDB spells the same bound <c>LIMIT m OFFSET n</c>. Rewritten for every query
    /// rather than one at a time, because the <c>edge</c> family is full of them (D165).
    /// </summary>
    private static string Limits(string sql)
    {
        sql = OffsetAndFetch().Replace(sql, "LIMIT $2 OFFSET $1");
        sql = FetchOnly().Replace(sql, "LIMIT $1");
        return OffsetOnly().Replace(sql, "OFFSET $1");
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"OFFSET\s+(\d+)\s+ROWS?\s+FETCH\s+(?:NEXT|FIRST)\s+(\d+)\s+ROWS?\s+ONLY",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex OffsetAndFetch();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"FETCH\s+(?:NEXT|FIRST)\s+(\d+)\s+ROWS?\s+ONLY",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FetchOnly();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"OFFSET\s+(\d+)\s+ROWS?(?!\s*\w)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex OffsetOnly();

    /// <summary>Rewrites Calcite's ASOF syntax into DuckDB's.</summary>
    private static string AsOf(string sql)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            sql,
            @"(?<outer>LEFT\s+)?ASOF\s+JOIN\s+(?<right>.+?)\s+MATCH_CONDITION\s+(?<cmp>.+?)\s+ON\s+(?<on>.+?)(?=\r?\n(?:ORDER|WHERE|GROUP|LIMIT)|$)",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(match.Success, "the ASOF query does not match the shape the translation expects:\n" + sql);

        var replacement =
            "ASOF " + (match.Groups["outer"].Success ? "LEFT " : string.Empty)
            + "JOIN " + match.Groups["right"].Value.Trim()
            + " ON " + match.Groups["on"].Value.Trim()
            + " AND " + match.Groups["cmp"].Value.Trim();
        return sql.Remove(match.Index, match.Length).Insert(match.Index, replacement);
    }

    /// <summary>
    /// The statement as the rewriter renders it for the shape the differential tests bind, with every
    /// <c>?</c> replaced by its literal. Derived from <see cref="ParameterRewriter"/> rather than
    /// written out by hand, so an oracle run cannot silently stop testing what the rewriter does.
    /// </summary>
    private static string Expand(CorpusQuery query, IReadOnlyDictionary<string, object?> bindings)
    {
        var rewriter = ParameterRewriter.Parse(query.Sql);
        var values = Positional(rewriter, bindings);
        var rendered = rewriter.Render(ParameterBinder.ShapeOf(rewriter.Parameters, values));

        var sql = rendered.Sql;
        var result = new System.Text.StringBuilder(sql.Length);
        var slot = 0;
        foreach (var c in sql)
        {
            if (c != '?')
            {
                result.Append(c);
                continue;
            }

            result.Append(Literal(values, rendered.Slots[slot++]));
        }

        Assert.Equal(rendered.Slots.Count, slot);
        return result.ToString();
    }

    /// <summary>The bound values in the order the rewriter's parameters expect them.</summary>
    private static object?[] Positional(
        ParameterRewriter rewriter, IReadOnlyDictionary<string, object?> bindings) =>
        [.. rewriter.Parameters.Select((p, i) => p.Name is { } name ? bindings[name] : bindings["p" + i])];

    private static string Literal(object?[] values, PlaceholderSlot slot)
    {
        if (slot.ElementIndex == PlaceholderSlot.EmptyListNull)
        {
            return "NULL";
        }

        var value = values[slot.ParameterIndex];
        if (slot.ElementIndex != PlaceholderSlot.Scalar)
        {
            value = ((System.Collections.IEnumerable)value!).Cast<object?>().ElementAt(slot.ElementIndex);
        }

        return value switch
        {
            null => "NULL",
            string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
            Utf8String text => "'" + text.ToString().Replace("'", "''", StringComparison.Ordinal) + "'",
            DateTime moment =>
                "TIMESTAMP '" + moment.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'",
            DateOnly date => "DATE '" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'",
            bool flag => flag ? "TRUE" : "FALSE",
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"no DuckDB literal for {value.GetType().Name}"),
        };
    }
}
