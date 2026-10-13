using System.Data.Common;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using Microsoft.Data.Sqlite;

namespace Chalk.Integration.Tests;

/// <summary>
/// A plan made on other numbers (D342, <c>docs/design/67-plans-and-statistics.md</c>): a statement is
/// stale once a table it reads has drifted tenfold since it was planned, a view over it prepares it
/// again without resetting its readers, and the logical digest names the statement throughout.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class PlanStatisticsTests(SharedSidecar sidecar) : IDisposable
{
    private sealed record Fill(int Seq, int Book, long Qty);

    private sealed record Book(int Id, int Desk);

    private struct One
    {
        public long Value;
    }

    private const string ByDesk =
        "SELECT b.desk, SUM(f.qty) AS qty FROM fills AS f JOIN books AS b ON b.id = f.book GROUP BY b.desk";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<DbConnection> _open = [];

    public void Dispose()
    {
        foreach (var connection in _open)
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// F196 as it was found: a statement prepared before the first load runs the plan that empty
    /// tables chose. After the load it is stale, and still runs correctly; the statement prepared in
    /// its place plans for the rows there are now, under the same logical digest.
    /// </summary>
    [Fact]
    public async Task A_statement_prepared_before_a_load_is_stale_after_it()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync([], []);
        var before = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        Assert.False(before.IsStale);

        await desk.LoadAsync(Fills(5_000, books: 50), Books(50));

        Assert.True(before.IsStale);
        var after = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        Assert.False(after.IsStale);
        Assert.NotEqual(before.PlanDigest, after.PlanDigest);
        Assert.NotEqual(0UL, after.LogicalDigest);
        Assert.Equal(before.LogicalDigest, after.LogicalDigest);
        Assert.Equal(await RowsAsync(desk.Engine, after), await RowsAsync(desk.Engine, before));
    }

    /// <summary>
    /// Tenfold either way, from the counts the statement was planned on: nine times as many rows is
    /// not drift, ten times is, and so is a tenth.
    /// </summary>
    [Fact]
    public async Task Drift_is_tenfold_either_way()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        const string totals = "SELECT book, SUM(qty) AS qty FROM fills GROUP BY book";
        await using var desk = await DeskAsync(Fills(100, books: 10), Books(10));

        var planned = await desk.Engine.PrepareAsync(totals, ct: Ct);
        await desk.LoadAsync(Fills(999, books: 10), Books(10));
        Assert.False(planned.IsStale);
        await desk.LoadAsync(Fills(1_000, books: 10), Books(10));
        Assert.True(planned.IsStale);

        var loaded = await desk.Engine.PrepareAsync(totals, ct: Ct);
        await desk.LoadAsync(Fills(101, books: 10), Books(10));
        Assert.False(loaded.IsStale);
        await desk.LoadAsync(Fills(100, books: 10), Books(10));
        Assert.True(loaded.IsStale);
    }

    /// <summary>An empty table counts as one row: from empty, nine rows are not drift and ten are.</summary>
    [Fact]
    public async Task An_empty_table_counts_as_one_row()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync([], Books(10));

        var empty = await desk.Engine.PrepareAsync("SELECT book, SUM(qty) AS qty FROM fills GROUP BY book", ct: Ct);
        await desk.LoadAsync(Fills(9, books: 10), Books(10));
        Assert.False(empty.IsStale);
        await desk.LoadAsync(Fills(10, books: 10), Books(10));
        Assert.True(empty.IsStale);
    }

    /// <summary>
    /// A table whose row count the catalog does not know is not judged: a discovered SQLite table,
    /// which states no count, grows a thousandfold and the statement over it stays current.
    /// </summary>
    [Fact]
    public async Task A_table_whose_count_is_unknown_is_not_judged()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (source, connection) = Database();
        await using var engine = await EngineAsync(source);
        Assert.True(RowCount(engine, "widget") < 0, "discovery states no row count");

        var prepared = await engine.PrepareAsync("SELECT id, name FROM db.widget", ct: Ct);
        Execute(
            connection,
            "WITH RECURSIVE n(i) AS (SELECT 4 UNION ALL SELECT i + 1 FROM n WHERE i < 3000) "
            + "INSERT INTO widget SELECT i, 'w' FROM n");
        await engine.RefreshAsync(Ct);

        Assert.False(prepared.IsStale);
        Assert.Equal(3_000, (await IdsAsync(engine, prepared)).Count);
    }

    /// <summary>
    /// A view whose statement drifts prepares it again. Its result has the columns and key it had,
    /// so its readers keep their place: the next drain sends what changed, and no reset. The reset
    /// threshold is set out of reach, so that a reset could only come from the new plan.
    /// </summary>
    [Fact]
    public async Task A_view_prepared_again_for_drift_keeps_its_readers()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync(Fills(30, books: 3), Books(3));
        var before = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        await using var view = await desk.Engine.MaterializeAsync(before, DeskOptions(), Ct);
        var sink = new Recorder();
        var cursor = ViewCursor.Start;
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        Assert.Equal("reset", sink.Calls[0]);
        sink.Calls.Clear();

        await desk.LoadAsync(Fills(3_000, books: 3), Books(3));
        Assert.True(before.IsStale);
        await view.UpdateAsync(Ct);

        Assert.NotSame(before, view.Statement);
        Assert.False(view.Statement.IsStale);
        Assert.Equal(before.LogicalDigest, view.Statement.LogicalDigest);
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        Assert.DoesNotContain("reset", sink.Calls);
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), sink.Rows);
    }

    /// <summary>
    /// A shape change that leaves the result as it was keeps a view's readers too: a column added to
    /// a table the view reads, which its statement does not select.
    /// </summary>
    [Fact]
    public async Task A_view_whose_result_is_unchanged_by_a_shape_change_keeps_its_readers()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (source, connection) = Database();
        await using var engine = await EngineAsync(source);
        await using var view = await engine.MaterializeAsync("SELECT id, name FROM db.widget", WidgetOptions(), Ct);
        var (sink, cursor) = Drained(view);
        var before = view.Statement;

        Execute(connection, "ALTER TABLE widget ADD COLUMN price REAL");
        Execute(connection, "INSERT INTO widget (id, name) VALUES (4, 'four')");
        await engine.RefreshAsync(Ct);
        await view.UpdateAsync(Ct);

        Assert.NotSame(before, view.Statement);
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        Assert.Equal(["upsert 4", "commit 1"], sink.Calls);
    }

    /// <summary>
    /// A statement prepared again with a result that changed resets the view's readers, as before:
    /// a column added to a table read with <c>SELECT *</c>.
    /// </summary>
    [Fact]
    public async Task A_view_whose_result_changed_resets_its_readers()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (source, connection) = Database();
        await using var engine = await EngineAsync(source);
        await using var view = await engine.MaterializeAsync("SELECT * FROM db.widget", WidgetOptions(), Ct);
        var (sink, cursor) = Drained(view);
        Assert.Equal(2, view.Columns.FieldsList.Count);

        Execute(connection, "ALTER TABLE widget ADD COLUMN price REAL");
        await engine.RefreshAsync(Ct);
        await view.UpdateAsync(Ct);

        Assert.Equal(3, view.Columns.FieldsList.Count);
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        Assert.Equal(["reset", "upsert 1", "upsert 2", "upsert 3", "commit 1"], sink.Calls);
    }

    // ---- the parts ----

    private static IReadOnlyList<Fill> Fills(int count, int books) =>
        [.. Enumerable.Range(1, count).Select(i => new Fill(i, i % books, (i % 7) + 1))];

    private static IReadOnlyList<Book> Books(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Book(i, i % 3))];

    private static ViewOptions<One, One> DeskOptions() => new()
    {
        Key = ["desk"],
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Map = static (in ViewRow row, out One key, out One value) =>
        {
            key = new One { Value = row.GetInt64(0) };
            value = new One { Value = row.GetInt64(1) };
        },
        ResetWhen = double.PositiveInfinity,
    };

    private static ViewOptions<One, One> WidgetOptions() => new()
    {
        Key = ["id"],
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        DatabaseReads = ViewDatabaseReads.WhenRefreshed,
        Map = static (in ViewRow row, out One key, out One value) =>
        {
            key = new One { Value = row.GetInt64(0) };
            value = key;
        },
    };

    private static (Recorder Sink, ViewCursor Cursor) Drained(StandingView<One, One> view)
    {
        var sink = new Recorder();
        var cursor = ViewCursor.Start;
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        sink.Calls.Clear();
        return (sink, cursor);
    }

    /// <summary>A statement's rows, the first column to the second, as numbers.</summary>
    private static async Task<Dictionary<long, long>> RowsAsync(ChalkEngine engine, PreparedQuery query)
    {
        var rows = new Dictionary<long, long>();
        await using var execution = await engine.ExecuteAsync(query, (IReadOnlyList<object?>?)null, ct: Ct);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    rows[Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(0), i))] =
                        Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(1), i));
                }
            }
        }

        return rows;
    }

    private static async Task<HashSet<long>> IdsAsync(ChalkEngine engine, PreparedQuery query)
    {
        var ids = new HashSet<long>();
        await using var execution = await engine.ExecuteAsync(query, (IReadOnlyList<object?>?)null, ct: Ct);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    ids.Add(Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(0), i)));
                }
            }
        }

        return ids;
    }

    private sealed class Desk : IAsyncDisposable
    {
        public required ChalkEngine Engine { get; init; }

        public required PocoSource Source { get; init; }

        public IReadOnlyList<Fill> CurrentFills { get; set; } = [];

        public IReadOnlyList<Book> CurrentBooks { get; set; } = [];

        /// <summary>Replaces both tables in one refresh, as a host's load does.</summary>
        public async Task LoadAsync(IReadOnlyList<Fill> fills, IReadOnlyList<Book> books)
        {
            CurrentFills = fills;
            CurrentBooks = books;
            await Engine.RefreshAsync(
                refresh =>
                {
                    refresh.Replace(Source, "fills", fills);
                    refresh.Replace(Source, "books", books);
                },
                Ct);
        }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private async Task<Desk> DeskAsync(IReadOnlyList<Fill> fills, IReadOnlyList<Book> books)
    {
        Desk? desk = null;
        var source = new PocoSourceBuilder("trading")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("fills", () => desk?.CurrentFills ?? fills)
            .AddTable("books", () => desk?.CurrentBooks ?? books)
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner() }, Ct);
        desk = new Desk { Engine = engine, Source = source, CurrentFills = fills, CurrentBooks = books };
        return desk;
    }

    private ValueTask<ChalkEngine> EngineAsync(AdoSource source) =>
        ChalkEngine.CreateAsync(new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner() }, Ct);

    /// <summary>A private in-memory SQLite with one small table, discovered rather than declared.</summary>
    private (AdoSource Source, DbConnection KeepAlive) Database()
    {
        var connectionString = $"Data Source=chalk-statistics-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        DbConnection Connect() => new SqliteConnection(connectionString);

        var keepAlive = Connect();
        keepAlive.Open();
        _open.Add(keepAlive);

        Execute(keepAlive, "CREATE TABLE widget (id INTEGER NOT NULL, name TEXT)");
        Execute(keepAlive, "INSERT INTO widget VALUES (1, 'one'), (2, 'two'), (3, 'three')");

        var source = new AdoSourceBuilder("db", Connect, "db")
            .Dialect(DialectProfiles.Sqlite)
            .Capabilities(AdoCapabilities.For(DialectProfiles.Sqlite))
            .DiscoverTables()
            .Build();

        return (source, keepAlive);
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long RowCount(ChalkEngine engine, string table) =>
        engine.Catalog.Schemas.Single(s => s.SourceId == "db").FindTable(table)!.RowCount;

    /// <summary>A sink that records each call by key, and keeps the rows it was sent.</summary>
    private sealed class Recorder : IViewSink<One, One>
    {
        public List<string> Calls { get; } = [];

        public Dictionary<long, long> Rows { get; } = [];

        public void Reset()
        {
            Calls.Add("reset");
            Rows.Clear();
        }

        public void Upsert(in One key, in One row)
        {
            Calls.Add($"upsert {key.Value}");
            Rows[key.Value] = row.Value;
        }

        public void Delete(in One key)
        {
            Calls.Add($"delete {key.Value}");
            Rows.Remove(key.Value);
        }

        public void Commit(long version, uint checksum) => Calls.Add($"commit {version}");
    }
}
