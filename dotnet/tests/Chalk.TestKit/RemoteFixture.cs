using System.Data.Common;
using System.Globalization;
using System.Text;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.DuckDb;
using Chalk.Sources.Poco;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using CatalogContext = Chalk.Catalog.CatalogContext;
using CrossSourceJoinPolicy = Chalk.Catalog.CrossSourceJoinPolicy;

namespace Chalk.TestKit;

/// <summary>
/// The corpus fixture's tables, again, inside a real SQLite database and a real DuckDB one, and
/// registered as REMOTE sources <c>sqlite</c> and <c>duck</c> (§5).
/// </summary>
/// <remarks>
/// <para>
/// The point of loading the <em>same</em> rows into three places is that every M4 query has three
/// answers to compare: the POCO copy through the reference executor (the I4 oracle), the same query
/// pushed into SQLite, and the same query pushed into DuckDB. A pushdown bug that changes an answer
/// therefore shows up as a disagreement rather than as a plausible-looking result.
/// </para>
/// <para>
/// The DDL and the rows both come from the POCO source's own descriptors and scans, so the two
/// databases cannot drift from the fixture: adding a column to a fixture row type changes all three
/// copies at once, and a type Chalk has no SQL spelling for fails here rather than at a query.
/// </para>
/// </remarks>
public sealed class RemoteFixture : IDisposable
{
    /// <summary>
    /// The tables loaded into both databases. The TPC-H half, plus <c>symbols</c> so a remote table
    /// with a string key exists, and <c>funding</c> because M5's three-source query and its ASOF
    /// join both want a small time series on the far side of a boundary.
    ///
    /// <para><c>sales</c> and <c>sorted</c> are the graft's two adversarial tables (D164): ten rows
    /// between them, and the <c>edge</c> family runs against every source that holds them.</para>
    /// </summary>
    public static IReadOnlyList<string> Tables { get; } =
    [
        "lineitem", "customer", "orders", "nation", "region", "supplier", "symbols", "funding",
        "sales", "sorted",
    ];

    /// <summary>
    /// Loaded into DuckDB only. <c>bars</c> is 100 800 rows, and the queries that want it remotely
    /// want it in one place — copying it into SQLite as well would double the fixture's load time to
    /// prove nothing the DuckDB copy does not (M5, §6).
    /// </summary>
    public static IReadOnlyList<string> DuckOnlyTables { get; } = ["bars"];

    /// <summary>
    /// Loaded into PostgreSQL, when a server is there (D133 §0b). Everything SQLite holds except
    /// <c>lineitem</c>, which no federation query reads remotely and which would double the load
    /// time to prove nothing, plus <c>bars</c> so corpus 01's lookup call has a PostgreSQL spelling
    /// as well as a DuckDB one.
    /// </summary>
    public static IReadOnlyList<string> PostgresTables { get; } =
    [
        "customer", "orders", "nation", "region", "supplier", "symbols", "funding", "bars",
        "sales", "sorted",
    ];

