using Apache.Arrow;
using Chalk.Tests;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// <c>Append</c>, the one incremental operation (D260, <c>docs/design/35-poco-refresh.md</c> §3):
/// the columns, the permutations and the clustered copies extended rather than rebuilt, a snapshot
/// become a row count, and every lookup answering what a table rebuilt from the appended list
/// answers.
/// </summary>
public sealed class PocoAppendTests
{
    /// <summary>
    /// A nullable column and a nullable string among them on purpose: an append has to join two
    /// validity bitmaps at a bit that is not a byte boundary, and two string columns whose lanes
    /// name variadic buffers by index.
    /// </summary>
    private sealed record Tick(int Seq, string Symbol, double Price, string? Sector, int? Lot);

    private static IReadOnlyList<Tick> Ordered(int from, int count) =>
    [
        .. Enumerable.Range(from, count).Select(i => new Tick(
            i,
            Symbols[i % Symbols.Length],
            i + 0.5,
            i % 3 == 0 ? null : $"sector-{i % 5}-with-a-long-enough-name",
            i % 4 == 0 ? null : i * 7)),
    ];

    private static readonly string[] Symbols = ["ACME", "BRIX", "CIRRUS", "DELTA"];

    /// <summary>
    /// Keys appended in order extend the permutation without a sort, and the table answers exactly
    /// what one built from the whole list answers.
    /// </summary>
    [Fact]
    public async Task An_append_in_key_order_extends_the_permutation_without_a_sort()
    {
        var source = Source(Ordered(1, 6), t => t.Index(x => x.Seq));
        await AppendAsync(source, Ordered(7, 4));

        var index = (PermutationIndex<Tick>)source.FindIndex<Tick>("ticks", "ix_ticks_Seq")!;
        Assert.True(index.ExtendedInOrder);

        Assert.Equal(10, PocoTestSupport.Describe(source, "ticks").RowCount);
        Assert.Equal(10, index.DistinctCount(1));

        await AssertAnswersMatchAsync(source, Source(Ordered(1, 10), t => t.Index(x => x.Seq)));
    }

    /// <summary>
    /// Keys that arrive out of order are sorted as a tail and merged with the permutation that was
    /// already there; the answer is the same, which is the only thing the reader may notice.
    /// </summary>
    [Fact]
    public async Task An_append_out_of_key_order_sorts_the_tail_and_merges()
    {
        var source = Source(Ordered(5, 6), t => t.Index(x => x.Seq));
        await AppendAsync(source, Ordered(1, 4));

        var index = (PermutationIndex<Tick>)source.FindIndex<Tick>("ticks", "ix_ticks_Seq")!;
        Assert.False(index.ExtendedInOrder);

        var rebuilt = Source([.. Ordered(5, 6), .. Ordered(1, 4)], t => t.Index(x => x.Seq));
        await AssertAnswersMatchAsync(source, rebuilt);
    }

    /// <summary>
    /// An index a declared collation backs costs nothing to extend at all: the rows are in key order
    /// because the table says so, and the appended ones keep it — which the D17 verification is what
    /// confirms.
    /// </summary>
    [Fact]
    public async Task An_append_to_a_collation_backed_index_keeps_costing_nothing()
    {
        var source = Source(Ordered(1, 6), t => t.OrderedBy(x => x.Seq).Index(x => x.Seq));
        await AppendAsync(source, Ordered(7, 4));

        var index = (PermutationIndex<Tick>)source.FindIndex<Tick>("ticks", "ix_ticks_Seq")!;
        Assert.True(index.IsCollationBacked);
        Assert.Equal(0, index.BytesPerRow);

        await AssertAnswersMatchAsync(
            source, Source(Ordered(1, 10), t => t.OrderedBy(x => x.Seq).Index(x => x.Seq)));
    }

    /// <summary>
    /// An append that breaks a declared collation is refused, and refused before anything is
    /// published: the table keeps the rows it was serving.
    /// </summary>
    [Fact]
    public async Task An_append_that_breaks_a_declared_collation_changes_nothing()
    {
        var source = Source(Ordered(5, 6), t => t.OrderedBy(x => x.Seq));

        var failure = await Assert.ThrowsAsync<Chalk.Catalog.CatalogVerificationException>(
            async () => await AppendAsync(source, Ordered(1, 4)));
        Assert.Contains("goes backwards", failure.Message, StringComparison.Ordinal);

        Assert.Equal(6, PocoTestSupport.Describe(source, "ticks").RowCount);
    }

