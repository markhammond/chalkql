using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Chalk.Client;

namespace Chalk.Sample.Blotter.Live;

/// <summary>A statement that ran past the host's time limit, and was stopped.</summary>
public sealed class QueryTimeLimitException(string what, TimeSpan limit, Exception inner)
    : TimeoutException($"{what} took longer than the {limit.TotalSeconds:0.#} s time limit and was stopped", inner)
{
    public TimeSpan Limit { get; } = limit;
}

/// <summary>
/// What the planner expects a page of a tab to cost, read from its plan: the rows the database reads,
/// the rows it sorts, and whether the sort and the filter run in the database or in the host.
/// </summary>
public sealed record PageEstimate(double RowsRead, double RowsSorted, bool SortInDatabase, bool FilterInDatabase, string PlanText);

/// <summary>
/// The statements the blotter sends through ChalkQL to the database: a page of a generation in order,
/// a count, the rank of a key, the generation's rows for a list of orders, an order's fills and
/// history, and a column's values. Each is prepared once per shape and bound per call; the sort, the
/// filter and the paging are pushed down into the database's own SQL. Every one runs under the host's
/// time limit, so no statement can run away.
/// </summary>
public sealed partial class SnapshotQueries(ChalkEngine engine, TimeSpan timeLimit)
{
    /// <summary>The list sizes a lookup is padded to, so that a few plans serve every batch.</summary>
    private static readonly int[] LookupSizes = [16, 64, 256, 1024];

    private readonly ConcurrentDictionary<string, Lazy<Task<PreparedQuery>>> _prepared = new(StringComparer.Ordinal);
    private long _executed;
    private long _timedOut;

    /// <summary>How many statements have run, for the page's counters.</summary>
    public long Executed => Interlocked.Read(ref _executed);

    /// <summary>How many were stopped at the time limit.</summary>
    public long TimedOut => Interlocked.Read(ref _timedOut);

    public TimeSpan TimeLimit => timeLimit;

    public static string PageSql(Tab tab, FilterSpec filter, SortSpec order) => $"""
        SELECT {tab.Select}
        FROM {tab.Table}
        WHERE {tab.Bound} AND {filter.Where()}
        ORDER BY {order.OrderBy()}
        LIMIT @limit OFFSET @offset
        """;

    /// <summary>Rows <paramref name="offset"/> on of the generation, filtered, in order.</summary>
    public async Task<IRow[]> PageAsync(Generation generation, FilterSpec filter, SortSpec order, long offset, int limit, CancellationToken ct)
    {
        var tab = order.Tab;
        var values = Bound(tab, generation, filter.Parameters());
        values["limit"] = (long)limit;
        values["offset"] = offset;
        return await RowsAsync(tab, PageSql(tab, filter, order), values, "a page", ct).ConfigureAwait(false);
    }

    /// <summary>How many rows of the generation pass the filter.</summary>
    public async Task<long> CountAsync(Tab tab, Generation generation, FilterSpec filter, CancellationToken ct) =>
        await CountAsync(
            $"SELECT COUNT(*) AS n FROM {tab.Table} WHERE {tab.Bound} AND {filter.Where()}",
            Bound(tab, generation, filter.Parameters()),
            ct).ConfigureAwait(false);

    /// <summary>How many rows of the generation pass the filter and sort before <paramref name="key"/>.</summary>
    public async Task<long> CountBeforeAsync(Generation generation, FilterSpec filter, SortSpec order, IRow key, CancellationToken ct)
    {
        var tab = order.Tab;
        var values = Bound(tab, generation, filter.Parameters());
        var before = Before(order, key, values);
        return await CountAsync(
            $"SELECT COUNT(*) AS n FROM {tab.Table} WHERE {tab.Bound} AND {filter.Where()} AND ({before})",
            values,
            ct).ConfigureAwait(false);
    }