    /// <summary>
    /// The partitions of <c>bars_by_symbol</c> (D106, §6): one physical table per symbol, spread
    /// over three databases so a partitioned scan genuinely spans sources. Two live in
    /// <c>duck</c>, two in <c>duck2</c> and one in <c>sqlite</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> BarPartitions { get; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["duck"] = ["BTCUSDT", "ETHUSDT"],
            ["duck2"] = ["SOLUSDT", "ADAUSDT"],
            ["sqlite"] = ["XRPUSDT"],
        };

    /// <summary>
    /// The same map with PostgreSQL standing in for SQLite (D133 §0b): the partition that was on the
    /// far side of an in-process driver is now on the far side of a socket, and everything else about
    /// the query is the same.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> PostgresBarPartitions { get; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["duck"] = ["BTCUSDT", "ETHUSDT"],
            ["duck2"] = ["SOLUSDT", "ADAUSDT"],
            [PostgresSourceId] = ["XRPUSDT"],
        };

    /// <summary>The source id PostgreSQL is registered under, and its schema name.</summary>
    public const string PostgresSourceId = "pg";

    /// <summary>The logical partitioned table's name (§6).</summary>
    public const string PartitionedTable = "bars_by_symbol";

    /// <summary>
    /// The source id the in-memory tail is registered under, and its schema name
    /// (<c>docs/design/39-tutorial-domain.md</c> §6).
    /// </summary>
    public const string TailSourceId = "tail";

    /// <summary>
    /// A second logical partitioned table whose two partitions sit in two different <em>kinds</em>
    /// of source: a DuckDB history beside an in-memory tail (design 39 §6). Every other partitioned
    /// table in this fixture spans ADO sources only, so the mixed case was never exercised.
    /// </summary>
    public const string AcrossKindsTable = "bars_across_kinds";

    /// <summary>The symbol the DuckDB history partition of <see cref="AcrossKindsTable"/> holds.</summary>
    public const string HistorySymbol = "BTCUSDT";

    /// <summary>The symbol the in-memory tail partition of <see cref="AcrossKindsTable"/> holds.</summary>
    public const string TailSymbol = "ETHUSDT";

    /// <summary>The schema the partitioned table is declared in. See <see cref="PartitionedViewSource"/>.</summary>
    public const string PartitionedSchema = "federated";

    /// <summary>The physical table one symbol's partition lives in.</summary>
    public static string PartitionTable(string symbol) =>
        "bars_" + symbol.ToLowerInvariant();

    private readonly List<DbConnection> _connections = [];
    private readonly List<DuckDbFile> _files = [];
    private bool _disposed;

    private readonly IRemoteFetch _duckFetch;

    private RemoteFixture(CorpusFixture poco, IRemoteFetch duckFetch)
    {
        Poco = poco;
        _duckFetch = duckFetch;
    }

    /// <summary>
    /// The three copies, built once per process. Loading 60 000 line items into two databases takes
    /// long enough that every test class having its own would be a waste; nothing mutates it.
    /// </summary>
    public static RemoteFixture Shared => LazyShared.Value;

    private static readonly Lazy<RemoteFixture> LazyShared = new(() => Create());

    /// <summary>The POCO fixture these tables were copied from.</summary>
    public CorpusFixture Poco { get; }

    /// <summary>
    /// A second in-process source holding one partition of <see cref="AcrossKindsTable"/>: the tail
    /// of the series, in memory, beside the history DuckDB holds (design 39 §6). It is a source of
    /// its own rather than another table on <see cref="Poco"/> because the corpus fixture's schema
    /// is compared column for column with the planner's Java twin.
    /// </summary>
    public PocoSource Tail => _tail ??= BuildTail();

    private PocoSource? _tail;

    private PocoSource BuildTail() =>
        new PocoSourceBuilder(TailSourceId, TailSourceId)
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable(
                PartitionTable(TailSymbol),
                [.. Poco.Bars.Where(b => string.Equals(b.Symbol.ToString(), TailSymbol, StringComparison.Ordinal))],
                t => t
                    .OrderedBy(b => b.Ts)
                    .ThenBy(b => b.Symbol)
                    .UniqueKey(b => b.Ts, b => b.Symbol))
            .Build();

    /// <summary>The SQLite copy, registered as source <c>sqlite</c>.</summary>
    public AdoSource Sqlite { get; private set; } = null!;

    /// <summary>The DuckDB copy, registered as source <c>duck</c>.</summary>
    public AdoSource Duck { get; private set; } = null!;

    /// <summary>
    /// A second DuckDB database, registered as source <c>duck2</c> (D110). Two databases in the same
    /// engine are still two sources: nothing may be pushed across them, and a partitioned table that
    /// spans both has to fan out. It holds only its share of <c>bars_by_symbol</c>.
    /// </summary>
    public AdoSource Duck2 { get; private set; } = null!;

    /// <summary>
    /// The PostgreSQL copy, registered as source <c>pg</c>, or null until
    /// <see cref="AttachPostgres"/> has been called. A real server on a real socket, which is what
    /// makes it worth having: every other source in this fixture is in-process (D133 §0b).
    /// </summary>
    public AdoSource? Postgres { get; private set; }

    /// <summary>
    /// Loads <see cref="PostgresTables"/> and the XRPUSDT partition into a PostgreSQL server and
    /// registers the result as source <c>pg</c>. Idempotent: the second caller gets the first
    /// caller's source, because loading it twice would be a hundred thousand wasted inserts.
    /// </summary>
    /// <param name="connectionString">A connection string for a database this may create tables in.</param>
    /// <param name="connect">
    /// Opens a connection from that string. Injected because <c>Chalk.TestKit</c> does not reference
    /// Npgsql — the provider is the integration project's, exactly as it is a host's.
    /// </param>
    public AdoSource AttachPostgres(string connectionString, Func<string, DbConnection> connect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(connect);
        lock (_postgresGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Postgres ??= Load(PostgresSourceId, DialectProfiles.PostgreSql, connectionString, connect);
        }
    }

    private readonly Lock _postgresGate = new();

    /// <summary>
    /// The reader a DuckDB source gets unless the caller names another: whatever
    /// <c>UseNativeReader</c> sets, read back off a throwaway builder rather than named here, so the
    /// fixture cannot drift from the default a host would get (D148a).
    /// </summary>
    public static IRemoteFetch DefaultDuckFetch => DuckDbSources.NativeReader;

    /// <summary>Why the fixture is unavailable, or null when it is. DuckDB needs a native library.</summary>
    public static string? SkipReason { get; } = Probe();

    /// <summary>
    /// The three copies, sharing one set of rows. Both databases are in-process and private to this
    /// fixture: SQLite through a shared-cache in-memory database whose lifetime is the connection
    /// this object keeps open, and DuckDB the same way.
    /// </summary>
    /// <param name="poco">The POCO fixture to copy, or the shared one.</param>
    /// <param name="duckFetch">
    /// Which reader the two DuckDB sources use (D148a). Null means the one
    /// <c>UseNativeReader</c> sets, which is what every test and every host gets; the benchmark
    /// passes the other so the gates can be measured on both.
    /// </param>
    public static RemoteFixture Create(CorpusFixture? poco = null, IRemoteFetch? duckFetch = null)
    {
        var fixture = new RemoteFixture(poco ?? CorpusFixture.Shared, duckFetch ?? DefaultDuckFetch);
        try
        {
            fixture.Sqlite = fixture.Load(
                "sqlite",
                DialectProfiles.Sqlite,
                $"Data Source=chalk-{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
                connectionString => new SqliteConnection(connectionString));
            fixture.Duck = fixture.Load(
                "duck",
                DialectProfiles.DuckDb,
                // A real file, under the test temp root. ":memory:name" looks like DuckDB's own
                // spelling of a named in-memory database and is not one to DuckDB.NET 1.5.5: the
                // provider hands the whole string to duckdb_open as a path, so it was writing a
                // 3.4 MB database and a 7.8 MB write-ahead log wherever the test process happened
                // to be standing -- bin/, or the working tree (D133 §0c).
                fixture.NewDuckDbFile("duck").ConnectionString,
                connectionString => new DuckDBConnection(connectionString));
            fixture.Duck2 = fixture.Load(
                "duck2",
                DialectProfiles.DuckDb,
                fixture.NewDuckDbFile("duck2").ConnectionString,
                connectionString => new DuckDBConnection(connectionString));
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A catalog holding all three sources: the POCO copy as the default schema, then the two
    /// remote ones. Every corpus query addresses a remote table as <c>sqlite.lineitem</c> or
    /// <c>duck.lineitem</c>, and an unqualified name still means the POCO copy (A4).
    /// </summary>
    public CatalogContext Catalog(long epoch = CorpusFixture.Epoch) => new()
    {
        ContextId = CorpusFixture.ContextId,
        Epoch = epoch,
        Schemas =
        [
            Poco.Source.DescribeSchema(),
            Sqlite.DescribeSchema(),
            Duck.DescribeSchema(),
            Duck2.DescribeSchema(),
        ],
    };

    /// <summary>
    /// The same catalog with <c>bars_by_symbol</c> in the default schema: a table whose rows live in
    /// five physical tables across three sources (D106, §6), and which no source serves itself. The
    /// planner expands a scan of it into its partitions before anything asks who owns it, which is
    /// exactly why a descriptor is all it needs.
    /// </summary>
    public CatalogContext PartitionedCatalog(
        long epoch = CorpusFixture.Epoch,
        CrossSourceJoinPolicy? policy = null)
    {
        var catalog = Catalog(epoch);
        return new CatalogContext
        {
            ContextId = catalog.ContextId,
            Epoch = epoch,
            Schemas = [.. catalog.Schemas, Tail.DescribeSchema(), PartitionedView.DescribeSchema()],
            JoinPolicy = policy ?? CrossSourceJoinPolicy.Default,
        };
    }

    /// <summary>
    /// The same catalog again with PostgreSQL in SQLite's place (D133 §0b): the POCO copy, DuckDB,
    /// the second DuckDB and <c>pg</c>, plus a partitioned table whose XRPUSDT partition is on the
    /// far side of a socket. Requires <see cref="AttachPostgres"/> to have been called.
    /// </summary>
    public CatalogContext PostgresPartitionedCatalog(
        long epoch = CorpusFixture.Epoch,
        CrossSourceJoinPolicy? policy = null)
    {
        var postgres = Postgres
            ?? throw new InvalidOperationException("AttachPostgres has not been called.");
        return new CatalogContext
        {
            ContextId = CorpusFixture.ContextId,
            Epoch = epoch,
            Schemas =
            [
                Poco.Source.DescribeSchema(),
                Duck.DescribeSchema(),
                Duck2.DescribeSchema(),
                postgres.DescribeSchema(),
                Tail.DescribeSchema(),
                PostgresPartitionedView.DescribeSchema(),
            ],
            JoinPolicy = policy ?? CrossSourceJoinPolicy.Default,
        };
    }

    /// <summary>The sources of <see cref="PostgresPartitionedCatalog"/>, for the engine.</summary>
    public IReadOnlyList<ISourceRuntime> PostgresSources =>
    [
        Poco.Source,
        Duck,
        Duck2,
        Postgres ?? throw new InvalidOperationException("AttachPostgres has not been called."),
        Tail,
        PostgresPartitionedView,
    ];

    /// <summary>
    /// The source that declares <c>federated.bars_by_symbol</c> and holds none of it: five physical
    /// tables across three sources, keyed by symbol (D106, §6).
    /// </summary>
    public PartitionedViewSource PartitionedView => _partitionedView ??= BuildPartitionedView();

    /// <summary>The same, with the XRPUSDT partition in PostgreSQL rather than SQLite.</summary>
    public PartitionedViewSource PostgresPartitionedView =>
        _postgresPartitionedView ??= BuildPartitionedView(PostgresBarPartitions);

    private PartitionedViewSource? _partitionedView;
    private PartitionedViewSource? _postgresPartitionedView;

    private PartitionedViewSource BuildPartitionedView(
        IReadOnlyDictionary<string, string[]>? partitionMap = null)
    {
        var map = partitionMap ?? BarPartitions;
        var columns = Duck.DescribeSchema().FindTable(PartitionTable("BTCUSDT"))?.Columns
            ?? throw new InvalidOperationException("the duck fixture has no bars partition.");

        var partitions = new List<PartitionDescriptor>();
        foreach (var (schema, symbols) in map.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            foreach (var symbol in symbols)
            {
                partitions.Add(new PartitionDescriptor
                {
                    Schema = schema,
                    Table = PartitionTable(symbol),
                    Value = symbol,
                    HasValue = true,
                    RowCount = Fixtures.BarMinutes,
                });
            }
        }

        var logical = new TableDescriptor
        {
            Name = PartitionedTable,
            Columns = columns,
            RowCount = (long)Fixtures.BarMinutes * Fixtures.Symbols.Length,
            RowCountKind = RowCountKind.Exact,
            Partitioning = new PartitioningDescriptor
            {
                // `symbol` is the first column of `bars`, which is what the partitions are keyed by.
                PartitionColumn = 0,
                Partitions = partitions,
            },
        };

        // Design 39 §6: the same mechanism with the two partitions in two different kinds of
        // source. The history is DuckDB's copy of BTCUSDT; the tail is the in-process POCO source,
        // which has no dialect, no connection and no SQL — so a scan of this table cannot be one
        // remote query however the planner would like to push it.
        var acrossKinds = new TableDescriptor
        {
            Name = AcrossKindsTable,
            Columns = columns,
            RowCount = (long)Fixtures.BarMinutes * 2,
            RowCountKind = RowCountKind.Exact,
            Partitioning = new PartitioningDescriptor
            {
                PartitionColumn = 0,
                Partitions =
                [
                    new PartitionDescriptor
                    {
                        Schema = "duck",
                        Table = PartitionTable(HistorySymbol),
                        Value = HistorySymbol,
                        HasValue = true,
                        RowCount = Fixtures.BarMinutes,
                    },
                    new PartitionDescriptor
                    {
                        Schema = TailSourceId,
                        Table = PartitionTable(TailSymbol),
                        Value = TailSymbol,
                        HasValue = true,
                        RowCount = Fixtures.BarMinutes,
                    },
                ],
            },
        };

        // The across-kinds table is the same in both views: its two partitions are DuckDB's and the
        // in-process source's, neither of which PostgreSQL stands in for.
        return new PartitionedViewSource(PartitionedSchema, PartitionedSchema, [logical, acrossKinds]);
    }

    /// <summary>All four data sources, by id, for <c>ChalkEngineOptions.Sources</c>.</summary>
    public IReadOnlyList<ISourceRuntime> Sources => [Poco.Source, Sqlite, Duck, Duck2];

    /// <summary>
    /// The same, plus the in-memory tail and the source that declares the partitioned tables (§6).
    /// </summary>
    public IReadOnlyList<ISourceRuntime> FederatedSources =>
        [Poco.Source, Sqlite, Duck, Duck2, Tail, PartitionedView];

    /// <summary>A DuckDB database under the temp root, deleted with its <c>.wal</c> on dispose.</summary>
    private DuckDbFile NewDuckDbFile(string label)
    {
        var file = DuckDbFile.Create(label);
        _files.Add(file);
        return file;
    }

    private AdoSource Load(
        string sourceId,
        DialectProfileDescriptor profile,
        string connectionString,
        Func<string, DbConnection> connect)
    {
        // One connection stays open for the fixture's lifetime: an in-memory database exists only
        // while something is connected to it, and every source connection is a second one into the
        // same shared cache.
        var keepAlive = connect(connectionString);
        keepAlive.Open();
        _connections.Add(keepAlive);

        // duck2 holds nothing but its share of the partitioned table: it exists to be a *second*
        // source in the same engine, not a second copy of everything (D110).
        var tables = sourceId switch
        {
            "duck2" => [],
            "duck" => Tables.Concat(DuckOnlyTables),
            PostgresSourceId => PostgresTables,
            _ => Tables,
        };

        foreach (var table in tables)
        {
            var descriptor = Poco.Source.DescribeSchema().FindTable(table)
                ?? throw new InvalidOperationException($"the POCO fixture has no table '{table}'.");
            CreateTable(keepAlive, descriptor, profile);
            InsertRows(keepAlive, descriptor, profile);
        }

        // The partitions of `bars_by_symbol` this source owns: the `bars` row type, one table per
        // symbol, holding exactly that symbol's rows (D106). PostgreSQL stands in for SQLite, so it
        // holds SQLite's share.
        var partitionMap = sourceId == PostgresSourceId ? PostgresBarPartitions : BarPartitions;
        if (partitionMap.TryGetValue(sourceId, out var symbols))
        {
            var bars = Poco.Source.DescribeSchema().FindTable("bars")
                ?? throw new InvalidOperationException("the POCO fixture has no table 'bars'.");
            foreach (var symbol in symbols)
            {
                var partition = Rename(bars, PartitionTable(symbol));
                CreateTable(keepAlive, partition, profile);
                InsertRows(keepAlive, partition, profile, row => (string?)row[0] == symbol);
            }
        }

        // Discovery, not a hand-written registration: reading the shape back out of the provider is
        // what the in-box source does for a real database, so the corpus exercises it every run.
        var builder = new AdoSourceBuilder(sourceId, () => connect(connectionString), sourceId)
            .Dialect(profile)
            .Options(TestTimeouts.SourceOptions)
            // SQLite's own default ceiling is 999 host parameters, and a conservative adapter
            // declares less than the engine's absolute limit. Declaring 200 here is that, and it is
            // also what gives the federation corpus a source whose `max_in_list` is small enough for
            // an adaptive join to reach its local branch (§6 corpus 02).
            // PostgreSQL declares SQLite's ceiling rather than its own, because it is standing in
            // for the SQLite source in the same queries: the point of the leg is a real dialect over
            // a real socket, not a different set of strategies (D133 §0b, ADR 0022).
            .Capabilities(AdoCapabilities.For(
                profile,
                maxInList: profile.Dialect is "sqlite" or "postgresql" ? 200 : 1000))

            // One COUNT(*) per table at build time (M5). Without it a discovered table's row count
            // is unknown, Calcite falls back to its 100-row guess, and a 60 000-row remote table
            // looks smaller than the local one it is being joined to — which makes every
            // cross-source cost comparison meaningless. A real deployment knows its row counts too;
            // this is the fixture saying so rather than pretending not to.
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .DiscoverTables();

        // D148: the DuckDB sources read through the native data-chunk path, so the whole M4 kit,
        // the pushdown corpus, the UDF remote corpus and the M5 federation corpus run over it. The
        // SQLite and PostgreSQL sources stay on the DbDataReader path, which is what makes the two
        // a differential rather than one implementation checked against itself.
        if (profile.Dialect == "duckdb")
        {
            builder.Fetch(_duckFetch);
        }

        // §5's native function. DuckDB has md5(); SQLite does not, and declaring one a source does
        // not have is precisely the mistake the capability model exists to make visible (D78).
        if (profile.Dialect == "duckdb")
        {
            builder.AddFunction("md5", f => f
                .Scalar<string, string>("s")
                .Strict()
                .Native("md5"));
        }

        return builder.Build();
    }

    private static void CreateTable(
        DbConnection connection, TableDescriptor table, DialectProfileDescriptor profile)
    {
        // The in-memory engines are new every run; a PostgreSQL database may not be, when
        // CHALK_TEST_POSTGRES names one the developer already had.
        if (profile.Dialect == "postgresql")
        {
            var drop = new StringBuilder("DROP TABLE IF EXISTS ");
            Quote(drop, table.Name, profile);
            Execute(connection, drop.ToString());
        }

        var sql = new StringBuilder("CREATE TABLE ");
        Quote(sql, table.Name, profile);
        sql.Append(" (");
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            Quote(sql, table.Columns[i].Name, profile);
            sql.Append(' ').Append(SqlTypeOf(table.Columns[i].Type, profile));
            if (!table.Columns[i].Type.Nullable)
            {
                sql.Append(" NOT NULL");
            }
        }

        sql.Append(')');
        Execute(connection, sql.ToString());
    }

    /// <summary>The same table under another name, for a partition of it.</summary>
    private static TableDescriptor Rename(TableDescriptor table, string name) => new()
    {
        Name = name,
        Columns = table.Columns,
        RowCount = -1,
        RowCountKind = RowCountKind.Unknown,
    };

    private void InsertRows(
        DbConnection connection,
        TableDescriptor table,
        DialectProfileDescriptor profile,
        Func<object?[], bool>? keep = null)
    {
        // Rows per statement. One INSERT per row is 60 000 round trips for `lineitem` alone; two
        // hundred rows per statement cuts that to three hundred, and stays far below SQLite's
        // 32 766-variable ceiling at sixteen columns. The command and its parameter objects are
        // built once and refilled, because a fresh DbParameter per value is a million allocations
        // for this fixture and the drivers charge for every one.
        // DuckDB has an appender, which is an order of magnitude faster than any INSERT, and it is
        // what the DuckDB oracle already loads through. Everything else goes through SQL.
        if (connection is DuckDBConnection duck)
        {
            AppendRows(duck, table, keep);
            return;
        }

        // One row per statement, and the command prepared once. A multi-row INSERT looks like the
        // obvious optimisation and is thirty times *slower* here: Microsoft.Data.Sqlite looks a
        // parameter up by name with a linear scan, so a statement with 3 200 of them is quadratic.
        // Npgsql has no such problem and every statement is a network round trip, so PostgreSQL
        // takes a hundred rows at a time: a hundred thousand bars in a thousand statements rather
        // than a hundred thousand.
        var rowsPerStatement = profile.Dialect == "postgresql" ? 100 : 1;

        var projection = Enumerable.Range(0, table.Columns.Count).ToArray();
        var request = new ScanRequest
        {
            Table = SourceTableOf(table.Name),
            Projection = projection,
            OutputSchema = ArrowTypeMapping.ToArrowSchema(table.Columns),
            BatchSize = 4096,
        };

        using var arena = new ExecutionArena();
        using var transaction = connection.BeginTransaction();
        using var full = Statement(connection, transaction, table, profile, rowsPerStatement);
        var pending = 0;
        var context = new ScanContext { Stats = new ExecutionStats(), Arena = arena };

        foreach (var batch in Drain(Poco.Source.ScanAsync(request, context, CancellationToken.None)))
        {
            using (batch)
            {
                foreach (var row in BatchReader.ToRows(batch))
                {
                    if (keep is not null && !keep(row))
                    {
                        continue;
                    }

                    for (var c = 0; c < table.Columns.Count; c++)
                    {
                        full.Parameters[(pending * table.Columns.Count) + c].Value =
                            ToProviderValue(row[c], table.Columns[c].Type) ?? DBNull.Value;
                    }

                    if (++pending == rowsPerStatement)
                    {
                        full.ExecuteNonQuery();
                        pending = 0;
                    }
                }
            }
        }

        if (pending > 0)
        {
            using var remainder = Statement(connection, transaction, table, profile, pending);
            for (var i = 0; i < pending * table.Columns.Count; i++)
            {
                remainder.Parameters[i].Value = full.Parameters[i].Value;
            }

            remainder.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// DuckDB's appender: rows straight into the column store, with no statement to parse and no
    /// parameters to bind. The value shapes are the driver's own, so this is the one place the
    /// fixture converts to them explicitly.
    /// </summary>
    private void AppendRows(
        DuckDBConnection connection, TableDescriptor table, Func<object?[], bool>? keep)
    {
        var projection = Enumerable.Range(0, table.Columns.Count).ToArray();
        var request = new ScanRequest
        {
            Table = SourceTableOf(table.Name),
            Projection = projection,
            OutputSchema = ArrowTypeMapping.ToArrowSchema(table.Columns),
            BatchSize = 4096,
        };

        using var arena = new ExecutionArena();
        using var appender = connection.CreateAppender(table.Name);
        var context = new ScanContext { Stats = new ExecutionStats(), Arena = arena };
        foreach (var batch in Drain(Poco.Source.ScanAsync(request, context, CancellationToken.None)))
        {
            using (batch)
            {
                foreach (var values in BatchReader.ToRows(batch))
                {
                    if (keep is not null && !keep(values))
                    {
                        continue;
                    }

                    var row = appender.CreateRow();
                    for (var c = 0; c < table.Columns.Count; c++)
                    {
                        row = Append(row, ToProviderValue(values[c], table.Columns[c].Type));
                    }

                    row.EndRow();
                }
            }
        }
    }

    private static DuckDB.NET.Data.IDuckDBAppenderRow Append(
        DuckDB.NET.Data.IDuckDBAppenderRow row, object? value) => value switch
        {
            null => row.AppendNullValue(),
            bool b => row.AppendValue(b),
            sbyte v => row.AppendValue(v),
            short v => row.AppendValue(v),
            int v => row.AppendValue(v),
            long v => row.AppendValue(v),
            float v => row.AppendValue(v),
            double v => row.AppendValue(v),
            decimal v => row.AppendValue(v),
            string v => row.AppendValue(v),
            Guid v => row.AppendValue(v),
            DateTime v => row.AppendValue(v),
            DateTimeOffset v => row.AppendValue(v.UtcDateTime),
            DateOnly v => row.AppendValue(v),
            TimeSpan v => row.AppendValue(TimeOnly.FromTimeSpan(v)),
            byte[] v => row.AppendValue(v),
            _ => row.AppendValue(Convert.ToString(value, CultureInfo.InvariantCulture)),
        };

    /// <summary>A multi-row <c>INSERT</c> for exactly <paramref name="rows"/> rows, parameters and all.</summary>
    private static DbCommand Statement(
        DbConnection connection,
        DbTransaction transaction,
        TableDescriptor table,
        DialectProfileDescriptor profile,
        int rows)
    {
        // $p0-style names: both in-process drivers take them, and a bare `?` needs each provider's
        // own convention for what a parameter is called. Npgsql reads `$1` as a positional
        // placeholder and would send `$p0` to the server, which rejects it, so PostgreSQL gets `@`.
        // The real query path solves the same problem through DialectProfile.ParameterStyle; this is
        // the loader, so it says it directly.
        var marker = profile.Dialect == "postgresql" ? "@p" : "$p";
        var sql = new StringBuilder("INSERT INTO ");
        Quote(sql, table.Name, profile);
        sql.Append(" VALUES ");
        var ordinal = 0;
        for (var r = 0; r < rows; r++)
        {
            sql.Append(r > 0 ? ", (" : "(");
            for (var c = 0; c < table.Columns.Count; c++)
            {
                if (c > 0)
                {
                    sql.Append(", ");
                }

                sql.Append(marker).Append(ordinal++);
            }

            sql.Append(')');
        }

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql.ToString();
        for (var i = 0; i < ordinal; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "p" + i;
            parameter.Value = DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    /// <summary>
    /// The value a driver takes for this column. Both drivers are happiest with the CLR shape the
    /// declared type names — a <c>DateTime</c> for a TIMESTAMP, a <c>decimal</c> for a DECIMAL — and
    /// the reference values arrive as the executor's storage form, so this is the one place the two
    /// meet.
    /// </summary>
    private static object? ToProviderValue(object? value, ChalkType type) => value switch
    {
        null => null,
        string[] tags => string.Join(',', tags),
        System.Collections.IEnumerable list and not string => string.Join(
            ',', list.Cast<object?>().Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))),
        _ => value,
    };

    /// <summary>The SQL type a Chalk type is spelled as, in a dialect that has to store it exactly.</summary>
    private static string SqlTypeOf(ChalkType type, DialectProfileDescriptor profile) => type.Kind switch
    {
        TypeKind.Bool => profile.HasBoolean ? "BOOLEAN" : "INTEGER",
        TypeKind.I8 or TypeKind.I16 or TypeKind.I32 => "INTEGER",
        TypeKind.I64 => "BIGINT",
        TypeKind.Fp32 => "REAL",
        TypeKind.Fp64 => profile.Dialect == "postgresql" ? "DOUBLE PRECISION" : "DOUBLE",
        TypeKind.Decimal => profile.Dialect == "sqlite"
            // SQLite has no exact decimal; the corpus's decimals are money-shaped and fit a double,
            // and the profile says so (MaxNumericPrecision 15), which is what stops the planner
            // pushing a comparison that would truncate.
            ? "DECIMAL(" + type.Precision + "," + type.Scale + ")"
            : "DECIMAL(" + type.Precision + "," + type.Scale + ")",
        TypeKind.String => "VARCHAR",
        TypeKind.Binary => profile.Dialect == "postgresql" ? "BYTEA" : "BLOB",
        TypeKind.Date => "DATE",
        TypeKind.Time => "TIME",
        TypeKind.Timestamp => "TIMESTAMP",
        TypeKind.TimestampTz => "TIMESTAMPTZ",
        TypeKind.Uuid => "UUID",

        // A LIST has no portable SQL spelling and no corpus query reads one remotely; storing the
        // elements as a comma-joined string keeps the column present so the row shapes match.
        TypeKind.List => "VARCHAR",
        _ => throw new NotSupportedException($"no SQL spelling for {type.Kind} in the remote fixture."),
    };

    /// <summary>
    /// The POCO table a copy's rows come from. A partition is named after its symbol and reads from
    /// <c>bars</c>, so the two names part company in exactly one place — here.
    /// </summary>
    private static string SourceTableOf(string name) =>
        name.StartsWith("bars_", StringComparison.Ordinal) && name != "bars_small" ? "bars" : name;

    private static void Quote(StringBuilder sql, string name, DialectProfileDescriptor profile)
    {
        sql.Append('"');
        foreach (var c in name)
        {
            if (c == '"')
            {
                sql.Append('"');
            }

            sql.Append(c);
        }

        sql.Append('"');
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The POCO scan drained synchronously. It never awaits — it is an in-process collection — so
    /// this is a loop rather than an <c>await foreach</c> in a synchronous method.
    /// </summary>
    private static IEnumerable<RecordBatch> Drain(IAsyncEnumerable<RecordBatch> batches)
    {
        var enumerator = batches.GetAsyncEnumerator(CancellationToken.None);
        try
        {
            while (true)
            {
                var move = enumerator.MoveNextAsync();
                if (!move.IsCompleted)
                {
                    throw new InvalidOperationException(
                        "the POCO source awaited; the remote fixture loads from it synchronously.");
                }

                if (!move.GetAwaiter().GetResult())
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            var dispose = enumerator.DisposeAsync();
            if (dispose.IsCompleted)
            {
                dispose.GetAwaiter().GetResult();
            }
        }
    }

    private static string? Probe()
    {
        try
        {
            using var duck = new DuckDBConnection("DataSource=:memory:");
            duck.Open();
            using var sqlite = new SqliteConnection("Data Source=:memory:");
            sqlite.Open();
            return null;
        }
        catch (Exception failure)
        {
            return $"the remote fixture needs SQLite and DuckDB in-process: {failure.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var connection in _connections)
        {
            connection.Dispose();
        }

        // After the last connection, so DuckDB has released the database it was holding open.
        foreach (var file in _files)
        {
            file.Dispose();
        }
    }
}
