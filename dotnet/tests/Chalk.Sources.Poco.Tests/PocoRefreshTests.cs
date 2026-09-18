using Apache.Arrow;
using Chalk.Catalog;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// <c>PocoSource.RefreshAsync</c> and the release it reports (D260,
/// <c>docs/design/35-poco-refresh.md</c> §2): the <c>Func</c> registrations re-read, the new
/// snapshots built off the execution path and swapped, and the old collection handed back once its
/// last reader has gone.
/// </summary>
public sealed class PocoRefreshTests
{
    private sealed record Tick(string Symbol, int Seq, double Price);

    private static readonly Tick[] First =
    [
        new("ACME", 1, 1.5),
        new("ACME", 2, 2.5),
        new("BRIX", 3, 3.5),
        new("BRIX", 4, 4.5),
    ];

    private static readonly Tick[] Second =
    [
        new("CIRRUS", 5, 5.5),
        new("CIRRUS", 6, 6.5),
        new("DELTA", 7, 7.5),
    ];

    [Fact]
    public async Task A_refresh_re_reads_the_registration_and_swaps_the_snapshot()
    {
        IReadOnlyList<Tick> current = First;
        var source = new PocoSourceBuilder("mem")
            .AddTable("ticks", () => current, t => t.Index(x => x.Seq))
            .Build();

        Assert.Equal(4, PocoTestSupport.Describe(source, "ticks").RowCount);

        current = Second;
        await source.RefreshAsync(TestContext.Current.CancellationToken);

        var table = PocoTestSupport.Describe(source, "ticks");
        Assert.Equal(3, table.RowCount);

        // The statistics are the new rows' — the describe after a swap reports what the swap
        // brought, not what the cache held.
        Assert.Equal(3, table.Columns[1].Statistics.DistinctCount);

        var scanned = await SequencesAsync(source, batchSize: 8);
        Assert.Equal([5, 6, 7], scanned);

        // And the index was rebuilt over the new rows: the lookup answers them.
        var found = await LookupAsync(source, "ix_ticks_Seq", IndexKeyRange.Equality(6));
        Assert.Equal([6], found);
    }

    /// <summary>
    /// §1's promise, exercised: an execution that has consumed a batch keeps reading the snapshot it
    /// captured while a refresh lands, and one started afterwards reads the new one.
    /// </summary>
    [Fact]
    public async Task An_execution_in_flight_keeps_the_snapshot_it_started_with()
    {
        IReadOnlyList<Tick> current = First;
        var source = new PocoSourceBuilder("mem").AddTable("ticks", () => current).Build();

        var ct = TestContext.Current.CancellationToken;
        var request = PocoTestSupport.Request(source, "ticks", batchSize: 2);
        var enumerator = source.ScanAsync(request, PocoTestSupport.Context(), ct)
            .GetAsyncEnumerator(ct);

        var seen = new List<int>();
        Assert.True(await enumerator.MoveNextAsync());
        Read(enumerator.Current, seen);
        enumerator.Current.Dispose();

        current = Second;
        await source.RefreshAsync(ct);

        while (await enumerator.MoveNextAsync())
        {
            Read(enumerator.Current, seen);
            enumerator.Current.Dispose();
        }

        await enumerator.DisposeAsync();

        var next = await SequencesAsync(source, batchSize: 8);
        Assert.Equal([1, 2, 3, 4], seen);
        Assert.Equal([5, 6, 7], next);
    }

    /// <summary>
    /// The release: once, with the old collection, after the last reader of it has gone and not
    /// before. No clock anywhere — the scans are advanced by hand, which is what makes "not before"
    /// an assertion rather than a race.
    /// </summary>
    [Fact]
    public async Task The_release_fires_once_when_the_last_reader_of_the_old_snapshot_leaves()
    {
        IReadOnlyList<Tick> current = First;
        var source = new PocoSourceBuilder("mem").AddTable("ticks", () => current).Build();

        var released = new List<PocoSnapshotRelease>();
        source.SnapshotReleased += released.Add;

        var ct = TestContext.Current.CancellationToken;
        var one = await StartedAsync(source, ct);
        var two = await StartedAsync(source, ct);

        current = Second;
        await source.RefreshAsync(ct);
        Assert.Empty(released);

        await Drain(one);
        Assert.Empty(released);

        await Drain(two);
        var release = Assert.Single(released);
        Assert.Equal("ticks", release.Table);
        Assert.Same(First, release.Collection);

        // And never for the current one: a second refresh that reads back the same collection
        // reports nothing, and one that swaps again reports the collection it swapped out.
        await source.RefreshAsync(ct);
        Assert.Single(released);

        current = First;
        await source.RefreshAsync(ct);
        Assert.Equal(2, released.Count);
        Assert.Same(Second, released[1].Collection);
    }

