using System.Buffers;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using Chalk.Tests;

namespace Chalk.Integration.Tests;

/// <summary>
/// A replacement prepared over an array the host hands over (F245): the rewrite is not copied, the rows
/// appended since the cut are written into the array's room, and the array goes back once no snapshot
/// reads it, to the pool the table was declared with or, without one, through
/// <see cref="PocoSource.SnapshotReleased"/>. The arrays a table grows into come from its pool too, so
/// a compaction, and the appends and keyed changes after it, allocate no row arrays once warm.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class OwnedReplacementTests(SharedSidecar sidecar, ITestOutputHelper output)
{
    private sealed record Tick(int Seq, int Book, long Qty, DateTime Ts);

    private sealed record Price(int Id, long Px);

    private static readonly DateTime Open = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Tick[] Ticks(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => new Tick(i, i % 4, i * 10L, Open.AddSeconds(i)))];

    /// <summary>The host's compaction: each book's last row, in time order.</summary>
    private static Tick[] LastOfEach(IEnumerable<Tick> rows) =>
        [.. rows.GroupBy(t => t.Book).Select(g => g.Last()).OrderBy(t => t.Ts)];

    /// <summary>
    /// The table reads the rewrite from the host's array and the rows since the cut after it, written
    /// into the array's room: the commit rents nothing, and the host's array stays out.
    /// </summary>
    [Fact]
    public async Task The_rewrite_is_read_from_the_hosts_array_and_the_rows_since_the_cut_follow()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var pool = new CountingPool<Tick>();
        var source = Desk(Ticks(1, 40), pool);
        await using var engine = await EngineAsync(source);
        var rewrite = LastOfEach(Ticks(1, 30));
        var array = HandOver(pool, rewrite, room: 32);

        using var prepared = await engine.PrepareReplaceAsync(source, "ticks", array, rewrite.Length, through: 30, Ct);
        await engine.RefreshAsync(r => r.Append(source, "ticks", Ticks(41, 5)), Ct);
        var rented = pool.Rented;
        await engine.RefreshAsync(r => r.Commit(prepared), Ct);

        Tick[] expected = [.. rewrite, .. Ticks(31, 15)];
        Assert.Equal(
            expected.Select(t => $"{t.Seq}|{t.Book}|{t.Qty}"),
            await RowsAsync(engine, "SELECT seq, book, qty FROM ticks ORDER BY seq"));
        Assert.Equal(rented, pool.Rented);
        Assert.False(pool.WasReturned(array));
    }

    /// <summary>
    /// A reader that began before the next compaction keeps the host's array from going back; it goes
    /// back to the table's pool when the reader finishes, and not before.
    /// </summary>
    [Fact]
    public async Task The_hosts_array_goes_back_to_its_pool_once_no_snapshot_reads_it()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var pool = new CountingPool<Tick>();
        var source = Desk(Ticks(1, 40), pool);
        await using var engine = await EngineAsync(source);
        var first = HandOver(pool, LastOfEach(Ticks(1, 40)), room: 32);
        using (var prepared = await engine.PrepareReplaceAsync(source, "ticks", first, 4, through: 40, Ct))
        {
            await engine.RefreshAsync(r => r.Commit(prepared), Ct);
        }

        // A reader of the table as it stands: its first batch read, the rest not.
        var statement = await engine.PrepareAsync("SELECT seq FROM ticks", ct: Ct);
        var reading = await engine.ExecuteAsync(statement, (IReadOnlyList<object?>?)null, ct: Ct);
        var batches = reading.Batches.GetAsyncEnumerator(Ct);
        Assert.True(await batches.MoveNextAsync());
        batches.Current.Dispose();

        await engine.RefreshAsync(r => r.Append(source, "ticks", Ticks(41, 2)), Ct);
        var second = HandOver(pool, LastOfEach(Ticks(1, 42)), room: 32);
        using (var prepared = await engine.PrepareReplaceAsync(source, "ticks", second, 4, through: 6, Ct))
        {
            await engine.RefreshAsync(r => r.Commit(prepared), Ct);
        }

        Assert.False(pool.WasReturned(first), "the first array went back while a reader still read it");

        await batches.DisposeAsync();
        await reading.DisposeAsync();

        Assert.True(pool.WasReturned(first), "the first array did not go back once nothing read it");
        Assert.False(pool.WasReturned(second));
    }

    /// <summary>Without a pool, the host's array is reported released, as a list a <c>Replace</c> took over is.</summary>
    [Fact]
    public async Task Without_a_pool_the_hosts_array_is_reported_released()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var source = Desk(Ticks(1, 40), pool: null);
        var released = new List<object>();
        source.SnapshotReleased += release => released.Add(release.Collection);
        await using var engine = await EngineAsync(source);
        var array = new Tick[64];
        LastOfEach(Ticks(1, 40)).CopyTo(array, 0);

        using (var prepared = await engine.PrepareReplaceAsync(source, "ticks", array, 4, through: 40, Ct))
        {
            await engine.RefreshAsync(r => r.Commit(prepared), Ct);
        }

        Assert.DoesNotContain(array, released);
        await engine.RefreshAsync(r => r.Replace(source, "ticks", Ticks(100, 3)), Ct);

        Assert.Contains(array, released);
    }

    /// <summary>A replacement disposed before a refresh commits it gives its array back at once.</summary>
    [Fact]
    public async Task A_replacement_disposed_before_its_commit_gives_its_array_back()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var pool = new CountingPool<Tick>();
        var source = Desk(Ticks(1, 40), pool);
        await using var engine = await EngineAsync(source);
        var array = HandOver(pool, LastOfEach(Ticks(1, 40)), room: 32);

        var prepared = await engine.PrepareReplaceAsync(source, "ticks", array, 4, through: 40, Ct);
        Assert.False(pool.WasReturned(array));
        prepared.Dispose();

        Assert.True(pool.WasReturned(array));
    }

    /// <summary>
    /// A prepare that is refused takes nothing: a count past the array, or a rewrite out of the table's
    /// declared order, leaves the array the host's, neither pooled nor reported.
    /// </summary>
    [Fact]
    public async Task A_refused_prepare_takes_nothing()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var pool = new CountingPool<Tick>();
        var source = Desk(Ticks(1, 40), pool);
        await using var engine = await EngineAsync(source);
        var array = HandOver(pool, LastOfEach(Ticks(1, 40)), room: 4);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await engine.PrepareReplaceAsync(source, "ticks", array, array.Length + 1, through: 40, Ct));
        (array[0], array[1]) = (array[1], array[0]);
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await engine.PrepareReplaceAsync(source, "ticks", array, 4, through: 40, Ct));

        Assert.False(pool.WasReturned(array));
        Assert.Equal(0, pool.Returned);
    }

    /// <summary>
    /// A host that rents its compactions' arrays from the table's pool, and hands them over, has them
    /// back in it: a warm round of three appends and a compaction of 20,000 rows allocates less than a
    /// quarter of one of the table's row arrays, where the copy alone was two.
    /// </summary>
    [Fact]
    public async Task Compactions_and_appends_with_the_tables_pool_allocate_no_row_arrays()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        const int Rows = 20_000;
        const int Rounds = 16;
        var pool = new CountingPool<Tick>();

        // Every row made before anything is measured: the host's own work is not the engine's.
        var all = Ticks(1, Rows + (30 * Rounds));
        Tick[][] batches = [.. Enumerable.Range(0, 3 * Rounds).Select(k => all[(Rows + (10 * k))..(Rows + (10 * k) + 10)])];
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("ticks", all[..Rows], t =>
            {
                t.AppendOnly(x => x.Ts);
                t.ArrayPool(pool);
            })
            .Build();
        await using var engine = await EngineAsync(source);
        var appended = 0;

        // Three appends, then a compaction that keeps the table's newest rows, in an array the host rents.
        async Task<int> RoundAsync()
        {
            for (var i = 0; i < 3; i++)
            {
                var batch = batches[appended++];
                await engine.RefreshAsync(r => r.Append(source, "ticks", batch).DeferPlanning(), Ct);
            }

            var array = pool.Rent(Rows + 64);
            Array.Copy(all, 10 * appended, array, 0, Rows);
            using var prepared = await engine.PrepareReplaceAsync(source, "ticks", array, Rows, through: Rows + 30, Ct);
            await engine.RefreshAsync(r => r.Commit(prepared).DeferPlanning(), Ct);
            return appended;
        }

        for (var warm = 0; warm < 3; warm++)
        {
            await RoundAsync();
        }

        var (bytes, _) = await AllocationProbe.SteadyStateAsync(RoundAsync, samples: 4);
        var array = (Rows + 64) * (long)IntPtr.Size;
        output.WriteLine($"a warm round allocated {bytes} bytes; one row array is {array}; {pool.Outstanding} arrays out of the pool");
        Assert.True(bytes < array / 4, $"a warm round allocated {bytes} bytes, against {array} for one row array");
        Assert.True(pool.Outstanding <= 3, $"{pool.Outstanding} arrays are out of the pool");
    }

    /// <summary>
    /// A keyed table's change writes its next rows into an array from the table's pool (F243): a warm
    /// upsert of five rows of 20,000 allocates less than a tenth of the table's row array.
    /// </summary>
    [Fact]
    public async Task A_keyed_change_takes_its_array_from_the_tables_pool()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        const int Rows = 20_000;
        var pool = new CountingPool<Price>();
        var source = new PocoSourceBuilder("market")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("prices", (IReadOnlyList<Price>)[.. Enumerable.Range(1, Rows).Select(i => new Price(i, i))], t =>
            {
                t.Keyed(x => x.Id);
                t.ArrayPool(pool);
            })
            .Build();
        await using var engine = await EngineAsync(source);
        IReadOnlyList<Price>[] changes = [.. Enumerable.Range(1, 16).Select(
            round => (IReadOnlyList<Price>)[.. Enumerable.Range(1, 5).Select(i => new Price((i * 997 % Rows) + 1, round))])];
        var round = 0;

        async Task<int> UpsertAsync()
        {
            var changed = changes[round++];
            await engine.RefreshAsync(r => r.Upsert(source, "prices", changed).DeferPlanning(), Ct);
            return round;
        }

        for (var warm = 0; warm < 5; warm++)
        {
            await UpsertAsync();
        }

        var (bytes, _) = await AllocationProbe.SteadyStateAsync(UpsertAsync, samples: 4);
        var array = Rows * (long)IntPtr.Size;
        output.WriteLine($"a warm upsert allocated {bytes} bytes; the table's row array is {array}");
        Assert.True(bytes < array / 10, $"a warm upsert allocated {bytes} bytes, against {array} for the row array");
    }

    /// <summary>
    /// Without a pool declared, a keyed table keeps one of its own: a warm upsert still allocates less
    /// than a tenth of the table's row array, where every change used to copy it whole.
    /// </summary>
    [Fact]
    public async Task A_keyed_change_takes_its_array_from_the_tables_own_pool_by_default()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        const int Rows = 20_000;
        var source = new PocoSourceBuilder("market")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("prices", (IReadOnlyList<Price>)[.. Enumerable.Range(1, Rows).Select(i => new Price(i, i))], t => t.Keyed(x => x.Id))
            .Build();
        await using var engine = await EngineAsync(source);
        IReadOnlyList<Price>[] changes = [.. Enumerable.Range(1, 16).Select(
            round => (IReadOnlyList<Price>)[.. Enumerable.Range(1, 5).Select(i => new Price((i * 997 % Rows) + 1, round))])];
        var round = 0;

        async Task<int> UpsertAsync()
        {
            var changed = changes[round++];
            await engine.RefreshAsync(r => r.Upsert(source, "prices", changed).DeferPlanning(), Ct);
            return round;
        }

        for (var warm = 0; warm < 5; warm++)
        {
            await UpsertAsync();
        }

        var (bytes, _) = await AllocationProbe.SteadyStateAsync(UpsertAsync, samples: 4);
        var array = Rows * (long)IntPtr.Size;
        output.WriteLine($"a warm upsert allocated {bytes} bytes; the table's row array is {array}");
        Assert.True(bytes < array / 10, $"a warm upsert allocated {bytes} bytes, against {array} for the row array");
    }

    // ---- the parts ----

    /// <summary>The rows in an array rented from <paramref name="pool"/>, with room for <paramref name="room"/> more.</summary>
    private static Tick[] HandOver(CountingPool<Tick> pool, Tick[] rows, int room)
    {
        var array = pool.Rent(rows.Length + room);
        rows.CopyTo(array, 0);
        return array;
    }

    private static PocoSource Desk(IReadOnlyList<Tick> rows, ArrayPool<Tick>? pool) => new PocoSourceBuilder("desk")
        .NamingPolicy(PocoNamingPolicy.SnakeCase)
        .AddTable("ticks", rows, t =>
        {
            t.AppendOnly(x => x.Ts).Index(x => x.Seq);
            if (pool is not null)
            {
                t.ArrayPool(pool);
            }
        })
        .Build();

    private async Task<ChalkEngine> EngineAsync(PocoSource source) =>
        await ChalkEngine.CreateAsync(
            new ChalkEngineOptions
            {
                Sources = [source],
                Planner = sidecar.CreatePlanner(),
                Execution = new ExecutionOptions { BatchSize = 8 },
            },
            Ct);

    private static async Task<List<string>> RowsAsync(ChalkEngine engine, string sql)
    {
        var rows = new List<string>();
        await foreach (var batch in engine.QueryAsync(sql, ct: Ct))
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

    /// <summary>A pool that counts what it rents and what it is given back, and keeps which arrays came back.</summary>
    private sealed class CountingPool<T> : ArrayPool<T>
    {
        private readonly ArrayPool<T> _inner = Create(1 << 24, 8);
        private readonly HashSet<T[]> _out = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<T[]> _back = new(ReferenceEqualityComparer.Instance);
        private readonly Lock _gate = new();

        public int Rented { get; private set; }

        public int Returned { get; private set; }

        public int Outstanding
        {
            get
            {
                lock (_gate)
                {
                    return _out.Count;
                }
            }
        }

        public bool WasReturned(T[] array)
        {
            lock (_gate)
            {
                return _back.Contains(array);
            }
        }

        public override T[] Rent(int minimumLength)
        {
            var array = _inner.Rent(minimumLength);
            lock (_gate)
            {
                Rented++;
                _out.Add(array);
                _back.Remove(array);
            }

            return array;
        }

        public override void Return(T[] array, bool clearArray = false)
        {
            lock (_gate)
            {
                Returned++;
                _out.Remove(array);
                _back.Add(array);
            }

            _inner.Return(array, clearArray);
        }
    }
}
