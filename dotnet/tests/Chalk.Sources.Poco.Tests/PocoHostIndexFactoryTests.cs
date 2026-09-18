using Apache.Arrow;
using Chalk.Catalog;
using IndexKind = Chalk.Ir.IndexKind;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// F110: a host index registered as a factory is made once per snapshot, over that snapshot's rows,
/// so an execution keeps the index it started with while a refresh lands, the next execution gets
/// the replacement, and a product that is not what the catalog declares is refused.
/// </summary>
public sealed class PocoHostIndexFactoryTests
{
    private sealed record Row(int Id, string Name);

    private static readonly Row[] First = [new(1, "a"), new(2, "b"), new(3, "c")];
    private static readonly Row[] Second = [new(4, "d"), new(5, "e")];

    private static readonly IndexDescriptor ById = new()
    {
        Name = "ix_rows_id",
        Kind = IndexKind.Ordered,
        Columns = [0],
        Unique = true,
    };

    /// <summary>A host structure that answers from the rows it was built over, and only those.</summary>
    private sealed class RowsIndex(IndexDescriptor descriptor, IReadOnlyCollection<Row> rows) : IPocoIndex<Row>
    {
        public IReadOnlyCollection<Row> Rows { get; } = rows;

        public IndexDescriptor Descriptor { get; } = descriptor;

        public long BytesPerRow => -1;

        public long? DistinctCount(int keyPositions) => null;

        public IEnumerable<Row> Lookup(IndexKeyRange range) => Rows.OrderBy(r => r.Id);
    }

    [Fact]
    public async Task An_execution_in_flight_keeps_the_index_it_started_with_and_the_next_gets_the_replacement()
    {
        IReadOnlyList<Row> current = First;
        var made = new List<IReadOnlyCollection<Row>>();
        var source = new PocoSourceBuilder("mem")
            .AddTable("rows", () => current, t => t.Index(ById, rows =>
            {
                made.Add(rows);
                return new RowsIndex(ById, rows);
            }))
            .Build();
        var ct = TestContext.Current.CancellationToken;
        Assert.Same(First, Assert.Single(made));

        var enumerator = source.IndexLookupAsync(Lookup(source), PocoTestSupport.Context(), ct).GetAsyncEnumerator(ct);
        var seen = new List<int>();
        Assert.True(await enumerator.MoveNextAsync());
        Read(enumerator.Current, seen);
        enumerator.Current.Dispose();

        current = Second;
        await source.RefreshAsync(ct);
        Assert.Equal(2, made.Count);
        Assert.Same(Second, made[1]);

        while (await enumerator.MoveNextAsync())
        {
            Read(enumerator.Current, seen);
            enumerator.Current.Dispose();
        }

        await enumerator.DisposeAsync();
        Assert.Equal([1, 2, 3], seen);
        var next = await LookupAsync(source);
        Assert.Equal([4, 5], next);
    }

    [Fact]
    public async Task An_instance_serves_every_snapshot_unchanged()
    {
        IReadOnlyList<Row> current = First;
        var instance = new RowsIndex(ById, First);
        var source = new PocoSourceBuilder("mem")
            .AddTable("rows", () => current, t => t.Index(instance))
            .Build();

        current = Second;
        await source.RefreshAsync(TestContext.Current.CancellationToken);

        // The contract of the instance overload, stated so that it stays a choice: the same object
        // answers for the new snapshot, and what it answers is its own business.
        var found = await LookupAsync(source);
        Assert.Equal([1, 2, 3], found);
    }

    [Fact]
    public void A_product_that_is_not_what_the_catalog_declares_is_refused()
    {
        var other = new IndexDescriptor
        {
            Name = ById.Name,
            Kind = ById.Kind,
            Columns = ById.Columns,
            Unique = false,
        };
        var builder = new PocoSourceBuilder("mem")
            .AddTable("rows", () => First, t => t.Index(ById, rows => new RowsIndex(other, rows)));

        var refusal = Assert.Throws<CatalogVerificationException>(() => builder.Build());

        Assert.Contains("rows", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("ix_rows_id", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("unique False", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_append_makes_the_index_over_the_appended_rows()
    {
        IReadOnlyList<Row> current = First;
        var made = new List<IReadOnlyCollection<Row>>();
        var source = new PocoSourceBuilder("mem")
            .AddTable("rows", () => current, t => t.Index(ById, rows =>
            {
                made.Add(rows);
                return new RowsIndex(ById, rows);
            }))
            .Build();
        var ct = TestContext.Current.CancellationToken;

        var commit = await source.PrepareRefreshAsync(
            [new SourceRefreshEntry { Table = "rows", Kind = SourceRefreshKind.Append, RowType = typeof(Row), Rows = Second }],
            ct);
        commit.Commit();

        Assert.Equal(2, made.Count);
        Assert.Equal(5, made[1].Count);
        var all = await LookupAsync(source);
        Assert.Equal([1, 2, 3, 4, 5], all);
    }

    // ---- the scaffolding ----

    private static IndexLookupRequest Lookup(PocoSource source)
    {
        var descriptor = PocoTestSupport.Describe(source, "rows");
        var projection = Enumerable.Range(0, descriptor.Columns.Count).ToArray();
        return new IndexLookupRequest
        {
            Table = "rows",
            Index = "ix_rows_id",
            Projection = projection,
            OutputSchema = ArrowTypeMapping.ToArrowSchema(descriptor, projection),
            BatchSize = 2,
            Ranges = [IndexKeyRange.Equality(1)],
        };
    }

    private static async Task<int[]> LookupAsync(PocoSource source)
    {
        var seen = new List<int>();
        await foreach (var batch in source.IndexLookupAsync(
            Lookup(source), PocoTestSupport.Context(), TestContext.Current.CancellationToken))
        {
            Read(batch, seen);
            batch.Dispose();
        }

        return [.. seen];
    }

    private static void Read(RecordBatch batch, List<int> into)
    {
        var id = (Int32Array)batch.Column(0);
        for (var i = 0; i < batch.Length; i++)
        {
            into.Add(id.GetValue(i)!.Value);
        }
    }
}