    /// <summary>
    /// The clustered copy extends with the permutation, and every covered lookup answers what a
    /// rebuilt copy answers — down to the strings, whose data buffer and lanes are what an append
    /// has to join correctly.
    /// </summary>
    [Fact]
    public async Task An_append_extends_the_clustered_copy()
    {
        var source = Source(
            Ordered(1, 6),
            t => t.ClusteredIndex(x => x.Seq).Covering(x => x.Symbol!, x => x.Sector!, x => x.Lot!));
        await AppendAsync(source, Ordered(7, 4));

        var index = (ClusteredIndex<Tick>)source.FindIndex<Tick>("ticks", "ix_ticks_Seq")!;
        Assert.True(index.ExtendedInOrder);

        var rebuilt = Source(
            Ordered(1, 10),
            t => t.ClusteredIndex(x => x.Seq).Covering(x => x.Symbol!, x => x.Sector!, x => x.Lot!));
        await AssertAnswersMatchAsync(source, rebuilt, [0, 1, 3, 4]);
    }

    /// <summary>
    /// One append after another over the same table: the array grows amortised and every snapshot is
    /// a longer view of it, so what a reader sees is still exactly a rebuilt table's answer.
    /// </summary>
    [Fact]
    public async Task Appends_accumulate()
    {
        var source = Source(Ordered(1, 3), t => t.Index(x => x.Seq));
        for (var next = 4; next <= 22; next += 3)
        {
            await AppendAsync(source, Ordered(next, 3));
        }

        Assert.Equal(24, PocoTestSupport.Describe(source, "ticks").RowCount);
        await AssertAnswersMatchAsync(source, Source(Ordered(1, 24), t => t.Index(x => x.Seq)));
    }

