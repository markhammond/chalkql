using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace Chalk.Sample.Blotter.Live;

/// <summary>PostgreSQL's estimate of a page: the rows it expects to read and to sort, and its plan.</summary>
public sealed record PostgresEstimate(double RowsRead, double RowsSorted, string PlanText);

/// <summary>
/// Asks PostgreSQL what a page would cost, with <c>EXPLAIN</c> on the page's own SQL, its filter's values
/// written in. ChalkQL's plan prices a remote table by its row count alone; PostgreSQL knows its indexes
/// and its statistics, and so knows that a top-N read down an index stops after a few rows.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL reports what each node would return run to completion, not what a <c>LIMIT</c> above it
/// lets it read. So the rows read are worked out from the plan's shape. Under a sort, every row the
/// scans below it read is read: a sequential scan reads the table, an index scan the rows its
/// condition selects. With no sort, an index gives the order and the scan stops at the limit: it reads
/// about the limit's worth of rows that pass the filter, which, at the share of the table that passes,
/// is the limit times the table over the rows that pass.
/// </para>
/// <para>
/// It is an estimate for showing, and statements past the time limit are still stopped.
/// </para>
/// </remarks>
public sealed class PostgresExplain(string connectionString)
{
    private readonly ConcurrentDictionary<string, double> _tuples = new(StringComparer.Ordinal);

    public async Task<PostgresEstimate> EstimateAsync(Tab tab, FilterSpec filter, SortSpec order, int limit, CancellationToken ct)
    {
        var sql = string.Create(CultureInfo.InvariantCulture,
            $"SELECT {tab.Select} FROM {tab.Table} WHERE {filter.Literal()} ORDER BY {order.OrderBy()} LIMIT {limit}");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        string json;
        await using (var command = new NpgsqlCommand("EXPLAIN (FORMAT JSON) " + sql, connection))
        {
            json = (string)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        var text = new List<string>();
        await using (var command = new NpgsqlCommand("EXPLAIN " + sql, connection))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                text.Add(reader.GetString(0));
            }
        }

        using var plan = JsonDocument.Parse(json);
        var root = plan.RootElement[0].GetProperty("Plan");
        var sorted = Find(root, IsSort);
        double read;
        if (sorted is { } sort)
        {
            read = 0;
            foreach (var scan in Scans(sort))
            {
                read += await ScanReadsAsync(connection, scan, ct).ConfigureAwait(false);
            }
        }
        else
        {
            read = 0;
            foreach (var scan in Scans(root))
            {
                var table = await TuplesAsync(connection, scan, ct).ConfigureAwait(false);
                var passes = Math.Max(1, scan.GetProperty("Plan Rows").GetDouble());
                read += Math.Clamp(limit * table / passes, Math.Min(limit, table), table);
            }
        }

        return new PostgresEstimate(
            read, sorted is { } s ? s.GetProperty("Plan Rows").GetDouble() : 0, string.Join('\n', text));
    }

    private static bool IsSort(JsonElement node) =>
        node.GetProperty("Node Type").GetString() is "Sort" or "Incremental Sort";

    private static JsonElement? Find(JsonElement node, Func<JsonElement, bool> test)
    {
        if (test(node))
        {
            return node;
        }

        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                if (Find(child, test) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> Scans(JsonElement node)
    {
        if (node.TryGetProperty("Relation Name", out _))
        {
            yield return node;
            yield break;
        }

        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                foreach (var scan in Scans(child))
                {
                    yield return scan;
                }
            }
        }
    }

    /// <summary>What a scan reads run to completion: the table for a sequential scan or an index scanned whole; else what its condition selects.</summary>
    private async Task<double> ScanReadsAsync(NpgsqlConnection connection, JsonElement scan, CancellationToken ct)
    {
        var bounded = scan.TryGetProperty("Index Cond", out _) || scan.TryGetProperty("Recheck Cond", out _);
        return bounded
            ? scan.GetProperty("Plan Rows").GetDouble()
            : await TuplesAsync(connection, scan, ct).ConfigureAwait(false);
    }

    private async Task<double> TuplesAsync(NpgsqlConnection connection, JsonElement scan, CancellationToken ct)
    {
        var schema = scan.TryGetProperty("Schema", out var s) ? s.GetString() : "blotter";
        var name = $"{schema}.{scan.GetProperty("Relation Name").GetString()}";
        if (_tuples.TryGetValue(name, out var known))
        {
            return known;
        }

        await using var command = new NpgsqlCommand("SELECT reltuples FROM pg_class WHERE oid = @name::regclass", connection);
        command.Parameters.AddWithValue("name", name);
        var tuples = Convert.ToDouble(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        _tuples[name] = tuples;
        return tuples;
    }
}
