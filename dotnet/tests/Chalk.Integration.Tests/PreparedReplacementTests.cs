using System.Data.Common;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using Microsoft.Data.Sqlite;

namespace Chalk.Integration.Tests;

/// <summary>
/// A replacement prepared before the refresh that commits it, through the engine (design 69 §3): the
/// build takes no refresh lock, so appends commit while it runs; the commit appends the rows the
/// table took since the cut and publishes the result in the refresh's epoch, which executions see
/// whole or not at all.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class PreparedReplacementTests(SharedSidecar sidecar)
{
    private sealed record Tick(int Seq, int Book, long Qty, DateTime Ts);

    private sealed record Left(int Id, int V);

    private sealed record Right(int Id, int W);

    private static readonly DateTime Open = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<Tick> Ticks(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => new Tick(i, i % 4, i * 10L, Open.AddSeconds(i)))];

    /// <summary>Rows <paramref name="from"/> on, ten to a book.</summary>
    private static IReadOnlyList<Tick> Decades(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => new Tick(i, i / 10, i * 10L, Open.AddSeconds(i)))];

    /// <summary>The host's compaction: each book's last row, in time order.</summary>
    private static IReadOnlyList<Tick> LastOfEach(IEnumerable<Tick> rows) =>
        [.. rows.GroupBy(t => t.Book).Select(g => g.Last()).OrderBy(t => t.Ts)];

    /// <summary>
    /// After the commit the table reads as the rewrite followed by every row since the cut: those it
    /// held when the replacement was prepared and those appended after.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_commit_reads_as_the_rewrite_followed_by_the_rows_since_the_cut(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;
        var source = Desk(Ticks(1, 40));
        await using var engine = await EngineAsync([source], executor);
        var rewrite = LastOfEach(Ticks(1, 30));

        using var prepared = await engine.PrepareReplaceAsync(source, "ticks", rewrite, through: 30, ct);
        await engine.RefreshAsync(r => r.Append(source, "ticks", Ticks(41, 5)), ct);
        var before = engine.Catalog.Epoch;
        var epoch = await engine.RefreshAsync(r => r.Commit(prepared), ct);

        Assert.Equal(before + 1, epoch);
        Assert.False(prepared.Pending);
        IReadOnlyList<Tick> expected = [.. rewrite, .. Ticks(31, 15)];
        Assert.Equal(
            expected.Select(t => $"{t.Seq}|{t.Book}|{t.Qty}"),
            await RowsAsync(engine, "SELECT seq, book, qty FROM ticks ORDER BY seq"));
        Assert.Equal(
            expected.GroupBy(t => t.Book).OrderBy(g => g.Key).Select(g => $"{g.Key}|{g.Count()}|{g.Sum(t => t.Qty)}"),
            await RowsAsync(engine, "SELECT book, COUNT(*), SUM(qty) FROM ticks GROUP BY book ORDER BY book"));
    }

    /// <summary>
    /// The engine takes no lock to prepare: a refresh appending to the same table commits while the
    /// build is held part way, and the commit then carries those rows over.
    /// </summary>
    [Fact]
    public async Task An_append_commits_while_the_engine_prepares_a_replacement()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("gated", Gates(1, 20), t => t.Index(x => x.Seq))
            .Build();
        await using var engine = await EngineAsync([source], ExecutionEngine.Vectorised);
        using var hold = new Hold();

        var preparing = Task.Run(
            () => engine.PrepareReplaceAsync(
                source, "gated", (IReadOnlyList<Gated>)[new Gated(1, hold), new Gated(2, hold)], through: 20, ct).AsTask(),
            ct);
        Assert.True(hold.Entered.Wait(TimeSpan.FromSeconds(30), ct), "the prepare never reached its build");

        await engine.RefreshAsync(r => r.Append(source, "gated", Gates(21, 3)), ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.False(preparing.IsCompleted);
        Assert.Equal(23, await CountAsync(engine, "SELECT COUNT(*) FROM gated"));

        hold.Released.Set();
        using var prepared = await preparing;
        await engine.RefreshAsync(r => r.Commit(prepared), ct);

        var seqs = await RowsAsync(engine, "SELECT seq FROM gated ORDER BY seq");
        Assert.Equal(["1", "2", "21", "22", "23"], seqs);
    }

    /// <summary>
    /// A join running when the commit lands reads neither the commit nor the other table the same
    /// refresh replaced; the next reads both (F188).
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_join_sees_a_commit_whole_or_not_at_all(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;
        var tables = new PocoSourceBuilder("pair")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("lefts", Lefts(1))
            .AddTable("rights", Rights(1))
            .AddFunction("trip", f => f.Scalar<int, bool>("id").Strict().Client())
            .Build();

        ChalkEngine? engine = null;
        PreparedReplacement? prepared = null;
        var tripped = 0;
        engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions
            {
                Sources = [tables],
                Planner = sidecar.CreatePlanner(),
                Execution = new ExecutionOptions { Engine = executor, BatchSize = 8 },
                Functions = registry => registry.AddScalar<int, bool>("trip", _ =>
                {
                    if (Interlocked.Exchange(ref tripped, 1) == 0)
                    {
                        engine!.RefreshAsync(r => r.Commit(prepared!).Replace(tables, "rights", Rights(2)))
                            .AsTask().GetAwaiter().GetResult();
                    }

                    return true;
                }),
            },
            ct);
        await using var _ = engine;
        using var replacement = await engine.PrepareReplaceAsync(tables, "lefts", Lefts(2), through: 50, ct);
        prepared = replacement;

        const string Joined =
            "SELECT l.v, r.w FROM lefts l JOIN rights r ON l.id = r.id WHERE trip(l.id) AND trip(r.id)";
        var during = await RowsAsync(engine, Joined);
        Assert.Equal(1, tripped);
        Assert.Equal(50, during.Count);
        Assert.All(during, row => Assert.Equal("1|1", row));

        var after = await RowsAsync(engine, Joined);
        Assert.All(after, row => Assert.Equal("2|2", row));
    }

    /// <summary>
    /// What the engine refuses before anything is built: a source it was not built with, a source
    /// that cannot prepare, and a negative cut; and at the refresh, a replacement already committed or
    /// disposed.
    /// </summary>
    [Fact]
    public async Task The_engine_refuses_what_it_cannot_prepare_or_commit()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;
        var source = Desk(Ticks(1, 10));
        var (database, keepAlive) = Database();
        using var _ = keepAlive;
        await using var engine = await EngineAsync([source, database], ExecutionEngine.Vectorised);

        var stranger = Desk(Ticks(1, 10));
        var notServed = await Assert.ThrowsAsync<ArgumentException>(
            async () => await engine.PrepareReplaceAsync(stranger, "ticks", Ticks(1, 2), through: 10, ct));
        Assert.Contains("is not one this engine was built with", notServed.Message, StringComparison.Ordinal);

        var cannot = await Assert.ThrowsAsync<ArgumentException>(
            async () => await engine.PrepareReplaceAsync(database, "widget", (IReadOnlyList<Tick>)[], through: 0, ct));
        Assert.Contains("cannot prepare a replacement before a refresh", cannot.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await engine.PrepareReplaceAsync(source, "ticks", Ticks(1, 2), through: -1, ct));

        var prepared = await engine.PrepareReplaceAsync(source, "ticks", LastOfEach(Ticks(1, 10)), through: 10, ct);
        await engine.RefreshAsync(r => r.Commit(prepared), ct);
        var again = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await engine.RefreshAsync(r => r.Commit(prepared), ct));
        Assert.Contains("has already been committed or disposed", again.Message, StringComparison.Ordinal);

        var dropped = await engine.PrepareReplaceAsync(source, "ticks", Ticks(1, 2), through: 4, ct);
        dropped.Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await engine.RefreshAsync(r => r.Commit(dropped), ct));
        Assert.Equal(LastOfEach(Ticks(1, 10)).Count, await CountAsync(engine, "SELECT COUNT(*) FROM ticks"));
    }

    /// <summary>
    /// A standing view over the table follows the commit as it follows any refresh: its readers keep
    /// their place and hear only the keys whose rows changed, here two books of five, and the checksum
    /// it commits is the sum of their replica's rows.
    /// </summary>
    [Fact]
    public async Task A_standing_view_follows_a_commit()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;
        var source = Desk(Decades(1, 40));
        await using var engine = await EngineAsync([source], ExecutionEngine.Vectorised);
        const string Counts = "SELECT book, COUNT(*) AS n FROM ticks GROUP BY book";
        await using var view = await engine.MaterializeAsync(Counts, ViewOptions());

        var sink = new Replica();
        var cursor = ViewCursor.Start;
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        sink.Calls.Clear();

        // Books 0 and 1 lie wholly before the cut and keep one row each; book 2 straddles it and
        // keeps its count.
        using var prepared = await engine.PrepareReplaceAsync(source, "ticks", LastOfEach(Decades(1, 20)), through: 20, ct);
        await engine.RefreshAsync(r => r.Commit(prepared), ct);
        await view.UpdateAsync();
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));

        Assert.DoesNotContain("reset", sink.Calls);
        Assert.Equal(2, sink.Calls.Count(call => call.StartsWith("upsert", StringComparison.Ordinal)));
        Assert.Equal(new Dictionary<long, long> { [0] = 1, [1] = 1, [2] = 10, [3] = 10, [4] = 1 }, sink.Rows);
        Assert.Equal(sink.Checksum(), sink.CommittedChecksum);
        Assert.Equal(view.Checksum, sink.CommittedChecksum);
    }

    // ---- the scaffolding ----

    private static PocoSource Desk(IReadOnlyList<Tick> rows) => new PocoSourceBuilder("desk")
        .NamingPolicy(PocoNamingPolicy.SnakeCase)
        .AddTable("ticks", rows, t => t.AppendOnly(x => x.Ts).Index(x => x.Seq))
        .Build();

    private static IReadOnlyList<Left> Lefts(int value) => [.. Enumerable.Range(1, 50).Select(i => new Left(i, value))];

    private static IReadOnlyList<Right> Rights(int value) => [.. Enumerable.Range(1, 50).Select(i => new Right(i, value))];

    private async Task<ChalkEngine> EngineAsync(IReadOnlyList<Chalk.Sources.ISourceRuntime> sources, ExecutionEngine executor) =>
        await ChalkEngine.CreateAsync(
            new ChalkEngineOptions
            {
                Sources = sources,
                Planner = sidecar.CreatePlanner(),
                Execution = new ExecutionOptions { Engine = executor, BatchSize = 8 },
            },
            TestContext.Current.CancellationToken);

    /// <summary>A private in-memory SQLite with one table: a source that refreshes by changing the database.</summary>
    private static (AdoSource Source, DbConnection KeepAlive) Database()
    {
        var connectionString = $"Data Source=chalk-prepared-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        DbConnection Connect() => new SqliteConnection(connectionString);

        var keepAlive = Connect();
        keepAlive.Open();
        using (var command = keepAlive.CreateCommand())
        {
            command.CommandText = "CREATE TABLE widget (id INTEGER NOT NULL, name TEXT)";
            command.ExecuteNonQuery();
        }

        var source = new AdoSourceBuilder("db", Connect, "db")
            .Dialect(DialectProfiles.Sqlite)
            .Capabilities(AdoCapabilities.For(DialectProfiles.Sqlite))
            .DiscoverTables()
            .Build();
        return (source, keepAlive);
    }

    private static async Task<List<string>> RowsAsync(ChalkEngine engine, string sql)
    {
        var rows = new List<string>();
        await foreach (var batch in engine.QueryAsync(sql, ct: TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    rows.Add(string.Join(
                        "|",
                        Enumerable.Range(0, batch.ColumnCount)
                            .Select(c => Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(c), i))));
                }
            }
        }

        return rows;
    }

    private static async Task<long> CountAsync(ChalkEngine engine, string sql) =>
        long.Parse((await RowsAsync(engine, sql)).Single(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A row whose key read waits on a hold, so a test can stop a build part way.</summary>
    private sealed class Gated(int seq, Hold? hold = null)
    {
        [ChalkIgnore]
        public Hold? Hold { get; } = hold;

        public int Seq
        {
            get
            {
                Hold?.Pass();
                return seq;
            }
        }
    }

    private sealed class Hold : IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Released { get; } = new();

        public void Pass()
        {
            Entered.Set();
            Released.Wait(TimeSpan.FromSeconds(30));
        }

        public void Dispose()
        {
            Released.Set();
            Entered.Dispose();
            Released.Dispose();
        }
    }

    private static IReadOnlyList<Gated> Gates(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => new Gated(i))];

    private struct BookKey
    {
        public long Book;
    }

    private struct BookRow
    {
        public long N;
    }

    private static ViewOptions<BookKey, BookRow> ViewOptions() => new()
    {
        Key = ["book"],
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Map = static (in ViewRow row, out BookKey key, out BookRow value) =>
        {
            key = new BookKey { Book = row.GetInt64(0) };
            value = new BookRow { N = row.GetInt64(1) };
        },
        RowHash = Fnv,
    };

    private static uint Fnv(ReadOnlySpan<byte> key, ReadOnlySpan<byte> row)
    {
        var hash = 2166136261u;
        foreach (var b in key)
        {
            hash = (hash ^ b) * 16777619u;
        }

        foreach (var b in row)
        {
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }

    /// <summary>A sink that records each call and keeps a replica of the rows, as a host's would.</summary>
    private sealed class Replica : IViewSink<BookKey, BookRow>
    {
        public List<string> Calls { get; } = [];

        public Dictionary<long, long> Rows { get; } = [];

        public uint CommittedChecksum { get; private set; }

        public void Reset()
        {
            Calls.Add("reset");
            Rows.Clear();
        }

        public void Upsert(in BookKey key, in BookRow row)
        {
            Calls.Add($"upsert {key.Book}={row.N}");
            Rows[key.Book] = row.N;
        }

        public void Delete(in BookKey key)
        {
            Calls.Add($"delete {key.Book}");
            Rows.Remove(key.Book);
        }

        public void Commit(long version, uint checksum)
        {
            Calls.Add($"commit {version}");
            CommittedChecksum = checksum;
        }

        public uint Checksum()
        {
            var sum = 0u;
            foreach (var (book, n) in Rows)
            {
                var key = new BookKey { Book = book };
                var row = new BookRow { N = n };
                sum += Fnv(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<BookKey>(in key)),
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<BookRow>(in row)));
            }

            return sum;
        }
    }
}