    /// <summary>The generation's rows for these orders, by order; an order it does not hold is absent.</summary>
    public async Task<Dictionary<Guid, Order>> LookupAsync(Generation generation, IReadOnlyList<Guid> orderIds, CancellationToken ct)
    {
        var found = new Dictionary<Guid, Order>(orderIds.Count);
        for (var start = 0; start < orderIds.Count; start += LookupSizes[^1])
        {
            var count = Math.Min(LookupSizes[^1], orderIds.Count - start);
            var size = LookupSizes.First(s => s >= count);

            // Padded by repeating the first id, which changes nothing an IN finds.
            var ids = new Guid[size];
            for (var i = 0; i < size; i++)
            {
                ids[i] = orderIds[start + (i < count ? i : 0)];
            }

            var rows = await RowsAsync(
                Tab.Orders,
                $"SELECT {Order.Select} FROM main.snapshots WHERE generation = @generation AND order_id IN @ids",
                new Dictionary<string, object?> { ["generation"] = generation.Number, ["ids"] = ids },
                "a lookup",
                ct).ConfigureAwait(false);
            foreach (Order row in rows)
            {
                found[row.OrderId] = row;
            }
        }

        return found;
    }

    /// <summary>An order's fills in the database at or before the generation's watermark, oldest first.</summary>
    public async Task<Fill[]> FillsOfAsync(Generation generation, Guid orderId, CancellationToken ct) =>
        [.. (await RowsAsync(
            Tab.Fills,
            $"SELECT {Fill.Select} FROM main.fills WHERE order_id = @order AND event_time <= @through ORDER BY event_time",
            new Dictionary<string, object?> { ["order"] = orderId, ["through"] = generation.FillsThrough },
            "an order's fills",
            ct).ConfigureAwait(false)).Cast<Fill>()];

    /// <summary>
    /// Every version of one order, newest first: those the database has, and those only the tail has
    /// yet, in one statement over both. A version in both is the same row, and the union keeps one.
    /// </summary>
    public async Task<Order[]> HistoryAsync(Guid orderId, CancellationToken ct) =>
        [.. (await RowsAsync(
            Tab.Orders,
            $"""
            SELECT {Order.Select} FROM main.order_versions WHERE order_id = @order
            UNION
            SELECT {Order.Select} FROM live.order_versions WHERE order_id = @order
            ORDER BY version DESC
            """,
            new Dictionary<string, object?> { ["order"] = orderId },
            "an order's history",
            ct).ConfigureAwait(false)).Cast<Order>()];

    /// <summary>
    /// Every fill of one order, newest first: those the database has and those only the tail has yet,
    /// in one statement over both. The tail's start after the generation's watermark; a fill in both is one row.
    /// </summary>
    public async Task<Fill[]> AllFillsOfAsync(Guid orderId, CancellationToken ct) =>
        [.. (await RowsAsync(
            Tab.Fills,
            $"""
            SELECT {Fill.Select} FROM main.fills WHERE order_id = @order
            UNION
            SELECT {Fill.Select} FROM live.tail_fills WHERE order_id = @order
            ORDER BY event_time DESC
            """,
            new Dictionary<string, object?> { ["order"] = orderId },
            "an order's fills",
            ct).ConfigureAwait(false)).Cast<Fill>()];

    /// <summary>The versions of one order the tail has and the database does not show yet.</summary>
    public async Task<HashSet<int>> TailOnlyAsync(Guid orderId, CancellationToken ct)
    {
        var rows = await RowsAsync(
            Tab.Orders,
            $"""
            SELECT {Order.Select} FROM live.order_versions WHERE order_id = @order
              AND version NOT IN (SELECT version FROM main.order_versions WHERE order_id = @order)
            """,
            new Dictionary<string, object?> { ["order"] = orderId },
            "an order's history",
            ct).ConfigureAwait(false);
        return [.. rows.Select(r => r.Version)];
    }

