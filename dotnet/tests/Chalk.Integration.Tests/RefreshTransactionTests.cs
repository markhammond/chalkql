using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The engine's refresh transaction (D260, <c>docs/design/35-poco-refresh.md</c> §2): tables across
/// several sources replaced under one catalog epoch, seen whole or not at all, with prepared queries
/// surviving a refresh that only moved rows.
/// </summary>
/// <remarks>
/// Nothing here waits on a clock. The transaction is held open by a latch a test source closes, so
/// "while the build is in flight" is a place in the code rather than an interval.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class RefreshTransactionTests(SharedSidecar sidecar)
{
    private sealed record Bar(string Symbol, int Seq, double Price);

    private sealed record Symbol(string Ticker, string Sector);

    private static readonly Bar[] FirstBars =
    [
        new("ACME", 1, 1.5),
        new("ACME", 2, 2.5),
        new("BRIX", 3, 3.5),
    ];

    private static readonly Bar[] SecondBars =
    [
        new("CIRRUS", 4, 4.5),
        new("CIRRUS", 5, 5.5),
    ];

    private static readonly Symbol[] FirstSymbols =
    [
        new("ACME", "industrials"),
        new("BRIX", "materials"),
    ];

    private static readonly Symbol[] SecondSymbols =
    [
        new("CIRRUS", "transport"),
    ];

    /// <summary>
    /// Two tables in two sources, replaced under one epoch; the rows of both move together, the
    /// epoch moves once, and the query prepared before it is still valid because nothing about the
    /// shape changed.
    /// </summary>
    [Fact]
    public async Task A_transaction_replaces_tables_across_sources_under_one_epoch()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var bars = Bars();
        var symbols = Symbols();
        await using var engine = await EngineAsync(bars, symbols);

        var ct = TestContext.Current.CancellationToken;
        var prepared = await engine.PrepareAsync(
            "SELECT b.seq, s.sector FROM mem.bars AS b JOIN book.symbols AS s "
            + "ON s.ticker = b.symbol ORDER BY b.seq");

        var rows1 = await RowsAsync(engine, prepared);
        Assert.Equal([1, 2, 3], rows1);
        Assert.Equal(1, engine.Catalog.Epoch);

        var epoch = await engine.RefreshAsync(
            refresh =>
            {
                refresh.Replace(bars, "bars", SecondBars);
                refresh.Replace(symbols, "symbols", SecondSymbols);
            },
            ct);

        Assert.Equal(2, epoch);
        Assert.Equal(2, engine.Catalog.Epoch);

        // Data only: the shape never moved, so the plan prepared at epoch 1 is still this engine's
        // to run — and it runs against the rows the transaction brought.
        Assert.Equal(1, engine.ShapeEpoch);
        Assert.False(prepared.IsStale);
        var rows2 = await RowsAsync(engine, prepared);
        Assert.Equal([4, 5], rows2);
    }

    /// <summary>
    /// An execution started while the transaction is building sees none of it — not one table of it
    /// — and one started after it has committed sees all of it.
    /// </summary>
    [Fact]
    public async Task A_transaction_is_seen_whole_or_not_at_all()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var bars = Bars();
        var symbols = Symbols();
        var latch = new LatchedSource("latch");
        await using var engine = await EngineAsync(bars, symbols, latch);

        var ct = TestContext.Current.CancellationToken;
        var prepared = await engine.PrepareAsync(
            "SELECT b.seq, s.sector FROM mem.bars AS b JOIN book.symbols AS s "
            + "ON s.ticker = b.symbol ORDER BY b.seq");

        // The latch source is named last, so both POCO sources have built their new snapshots and
        // neither has published one when this returns.
        var transaction = engine.RefreshAsync(
            refresh =>
            {
                refresh.Replace(bars, "bars", SecondBars);
                refresh.Replace(symbols, "symbols", SecondSymbols);
                refresh.Replace(latch, "held", System.Array.Empty<Bar>());
            },
            ct).AsTask();

        await latch.Building;
        var rows3 = await RowsAsync(engine, prepared);
        Assert.Equal([1, 2, 3], rows3);
        Assert.Equal(1, engine.Catalog.Epoch);

        latch.Release();
        Assert.Equal(2, await transaction);
        var rows4 = await RowsAsync(engine, prepared);
        Assert.Equal([4, 5], rows4);
    }

    /// <summary>
    /// The cadence serialises behind a transaction: a refresh the timer starts while one is building
    /// cannot publish first, so the transaction's epoch is the next one and the scheduled refresh's
    /// is the one after.
    /// </summary>
    [Fact]
    public async Task A_scheduled_refresh_serialises_behind_a_transaction()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var bars = Bars();
        var symbols = Symbols();
        var latch = new LatchedSource("latch");
        var clock = new ManualTimerProvider();
        await using var engine = await EngineAsync(
            bars,
            symbols,
            latch,
            new ExecutionOptions
            {
                TimeProvider = clock,
                CatalogRefreshInterval = TimeSpan.FromMinutes(5),
            });

        var ct = TestContext.Current.CancellationToken;
        var transaction = engine.RefreshAsync(
            refresh => refresh.Replace(latch, "held", System.Array.Empty<Bar>()), ct).AsTask();

        await latch.Building;
        clock.Fire();

        latch.Release();

        // Two refreshes, in this order: the transaction was holding the lock when the timer fired.
        Assert.Equal(2, await transaction);
        await WaitForEpochAsync(engine, 3);
    }

    /// <summary>
    /// A replacement naming a table the source does not have, or rows of a type its table was not
    /// built over, is refused before anything is built: no swap, no epoch, and the other entries of
    /// the same transaction untouched.
    /// </summary>
    [Fact]
    public async Task A_refused_entry_changes_nothing()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var bars = Bars();
        var symbols = Symbols();
        await using var engine = await EngineAsync(bars, symbols);

        var ct = TestContext.Current.CancellationToken;
        var prepared = await engine.PrepareAsync("SELECT seq FROM mem.bars ORDER BY seq");

        var unknown = await Assert.ThrowsAsync<SourceContractException>(
            async () => await engine.RefreshAsync(
                refresh =>
                {
                    refresh.Replace(bars, "bars", SecondBars);
                    refresh.Replace(bars, "quotes", SecondBars);
                },
                ct));
        Assert.Contains("no such table", unknown.Message, StringComparison.Ordinal);

        var wrongType = await Assert.ThrowsAsync<SourceContractException>(
            async () => await engine.RefreshAsync(
                refresh =>
                {
                    refresh.Replace(bars, "bars", SecondBars);
                    refresh.Replace(bars, "bars", SecondSymbols);
                },
                ct));
        Assert.Contains("twice", wrongType.Message, StringComparison.Ordinal);

        var mismatched = await Assert.ThrowsAsync<SourceContractException>(
            async () => await engine.RefreshAsync(
                refresh => refresh.Replace(bars, "bars", SecondSymbols),
                ct));
        Assert.Contains("built over", mismatched.Message, StringComparison.Ordinal);

        var foreign = await Assert.ThrowsAsync<ArgumentException>(
            async () => await engine.RefreshAsync(
                refresh => refresh.Replace(Bars(), "bars", SecondBars),
                ct));
        Assert.Contains("not one this engine", foreign.Message, StringComparison.Ordinal);

        Assert.Equal(1, engine.Catalog.Epoch);
        Assert.False(prepared.IsStale);
        var rows5 = await RowsAsync(engine, prepared);
        Assert.Equal([1, 2, 3], rows5);
    }

    /// <summary>
    /// The simple form re-reads every source's registrations and moves the epoch once, and a
    /// prepared query survives it for the same reason a transaction's rows do.
    /// </summary>
    [Fact]
    public async Task The_simple_form_re_reads_every_registration()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        IReadOnlyList<Bar> current = FirstBars;
        var bars = new PocoSourceBuilder("mem", "mem").AddTable("bars", () => current).Build();
        await using var engine = await EngineAsync(bars, Symbols());

        var ct = TestContext.Current.CancellationToken;
        var prepared = await engine.PrepareAsync("SELECT seq FROM mem.bars ORDER BY seq");
        var rows6 = await RowsAsync(engine, prepared);
        Assert.Equal([1, 2, 3], rows6);

        current = SecondBars;
        Assert.Equal(2, await engine.RefreshAsync(ct));

        Assert.False(prepared.IsStale);
        var rows7 = await RowsAsync(engine, prepared);
        Assert.Equal([4, 5], rows7);
    }

    // ---- the parts ----

    private static PocoSource Bars() =>
        new PocoSourceBuilder("mem", "mem").AddTable("bars", FirstBars).Build();

    private static PocoSource Symbols() =>
        new PocoSourceBuilder("book", "book").AddTable("symbols", FirstSymbols).Build();

    private ValueTask<ChalkEngine> EngineAsync(
        ISourceRuntime bars, ISourceRuntime symbols, ExecutionOptions? execution = null) =>
        EngineAsync(bars, symbols, other: null, execution);

    private ValueTask<ChalkEngine> EngineAsync(
        ISourceRuntime bars,
        ISourceRuntime symbols,
        ISourceRuntime? other,
        ExecutionOptions? execution = null) =>
        ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "refresh",
            Sources = other is null ? [bars, symbols] : [bars, symbols, other],
            Planner = sidecar.CreatePlanner(),
            Execution = execution ?? new ExecutionOptions(),
        });

    private static async Task<int[]> RowsAsync(ChalkEngine engine, PreparedQuery query)
    {
        var seen = new List<int>();
        await using var execution = await engine.ExecuteAsync(query, (IReadOnlyList<object?>?)null);
        await foreach (var batch in execution.Batches)
        {
            var seq = (Int32Array)batch.Column(0);
            for (var i = 0; i < batch.Length; i++)
            {
                seen.Add(seq.GetValue(i)!.Value);
            }

            batch.Dispose();
        }

        return [.. seen];
    }

    private static async Task WaitForEpochAsync(ChalkEngine engine, long epoch)
    {
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (engine.Catalog.Epoch < epoch)
        {
            await Task.Delay(5, giveUp.Token);
        }

        Assert.Equal(epoch, engine.Catalog.Epoch);
    }

    /// <summary>
    /// A source that exists to hold a transaction open: its build waits until the test says go, so
    /// "while the transaction is building" is a latch rather than a duration. It serves no rows —
    /// nothing queries it — and declares one table so a transaction may name it.
    /// </summary>
    private sealed class LatchedSource : ISourceRuntime, IRefreshableSource
    {
        private readonly TaskCompletionSource _building =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LatchedSource(string sourceId) => SourceId = sourceId;

        public string SourceId { get; }

        /// <summary>Completes when the engine has reached this source's half of the build.</summary>
        public Task Building => _building.Task;

        /// <summary>Lets the build finish.</summary>
        public void Release() => _release.TrySetResult();

        public SchemaDescriptor DescribeSchema() => new()
        {
            SourceId = SourceId,
            Name = SourceId,
            Kind = Chalk.Ir.SourceKind.Local,
            Capabilities = SourceCapabilities.None,
            Tables =
            [
                new TableDescriptor
                {
                    Name = "held",
                    Columns = [new ColumnDescriptor { Name = "seq", Type = ChalkType.Int32() }],
                    RowCount = 0,
                },
            ],
        };

        public IAsyncEnumerable<RecordBatch> ScanAsync(
            ScanRequest request, ScanContext context, CancellationToken ct) =>
            throw new NotSupportedException("the latch source is never scanned");

        public void ValidateRefresh(IReadOnlyList<SourceRefreshEntry> entries)
        {
        }

        public async ValueTask<ISourceRefreshCommit> PrepareRefreshAsync(
            IReadOnlyList<SourceRefreshEntry> entries, CancellationToken ct)
        {
            _building.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return new NothingToDo();
        }

        private sealed class NothingToDo : ISourceRefreshCommit
        {
            public void Commit()
            {
            }
        }
    }
}
