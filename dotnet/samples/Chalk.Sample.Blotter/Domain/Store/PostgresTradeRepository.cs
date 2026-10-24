using Npgsql;
using NpgsqlTypes;

namespace Chalk.Sample.Blotter.Domain;

/// <summary>
/// The system of record in PostgreSQL, as the simulated house writes it (see <see cref="ITradeRepository"/>):
/// <c>UNLOGGED</c> tables, text collated <c>"C"</c>, loaded with binary <c>COPY</c>, and
/// <c>orders_current</c> kept by a trigger or by a job's upserts.
/// </summary>
public sealed class PostgresTradeRepository(string connectionString) : ITradeRepository
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

    public NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    public void CreateTables() => Execute($"""
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

    /// <summary>
    /// The keys and the indexes the blotter reads by, each built on a connection of its own (PostgreSQL
    /// gives each build workers of its own too); then the tables analysed, for the planner.
    /// </summary>
    public async Task CreateIndexesAsync(CancellationToken ct)
    {
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
        Execute("ANALYZE order_versions; ANALYZE orders_current; ANALYZE fills");
    }

    public void KeepCurrent(CurrentSync sync)
    {
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

    /// <summary>
    /// A bulk load on three connections, one per table, so that the three <c>COPY</c>s stream side by side.
    /// </summary>
    public ITradeLoad BeginLoad() => new Load(this);

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

    public void WriteOrders(IReadOnlyList<Order> versions) =>
        Copy("order_versions", versions, WriteOrder);

    public void WriteFills(IReadOnlyList<Fill> fills) =>
        Copy("fills", fills, WriteFill);

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

    public static void WriteOrder(NpgsqlBinaryImporter import, Order o)
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

    public static void WriteFill(NpgsqlBinaryImporter import, Fill f)
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

    private sealed class Load : ITradeLoad
    {
        private readonly NpgsqlConnection[] _connections;
        private readonly NpgsqlBinaryImporter _versions;
        private readonly NpgsqlBinaryImporter _current;
        private readonly NpgsqlBinaryImporter _fills;

        public Load(PostgresTradeRepository store)
        {
            _connections = [store.Open(), store.Open(), store.Open()];
            _versions = _connections[0].BeginBinaryImport("COPY order_versions FROM STDIN (FORMAT BINARY)");
            _current = _connections[1].BeginBinaryImport("COPY orders_current FROM STDIN (FORMAT BINARY)");
            _fills = _connections[2].BeginBinaryImport("COPY fills FROM STDIN (FORMAT BINARY)");
        }

        public void AddVersion(Order version) => WriteOrder(_versions, version);

        public void AddCurrent(Order order) => WriteOrder(_current, order);

        public void AddFill(Fill fill) => WriteFill(_fills, fill);

        public void Complete()
        {
            _versions.Complete();
            _current.Complete();
            _fills.Complete();
        }

        public void Dispose()
        {
            _versions.Dispose();
            _current.Dispose();
            _fills.Dispose();
            foreach (var connection in _connections)
            {
                connection.Dispose();
            }
        }
    }
}
