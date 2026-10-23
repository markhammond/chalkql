using System.Globalization;
using System.Text;
using Chalk.Sample.Blotter.Market;
using DuckDB.NET.Data;

namespace Chalk.Sample.Blotter.Live;

/// <summary>
/// The blotter's answer worked out the slow way, for checking: in DuckDB alone, with no tail, no
/// scope and no merge. A state covers its generation and every version the feed sent through its
/// <see cref="ScopeState.IngestedThrough"/>; the oracle takes exactly those, keeps the highest
/// version of each trade, filters, sorts and pages them in one statement, and so is right by
/// construction for that state.
/// </summary>
public static class Oracle
{
    /// <summary>Rows <paramref name="start"/> on, and how many rows there are, as <paramref name="state"/> should show them.</summary>
    public static (TradeVersion[] Rows, long Total) Page(
        TradeStore store, TradeFeed feed, ScopeState state, SortSpec order, long start, int count)
    {
        var generation = state.Generation ?? throw new InvalidOperationException("the state has no generation");
        using var connection = store.Open();
        var table = "oracle_" + Guid.NewGuid().ToString("N");
        Execute(connection, $"CREATE TEMP TABLE {table} AS SELECT * EXCLUDE (generation) FROM snapshots WHERE false");
        using (var appender = connection.CreateAppender(table))
        {
            foreach (var v in feed.Through(state.IngestedThrough))
            {
                appender.CreateRow()
                    .AppendValue(v.TradeId).AppendValue(v.Version).AppendValue(v.EventTime).AppendValue(v.TradeTime)
                    .AppendValue(v.Client).AppendValue(v.Symbol).AppendValue(v.Side).AppendValue(v.OrderQty)
                    .AppendValue(v.FilledQty).AppendValue(v.AvgPrice).AppendValue(v.Status).AppendValue(v.Venue)
                    .EndRow();
            }
        }

        try
        {
            var latest = string.Create(CultureInfo.InvariantCulture, $"""
                SELECT * FROM (
                    SELECT {TradeVersion.Select} FROM snapshots WHERE generation = {generation.Number}
                    UNION ALL
                    SELECT {TradeVersion.Select} FROM {table}
                )
                QUALIFY row_number() OVER (PARTITION BY trade_id ORDER BY version DESC) = 1
                """);
            var where = Where(state.Filter);
            using var total = connection.CreateCommand();
            total.CommandText = $"SELECT count(*) FROM ({latest}) WHERE {where}";
            var rows = Convert.ToInt64(total.ExecuteScalar(), CultureInfo.InvariantCulture);

            using var page = connection.CreateCommand();
            page.CommandText = string.Create(CultureInfo.InvariantCulture,
                $"SELECT {TradeVersion.Select} FROM ({latest}) WHERE {where} ORDER BY {order.OrderBy()} LIMIT {count} OFFSET {start}");
            using var reader = page.ExecuteReader();
            var found = new List<TradeVersion>();
            while (reader.Read())
            {
                found.Add(TradeStore.Read(reader));
            }

            return ([.. found], rows);
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
        }
    }

    /// <summary>What differs between the blotter's page and the oracle's, or null when nothing does.</summary>
    public static string? Compare(Page page, TradeVersion[] expected, long expectedTotal)
    {
        var problems = new StringBuilder();
        if (page.Total != expectedTotal)
        {
            problems.Append(CultureInfo.InvariantCulture, $"total {page.Total}, expected {expectedTotal}; ");
        }

        if (page.Rows.Length != expected.Length)
        {
            problems.Append(CultureInfo.InvariantCulture, $"{page.Rows.Length} rows, expected {expected.Length}; ");
        }

        for (var i = 0; i < Math.Min(page.Rows.Length, expected.Length); i++)
        {
            if (page.Rows[i] != expected[i])
            {
                problems.Append(CultureInfo.InvariantCulture,
                    $"row {page.Start + i}: {page.Rows[i]}, expected {expected[i]}; ");
                break;
            }
        }

        return problems.Length == 0 ? null : problems.ToString();
    }

    /// <summary>The filter with its values written in, which only the oracle does.</summary>
    private static string Where(FilterSpec filter)
    {
        var conditions = new List<string>();
        if (!filter.Clients.IsEmpty)
        {
            conditions.Add($"client IN ({Quoted(filter.Clients)})");
        }

        if (!filter.Symbols.IsEmpty)
        {
            conditions.Add($"symbol IN ({Quoted(filter.Symbols)})");
        }

        if (!filter.Statuses.IsEmpty)
        {
            conditions.Add($"status IN ({Quoted(filter.Statuses)})");
        }

        if (filter.Side is { } side)
        {
            conditions.Add($"side = '{side}'");
        }

        if (filter.From is { } from)
        {
            conditions.Add(string.Create(CultureInfo.InvariantCulture, $"trade_time >= TIMESTAMP '{from:yyyy-MM-dd HH:mm:ss.ffffff}'"));
        }

        if (filter.To is { } to)
        {
            conditions.Add(string.Create(CultureInfo.InvariantCulture, $"trade_time < TIMESTAMP '{to:yyyy-MM-dd HH:mm:ss.ffffff}'"));
        }

        return conditions.Count == 0 ? "TRUE" : string.Join(" AND ", conditions);
    }

    private static string Quoted(IEnumerable<string> values) => string.Join(", ", values.Select(v => "'" + v.Replace("'", "''", StringComparison.Ordinal) + "'"));

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
