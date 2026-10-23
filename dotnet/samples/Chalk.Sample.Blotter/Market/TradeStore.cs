using System.Diagnostics;
using System.Globalization;
using DuckDB.NET.Data;

namespace Chalk.Sample.Blotter.Market;

/// <summary>
/// The system of record: append-only tables of order versions and of fills in DuckDB, standing in
/// for a database in another process. It is in memory and in this process only so the sample runs
/// with one command; nothing in the blotter reads it except through ChalkQL, or through SQL a remote
/// database would take as well.
/// </summary>
/// <remarks>
/// <para>
/// <c>order_versions</c> holds every version of every client order, indexed by order and version,
/// which serves an order's history; <c>fills</c>, every fill, indexed by order, which serves an
/// order's fills. The two are ingested apart, each from its own stream, so the database can hold a
/// fill whose order it does not have yet. <c>ingest_watermark</c> holds a time for each stream: every
/// version, or every fill, whose event time is at or before it is visible. Each only moves forward,
/// and they are all the blotter needs to know about the database's lag.
/// </para>
/// <para>
/// <c>snapshots</c> is the blotter's own: the latest version of each order as of a moment, one
/// generation at a time, so that a page sorts one row per order rather than every version. Fills need
/// none: they never change, so the fills at or before a watermark are a snapshot already.
/// </para>
/// </remarks>
public sealed class TradeStore : IDisposable
{
    private const string OrderColumns = """
        order_id    UUID      NOT NULL,
        version     INTEGER   NOT NULL,
        event_time  TIMESTAMP NOT NULL,
        order_time  TIMESTAMP NOT NULL,
        cl_ord_id   VARCHAR   NOT NULL,
        client      VARCHAR   NOT NULL,
        region      VARCHAR   NOT NULL,
        channel     VARCHAR   NOT NULL,
        instrument  VARCHAR   NOT NULL,
        side        VARCHAR   NOT NULL,
        qty         DOUBLE    NOT NULL,
        filled_qty  DOUBLE    NOT NULL,
        avg_price   DOUBLE    NOT NULL,
        markup_bps  DOUBLE    NOT NULL,
        status      VARCHAR   NOT NULL
        """;

    private readonly DuckDBConnection _keepAlive;

    public TradeStore(long memoryLimitBytes)
    {
        ConnectionString = $"DataSource=:memory:blotter-{Guid.NewGuid():N}?cache=shared";
        _keepAlive = new DuckDBConnection(ConnectionString);
        _keepAlive.Open();
        Execute($"SET memory_limit = '{memoryLimitBytes / (1 << 20)}MB'");
        Execute($"CREATE TABLE order_versions ({OrderColumns})");
        Execute("""
            CREATE TABLE fills (
                fill_id     UUID      NOT NULL,
                order_id    UUID      NOT NULL,
                event_time  TIMESTAMP NOT NULL,
                cl_ord_id   VARCHAR   NOT NULL,
                client      VARCHAR   NOT NULL,
                region      VARCHAR   NOT NULL,
                channel     VARCHAR   NOT NULL,
                instrument  VARCHAR   NOT NULL,
                side        VARCHAR   NOT NULL,
                qty         DOUBLE    NOT NULL,
                price       DOUBLE    NOT NULL,
                mid         DOUBLE    NOT NULL,
                markup_bps  DOUBLE    NOT NULL
            )
            """);
        Execute("CREATE TABLE ingest_watermark (stream VARCHAR NOT NULL, complete_through TIMESTAMP NOT NULL)");
        Execute($"CREATE TABLE snapshots (generation BIGINT NOT NULL, {OrderColumns})");
    }

    public string ConnectionString { get; }

    /// <summary>How long <see cref="Generate"/> took.</summary>
    public TimeSpan GenerationTime { get; private set; }

