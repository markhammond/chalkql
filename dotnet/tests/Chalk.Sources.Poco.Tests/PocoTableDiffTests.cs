using Chalk.Catalog;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// What a replacement changed, for a view kept by incremental maintenance (design 68 §15): when the
/// new rows are the old ones, the same objects, with some taken out and the rest in their order, and
/// rows appended after them — a host's compaction of a log — the rows taken out and where the appended
/// rows begin. Any other replacement is a rewrite, read whole.
/// </summary>
public sealed class PocoTableDiffTests
{
    private sealed record Tick(int Seq, string Symbol);

    private static Tick[] Ticks(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => new Tick(i, i % 2 == 0 ? "ACME" : "BRIX"))];

    [Fact]
    public async Task A_compaction_reads_as_the_rows_it_took_out_and_the_rows_it_appended()
    {
        var rows = Ticks(0, 10);
        var source = Source(rows);
        var before = Pin(source);
        Tick[] compacted = [rows[0], rows[2], rows[3], rows[5], rows[6], rows[7], rows[8], rows[9], .. Ticks(10, 2)];
        await ReplaceAsync(source, compacted);
        var after = Pin(source);

        var tracked = (ITrackedSource)source;
        Assert.Equal(-1, tracked.AppendedFrom("ticks", before, after));
        var diff = Assert.IsType<TableDiff>(tracked.DiffFrom("ticks", before, after));
        Assert.Equal([1, 4], diff.Removed);
        Assert.Equal(8, diff.AppendedFrom);

        Assert.Equal([1, 4], await SeqsAsync(source, tracked.ScanAt("ticks", before, diff.Removed, Request(source), PocoTestSupport.Context())));
        Assert.Equal([10, 11], await SeqsAsync(source, tracked.ScanFrom("ticks", after, diff.AppendedFrom, Request(source), PocoTestSupport.Context())));
        Unpin(source, before, after);
    }

    [Fact]
    public async Task A_replacement_that_keeps_every_row_and_appends_takes_nothing_out()
    {
        var rows = Ticks(0, 6);
        var source = Source(rows);
        var before = Pin(source);
        await ReplaceAsync(source, [.. rows, .. Ticks(6, 3)]);
        var after = Pin(source);

        var diff = Assert.IsType<TableDiff>(((ITrackedSource)source).DiffFrom("ticks", before, after));
        Assert.Empty(diff.Removed);
        Assert.Equal(6, diff.AppendedFrom);
        Unpin(source, before, after);
    }

    [Fact]
    public async Task A_replacement_by_new_objects_is_a_rewrite()
    {
        var rows = Ticks(0, 6);
        var source = Source(rows);
        var before = Pin(source);
        await ReplaceAsync(source, [.. rows.Select(r => r with { })]);
        var after = Pin(source);

        Assert.Null(((ITrackedSource)source).DiffFrom("ticks", before, after));
        Unpin(source, before, after);
    }

    [Fact]
    public async Task A_replacement_that_takes_out_more_than_it_keeps_is_a_rewrite()
    {
        var rows = Ticks(0, 10);
        var source = Source(rows);
        var before = Pin(source);
        await ReplaceAsync(source, [rows[7], rows[8], rows[9]]);
        var after = Pin(source);

        Assert.Null(((ITrackedSource)source).DiffFrom("ticks", before, after));
        Unpin(source, before, after);
    }

    [Fact]
    public async Task A_replacement_that_reorders_the_rows_is_a_rewrite()
    {
        var rows = Ticks(0, 10);
        var source = Source(rows);
        var before = Pin(source);
        await ReplaceAsync(source, [.. rows.Reverse()]);
        var after = Pin(source);

        Assert.Null(((ITrackedSource)source).DiffFrom("ticks", before, after));
        Unpin(source, before, after);
    }

    private static PocoSource Source(IReadOnlyList<Tick> rows) =>
        new PocoSourceBuilder("mem").AddTable("ticks", rows).Build();

    private static object Pin(PocoSource source) =>
        ((IPinnableSource)source).Pin("ticks") ?? throw new InvalidOperationException("nothing to pin");

    private static void Unpin(PocoSource source, params object[] pinned)
    {
        foreach (var state in pinned)
        {
            ((IPinnableSource)source).Unpin("ticks", state);
        }
    }

    private static ScanRequest Request(PocoSource source) => PocoTestSupport.Request(source, "ticks", batchSize: 4);

    private static async Task ReplaceAsync(PocoSource source, IReadOnlyList<Tick> rows)
    {
        var commit = await source.PrepareRefreshAsync(
            [
                new SourceRefreshEntry
                {
                    Table = "ticks",
                    Kind = SourceRefreshKind.Replace,
                    RowType = typeof(Tick),
                    Rows = rows,
                },
            ],
            TestContext.Current.CancellationToken);

        commit.Commit();
    }

    private static async Task<List<int>> SeqsAsync(PocoSource source, IColumnarScan scan)
    {
        var types = PocoTestSupport.Describe(source, "ticks").Columns.Select(c => c.Type).ToArray();
        var seqs = new List<int>();
        await using (scan.ConfigureAwait(false))
        {
            var batch = new ColumnarBatch(types);
            while (scan.TryNext(batch))
            {
                var lanes = batch.Column(0).Lanes<int>();
                for (var i = 0; i < batch.Count; i++)
                {
                    seqs.Add(lanes[batch.RowAt(i)]);
                }
            }
        }

        return seqs;
    }
}