    /// <summary>
    /// A text column's values in the generation over a period, the most frequent first, each with how
    /// many rows hold it: the choices a filter offers. Bounded by the period and by
    /// <paramref name="limit"/>, so a column of a million values offers a page of them.
    /// </summary>
    public async Task<List<(string Value, long Rows)>> ValuesAsync(
        Generation generation, Tab tab, Column column, DateTime from, DateTime to, string? startsWith, int limit, CancellationToken ct)
    {
        var values = Bound(tab, generation, []);
        values["from"] = from;
        values["to"] = to;
        values["limit"] = (long)limit;
        var prefix = "";
        if (!string.IsNullOrEmpty(startsWith))
        {
            values["prefix"] = FilterSpec.Of([new ColumnFilter { Column = column, Values = [startsWith], Match = TextMatch.StartsWith }])
                .Parameters()[$"{column.Name}_p0"];
            prefix = $" AND {column.Name} LIKE @prefix ESCAPE '\\'";
        }

        var sql = $"""
            SELECT {column.Name} AS v, COUNT(*) AS n FROM {tab.Table}
            WHERE {tab.Bound} AND {tab.Time.Name} >= @from AND {tab.Time.Name} < @to{prefix}
            GROUP BY {column.Name}
            ORDER BY n DESC, v
            LIMIT @limit
            """;
        var query = await PrepareAsync(sql, ct).ConfigureAwait(false);
        var found = new List<(string, long)>();
        await RunAsync($"the values of {column.Title}", async limited =>
        {
            await using var execution = await engine.ExecuteAsync(query, values, ct: limited).ConfigureAwait(false);
            await foreach (var batch in execution.Batches.WithCancellation(limited).ConfigureAwait(false))
            {
                using (batch)
                {
                    for (var row = 0; row < batch.Length; row++)
                    {
                        found.Add((Cells.Text(batch.Column(0), row), Cells.Int64(batch.Column(1), row)));
                    }
                }
            }
        }, ct).ConfigureAwait(false);
        return found;
    }

    /// <summary>What the planner expects a page of <paramref name="order"/>'s tab under <paramref name="filter"/> to cost.</summary>
    public async Task<PageEstimate> EstimateAsync(FilterSpec filter, SortSpec order, CancellationToken ct)
    {
        var sql = PageSql(order.Tab, filter, order);
        var plan = _prepared.GetOrAdd("plan:" + sql, _ => new Lazy<Task<PreparedQuery>>(
            () => engine.PrepareAsync(sql, new PrepareOptions { IncludePlanText = true }, CancellationToken.None).AsTask()));
        var text = (await plan.Value.WaitAsync(ct).ConfigureAwait(false)).PlanText ?? "";
        return Estimate(text);
    }

    /// <summary>The plan of a page query, for the page's "how" panel.</summary>
    public async Task<string> PagePlanAsync(FilterSpec filter, SortSpec order, CancellationToken ct) =>
        (await EstimateAsync(filter, order, ct).ConfigureAwait(false)).PlanText;

    /// <summary>
    /// Reads the physical plan's estimates: the scan's rows are what the database reads; the rows
    /// under the sort, what is sorted; and where the sort and the filter run, by the operator that
    /// does them — a <c>Source…</c> one in the database, a <c>Chalk…</c> one in the host.
    /// </summary>
    internal static PageEstimate Estimate(string plan)
    {
        var physical = plan[(plan.IndexOf("-- physical", StringComparison.Ordinal) is var at and >= 0 ? at : 0)..]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        double Rows(string line) => RowCount().Match(line) is { Success: true } m
            ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            : double.NaN;

        var scan = physical.FirstOrDefault(l => l.TrimStart().StartsWith("SourceScan", StringComparison.Ordinal));
        var sortAt = Array.FindIndex(physical, l => l.TrimStart() is var t
            && (t.StartsWith("SourceSort", StringComparison.Ordinal) || t.StartsWith("ChalkTopN", StringComparison.Ordinal)
                || t.StartsWith("ChalkSort", StringComparison.Ordinal)));
        var sorted = sortAt >= 0 && sortAt + 1 < physical.Length ? Rows(physical[sortAt + 1]) : double.NaN;
        return new PageEstimate(
            scan is null ? double.NaN : Rows(scan),
            sorted,
            sortAt >= 0 && physical[sortAt].TrimStart().StartsWith("SourceSort", StringComparison.Ordinal),
            !physical.Any(l => l.TrimStart().StartsWith("ChalkFilter", StringComparison.Ordinal)),
            plan);
    }

