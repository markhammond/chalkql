using System.Globalization;
using System.Numerics;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.TestKit;
using DuckDB.NET.Data;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Integration.Tests;

/// <summary>
/// The third oracle (D28): an in-memory DuckDB loaded from the same fixtures, so a corpus query has
/// a mature independent engine to disagree with. Two executors that share a misunderstanding of SQL
/// agree with each other perfectly; this is the check that costs a native dependency and is worth it.
/// </summary>
/// <remarks>
/// DuckDB's answers arrive as CLR values through <see cref="DuckDBDataReader"/> and are converted to
/// Chalk's storage form — <c>long</c> for every exact and temporal kind, <c>decimal</c>, <c>double</c>
/// — against the Chalk schema for the same query, then built into a <see cref="RecordBatch"/> so the
/// comparison is the same <see cref="ResultComparer"/> the two executors use. The conversion is where
/// the dialects meet: DuckDB widens <c>SUM(BIGINT)</c> to HUGEINT and returns
/// <see cref="BigInteger"/>, and gives <c>AVG(DECIMAL)</c> a DOUBLE where Calcite gives a DECIMAL.
/// </remarks>
internal sealed class DuckDbOracle : IDisposable
{
    private readonly DuckDBConnection _connection;

    private DuckDbOracle(DuckDBConnection connection) => _connection = connection;

    /// <summary>Why the oracle is unavailable, or null when it is.</summary>
    public static string? SkipReason { get; } = Probe();

    /// <summary>An oracle holding the same rows as <paramref name="fixture"/>.</summary>
    public static DuckDbOracle Create(CorpusFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        var oracle = new DuckDbOracle(connection);
        try
        {
            oracle.LoadBars(fixture.Bars);
            oracle.LoadBars(fixture.BarsSmall, "bars_small");

            // D257: the same rows again, under the name the clustered corpus query asks for. DuckDB
            // has no such index and is not meant to — what it answers for is the *rows*, which must
            // be the same to the byte whether Chalk read them through a copy or through a gather.
            oracle.LoadBars(fixture.BarsSmall, "bars_clustered");
            oracle.LoadSymbols(fixture.SymbolRows);
            oracle.LoadTrades(fixture.SparseTrades);
            oracle.LoadLineItems(fixture.LineItems);
            oracle.LoadFunding(fixture.Funding);
            oracle.LoadEvents(fixture.Events);
            oracle.LoadCustomers(fixture.Customers);
            oracle.LoadOrders(fixture.Orders);
            oracle.LoadNations(fixture.Nations);
            oracle.LoadRegions(fixture.Regions);
            oracle.LoadSuppliers(fixture.Suppliers);
            oracle.LoadSales(fixture.Sales);
            oracle.LoadSorted(fixture.SortedRows);
            return oracle;
        }
        catch
        {
            oracle.Dispose();
            throw;
        }
    }

    /// <summary>Runs <paramref name="sql"/> and shapes the answer like <paramref name="schema"/>.</summary>
    public IReadOnlyList<RecordBatch> Run(string sql, ArrowSchema schema)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(schema);

        var types = BatchReader.TypesOf(schema);
        var rows = new List<object?[]>();

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            if (reader.FieldCount != types.Count)
            {
                throw new InvalidOperationException(
                    $"DuckDB returned {reader.FieldCount} columns, Chalk's plan has {types.Count}:\n{sql}");
            }

