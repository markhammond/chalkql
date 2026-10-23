using System.Diagnostics;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Chalk.Sample.Blotter.Market;

/// <summary>How <c>orders_current</c> keeps up with <c>order_versions</c>.</summary>
public enum CurrentSync
{
    /// <summary>A trigger on <c>order_versions</c> upserts the order's row in the statement that writes the version.</summary>
    Trigger,

    /// <summary>A job of its own applies the versions some time after they are written, and stalls now and then.</summary>
    Lagging,
}

/// <summary>
/// The system of record, in PostgreSQL: every version of every client order (<c>order_versions</c>),
/// every fill (<c>fills</c>), and <c>orders_current</c>, one row per order at its latest version, which
/// the blotter reads live. The blotter is a bolt-on: it asks of these tables what any reader could —
/// rows found by key, sorted and filtered — and nothing about when <c>orders_current</c> catches up.
/// </summary>
/// <remarks>
/// <para>
/// The tables are <c>UNLOGGED</c>, and the corpus is written with binary <c>COPY</c> on several
/// connections at once before any index or trigger exists; then the indexes are built in parallel.
/// </para>
/// <para>
/// Text columns are collated <c>"C"</c>, byte by byte, whatever the database's own collation: the
/// host merges the database's rows with its own and must order text exactly as the database does.
/// </para>
/// </remarks>
public sealed class TradeStore(string connectionString)
{
    private const string OrderColumns = """
        order_id    uuid             NOT NULL,
        version     integer          NOT NULL,
        event_time  timestamp(6)     NOT NULL,
        order_time  timestamp(6)     NOT NULL,
        cl_ord_id   text COLLATE "C" NOT NULL,
        client      text COLLATE "C" NOT NULL,
        region      text COLLATE "C" NOT NULL,
        channel     text COLLATE "C" NOT NULL,
        instrument  text COLLATE "C" NOT NULL,
        side        text COLLATE "C" NOT NULL,
        qty         double precision NOT NULL,
        filled_qty  double precision NOT NULL,
        avg_price   double precision NOT NULL,
        markup_bps  double precision NOT NULL,
        status      text COLLATE "C" NOT NULL
        """;

    // A newer version replaces the row; an older one, arriving late, changes nothing.
    private const string Upsert = """
        ON CONFLICT (order_id) DO UPDATE SET
            version = EXCLUDED.version, event_time = EXCLUDED.event_time, filled_qty = EXCLUDED.filled_qty,
            avg_price = EXCLUDED.avg_price, status = EXCLUDED.status
        WHERE orders_current.version < EXCLUDED.version
        """;

    public string ConnectionString { get; } = connectionString;

    /// <summary>How long <see cref="GenerateAsync"/> took to write the corpus, and then to index it.</summary>
    public TimeSpan LoadTime { get; private set; }

    public TimeSpan IndexTime { get; private set; }

    /// <summary>What the corpus holds, for the planner's estimates.</summary>
    public long Orders { get; private set; }

    public long Versions { get; private set; }

    public long Fills { get; private set; }

    /// <summary>The latest event in the corpus: everything after it, the exchange wrote.</summary>
    public DateTime LoadedThrough { get; private set; }

    /// <summary>What <see cref="GenerateAsync"/> is doing now, for a console to show while it waits.</summary>
    public string Stage { get; private set; } = "";

    /// <summary>Orders <see cref="GenerateAsync"/> has written so far.</summary>
    public long Written => Interlocked.Read(ref _written);

    private long _written;

