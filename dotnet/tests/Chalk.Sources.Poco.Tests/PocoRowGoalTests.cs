using System.Collections;
using Chalk.Catalog;
using IndexKind = Chalk.Ir.IndexKind;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// The row goal at the source (D276, <c>docs/design/46-row-goals.md</c> §5): a scan or a lookup the
/// planner goaled starts with about that many rows and doubles from there, on the Arrow path and on
/// the columnar one alike. A source that was told nothing keeps producing full batches, which is
/// what makes this change invisible to every plan the goal rule never touched.
/// </summary>
/// <remarks>
/// The claims here are structural — batch lengths and <c>RowsScanned</c> — and never about time.
/// The ramp's whole point is that a <c>LIMIT 1</c> whose first row matches reads one row instead of
/// a batch, and "one row" is a number a test can assert.
/// </remarks>
public sealed class PocoRowGoalTests
{
    private sealed record Row(int Key, string Label);

    private const int Rows = 100;

    private const int BatchSize = 64;

    /// <summary>
    /// The batch lengths a goal of one produces over a hundred rows at a batch size of 64: the ramp
    /// doubles until it reaches the batch size, and the last batch is whatever is left.
    /// </summary>
    private static readonly int[] RampedFromOne = [1, 2, 4, 8, 16, 32, 37];

    private static Row[] Data() =>
        [.. Enumerable.Range(0, Rows).Select(i => new Row(i, "row-" + i))];

    private static PocoSource ListSource() =>
        new PocoSourceBuilder("mem")
            .AddTable("rows", Data(), t => t.Index(r => r.Key))
            .Build();

    /// <summary>
    /// The same table as a collection that only enumerates. A scan of it stages rows a batch at a
    /// time, and its index — which cannot be a permutation of row positions, because there are no
    /// positions — yields rows rather than ordinals. Both are ramp paths of their own.
    /// </summary>
    private static PocoSource CollectionSource() =>
        new PocoSourceBuilder("mem")
            .AddTable("rows", new EnumerateOnly(Data()), out _, t => t.Index(ByKey, KeyIndex.Over))
            .Build();

    private static readonly IndexDescriptor ByKey = new()
    {
        Name = "ix_rows_Key",
        Kind = IndexKind.Ordered,
        Columns = [0],
        Unique = true,
    };

    /// <summary>A host index over a collection with no positions: it yields rows, in key order.</summary>
    private sealed class KeyIndex(IReadOnlyCollection<Row> rows) : IPocoIndex<Row>
    {
        public static IPocoIndex<Row> Over(IReadOnlyCollection<Row> rows) => new KeyIndex(rows);

        public IndexDescriptor Descriptor => ByKey;

        public IEnumerable<Row> Lookup(IndexKeyRange range) => rows.OrderBy(r => r.Key);
    }

    // ---- scans ----

    [Fact]
    public async Task A_goaled_scan_starts_at_the_goal_and_doubles_on_the_arrow_path()
    {
        var lengths = await ArrowScanLengthsAsync(ListSource(), rowGoal: 1);
        Assert.Equal(RampedFromOne, lengths);
    }

    [Fact]
    public async Task A_goaled_scan_starts_at_the_goal_and_doubles_on_the_columnar_path()
    {
        var lengths = await ColumnarScanLengthsAsync(ListSource(), rowGoal: 1);
        Assert.Equal(RampedFromOne, lengths);
    }

    /// <summary>The enumerate-only path stages into a pooled chunk and ramps the same way.</summary>
    [Fact]
    public async Task A_goaled_scan_over_a_collection_that_only_enumerates_ramps_too()
    {
        var lengths = await ArrowScanLengthsAsync(CollectionSource(), rowGoal: 1);
        Assert.Equal(RampedFromOne, lengths);
    }

    [Fact]
    public async Task A_scan_with_no_goal_produces_full_batches()
    {
        Assert.Equal([64, 36], await ArrowScanLengthsAsync(ListSource(), rowGoal: null));
        Assert.Equal([64, 36], await ColumnarScanLengthsAsync(ListSource(), rowGoal: null));
    }

