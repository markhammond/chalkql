using System.Globalization;
using System.Text;
using Chalk.Sample.Blotter.Market;
using DuckDB.NET.Data;

namespace Chalk.Sample.Blotter.Live;

/// <summary>
/// A tab's answer worked out the slow way, for checking: in DuckDB alone, with no tail, no scope and
/// no merge, and with the filter's values written in. A state covers every event the feed sent through
/// its stream's watermark (<see cref="ScopeState.IngestedThrough"/>); the oracle takes exactly those.
/// </summary>
/// <remarks>
/// For orders: the state's generation and every version sent through that time, the highest version
/// of each order kept. For fills: every fill sent through that time, from the database and the stream,
/// whatever the generation; a fill in both is one row.
/// </remarks>
public static class Oracle
{
    /// <summary>Rows <paramref name="start"/> on, and how many rows there are, as <paramref name="state"/> should show them.</summary>
    public static (IRow[] Rows, long Total) Page(
        TradeStore store, Exchange market, ScopeState state, SortSpec order, long start, int count)
    {
        var generation = state.Generation ?? throw new InvalidOperationException("the state has no generation");
        var tab = state.Tab;
        using var connection = store.Open();
        var sent = "oracle_" + Guid.NewGuid().ToString("N");
        Execute(connection, tab.Overrides
            ? $"CREATE TEMP TABLE {sent} AS SELECT * FROM order_versions WHERE false"
            : $"CREATE TEMP TABLE {sent} AS SELECT * FROM fills WHERE false");
        using (var appender = connection.CreateAppender(sent))
        {
            var rows = tab.Overrides
                ? market.Orders.Through(state.OrdersThrough).Select(o => o.Values())
                : market.Fills.Through(state.FillsThrough).Select(f => f.Values());
            foreach (var values in rows)
            {
                var row = appender.CreateRow();
                foreach (var value in values)
                {
                    row = value switch
                    {
                        Guid v => row.AppendValue(v),
                        long v => row.AppendValue(v),
                        int v => row.AppendValue(v),
                        double v => row.AppendValue(v),
                        DateTime v => row.AppendValue(v),
                        string v => row.AppendValue(v),
                        _ => throw new InvalidOperationException($"the oracle cannot append a {value?.GetType().Name}"),
                    };
                }

                row.EndRow();
            }
        }

        try
        {
            var through = string.Create(CultureInfo.InvariantCulture, $"TIMESTAMP '{state.IngestedThrough:yyyy-MM-dd HH:mm:ss.ffffff}'");
            var rowsOf = tab.Overrides
                ? string.Create(CultureInfo.InvariantCulture, $"""
                    SELECT * FROM (
                        SELECT {Order.Select} FROM snapshots WHERE generation = {generation.Number}
                        UNION ALL
                        SELECT {Order.Select} FROM {sent}
                    )
                    QUALIFY row_number() OVER (PARTITION BY order_id ORDER BY version DESC) = 1
                    """)
                : $"""
                    SELECT {Fill.Select} FROM fills WHERE event_time <= {through}
                    UNION
                    SELECT {Fill.Select} FROM {sent}
                    """;
            var where = state.Filter.Literal();
            using var total = connection.CreateCommand();
            total.CommandText = $"SELECT count(*) FROM ({rowsOf}) WHERE {where}";
            var all = Convert.ToInt64(total.ExecuteScalar(), CultureInfo.InvariantCulture);

            using var page = connection.CreateCommand();
            page.CommandText = string.Create(CultureInfo.InvariantCulture,
                $"SELECT {tab.Select} FROM ({rowsOf}) WHERE {where} ORDER BY {order.OrderBy()} LIMIT {count} OFFSET {start}");
            using var reader = page.ExecuteReader();
            var found = new List<IRow>();
            while (reader.Read())
            {
                found.Add(tab.ReadDuck(reader));
            }

            return ([.. found], all);
        }
        finally
        {
            Execute(connection, $"DROP TABLE {sent}");
        }
    }

    /// <summary>What differs between the blotter's page and the oracle's, or null when nothing does.</summary>
    public static string? Compare(Page page, IRow[] expected, long expectedTotal)
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
            if (!Equals(page.Rows[i], expected[i]))
            {
                problems.Append(CultureInfo.InvariantCulture, $"row {page.Start + i}: {page.Rows[i]}, expected {expected[i]}; ");
                break;
            }
        }

        return problems.Length == 0 ? null : problems.ToString();
    }

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
