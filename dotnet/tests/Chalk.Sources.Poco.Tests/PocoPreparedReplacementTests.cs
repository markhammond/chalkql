using Chalk.Catalog;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// A replacement prepared before the refresh that commits it (design 69 §3): built beside the
/// table's appends, and rebased at its commit onto the rows the table took since its cut. Every
/// refusal names why and changes nothing.
/// </summary>
public sealed class PocoPreparedReplacementTests
{
    private sealed record Tick(int Seq, string Symbol, double Price, string? Sector, DateTime Ts);

    private sealed record Other(int Seq);

    private sealed record Position(string Book, string Asset, decimal Qty);

    private static readonly DateTime Open = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Symbols = ["ACME", "BRIX", "CIRRUS", "DELTA"];

    /// <summary>Rows <paramref name="from"/> on, one a second, the symbols taking turns.</summary>
    private static IReadOnlyList<Tick> Ticks(int from, int count) =>
    [
        .. Enumerable.Range(from, count).Select(i => new Tick(
            i,
            Symbols[i % Symbols.Length],
            i + 0.5,
            i % 3 == 0 ? null : $"sector-{i % 5}-with-a-long-enough-name",
            Open.AddSeconds(i))),
    ];

    /// <summary>The host's compaction: each symbol's last row, in time order.</summary>
    private static IReadOnlyList<Tick> LastOfEach(IEnumerable<Tick> rows) =>
        [.. rows.GroupBy(t => t.Symbol).Select(g => g.Last()).OrderBy(t => t.Ts)];

    private static void Shape(PocoTableBuilder<Tick> table) => table
        .AppendOnly(x => x.Ts)
        .Index(x => x.Seq)
        .ClusteredIndex(x => x.Symbol)
        .Covering(x => x.Price, x => x.Sector!);

    // ---- what a commit publishes ----

    /// <summary>
    /// The rows from the cut on, those the table held when the replacement was prepared and those
    /// appended after, follow the rewrite: the table answers every scan and lookup as a table
    /// registered over the rewrite and those rows answers.
    /// </summary>
    [Fact]
    public async Task A_commit_answers_as_the_rewrite_followed_by_the_rows_since_the_cut()
    {
        var source = Source(Ticks(1, 40), Shape);
        var rewrite = LastOfEach(Ticks(1, 30));

        using var prepared = await PrepareAsync(source, rewrite, through: 30);
        await RefreshAsync(source, Entry("ticks", SourceRefreshKind.Append, Ticks(41, 5)));
        await RefreshAsync(source, Commit(prepared));

        var expected = Source([.. rewrite, .. Ticks(31, 15)], Shape);
        Assert.Equal(await AnswersAsync(expected), await AnswersAsync(source));
        Assert.Equal(rewrite.Count + 15, PocoTestSupport.Describe(source, "ticks").RowCount);
        Assert.False(prepared.Pending);
    }

    /// <summary>A replacement with no rows appended after its cut is published as it was built.</summary>
    [Fact]
    public async Task A_commit_with_nothing_since_the_cut_publishes_the_rewrite()
    {
        var source = Source(Ticks(1, 12), Shape);
        var rewrite = LastOfEach(Ticks(1, 12));

        using var prepared = await PrepareAsync(source, rewrite, through: 12);
        await RefreshAsync(source, Commit(prepared));

        Assert.Equal(await AnswersAsync(Source(rewrite, Shape)), await AnswersAsync(source));
    }

    /// <summary>
    /// Appends commit while a replacement is being built, without waiting for it: the build holds
    /// none of the locks a refresh takes. The commit then carries the appended rows over.
    /// </summary>
    [Fact]
    public async Task An_append_commits_while_a_replacement_is_being_prepared()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new PocoSourceBuilder("mem")
            .AddTable("gated", Gates(1, 20), t => t.Index(x => x.Seq))
            .Build();
        using var hold = new Hold();