    /// <summary>
    /// A snapshot is a row count: a scan that has started sees the rows it started with, and a scan
    /// started after the append sees them all.
    /// </summary>
    [Fact]
    public async Task A_reader_keeps_its_prefix_and_the_next_reader_sees_more()
    {
        var source = Source(Ordered(1, 6), t => t.Index(x => x.Seq));

        var ct = TestContext.Current.CancellationToken;
        var request = PocoTestSupport.Request(source, "ticks", batchSize: 2);
        var enumerator = source.ScanAsync(request, PocoTestSupport.Context(), ct)
            .GetAsyncEnumerator(ct);

        var seen = new List<int>();
        Assert.True(await enumerator.MoveNextAsync());
        Read(enumerator.Current, seen);
        enumerator.Current.Dispose();

        await AppendAsync(source, Ordered(7, 4));

        while (await enumerator.MoveNextAsync())
        {
            Read(enumerator.Current, seen);
            enumerator.Current.Dispose();
        }

        await enumerator.DisposeAsync();

        var after = await ScanAsync(source);
        Assert.Equal([1, 2, 3, 4, 5, 6], seen);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], after);
    }

    /// <summary>
    /// The gate, over a table a refresh has moved: a scan of rows that were replaced and then
    /// appended costs nothing per row, exactly as a scan of the collection they were registered with
    /// does. Measured as a slope the way <c>PocoUtf8Tests</c> measures its column — the same number
    /// of batches over three times the rows, so what is left is the per-row cost and not the
    /// per-batch fixed one.
    /// </summary>
    [Fact]
    public async Task A_scan_over_a_refreshed_table_allocates_nothing_per_row()
    {
        const int Small = 20_000;
        const int Large = 60_000;
        const int Batches = 5;

        using var arena = new ExecutionArena();
        var small = await MeasureAsync(Small, Small / Batches, arena);
        var large = await MeasureAsync(Large, Large / Batches, arena);

        var slope = (large - small) / (double)(Large - Small);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"refreshed table: {small} bytes for {Small} rows, {large} for {Large} "
            + $"in {Batches} batches either way = {slope:0.0000} bytes/row");
        Assert.Equal(0, arena.OutstandingBytes);
        Assert.True(
            Math.Abs(slope) < 0.01,
            $"a scan of a refreshed table allocated {slope:0.0000} bytes per row ({small} → {large})");
    }

    private static async Task<long> MeasureAsync(int rows, int batchSize, ExecutionArena arena)
    {
        // Registered with one collection, replaced with another, then appended to: the rows a scan
        // reads are in an array the append extended rather than in the one the host registered.
        var source = Source(Ordered(1, 4));
        await RefreshAsync(source, SourceRefreshKind.Replace, Ordered(1, rows / 2));
        await RefreshAsync(source, SourceRefreshKind.Append, Ordered((rows / 2) + 1, rows - (rows / 2)));

        // Measured the way the rest of the family measures (F85): warmed, then the smallest of
        // several same-thread windows. One window is one reading, and a collection inside it charges
        // this thread for the unconsumed tail of its allocation context — up to about 8 KB, which
        // over forty thousand rows is twenty times this gate's whole tolerance. A collection can
        // only ever add, so the smallest of several windows is the closest to the truth and the
        // tolerance below is unchanged.
        var (bytes, _) = await AllocationProbe.SteadyStateAsync(
            () => DrainAsync(source, batchSize, arena));
        return bytes;
    }

    private static async Task<int> DrainAsync(PocoSource source, int batchSize, ExecutionArena arena)
    {
        var scanned = 0;
        var request = PocoTestSupport.Request(source, "ticks", batchSize);
        await foreach (var batch in source.ScanAsync(
            request, PocoTestSupport.Context(arena: arena), TestContext.Current.CancellationToken))
        {
            scanned += batch.Length;
            batch.Dispose();
        }

        return scanned;
    }

    /// <summary>A collection that only enumerates has no positions to append after.</summary>
    [Fact]
    public async Task An_append_to_an_enumerate_only_registration_is_refused()
    {
        IReadOnlyCollection<Tick> rows = new HashSet<Tick>(Ordered(1, 3));
        var source = new PocoSourceBuilder("mem").AddTable("ticks", rows).Build();

        var failure = await Assert.ThrowsAsync<SourceContractException>(
            async () => await AppendAsync(source, Ordered(4, 2)));
        Assert.Contains("only enumerates", failure.Message, StringComparison.Ordinal);
    }

    // ---- the scaffolding ----

    private static PocoSource Source(
        IReadOnlyList<Tick> rows, Action<PocoTableBuilder<Tick>>? configure = null) =>
        new PocoSourceBuilder("mem").AddTable("ticks", rows, configure).Build();

    private static Task AppendAsync(PocoSource source, IReadOnlyList<Tick> rows) =>
        RefreshAsync(source, SourceRefreshKind.Append, rows);

    private static async Task RefreshAsync(
        PocoSource source, SourceRefreshKind kind, IReadOnlyList<Tick> rows)
    {
        var commit = await source.PrepareRefreshAsync(
            [
                new SourceRefreshEntry
                {
                    Table = "ticks",
                    Kind = kind,
                    RowType = typeof(Tick),
                    Rows = rows,
                },
            ],
            TestContext.Current.CancellationToken);

        commit.Commit();
    }

    /// <summary>
    /// The claim §3 makes, checked shape by shape: for every range an index can be asked, the
    /// appended table answers exactly what a table built from the whole list answers — the same
    /// rows, in the same order, to the value. And the scan answers the same too.
    /// </summary>
    private static async Task AssertAnswersMatchAsync(
        PocoSource appended, PocoSource rebuilt, IReadOnlyList<int>? projection = null)
    {
        Assert.Equal(await ScanAsync(rebuilt), await ScanAsync(appended));

        foreach (var range in Ranges())
        {
            var expected = await LookupAsync(rebuilt, range, projection);
            var actual = await LookupAsync(appended, range, projection);
            Assert.Equal(expected, actual);
        }
    }

    private static IEnumerable<IndexKeyRange> Ranges()
    {
        yield return IndexKeyRange.All;
        yield return IndexKeyRange.Equality(8);
        yield return IndexKeyRange.Equality(99);
        yield return new IndexKeyRange { Lower = [3], Upper = [], LowerInclusive = true, UpperInclusive = true };
        yield return new IndexKeyRange { Lower = [], Upper = [8], LowerInclusive = true, UpperInclusive = false };
        yield return new IndexKeyRange { Lower = [2], Upper = [9], LowerInclusive = false, UpperInclusive = true };
    }

    private static async Task<int[]> ScanAsync(PocoSource source)
    {
        var seen = new List<int>();
        var request = PocoTestSupport.Request(source, "ticks", batchSize: 4);
        await foreach (var batch in source.ScanAsync(
            request, PocoTestSupport.Context(), TestContext.Current.CancellationToken))
        {
            Read(batch, seen);
            batch.Dispose();
        }

        return [.. seen];
    }

    private static async Task<string[]> LookupAsync(
        PocoSource source, IndexKeyRange range, IReadOnlyList<int>? projection)
    {
        var descriptor = PocoTestSupport.Describe(source, "ticks");
        projection ??= [.. Enumerable.Range(0, descriptor.Columns.Count)];
        var request = new IndexLookupRequest
        {
            Table = "ticks",
            Index = "ix_ticks_Seq",
            Projection = projection,
            OutputSchema = ArrowTypeMapping.ToArrowSchema(descriptor, projection),
            BatchSize = 3,
            Ranges = [range],
        };

        var rows = new List<string>();
        await foreach (var batch in source.IndexLookupAsync(
            request, PocoTestSupport.Context(), TestContext.Current.CancellationToken))
        {
            for (var row = 0; row < batch.Length; row++)
            {
                rows.Add(string.Join(
                    "|",
                    Enumerable.Range(0, batch.ColumnCount)
                        .Select(c => PocoTestSupport.Read(batch.Column(c), row))));
            }

            batch.Dispose();
        }

        return [.. rows];
    }

    private static void Read(RecordBatch batch, List<int> into)
    {
        var seq = (Int32Array)batch.Column(0);
        for (var i = 0; i < batch.Length; i++)
        {
            into.Add(seq.GetValue(i)!.Value);
        }
    }
}
