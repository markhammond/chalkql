using System.Collections.Concurrent;
using System.Text;
using Chalk.Client;

namespace Chalk.Sample.Blotter.Live;

/// <summary>
/// The statements the blotter sends through ChalkQL to the database: a page of a snapshot in
/// order, a count, the rank of a key, the snapshot's rows for a list of trades, and a trade's
/// history. Each is prepared once per shape and bound per call; the sort, the filter and the paging
/// are pushed down into the database's own SQL, so a page brings back a page's rows.
/// </summary>
public sealed class SnapshotQueries(ChalkEngine engine)
{
    /// <summary>The list sizes a lookup is padded to, so that a few plans serve every batch.</summary>
    private static readonly int[] LookupSizes = [16, 64, 256, 1024];

    private readonly ConcurrentDictionary<string, Lazy<Task<PreparedQuery>>> _prepared = new(StringComparer.Ordinal);

    /// <summary>How many statements have run, for the page's counters.</summary>
    public long Executed => Interlocked.Read(ref _executed);

    private long _executed;

    public static string PageSql(FilterSpec filter, SortSpec order) => $"""
        SELECT {TradeVersion.Select}
        FROM main.snapshots
        WHERE generation = @generation AND {filter.Where()}
        ORDER BY {order.OrderBy()}
        LIMIT @limit OFFSET @offset
        """;

    /// <summary>Rows <paramref name="offset"/> on of generation <paramref name="generation"/>, filtered, in order.</summary>
    public async Task<TradeVersion[]> PageAsync(
        long generation, FilterSpec filter, SortSpec order, long offset, int limit, CancellationToken ct)
    {
        var values = filter.Parameters();
        values["generation"] = generation;
        values["limit"] = (long)limit;
        values["offset"] = offset;
        return await RowsAsync(PageSql(filter, order), values, ct).ConfigureAwait(false);
    }

    /// <summary>How many rows of the generation pass the filter.</summary>
    public async Task<long> CountAsync(long generation, FilterSpec filter, CancellationToken ct)
    {
        var values = filter.Parameters();
        values["generation"] = generation;
        return await CountAsync(
            $"SELECT COUNT(*) AS n FROM main.snapshots WHERE generation = @generation AND {filter.Where()}",
            values,
            ct).ConfigureAwait(false);
    }

    /// <summary>How many rows of the generation pass the filter and sort before <paramref name="key"/>.</summary>
    public async Task<long> CountBeforeAsync(
        long generation, FilterSpec filter, SortSpec order, TradeVersion key, CancellationToken ct)
    {
        var values = filter.Parameters();
        values["generation"] = generation;
        var before = Before(order, key, values);
        return await CountAsync(
            $"SELECT COUNT(*) AS n FROM main.snapshots WHERE generation = @generation AND {filter.Where()} AND ({before})",
            values,
            ct).ConfigureAwait(false);
    }

    /// <summary>The generation's rows for these trades, by trade; a trade it does not hold is absent.</summary>
    public async Task<Dictionary<long, TradeVersion>> LookupAsync(
        long generation, IReadOnlyList<long> tradeIds, CancellationToken ct)
    {
        var found = new Dictionary<long, TradeVersion>(tradeIds.Count);
        for (var start = 0; start < tradeIds.Count; start += LookupSizes[^1])
        {
            var count = Math.Min(LookupSizes[^1], tradeIds.Count - start);
            var size = LookupSizes.First(s => s >= count);

            // Padded by repeating the first id, which changes nothing an IN finds.
            var ids = new long[size];
            for (var i = 0; i < size; i++)
            {
                ids[i] = tradeIds[start + (i < count ? i : 0)];
            }

            var rows = await RowsAsync(
                $"SELECT {TradeVersion.Select} FROM main.snapshots WHERE generation = @generation AND trade_id IN @ids",
                new Dictionary<string, object?> { ["generation"] = generation, ["ids"] = ids },
                ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                found[row.TradeId] = row;
            }
        }

        return found;
    }

    /// <summary>
    /// Every version of one trade, newest first: those the database has, and those only the tail has
    /// yet, in one statement over both. A version in both is the same row, and the union keeps one.
    /// </summary>
    public async Task<TradeVersion[]> HistoryAsync(long tradeId, CancellationToken ct) =>
        await RowsAsync(
            $"""
            SELECT {TradeVersion.Select} FROM main.trade_versions WHERE trade_id = @trade
            UNION
            SELECT {TradeVersion.Select} FROM live.tail_versions WHERE trade_id = @trade
            ORDER BY version DESC
            """,
            new Dictionary<string, object?> { ["trade"] = tradeId },
            ct).ConfigureAwait(false);