    public NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Creates the tables and fills them with <paramref name="orders"/> client orders worked over the
    /// <paramref name="span"/> before <paramref name="end"/>, the newest <paramref name="openTail"/> left
    /// new; indexes them; and from then on keeps <c>orders_current</c> as <paramref name="sync"/> says.
    /// </summary>
    public async Task GenerateAsync(
        long orders, DateTime end, TimeSpan span, int openTail, CurrentSync sync, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        Stage = "creating the tables";
        Execute($"""
            CREATE UNLOGGED TABLE order_versions ({OrderColumns});
            CREATE UNLOGGED TABLE orders_current ({OrderColumns});
            CREATE UNLOGGED TABLE fills (
                fill_id     uuid             NOT NULL,
                order_id    uuid             NOT NULL,
                event_time  timestamp(6)     NOT NULL,
                cl_ord_id   text COLLATE "C" NOT NULL,
                client      text COLLATE "C" NOT NULL,
                region      text COLLATE "C" NOT NULL,
                channel     text COLLATE "C" NOT NULL,
                instrument  text COLLATE "C" NOT NULL,
                side        text COLLATE "C" NOT NULL,
                qty         double precision NOT NULL,
                price       double precision NOT NULL,
                mid         double precision NOT NULL,
                markup_bps  double precision NOT NULL
            );
            """);

        var spacing = Math.Max(1, (long)(span.TotalMicroseconds / Math.Max(1, orders)));
        var origin = Cells.Micros(end - TimeSpan.FromMicroseconds(spacing * orders));
        var parts = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        Stage = string.Create(CultureInfo.InvariantCulture, $"writing {orders:N0} orders, their versions and fills with COPY");
        var size = (orders + parts - 1) / parts;
        var counted = await Task.WhenAll(Enumerable.Range(0, parts).Select(p => Task.Run(() =>
        {
            // Each part on three connections, one per table, so the three COPYs stream side by side.
            using var versions = Open();
            using var current = Open();
            using var fills = Open();
            using var versionsIn = versions.BeginBinaryImport("COPY order_versions FROM STDIN (FORMAT BINARY)");
            using var currentIn = current.BeginBinaryImport("COPY orders_current FROM STDIN (FORMAT BINARY)");
            using var fillsIn = fills.BeginBinaryImport("COPY fills FROM STDIN (FORMAT BINARY)");
            long versionCount = 0, fillCount = 0;
            var latest = DateTime.MinValue;
            for (var n = p * size; n < Math.Min(orders, (p + 1) * size); n++)
            {
                ct.ThrowIfCancellationRequested();
                var (made, filled) = Corpus.Make(n, origin, spacing, open: n >= orders - openTail);
                foreach (var version in made)
                {
                    WriteOrder(versionsIn, version);
                }

                WriteOrder(currentIn, made[^1]);
                foreach (var fill in filled)
                {
                    WriteFill(fillsIn, fill);
                }

                versionCount += made.Length;
                fillCount += filled.Length;
                if ((n & 4095) == 4095)
                {
                    Interlocked.Add(ref _written, 4096);
                }

                latest = made[^1].EventTime > latest ? made[^1].EventTime : latest;
            }

            versionsIn.Complete();
            currentIn.Complete();
            fillsIn.Complete();
            return (versionCount, fillCount, latest);
        }, ct))).ConfigureAwait(false);
        Orders = orders;
        Versions = counted.Sum(c => c.versionCount);
        Fills = counted.Sum(c => c.fillCount);
        LoadedThrough = counted.Max(c => c.latest);
        LoadTime = Stopwatch.GetElapsedTime(started);

        // Each index on a connection of its own; PostgreSQL gives each build workers of its own too.
        Interlocked.Exchange(ref _written, orders);
        Stage = "building seven indexes";
        started = Stopwatch.GetTimestamp();
        string[] indexes =
        [
            "ALTER TABLE orders_current ADD PRIMARY KEY (order_id)",
            "CREATE INDEX orders_current_newest ON orders_current (order_time DESC, order_id DESC)",
            "ALTER TABLE order_versions ADD PRIMARY KEY (order_id, version)",
            "CREATE INDEX order_versions_recent ON order_versions (event_time)",
            "ALTER TABLE fills ADD PRIMARY KEY (fill_id)",
            "CREATE INDEX fills_by_order ON fills (order_id)",
            "CREATE INDEX fills_newest ON fills (event_time DESC, fill_id DESC)",
        ];
        await Task.WhenAll(indexes.Select(sql => Task.Run(() => Execute(sql), ct))).ConfigureAwait(false);
        Stage = "analyzing the tables";
        Execute("ANALYZE order_versions; ANALYZE orders_current; ANALYZE fills");
        IndexTime = Stopwatch.GetElapsedTime(started);

        if (sync == CurrentSync.Trigger)
        {
            Execute($"""
                CREATE FUNCTION keep_current() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    INSERT INTO orders_current VALUES (NEW.*)
                    {Upsert};
                    RETURN NULL;
                END $$;
                CREATE TRIGGER order_versions_keep_current AFTER INSERT ON order_versions
                    FOR EACH ROW EXECUTE FUNCTION keep_current();
                """);
        }
    }