    /// <summary>A goal at or above the batch size buys nothing and is not allowed to cost anything.</summary>
    [Fact]
    public async Task A_goal_larger_than_the_batch_size_produces_full_batches()
    {
        Assert.Equal([64, 36], await ArrowScanLengthsAsync(ListSource(), rowGoal: 4096));
    }

    /// <summary>
    /// The number the ramp exists for. A consumer that stops after the first batch of a
    /// <c>RowGoal = 1</c> scan has made the source read one row, not a batch of them, and
    /// <c>RowsScanned</c> still means what it always meant: the rows the source read.
    /// </summary>
    [Fact]
    public async Task A_consumer_that_stops_after_the_first_batch_has_read_one_row()
    {
        var source = ListSource();
        var stats = new ExecutionStats();
        var context = PocoTestSupport.Context(stats);

        await using var scan = source
            .ScanAsync(ScanRequest(source, rowGoal: 1), context, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await scan.MoveNextAsync());
        using (scan.Current)
        {
            Assert.Equal(1, scan.Current.Length);
        }

        Assert.Equal(1, stats.RowsScanned);
    }

    /// <summary>And the same scan told nothing reads a whole batch to hand back its first row.</summary>
    [Fact]
    public async Task The_same_scan_without_a_goal_reads_a_whole_batch_for_its_first_row()
    {
        var source = ListSource();
        var stats = new ExecutionStats();
        var context = PocoTestSupport.Context(stats);

        await using var scan = source
            .ScanAsync(ScanRequest(source, rowGoal: null), context, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await scan.MoveNextAsync());
        using (scan.Current)
        {
            Assert.Equal(BatchSize, scan.Current.Length);
        }

        Assert.Equal(BatchSize, stats.RowsScanned);
    }

    /// <summary>A ramped scan hands back every row it would have without a goal, in the same order.</summary>
    [Fact]
    public async Task A_goaled_scan_produces_exactly_the_rows_the_same_scan_without_one_does()
    {
        var goaled = await ArrowScanKeysAsync(ListSource(), rowGoal: 1);
        var plain = await ArrowScanKeysAsync(ListSource(), rowGoal: null);

        Assert.Equal(plain, goaled);
        Assert.Equal(Rows, goaled.Count);
    }

    // ---- lookups ----

    [Fact]
    public async Task A_goaled_lookup_starts_at_the_goal_and_doubles_on_the_arrow_path()
    {
        var lengths = await ArrowLookupLengthsAsync(ListSource(), rowGoal: 1);
        Assert.Equal(RampedFromOne, lengths);
    }

    [Fact]
    public async Task A_goaled_lookup_starts_at_the_goal_and_doubles_on_the_columnar_path()
    {
        var lengths = await ColumnarLookupLengthsAsync(ListSource(), rowGoal: 1);
        Assert.Equal(RampedFromOne, lengths);
    }

    /// <summary>The row-yielding lookup — the path an index over a bare collection takes.</summary>
    [Fact]
    public async Task A_goaled_lookup_over_a_collection_that_only_enumerates_ramps_too()
    {
        var lengths = await ArrowLookupLengthsAsync(CollectionSource(), rowGoal: 1);
        Assert.Equal(RampedFromOne, lengths);
    }

    [Fact]
    public async Task A_lookup_with_no_goal_produces_full_batches()
    {
        Assert.Equal([64, 36], await ArrowLookupLengthsAsync(ListSource(), rowGoal: null));
    }

    [Fact]
    public async Task A_consumer_that_stops_after_a_goaled_lookups_first_batch_has_read_one_row()
    {
        var source = ListSource();
        var stats = new ExecutionStats();

        await using var lookup = source
            .IndexLookupAsync(
                LookupRequest(source, rowGoal: 1),
                PocoTestSupport.Context(stats),
                TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await lookup.MoveNextAsync());
        using (lookup.Current)
        {
            Assert.Equal(1, lookup.Current.Length);
        }

        Assert.Equal(1, stats.RowsScanned);
    }

