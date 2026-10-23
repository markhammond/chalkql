using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Chalk.Client;
using Chalk.Sources;

namespace Chalk.Sample.Blotter.Live;

/// <summary>A statement that ran past the host's time limit, and was stopped.</summary>
public sealed class QueryTimeLimitException(string what, TimeSpan limit, Exception inner)
    : TimeoutException($"{what} took longer than the {limit.TotalSeconds:0.#} s time limit and was stopped", inner)
{
    public TimeSpan Limit { get; } = limit;
}

/// <summary>
/// What a page of a tab is expected to cost: the rows the database reads and sorts, where the sort and
/// the filter run, and whose estimate that is — PostgreSQL's own, or ChalkQL's plan's.
/// </summary>
public sealed record PageEstimate(
    double RowsRead, double RowsSorted, bool SortInDatabase, bool FilterInDatabase, string EstimatedBy, string PlanText,
    double RowsReturned = double.NaN);

/// <summary>
/// The statements the blotter sends through ChalkQL to the database: rows from a key on, in order; the
/// row at an offset; counts; the current rows of a list of orders; the fills the database holds of a
/// list; an order's fills and history; a column's values; and the versions written lately. Each is
/// prepared once per shape and bound per call; the sort, the filter and the limit are pushed down into
/// the database's own SQL. Every one runs under the host's time limit, so no statement can run away.
/// </summary>
public sealed partial class SourceQueries(ChalkEngine engine, TimeSpan timeLimit, PostgresExplain? explain = null)
{
    /// <summary>The list sizes a lookup is padded to, so that a few plans serve every batch.</summary>
    private static readonly int[] LookupSizes = [16, 64, 256, 1024];

    private readonly ConcurrentDictionary<string, Lazy<Task<PreparedQuery>>> _prepared = new(StringComparer.Ordinal);
    private long _executed;
    private long _timedOut;
    private long _reads;

    /// <summary>How many statements have run, for the page's counters.</summary>
    public long Executed => Interlocked.Read(ref _executed);

    /// <summary>How many were stopped at the time limit.</summary>
    public long TimedOut => Interlocked.Read(ref _timedOut);

    /// <summary>How many reads of a tab's rows in order, the pages' own statements.</summary>
    public long Reads => Interlocked.Read(ref _reads);

    public TimeSpan TimeLimit => timeLimit;

    /// <summary>The limit of a first page: the rows in view, the grid's overscan and the merge's slack, rounded up.</summary>
    public const int FirstPage = 128;

