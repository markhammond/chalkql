using Apache.Arrow;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// Snapshot isolation (D260, <c>docs/design/35-poco-refresh.md</c> §1): a table's state is one
/// immutable object, every reader captures it once at entry, and what the registration reports
/// afterwards is nothing to do with what that reader sees.
/// </summary>
public sealed class PocoSnapshotTests
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
    ];

    /// <summary>
    /// The whole of §1 in one pass: a scan that has read a batch keeps reading the collection it
    /// started with, however many times the <c>Func</c> is repointed underneath it.
    /// </summary>
    [Fact]
    public async Task A_scan_reads_the_collection_its_snapshot_was_built_from()
    {
        IReadOnlyList<Tick> current = First;
        var source = new PocoSourceBuilder("mem").AddTable("ticks", () => current).Build();

        var request = PocoTestSupport.Request(source, "ticks", batchSize: 2);
        var seen = new List<int>();

        await foreach (var batch in source.ScanAsync(
            request, PocoTestSupport.Context(), TestContext.Current.CancellationToken))
        {
            Read(batch, seen);
            batch.Dispose();

            // Mid-scan, and twice: neither the count nor the rows of what this scan is reading may
            // move, because it is not reading the registration at all.
            current = Second;
            current = [];
        }

        Assert.Equal([1, 2, 3, 4], seen);
    }

    /// <summary>
    /// And a scan started afterwards reads the same thing, because nothing re-read the
    /// <c>Func</c>: a swapped collection arrives with a refresh and not before it.
    /// </summary>
    [Fact]
    public async Task A_later_scan_reads_the_same_snapshot_until_something_refreshes_it()
    {
        IReadOnlyList<Tick> current = First;
        var source = new PocoSourceBuilder("mem").AddTable("ticks", () => current).Build();

        current = Second;

        var seen = new List<int>();
        var request = PocoTestSupport.Request(source, "ticks", batchSize: 8);
        await foreach (var batch in source.ScanAsync(
            request, PocoTestSupport.Context(), TestContext.Current.CancellationToken))
        {
            Read(batch, seen);
            batch.Dispose();
        }

        Assert.Equal([1, 2, 3, 4], seen);
        Assert.Equal(4, PocoTestSupport.Describe(source, "ticks").RowCount);
    }

    /// <summary>
    /// A describe reads one snapshot for all of it. The row count and the statistics therefore
    /// describe the same rows, which is what makes a catalog a catalog rather than three readings.
    /// </summary>
    [Fact]
    public void A_describe_reports_one_snapshot()
    {
        IReadOnlyList<Tick> current = First;
        var source = new PocoSourceBuilder("mem")
            .AddTable("ticks", () => current, t => t.Index(x => x.Seq))
            .Build();

        var table = PocoTestSupport.Describe(source, "ticks");

        Assert.Equal(4, table.RowCount);
        Assert.Equal(4, table.Columns[1].Statistics.DistinctCount);
        Assert.Equal(4, source.FindIndex<Tick>("ticks", "ix_ticks_Seq")!.DistinctCount(1));
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
