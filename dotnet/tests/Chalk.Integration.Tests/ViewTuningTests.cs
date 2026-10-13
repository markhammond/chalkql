using Apache.Arrow;
using Chalk.Arrow;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Checking a standing view against its statement, and tuning its resets (D341,
/// <c>docs/design/66-view-checks-and-tuning.md</c>): a view made from the host's prepared statement
/// agrees with it to the bit; on an engine that names a clock every query reads the clock, as of the
/// refresh its pins read; and a reader is reset at the view's own threshold.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class ViewTuningTests(SharedSidecar sidecar)
{
    private sealed record Reading(int Id, int Book, double Amount, DateTime Ts);

    private struct Key
    {
        public int Book;
    }

    private struct Row
    {
        public long Bits;
    }

    private static readonly DateTime Open = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Unspecified);

    private const string Sums = "SELECT book, SUM(amount) AS total FROM readings GROUP BY book";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- §1: a view made from a prepared statement ----

    /// <summary>
    /// Over forty random epochs of doubles appended and replaced, a view made from the host's prepared
    /// statement holds exactly what a run of that statement returns at the view's epoch, bit for bit,
    /// with the same checksum: the two share a plan, so their sums add in one order.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_view_of_a_prepared_statement_agrees_with_it_to_the_bit(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var random = new Random(66);
        var rows = Enumerable.Range(1, 200).Select(i => Random(random, i)).ToList();
        var source = Source(rows);
        await using var engine = await Engine(source, executor);
        var prepared = await engine.PrepareAsync(Sums, ct: Ct);
        await using var view = await engine.MaterializeAsync(prepared, Options(), Ct);
        var reader = new Reader(view);

        for (var epoch = 0; epoch < 40; epoch++)
        {
            if (random.Next(3) == 0)
            {
                rows = [.. Enumerable.Range(1, random.Next(50, 300)).Select(i => Random(random, i))];
                await engine.RefreshAsync(r => r.Replace(source, "readings", (IReadOnlyList<Reading>)rows), Ct);
            }
            else
            {
                Reading[] added = [.. Enumerable.Range(rows.Count + 1, random.Next(1, 30)).Select(i => Random(random, i))];
                rows.AddRange(added);
                await engine.RefreshAsync(r => r.Append(source, "readings", (IReadOnlyList<Reading>)added), Ct);
            }

            await view.UpdateAsync(Ct);
            reader.Drain();
            var run = await RunAsync(engine, prepared);
            Assert.Equal(run, reader.Rows);
            Assert.Equal(Checksum(run), view.Checksum);
        }
    }

    /// <summary>
    /// A view of a prepared statement runs on the statement's engine, and takes no prepare options of
    /// its own.
    /// </summary>
    [Fact]
    public async Task A_view_of_a_prepared_statement_refuses_another_engine_and_its_own_prepare()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var source = Source([new(1, 1, 1.5, Open)]);
        await using var engine = await Engine(source, ExecutionEngine.Vectorised);
        await using var other = await Engine(source, ExecutionEngine.Vectorised);
        var prepared = await engine.PrepareAsync(Sums, ct: Ct);

        var elsewhere = await Assert.ThrowsAsync<ArgumentException>(
            () => other.MaterializeAsync(prepared, Options(), Ct).AsTask());
        Assert.Contains("prepared on another engine", elsewhere.Message, StringComparison.Ordinal);

        var twice = await Assert.ThrowsAsync<ArgumentException>(() => engine
            .MaterializeAsync(prepared, Options(new PrepareOptions()), Ct).AsTask());
        Assert.Contains("prepared already", twice.Message, StringComparison.Ordinal);
    }

    // ---- §2: the engine's clock in every query ----

    /// <summary>
    /// On an engine that names a clock, a plain query reads the clock, not the wall clock, and agrees
    /// with a view of the same statement at its epoch, rows stamped after the clock included.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_plain_query_reads_the_engines_clock_and_agrees_with_its_view(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Reading[] rows = [new(1, 1, 1, Open), new(2, 1, 2, Open.AddMinutes(10)), new(3, 2, 4, Open.AddHours(2))];
        var source = Source(rows);
        await using var engine = await Engine(source, executor);
        await engine.RefreshAsync(r => r.AdvanceClock(At(Open.AddMinutes(30))), Ct);

        var now = Assert.IsType<DateTime>((await RowsAsync(engine, "SELECT CURRENT_TIMESTAMP(9) AS now FROM readings WHERE id = 1")).Single()[0]);
        Assert.Equal(Open.AddMinutes(30), now);

        const string Due = "SELECT book, SUM(amount) AS total FROM readings WHERE ts <= CURRENT_TIMESTAMP GROUP BY book";
        var prepared = await engine.PrepareAsync(Due, ct: Ct);
        await using var view = await engine.MaterializeAsync(prepared, Options(), Ct);
        var reader = new Reader(view);
        reader.Drain();
        Assert.Equal(await RunAsync(engine, prepared), reader.Rows);
        Assert.Equal(new Dictionary<int, long> { [1] = Bits(3) }, reader.Rows);
    }

    /// <summary>
    /// The parameter named as the clock, left unbound, is the clock; bound by the host, it is the
    /// host's value.
    /// </summary>
    [Fact]
    public async Task The_clocks_parameter_left_unbound_is_the_clock()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Reading[] rows = [new(1, 1, 1, Open), new(2, 1, 2, Open.AddMinutes(10)), new(3, 2, 4, Open.AddHours(2))];
        await using var engine = await Engine(Source(rows), ExecutionEngine.Vectorised);
        await engine.RefreshAsync(r => r.AdvanceClock(At(Open.AddMinutes(30))), Ct);
        var prepared = await engine.PrepareAsync("SELECT id FROM readings WHERE ts <= @now ORDER BY id", ct: Ct);

        Assert.Equal([1, 2], await IdsAsync(engine, prepared, new Dictionary<string, object?>()));
        Assert.Equal([1, 2, 3], await IdsAsync(engine, prepared, new Dictionary<string, object?> { ["now"] = Open.AddHours(3) }));
    }

    /// <summary>
    /// Before any refresh sets the clock, a query that reads the time is refused, naming the clock; one
    /// that does not runs.
    /// </summary>
    [Fact]
    public async Task A_query_that_reads_the_time_before_the_clock_is_set_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await Engine(Source([new(1, 1, 1, Open)]), ExecutionEngine.Vectorised);

        var refused = await Assert.ThrowsAsync<ClockRequiredException>(
            () => RowsAsync(engine, "SELECT CURRENT_TIMESTAMP(9) AS now FROM readings"));
        Assert.Equal(ChalkErrorCodes.ClockRequired, refused.Code);
        Assert.Equal("now", refused.Clock);

        Assert.Single(await RowsAsync(engine, "SELECT id FROM readings"));
    }

    /// <summary>
    /// Each transaction replaces the table with one row stamped at the clock it moves to. Executions
    /// racing them read the row and the time of one transaction, every time: the clock is read in the
    /// same hold of the gate as the pins.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_querys_time_and_tables_are_of_one_refresh(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var source = Source([new(0, 1, 0, Open)]);
        await using var engine = await Engine(source, executor);
        await engine.RefreshAsync(r => r.AdvanceClock(At(Open)), Ct);
        var prepared = await engine.PrepareAsync("SELECT ts, CURRENT_TIMESTAMP(9) AS now FROM readings", ct: Ct);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var refreshes = Task.Run(async () =>
        {
            for (var k = 1; !stop.IsCancellationRequested; k++)
            {
                var at = Open.AddSeconds(k);
                await engine.RefreshAsync(r => r
                    .Replace(source, "readings", (IReadOnlyList<Reading>)[new(k, 1, k, at)])
                    .AdvanceClock(At(at)));
            }
        });

        var torn = 0;
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[], ct: Ct);
                await foreach (var batch in execution.Batches.WithCancellation(Ct))
                {
                    using (batch)
                    {
                        foreach (var row in batch.ToRows())
                        {
                            if (!Equals(row[0], row[1]))
                            {
                                Interlocked.Increment(ref torn);
                            }
                        }
                    }
                }
            }
        })).ToArray();

        await Task.WhenAll(readers);
        await stop.CancelAsync();
        await refreshes;
        Assert.Equal(0, torn);
    }

    // ---- §3: the reset threshold ----

    /// <summary>
    /// A reader is reset once its changes reach the view's threshold, a fraction of its live rows: at
    /// a half, half the rows changing resets it; at infinity, every row changing does not.
    /// </summary>
    [Theory]
    [InlineData(0.5, 2, true)]
    [InlineData(0.5, 1, false)]
    [InlineData(1.0, 4, true)]
    [InlineData(1.0, 3, false)]
    [InlineData(double.PositiveInfinity, 4, false)]
    public async Task A_reader_is_reset_at_the_views_threshold(double resetWhen, int changed, bool reset)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        List<Reading> rows = [.. Enumerable.Range(1, 4).Select(i => new Reading(i, i, i, Open))];
        var source = Source(rows);
        await using var engine = await Engine(source, ExecutionEngine.Vectorised);
        await using var view = await engine.MaterializeAsync(Sums, Options(resetWhen: resetWhen), Ct);
        var reader = new Reader(view);
        reader.Drain();
        Assert.Equal(1, reader.Resets);

        for (var i = 0; i < changed; i++)
        {
            rows[i] = rows[i] with { Amount = 100 + i };
        }

        await engine.RefreshAsync(r => r.Replace(source, "readings", (IReadOnlyList<Reading>)[.. rows]), Ct);
        await view.UpdateAsync(Ct);
        reader.Drain();

        Assert.Equal(reset ? 2 : 1, reader.Resets);
        Assert.Equal(await RunAsync(engine, await engine.PrepareAsync(Sums, ct: Ct)), reader.Rows);
    }

    /// <summary>A threshold of none, or not a number, is refused when the view is made.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public async Task A_threshold_of_none_is_refused(double resetWhen)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await Engine(Source([new(1, 1, 1, Open)]), ExecutionEngine.Vectorised);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => engine.MaterializeAsync(Sums, Options(resetWhen: resetWhen), Ct).AsTask());
    }

    // ---- the scaffolding ----

    private static Reading Random(Random random, int id) =>
        new(id, random.Next(1, 6), random.NextDouble() * Math.Pow(10, random.Next(-3, 6)), Open);

    private static DateTimeOffset At(DateTime wall) => new(DateTime.SpecifyKind(wall, DateTimeKind.Utc));

    private static PocoSource Source(IReadOnlyList<Reading> rows) => new PocoSourceBuilder("desk")
        .NamingPolicy(PocoNamingPolicy.SnakeCase)
        .AddTable("readings", rows)
        .Build();

    private Task<ChalkEngine> Engine(PocoSource source, ExecutionEngine executor) => ChalkEngine.CreateAsync(new ChalkEngineOptions
    {
        Sources = [source],
        Planner = sidecar.CreatePlanner(),
        Clock = "now",
        Execution = new ExecutionOptions { Engine = executor },
    }, Ct).AsTask();

    private static ViewOptions<Key, Row> Options(PrepareOptions? prepare = null, double resetWhen = 1) => new()
    {
        Key = ["book"],
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Exactness = ViewExactness.EqualUpToRounding,
        ResetWhen = resetWhen,
        Prepare = prepare,
        RowHash = static (key, row) => Hash(key, row),
        Map = static (in ViewRow row, out Key key, out Row value) =>
        {
            key = new Key { Book = row.GetInt32(0) };
            value = new Row { Bits = Bits(row.GetDouble(1)) };
        },
    };

    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);

    private static uint Hash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> row)
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

    private static uint Checksum(Dictionary<int, long> rows)
    {
        var sum = 0u;
        foreach (var (book, bits) in rows)
        {
            var key = new Key { Book = book };
            var row = new Row { Bits = bits };
            sum += Hash(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<Key>(in key)),
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<Row>(in row)));
        }

        return sum;
    }

    /// <summary>The statement run plainly: each book's sum, as its bits.</summary>
    private static async Task<Dictionary<int, long>> RunAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        var sums = new Dictionary<int, long>();
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[], ct: Ct);
        await foreach (var batch in execution.Batches.WithCancellation(Ct))
        {
            using (batch)
            {
                var books = (Int32Array)batch.Column(0);
                var totals = (DoubleArray)batch.Column(1);
                for (var i = 0; i < batch.Length; i++)
                {
                    sums[books.GetValue(i)!.Value] = Bits(totals.GetValue(i)!.Value);
                }
            }
        }

        return sums;
    }

    private static async Task<List<object?[]>> RowsAsync(ChalkEngine engine, string sql)
    {
        var rows = new List<object?[]>();
        await foreach (var batch in engine.QueryAsync(sql, ct: Ct))
        {
            using (batch)
            {
                rows.AddRange(batch.ToRows());
            }
        }

        return rows;
    }

    private static async Task<List<int>> IdsAsync(
        ChalkEngine engine, PreparedQuery prepared, IReadOnlyDictionary<string, object?> values)
    {
        var ids = new List<int>();
        await using var execution = await engine.ExecuteAsync(prepared, values, ct: Ct);
        await foreach (var batch in execution.Batches.WithCancellation(Ct))
        {
            using (batch)
            {
                ids.AddRange(batch.ToRows().Select(r => (int)r[0]!));
            }
        }

        return ids;
    }

    /// <summary>A reader of the view: its rows by book, as bits, and how many resets it was sent.</summary>
    private sealed class Reader(StandingView<Key, Row> view) : IViewSink<Key, Row>
    {
        private ViewCursor _cursor = ViewCursor.Start;

        public Dictionary<int, long> Rows { get; } = [];

        public int Resets { get; private set; }

        public void Drain() => Assert.Equal(DrainOutcome.Complete, view.Drain(ref _cursor, this));

        public void Reset()
        {
            Resets++;
            Rows.Clear();
        }

        public void Upsert(in Key key, in Row row) => Rows[key.Book] = row.Bits;

        public void Delete(in Key key) => Rows.Remove(key.Book);

        public void Commit(long version, uint checksum)
        {
        }
    }
}