    // ---- support ----

    private static ScanRequest ScanRequest(PocoSource source, long? rowGoal)
    {
        var descriptor = PocoTestSupport.Describe(source, "rows");
        var projection = Enumerable.Range(0, descriptor.Columns.Count).ToArray();
        return new ScanRequest
        {
            Table = "rows",
            Projection = projection,
            OutputSchema = ArrowTypeMapping.ToArrowSchema(descriptor, projection),
            BatchSize = BatchSize,
            RowGoal = rowGoal,
        };
    }

    private static IndexLookupRequest LookupRequest(PocoSource source, long? rowGoal)
    {
        var descriptor = PocoTestSupport.Describe(source, "rows");
        var projection = Enumerable.Range(0, descriptor.Columns.Count).ToArray();
        return new IndexLookupRequest
        {
            Table = "rows",
            Index = "ix_rows_Key",
            Ranges = [IndexKeyRange.All],
            Projection = projection,
            OutputSchema = ArrowTypeMapping.ToArrowSchema(descriptor, projection),
            BatchSize = BatchSize,
            RowGoal = rowGoal,
        };
    }

    private static async Task<List<int>> ArrowScanLengthsAsync(PocoSource source, long? rowGoal)
    {
        var lengths = new List<int>();
        await foreach (var batch in source.ScanAsync(
            ScanRequest(source, rowGoal),
            PocoTestSupport.Context(),
            TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                lengths.Add(batch.Length);
            }
        }

        return lengths;
    }

    private static async Task<List<int>> ArrowScanKeysAsync(PocoSource source, long? rowGoal)
    {
        var keys = new List<int>();
        await foreach (var batch in source.ScanAsync(
            ScanRequest(source, rowGoal),
            PocoTestSupport.Context(),
            TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    keys.Add((int)PocoTestSupport.Read(batch.Column(0), i)!);
                }
            }
        }

        return keys;
    }

    private static async Task<List<int>> ArrowLookupLengthsAsync(PocoSource source, long? rowGoal)
    {
        var lengths = new List<int>();
        await foreach (var batch in source.IndexLookupAsync(
            LookupRequest(source, rowGoal),
            PocoTestSupport.Context(),
            TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                lengths.Add(batch.Length);
            }
        }

        return lengths;
    }

    private static async Task<List<int>> ColumnarScanLengthsAsync(PocoSource source, long? rowGoal)
    {
        var request = ScanRequest(source, rowGoal);
        var scan = ((IColumnarBatchSource)source).ColumnarScan(request, PocoTestSupport.Context())
            ?? throw new InvalidOperationException("the source declined to serve a columnar scan");
        return await DrainAsync(scan, source);
    }

    private static async Task<List<int>> ColumnarLookupLengthsAsync(PocoSource source, long? rowGoal)
    {
        var request = LookupRequest(source, rowGoal);
        var scan = ((IColumnarBatchSource)source).ColumnarLookup(request, PocoTestSupport.Context())
            ?? throw new InvalidOperationException("the source declined to serve a columnar lookup");
        return await DrainAsync(scan, source);
    }

    private static async Task<List<int>> DrainAsync(IColumnarScan scan, PocoSource source)
    {
        var descriptor = PocoTestSupport.Describe(source, "rows");
        var types = descriptor.Columns.Select(c => c.Type).ToArray();
        var lengths = new List<int>();
        await using (scan.ConfigureAwait(false))
        {
            var batch = new ColumnarBatch(types);
            while (scan.TryNext(batch))
            {
                lengths.Add(batch.Count);
            }
        }

        return lengths;
    }

    /// <summary>A collection the source cannot index into by position.</summary>
    private sealed class EnumerateOnly(IReadOnlyList<Row> rows) : IReadOnlyCollection<Row>
    {
        public int Count => rows.Count;

        public IEnumerator<Row> GetEnumerator() => rows.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
