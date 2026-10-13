using System.Collections;
using Apache.Arrow;
using Chalk.Catalog;
using IndexKind = Chalk.Ir.IndexKind;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// <c>IndexLookupRequest.RowsPerRange</c> (D338): only the first rows of each range, the first the
/// read reaches in its direction. An ASOF join that seeks an index asks for one row of each, and its
/// ranges come in the order of its left rows rather than the index's. Every lookup path honours it:
/// the permutation, the clustered copy, and a host's index that yields rows, forwards and backwards.
/// </summary>
public sealed class PocoRowsPerRangeTests
{
    private sealed record Row(int Key, string Label);

    private static Row[] Data() => [.. Enumerable.Range(0, 100).Select(i => new Row(i, "row-" + i))];

    /// <summary>Three ranges open above, out of the index's order: their first rows are 50, 10 and 71.</summary>
    private static readonly IndexKeyRange[] FromBelow =
    [
        new() { Lower = [50], Upper = [] },
        new() { Lower = [10], Upper = [] },
        new() { Lower = [70], LowerInclusive = false, Upper = [] },
    ];

    /// <summary>Three ranges open below, read backwards: their first rows are 50, 10 and 69.</summary>
    private static readonly IndexKeyRange[] FromAbove =
    [
        new() { Lower = [], Upper = [50] },
        new() { Lower = [], Upper = [10] },
        new() { Lower = [], Upper = [70], UpperInclusive = false },
    ];

    public static TheoryData<string> Paths => ["permutation", "clustered", "host"];

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task One_row_per_range_forwards(string path)
    {
        var (keys, scanned) = await LookupAsync(Source(path), FromBelow, reverse: false, rowsPerRange: 1);

        Assert.Equal([10, 50, 71], keys.Order().ToArray());
        Assert.Equal(3, scanned);
    }

    [Theory]
    [InlineData("permutation")]
    [InlineData("clustered")]
    public async Task One_row_per_range_backwards(string path)
    {
        var (keys, scanned) = await LookupAsync(Source(path), FromAbove, reverse: true, rowsPerRange: 1);

        Assert.Equal([10, 50, 69], keys.Order().ToArray());
        Assert.Equal(3, scanned);
    }

    /// <summary>Without a number, every row of every range, once each.</summary>
    [Fact]
    public async Task No_number_is_every_row()
    {
        var (keys, _) = await LookupAsync(Source("permutation"), FromBelow, reverse: false, rowsPerRange: null);

        Assert.Equal(90, keys.Count);
    }

    private static PocoSource Source(string path) => path switch
    {
        "permutation" => new PocoSourceBuilder("mem").AddTable("rows", Data(), t => t.Index(r => r.Key)).Build(),
        "clustered" => new PocoSourceBuilder("mem").AddTable("rows", Data(), t => t.ClusteredIndex(r => r.Key)).Build(),
        _ => new PocoSourceBuilder("mem")
            .AddTable("rows", new EnumerateOnly(Data()), out _, t => t.Index(ByKey, KeyIndex.Over))
            .Build(),
    };

    private static readonly IndexDescriptor ByKey = new()
    {
        Name = "ix_rows_Key",
        Kind = IndexKind.Ordered,
        Columns = [0],
        Unique = true,
    };

    /// <summary>A host index over a collection with no positions: it yields the rows of a range, in key order.</summary>
    private sealed class KeyIndex(IReadOnlyCollection<Row> rows) : IPocoIndex<Row>
    {
        public static IPocoIndex<Row> Over(IReadOnlyCollection<Row> rows) => new KeyIndex(rows);

        public IndexDescriptor Descriptor => ByKey;

        public IEnumerable<Row> Lookup(IndexKeyRange range) =>
            rows.Where(r => range.Lower.Count == 0
                    || (range.LowerInclusive ? r.Key >= (int)range.Lower[0]! : r.Key > (int)range.Lower[0]!))
                .Where(r => range.Upper.Count == 0
                    || (range.UpperInclusive ? r.Key <= (int)range.Upper[0]! : r.Key < (int)range.Upper[0]!))
                .OrderBy(r => r.Key);
    }

    private sealed class EnumerateOnly(IReadOnlyList<Row> rows) : IReadOnlyCollection<Row>
    {
        public int Count => rows.Count;

        public IEnumerator<Row> GetEnumerator() => rows.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static async Task<(List<int> Keys, long Scanned)> LookupAsync(
        PocoSource source, IReadOnlyList<IndexKeyRange> ranges, bool reverse, int? rowsPerRange)
    {
        var descriptor = PocoTestSupport.Describe(source, "rows");
        var index = descriptor.Indexes[0].Name;
        var projection = Enumerable.Range(0, descriptor.Columns.Count).ToArray();
        var stats = new ExecutionStats();
        var keys = new List<int>();
        await foreach (var batch in source.IndexLookupAsync(
            new IndexLookupRequest
            {
                Table = "rows",
                Index = index,
                Ranges = ranges,
                Projection = projection,
                OutputSchema = ArrowTypeMapping.ToArrowSchema(descriptor, projection),
                BatchSize = 64,
                Reverse = reverse,
                RowsPerRange = rowsPerRange,
            },
            PocoTestSupport.Context(stats),
            TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                var column = (Int32Array)batch.Column(0);
                for (var i = 0; i < batch.Length; i++)
                {
                    keys.Add(column.GetValue(i)!.Value);
                }
            }
        }

        return (keys, stats.RowsScanned);
    }
}