    /// <summary>
    /// A table registered with a plain list keeps its snapshot: there is no new collection to build
    /// one from, and reporting the rows the table is still serving as released would be a lie.
    /// </summary>
    [Fact]
    public async Task A_plain_list_registration_keeps_its_snapshot()
    {
        var source = new PocoSourceBuilder("mem").AddTable("ticks", First).Build();

        var released = new List<PocoSnapshotRelease>();
        source.SnapshotReleased += released.Add;

        await source.RefreshAsync(TestContext.Current.CancellationToken);

        var scanned = await SequencesAsync(source, batchSize: 8);
        Assert.Empty(released);
        Assert.Equal(4, PocoTestSupport.Describe(source, "ticks").RowCount);
        Assert.Equal([1, 2, 3, 4], scanned);
    }

    /// <summary>
    /// D17 runs over every snapshot, not only the first: a replacement that breaks a declared
    /// collation is refused, and refused before anything is swapped, so the table keeps the rows it
    /// was serving and nothing is reported released.
    /// </summary>
    [Fact]
    public async Task A_refresh_that_breaks_a_declared_collation_changes_nothing()
    {
        IReadOnlyList<Tick> current = First;
        var source = new PocoSourceBuilder("mem")
            .AddTable("ticks", () => current, t => t.OrderedBy(x => x.Seq))
            .Build();

        var released = new List<PocoSnapshotRelease>();
        source.SnapshotReleased += released.Add;

        current = [.. Second.Reverse()];

        var failure = await Assert.ThrowsAsync<CatalogVerificationException>(
            async () => await source.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Contains("ticks", failure.Message, StringComparison.Ordinal);

        var scanned = await SequencesAsync(source, batchSize: 8);
        Assert.Empty(released);
        Assert.Equal(4, PocoTestSupport.Describe(source, "ticks").RowCount);
        Assert.Equal([1, 2, 3, 4], scanned);
    }

    // ---- the scaffolding ----

    private static async Task<IAsyncEnumerator<RecordBatch>> StartedAsync(
        PocoSource source, CancellationToken ct)
    {
        var request = PocoTestSupport.Request(source, "ticks", batchSize: 2);
        var enumerator = source.ScanAsync(request, PocoTestSupport.Context(), ct)
            .GetAsyncEnumerator(ct);
        Assert.True(await enumerator.MoveNextAsync());
        enumerator.Current.Dispose();
        return enumerator;
    }

    private static async Task Drain(IAsyncEnumerator<RecordBatch> enumerator)
    {
        while (await enumerator.MoveNextAsync())
        {
            enumerator.Current.Dispose();
        }

        await enumerator.DisposeAsync();
    }

    private static async Task<int[]> SequencesAsync(PocoSource source, int batchSize)
    {
        var seen = new List<int>();
        var request = PocoTestSupport.Request(source, "ticks", batchSize);
        await foreach (var batch in source.ScanAsync(
            request, PocoTestSupport.Context(), TestContext.Current.CancellationToken))
        {
            Read(batch, seen);
            batch.Dispose();
        }

        return [.. seen];
    }

    private static async Task<int[]> LookupAsync(PocoSource source, string index, IndexKeyRange range)
    {
        var descriptor = PocoTestSupport.Describe(source, "ticks");
        var projection = Enumerable.Range(0, descriptor.Columns.Count).ToArray();
        var request = new IndexLookupRequest
        {
            Table = "ticks",
            Index = index,
            Projection = projection,
            OutputSchema = ArrowTypeMapping.ToArrowSchema(descriptor, projection),
            BatchSize = 8,
            Ranges = [range],
        };

        var seen = new List<int>();
        await foreach (var batch in source.IndexLookupAsync(
            request, PocoTestSupport.Context(), TestContext.Current.CancellationToken))
        {
            Read(batch, seen);
            batch.Dispose();
        }

        return [.. seen];
    }

    private static void Read(RecordBatch batch, List<int> into)
    {
        var seq = (Int32Array)batch.Column(1);
        for (var i = 0; i < batch.Length; i++)
        {
            into.Add(seq.GetValue(i)!.Value);
        }
    }
}
