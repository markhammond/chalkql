using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Chalk.Client;
using Chalk.Execution;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.DuckDb;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using DuckDB.NET.Data;
using Npgsql;

namespace Chalk.Benchmarks;

/// <summary>
/// The three M1 cases (docs/design/05-testing.md §10). Not run in CI: it exists so that "no per-row
/// allocation" is checked by a number rather than by reading code.
///
/// <para>
/// The numbers that matter are <c>Allocated</c>, <c>RowsScanned</c> and <c>PeakPooledBytes</c> — they
/// stay comparable when the hardware changes, and wall-clock does not. The gate is
/// <c>Allocated / RowsScanned</c> well under one byte for the scan+filter case.
/// </para>
/// </summary>
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[MemoryDiagnoser]
[HideColumns("Mean", "Job", "Error", "StdDev", "Median", "RatioSD")]
public class ExecutionBenchmarks
{
    [BenchmarkCategory("scan")]
    [Benchmark(Description = "chalkql scan + filter + project")]
    public async Task<long> ScanFilterProject() =>
        await ConsumeAsync(_scanFilter);

    [BenchmarkCategory("scan")]
    [Benchmark(Baseline = true, Description = "duckdb scan + filter + project")]
    public async Task<long> DuckDbScanFilterProject() =>
        await ConsumeDuckDbAsync(_scanFilterDuckDb!);

    [BenchmarkCategory("group by")]
    [Benchmark(Description = "chalkql group by")]
    public async Task<long> GroupBy() =>
        await ConsumeAsync(_groupBy);

    [BenchmarkCategory("group by")]
    [Benchmark(Baseline = true, Description = "duckdb group by")]
    public async Task<long> DuckDbGroupBy() =>
        await ConsumeDuckDbAsync(_groupByDuckDb!);
    
    [BenchmarkCategory("sort")]
    [Benchmark(Description = "chalkql sort")]
    public async Task<long> Sort() => await ConsumeAsync(_sort);

    [BenchmarkCategory("sort")]
    [Benchmark(Baseline = true, Description = "duckdb sort")]
    public async Task<long> DuckDbSort() => await ConsumeDuckDbAsync(_sortDuckDb!);

    [BenchmarkCategory("window")]
    [Benchmark(Description = "chalkql window")]
    public async Task<long> Window() => await ConsumeAsync(_window);

    /// <summary>The same window over the clustered table, whose leaf is a slice of the copy.</summary>
    [BenchmarkCategory("window")]
    [Benchmark(Description = "chalkql window (clustered)")]
    public async Task<long> WindowClustered() => await ConsumeAsync(_windowClustered);

    [BenchmarkCategory("window")]
    [Benchmark(Baseline = true, Description = "duckdb window")]
    public async Task<long> DuckDbWindow() => await ConsumeDuckDbAsync(_windowDuckDb!);

    [BenchmarkCategory("hop")]
    [Benchmark(Description = "chalkql hop")]
    public async Task<long> Hop() => await ConsumeAsync(_hop);

    [BenchmarkCategory("hop")]
    [Benchmark(Baseline = true, Description = "duckdb hop")]
    public async Task<long> DuckDbHop() => await ConsumeDuckDbAsync(_hopDuckDb!);
    
    public class ExecutionBenchmarksSettings
    {
        public const int BarCount = 20_160 * 100;
        
        static ExecutionBenchmarksSettings()
        {
            Bars = Fixtures.Bars(Fixtures.DefaultSeed, BarCount);
            SymbolRows = Fixtures.SymbolRows();
            
            var source = new PocoSourceBuilder("mem")
                // bars and symbols use snake_case so the naming policy is exercised; lineitem names its
                // columns explicitly because TPC-H's l_ prefix is not a naming policy.
                .NamingPolicy(PocoNamingPolicy.SnakeCase)
                .AddTable("bars", Bars, t => t
                    .OrderedBy(b => b.Ts)
                    .ThenBy(b => b.Symbol)
                    .UniqueKey(b => b.Ts, b => b.Symbol)
                    // M2 (D40). The (symbol, ts) index is a permutation — one int per row — and the
                    // (ts, symbol) one is the declared collation, so it costs nothing at all (§1).
                    .UniqueIndex(b => b.Symbol, b => b.Ts)
                    .Index(b => b.Ts, b => b.Symbol)
                    // F14: every bar names one of the five `symbols` rows. The fluent spelling of F16,
                    // which finds `symbols` by row type.
                    .ForeignKey(b => b.Symbol).References<SymbolRow>(s2 => s2.Symbol, verify: false))
                // D257: the same two million bars again, with the (symbol, ts) index clustered and
                // its copy covering exactly what the moving average projects. `bars` is untouched,
                // so the two window figures are the same statement over the same rows and differ in
                // nothing but how the leaf reads them — a gather through the permutation there,
                // slices of the copy here.
                .AddTable("bars_clustered", Bars, t => t
                    .OrderedBy(b => b.Ts)
                    .ThenBy(b => b.Symbol)
                    .UniqueKey(b => b.Ts, b => b.Symbol)
                    .UniqueClusteredIndex(b => b.Symbol, b => b.Ts)
                        .Covering(b => b.Close))
                .AddTable("symbols", SymbolRows, t => t
                    .OrderedBy(s => s.Symbol)
                    .UniqueKey(s => s.Symbol))
                .Build();

            Fixture = [source];
            ClusteredCopyBytes =
                (source.FindIndex<Bar>("bars_clustered", "ix_bars_clustered_symbol_ts")
                    as ClusteredIndex<Bar>)?.CopyBytes ?? -1;
        }

        public static readonly IReadOnlyList<Bar> Bars;
        public static readonly IReadOnlyList<SymbolRow> SymbolRows;
        public static readonly IReadOnlyList<PocoSource> Fixture;

        /// <summary>What the clustered index's second column set holds, over every bar (D257).</summary>
        public static readonly long ClusteredCopyBytes;
    }

    /// <summary>
    /// The two window-shaped statements, and the total orders that make them deterministic. A
    /// benchmark that compares two engines must compare the same sequence of rows: without an
    /// <c>ORDER BY</c> the window's output happens to arrive in (symbol, ts) order out of Chalk's
    /// index-ordered scan and in whatever order DuckDB's hash-partitioned window produces, and a hop
    /// has no order at all. Both statements are measured with and without it.
    /// </summary>
    internal const string WindowSql =
        "SELECT symbol, ts, AVG(\"close\") OVER "
        + "(PARTITION BY symbol ORDER BY ts ROWS 19 PRECEDING) AS sma20 FROM bars";

    internal const string WindowOrder = " ORDER BY symbol, ts";

    internal const string HopSql =
        "SELECT symbol, window_start, window_end, volume FROM TABLE(HOP(TABLE bars, "
        + "DESCRIPTOR(ts), INTERVAL '5' MINUTE, INTERVAL '15' MINUTE))";

    /// <summary>
    /// Every output column, because no subset of them is unique: one (symbol, window_start) pair
    /// holds three bars' volumes, and two bars in one window may carry the same volume. This is the
    /// strongest order the hop's own output can be given.
    /// </summary>
    internal const string HopOrder = " ORDER BY symbol, window_start, window_end, volume";

    private ChalkEngine _engine = null!;
    private SidecarFixture _sidecar = null!;
    private PreparedQuery _scanFilter = null!;
    private PreparedQuery _groupBy = null!;
    private PreparedQuery _sort = null!;
    private PreparedQuery _window = null!;
    private PreparedQuery _windowClustered = null!;
    private PreparedQuery _hop = null!;
    private PreparedQuery _windowUnordered = null!;
    private PreparedQuery _hopUnordered = null!;

    private DuckDBCommand? _scanFilterDuckDb;
    private DuckDBCommand? _groupByDuckDb;
    private DuckDBCommand? _sortDuckDb;
    private DuckDBCommand? _windowDuckDb;
    private DuckDBCommand? _hopDuckDb;
    private DuckDBCommand? _windowUnorderedDuckDb;
    private DuckDBCommand? _hopUnorderedDuckDb;

    
    public int Rows { get; set; }

    public IEnumerable<int> RowCounts => [ExecutionBenchmarksSettings.BarCount];
    
    /// <summary>Rows the last measured iteration read; divide <c>Allocated</c> by it.</summary>
    public static long LastRowsScanned { get; private set; }

    /// <summary>Rows the last measured iteration handed back — the window gate's denominator.</summary>
    public static long LastRowsProduced { get; private set; }

    private DuckDBConnection? _duckDbConnection;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _sidecar = await SidecarFixture.StartAsync();
        if (!_sidecar.IsAvailable)
        {
            throw new InvalidOperationException(_sidecar.SkipReason);
        }

