using System.Globalization;
using System.Text;
using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Fixtures;
using Chalk.Sample.Blotter.Domain;
using Chalk.Sample.Blotter.Grid;
using Chalk.Sample.Blotter.Postgres;
using Npgsql;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>
/// A tab's answer worked out the slow way, for checking: in PostgreSQL alone, with no tail, no scope
/// and no merge, and with the filter's values written in.
/// </summary>
/// <remarks>
/// <para>
/// What the blotter should show is every order at the highest version known — to the database, in
/// <c>order_versions</c>, or sent on the stream up to how far the state has read it — and every fill the
/// database holds or the stream has sent. The oracle takes exactly those: the stream's rows go into a
/// temporary table, and the truth is <c>orders_current</c> with every newer version laid over it.
/// </para>
/// <para>
/// It is exact only while nothing moves: the market paused, the host caught up with the streams, and
/// the database neither writing nor catching up. Then a page of the blotter and the oracle's must be
/// the same rows. The checks stall the database to hold it still, which also leaves the tail with
/// something to cover.
/// </para>
/// </remarks>
public static class Oracle
{
    /// <summary>
    /// The first <paramref name="count"/> rows the state should show from <paramref name="from"/> on, or
    /// from the first, in <paramref name="order"/>; and how many rows it should have in all.
    /// </summary>
    public static (IRow[] Rows, long Total) From(
        Seeder seeded, Exchange market, ScopeState state, SortSpec order, IRow? from, int count)
    {
        var tab = state.Tab;

        // The truth is worked out in the database itself, in its own SQL: PostgreSQL's, here.
        var repository = seeded.Repository as PostgresTradeRepository
            ?? throw new NotSupportedException($"the oracle works out pages in PostgreSQL, not in {seeded.Repository.GetType().Name}");
        using var connection = repository.Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, tab.Overrides
            ? "CREATE TEMP TABLE sent (LIKE blotter.order_versions) ON COMMIT DROP"
            : "CREATE TEMP TABLE sent (LIKE blotter.fills) ON COMMIT DROP");
        using (var import = connection.BeginBinaryImport("COPY sent FROM STDIN (FORMAT BINARY)"))
        {
            if (tab.Overrides)
            {
                foreach (var order1 in market.Orders.Read(state.Mark.OrdersRead))
                {
                    PostgresTradeRepository.WriteOrder(import, order1);
                }
            }
            else
            {
                foreach (var fill in market.Fills.Read(state.Mark.FillsRead))
                {
                    PostgresTradeRepository.WriteFill(import, fill);
                }
            }

            import.Complete();
        }

        // Everything since the corpus was loaded came from the exchange: the versions written since
        // then, and those sent, are all that can be newer than orders_current.
        var since = string.Create(CultureInfo.InvariantCulture, $"TIMESTAMP '{seeded.LoadedThrough:yyyy-MM-dd HH:mm:ss.ffffff}'");
        var truth = tab.Overrides
            ? $"""
              WITH newer AS (
                  SELECT DISTINCT ON (order_id) * FROM (
                      SELECT * FROM sent
                      UNION ALL
                      SELECT * FROM blotter.order_versions WHERE event_time > {since}
                  ) known
                  ORDER BY order_id, version DESC
              )
              SELECT c.* FROM blotter.orders_current c
              WHERE NOT EXISTS (SELECT 1 FROM newer n WHERE n.order_id = c.order_id AND n.version > c.version)
              UNION ALL
              SELECT n.* FROM newer n
              WHERE NOT EXISTS (SELECT 1 FROM blotter.orders_current c WHERE c.order_id = n.order_id AND c.version >= n.version)
              """
            : """
              SELECT * FROM blotter.fills
              UNION ALL
              SELECT s.* FROM sent s WHERE NOT EXISTS (SELECT 1 FROM blotter.fills f WHERE f.fill_id = s.fill_id)
              """;
        var where = PostgresSql.Where(state.Filter);

        long total;
        using (var counting = new NpgsqlCommand($"SELECT count(*) FROM ({truth}) truth WHERE {where}", connection, transaction))
        {
            counting.CommandTimeout = 0;
            total = Convert.ToInt64(counting.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using var page = new NpgsqlCommand { Connection = connection, Transaction = transaction, CommandTimeout = 0 };
        var values = new Dictionary<string, object?>();
        var seek = from is null ? "" : $" AND ({SourceQueries.Seek(order, from, inclusive: true, values)})";
        foreach (var (name, value) in values)
        {
            page.Parameters.AddWithValue(name, value!);
        }

        page.CommandText = string.Create(CultureInfo.InvariantCulture,
            $"SELECT {tab.Select} FROM ({truth}) truth WHERE {where}{seek} ORDER BY {order.OrderBy()} LIMIT {count}");
        var found = new List<IRow>();
        using (var reader = page.ExecuteReader())
        {
            while (reader.Read())
            {
                found.Add(tab.Overrides ? Order.Read(reader) : Fill.Read(reader));
            }
        }

        transaction.Commit();
        return ([.. found], total);
    }

    /// <summary>What differs between the blotter's rows and the oracle's, or null when nothing does.</summary>
    public static string? Compare(IRow[] rows, IRow[] expected)
    {
        var problems = new StringBuilder();
        if (rows.Length != expected.Length)
        {
            problems.Append(CultureInfo.InvariantCulture, $"{rows.Length} rows, expected {expected.Length}; ");
        }

        for (var i = 0; i < Math.Min(rows.Length, expected.Length); i++)
        {
            if (!Equals(rows[i], expected[i]))
            {
                problems.Append(CultureInfo.InvariantCulture, $"row {i}: {rows[i]}, expected {expected[i]}; ");
                break;
            }
        }

        return problems.Length == 0 ? null : problems.ToString();
    }

    /// <summary>
    /// What a page shown while things move must hold however the database and the streams interleave:
    /// its rows in order, no row twice, and every row passing the filter. Null when it holds.
    /// </summary>
    public static string? Invariants(Page page, SortSpec order, FilterSpec filter)
    {
        for (var i = 0; i < page.Rows.Length; i++)
        {
            if (!filter.Matches(page.Rows[i]))
            {
                return $"row {page.Start + i} ({page.Rows[i]}) does not pass {filter}";
            }

            if (i > 0 && order.Compare(page.Rows[i - 1], page.Rows[i]) >= 0)
            {
                return $"rows {page.Start + i - 1} and {page.Start + i} are out of order: {page.Rows[i - 1]}, then {page.Rows[i]}";
            }
        }

        return page.Rows.DistinctBy(r => r.Id).Count() == page.Rows.Length ? null : "a row appears twice";
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }
}