    /// <summary>
    /// A read's limit as it is written into the SQL: the next power of two, 64 at least. Written in
    /// rather than bound, the planner knows how many rows a read returns; rounded, a few plans serve
    /// every read.
    /// </summary>
    public static int Rounded(int rows) => (int)Math.Max(64, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, rows)));

    /// <summary>A read of a tab, for the page's "how" panel: from a key on, as every page after the first is read.</summary>
    public static string ReadSql(Tab tab, FilterSpec filter, SortSpec order, bool fromKey, int limit = FirstPage) => string.Create(
        CultureInfo.InvariantCulture, $"""
        SELECT {tab.Select}
        FROM {tab.Table}
        WHERE {filter.Where()}{(fromKey ? $"\n  AND ({SeekText(order, inclusive: true)})" : "")}
        ORDER BY {order.OrderBy()}
        LIMIT {Rounded(limit)}
        """);

    /// <summary>
    /// The first rows of the database that pass the filter in order, from <paramref name="from"/> on (or
    /// after it), or from the first: <paramref name="limit"/> of them, <see cref="Rounded"/> up.
    /// </summary>
    public async Task<IRow[]> ReadAsync(
        FilterSpec filter, SortSpec order, IRow? from, bool inclusive, int limit, CancellationToken ct)
    {
        var tab = order.Tab;
        var values = filter.Parameters();
        var seek = from is null ? "" : $" AND ({Seek(order, from, inclusive, values)})";
        Interlocked.Increment(ref _reads);
        return await RowsAsync(
            tab,
            string.Create(CultureInfo.InvariantCulture,
                $"SELECT {tab.Select} FROM {tab.Table} WHERE {filter.Where()}{seek} ORDER BY {order.OrderBy()} LIMIT {Rounded(limit)}"),
            values,
            "a page",
            ct).ConfigureAwait(false);
    }

    /// <summary>The database's row at <paramref name="offset"/> of the filter's rows in order, or null past the end.</summary>
    public async Task<IRow?> AtOffsetAsync(FilterSpec filter, SortSpec order, long offset, CancellationToken ct)
    {
        var tab = order.Tab;
        var values = filter.Parameters();
        values["offset"] = offset;
        Interlocked.Increment(ref _reads);
        var rows = await RowsAsync(
            tab,
            $"SELECT {tab.Select} FROM {tab.Table} WHERE {filter.Where()} ORDER BY {order.OrderBy()} LIMIT 1 OFFSET @offset",
            values,
            "a jump",
            ct).ConfigureAwait(false);
        return rows.Length == 0 ? null : rows[0];
    }

    /// <summary>How many rows of the database pass the filter.</summary>
    public async Task<long> CountAsync(Tab tab, FilterSpec filter, CancellationToken ct) =>
        await CountAsync($"SELECT COUNT(*) AS n FROM {tab.Table} WHERE {filter.Where()}", filter.Parameters(), ct)
            .ConfigureAwait(false);

    /// <summary>How many rows of the database pass the filter and sort before <paramref name="key"/>.</summary>
    public async Task<long> CountBeforeAsync(FilterSpec filter, SortSpec order, IRow key, CancellationToken ct)
    {
        var values = filter.Parameters();
        var before = Before(order, key, values);
        return await CountAsync(
            $"SELECT COUNT(*) AS n FROM {order.Tab.Table} WHERE {filter.Where()} AND ({before})", values, ct).ConfigureAwait(false);
    }

    /// <summary><c>orders_current</c>'s rows for these orders, by order; an order it has no row for is absent.</summary>
    public async Task<Dictionary<Guid, Order>> CurrentAsync(IReadOnlyList<Guid> orderIds, CancellationToken ct)
    {
        var found = new Dictionary<Guid, Order>(orderIds.Count);
        foreach (Order row in await LookupAsync(
            Tab.Orders, $"SELECT {Order.Select} FROM blotter.orders_current WHERE order_id IN @ids", orderIds, ct).ConfigureAwait(false))
        {
            found[row.OrderId] = row;
        }

        return found;
    }

    /// <summary>Which of these fills the database holds.</summary>
    public async Task<HashSet<Guid>> FillsHeldAsync(IReadOnlyList<Guid> fillIds, CancellationToken ct) =>
        [.. (await LookupAsync(
            Tab.Fills, $"SELECT {Fill.Select} FROM blotter.fills WHERE fill_id IN @ids", fillIds, ct).ConfigureAwait(false))
            .Select(f => f.Id)];

    /// <summary>An order's fills in the database, oldest first.</summary>
    public async Task<Fill[]> FillsOfAsync(Guid orderId, CancellationToken ct) =>
        [.. (await RowsAsync(
            Tab.Fills,
            $"SELECT {Fill.Select} FROM blotter.fills WHERE order_id = @order ORDER BY event_time",
            new Dictionary<string, object?> { ["order"] = orderId },
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
            SELECT {Order.Select} FROM blotter.order_versions WHERE order_id = @order
            UNION
            SELECT {Order.Select} FROM live.order_versions WHERE order_id = @order
            ORDER BY version DESC
            """,
            new Dictionary<string, object?> { ["order"] = orderId },
            "an order's history",
            ct).ConfigureAwait(false)).Cast<Order>()];

    /// <summary>
    /// Every fill of one order, newest first: those the database has and those only the tail has yet,
    /// in one statement over both; a fill in both is one row.
    /// </summary>
    public async Task<Fill[]> AllFillsOfAsync(Guid orderId, CancellationToken ct) =>
        [.. (await RowsAsync(
            Tab.Fills,
            $"""
            SELECT {Fill.Select} FROM blotter.fills WHERE order_id = @order
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
              AND version NOT IN (SELECT version FROM blotter.order_versions WHERE order_id = @order)
            """,
            new Dictionary<string, object?> { ["order"] = orderId },
            "an order's history",
            ct).ConfigureAwait(false);
        return [.. rows.Select(r => r.Version)];
    }

    /// <summary>
    /// The gap fill: every order version <c>order_versions</c> holds with an event time at or after
    /// <paramref name="since"/>, oldest first, for the host to take as if the stream had sent it.
    /// </summary>
    public async Task<Order[]> VersionsSinceAsync(DateTime since, CancellationToken ct) =>
        [.. (await RowsAsync(
            Tab.Orders,
            $"SELECT {Order.Select} FROM blotter.order_versions WHERE event_time >= @since ORDER BY event_time",
            new Dictionary<string, object?> { ["since"] = since },
            "the gap fill",
            ct).ConfigureAwait(false)).Cast<Order>()];

    /// <summary>The newest order <c>orders_current</c> has: how far it has caught up with new orders.</summary>
    public async Task<Order?> NewestOrderAsync(CancellationToken ct) =>
        (await RowsAsync(
            Tab.Orders,
            $"SELECT {Order.Select} FROM blotter.orders_current ORDER BY order_time DESC, order_id DESC LIMIT 1",
            [],
            "the newest order",
            ct).ConfigureAwait(false)).Cast<Order>().FirstOrDefault();

    /// <summary>
    /// A text column's values over a period, the most frequent first, each with how many rows hold
    /// it: the choices a filter offers. Bounded by the period and by <paramref name="limit"/>, so a
    /// column of a million values offers a page of them.
    /// </summary>
    public async Task<List<(string Value, long Rows)>> ValuesAsync(
        Tab tab, Column column, DateTime from, DateTime to, string? startsWith, int limit, CancellationToken ct)
    {
        var values = new Dictionary<string, object?> { ["from"] = from, ["to"] = to, ["limit"] = (long)limit };
        var prefix = "";
        if (!string.IsNullOrEmpty(startsWith))
        {
            values["prefix"] = FilterSpec.Of([new ColumnFilter { Column = column, Values = [startsWith], Match = TextMatch.StartsWith }])
                .Parameters()[$"{column.Name}_p0"];
            prefix = $" AND {column.Name} LIKE @prefix ESCAPE '\\'";
        }

        var sql = $"""
            SELECT {column.Name} AS v, COUNT(*) AS n FROM {tab.Table}
            WHERE {tab.Time.Name} >= @from AND {tab.Time.Name} < @to{prefix}
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

    /// <summary>
    /// What the tab's first page under <paramref name="filter"/> and <paramref name="order"/> is expected
    /// to cost: where ChalkQL runs the sort and the filter, from its plan; and the rows read, from
    /// PostgreSQL's own <c>EXPLAIN</c> when the database is PostgreSQL, or else from ChalkQL's plan.
    /// </summary>
    public async Task<PageEstimate> EstimateAsync(FilterSpec filter, SortSpec order, CancellationToken ct, int limit = FirstPage)
    {
        var sql = ReadSql(order.Tab, filter, order, fromKey: false, limit);
        var plan = _prepared.GetOrAdd("plan:" + sql, _ => new Lazy<Task<PreparedQuery>>(
            () => engine.PrepareAsync(sql, new PrepareOptions { IncludePlanText = true }, CancellationToken.None).AsTask()));
        var text = (await plan.Value.WaitAsync(ct).ConfigureAwait(false)).PlanText ?? "";
        var estimate = Estimate(text);
        if (explain is null)
        {
            return estimate;
        }

        var postgres = await explain.EstimateAsync(order.Tab, filter, order, Rounded(limit), ct).ConfigureAwait(false);
        return estimate with
        {
            RowsRead = postgres.RowsRead,
            RowsSorted = postgres.RowsSorted,
            EstimatedBy = "PostgreSQL",
            PlanText = text + "\n-- PostgreSQL's plan of the same page, its filter's values written in\n" + postgres.PlanText,
        };
    }

    /// <summary>
    /// Reads ChalkQL's physical plan: the scan's rows are what the database reads; the rows under the
    /// sort, what is sorted; the rows that cross from the database into ChalkQL, what it returns; and
    /// where the sort and the filter run, by the operator that does them — a <c>Source…</c> one in the
    /// database, a <c>Chalk…</c> one in the host.
    /// </summary>
    internal static PageEstimate Estimate(string plan)
    {
        var physical = plan[(plan.IndexOf("-- physical", StringComparison.Ordinal) is var at and >= 0 ? at : 0)..]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        double Rows(string line) => RowCount().Match(line) is { Success: true } m
            ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            : double.NaN;

        var scan = physical.FirstOrDefault(l => l.TrimStart().StartsWith("SourceScan", StringComparison.Ordinal));
        var crossing = physical.FirstOrDefault(l => l.TrimStart().StartsWith("SourceToLocalConverter", StringComparison.Ordinal));
        var sortAt = Array.FindIndex(physical, l => l.TrimStart() is var t
            && (t.StartsWith("SourceSort", StringComparison.Ordinal) || t.StartsWith("ChalkTopN", StringComparison.Ordinal)
                || t.StartsWith("ChalkSort", StringComparison.Ordinal)));
        var sorted = sortAt >= 0 && sortAt + 1 < physical.Length ? Rows(physical[sortAt + 1]) : double.NaN;
        return new PageEstimate(
            scan is null ? double.NaN : Rows(scan),
            sorted,
            sortAt >= 0 && physical[sortAt].TrimStart().StartsWith("SourceSort", StringComparison.Ordinal),
            !physical.Any(l => l.TrimStart().StartsWith("ChalkFilter", StringComparison.Ordinal)),
            "ChalkQL's plan",
            plan,
            crossing is null ? double.NaN : Rows(crossing));
    }

    /// <summary>
    /// The condition "sorts after <paramref name="key"/>", or at it when <paramref name="inclusive"/>,
    /// under <paramref name="order"/>, with the key's values bound into <paramref name="values"/>.
    /// </summary>
    public static string Seek(SortSpec order, IRow key, bool inclusive, Dictionary<string, object?> values)
    {
        for (var i = 0; i < order.Keys.Length; i++)
        {
            values["k" + i] = order.Keys[i].Column.Value(key);
        }

        return SeekText(order, inclusive);
    }

    /// <summary>The condition "sorts before <paramref name="key"/>" under <paramref name="order"/>.</summary>
    public static string Before(SortSpec order, IRow key, Dictionary<string, object?> values) =>
        Seek(order.Reversed(), key, inclusive: false, values);

    /// <summary>
    /// Written out column by column (<c>a &gt; @k0 OR (a = @k0 AND b &lt; @k1 …)</c>), with the first
    /// column's bound repeated alone in front: a plain range on the leading column, which is what lets
    /// the database start an index on it at the key rather than at its first entry.
    /// </summary>
    private static string SeekText(SortSpec order, bool inclusive)
    {
        var text = new StringBuilder();
        var closing = 0;
        for (var i = 0; i < order.Keys.Length; i++)
        {
            var (column, descending) = order.Keys[i];
            if (i > 0)
            {
                text.Append(" OR (").Append(order.Keys[i - 1].Column.Name).Append(" = @k").Append(i - 1).Append(" AND (");
                closing += 2;
            }

            var last = i == order.Keys.Length - 1;
            text.Append(column.Name).Append(descending ? " <" : " >").Append(last && inclusive ? "=" : "").Append(" @k").Append(i);
        }

        var (first, firstDescending) = order.Keys[0];
        return $"{first.Name} {(firstDescending ? "<=" : ">=")} @k0 AND ({text.Append(')', closing)})";
    }

    private async Task<IRow[]> LookupAsync(Tab tab, string sql, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var found = new List<IRow>(ids.Count);
        for (var start = 0; start < ids.Count; start += LookupSizes[^1])
        {
            var count = Math.Min(LookupSizes[^1], ids.Count - start);
            var size = LookupSizes.First(s => s >= count);

            // Padded by repeating the first id, which changes nothing an IN finds.
            var batch = new Guid[size];
            for (var i = 0; i < size; i++)
            {
                batch[i] = ids[start + (i < count ? i : 0)];
            }

            found.AddRange(await RowsAsync(tab, sql, new Dictionary<string, object?> { ["ids"] = batch }, "a lookup", ct)
                .ConfigureAwait(false));
        }

        return [.. found];
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
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // The caller moved on, and the statement was stopped for it, however that reads.
            throw new OperationCanceledException(ct);
        }
        catch (Exception late) when (late is SourceTimeoutException || StoppedByDatabase(late))
        {
            // The database source keeps the same limit, and so does PostgreSQL: either may be the
            // one to stop the statement.
            Interlocked.Increment(ref _timedOut);
            throw new QueryTimeLimitException(what, timeLimit, late);
        }
    }

    /// <summary>Whether PostgreSQL stopped the statement at its <c>statement_timeout</c>, however the source wrapped it.</summary>
    private static bool StoppedByDatabase(Exception failure)
    {
        for (var at = failure; at is not null; at = at.InnerException)
        {
            if (at is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.QueryCanceled })
            {
                return true;
            }
        }

        return false;
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