    /// <summary>
    /// The condition "sorts before <paramref name="key"/>" under <paramref name="order"/>, written out
    /// column by column (<c>a &lt; @k0 OR (a = @k0 AND b &gt; @k1 …)</c>), with the key's values bound
    /// into <paramref name="values"/>.
    /// </summary>
    public static string Before(SortSpec order, IRow key, Dictionary<string, object?> values)
    {
        var text = new StringBuilder();
        var closing = 0;
        for (var i = 0; i < order.Keys.Length; i++)
        {
            var (column, descending) = order.Keys[i];
            var name = "k" + i;
            values[name] = column.Value(key);
            if (i > 0)
            {
                text.Append(" OR (").Append(order.Keys[i - 1].Column.Name).Append(" = @k").Append(i - 1).Append(" AND (");
                closing += 2;
            }

            text.Append(column.Name).Append(descending ? " > @" : " < @").Append(name);
        }

        return text.Append(')', closing).ToString();
    }

    private static Dictionary<string, object?> Bound(Tab tab, Generation generation, Dictionary<string, object?> values)
    {
        if (tab.Overrides)
        {
            values["generation"] = generation.Number;
        }
        else
        {
            values["through"] = generation.FillsThrough;
        }

        return values;
    }

    private async Task<IRow[]> RowsAsync(Tab tab, string sql, Dictionary<string, object?> values, string what, CancellationToken ct)
    {
        var query = await PrepareAsync(sql, ct).ConfigureAwait(false);
        var rows = new List<IRow>();
        await RunAsync(what, async limited =>
        {
            await using var execution = await engine.ExecuteAsync(query, values, ct: limited).ConfigureAwait(false);
            await foreach (var batch in execution.Batches.WithCancellation(limited).ConfigureAwait(false))
            {
                using (batch)
                {
                    for (var row = 0; row < batch.Length; row++)
                    {
                        rows.Add(tab.Read(batch, row));
                    }
                }
            }
        }, ct).ConfigureAwait(false);
        return [.. rows];
    }

    private async Task<long> CountAsync(string sql, Dictionary<string, object?> values, CancellationToken ct)
    {
        var query = await PrepareAsync(sql, ct).ConfigureAwait(false);
        long count = 0;
        await RunAsync("a count", async limited =>
        {
            await using var execution = await engine.ExecuteAsync(query, values, ct: limited).ConfigureAwait(false);
            await foreach (var batch in execution.Batches.WithCancellation(limited).ConfigureAwait(false))
            {
                using (batch)
                {
                    if (batch.Length > 0)
                    {
                        count = Cells.Int64(batch.Column(0), 0);
                    }
                }
            }
        }, ct).ConfigureAwait(false);
        return count;
    }

    /// <summary>Runs a statement under the time limit: past it, the statement is cancelled and the caller told why.</summary>
    private async Task RunAsync(string what, Func<CancellationToken, Task> run, CancellationToken ct)
    {
        Interlocked.Increment(ref _executed);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeLimit);
        try
        {
            await run(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancelled) when (!ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _timedOut);
            throw new QueryTimeLimitException(what, timeLimit, cancelled);
        }
    }

    private Task<PreparedQuery> PrepareAsync(string sql, CancellationToken ct)
    {
        var lazy = _prepared.GetOrAdd(sql, text => new Lazy<Task<PreparedQuery>>(
            () => engine.PrepareAsync(text, ct: CancellationToken.None).AsTask()));
        if (lazy.Value.IsFaulted)
        {
            _prepared.TryRemove(new KeyValuePair<string, Lazy<Task<PreparedQuery>>>(sql, lazy));
        }

        return lazy.Value.WaitAsync(ct);
    }

    [GeneratedRegex(@"rowcount = ([0-9.Ee+-]+)")]
    private static partial Regex RowCount();
}