    public DuckDBConnection Open()
    {
        var connection = new DuckDBConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Fills the tables with a history of <paramref name="orders"/> client orders, worked over the days
    /// before <paramref name="end"/>, in a few statements. The values are hashes of the order id, so a
    /// corpus of one size is the same corpus every time. Each order is filled in up to three fills that
    /// split its lots, at prices around a mid with the channel's markup on top; its average price is the
    /// fills', weighted; one in fifty is cancelled after some of its lots are filled. The newest
    /// <paramref name="openTail"/> are left new, for the exchange to work from its first second.
    /// </summary>
    public (DateTime OrdersThrough, DateTime FillsThrough) Generate(long orders, DateTime end, TimeSpan span, int openTail)
    {
        var started = Stopwatch.GetTimestamp();
        var spacing = Math.Max(1, (long)(span.TotalMicroseconds / Math.Max(1, orders)));
        var origin = Cells.Micros(end - TimeSpan.FromMicroseconds(spacing * orders));
        var clients = Reference.Clients;

        // A client by how much it trades (the first twelve times the twelfth), its channel by its
        // rank's mix, an asset by how popular it is: each a threshold on a hash.
        var clientByHash = Pick("u1", clients.Select((_, i) => (1.0 / (i + 1), $"{i + 1}")));
        var assetByHash = Pick("u3", Reference.Assets.Select((a, i) => (a.Weight, $"{i + 1}")));
        var channelByRank = "CASE " + string.Join(" ", Enumerable.Range(1, clients.Count).Select(rank =>
            $"WHEN client_rank = {rank} THEN {Pick("u2", Reference.ChannelsOf(rank).Select(c => (c.Weight, $"'{c.Name}'")))}")) + " END";

        // A version 7 UUID from a time and a number: the time's milliseconds, then bits hashed from
        // the number, so a corpus of one size has the same ids every time.
        Execute("""
            CREATE OR REPLACE MACRO uuid7_hex(t, x) AS
                lpad(printf('%x', epoch_ms(t)), 12, '0') || '7'
                || lpad(printf('%x', (hash(x, 71) % 4096)::BIGINT), 3, '0')
                || printf('%x', (8 + hash(x, 72) % 4)::BIGINT)
                || lpad(printf('%x', (hash(x, 73) % 1152921504606846976)::BIGINT), 15, '0')
            """);
        Execute(string.Create(CultureInfo.InvariantCulture, $"""
            CREATE TABLE corpus_orders AS
            WITH h AS (
                SELECT range AS n,
                       (hash(range, 1) % 1000000) / 1e6 AS u1,
                       (hash(range, 2) % 1000000) / 1e6 AS u2,
                       (hash(range, 3) % 1000000) / 1e6 AS u3,
                       hash(range, 4) AS h4,
                       range >= {orders - openTail} AS open
                FROM range({orders})
            ), c AS (
                SELECT *, {clientByHash} AS client_rank, {assetByHash} AS asset,
                       TIMESTAMP '{origin:yyyy-MM-dd HH:mm:ss.ffffff}' + to_microseconds(n * {spacing}) AS order_time
                FROM h
            )
            SELECT n, order_time,
                   list_extract({Literal(clients.Select(c => c.Ref))}, client_rank) AS client,
                   list_extract({Literal(clients.Select(c => c.Region))}, client_rank) AS region,
                   {channelByRank} AS channel,
                   list_extract({Literal(Reference.Instruments)}, asset) AS instrument,
                   list_extract({Literal(Reference.Assets.Select(a => a.Lot))}, asset) AS lot,
                   list_extract({Literal(Reference.Assets.Select(a => a.Price))}, asset)
                       * (1 + ((h4 % 2000)::DOUBLE - 1000) / 10000) AS px,
                   CASE WHEN h4 % 2 = 0 THEN 'Buy' ELSE 'Sell' END AS side,
                   (1 + (h4 // 7) % 6)::INTEGER AS lots,
                   CASE WHEN open THEN 0 ELSE 1 + ((h4 // 11) % 3)::INTEGER END AS fills,
                   NOT open AND (h4 // 13) % 50 = 0 AS cancelled
            FROM c
            """));

        // The ids over the table as written: a function of a stored column, worked out once a row.
        Execute("CREATE TABLE corpus AS SELECT CAST(uuid7_hex(order_time, n) AS UUID) AS order_id, * FROM corpus_orders");
        Execute("DROP TABLE corpus_orders");
        Execute(string.Create(CultureInfo.InvariantCulture, $"""
            CREATE TABLE corpus_fills AS
            WITH k AS (
                SELECT *, unnest(range(1, fills + 1))::INTEGER AS k FROM corpus
            )
            SELECT n, order_id, k,
                   order_time + to_microseconds(k * 750000) AS event_time,
                   client, region, channel, instrument, side,
                   -- The lots each fill takes; a cancelled order is cancelled with some of its lots unfilled.
                   (floor(lots * CASE WHEN cancelled THEN 0.6 ELSE 1 END * k / fills)
                    - floor(lots * CASE WHEN cancelled THEN 0.6 ELSE 1 END * (k - 1) / fills)) * lot AS qty,
                   round(px * (1 + ((hash(n, k) % 200)::DOUBLE - 100) / 100000), 6) AS mid
            FROM k
            """));
        Execute(string.Create(CultureInfo.InvariantCulture, $"""
            INSERT INTO fills
            SELECT CAST(hx AS UUID), order_id, event_time,
                   client || '-' || n, client, region, channel, instrument, side, qty,
                   round(mid * (1 + CASE WHEN side = 'Buy' THEN 1 ELSE -1 END * {MarkupCase("channel")} / 10000), 4),
                   mid, {MarkupCase("channel")}
            FROM (SELECT *, uuid7_hex(event_time, n * 4 + k) AS hx FROM corpus_fills WHERE qty > 0)
            ORDER BY event_time, n
            """));
        Execute(string.Create(CultureInfo.InvariantCulture, $"""
            INSERT INTO order_versions
            WITH fills_so_far AS (
                SELECT f.order_id, f.event_time,
                       row_number() OVER w + 1 AS version,
                       sum(f.qty) OVER w AS filled,
                       sum(f.qty * f.price) OVER w / sum(f.qty) OVER w AS avg_price
                FROM fills f
                WINDOW w AS (PARTITION BY f.order_id ORDER BY f.event_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)
            ), versions AS (
                SELECT o.order_id, 1 AS version, o.order_time AS event_time, 0.0 AS filled, 0.0 AS avg_price, 'New' AS status
                FROM corpus o
                UNION ALL
                SELECT s.order_id, s.version, s.event_time, s.filled, round(s.avg_price, 4),
                       CASE WHEN s.filled >= o.lots * o.lot - 1e-9 THEN 'Filled' ELSE 'Partial' END
                FROM fills_so_far s JOIN corpus o USING (order_id)
                UNION ALL
                SELECT o.order_id, coalesce(max(s.version), 1) + 1, o.order_time + to_microseconds((o.fills + 1) * 750000),
                       coalesce(max(s.filled), 0), coalesce(round(arg_max(s.avg_price, s.version), 4), 0), 'Cancelled'
                FROM corpus o LEFT JOIN fills_so_far s USING (order_id)
                WHERE o.cancelled
                GROUP BY o.order_id, o.order_time, o.fills
            )
            SELECT v.order_id, v.version::INTEGER, v.event_time, o.order_time, o.client || '-' || o.n, o.client,
                   o.region, o.channel, o.instrument, o.side, o.lots * o.lot, v.filled, v.avg_price,
                   {MarkupCase("o.channel")}, v.status
            FROM versions v JOIN corpus o USING (order_id)
            ORDER BY o.n, v.version
            """));
        Execute("DROP TABLE corpus_fills");
        Execute("DROP TABLE corpus");
        Execute("DROP MACRO uuid7_hex");
        Execute("CREATE INDEX order_versions_by_order ON order_versions (order_id, version)");
        Execute("CREATE INDEX fills_by_order ON fills (order_id)");

        using var connection = Open();
        using var last = connection.CreateCommand();
        last.CommandText = "SELECT (SELECT max(event_time) FROM order_versions), (SELECT max(event_time) FROM fills)";
        using var reader = last.ExecuteReader();
        reader.Read();
        var ordersThrough = Cells.Micros(reader.GetDateTime(0));
        var fillsThrough = Cells.Micros(reader.GetDateTime(1));
        Execute(string.Create(CultureInfo.InvariantCulture, $"""
            INSERT INTO ingest_watermark VALUES
                ('orders', TIMESTAMP '{ordersThrough:yyyy-MM-dd HH:mm:ss.ffffff}'),
                ('fills', TIMESTAMP '{fillsThrough:yyyy-MM-dd HH:mm:ss.ffffff}')
            """));
        GenerationTime = Stopwatch.GetElapsedTime(started);
        return (ordersThrough, fillsThrough);
    }

    /// <summary>The orders still open at the end of the corpus, latest version each, for the exchange to work.</summary>
    public List<Order> OpenOrders()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Order.Select} FROM order_versions
            QUALIFY row_number() OVER (PARTITION BY order_id ORDER BY version DESC) = 1
                AND status IN ('New', 'Partial')
            """;
        using var reader = command.ExecuteReader();
        var open = new List<Order>();
        while (reader.Read())
        {
            open.Add(Order.Read(reader));
        }

        return open;
    }

    /// <summary>
    /// Writes order versions the way an ingestion job would, in one transaction with the orders'
    /// watermark it now holds: a reader that sees the watermark sees every version it promises.
    /// </summary>
    public void WriteOrders(IReadOnlyList<Order> versions, DateTime completeThrough) =>
        Write("order_versions", "orders", versions.Select(o => o.Values()), completeThrough);

    /// <summary>The same for fills, and the fills' watermark.</summary>
    public void WriteFills(IReadOnlyList<Fill> fills, DateTime completeThrough) =>
        Write("fills", "fills", fills.Select(f => f.Values()), completeThrough);

    private void Write(string table, string stream, IEnumerable<object?[]> rows, DateTime completeThrough)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var appender = connection.CreateAppender(table))
        {
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
                        _ => throw new InvalidOperationException($"cannot append a {value?.GetType().Name}"),
                    };
                }

                row.EndRow();
            }
        }

        using var watermark = connection.CreateCommand();
        watermark.Transaction = transaction;
        watermark.CommandText = "UPDATE ingest_watermark SET complete_through = greatest(complete_through, $1) WHERE stream = $2";
        watermark.Parameters.Add(new DuckDBParameter(completeThrough));
        watermark.Parameters.Add(new DuckDBParameter(stream));
        watermark.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>The watermarks as the database holds them now: orders', then fills'.</summary>
    public (DateTime Orders, DateTime Fills) Watermarks(DuckDBConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT complete_through FROM ingest_watermark WHERE stream = 'orders'),
                   (SELECT complete_through FROM ingest_watermark WHERE stream = 'fills')
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return (Cells.Micros(reader.GetDateTime(0)), Cells.Micros(reader.GetDateTime(1)));
    }

    /// <summary>DuckDB's memory in use, in bytes, as it reports it.</summary>
    public long MemoryBytes()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT coalesce(sum(memory_usage_bytes), 0)::BIGINT FROM duckdb_memory()";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _keepAlive.Dispose();

    private static string MarkupCase(string channel) =>
        $"CASE {channel} " + string.Join(" ", Reference.Channels.Select(c => string.Create(
            CultureInfo.InvariantCulture, $"WHEN '{c}' THEN {Reference.MarkupBps(c)}"))) + " END";

    /// <summary>A pick by weight, as a CASE over thresholds of the uniform <paramref name="uniform"/>.</summary>
    private static string Pick(string uniform, IEnumerable<(double Weight, string Value)> choices)
    {
        var list = choices.ToList();
        var total = list.Sum(c => c.Weight);
        var below = 0.0;
        var cases = new List<string>();
        foreach (var (weight, value) in list.SkipLast(1))
        {
            below += weight / total;
            cases.Add(string.Create(CultureInfo.InvariantCulture, $"WHEN {uniform} < {below} THEN {value}"));
        }

        return cases.Count == 0 ? list[0].Value : $"CASE {string.Join(" ", cases)} ELSE {list[^1].Value} END";
    }

    private static string Literal(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    private static string Literal(IEnumerable<double> values) =>
        "[" + string.Join(", ", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";
}