    /// <summary>The orders still open, latest version each, for the exchange to work.</summary>
    public List<Order> OpenOrders()
    {
        using var connection = Open();
        using var command = new NpgsqlCommand(
            $"SELECT {Order.Select} FROM orders_current WHERE status IN ('New', 'Partial')", connection);
        using var reader = command.ExecuteReader();
        var open = new List<Order>();
        while (reader.Read())
        {
            open.Add(Order.Read(reader));
        }

        return open;
    }

    /// <summary>Writes order versions as an ingestion job would; under the trigger, <c>orders_current</c> moves with them.</summary>
    public void WriteOrders(IReadOnlyList<Order> versions) =>
        Copy("order_versions", versions, WriteOrder);

    public void WriteFills(IReadOnlyList<Fill> fills) =>
        Copy("fills", fills, WriteFill);

    /// <summary>What the lagging job does with a batch: each order's row moves to the newest version in it, never back.</summary>
    public void UpsertCurrent(IReadOnlyList<Order> versions)
    {
        if (versions.Count == 0)
        {
            return;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var stage = new NpgsqlCommand(
            $"CREATE TEMP TABLE staged (LIKE orders_current INCLUDING ALL) ON COMMIT DROP", connection, transaction))
        {
            stage.ExecuteNonQuery();
        }

        using (var import = connection.BeginBinaryImport("COPY staged FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var version in versions.GroupBy(v => v.OrderId).Select(g => g.MaxBy(v => v.Version)!))
            {
                WriteOrder(import, version);
            }

            import.Complete();
        }

        using (var upsert = new NpgsqlCommand($"INSERT INTO orders_current SELECT * FROM staged {Upsert}", connection, transaction))
        {
            upsert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>The database's size on disk, as PostgreSQL reports it.</summary>
    public long SizeBytes()
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("SELECT pg_database_size(current_database())", connection);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void Execute(string sql)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        command.ExecuteNonQuery();
    }

    private void Copy<T>(string table, IReadOnlyList<T> rows, Action<NpgsqlBinaryImporter, T> write)
    {
        if (rows.Count == 0)
        {
            return;
        }

        using var connection = Open();
        using var import = connection.BeginBinaryImport($"COPY {table} FROM STDIN (FORMAT BINARY)");
        foreach (var row in rows)
        {
            write(import, row);
        }

        import.Complete();
    }

    internal static void WriteOrder(NpgsqlBinaryImporter import, Order o)
    {
        import.StartRow();
        import.Write(o.OrderId, NpgsqlDbType.Uuid);
        import.Write(o.Version, NpgsqlDbType.Integer);
        import.Write(o.EventTime, NpgsqlDbType.Timestamp);
        import.Write(o.OrderTime, NpgsqlDbType.Timestamp);
        import.Write(o.ClOrdId, NpgsqlDbType.Text);
        import.Write(o.Client, NpgsqlDbType.Text);
        import.Write(o.Region, NpgsqlDbType.Text);
        import.Write(o.Channel, NpgsqlDbType.Text);
        import.Write(o.Instrument, NpgsqlDbType.Text);
        import.Write(o.Side, NpgsqlDbType.Text);
        import.Write(o.Qty, NpgsqlDbType.Double);
        import.Write(o.FilledQty, NpgsqlDbType.Double);
        import.Write(o.AvgPrice, NpgsqlDbType.Double);
        import.Write(o.MarkupBps, NpgsqlDbType.Double);
        import.Write(o.Status, NpgsqlDbType.Text);
    }

    internal static void WriteFill(NpgsqlBinaryImporter import, Fill f)
    {
        import.StartRow();
        import.Write(f.FillId, NpgsqlDbType.Uuid);
        import.Write(f.OrderId, NpgsqlDbType.Uuid);
        import.Write(f.EventTime, NpgsqlDbType.Timestamp);
        import.Write(f.ClOrdId, NpgsqlDbType.Text);
        import.Write(f.Client, NpgsqlDbType.Text);
        import.Write(f.Region, NpgsqlDbType.Text);
        import.Write(f.Channel, NpgsqlDbType.Text);
        import.Write(f.Instrument, NpgsqlDbType.Text);
        import.Write(f.Side, NpgsqlDbType.Text);
        import.Write(f.Qty, NpgsqlDbType.Double);
        import.Write(f.Price, NpgsqlDbType.Double);
        import.Write(f.Mid, NpgsqlDbType.Double);
        import.Write(f.MarkupBps, NpgsqlDbType.Double);
    }
}
