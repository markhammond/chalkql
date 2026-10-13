using System.Data.Common;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using DuckDB.NET.Data;
using Npgsql;

namespace Chalk.Integration.Tests;

/// <summary>
/// A standing view over a database (D340, <c>docs/design/65-views-over-databases.md</c>): positions
/// held in-process, joined to instruments in DuckDB. The view reads the instruments when it is made,
/// keeps what it read, and reads them again only after a refresh names the table: an in-process
/// change replays what it kept, with no round trip. On each path a database read takes: a remote
/// query, a scan with nothing pushed, and the reference engine's scan.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class StandingViewDatabaseTests(SharedSidecar sidecar, SharedPostgres postgres)
{
    private sealed record Position(int Book, string Symbol, long Qty);

    private sealed record Instrument(string Symbol, string Sector, int Multiplier, DateTime Listed);

    private struct Key
    {
        public int Book;
        public int Sector;
    }

    private struct Row
    {
        public long Exposure;
    }

    private static readonly string[] Sectors = ["energy", "metals", "tech"];

    private static readonly DateTime Open = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private const string Exposure = """
        SELECT p.book, i.sector, SUM(p.qty * i.multiplier) AS exposure
        FROM positions p JOIN db.instruments i ON i.symbol = p.symbol
        GROUP BY p.book, i.sector
        """;

    private static readonly Position[] FirstPositions =
    [
        new(1, "AAA", 10), new(1, "BBB", 5), new(2, "AAA", 3), new(2, "CCC", 7),
    ];

    private static readonly Instrument[] FirstInstruments =
    [
        new("AAA", "energy", 2, Open), new("BBB", "metals", 3, Open.AddHours(1)), new("CCC", "tech", 4, Open.AddHours(2)),
    ];

    public static TheoryData<ExecutionEngine, PushdownLevel> Paths => new()
    {
        { ExecutionEngine.Vectorised, PushdownLevel.Full },
        { ExecutionEngine.Vectorised, PushdownLevel.None },
        { ExecutionEngine.Reference, PushdownLevel.None },
    };

    public enum Naming
    {
        Table,
        Source,
        Every,
    }

    public static TheoryData<ExecutionEngine, PushdownLevel, Naming> PathsAndNamings
    {
        get
        {
            var data = new TheoryData<ExecutionEngine, PushdownLevel, Naming>();
            foreach (var naming in Enum.GetValues<Naming>())
            {
                data.Add(ExecutionEngine.Vectorised, PushdownLevel.Full, naming);
                data.Add(ExecutionEngine.Vectorised, PushdownLevel.None, naming);
                data.Add(ExecutionEngine.Reference, PushdownLevel.None, naming);
            }

            return data;
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- refusals and admission ----

    /// <summary>Without the opt-in, a view over a database is refused, and the message names the opt-in.</summary>
    [Fact]
    public async Task Without_the_opt_in_a_database_is_refused_naming_the_opt_in()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await Desk.CreateAsync(sidecar, ExecutionEngine.Vectorised);

        var refused = await Assert.ThrowsAsync<StandingViewException>(() => desk.Engine
            .MaterializeAsync(Exposure, Options(PushdownLevel.Full, databases: ViewDatabaseReads.None), Ct)
            .AsTask());

        Assert.Equal(ChalkErrorCodes.SourceOutsideEpochs, refused.Code);
        Assert.Contains("DatabaseReads = ViewDatabaseReads.WhenRefreshed", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A lookup join asked for by the view's own join policy would bind the database's query with keys
    /// from the positions, so the read would change with them: refused, naming the source.
    /// </summary>
    [Fact]
    public async Task A_lookup_join_the_views_own_policy_asks_for_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await Desk.CreateAsync(sidecar, ExecutionEngine.Vectorised);
        var options = Options(PushdownLevel.Full, prepare: new PrepareOptions
        {
            JoinPolicy = new CrossSourceJoinPolicy
            {
                Pairs =
                [
                    new SourcePairRule
                    {
                        LeftSource = "desk",
                        RightSource = "db",
                        Allowed = [JoinStrategy.Lookup],
                        Preferred = JoinStrategy.Lookup,
                    },
                ],
            },
        });

        var refused = await Assert.ThrowsAsync<StandingViewException>(
            () => desk.Engine.MaterializeAsync(Exposure, options, Ct).AsTask());

        Assert.Equal(ChalkErrorCodes.UnsupportedInView, refused.Code);
        Assert.Contains("source 'db'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("join be local", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cap on local joins is the view's: past it the statement is refused when the view is made,
    /// a cap of none is refused outright, and a second cap in the join policy is refused as a conflict.
    /// </summary>
    [Fact]
    public async Task The_cap_on_local_joins_is_held_and_stated_once()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await Desk.CreateAsync(sidecar, ExecutionEngine.Vectorised);

        // The instruments' row count is unknown, so the planner takes it to be a million rows.
        var capped = await Assert.ThrowsAnyAsync<ChalkException>(() => desk.Engine
            .MaterializeAsync(Exposure, Options(PushdownLevel.Full, cap: 10), Ct)
            .AsTask());
        Assert.Equal(ChalkErrorCodes.CrossSourceJoinRefused, capped.Code);
        Assert.Contains("local_join_max_rows is 10", capped.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => desk.Engine
            .MaterializeAsync(Exposure, Options(PushdownLevel.Full, cap: 0), Ct)
            .AsTask());

        var twice = await Assert.ThrowsAsync<ArgumentException>(() => desk.Engine
            .MaterializeAsync(
                Exposure,
                Options(
                    PushdownLevel.Full,
                    prepare: new PrepareOptions { JoinPolicy = new CrossSourceJoinPolicy { LocalJoinMaxRows = 500 } }),
                Ct)
            .AsTask());
        Assert.Contains("State the cap once", twice.Message, StringComparison.Ordinal);

        // The default admits one table of unknown size a join.
        await using var view = await desk.Engine.MaterializeAsync(Exposure, Options(PushdownLevel.Full), Ct);
        Assert.Equal(Expected(FirstPositions, FirstInstruments), Drained(view));
    }

    // ---- reading when refreshed ----

    /// <summary>
    /// The view reads the database when it is made; an in-process change replays what it read, with
    /// no round trip, and the rows follow the positions.
    /// </summary>
    [Theory]
    [MemberData(nameof(Paths))]
    public async Task An_in_process_change_replays_what_was_read_from_the_database(
        ExecutionEngine executor, PushdownLevel pushdown)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await Desk.CreateAsync(sidecar, executor);
        await using var view = await desk.Engine.MaterializeAsync(Exposure, Options(pushdown), Ct);
        var reader = new Reader(view);
        reader.Drain();
        Assert.Equal(Expected(FirstPositions, FirstInstruments), reader.Rows);
        var opened = desk.Opened;
        Assert.Equal(1, view.DatabaseReads.Single().Reads);

        Position[] moved = [new(1, "AAA", 11), new(1, "BBB", 5), new(2, "CCC", 7), new(3, "BBB", 1)];
        await desk.ReplacePositionsAsync(moved);
        await view.UpdateAsync(Ct);
        reader.Drain();

        Assert.Equal(Expected(moved, FirstInstruments), reader.Rows);
        Assert.Equal(opened, desk.Opened);
        var read = view.DatabaseReads.Single();
        Assert.Equal("db", read.SourceId);
        Assert.Equal(1, read.Reads);
        Assert.Equal(3, read.Rows);
    }

    /// <summary>
    /// A change to the database is not seen until a refresh names the table — by itself, by its
    /// source, or with every source — and then the next update reads it again, once.
    /// </summary>
    [Theory]
    [MemberData(nameof(PathsAndNamings))]
    public async Task A_database_change_is_seen_after_a_refresh_names_the_table(
        ExecutionEngine executor, PushdownLevel pushdown, Naming naming)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await Desk.CreateAsync(sidecar, executor);
        await using var view = await desk.Engine.MaterializeAsync(Exposure, Options(pushdown), Ct);
        var reader = new Reader(view);
        reader.Drain();

        desk.Write("UPDATE instruments SET multiplier = 20 WHERE symbol = 'AAA'");
        Instrument[] changed = [FirstInstruments[0] with { Multiplier = 20 }, FirstInstruments[1], FirstInstruments[2]];

        // An in-process change moves the epoch, and the view keeps the instruments it read.
        Position[] moved = [.. FirstPositions, new(3, "AAA", 1)];
        await desk.ReplacePositionsAsync(moved);
        await view.UpdateAsync(Ct);
        reader.Drain();
        Assert.Equal(Expected(moved, FirstInstruments), reader.Rows);

        switch (naming)
        {
            case Naming.Table:
                await desk.Engine.RefreshAsync(r => r.Refresh(desk.Db.Table("instruments")), Ct);
                break;
            case Naming.Source:
                await desk.Engine.RefreshAsync(r => r.Refresh(RefreshTarget.Source("db")), Ct);
                break;
            case Naming.Every:
                await desk.Engine.RefreshAsync(Ct);
                break;
        }

        await view.UpdateAsync(Ct);
        reader.Drain();
        Assert.Equal(Expected(moved, changed), reader.Rows);
        Assert.Equal(2, view.DatabaseReads.Single().Reads);
    }

    /// <summary>
    /// The host writes to the database, then refreshes the table with its own version: a view that has
    /// reached that version has read the table after the write.
    /// </summary>
    [Fact]
    public async Task A_hosts_write_is_seen_by_the_version_that_names_it()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await Desk.CreateAsync(sidecar, ExecutionEngine.Vectorised);
        await using var view = await desk.Engine.MaterializeAsync(Exposure, Options(PushdownLevel.Full), Ct);
        var reader = new Reader(view);
        reader.Drain();

        desk.Write("UPDATE instruments SET sector = 'tech' WHERE symbol = 'BBB'");
        await desk.Engine.RefreshAsync(r => r.Version(7).Refresh(desk.Db.Table("instruments")), Ct);
        await view.ReachedAsync(7, Ct);
        reader.Drain();

        Instrument[] written = [FirstInstruments[0], FirstInstruments[1] with { Sector = "tech" }, FirstInstruments[2]];
        Assert.Equal(Expected(FirstPositions, written), reader.Rows);
        Assert.Equal(7, reader.Version);
        Assert.Equal(7, view.DatabaseReads.Single().LastReadVersion);
    }

    /// <summary>
    /// <c>CURRENT_TIMESTAMP</c> is never pushed into a database, so a view comparing a database column
    /// with it filters what it kept, and a move of the clock reads nothing again. The clock's
    /// parameter can be pushed: a read compared with it is read again when the clock moves.
    /// </summary>
    [Theory]
    [InlineData("CURRENT_TIMESTAMP", 1)]
    [InlineData("@now", 2)]
    public async Task A_read_bound_to_the_clock_is_read_again_when_the_clock_moves(string now, int reads)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await Desk.CreateAsync(sidecar, ExecutionEngine.Vectorised);
        await desk.Engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(Open.AddMinutes(30))), Ct);
        var listed = Exposure.Replace("GROUP BY", $"WHERE i.listed <= {now}\nGROUP BY", StringComparison.Ordinal);
        await using var view = await desk.Engine.MaterializeAsync(listed, Options(PushdownLevel.Full), Ct);
        var reader = new Reader(view);
        reader.Drain();
        Assert.Equal(Expected(FirstPositions, [FirstInstruments[0]]), reader.Rows);

        await desk.Engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(Open.AddMinutes(90))), Ct);
        await view.UpdateAsync(Ct);
        reader.Drain();

        Assert.Equal(Expected(FirstPositions, FirstInstruments[..2]), reader.Rows);
        Assert.Equal(reads, view.DatabaseReads.Single().Reads);
    }

    /// <summary>
    /// What the view keeps of its database shares its StateBudget. A refresh that would have it keep
    /// more fails the update, and the view keeps the epoch, and the read, it held.
    /// </summary>
    [Fact]
    public async Task What_a_view_keeps_of_its_database_counts_against_its_budget()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await Desk.CreateAsync(sidecar, ExecutionEngine.Vectorised);
        await using var view = await desk.Engine.MaterializeAsync(
            Exposure, Options(PushdownLevel.Full, budget: 64 << 10), Ct);
        var reader = new Reader(view);
        reader.Drain();
        var version = view.Version;

        desk.Write("INSERT INTO instruments SELECT 'X' || i, 'tech', 1, TIMESTAMP '2026-10-12 09:00:00' FROM range(5000) t(i)");
        await desk.Engine.RefreshAsync(r => r.Refresh(desk.Db.Table("instruments")), Ct);
        var refused = await Assert.ThrowsAsync<StandingViewException>(() => view.UpdateAsync(Ct).AsTask());

        Assert.Equal(ChalkErrorCodes.MemoryBudgetExceeded, refused.Code);
        Assert.Equal(version, view.Version);
        Assert.Equal(3, view.DatabaseReads.Single().Rows);
        reader.Drain();
        Assert.Equal(Expected(FirstPositions, FirstInstruments), reader.Rows);
    }

    /// <summary>
    /// Forty random steps — positions changed, the database written with and without a refresh naming
    /// the table — and after each, the reader's rows are the positions joined to the instruments as
    /// the view last read them, computed here without ChalkQL; the database is read once per refresh.
    /// </summary>
    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Random_steps_keep_the_view_level_with_what_it_read(ExecutionEngine executor, PushdownLevel pushdown)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await Desk.CreateAsync(sidecar, executor);
        await using var view = await desk.Engine.MaterializeAsync(Exposure, Options(pushdown), Ct);
        var reader = new Reader(view);
        reader.Drain();

        var random = new Random(65);
        var positions = FirstPositions.ToList();
        var database = FirstInstruments.ToDictionary(i => i.Symbol);
        var seen = database.Values.ToArray();
        var refreshes = 1;
        string[] symbols = ["AAA", "BBB", "CCC", "DDD"];
        for (var step = 0; step < 40; step++)
        {
            switch (random.Next(3))
            {
                case 0:
                    var book = random.Next(1, 4);
                    var symbol = symbols[random.Next(symbols.Length)];
                    positions.RemoveAll(p => p.Book == book && p.Symbol == symbol);
                    if (random.Next(4) > 0)
                    {
                        positions.Add(new Position(book, symbol, random.Next(-5, 20)));
                    }

                    await desk.ReplacePositionsAsync([.. positions]);
                    break;
                case 1:
                    var written = symbols[random.Next(symbols.Length)];
                    var instrument = new Instrument(written, Sectors[random.Next(Sectors.Length)], random.Next(1, 9), Open);
                    desk.Write($"DELETE FROM instruments WHERE symbol = '{written}'");
                    desk.Write(
                        $"INSERT INTO instruments VALUES ('{written}', '{instrument.Sector}', {instrument.Multiplier}, TIMESTAMP '2026-10-12 09:00:00')");
                    database[written] = instrument;
                    break;
                default:
                    await desk.Engine.RefreshAsync(r => r.Refresh(desk.Db.Table("instruments")), Ct);
                    seen = [.. database.Values];
                    refreshes++;
                    break;
            }

            await view.UpdateAsync(Ct);
            reader.Drain();
            Assert.Equal(Expected(positions, seen), reader.Rows);
        }

        Assert.Equal(refreshes, view.DatabaseReads.Single().Reads);
    }

    /// <summary>
    /// The same over PostgreSQL, through its remote queries: read when the view is made, replayed at
    /// an in-process change, and read again after a refresh names the table.
    /// </summary>
    [Fact]
    public async Task A_view_over_postgres_reads_it_when_refreshed()
    {
        var fixture = postgres.Fixture;
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var connection = await PostgresDatabaseAsync(fixture.ConnectionString!);
        await using var desk = await Desk.CreateAsync(
            sidecar, ExecutionEngine.Vectorised, () => new NpgsqlConnection(connection), DialectProfiles.PostgreSql);
        await using var view = await desk.Engine.MaterializeAsync(Exposure, Options(PushdownLevel.Full), Ct);
        var reader = new Reader(view);
        reader.Drain();
        Assert.Equal(Expected(FirstPositions, FirstInstruments), reader.Rows);
        var opened = desk.Opened;

        Position[] moved = [.. FirstPositions, new(3, "CCC", 2)];
        await desk.ReplacePositionsAsync(moved);
        await view.UpdateAsync(Ct);
        reader.Drain();
        Assert.Equal(Expected(moved, FirstInstruments), reader.Rows);
        Assert.Equal(opened, desk.Opened);

        desk.Write("UPDATE instruments SET multiplier = 9 WHERE symbol = 'CCC'");
        await desk.Engine.RefreshAsync(r => r.Refresh(desk.Db.Table("instruments")), Ct);
        await view.UpdateAsync(Ct);
        reader.Drain();
        Instrument[] changed = [FirstInstruments[0], FirstInstruments[1], FirstInstruments[2] with { Multiplier = 9 }];
        Assert.Equal(Expected(moved, changed), reader.Rows);
        Assert.Equal(2, view.DatabaseReads.Single().Reads);
    }

    /// <summary>A database of its own on the shared server, for one test.</summary>
    private static async Task<string> PostgresDatabaseAsync(string server)
    {
        var name = $"view_reads_{Environment.ProcessId}_{Guid.NewGuid():N}"[..40];
        await using (var admin = new NpgsqlConnection(server))
        {
            await admin.OpenAsync(Ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync(Ct);
        }

        return server.Replace("Database=postgres", "Database=" + name, StringComparison.Ordinal);
    }

    // ---- the scaffolding ----

    private static ViewOptions<Key, Row> Options(
        PushdownLevel pushdown,
        ViewDatabaseReads databases = ViewDatabaseReads.WhenRefreshed,
        long cap = StandingViews.DefaultLocalJoinMaxRows,
        long budget = 64L << 20,
        PrepareOptions? prepare = null) => new()
    {
        Key = ["book", "sector"],
        LocalJoinMaxRows = cap,
        DatabaseReads = databases,
        StateBudget = budget,
        Prepare = (prepare ?? new PrepareOptions()) with { Pushdown = pushdown },
        Map = static (in ViewRow row, out Key key, out Row value) =>
        {
            key = new Key { Book = row.GetInt32(0), Sector = Array.IndexOf(Sectors, row.GetString(1)) };
            value = new Row { Exposure = row.GetInt64(2) };
        },
    };

    /// <summary>The statement's answer, computed here: each book's exposure in each sector.</summary>
    private static Dictionary<(int Book, int Sector), long> Expected(
        IEnumerable<Position> positions, IEnumerable<Instrument> instruments) =>
        positions
            .Join(instruments, p => p.Symbol, i => i.Symbol, (p, i) => (p.Book, Sector: Array.IndexOf(Sectors, i.Sector), Exposure: p.Qty * i.Multiplier))
            .GroupBy(x => (x.Book, x.Sector))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Exposure));

    private static Dictionary<(int Book, int Sector), long> Drained(StandingView<Key, Row> view)
    {
        var reader = new Reader(view);
        reader.Drain();
        return reader.Rows;
    }

    /// <summary>A reader of the view, its rows by key, drained whole.</summary>
    private sealed class Reader(StandingView<Key, Row> view) : IViewSink<Key, Row>
    {
        private ViewCursor _cursor = ViewCursor.Start;

        public Dictionary<(int Book, int Sector), long> Rows { get; } = [];

        public long Version { get; private set; }

        public void Drain() => Assert.Equal(DrainOutcome.Complete, view.Drain(ref _cursor, this));

        public void Reset() => Rows.Clear();

        public void Upsert(in Key key, in Row row) => Rows[(key.Book, key.Sector)] = row.Exposure;

        public void Delete(in Key key) => Rows.Remove((key.Book, key.Sector));

        public void Commit(long version, uint checksum) => Version = version;
    }

    /// <summary>The positions in-process, the instruments in a DuckDB file, and an engine over both.</summary>
    private sealed class Desk : IAsyncDisposable
    {
        private int _opened;

        /// <summary>
        /// The positions as the host holds them, registered as a function, so that a plain refresh,
        /// which reads every registration again, reads these rather than the first ones.
        /// </summary>
        private IReadOnlyList<Position> _positions = FirstPositions;

        private Desk(Func<DbConnection> connect, DuckDbFile? file)
        {
            Connect = connect;
            File = file;
        }

        /// <summary>A new connection to the database, as the host's own writers would open one.</summary>
        public Func<DbConnection> Connect { get; }

        /// <summary>The DuckDB file the instruments are in, or null for another database.</summary>
        public DuckDbFile? File { get; }

        public PocoSource Positions { get; private set; } = null!;

        public AdoSource Db { get; private set; } = null!;

        public ChalkEngine Engine { get; private set; } = null!;

        /// <summary>The connections the engine has opened to the database: one a round trip.</summary>
        public int Opened => Volatile.Read(ref _opened);

        public static async Task<Desk> CreateAsync(
            SharedSidecar sidecar,
            ExecutionEngine executor,
            Func<DbConnection>? connect = null,
            Chalk.Catalog.DialectProfileDescriptor? dialect = null)
        {
            DuckDbFile? file = null;
            if (connect is null)
            {
                file = DuckDbFile.Create("standing-view-databases");
                var path = file.ConnectionString;
                connect = () => new DuckDBConnection(path);
                dialect = DialectProfiles.DuckDb;
            }

            var desk = new Desk(connect, file);
            using (var connection = connect())
            {
                connection.Open();
                Execute(connection, "CREATE TABLE instruments (symbol VARCHAR NOT NULL PRIMARY KEY, sector VARCHAR NOT NULL, multiplier INTEGER NOT NULL, listed TIMESTAMP NOT NULL)");
                foreach (var i in FirstInstruments)
                {
                    Execute(connection, $"INSERT INTO instruments VALUES ('{i.Symbol}', '{i.Sector}', {i.Multiplier}, TIMESTAMP '{i.Listed:yyyy-MM-dd HH:mm:ss}')");
                }
            }

            desk.Positions = new PocoSourceBuilder("desk")
                .NamingPolicy(PocoNamingPolicy.SnakeCase)
                .AddTable("positions", () => desk._positions)
                .Build();
            desk.Db = new AdoSourceBuilder(
                    "db",
                    () =>
                    {
                        Interlocked.Increment(ref desk._opened);
                        return connect();
                    },
                    "db")
                .Dialect(dialect!)
                .Options(TestTimeouts.SourceOptions)
                .Capabilities(AdoCapabilities.For(dialect!))
                .AddTable(
                    "instruments",
                    [
                        new ColumnDescriptor { Name = "symbol", Type = ChalkType.String() },
                        new ColumnDescriptor { Name = "sector", Type = ChalkType.String() },
                        new ColumnDescriptor { Name = "multiplier", Type = ChalkType.Int32() },
                        new ColumnDescriptor { Name = "listed", Type = ChalkType.Timestamp(6) },
                    ])
                .Build();
            desk.Engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                Sources = [desk.Positions, desk.Db],
                Planner = sidecar.CreatePlanner(),
                Clock = "now",
                Execution = new ExecutionOptions { Engine = executor },
            }, Ct);
            return desk;
        }

        public ValueTask<long> ReplacePositionsAsync(IReadOnlyList<Position> positions)
        {
            _positions = positions;
            return Engine.RefreshAsync(r => r.Replace(Positions, "positions", positions), Ct);
        }

        /// <summary>Changes the database behind the engine's back, as another of its writers would.</summary>
        public void Write(string sql)
        {
            using var connection = Connect();
            connection.Open();
            Execute(connection, sql);
        }

        public async ValueTask DisposeAsync()
        {
            if (Engine is not null)
            {
                await Engine.DisposeAsync();
            }

            File?.Dispose();
        }

        private static void Execute(DbConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