            while (reader.Read())
            {
                var values = new object?[types.Count];
                for (var i = 0; i < types.Count; i++)
                {
                    values[i] = reader.IsDBNull(i) ? null : Storage(reader.GetValue(i), types[i]);
                }

                rows.Add(values);
            }
        }

        return [BatchBuilder.FromStorageRows(schema, rows)];
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>One DuckDB value in Chalk's storage form for the column it landed in.</summary>
    private static object? Storage(object value, ChalkType type) => type.Kind switch
    {
        TypeKind.Bool => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
        TypeKind.Fp32 => (float)ToDouble(value),
        TypeKind.Fp64 => ToDouble(value),
        TypeKind.String => (string)value,
        TypeKind.Binary => (byte[])value,
        TypeKind.Decimal => ToDecimal(value),
        TypeKind.Date => (long)DayNumber(value),
        TypeKind.Time => ((TimeOnly)value).Ticks / TimeSpan.TicksPerMicrosecond,
        TypeKind.Timestamp or TypeKind.TimestampTz => TimestampUnits(value, type),
        TypeKind.List => ToList(value, type.Element!.Value),
        _ => ToLong(value),
    };

    /// <summary>A DuckDB list, as the <c>object?[]</c> of storage values Chalk's comparer reads.</summary>
    private static object?[] ToList(object value, ChalkType element)
    {
        var source = (System.Collections.IEnumerable)value;
        var elements = new List<object?>();
        foreach (var item in source)
        {
            elements.Add(item is null ? null : Storage(item, element));
        }

        return [.. elements];
    }

    private static double ToDouble(object value) => value switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        BigInteger big => (double)big,
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
    };

    private static decimal ToDecimal(object value) => value switch
    {
        decimal m => m,
        double d => (decimal)d,
        BigInteger big => (decimal)big,
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
    };

    // DuckDB promotes SUM over a 64-bit column to HUGEINT, so the count of a few thousand rows comes
    // back as a BigInteger. Everything else is already an integral CLR type.
    private static long ToLong(object value) => value switch
    {
        BigInteger big => (long)big,
        bool flag => flag ? 1L : 0L,
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    };

    private static int DayNumber(object value) => value switch
    {
        DateOnly date => date.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber,
        DateTime moment => DateOnly.FromDateTime(moment).DayNumber
            - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber,
        _ => throw new InvalidOperationException($"{value.GetType().Name} is not a DATE"),
    };

    private static long TimestampUnits(object value, ChalkType type)
    {
        var moment = value switch
        {
            DateTime instant => instant,
            DateTimeOffset offset => offset.UtcDateTime,
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            _ => throw new InvalidOperationException($"{value.GetType().Name} is not a TIMESTAMP"),
        };

        var perSecond = IrTypes.TimestampUnitsPerSecond((uint)type.Precision);
        var ticks = moment.Ticks - DateTime.UnixEpoch.Ticks;
        return perSecond >= TimeSpan.TicksPerSecond
            ? ticks * (perSecond / TimeSpan.TicksPerSecond)
            : ticks / (TimeSpan.TicksPerSecond / perSecond);
    }

    private static string? Probe()
    {
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            _ = command.ExecuteScalar();
            return null;
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException
            or BadImageFormatException or EntryPointNotFoundException)
        {
            return $"DuckDB's native library did not load: {e.Message}";
        }
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void LoadBars(IReadOnlyList<Bar> bars, string table = "bars")
    {
        Execute($"""
            CREATE TABLE {table} (
              symbol VARCHAR, ts TIMESTAMP, "open" DOUBLE, high DOUBLE, low DOUBLE, "close" DOUBLE,
              volume BIGINT, vwap DECIMAL(28,10), trade_count INTEGER)
            """);

        using var appender = _connection.CreateAppender(table);
        foreach (var bar in bars)
        {
            var row = appender.CreateRow()
                .AppendValue(bar.Symbol.ToString())
                .AppendValue(bar.Ts)
                .AppendValue(bar.Open)
                .AppendValue(bar.High)
                .AppendValue(bar.Low)
                .AppendValue(bar.Close)
                .AppendValue(bar.Volume);
            row = bar.Vwap is { } vwap ? row.AppendValue(vwap) : row.AppendNullValue();
            row = bar.TradeCount is { } count ? row.AppendValue(count) : row.AppendNullValue();
            row.EndRow();
        }
    }

    /// <summary>
    /// <c>symbols</c>, including step 19's <c>tags VARCHAR[]</c> (D58). The appender has no list
    /// overload, so the rows go in through an INSERT with the array written as a literal — five rows,
    /// once per fixture.
    /// </summary>
    private void LoadSymbols(IReadOnlyList<SymbolRow> symbols)
    {
        Execute("""
            CREATE TABLE symbols (
              symbol VARCHAR, "base" VARCHAR, "quote" VARCHAR, tick_size DECIMAL(28,10),
              tags VARCHAR[])
            """);

        foreach (var symbol in symbols)
        {
            var tags = symbol.Tags is null
                ? "NULL"
                : "[" + string.Join(", ", symbol.Tags.Select(t => "'" + t.Replace("'", "''", StringComparison.Ordinal) + "'")) + "]";
            Execute(string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT INTO symbols VALUES ('{symbol.Symbol}', '{symbol.Base}', '{symbol.Quote}', "
                + $"{symbol.TickSize}, {tags})"));
        }
    }

    /// <summary>The graft's six adversarial rows (D164): ties, a NULL amount, two regions.</summary>
    private void LoadSales(IReadOnlyList<Sale> sales)
    {
        Execute("CREATE TABLE sales (id INTEGER, region VARCHAR, amount INTEGER, label VARCHAR)");
        foreach (var sale in sales)
        {
            Execute(string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT INTO sales VALUES ({sale.Id}, '{sale.Region}', "
                + $"{(sale.Amount is { } amount ? amount.ToString(CultureInfo.InvariantCulture) : "NULL")}, "
                + $"'{sale.Label}')"));
        }
    }

    /// <summary>The graft's four ordered rows (D164): a duplicate key and a gap.</summary>
    private void LoadSorted(IReadOnlyList<SortedRow> rows)
    {
        Execute("CREATE TABLE sorted (k INTEGER, v VARCHAR)");
        foreach (var row in rows)
        {
            Execute(string.Create(
                CultureInfo.InvariantCulture, $"INSERT INTO sorted VALUES ({row.K}, '{row.V}')"));
        }
    }

    /// <summary>The SESSION fixture (D55). DuckDB has no session window, but the rows are still
    /// wanted: a query that only reads them is portable even when one that windows them is not.</summary>
    private void LoadTrades(IReadOnlyList<Trade> trades)
    {
        Execute("""
            CREATE TABLE trades_sparse (
              symbol VARCHAR, ts TIMESTAMP_NS, price DOUBLE, "size" BIGINT)
            """);

        using var appender = _connection.CreateAppender("trades_sparse");
        foreach (var trade in trades)
        {
            appender.CreateRow()
                .AppendValue(trade.Symbol.ToString())
                .AppendValue(trade.Ts)
                .AppendValue(trade.Price)
                .AppendValue(trade.Size)
                .EndRow();
        }
    }

    private void LoadLineItems(IReadOnlyList<LineItem> lineItems)
    {
        Execute("""
            CREATE TABLE lineitem (
              l_orderkey BIGINT, l_linenumber INTEGER, l_partkey BIGINT, l_suppkey BIGINT,
              l_quantity DECIMAL(15,2), l_extendedprice DECIMAL(15,2), l_discount DECIMAL(15,2),
              l_tax DECIMAL(15,2), l_returnflag VARCHAR, l_linestatus VARCHAR,
              l_shipdate DATE, l_commitdate DATE, l_receiptdate DATE,
              l_shipinstruct VARCHAR, l_shipmode VARCHAR, l_comment VARCHAR)
            """);

        using var appender = _connection.CreateAppender("lineitem");
        foreach (var item in lineItems)
        {
            appender.CreateRow()
                .AppendValue(item.OrderKey)
                .AppendValue(item.LineNumber)
                .AppendValue(item.PartKey)
                .AppendValue(item.SuppKey)
                .AppendValue(item.Quantity)
                .AppendValue(item.ExtendedPrice)
                .AppendValue(item.Discount)
                .AppendValue(item.Tax)
                .AppendValue(item.ReturnFlag)
                .AppendValue(item.LineStatus)
                .AppendValue(item.ShipDate)
                .AppendValue(item.CommitDate)
                .AppendValue(item.ReceiptDate)
                .AppendValue(item.ShipInstruct)
                .AppendValue(item.ShipMode)
                .AppendValue(item.Comment)
                .EndRow();
        }
    }

    private void LoadFunding(IReadOnlyList<Funding> funding)
    {
        Execute("""
            CREATE TABLE funding (symbol VARCHAR, ts TIMESTAMP, rate DECIMAL(10,8))
            """);

        using var appender = _connection.CreateAppender("funding");
        foreach (var row in funding)
        {
            var built = appender.CreateRow().AppendValue(row.Symbol.ToString());
            built = row.Ts is { } ts ? built.AppendValue(ts) : built.AppendNullValue();
            built.AppendValue(row.Rate).EndRow();
        }
    }

    private void LoadEvents(IReadOnlyList<MarketEvent> events)
    {
        Execute("""
            CREATE TABLE events (
              id INTEGER, symbol VARCHAR, start_ts TIMESTAMP, end_ts TIMESTAMP, kind VARCHAR)
            """);

        using var appender = _connection.CreateAppender("events");
        foreach (var row in events)
        {
            appender.CreateRow()
                .AppendValue(row.Id)
                .AppendValue(row.Symbol.ToString())
                .AppendValue(row.StartTs)
                .AppendValue(row.EndTs)
                .AppendValue(row.Kind)
                .EndRow();
        }
    }

    private void LoadCustomers(IReadOnlyList<Customer> customers)
    {
        Execute("""
            CREATE TABLE customer (
              c_custkey BIGINT, c_name VARCHAR, c_address VARCHAR, c_nationkey BIGINT,
              c_phone VARCHAR, c_acctbal DECIMAL(15,2), c_mktsegment VARCHAR, c_comment VARCHAR)
            """);

        using var appender = _connection.CreateAppender("customer");
        foreach (var row in customers)
        {
            appender.CreateRow()
                .AppendValue(row.CustKey)
                .AppendValue(row.Name)
                .AppendValue(row.Address)
                .AppendValue(row.NationKey)
                .AppendValue(row.Phone)
                .AppendValue(row.AcctBal)
                .AppendValue(row.MktSegment)
                .AppendValue(row.Comment)
                .EndRow();
        }
    }

    private void LoadOrders(IReadOnlyList<Order> orders)
    {
        Execute("""
            CREATE TABLE orders (
              o_orderkey BIGINT, o_custkey BIGINT, o_orderstatus VARCHAR, o_totalprice DECIMAL(15,2),
              o_orderdate DATE, o_orderpriority VARCHAR, o_clerk VARCHAR, o_shippriority INTEGER,
              o_comment VARCHAR)
            """);

        using var appender = _connection.CreateAppender("orders");
        foreach (var row in orders)
        {
            appender.CreateRow()
                .AppendValue(row.OrderKey)
                .AppendValue(row.CustKey)
                .AppendValue(row.OrderStatus)
                .AppendValue(row.TotalPrice)
                .AppendValue(row.OrderDate)
                .AppendValue(row.OrderPriority)
                .AppendValue(row.Clerk)
                .AppendValue(row.ShipPriority)
                .AppendValue(row.Comment)
                .EndRow();
        }
    }

    private void LoadNations(IReadOnlyList<Nation> nations)
    {
        Execute("""
            CREATE TABLE nation (
              n_nationkey BIGINT, n_name VARCHAR, n_regionkey BIGINT, n_comment VARCHAR)
            """);

        using var appender = _connection.CreateAppender("nation");
        foreach (var row in nations)
        {
            appender.CreateRow()
                .AppendValue(row.NationKey)
                .AppendValue(row.Name)
                .AppendValue(row.RegionKey)
                .AppendValue(row.Comment)
                .EndRow();
        }
    }

    private void LoadRegions(IReadOnlyList<Region> regions)
    {
        Execute("""
            CREATE TABLE region (r_regionkey BIGINT, r_name VARCHAR, r_comment VARCHAR)
            """);

        using var appender = _connection.CreateAppender("region");
        foreach (var row in regions)
        {
            appender.CreateRow()
                .AppendValue(row.RegionKey)
                .AppendValue(row.Name)
                .AppendValue(row.Comment)
                .EndRow();
        }
    }

    private void LoadSuppliers(IReadOnlyList<Supplier> suppliers)
    {
        Execute("""
            CREATE TABLE supplier (
              s_suppkey BIGINT, s_name VARCHAR, s_address VARCHAR, s_nationkey BIGINT,
              s_phone VARCHAR, s_acctbal DECIMAL(15,2), s_comment VARCHAR)
            """);

        using var appender = _connection.CreateAppender("supplier");
        foreach (var row in suppliers)
        {
            appender.CreateRow()
                .AppendValue(row.SuppKey)
                .AppendValue(row.Name)
                .AppendValue(row.Address)
                .AppendValue(row.NationKey)
                .AppendValue(row.Phone)
                .AppendValue(row.AcctBal)
                .AppendValue(row.Comment)
                .EndRow();
        }
    }
}