        _engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = ExecutionBenchmarksSettings.Fixture,
            Planner = _sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { BatchSize = 4096, OutputMemory = OutputMemory.Pooled },
        });

        _scanFilter = await _engine.PrepareAsync(
            "SELECT symbol, ts, \"close\" FROM bars WHERE volume > 5000");
        
        _groupBy = await _engine.PrepareAsync(
            "SELECT symbol, COUNT(*) AS n, SUM(volume) AS vol FROM bars GROUP BY symbol");
        _sort = await _engine.PrepareAsync("SELECT symbol, ts, \"close\" FROM bars ORDER BY symbol, ts");

        // Windowed per-symbol moving average: it streams out of the (symbol, ts) index rather than
        // sorting, and the operator buffers the whole input and slides a twenty-row frame across it.
        // The benchmark's own statement carries a total order, so the two engines are asked for the
        // same sequence of rows and not merely the same set; the unordered form is kept beside it so
        // the harness can report what the order costs.
        _window = await _engine.PrepareAsync(WindowSql + WindowOrder);
        _windowUnordered = await _engine.PrepareAsync(WindowSql);

        // D257: the same statement over the clustered table. The window's input arrives in
        // (symbol, ts) order either way; what differs is that this one is read as slices of a copy
        // held in that order rather than gathered a row at a time through the permutation.
        _windowClustered = await _engine.PrepareAsync(
            "SELECT symbol, ts, AVG(\"close\") OVER "
            + "(PARTITION BY symbol ORDER BY ts ROWS 19 PRECEDING) AS sma20 FROM bars_clustered");

        // The hop case. Three overlapping fifteen-minute windows per row, so the operator produces three output rows
        // for every input one — which is what makes bytes per *output* row the number to read for it.
        _hop = await _engine.PrepareAsync(HopSql + HopOrder);
        _hopUnordered = await _engine.PrepareAsync(HopSql);

        _duckDbConnection = await CreateAndPopulateDuckDbMemoryTable();
    }

    private static DuckDBCommand CreateDuckDbCommand(DuckDBConnection connection, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Prepare();
        return cmd;
    }
    
    private async Task<DuckDBConnection> CreateAndPopulateDuckDbMemoryTable()
    {
        var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await using (var configCommand = connection.CreateCommand())
        {
            configCommand.CommandText = "SET threads = 1;";
            await configCommand.ExecuteNonQueryAsync();
        }
        
        await using (var command = connection.CreateCommand())
        {
            command.CommandText 
                = """
                  CREATE TABLE symbols (
                      symbol TEXT PRIMARY KEY,
                      base VARCHAR NOT NULL,
                      quote VARCHAR NOT NULL,
                      tick_size DECIMAL(18, 8) NOT NULL,
                      tags VARCHAR[]
                  );

                  CREATE TABLE bars (
                      symbol TEXT NOT NULL,
                      ts TIMESTAMP NOT NULL,
                      open DOUBLE NOT NULL,
                      high DOUBLE NOT NULL,
                      low DOUBLE NOT NULL,
                      close DOUBLE NOT NULL,
                      volume BIGINT NOT NULL,
                      vwap DECIMAL(18, 8),
                      trade_count INTEGER,

                      FOREIGN KEY (symbol) REFERENCES symbols(symbol)
                  );
                  """;

            await command.ExecuteNonQueryAsync();
        }
        
      
        using var appender = connection.CreateAppender("symbols");

        foreach (var symbol in ExecutionBenchmarksSettings.SymbolRows)
        {
            appender.AppendRow(symbol, (row, t) =>
            {
                row.AppendValue(t.Symbol.ToString())
                    .AppendValue(t.Base.ToString())
                    .AppendValue(t.Quote.ToString())
                    .AppendValue(t.TickSize)
                    .AppendValue(t.Tags);
            });
        }
        appender.Close();
      
        using var barAppender = connection.CreateAppender("bars");

        foreach (var bar in ExecutionBenchmarksSettings.Bars)
        {
            barAppender.AppendRow(bar, (row, t) =>
            {
                row.AppendValue(t.Symbol.ToString())
                    .AppendValue(t.Ts)
                    .AppendValue(t.Open)
                    .AppendValue(t.High)
                    .AppendValue(t.Low)
                    .AppendValue(t.Close)
                    .AppendValue(t.Volume)
                    .AppendValue(t.Vwap)
                    .AppendValue(t.TradeCount);
            });
        }
        barAppender.Close();
        
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = 
                """
                  CREATE UNIQUE INDEX bars_ts_symbol
                      ON bars (ts, symbol);

                  CREATE UNIQUE INDEX bars_symbol_ts
                      ON bars (symbol, ts);
                """;

            await command.ExecuteNonQueryAsync();
        }
        
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "ANALYZE;";
            await command.ExecuteNonQueryAsync();
        }

        _scanFilterDuckDb =
            CreateDuckDbCommand(connection, """SELECT symbol, ts, "close" FROM bars WHERE volume > 5000""");
        
        _groupByDuckDb =
            CreateDuckDbCommand(connection, """SELECT symbol, COUNT(*) AS n, SUM(volume) AS vol FROM bars GROUP BY symbol""");
        
        _sortDuckDb = 
            CreateDuckDbCommand(connection, """SELECT symbol, ts, "close" FROM bars ORDER BY symbol, ts""");

        // Windowed per-symbol moving average: DuckDB has only a (ts, symbol) unique index here, so it
        // sorts where Chalk streams out of the (symbol, ts) permutation. The same total order as the
        // Chalk statement, so the two engines are asked for the same sequence of rows.
        _windowDuckDb = CreateDuckDbCommand(connection, WindowSql + WindowOrder);
        _windowUnorderedDuckDb = CreateDuckDbCommand(connection, WindowSql);

        // The hop case. Three overlapping fifteen-minute windows per row, so the operator produces three output rows
        // for every input one — which is what makes bytes per *output* row the number to read for it.
        const string hopDuckDbSql =
            """
                SELECT
                    b.symbol,
                    time_bucket(INTERVAL '5 minutes', b.ts)
                        - hop.i * INTERVAL '5 minutes' AS window_start,
                            time_bucket(INTERVAL '5 minutes', b.ts)
                - hop.i * INTERVAL '5 minutes'
                + INTERVAL '15 minutes' AS window_end,
                    b.volume
                FROM bars AS b
                CROSS JOIN range(3) AS hop(i)
                """;

        _hopDuckDb = CreateDuckDbCommand(connection, hopDuckDbSql + HopOrder);
        _hopUnorderedDuckDb = CreateDuckDbCommand(connection, hopDuckDbSql);

        return connection;
    }
    
    internal ChalkEngine Engine => _engine;

    internal PreparedQuery ScanFilterQuery => _scanFilter;

    internal PreparedQuery WindowQuery => _window;

    internal PreparedQuery HopQuery => _hop;

    internal PreparedQuery WindowUnorderedQuery => _windowUnordered;

    internal PreparedQuery HopUnorderedQuery => _hopUnordered;

    internal DuckDBCommand WindowDuckDb => _windowDuckDb!;

    internal DuckDBCommand HopDuckDb => _hopDuckDb!;

    internal DuckDBCommand WindowUnorderedDuckDb => _windowUnorderedDuckDb!;

    internal DuckDBCommand HopUnorderedDuckDb => _hopUnorderedDuckDb!;

    /// <summary>One run of a DuckDB command, for the harness's side-by-side table.</summary>
    internal static async Task<long> DrainDuckDbAsync(DuckDBCommand command)
    {
        long rows = 0;
        await foreach (var batch in command.ExecuteArrowBatchesAsync().ConfigureAwait(false))
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return rows;
    }

    internal PreparedQuery GroupByQuery => _groupBy;

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_duckDbConnection is not null)
            await _duckDbConnection.DisposeAsync();
        await _engine.DisposeAsync();
        await _sidecar.DisposeAsync();
    }

    private async Task<long> ConsumeAsync(PreparedQuery query)
    {
        await using var execution = await _engine.ExecuteAsync(query);
        long rows = 0;
        await foreach (var batch in execution.Batches)
        {
            rows += batch.Length;
            batch.Dispose();
        }

        LastRowsScanned = execution.Stats.RowsScanned;
        LastRowsProduced = rows;
        return rows;
    }
    
    private async Task<long> ConsumeDuckDbAsync(DuckDBCommand query)
    {
        long rows = 0;
        await foreach (var batch in query.ExecuteArrowBatchesAsync())
        {
            rows += batch.Length;
            batch.Dispose();
        }

        LastRowsProduced = rows;
        return rows;
    }
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (Array.IndexOf(args, "--allocation-profile") is var at and >= 0)
        {
            return await AllocationProfile.RunAsync(
                at + 1 < args.Length ? args[at + 1] : null).ConfigureAwait(false);
        }

        if (args.Contains("--allocation"))
        {
            return await MeasureAllocationAsync();
        }

        // The rest of the command line is BenchmarkDotNet's, so one case can be measured on its own:
        // `-- --filter "*group by*"` is how the aggregate work of D255 was read.
        BenchmarkRunner.Run<ExecutionBenchmarks>(
            DefaultConfig.Instance.WithOptions(ConfigOptions.DisableOptimizationsValidator), args);
        return 0;
    }

    private static async Task<int> MeasureAllocationAsync()
    {
        var benchmarks = new ExecutionBenchmarks();
        await benchmarks.SetupAsync();
        try
        {
            // Warm up JIT, pooled allocators, and per-column scratch retained across successive scans.
            await benchmarks.ScanFilterProject();

            var before = GC.GetAllocatedBytesForCurrentThread();
            var rowsProduced = await benchmarks.ScanFilterProject();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var scanned = ExecutionBenchmarks.LastRowsScanned;

            Console.WriteLine($"scan+filter+project: {rowsProduced} rows produced, {scanned} scanned");
            Console.WriteLine($"allocated {allocated} bytes = {(double)allocated / scanned:F4} bytes/row");
            var pass = (double)allocated / scanned < 1.0;
            Console.WriteLine(pass ? "PASS: under one byte per row" : "FAIL: the scan path allocates per row");

            // The hop still buffers, so its figure is about how much of that buffering escapes the
            // arena and is not comparable with the scan path's. The window no longer does: since
            // D258.4 a frame that ends at or before the current row streams, and the plan is gated
            // below rather than reported here (33-aggregate-performance.md §3.9, step 26o).
            Console.WriteLine();
            Console.WriteLine("--- measured, not gated: the hop plan ---");

            // The window's total order is free — the planner sees the collation its output already
            // has and adds no sort — so the gate run measures the statement the benchmark runs. The
            // hop's is not: see the note below MeasureTotalOrderAsync's table.
            await MeasurePlanAsync(benchmarks.Engine, "window", benchmarks.WindowQuery)
                .ConfigureAwait(false);
            await MeasurePlanAsync(benchmarks.Engine, "hop (no ORDER BY)", benchmarks.HopUnorderedQuery)
                .ConfigureAwait(false);

            // D257: the same statement over the clustered table, beside the figure above. The copy's
            // size is what a host pays for it, so it is reported here rather than left to be guessed.
            await benchmarks.WindowClustered();
            var clusteredBefore = GC.GetAllocatedBytesForCurrentThread();
            var clusteredRows = await benchmarks.WindowClustered();
            var clusteredAllocated = GC.GetAllocatedBytesForCurrentThread() - clusteredBefore;
            Console.WriteLine(
                $"window (clustered): {clusteredRows} rows produced, "
                + $"{ExecutionBenchmarks.LastRowsScanned} scanned");
            Console.WriteLine(
                $"allocated {clusteredAllocated} bytes = "
                + $"{(double)clusteredAllocated / Math.Max(clusteredRows, 1):F4} bytes/output row");
            var copyBytes = ExecutionBenchmarks.ExecutionBenchmarksSettings.ClusteredCopyBytes;
            var bars = ExecutionBenchmarks.ExecutionBenchmarksSettings.Bars.Count;
            Console.WriteLine(
                $"clustered copy: {copyBytes} bytes over {bars} bars = "
                + $"{copyBytes / (double)bars:F1} bytes/row (symbol, ts and close, in key order)");

            await MeasureTotalOrderAsync(benchmarks).ConfigureAwait(false);

            var window = await MeasureWindowGateAsync(benchmarks).ConfigureAwait(false);
            var sort = await MeasureSortGateAsync(benchmarks).ConfigureAwait(false);
            var gates = await MeasureGatesAsync(benchmarks).ConfigureAwait(false);
            return pass && window && sort && gates ? 0 : 1;
        }
        finally
        {
            await benchmarks.CleanupAsync();
        }
    }

    /// <summary>
    /// What the benchmark's total order costs, on both engines, for the two statements that did not
    /// have one. A comparison of two engines over an unordered result compares two different
    /// sequences of rows; this is the price of making it one sequence, said out loud rather than
    /// assumed to be zero.
    /// </summary>
    /// <remarks>
    /// Bytes are counted over every thread — DuckDB's reader does not stay on the calling one — and
    /// they are <em>managed</em> bytes only: DuckDB's own buffers are native and invisible to the
    /// collector, so its allocation figure is the .NET side of the boundary and nothing more. Wall
    /// clock is best of three after a warm-up, reported and asserted nowhere.
    /// </remarks>
    /// <remarks>
    /// Seven of the eight rows are measured. The ordered hop is not: it is the one statement here
    /// whose order is expensive, and measuring it five times would cost this run more than
    /// everything else in it put together. It is an <c>--allocation-profile</c> variant instead, so
    /// the number stays reachable without being paid for on every gate check.
    /// </remarks>
    private static async Task MeasureTotalOrderAsync(ExecutionBenchmarks benchmarks)
    {
        Console.WriteLine();
        Console.WriteLine(
            "--- measured, not gated: what the benchmark's total order costs, both engines ---");
        Console.WriteLine(
            "(duckdb bytes are managed bytes only: its own buffers are native and the collector "
            + "never sees them)");

        await OrderPairAsync(
            "window",
            () => DrainAsync(benchmarks.Engine, benchmarks.WindowUnorderedQuery),
            () => DrainAsync(benchmarks.Engine, benchmarks.WindowQuery),
            () => ExecutionBenchmarks.DrainDuckDbAsync(benchmarks.WindowUnorderedDuckDb),
            () => ExecutionBenchmarks.DrainDuckDbAsync(benchmarks.WindowDuckDb)).ConfigureAwait(false);

        await OrderRowAsync(
            "hop, chalkql, no ORDER BY",
            () => DrainAsync(benchmarks.Engine, benchmarks.HopUnorderedQuery)).ConfigureAwait(false);

        // Measured again since D267. It was a profile variant while it cost about 21 s an execution
        // — five of those is more than the rest of this harness put together — and the encoded key
        // brought it low enough to belong in its own table. What it still allocates is the
        // concatenation of thirty million rows, which no retention budget here can keep: ADR 0051 §3
        // and the gate below, whose pair is small enough that the arena keeps every chunk.
        await OrderRowAsync(
            "hop, chalkql, with ORDER BY",
            () => DrainAsync(benchmarks.Engine, benchmarks.HopQuery)).ConfigureAwait(false);

        await OrderRowAsync(
            "hop, duckdb,  no ORDER BY",
            () => ExecutionBenchmarks.DrainDuckDbAsync(benchmarks.HopUnorderedDuckDb))
            .ConfigureAwait(false);
        await OrderRowAsync(
            "hop, duckdb,  with ORDER BY",
            () => ExecutionBenchmarks.DrainDuckDbAsync(benchmarks.HopDuckDb)).ConfigureAwait(false);
    }

    private static async Task OrderPairAsync(
        string what,
        Func<Task<long>> chalkBefore,
        Func<Task<long>> chalkAfter,
        Func<Task<long>> duckBefore,
        Func<Task<long>> duckAfter)
    {
        await OrderRowAsync($"{what}, chalkql, no ORDER BY", chalkBefore).ConfigureAwait(false);
        await OrderRowAsync($"{what}, chalkql, with ORDER BY", chalkAfter).ConfigureAwait(false);
        await OrderRowAsync($"{what}, duckdb,  no ORDER BY", duckBefore).ConfigureAwait(false);
        await OrderRowAsync($"{what}, duckdb,  with ORDER BY", duckAfter).ConfigureAwait(false);
    }

    private static async Task OrderRowAsync(string what, Func<Task<long>> run)
    {
        await run().ConfigureAwait(false);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var rows = await run().ConfigureAwait(false);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        var best = TimeSpan.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await run().ConfigureAwait(false);
            var taken = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (taken < best)
            {
                best = taken;
            }
        }

        Console.WriteLine(
            $"{what,-34} {rows,10} rows {allocated,14} bytes "
            + $"{(double)allocated / Math.Max(rows, 1),10:F4} bytes/row "
            + $"{best.TotalMilliseconds,9:F1} ms (best of 3, reported)");
    }

    private static async Task<long> DrainAsync(ChalkEngine engine, PreparedQuery query)
    {
        await using var execution = await engine.ExecuteAsync(query).ConfigureAwait(false);
        long rows = 0;
        await foreach (var batch in execution.Batches)
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return rows;
    }

    /// <summary>
    /// One measured plan: bytes per execution and bytes per output row, with what the execution's
    /// own arena did to earn them (33-aggregate-performance.md §3, step 1).
    /// </summary>
    /// <remarks>
    /// The arena is this measurement's own rather than one of the engine's pooled ones, so its
    /// counters describe exactly the execution being measured: how many rentals it took, how many of
    /// them the pools could not serve — the arena's own fall back to the heap — and how many bytes
    /// left the pools again when the run ended.
    /// </remarks>
    private static async Task<(long Bytes, long Rows)> MeasurePlanAsync(
        ChalkEngine engine, string what, PreparedQuery query)
    {
        using var arena = new ExecutionArena();

        // Warm: the JIT, the operator tree and the arena's pools. Two runs, because the first one
        // fills the pools and the second is the first that can reuse them.
        await ConsumeOnceAsync(engine, query, arena).ConfigureAwait(false);
        await ConsumeOnceAsync(engine, query, arena).ConfigureAwait(false);

        var rentals = arena.Rentals;
        var rented = arena.RentedBytes;
        var fallbacks = arena.HeapFallbacks;
        var fallbackBytes = arena.HeapFallbackBytes;
        var unpooled = arena.UnpooledReturnBytes;
        var trimmed = arena.TrimmedBytes;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var (rows, scanned, peak) = await ConsumeOnceAsync(engine, query, arena).ConfigureAwait(false);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Console.WriteLine($"{what}: {rows} rows produced, {scanned} scanned");
        Console.WriteLine(
            $"{what}: allocated {allocated} bytes per execution = "
            + $"{(double)allocated / Math.Max(rows, 1):F4} bytes/output row, "
            + $"{(double)allocated / Math.Max(scanned, 1):F4} bytes/scanned row (reported)");
        Console.WriteLine(
            $"{what}: arena {arena.Rentals - rentals} rentals over "
            + $"{arena.RentedBytes - rented} bytes, "
            + $"{arena.HeapFallbacks - fallbacks} heap fallbacks costing "
            + $"{arena.HeapFallbackBytes - fallbackBytes} bytes, "
            + $"{arena.UnpooledReturnBytes - unpooled} bytes returned unpooled, "
            + $"{arena.TrimmedBytes - trimmed} bytes trimmed, peak pooled {peak} bytes");
        return (allocated, rows);
    }

    /// <summary>
    /// The window plan's gate (D258.4, step 26o): the twenty-row moving average allocates nothing per
    /// row, and what it does allocate is a fixed cost per execution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two runs of the same plan shape over different numbers of rows, at the <b>same batch size</b>,
    /// so the slope between them is what one more row costs the pipeline — per-row and the share of a
    /// batch a row carries, together. The batch size is the engine's own 4 096 rather than a count
    /// divided into the rows: a batch of half a million rows measures the arena's size classes moving
    /// under a cold pool, which is not what this plan is being asked about.
    /// </para>
    /// <para>
    /// The two statements differ only in where the index range starts, so both compile to the same
    /// three nodes over the same index; the shapes are printed and the gate fails rather than skips
    /// if the planner ever gives them different ones. Measured on the pipeline, without the Arrow
    /// output boundary, which is the shape 15-zero-allocation-execution.md §5 gates.
    /// </para>
    /// </remarks>
    private static async Task<bool> MeasureWindowGateAsync(ExecutionBenchmarks benchmarks)
    {
        const int BatchSize = 4096;

        // The fixture's five symbols each hold a fifth of the bars, and the (symbol, ts) index makes
        // a range on the symbol prefix a lookup rather than a filter: two symbols against four.
        const string Small = "SELECT symbol, ts, AVG(\"close\") OVER "
            + "(PARTITION BY symbol ORDER BY ts ROWS 19 PRECEDING) AS sma20 "
            + "FROM bars WHERE symbol >= 'SOLUSDT'";
        const string Large = "SELECT symbol, ts, AVG(\"close\") OVER "
            + "(PARTITION BY symbol ORDER BY ts ROWS 19 PRECEDING) AS sma20 "
            + "FROM bars WHERE symbol >= 'BTCUSDT'";

        Console.WriteLine();
        Console.WriteLine("--- the window plan, gated (D258.4) ---");

        var small = await benchmarks.Engine.PrepareAsync(Small).ConfigureAwait(false);
        var large = await benchmarks.Engine.PrepareAsync(Large).ConfigureAwait(false);
        var smallShape = AllocationProfile.Shape(small.Plan);
        var largeShape = AllocationProfile.Shape(large.Plan);
        var smallPlan = await small.CompiledAsync(CancellationToken.None).ConfigureAwait(false);
        var largePlan = await large.CompiledAsync(CancellationToken.None).ConfigureAwait(false);

        using var arena = new ExecutionArena();
        var smallRows = await RowsAsync(smallPlan, arena).ConfigureAwait(false);
        var largeRows = await RowsAsync(largePlan, arena).ConfigureAwait(false);

        var streamed = await WindowPathAsync(smallPlan, arena).ConfigureAwait(false);
        Console.WriteLine($"  shape: {smallShape}");
        Console.WriteLine(
            $"  windows: {streamed.Streamed} streaming, {streamed.Buffered} buffered");

        if (smallShape != largeShape || largeRows <= smallRows || smallRows == 0)
        {
            Console.WriteLine(
                $"window plan: FAIL — the pair needs one plan shape and two row counts; got "
                + $"{smallRows} rows as {smallShape} and {largeRows} as {largeShape}");
            return false;
        }

        var smallBytes = await MeasureAsync(
            () => DrainPipelineAsync(smallPlan.WithBatchSize(BatchSize), arena)).ConfigureAwait(false);
        var largeBytes = await MeasureAsync(
            () => DrainPipelineAsync(largePlan.WithBatchSize(BatchSize), arena)).ConfigureAwait(false);

        var perRow = (largeBytes - smallBytes) / (double)(largeRows - smallRows);
        var fixedBytes = smallBytes - (perRow * smallRows);
        var perRowPass = Math.Abs(perRow) <= 0.1;
        var fixedPass = fixedBytes <= 65_536;

        Console.WriteLine(
            $"  per row: {perRow:F4} bytes ({smallBytes} bytes over {smallRows} rows, "
            + $"{largeBytes} over {largeRows}, batch size {BatchSize} either way) "
            + (perRowPass ? "PASS (<= 0.1)" : "FAIL (<= 0.1)"));
        Console.WriteLine(
            $"  per execution, fixed: {fixedBytes:F0} bytes, the intercept of those two "
            + (fixedPass ? "PASS (<= 65536)" : "FAIL (<= 65536)"));
        return perRowPass && fixedPass;
    }

    /// <summary>
    /// The ordered hop's gate (D267 d, <c>41-blocking-sort.md</c> §4): the blocking sort allocates
    /// nothing per row it sorts, and what it does allocate is a fixed cost per execution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same pair-of-sizes reading as the window's, over the statement design 33 §3.2 measured:
    /// the hop with the benchmark's total order, cut down by a bound on <c>window_start</c> so that
    /// the sort's own buffers stay inside the arena's default retention. That bound is the point
    /// rather than a convenience — a working set larger than <c>RetainBytes</c> cannot survive to
    /// the next execution whatever the sort does with it, so a pair that exceeded it would measure
    /// the arena's retention policy and not this operator (ADR 0051 §3). The unbounded statement is
    /// in the table above, beside DuckDB's.
    /// </para>
    /// <para>
    /// Both statements hop and filter the same ten million bars, so the scan and the hop are the
    /// same work in both and the slope between them is what one more <em>sorted</em> row costs.
    /// </para>
    /// </remarks>
    private static async Task<bool> MeasureSortGateAsync(ExecutionBenchmarks benchmarks)
    {
        const int BatchSize = 4096;
        const string Small = ExecutionBenchmarks.HopSql
            + " WHERE window_start < TIMESTAMP '2026-01-10 00:00:00'" + ExecutionBenchmarks.HopOrder;
        const string Large = ExecutionBenchmarks.HopSql
            + " WHERE window_start < TIMESTAMP '2026-01-19 00:00:00'" + ExecutionBenchmarks.HopOrder;

        Console.WriteLine();
        Console.WriteLine("--- the blocking sort, gated (D267) ---");

        var small = await benchmarks.Engine.PrepareAsync(Small).ConfigureAwait(false);
        var large = await benchmarks.Engine.PrepareAsync(Large).ConfigureAwait(false);
        var smallShape = AllocationProfile.Shape(small.Plan);
        var largeShape = AllocationProfile.Shape(large.Plan);
        var smallPlan = await small.CompiledAsync(CancellationToken.None).ConfigureAwait(false);
        var largePlan = await large.CompiledAsync(CancellationToken.None).ConfigureAwait(false);

        using var arena = new ExecutionArena();
        var smallRows = await RowsAsync(smallPlan, arena).ConfigureAwait(false);
        var largeRows = await RowsAsync(largePlan, arena).ConfigureAwait(false);

        var stats = new ExecutionStats();
        await foreach (var batch in largePlan.ExecuteColumnarAsync(
            [], stats, arena, CancellationToken.None))
        {
            _ = batch.Count;
        }

        Console.WriteLine($"  shape: {smallShape}");
        Console.WriteLine(
            $"  sorts: {stats.SortsEncoded64} on one encoded word, {stats.SortsEncoded128} on two, "
            + $"{stats.SortsCompared} on the comparer, {stats.SortsTieBroken} tie-broken");

        if (smallShape != largeShape || largeRows <= smallRows || smallRows == 0)
        {
            Console.WriteLine(
                "sort plan: FAIL — the pair needs one plan shape and two row counts; got "
                + $"{smallRows} rows as {smallShape} and {largeRows} as {largeShape}");
            return false;
        }

        var fallbacks = arena.HeapFallbacks;
        var smallBytes = await MeasureAsync(
            () => DrainPipelineAsync(smallPlan.WithBatchSize(BatchSize), arena)).ConfigureAwait(false);
        var largeBytes = await MeasureAsync(
            () => DrainPipelineAsync(largePlan.WithBatchSize(BatchSize), arena)).ConfigureAwait(false);

        var perRow = (largeBytes - smallBytes) / (double)(largeRows - smallRows);
        var fixedBytes = smallBytes - (perRow * smallRows);
        var perRowPass = Math.Abs(perRow) <= 0.1;
        var fixedPass = fixedBytes <= 65_536;
        var pooledPass = arena.HeapFallbacks == fallbacks;

        Console.WriteLine(
            $"  per row: {perRow:F4} bytes ({smallBytes} bytes over {smallRows} rows, "
            + $"{largeBytes} over {largeRows}, batch size {BatchSize} either way) "
            + (perRowPass ? "PASS (<= 0.1)" : "FAIL (<= 0.1)"));
        Console.WriteLine(
            $"  per execution, fixed: {fixedBytes:F0} bytes, the intercept of those two "
            + (fixedPass ? "PASS (<= 65536)" : "FAIL (<= 65536)"));
        Console.WriteLine(
            $"  arena: {arena.HeapFallbacks - fallbacks} heap fallbacks over the four measured runs "
            + (pooledPass ? "PASS (chunks below the retention are pooled)" : "FAIL (expected none)"));
        return perRowPass && fixedPass && pooledPass;
    }

    /// <summary>Which window operator a plan's nodes took, for the gate's own report (D258.4).</summary>
    private static async Task<(long Streamed, long Buffered)> WindowPathAsync(
        CompiledPlan compiled, ExecutionArena arena)
    {
        var stats = new ExecutionStats();
        await foreach (var batch in compiled.ExecuteColumnarAsync(
            [], stats, arena, CancellationToken.None))
        {
            _ = batch.Count;
        }

        return (stats.WindowsStreamed, stats.WindowsBuffered);
    }

    /// <summary>
    /// Zero allocation execution.
    /// </summary>
    private static async Task<bool> MeasureGatesAsync(ExecutionBenchmarks benchmarks)
    {
        var passed = true;
        Console.WriteLine();
        Console.WriteLine("--- zero allocation execution ---");

        // 1. Steady state, per batch: the slope of bytes-per-execution over 2 / 7 / 25 / 99 batches,
        //    one arena reused, pooled output. Measured on the pipeline — "anywhere between a source
        //    and the root" — because the Arrow batch the host receives is the boundary the third
        //    line reports rather than this one's subject.
        var compiled = await benchmarks.ScanFilterQuery
            .CompiledAsync(CancellationToken.None).ConfigureAwait(false);
        using var arena = new ExecutionArena();
        var counts = new[] { 2, 7, 25, 99 };
        var pipeline = new long[counts.Length];
        var endToEnd = new long[counts.Length];
        for (var i = 0; i < counts.Length; i++)
        {
            var sized = compiled.WithBatchSize(BatchSizeFor(counts[i]));
            pipeline[i] = await MeasureAsync(
                () => DrainPipelineAsync(sized, arena)).ConfigureAwait(false);
            endToEnd[i] = await MeasureAsync(
                () => DrainOutputAsync(sized, arena)).ConfigureAwait(false);
            Console.WriteLine(
                $"{counts[i],3} batches: pipeline {pipeline[i]} bytes, with output {endToEnd[i]} bytes");
        }

        var slope = (pipeline[^1] - pipeline[0]) / (double)(counts[^1] - counts[0]);
        var perBatch = Math.Abs(slope) <= 64;
        passed &= perBatch;
        Console.WriteLine(
            $"per batch, steady state: {slope:F1} bytes/batch "
            + (perBatch ? "PASS (tolerance 64)" : "FAIL (tolerance 64)"));

        // 2. Fixed per execution: the one-batch LIMIT 5 query, end to end including its one output
        //    batch, because that is what a host actually pays for a small query.
        var limit = await benchmarks.Engine
            .PrepareAsync("SELECT symbol, ts, \"close\" FROM bars LIMIT 5").ConfigureAwait(false);
        var fixedBytes = await FixedBytesAsync(benchmarks.Engine, limit).ConfigureAwait(false);
        var limitPlan = await limit.CompiledAsync(CancellationToken.None).ConfigureAwait(false);
        var limitPipeline = await MeasureAsync(
            () => DrainPipelineAsync(limitPlan, arena)).ConfigureAwait(false);
        var fixedPass = fixedBytes <= 4096;
        passed &= fixedPass;
        Console.WriteLine(
            $"per execution, fixed (LIMIT 5): {fixedBytes} bytes end to end, "
            + $"{limitPipeline} bytes in the pipeline "
            + (fixedPass ? "PASS (<= 4096)" : "FAIL (<= 4096)"));

        // 3. The output boundary, reported rather than gated: the Arrow graph the host receives.
        var boundary = (endToEnd[^1] - endToEnd[0]) / (double)(counts[^1] - counts[0]);
        Console.WriteLine($"output boundary: {boundary:F0} bytes per output batch (reported)");

        // 4. UNION ALL is pure pass-through, so its steady-state cost is the same zero (§6).
        var union = await benchmarks.Engine.PrepareAsync(
            "SELECT symbol, ts FROM bars WHERE symbol = 'BTCUSDT' "
            + "UNION ALL SELECT symbol, ts FROM bars WHERE symbol = 'ETHUSDT'").ConfigureAwait(false);
        var unionPlan = await union.CompiledAsync(CancellationToken.None).ConfigureAwait(false);
        var unionSmall = await MeasureAsync(
            () => DrainPipelineAsync(unionPlan.WithBatchSize(BatchSizeFor(2)), arena)).ConfigureAwait(false);
        var unionLarge = await MeasureAsync(
            () => DrainPipelineAsync(unionPlan.WithBatchSize(BatchSizeFor(99)), arena)).ConfigureAwait(false);
        var unionSlope = (unionLarge - unionSmall) / (double)(99 - 2);
        var unionPass = Math.Abs(unionSlope) <= 64;
        passed &= unionPass;
        Console.WriteLine(
            $"UNION ALL pass-through: {unionSlope:F1} bytes/batch "
            + (unionPass ? "PASS (tolerance 64)" : "FAIL (tolerance 64)"));

        // 5. The hash aggregate over the whole two-million-row table (D255): a fixed number of bytes
        //    per execution, and nothing for one more row folded.
        passed &= await MeasureAggregateGatesAsync(benchmarks, arena).ConfigureAwait(false);

        // 5b. D257: the clustered copy scan. A batch is a slice of arrays built once at Build(), so
        //    the steady-state cost of one more batch is the same zero the scan path's is — and the
        //    leaf really is the copy, which the plan's index name is what says.
        var copyScan = await benchmarks.Engine.PrepareAsync(
            "SELECT symbol, ts, \"close\" FROM bars_clustered ORDER BY symbol, ts").ConfigureAwait(false);
        var copyLeaf = Chalk.Ir.PlanWalker.Rels(copyScan.Plan)
            .FirstOrDefault(r => r.KindCase == Chalk.Ir.Rel.KindOneofCase.IndexLookup);
        var copyLeafIsTheCopy = copyLeaf is not null
            && copyLeaf.IndexLookup.Index == "ix_bars_clustered_symbol_ts";
        var copyPlan = await copyScan.CompiledAsync(CancellationToken.None).ConfigureAwait(false);
        var copySmall = await MeasureAsync(
            () => DrainPipelineAsync(copyPlan.WithBatchSize(BatchSizeFor(2)), arena)).ConfigureAwait(false);
        var copyLarge = await MeasureAsync(
            () => DrainPipelineAsync(copyPlan.WithBatchSize(BatchSizeFor(99)), arena)).ConfigureAwait(false);
        var copySlope = (copyLarge - copySmall) / (double)(99 - 2);
        var copyPass = copyLeafIsTheCopy && Math.Abs(copySlope) <= 64;
        passed &= copyPass;
        Console.WriteLine(
            $"clustered copy scan: {copySlope:F1} bytes/batch, leaf "
            + (copyLeafIsTheCopy ? "ix_bars_clustered_symbol_ts " : "NOT the clustered index ")
            + (copyPass ? "PASS (tolerance 64)" : "FAIL (tolerance 64)"));

        // 6. The remote case: gated from step 24 on, because the native DuckDB reader took the
        //    provider's own per-row cost out of it (24-zero-gc.md §6, §8).
        passed &= await MeasureRemoteGatesAsync().ConfigureAwait(false);

        // 7. The UTF-8 gates of step 24 (24-zero-gc.md §4 and §9).
        passed &= await MeasureUtf8GatesAsync().ConfigureAwait(false);

        return passed;
    }

    /// <summary>
    /// The hash aggregate's gate (D255, <c>33-aggregate-performance.md</c> §1): <c>GROUP BY symbol</c>
    /// over two million bars costs a fixed number of bytes per execution and nothing for one more row
    /// folded.
    /// </summary>
    /// <remarks>
    /// The per-row term is taken the way every other per-row number here is: the same plan shape over
    /// two different numbers of rows, read in the same number of batches, so the difference between
    /// the two runs is rows and nothing else. Both queries group the same five symbols, so the
    /// per-group state is the same size in both and cannot be mistaken for a per-row cost.
    /// </remarks>
    private static async Task<bool> MeasureAggregateGatesAsync(
        ExecutionBenchmarks benchmarks, ExecutionArena arena)
    {
        Console.WriteLine();
        Console.WriteLine("--- the hash aggregate (D255) ---");

        var whole = await benchmarks.GroupByQuery
            .CompiledAsync(CancellationToken.None).ConfigureAwait(false);

        // The table's rows, not the fixture's `minutes` parameter: `Fixtures.Bars` makes one bar per
        // symbol per minute, so the row count is five times the number the benchmark's `Rows` column
        // shows.
        var scanned = await FoldedRowsAsync(whole, arena).ConfigureAwait(false);
        var fixedBytes = await MeasureAsync(
            () => DrainPipelineAsync(whole, arena)).ConfigureAwait(false);
        var fixedPass = fixedBytes <= 4096;
        Console.WriteLine(
            $"group by, {scanned} rows: {fixedBytes} bytes per execution = "
            + $"{fixedBytes / (double)scanned:F6} bytes/scanned row "
            + (fixedPass ? "PASS (<= 4096 fixed)" : "FAIL (<= 4096 fixed)"));

        const string few =
            "SELECT symbol, COUNT(*) AS n, SUM(volume) AS vol, MIN(\"close\") AS lo, "
            + "MAX(\"close\") AS hi FROM bars WHERE volume > 9000 GROUP BY symbol";
        const string many =
            "SELECT symbol, COUNT(*) AS n, SUM(volume) AS vol, MIN(\"close\") AS lo, "
            + "MAX(\"close\") AS hi FROM bars WHERE volume > 1000 GROUP BY symbol";

        var smallPlan = await (await benchmarks.Engine.PrepareAsync(few).ConfigureAwait(false))
            .CompiledAsync(CancellationToken.None).ConfigureAwait(false);
        var largePlan = await (await benchmarks.Engine.PrepareAsync(many).ConfigureAwait(false))
            .CompiledAsync(CancellationToken.None).ConfigureAwait(false);

        var smallRows = await FoldedRowsAsync(smallPlan, arena).ConfigureAwait(false);
        var largeRows = await FoldedRowsAsync(largePlan, arena).ConfigureAwait(false);
        if (largeRows <= smallRows || smallRows == 0)
        {
            Console.WriteLine(
                $"group by, per folded row: skipped (folded {smallRows} and {largeRows} rows; the "
                + "pair needs two different, non-empty results)");
            return fixedPass;
        }

        var smallBytes = await MeasureAsync(
            () => DrainPipelineAsync(smallPlan, arena)).ConfigureAwait(false);
        var largeBytes = await MeasureAsync(
            () => DrainPipelineAsync(largePlan, arena)).ConfigureAwait(false);
        var perRow = (largeBytes - smallBytes) / (double)(largeRows - smallRows);
        var perRowPass = Math.Abs(perRow) <= 0.01;
        Console.WriteLine(
            $"group by, per folded row: {smallBytes} bytes over {smallRows} rows, {largeBytes} over "
            + $"{largeRows}, the same batches either way = {perRow:F6} bytes/row "
            + (perRowPass ? "PASS (<= 0.01)" : "FAIL (<= 0.01)"));

        var compoundPass = await MeasureCompoundKeyGateAsync(benchmarks, arena).ConfigureAwait(false);

        return fixedPass && perRowPass && compoundPass;
    }

    /// <summary>
    /// The same claim for two grouping keys (F115), which is the path that until then read every
    /// key's lane again on every probe and kept no image per group.
    /// </summary>
    /// <remarks>
    /// The pair is chosen so that both statements fold their rows into the <em>same</em>
    /// <c>(symbol, trade_count)</c> keys — the two columns are independent in the fixture and every
    /// pair survives the narrower predicate — so the per-group state is the same size in both and
    /// what is left between them is the per-row cost. A pair that did not would be measuring group
    /// state, so it fails the gate rather than being quietly skipped.
    /// </remarks>
    private static async Task<bool> MeasureCompoundKeyGateAsync(
        ExecutionBenchmarks benchmarks, ExecutionArena arena)
    {
        const string few =
            "SELECT symbol, trade_count, COUNT(*) AS n, SUM(volume) AS vol FROM bars "
            + "WHERE volume > 9000 GROUP BY symbol, trade_count";
        const string many =
            "SELECT symbol, trade_count, COUNT(*) AS n, SUM(volume) AS vol FROM bars "
            + "WHERE volume > 1000 GROUP BY symbol, trade_count";

        var smallPlan = await (await benchmarks.Engine.PrepareAsync(few).ConfigureAwait(false))
            .CompiledAsync(CancellationToken.None).ConfigureAwait(false);
        var largePlan = await (await benchmarks.Engine.PrepareAsync(many).ConfigureAwait(false))
            .CompiledAsync(CancellationToken.None).ConfigureAwait(false);

        // The count column is the third: two keys come before the measures.
        var (smallRows, smallGroups) =
            await FoldedAsync(smallPlan, arena, countColumn: 2).ConfigureAwait(false);
        var (largeRows, largeGroups) =
            await FoldedAsync(largePlan, arena, countColumn: 2).ConfigureAwait(false);
        if (smallGroups != largeGroups || largeRows <= smallRows || smallRows == 0)
        {
            Console.WriteLine(
                $"group by two keys, per folded row: FAIL (the pair must fold different row counts "
                + $"into the same groups; it folded {smallRows} rows into {smallGroups} groups and "
                + $"{largeRows} into {largeGroups})");
            return false;
        }

        var smallBytes = await MeasureAsync(
            () => DrainPipelineAsync(smallPlan, arena)).ConfigureAwait(false);
        var largeBytes = await MeasureAsync(
            () => DrainPipelineAsync(largePlan, arena)).ConfigureAwait(false);
        var perRow = (largeBytes - smallBytes) / (double)(largeRows - smallRows);
        var pass = Math.Abs(perRow) <= 0.01;
        Console.WriteLine(
            $"group by two keys, per folded row: {smallBytes} bytes over {smallRows} rows, "
            + $"{largeBytes} over {largeRows}, {smallGroups} groups either way = {perRow:F6} bytes/row "
            + (pass ? "PASS (<= 0.01)" : "FAIL (<= 0.01)"));

        return pass;
    }

    /// <summary>
    /// How many rows a filtered aggregate's fold actually saw, which is the sum of its own
    /// <c>COUNT(*)</c> column — the second, because the key comes first.
    /// </summary>
    private static async Task<long> FoldedRowsAsync(CompiledPlan compiled, ExecutionArena arena) =>
        (await FoldedAsync(compiled, arena, countColumn: 1).ConfigureAwait(false)).Rows;

    /// <summary>
    /// The same, with the groups those rows folded into: a per-row figure taken from two runs is
    /// only a per-row figure when both runs hold the same amount of per-group state.
    /// </summary>
    private static async Task<(long Rows, long Groups)> FoldedAsync(
        CompiledPlan compiled, ExecutionArena arena, int countColumn)
    {
        long folded = 0;
        long groups = 0;
        await foreach (var batch in compiled.ExecuteColumnarAsync(
            [], new ExecutionStats(), arena, CancellationToken.None))
        {
            var counts = batch.Column(countColumn).Lanes<long>();
            for (var i = 0; i < batch.Count; i++)
            {
                folded += counts[batch.RowAt(i)];
            }

            groups += batch.Count;
        }

        return (folded, groups);
    }

    /// <summary>
    /// Zero-allocation query gate over the in-process DuckDB fixture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each path is measured at two result sizes with the <em>same plan shape</em>, so the two
    /// numbers a remote query has can be separated: the <b>slope</b> is what one more fetched row
    /// costs, which is what §6 gates at 0.1 bytes; the <b>intercept</b> is what the query costs
    /// before any row arrives — a command, a reader, a channel and a task — which §8 reports with a
    /// target of 16 KB and does not gate, because those are the provider's objects and the
    /// runtime's.
    /// </para>
    /// <para>
    /// Counted over every thread rather than the measuring one, because a remote query prefetches on
    /// the thread pool (D107, ADR 0022).
    /// </para>
    /// </remarks>
    private static async Task<bool> MeasureRemoteGatesAsync()
    {
        Console.WriteLine();
        Console.WriteLine("--- zero-allocation and remote gates ---");
        if (RemoteFixture.SkipReason is { } why)
        {
            Console.WriteLine($"remote gates: skipped ({why})");
            return true;
        }

        await using var sidecar = await SidecarFixture.StartAsync().ConfigureAwait(false);
        if (!sidecar.IsAvailable)
        {
            Console.WriteLine($"remote gates: skipped ({sidecar.SkipReason})");
            return true;
        }

        using var arena = new ExecutionArena();
        var passed = true;

        // Compare two DuckDB readers side-by-side to assess transport and batch-chunking policy overhead.
        foreach (var (label, fetch) in new (string, IRemoteFetch)[]
        {
            ("vector copier", DuckDbFetch.Instance),
            ("Arrow export", DuckDbArrowFetch.Instance),
        })
        {
            var isDefault = ReferenceEquals(fetch, DuckDbSources.NativeReader);

            using var duckFixture = RemoteFixture.Create(duckFetch: fetch);
            await using var duckEngine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = CorpusFixture.ContextId,
                Functions = CorpusFunctions.Register,
                Sources = duckFixture.Sources,
                Planner = sidecar.CreatePlanner(),
                Execution = new ExecutionOptions { BatchSize = 4096, OutputMemory = OutputMemory.Pooled },
            }).ConfigureAwait(false);

            // A text-bearing scan: two VARCHAR columns beside the numbers, which is the case the
            // native reader exists for. The two filters differ only in how many rows match.
            passed &= await RemotePairAsync(
                duckEngine,
                arena,
                $"remote scan, DuckDB, two text columns ({label})",
                "SELECT l_orderkey, l_shipmode, l_comment FROM duck.lineitem "
                + "WHERE l_discount BETWEEN 0.05 AND 0.06",
                "SELECT l_orderkey, l_shipmode, l_comment FROM duck.lineitem "
                + "WHERE l_discount BETWEEN 0.02 AND 0.09",
                gated: isDefault,
                scanTarget: true).ConfigureAwait(false);

            // The lookup join: the same source and the same reader, one more operator
            // between.
            passed &= await RemotePairAsync(
                duckEngine,
                arena,
                $"lookup join, DuckDB, one text column ({label})",
                "SELECT s.symbol, s.\"base\", b.ts, b.\"close\" "
                + "FROM symbols s JOIN duck.bars b ON b.symbol = s.symbol WHERE b.volume > 9000",
                "SELECT s.symbol, s.\"base\", b.ts, b.\"close\" "
                + "FROM symbols s JOIN duck.bars b ON b.symbol = s.symbol WHERE b.volume > 1000",
                gated: isDefault,
                // Both results are large on purpose. A lookup join's buffers reach their final size
                // class somewhere in the first few thousand rows, and a pair with one small side
                // would charge that one-off growth to the per-row term; between eleven thousand rows
                // and a hundred thousand there is nothing left to grow.
                scanTarget: false).ConfigureAwait(false);
        }

        using var fixture = RemoteFixture.Create();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = fixture.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { BatchSize = 4096, OutputMemory = OutputMemory.Pooled },
        }).ConfigureAwait(false);

        // SQLite on the DbDataReader path, for the comparison the step is about. Reported: the
        // provider's own reader is between Chalk and the rows.
        passed &= await RemotePairAsync(
            engine,
            arena,
            "remote scan, SQLite, two text columns (DbDataReader)",
            "SELECT l_orderkey, l_shipmode, l_comment FROM sqlite.lineitem "
            + "WHERE l_discount BETWEEN 0.05 AND 0.06",
            "SELECT l_orderkey, l_shipmode, l_comment FROM sqlite.lineitem "
            + "WHERE l_discount BETWEEN 0.02 AND 0.09",
            gated: false,
            scanTarget: true).ConfigureAwait(false);

        passed &= await PostgresPairAsync(fixture, sidecar, arena).ConfigureAwait(false);

        Console.WriteLine($"arena outstanding after the runs: {arena.OutstandingBytes} bytes");
        return passed;
    }

    /// <summary>
    /// The same three numbers over PostgreSQL, reported and never gated (§7): Npgsql's own buffers
    /// are Npgsql's business, and the leg exists so the per-row number is on the record beside the
    /// two in-process ones.
    /// </summary>
    private static async Task<bool> PostgresPairAsync(
        RemoteFixture fixture, SidecarFixture sidecar, ExecutionArena arena)
    {
        using var postgres = PostgresFixture.Start();
        if (postgres.SkipReason is { } why)
        {
            Console.WriteLine($"remote scan, PostgreSQL: skipped ({why})");
            return true;
        }

        string database;
        try
        {
            database = postgres.CreateDatabase("chalk_bench");
        }
        catch (InvalidOperationException)
        {
            database = postgres.ConnectionString!;
        }

        fixture.AttachPostgres(database, connectionString => new NpgsqlConnection(connectionString));
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = fixture.PostgresSources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { BatchSize = 4096, OutputMemory = OutputMemory.Pooled },
        }).ConfigureAwait(false);

        return await RemotePairAsync(
            engine,
            arena,
            // orders rather than lineitem: the PostgreSQL leg loads everything SQLite holds except
            // lineitem, and orders carries three text columns of its own.
            "remote scan, PostgreSQL, three text columns (DbDataReader)",
            "SELECT o_orderkey, o_orderstatus, o_clerk, o_comment FROM pg.orders "
            + "WHERE o_totalprice > 250000",
            "SELECT o_orderkey, o_orderstatus, o_clerk, o_comment FROM pg.orders "
            + "WHERE o_totalprice > 50000",
            gated: false,
            scanTarget: true).ConfigureAwait(false);
    }

    /// <summary>
    /// The two gates §4 and §9 add beside the existing local ones: a POCO <c>Utf8String</c> column
    /// scans at zero bytes per row, and a Tier 1 function written in <c>Utf8String</c> adds nothing
    /// per batch.
    /// </summary>
    /// <remarks>
    /// Measured on the pooled pipeline, like every other local gate: an output batch's Arrow graph
    /// is the host's and is reported at the output boundary, not here.
    /// </remarks>
    private static async Task<bool> MeasureUtf8GatesAsync()
    {
        Console.WriteLine();
        Console.WriteLine("--- zero-gc UTF-8 gates ---");
        await using var sidecar = await SidecarFixture.StartAsync().ConfigureAwait(false);
        if (!sidecar.IsAvailable)
        {
            Console.WriteLine($"utf-8 gates: skipped ({sidecar.SkipReason})");
            return true;
        }

        var fixture = Utf8Fixture.Create(quotes: 100_000);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = Utf8Fixture.ContextId,
            Functions = Utf8Fixture.Register,
            Sources = fixture.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { BatchSize = 4096, OutputMemory = OutputMemory.Pooled },
        }).ConfigureAwait(false);

        using var arena = new ExecutionArena();
        var passed = true;

        passed &= await Utf8PerRowAsync(
            engine, arena, "POCO Utf8String scan+filter+project",
            "SELECT symbol, seq FROM utf8_quotes WHERE price > 40",
            "SELECT symbol, seq FROM utf8_quotes WHERE price > 10").ConfigureAwait(false);
        passed &= await Utf8PerBatchAsync(
            engine, arena, "Tier 1 upper_ascii(Utf8String) -> Utf8String",
            "SELECT upper_ascii(symbol) AS s FROM utf8_quotes WHERE price > 10").ConfigureAwait(false);
        passed &= await Utf8PerBatchAsync(
            engine, arena, "Tier 1 byte_length(Utf8String) -> I64",
            "SELECT byte_length(symbol) AS n FROM utf8_quotes WHERE price > 10").ConfigureAwait(false);

        Console.WriteLine($"arena outstanding after the runs: {arena.OutstandingBytes} bytes");
        return passed;
    }

    /// <summary>
    /// Bytes per row, with the batch count held constant so what is measured is a row and not a
    /// batch: the same query over the same table at two batch sizes an eighth apart in rows.
    /// </summary>
    private static async Task<bool> Utf8PerRowAsync(
        ChalkEngine engine, ExecutionArena arena, string what, string smallSql, string largeSql)
    {
        const int Batches = 8;
        var smallPlan = await (await engine.PrepareAsync(smallSql).ConfigureAwait(false))
            .CompiledAsync(CancellationToken.None).ConfigureAwait(false);
        var largePlan = await (await engine.PrepareAsync(largeSql).ConfigureAwait(false))
            .CompiledAsync(CancellationToken.None).ConfigureAwait(false);

        var small = await RowsAsync(smallPlan, arena).ConfigureAwait(false);
        var rows = await RowsAsync(largePlan, arena).ConfigureAwait(false);
        if (rows <= small || small == 0)
        {
            Console.WriteLine($"{what}: skipped (produced {small} and {rows} rows)");
            return true;
        }

        var quarter = await MeasureAsync(
            () => DrainPipelineAsync(smallPlan.WithBatchSize((int)(small / Batches) + 1), arena))
            .ConfigureAwait(false);
        var whole = await MeasureAsync(
            () => DrainPipelineAsync(largePlan.WithBatchSize((int)(rows / Batches) + 1), arena))
            .ConfigureAwait(false);

        var perRow = (whole - quarter) / (double)(rows - small);
        var pass = Math.Abs(perRow) <= 0.01;
        Console.WriteLine(
            $"{what}: {quarter} bytes over {small} rows, {whole} over {rows}, {Batches} batches "
            + $"either way = {perRow:F4} bytes/row "
            + (pass ? "PASS (<= 0.01)" : "FAIL (<= 0.01)"));
        return pass;
    }

    /// <summary>Bytes per batch over the same rows, which is the Tier 1 gate of §4.</summary>
    private static async Task<bool> Utf8PerBatchAsync(
        ChalkEngine engine, ExecutionArena arena, string what, string sql)
    {
        var compiled = await (await engine.PrepareAsync(sql).ConfigureAwait(false))
            .CompiledAsync(CancellationToken.None).ConfigureAwait(false);
        var rows = await RowsAsync(compiled, arena).ConfigureAwait(false);

        var few = await MeasureAsync(
            () => DrainPipelineAsync(compiled.WithBatchSize((int)(rows / 2) + 1), arena))
            .ConfigureAwait(false);
        var many = await MeasureAsync(
            () => DrainPipelineAsync(compiled.WithBatchSize((int)(rows / 64) + 1), arena))
            .ConfigureAwait(false);

        var slope = (many - few) / (64.0 - 2.0);
        var pass = Math.Abs(slope) <= 64;
        Console.WriteLine(
            $"{what}: {few} bytes over 2 batches, {many} over 64, {rows} rows either way = "
            + $"{slope:F1} bytes/batch " + (pass ? "PASS (tolerance 64)" : "FAIL (tolerance 64)"));
        return pass;
    }

    private static async Task<long> RowsAsync(CompiledPlan compiled, ExecutionArena arena)
    {
        long rows = 0;
        await foreach (var batch in compiled.ExecuteColumnarAsync(
            [], new ExecutionStats(), arena, CancellationToken.None))
        {
            rows += batch.Count;
        }

        return rows;
    }

    /// <summary>
    /// One path, resolved into the three numbers a remote query actually has: what one more fetched
    /// row costs, what one more batch costs, and what the query costs before any row arrives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three measurements, all of the same plan shapes. The two queries differ only in how many rows
    /// match, and both are read in <b>one batch</b>, so the difference between them is the per-row
    /// term and nothing else — that is the number §6 gates. The larger query is then read again in
    /// eight batches, and that difference over seven is the per-batch term: the Arrow object graph
    /// and the buffer owners a source hands across <c>ISourceRuntime</c>'s contract, which does not
    /// grow with rows and is the same on every path. What is left is the fixed cost of §8.
    /// </para>
    /// <para>
    /// Measuring the per-row term with the batch count held constant is what makes it a per-row term
    /// at all; a slope taken with the batch count growing is a per-batch cost wearing a per-row
    /// disguise, and the report prints it too so the difference is visible rather than assumed.
    /// </para>
    /// </remarks>
    private static async Task<bool> RemotePairAsync(
        ChalkEngine engine,
        ExecutionArena arena,
        string what,
        string smallSql,
        string largeSql,
        bool gated,
        bool scanTarget)
    {
        const int batches = 8;

        var small = await engine.PrepareAsync(smallSql).ConfigureAwait(false);
        var large = await engine.PrepareAsync(largeSql).ConfigureAwait(false);
        var smallPlan = await small.CompiledAsync(CancellationToken.None).ConfigureAwait(false);
        var largePlan = await large.CompiledAsync(CancellationToken.None).ConfigureAwait(false);

        // How many rows each fetches, so the batch sizes can be chosen from it.
        var (_, smallRows, path, _) = await RemoteRunAsync(smallPlan, arena).ConfigureAwait(false);
        var (_, largeRows, _, _) = await RemoteRunAsync(largePlan, arena).ConfigureAwait(false);
        if (largeRows <= smallRows || smallRows == 0)
        {
            Console.WriteLine(
                $"{what}: skipped (fetched {smallRows} and {largeRows} rows; the pair needs two "
                + "different, non-empty results)");
            return true;
        }

        // The same batch size for both, big enough for either result: the only thing that differs
        // between the two runs is then how many rows arrived, which is what makes the difference a
        // per-row number rather than a per-batch-size one.
        var oneBatch = (int)largeRows + 1;
        var (oneSmall, _, _, _) = await RemoteRunAsync(
            smallPlan.WithBatchSize(oneBatch), arena).ConfigureAwait(false);
        var (oneLarge, _, _, largeBatches) = await RemoteRunAsync(
            largePlan.WithBatchSize(oneBatch), arena).ConfigureAwait(false);
        var (manyLarge, _, _, _) = await RemoteRunAsync(
            largePlan.WithBatchSize((int)(largeRows / batches) + 1), arena).ConfigureAwait(false);

        var perRow = (oneLarge - oneSmall) / (double)(largeRows - smallRows);
        var perBatch = (manyLarge - oneLarge) / (double)(batches - 1);
        var fixedCost = oneSmall - (perRow * smallRows) - perBatch;

        // Reported, never asserted (the handoff rule): a path that allocates nothing but takes twice
        // as long is not the better path, and the only way to see that is to print it. Three runs of
        // the large query, best of, after everything above has warmed it.
        var elapsed = await WallClockAsync(largePlan.WithBatchSize(oneBatch), arena)
            .ConfigureAwait(false);

        var pass = !gated || Math.Abs(perRow) <= 0.1;
        Console.WriteLine($"{what} [{path}]:");
        Console.WriteLine(
            $"    per fetched row: {perRow:F4} bytes ({oneSmall} bytes over {smallRows} rows, "
            + $"{oneLarge} over {largeRows}, one batch each) "
            + (gated ? pass ? "PASS (<= 0.1)" : "FAIL (<= 0.1)" : "(reported)"));
        Console.WriteLine(
            $"    per batch: {perBatch:F0} bytes ({manyLarge} bytes over {batches} batches against "
            + $"{oneLarge} over one) (reported)");
        Console.WriteLine(
            $"    wall clock: {elapsed.TotalMilliseconds:F1} ms for {largeRows} rows in one batch, "
            + "best of three (reported)");

        // A path whose batch is the driver's own chunk never had one batch to be measured in, so
        // the "per fetched row" line above is that chunk's cost spread over its rows. Saying so,
        // with the chunk size the number implies, is the difference between a measurement and a
        // misreading.
        if (largeBatches > 1)
        {
            Console.WriteLine(
                $"    NB: this path ignored the batch size and produced {largeBatches} batches for "
                + $"{largeRows} rows, so the per-row line above is a per-batch cost divided by "
                + $"{largeRows / (double)largeBatches:F0} rows = {perRow * largeRows / largeBatches:F0} "
                + "bytes per batch (reported)");
        }

        // Only where the per-row term is already near zero do the other two terms mean anything: a
        // path that allocates per row buries its fixed cost under the rows, and subtracting an
        // estimate of one from a measurement of the other is arithmetic, not evidence.
        if (Math.Abs(perRow) > 1.0)
        {
            Console.WriteLine(
                "    per query, fixed: not separable — the per-row term dominates on this path");
        }
        else
        {
            Console.WriteLine(
                $"    per query, fixed: {fixedCost:F0} bytes "
                + (scanTarget
                    ? fixedCost <= 16_384 ? "(<= the 16 KB scan target)" : "(over the 16 KB scan target)"
                    : "(reported; §8's 16 KB target is a scan's)"));
        }

        return pass;
    }

    /// <summary>
    /// How long one run of this plan takes, best of three, with the arena and the connection warm.
    /// Reported beside the allocation numbers and asserted nowhere.
    /// </summary>
    private static async Task<TimeSpan> WallClockAsync(CompiledPlan compiled, ExecutionArena arena)
    {
        var best = TimeSpan.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            await DrainPipelineAsync(compiled, arena).ConfigureAwait(false);
            var taken = System.Diagnostics.Stopwatch.GetElapsedTime(start);
            if (taken < best)
            {
                best = taken;
            }
        }

        return best;
    }

    private static async Task<(long Bytes, long Rows, string Path, int Batches)> RemoteRunAsync(
        CompiledPlan compiled, ExecutionArena arena)
    {
        long fetched = 0;
        var batches = 0;
        var path = string.Empty;
        var bytes = await MeasureEverywhereAsync(async () =>
        {
            var stats = new ExecutionStats();
            batches = 0;
            await foreach (var batch in compiled.ExecuteColumnarAsync(
                [], stats, arena, CancellationToken.None))
            {
                _ = batch.Count;
                batches++;
            }

            fetched = stats.RowsFetched;
            path = string.Join(", ", stats.SourcePaths.Select(p => $"{p.Key}={p.Value}"));
        }).ConfigureAwait(false);

        return (bytes, fetched, path, batches);
    }

    /// <summary>One warm-up run, then the measured one.</summary>
    private static async Task<long> MeasureAsync(Func<Task> run)
    {
        await run().ConfigureAwait(false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        await run().ConfigureAwait(false);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// The same, counted over every thread in the process rather than the measuring one. This is
    /// what a query with a remote source needs from M5 on: a prefetching fetch task runs on the
    /// thread pool (D107), so the allocation the gate is asking about does not happen on the thread
    /// that is counting, and the continuation may not even come back to it. A per-thread counter
    /// across that hop is not a smaller number — it is a different number, and the difference of two
    /// unrelated threads' totals is meaningless.
    /// </summary>
    /// <remarks>
    /// Process-wide counting is only honest in a process doing one thing at a time, which is what
    /// this benchmark is; the gated local numbers stay on <see cref="MeasureAsync"/>, because their
    /// pipelines never leave the calling thread and a process-wide count would let a gRPC keepalive
    /// fail a gate.
    /// </remarks>
    private static async Task<long> MeasureEverywhereAsync(Func<Task> run)
    {
        await run().ConfigureAwait(false);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        await run().ConfigureAwait(false);
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    private static async Task DrainPipelineAsync(CompiledPlan compiled, ExecutionArena arena)
    {
        await foreach (var batch in compiled.ExecuteColumnarAsync(
            [], new ExecutionStats(), arena, CancellationToken.None))
        {
            _ = batch.Count;
        }
    }

    private static async Task DrainOutputAsync(CompiledPlan compiled, ExecutionArena arena)
    {
        await foreach (var batch in compiled.ExecuteAsync(
            [], new ExecutionStats(), arena, CancellationToken.None))
        {
            batch.Dispose();
        }
    }

    /// <summary>The batch size that splits the 100 800-row scan into roughly {batches} batches.</summary>
    private static int BatchSizeFor(int batches) => Math.Max(1024, (100_800 / batches) + 1);

    /// <summary>What a one-batch query costs per execution, arena and tree both warm.</summary>
    private static async Task<long> FixedBytesAsync(ChalkEngine engine, PreparedQuery query)
    {
        using var arena = new ExecutionArena();
        for (var i = 0; i < 3; i++)
        {
            await ConsumeOnceAsync(engine, query, arena).ConfigureAwait(false);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        await ConsumeOnceAsync(engine, query, arena).ConfigureAwait(false);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static async Task<(long Rows, long Scanned, long PeakPooledBytes)> ConsumeOnceAsync(
        ChalkEngine engine, PreparedQuery query, ExecutionArena arena)
    {
        await using var execution = await engine.ExecuteAsync(query, [], arena).ConfigureAwait(false);
        long rows = 0;
        await foreach (var batch in execution.Batches)
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return (rows, execution.Stats.RowsScanned, execution.Stats.PeakPooledBytes);
    }
}