        var preparing = Task.Run(
            () => ((IPreparableSource)source).PrepareReplaceAsync(
                "gated", typeof(Gated), (IReadOnlyList<Gated>)[new Gated(1, hold), new Gated(2, hold)], 20, null, ct).AsTask(),
            ct);
        Assert.True(hold.Entered.Wait(TimeSpan.FromSeconds(30), ct), "the prepare never reached its build");

        // The prepare is inside its build now, and stays there until the hold is released.
        var appending = Task.Run(
            async () =>
            {
                var commit = await source.PrepareRefreshAsync(
                    [Entry("gated", SourceRefreshKind.Append, Gates(21, 5))], ct);
                commit.Commit();
            },
            ct);
        await appending.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.False(preparing.IsCompleted);

        hold.Released.Set();
        using var prepared = await preparing;
        await RefreshAsync(source, Commit(prepared));

        var seqs = await SeqsAsync(source, "gated");
        Assert.Equal([1, 2, 21, 22, 23, 24, 25], seqs);
    }

    /// <summary>
    /// The list the host prepared from is copied, as an append's rows are: the host may reuse it at
    /// once, and it is never reported released.
    /// </summary>
    [Fact]
    public async Task The_rows_prepared_from_are_the_host_s_again_at_once()
    {
        var source = Source(Ticks(1, 8), Shape);
        var released = new List<object>();
        source.SnapshotReleased += release => released.Add(release.Collection);

        var rewrite = new List<Tick>(LastOfEach(Ticks(1, 8)));
        using var prepared = await PrepareAsync(source, rewrite, through: 8);
        rewrite.Clear();
        await RefreshAsync(source, Commit(prepared));
        await RefreshAsync(source, Entry("ticks", SourceRefreshKind.Replace, Ticks(20, 2)));

        var seqs = await SeqsAsync(source, "ticks");
        Assert.Equal([20, 21], seqs);
        Assert.DoesNotContain(rewrite, released);
    }

    /// <summary>
    /// The frontier does not move back at a commit: the rows since the cut were admitted against it,
    /// and so is everything after. A row older than it is still refused.
    /// </summary>
    [Fact]
    public async Task A_commit_keeps_the_table_s_frontier()
    {
        var source = Source(Ticks(1, 10), t => t.AppendOnly(x => x.Ts));
        await RefreshAsync(source, Advance("ticks", Open.AddSeconds(30)));

        using var prepared = await PrepareAsync(source, LastOfEach(Ticks(1, 10)), through: 10);
        await RefreshAsync(source, Commit(prepared));

        var late = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => RefreshAsync(source, Entry("ticks", SourceRefreshKind.Append, Ticks(20, 1))));
        Assert.Contains("older than the table's frontier, 2026-10-12T09:00:30", late.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refresh refused after the replacement was rebased onto the rows since its cut leaves it
    /// uncommitted, and a later refresh commits it over the rows there are then.
    /// </summary>
    [Fact]
    public async Task A_refused_refresh_leaves_the_replacement_to_commit_later()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("ticks", Ticks(1, 20), Shape)
            .AddTable("log", Ticks(1, 3), t => t.AppendOnly(x => x.Ts))
            .Build();
        var rewrite = LastOfEach(Ticks(1, 16));

        using var prepared = await PrepareAsync(source, rewrite, through: 16);
        await Assert.ThrowsAsync<SourceContractException>(
            () => RefreshAsync(source, Commit(prepared), Advance("log", Open)));
        Assert.True(prepared.Pending);
        Assert.Equal(20, PocoTestSupport.Describe(source, "ticks").RowCount);

        await RefreshAsync(source, Entry("ticks", SourceRefreshKind.Append, Ticks(21, 3)));
        await RefreshAsync(source, Commit(prepared));

        Assert.Equal(await AnswersAsync(Source([.. rewrite, .. Ticks(17, 7)], Shape)), await AnswersAsync(source));
    }

    /// <summary>
    /// A table registered as a collection that only enumerates takes no appends, so a replacement
    /// prepared for it rewrites every row it holds.
    /// </summary>
    [Fact]
    public async Task An_enumerated_table_takes_a_replacement_of_all_its_rows()
    {
        IReadOnlyCollection<Tick> registered = new HashSet<Tick>(Ticks(1, 6));
        var source = new PocoSourceBuilder("mem").AddTable("ticks", registered).Build();

        var partial = await Assert.ThrowsAsync<SourceContractException>(
            () => PrepareAsync(source, Ticks(1, 2), through: 4));
        Assert.Contains("the cut is its row count, 6", partial.Message, StringComparison.Ordinal);

        using var prepared = await PrepareAsync(source, Ticks(1, 2), through: 6);
        await RefreshAsync(source, Commit(prepared));
        Assert.Equal([1, 2], (await SeqsAsync(source, "ticks")).Order());
    }

    // ---- refusals ----

    /// <summary>
    /// A keyed table's rows change by key and not only at its end, so it has no rows since a cut to
    /// carry over: a replacement prepared for it is refused, saying to replace it in a refresh.
    /// </summary>
    [Fact]
    public async Task A_keyed_table_refuses_a_prepared_replacement()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("positions", (IReadOnlyList<Position>)[new("b1", "ACME", 1)], t => t.Keyed(x => x.Book, x => x.Asset))
            .Build();

        var refused = await Assert.ThrowsAsync<SourceContractException>(
            async () => await ((IPreparableSource)source).PrepareReplaceAsync(
                "positions", typeof(Position), (IReadOnlyList<Position>)[], 1, null, TestContext.Current.CancellationToken));

        Assert.Equal(ChalkErrorCodes.SourceRefresh, refused.Code);
        Assert.Contains("declares Keyed(Book, Asset), so its rows change by key", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Replace it in a refresh instead", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A cut is a count of rows the table already has.</summary>
    [Fact]
    public async Task A_cut_past_the_table_s_rows_is_refused()
    {
        var source = Source(Ticks(1, 5), Shape);

        var refused = await Assert.ThrowsAsync<SourceContractException>(() => PrepareAsync(source, Ticks(1, 2), through: 6));

        Assert.Contains("before row 6, and the table holds 5", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A replacement is built over the type its table was built over.</summary>
    [Fact]
    public async Task Rows_of_another_type_are_refused()
    {
        var source = Source(Ticks(1, 5), Shape);

        var refused = await Assert.ThrowsAsync<SourceContractException>(
            async () => await ((IPreparableSource)source).PrepareReplaceAsync(
                "ticks", typeof(Other), (IReadOnlyList<Other>)[new(1)], 5, null, TestContext.Current.CancellationToken));

        Assert.Contains("the rows are Other, but this table was built over Tick", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A table replaced after the replacement was prepared no longer starts with the rows it
    /// rewrote: the commit is refused, the table keeps its rows, and the replacement stays
    /// uncommitted.
    /// </summary>
    [Fact]
    public async Task A_table_replaced_after_the_prepare_refuses_the_commit()
    {
        var source = Source(Ticks(1, 10), Shape);
        using var prepared = await PrepareAsync(source, LastOfEach(Ticks(1, 10)), through: 10);
        await RefreshAsync(source, Entry("ticks", SourceRefreshKind.Replace, Ticks(1, 20)));

        var refused = await Assert.ThrowsAsync<SourceContractException>(() => RefreshAsync(source, Commit(prepared)));

        Assert.Equal(ChalkErrorCodes.SourceRefresh, refused.Code);
        Assert.Contains("replaced after the replacement was prepared", refused.Message, StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(1, 20), await SeqsAsync(source, "ticks"));
        Assert.True(prepared.Pending);
    }

    /// <summary>
    /// A commit rewrites the table, as a replacement does, even when the rows since its cut make it
    /// look like an append: of two replacements prepared from one state, the second is refused once
    /// the first commits.
    /// </summary>
    [Fact]
    public async Task A_commit_is_a_rewrite_so_a_second_replacement_from_its_state_is_refused()
    {
        var source = Source(Ticks(1, 10), Shape);

        // A rewrite as long as the table, so its row count alone says nothing.
        using var first = await PrepareAsync(source, Ticks(1, 5).Concat(Ticks(1, 5)).OrderBy(t => t.Ts).ToList(), through: 5);
        using var second = await PrepareAsync(source, LastOfEach(Ticks(1, 10)), through: 10);
        await RefreshAsync(source, Commit(first));

        var refused = await Assert.ThrowsAsync<SourceContractException>(() => RefreshAsync(source, Commit(second)));
        Assert.Contains("replaced after the replacement was prepared", refused.Message, StringComparison.Ordinal);
        Assert.Equal(15, PocoTestSupport.Describe(source, "ticks").RowCount);
    }

    /// <summary>
    /// The rows since the cut must follow the rewrite in the table's declared order. A rewrite that
    /// ends later than they begin is refused at the commit, naming the boundary, and nothing is
    /// applied.
    /// </summary>
    [Fact]
    public async Task Rows_since_the_cut_that_break_the_order_refuse_the_commit()
    {
        var source = Source(Ticks(1, 10), Shape);
        IReadOnlyList<Tick> rewrite = [new(5, "ACME", 1, null, Open.AddSeconds(8))];
        using var prepared = await PrepareAsync(source, rewrite, through: 5);

        var refused = await Assert.ThrowsAsync<CatalogVerificationException>(() => RefreshAsync(source, Commit(prepared)));

        Assert.Equal(ChalkErrorCodes.DeclarationNotHeld, refused.Code);
        Assert.Contains("the first row appended since the cut, row 5 of the table", refused.Message, StringComparison.Ordinal);
        Assert.Contains("must end where the rows appended after its cut begin", refused.Message, StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(1, 10), await SeqsAsync(source, "ticks"));
    }

    /// <summary>
    /// A replacement is committed once: the commit wrote the rows since the cut after the rewrite,
    /// and a second would write over them.
    /// </summary>
    [Fact]
    public async Task A_replacement_is_committed_once()
    {
        var source = Source(Ticks(1, 10), Shape);
        using var prepared = await PrepareAsync(source, LastOfEach(Ticks(1, 10)), through: 10);
        await RefreshAsync(source, Commit(prepared));

        var again = Assert.Throws<SourceContractException>(() => source.ValidateRefresh([Commit(prepared)]));
        Assert.Contains("has already been committed", again.Message, StringComparison.Ordinal);
    }

    /// <summary>A replacement disposed before a refresh commits it is let go, and refused.</summary>
    [Fact]
    public async Task A_disposed_replacement_is_refused()
    {
        var source = Source(Ticks(1, 10), Shape);
        var prepared = await PrepareAsync(source, LastOfEach(Ticks(1, 10)), through: 10);
        prepared.Dispose();

        var refused = Assert.Throws<SourceContractException>(() => source.ValidateRefresh([Commit(prepared)]));
        Assert.Contains("was disposed before a refresh committed it", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One refresh says once what a table becomes: a commit beside an append of the same table is
    /// refused. An advance beside it is not.
    /// </summary>
    [Fact]
    public async Task A_commit_is_the_one_thing_a_refresh_says_of_its_table()
    {
        var source = Source(Ticks(1, 10), Shape);
        using var prepared = await PrepareAsync(source, LastOfEach(Ticks(1, 10)), through: 10);

        var twice = Assert.Throws<SourceContractException>(
            () => source.ValidateRefresh([Commit(prepared), Entry("ticks", SourceRefreshKind.Append, Ticks(11, 1))]));
        Assert.Contains("names this table twice", twice.Message, StringComparison.Ordinal);

        await RefreshAsync(source, Commit(prepared), Advance("ticks", Open.AddSeconds(40)));
        var late = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => RefreshAsync(source, Entry("ticks", SourceRefreshKind.Append, Ticks(30, 1))));
        Assert.Contains("frontier, 2026-10-12T09:00:40", late.Message, StringComparison.Ordinal);
    }

    // ---- the scaffolding ----

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

    private static PocoSource Source(IReadOnlyList<Tick> rows, Action<PocoTableBuilder<Tick>>? configure = null) =>
        new PocoSourceBuilder("mem").AddTable("ticks", rows, configure).Build();

    private static async Task<IPreparedReplacement> PrepareAsync(PocoSource source, IReadOnlyList<Tick> rows, int through) =>
        await ((IPreparableSource)source).PrepareReplaceAsync(
            "ticks", typeof(Tick), rows, through, null, TestContext.Current.CancellationToken);

    private static SourceRefreshEntry Commit(IPreparedReplacement prepared) => new()
    {
        Table = prepared.Table,
        Kind = SourceRefreshKind.Commit,
        RowType = prepared.RowType,
        Rows = prepared,
    };

    private static SourceRefreshEntry Entry<T>(string table, SourceRefreshKind kind, IReadOnlyList<T> rows) => new()
    {
        Table = table,
        Kind = kind,
        RowType = typeof(T),
        Rows = rows,
    };

    private static SourceRefreshEntry Advance(string table, DateTime time) => new()
    {
        Table = table,
        Kind = SourceRefreshKind.Advance,
        RowType = typeof(object),
        Rows = Array.Empty<object>(),
        Frontier = time,
    };

    private static async Task RefreshAsync(PocoSource source, params SourceRefreshEntry[] entries)
    {
        var commit = await source.PrepareRefreshAsync(entries, TestContext.Current.CancellationToken);
        commit.Commit();
    }

    private static async Task<int[]> SeqsAsync(PocoSource source, string table)
    {
        var column = PocoTestSupport.Describe(source, table).Columns.ToList().FindIndex(c => c.Name == "Seq");
        var values = await PocoTestSupport.ColumnAsync(source, table, column, batchSize: 7);
        return [.. values.Select(v => (int)v!)];
    }

    /// <summary>
    /// Everything a reader can ask of the table: a scan, and a lookup of every index over a few
    /// ranges, each row rendered whole.
    /// </summary>
    private static async Task<List<string>> AnswersAsync(PocoSource source)
    {
        var answers = new List<string>();
        foreach (var batch in await PocoTestSupport.ScanAsync(source, "ticks", batchSize: 5))
        {
            Render(batch, answers);
            batch.Dispose();
        }

        var descriptor = PocoTestSupport.Describe(source, "ticks");
        var projection = Enumerable.Range(0, descriptor.Columns.Count).ToArray();
        IndexKeyRange[] bySeq =
        [
            IndexKeyRange.All,
            IndexKeyRange.Equality(31),
            new() { Lower = [5], Upper = [33], LowerInclusive = true, UpperInclusive = false },
        ];
        IndexKeyRange[] bySymbol = [IndexKeyRange.All, IndexKeyRange.Equality("BRIX")];

        foreach (var index in descriptor.Indexes)
        {
            var ranges = descriptor.Columns[index.Columns[0]].Name == "Seq" ? bySeq : bySymbol;
            foreach (var range in ranges)
            {
                answers.Add($"-- {index.Name} {range}");
                var request = new IndexLookupRequest
                {
                    Table = "ticks",
                    Index = index.Name,
                    Projection = projection,
                    OutputSchema = ArrowTypeMapping.ToArrowSchema(descriptor, projection),
                    BatchSize = 3,
                    Ranges = [range],
                };

                await foreach (var batch in source.IndexLookupAsync(
                    request, PocoTestSupport.Context(), TestContext.Current.CancellationToken))
                {
                    Render(batch, answers);
                    batch.Dispose();
                }
            }
        }

        return answers;
    }

    private static void Render(Apache.Arrow.RecordBatch batch, List<string> into)
    {
        for (var row = 0; row < batch.Length; row++)
        {
            into.Add(string.Join(
                "|",
                Enumerable.Range(0, batch.ColumnCount).Select(c => PocoTestSupport.Read(batch.Column(c), row))));
        }
    }
}
