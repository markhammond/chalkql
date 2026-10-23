using System.Diagnostics;
using System.Globalization;
using DuckDB.NET.Data;

namespace Chalk.Sample.Blotter.Market;

/// <summary>
/// The system of record: an append-only table of trade versions in DuckDB, standing in for a
/// database in another process. It is in memory and in this process only so the sample runs with
/// one command; nothing in the blotter reads it except through ChalkQL, or through SQL a remote
/// database would take as well.
/// </summary>
/// <remarks>
/// <para>
/// <c>trade_versions</c> holds every version of every trade, indexed by trade and version, which
/// serves a trade's history. <c>ingest_watermark</c> holds one time: every version whose event time
/// is at or before it is visible in <c>trade_versions</c>. It only moves forward, and it is all the
/// blotter needs to know about the database's lag.
/// </para>
/// <para>
/// <c>snapshots</c> is the blotter's own: the latest version of each trade as of a moment, one
/// generation at a time, so that a page sorts one row per trade rather than every version. A
/// generation is written once and read until a newer one replaces it.
/// </para>
/// </remarks>
public sealed class TradeStore : IDisposable
{
    private readonly DuckDBConnection _keepAlive;

    public TradeStore(long memoryLimitBytes)
    {
        ConnectionString = $"DataSource=:memory:blotter-{Guid.NewGuid():N}?cache=shared";
        _keepAlive = new DuckDBConnection(ConnectionString);
        _keepAlive.Open();
        Execute($"SET memory_limit = '{memoryLimitBytes / (1 << 20)}MB'");
        Execute("""
            CREATE TABLE trade_versions (
                trade_id   BIGINT    NOT NULL,
                version    INTEGER   NOT NULL,
                event_time TIMESTAMP NOT NULL,
                trade_time TIMESTAMP NOT NULL,
                client     VARCHAR   NOT NULL,
                symbol     VARCHAR   NOT NULL,
                side       VARCHAR   NOT NULL,
                order_qty  BIGINT    NOT NULL,
                filled_qty BIGINT    NOT NULL,
                avg_price  DOUBLE    NOT NULL,
                status     VARCHAR   NOT NULL,
                venue      VARCHAR   NOT NULL
            )
            """);
        Execute("CREATE TABLE ingest_watermark (complete_through TIMESTAMP NOT NULL)");
        Execute("""
            CREATE TABLE snapshots (
                generation BIGINT    NOT NULL,
                trade_id   BIGINT    NOT NULL,
                version    INTEGER   NOT NULL,
                event_time TIMESTAMP NOT NULL,
                trade_time TIMESTAMP NOT NULL,
                client     VARCHAR   NOT NULL,
                symbol     VARCHAR   NOT NULL,
                side       VARCHAR   NOT NULL,
                order_qty  BIGINT    NOT NULL,
                filled_qty BIGINT    NOT NULL,
                avg_price  DOUBLE    NOT NULL,
                status     VARCHAR   NOT NULL,
                venue      VARCHAR   NOT NULL
            )
            """);
    }

    public string ConnectionString { get; }