    /// <summary>The versions of one trade the tail has and the database does not show yet.</summary>
    public async Task<HashSet<int>> TailOnlyAsync(long tradeId, CancellationToken ct)
    {
        var rows = await RowsAsync(
            $"""
            SELECT {TradeVersion.Select} FROM live.tail_versions WHERE trade_id = @trade
              AND version NOT IN (SELECT version FROM main.trade_versions WHERE trade_id = @trade)
            """,
            new Dictionary<string, object?> { ["trade"] = tradeId },
            ct).ConfigureAwait(false);
        return [.. rows.Select(r => r.Version)];
    }

    /// <summary>The plan of a page query, for the page's "how" panel.</summary>
    public async Task<string> PagePlanAsync(FilterSpec filter, SortSpec order, CancellationToken ct) =>
        (await engine.PrepareAsync(PageSql(filter, order), new PrepareOptions { IncludePlanText = true }, ct)
            .ConfigureAwait(false)).PlanText ?? "";

    /// <summary>
    /// The condition "sorts before <paramref name="key"/>" under <paramref name="order"/>, written
    /// out column by column (<c>a &lt; @k0 OR (a = @k0 AND b &gt; @k1 …)</c>), with the key's values
    /// bound into <paramref name="values"/>.
    /// </summary>
    public static string Before(SortSpec order, TradeVersion key, Dictionary<string, object?> values)
    {
        var text = new StringBuilder();
        var closing = 0;
        for (var i = 0; i < order.Keys.Length; i++)
        {
            var (column, descending) = order.Keys[i];
            var name = "k" + i;
            values[name] = Value(column, key);
            if (i > 0)
            {
                text.Append(" OR (").Append(order.Keys[i - 1].Column.Name).Append(" = @k").Append(i - 1).Append(" AND (");
                closing += 2;
            }

            text.Append(column.Name).Append(descending ? " > @" : " < @").Append(name);
        }

        return text.Append(')', closing).ToString();
    }

    private static object Value(Column column, TradeVersion key) => column.Name switch
    {
        "trade_id" => key.TradeId,
        "version" => key.Version,
        "event_time" => key.EventTime,
        "trade_time" => key.TradeTime,
        "client" => key.Client,
        "symbol" => key.Symbol,
        "side" => key.Side,
        "order_qty" => key.OrderQty,
        "filled_qty" => key.FilledQty,
        "avg_price" => key.AvgPrice,
        "status" => key.Status,
        "venue" => key.Venue,
        _ => throw new ArgumentException($"no value for column '{column.Name}'", nameof(column)),
    };

    private async Task<TradeVersion[]> RowsAsync(string sql, Dictionary<string, object?> values, CancellationToken ct)
    {
        var query = await PrepareAsync(sql, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _executed);
        await using var execution = await engine.ExecuteAsync(query, values, ct: ct).ConfigureAwait(false);
        var rows = new List<TradeVersion>();
        await foreach (var batch in execution.Batches.WithCancellation(ct).ConfigureAwait(false))
        {
            using (batch)
            {
                for (var row = 0; row < batch.Length; row++)
                {
                    rows.Add(TradeVersion.Read(batch, row));
                }
            }
        }

        return [.. rows];
    }

    private async Task<long> CountAsync(string sql, Dictionary<string, object?> values, CancellationToken ct)
    {
        var query = await PrepareAsync(sql, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _executed);
        await using var execution = await engine.ExecuteAsync(query, values, ct: ct).ConfigureAwait(false);
        long count = 0;
        await foreach (var batch in execution.Batches.WithCancellation(ct).ConfigureAwait(false))
        {
            using (batch)
            {
                if (batch.Length > 0)
                {
                    count = TradeVersion.Int64(batch.Column(0), 0);
                }
            }
        }

        return count;
    }

    private Task<PreparedQuery> PrepareAsync(string sql, CancellationToken ct) =>
        _prepared.GetOrAdd(sql, text => new Lazy<Task<PreparedQuery>>(
            () => engine.PrepareAsync(text, ct: CancellationToken.None).AsTask())).Value.WaitAsync(ct);
}