    public DuckDBConnection Open()
    {
        var connection = new DuckDBConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Fills <c>trade_versions</c> with a history of <paramref name="trades"/> trades, a few versions
    /// each, traded over the days before <paramref name="end"/>, in one statement. The values are
    /// hashes of the trade id, so a corpus of one size is the same corpus every time.
    /// </summary>
    /// <returns>The last trade id, and the last event time.</returns>
    public (long LastTradeId, DateTime LastEventTime) Generate(long trades, DateTime end, TimeSpan span, int openTail)
    {
        var started = Stopwatch.GetTimestamp();
        var spacing = Math.Max(1, (long)(span.TotalMicroseconds / Math.Max(1, trades)));
        var origin = TradeVersion.Micros(end - TimeSpan.FromMicroseconds(spacing * trades));
        var clients = Reference.Clients.Count;
        var symbols = Reference.Symbols.Count;
        var venues = Reference.Venues.Count;

        // The newest openTail trades are left open, at their first versions, so the exchange has
        // working orders to fill from its first second. Every other trade has run its course.
        Execute(string.Create(CultureInfo.InvariantCulture, $"""
            INSERT INTO trade_versions
            WITH t AS (
                SELECT range AS trade_id,
                       CASE WHEN range >= {trades - openTail} THEN 1 ELSE 1 + (hash(range) % 4)::INTEGER END AS versions,
                       range >= {trades - openTail} AS open,
                       hash(range, 9) % 50 = 0 AS cancelled,
                       (100 + hash(range, 4) % 99) * 100 AS qty,
                       10 + (hash(range, 5) % 100000) / 100.0 AS px
                FROM range({trades})
            )
            SELECT t.trade_id,
                   v.version,
                   TIMESTAMP '{origin:yyyy-MM-dd HH:mm:ss.ffffff}' + to_microseconds(t.trade_id * {spacing} + (v.version - 1) * 750000),
                   TIMESTAMP '{origin:yyyy-MM-dd HH:mm:ss.ffffff}' + to_microseconds(t.trade_id * {spacing}),
                   list_extract({Literal(Reference.Clients)}, 1 + (hash(t.trade_id, 1) % {clients})::INTEGER),
                   list_extract({Literal(Reference.Symbols)}, 1 + (hash(t.trade_id, 2) % {symbols})::INTEGER),
                   CASE WHEN hash(t.trade_id, 3) % 2 = 0 THEN 'Buy' ELSE 'Sell' END,
                   t.qty,
                   CASE WHEN t.open THEN 0
                        WHEN v.version = t.versions AND NOT t.cancelled THEN t.qty
                        ELSE (t.qty * (v.version - 1) // t.versions // 100) * 100 END,
                   CASE WHEN t.open OR (v.version = 1 AND t.versions > 1) THEN 0.0
                        ELSE round(t.px + v.version * 0.0125, 4) END,
                   CASE WHEN t.open OR v.version = 1 AND t.versions > 1 THEN 'New'
                        WHEN v.version < t.versions THEN 'PartiallyFilled'
                        WHEN t.cancelled THEN 'Cancelled'
                        ELSE 'Filled' END,
                   list_extract({Literal(Reference.Venues)}, 1 + (hash(t.trade_id, 6) % {venues})::INTEGER)
            FROM t, LATERAL (SELECT (range + 1)::INTEGER AS version FROM range(t.versions)) v
            ORDER BY t.trade_id, v.version
            """));
        Execute("CREATE INDEX trade_versions_by_trade ON trade_versions (trade_id, version)");

        using var connection = Open();
        using var last = connection.CreateCommand();
        last.CommandText = "SELECT max(trade_id), max(event_time) FROM trade_versions";
        using var reader = last.ExecuteReader();
        reader.Read();
        var lastTrade = reader.GetInt64(0);
        var lastEvent = TradeVersion.Micros(reader.GetDateTime(1));
        Execute(string.Create(CultureInfo.InvariantCulture,
            $"INSERT INTO ingest_watermark VALUES (TIMESTAMP '{lastEvent:yyyy-MM-dd HH:mm:ss.ffffff}')"));
        GenerationTime = Stopwatch.GetElapsedTime(started);
        return (lastTrade, lastEvent);
    }

    /// <summary>How long <see cref="Generate"/> took.</summary>
    public TimeSpan GenerationTime { get; private set; }

    /// <summary>The trades still open at the end of the corpus, latest version each, for the exchange to work.</summary>
    public List<TradeVersion> OpenTrades()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {TradeVersion.Select} FROM trade_versions
            QUALIFY row_number() OVER (PARTITION BY trade_id ORDER BY version DESC) = 1
                AND status IN ('New', 'PartiallyFilled')
            """;
        using var reader = command.ExecuteReader();
        var open = new List<TradeVersion>();
        while (reader.Read())
        {
            open.Add(Read(reader));
        }

        return open;
    }

    /// <summary>
    /// Writes versions the way an ingestion job would, in one transaction with the watermark it now
    /// holds: a reader that sees the watermark sees every version it promises.
    /// </summary>
    public void Write(IReadOnlyList<TradeVersion> versions, DateTime completeThrough)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (versions.Count > 0)
        {
            using var appender = connection.CreateAppender("trade_versions");
            foreach (var v in versions)
            {
                appender.CreateRow()
                    .AppendValue(v.TradeId)
                    .AppendValue(v.Version)
                    .AppendValue(v.EventTime)
                    .AppendValue(v.TradeTime)
                    .AppendValue(v.Client)
                    .AppendValue(v.Symbol)
                    .AppendValue(v.Side)
                    .AppendValue(v.OrderQty)
                    .AppendValue(v.FilledQty)
                    .AppendValue(v.AvgPrice)
                    .AppendValue(v.Status)
                    .AppendValue(v.Venue)
                    .EndRow();
            }
        }

        using var watermark = connection.CreateCommand();
        watermark.Transaction = transaction;
        watermark.CommandText = "UPDATE ingest_watermark SET complete_through = greatest(complete_through, $1)";
        watermark.Parameters.Add(new DuckDBParameter(completeThrough));
        watermark.ExecuteNonQuery();
        transaction.Commit();
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

    internal static TradeVersion Read(DuckDBDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt32(1),
        TradeVersion.Micros(reader.GetDateTime(2)),
        TradeVersion.Micros(reader.GetDateTime(3)),
        string.Intern(reader.GetString(4)),
        string.Intern(reader.GetString(5)),
        string.Intern(reader.GetString(6)),
        reader.GetInt64(7),
        reader.GetInt64(8),
        reader.GetDouble(9),
        string.Intern(reader.GetString(10)),
        string.Intern(reader.GetString(11)));

    private static string Literal(IReadOnlyList<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";
}
