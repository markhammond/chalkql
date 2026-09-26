using System.Collections;
using System.Diagnostics.Tracing;
using System.Globalization;
using Chalk.Client;
using Chalk.Execution.Operators;
using Chalk.Execution.Windowing;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Chalk.Benchmarks;

/// <summary>
/// <c>--allocation-profile &lt;plan&gt;</c>: one execution of one named plan, with every byte it
/// allocates attributed to a type (<c>docs/design/33-aggregate-performance.md</c> §3, step 26k).
///
/// <para>
/// The <c>--allocation</c> harness says <em>how much</em> a plan allocates. This says <em>what</em>:
/// an in-process <see cref="EventListener"/> on the runtime's <c>GCAllocationTick</c> events, which
/// carry the type name and the bytes allocated since the last tick. Stacks are not available
/// in-process, so the table names types rather than call sites; a large-object allocation ticks
/// individually and is therefore exact, while small-object allocation is sampled about every 100 KB
/// per allocation context and its shares are approximate.
/// </para>
/// <para>
/// The plan table below is the bisect: the scan, the sort, the hop with and without the benchmark's
/// total order, and nine configurations of the same window query — over a pre-sorted source, with the
/// <c>(symbol, ts)</c> index withdrawn, with a fetched row repriced so the planner sorts instead of
/// gathering, with input verification on, with managed output, with the benchmark's total order, and
/// with a smaller and a larger arena retention. One variant per process, so no run inherits another's
/// warm pools.
/// </para>
/// <para>
/// <c>hop-ordered-output</c> is here because it is too slow to belong anywhere else: the order makes
/// the hop a blocking sort of thirty million rows at about 21 s an execution, which the
/// <c>--allocation</c> gate run would pay five times over on every check. This is where that figure
/// is measured.
/// </para>
/// </summary>
internal static class AllocationProfile
{
    /// <summary>One row of the bisect: a query and the configuration it runs under.</summary>
    internal sealed class ProfilePlan
    {
        public required string Name { get; init; }

        /// <summary>What this row is asking, in one line, printed with the numbers.</summary>
        public required string What { get; init; }

        public required string Sql { get; init; }

        /// <summary>
        /// Order the bars by (symbol, ts) and declare that ordering, so a window partitioned by
        /// symbol and ordered by ts needs no sort and no index permutation under it.
        /// </summary>
        public bool WindowOrderedSource { get; init; }

        /// <summary>
        /// Declare the table without its <c>(symbol, ts)</c> unique index, so the planner has no
        /// permutation to take the window's ordering from and must put a sort under it. The rows are
        /// the same rows in the same physical order; only what the catalog promises changes.
        /// </summary>
        public bool WindowWithoutIndex { get; init; }

        /// <summary>
        /// Declare the same table, index and all, with a <c>lookup_row_cost</c> this high (D38,
        /// <c>11-m2-index-support.md</c>). Nothing about the catalog's shape changes — only what the
        /// planner is told a fetched row costs — so the plan it picks instead is the cost model's
        /// answer rather than a consequence of hiding the index. Zero leaves the price alone.
        /// </summary>
        public double LookupRowCost { get; init; }

        /// <summary>Turn <c>WindowOperator.VerifyInputOrder</c> on, which a Release build leaves off.</summary>
        public bool VerifyInputOrder { get; init; }

        /// <summary>
        /// How a sort whose composite needs two words sorts them (D267 b): the default settles each
        /// run of equal high words by its low word, and the other radix-sorts both. The two are
        /// measured against each other here rather than argued about.
        /// </summary>
        public SortWide128 Wide128 { get; init; } = SortWide128.RunsByLowWord;

        public OutputMemory Output { get; init; } = OutputMemory.Pooled;

        public long RetainBytes { get; init; } = 64L << 20;

        public int BatchSize { get; init; } = 4096;
    }

    /// <summary>
    /// The plans the bisect runs, by name. Configuration lives here rather than in flags on the
    /// command line: a row of the table in the report is a row of this table.
    /// </summary>
    private static readonly ProfilePlan[] Plans =
    [
        new()
        {
            Name = "scan",
            What = "the scan the gates already cover, as a floor",
            Sql = "SELECT symbol, ts, \"close\" FROM bars WHERE volume > 5000",
        },
        new()
        {
            Name = "sort",
            What = "the sort the window sits on",
            Sql = "SELECT symbol, ts, \"close\" FROM bars ORDER BY symbol, ts",
        },
        new()
        {
            Name = "window",
            What = "the twenty-row moving average, as the benchmark runs it",
            Sql = WindowSql,
        },
        new()
        {
            Name = "window-ordered",
            What = "the same window over a source that already delivers (symbol, ts)",
            Sql = WindowSql,
            WindowOrderedSource = true,
        },
        new()
        {
            Name = "window-no-index",
            What = "the same window with the (symbol, ts) index withdrawn, so the plan must sort",
            Sql = WindowSql,
            WindowWithoutIndex = true,
        },
        new()
        {
            Name = "window-priced-lookup",
            What = "the same window, index and all, with a fetched row priced at 64 instead of 4",
            Sql = WindowSql,
            // The plan text prices the full-range gather at 40,320,017 and the scan-plus-sort at
            // 472,382,248, so the gather stops winning above 10,080,000 x c + 17 = 472,382,248,
            // which is c = 46.9. Sixty-four is the next power of two clear of it.
            LookupRowCost = 64.0,
        },
        new()
        {
            Name = "window-ordered-output",
            What = "the window with the benchmark's total order on its output",
            Sql = WindowSql + WindowOrder,
        },
        new()
        {
            Name = "window-verify",
            What = "the same window with the input-order verification on",
            Sql = WindowSql,
            VerifyInputOrder = true,
        },
        new()
        {
            Name = "window-managed",
            What = "the same window with managed output rather than pooled",
            Sql = WindowSql,
            Output = OutputMemory.Managed,
        },
        new()
        {
            Name = "window-retain-4mb",
            What = "the same window with a 4 MB arena retention",
            Sql = WindowSql,
            RetainBytes = 4L << 20,
        },
        new()
        {
            Name = "window-retain-2gb",
            What = "the same window with a 2 GB arena retention, so nothing is trimmed",
            Sql = WindowSql,
            RetainBytes = 2L << 30,
        },
        new()
        {
            Name = "hop",
            What = "the hopping window, three output rows per input row",
            Sql = HopSql,
        },
        new()
        {
            Name = "hop-ordered-output",
            What = "the hop with the benchmark's total order, which makes it blocking",
            Sql = HopSql + HopOrder,
        },
        new()
        {
            Name = "hop-ordered-output-radix",
            What = "the same, with the two-word composite radix-sorted instead (D267 b)",
            Sql = HopSql + HopOrder,
            Wide128 = SortWide128.Radix,
        },
        new()
        {
            Name = "hop-ordered-output-retained",
            What = "the same, on an arena whose retention holds the sort's whole working set",
            Sql = HopSql + HopOrder,
            RetainBytes = 4L << 30,
        },
    ];

    // The statements themselves live on ExecutionBenchmarks, so the plan a profile measures and the
    // plan the benchmark runs cannot drift apart.
    private const string WindowSql = ExecutionBenchmarks.WindowSql;

    private const string WindowOrder = ExecutionBenchmarks.WindowOrder;

    private const string HopSql = ExecutionBenchmarks.HopSql;

    private const string HopOrder = ExecutionBenchmarks.HopOrder;

    /// <summary>How many types the table names before it stops.</summary>
    private const int TopTypes = 20;

    public static async Task<int> RunAsync(string? name)
    {
        if (name is null || Plans.FirstOrDefault(p => p.Name == name) is not { } plan)
        {
            Console.Error.WriteLine(
                "usage: --allocation-profile <plan>, one of: "
                + string.Join(", ", Plans.Select(p => p.Name)));
            return 2;
        }

        // Set rather than switched: the AppContext switch behind this property is read once, by a
        // static initialiser, so a process that has already touched WindowOperator cannot change its
        // mind that way. Setting it here, before any query is prepared, is the honest equivalent.
        WindowOperator.VerifyInputOrder = plan.VerifyInputOrder;
        SortKeyComposite.Wide128 = plan.Wide128;

        await using var sidecar = await SidecarFixture.StartAsync().ConfigureAwait(false);
        if (!sidecar.IsAvailable)
        {
            Console.Error.WriteLine($"allocation profile: skipped ({sidecar.SkipReason})");
            return 2;
        }

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = plan switch
            {
                { WindowOrderedSource: true } => BarsOrderedBySymbolThenTs.Fixture,
                { WindowWithoutIndex: true } => BarsWithoutTheSymbolTsIndex.Fixture,
                { LookupRowCost: > 0 } => Repriced.Fixture(plan.LookupRowCost),
                _ => ExecutionBenchmarks.ExecutionBenchmarksSettings.Fixture,
            },
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions
            {
                BatchSize = plan.BatchSize,
                OutputMemory = plan.Output,
                Arena = new ArenaOptions { RetainBytes = plan.RetainBytes },
            },
        }).ConfigureAwait(false);

        var query = await engine
            .PrepareAsync(plan.Sql, new PrepareOptions { IncludePlanText = true })
            .ConfigureAwait(false);

        // The arena is this run's own, so its counters describe one execution rather than whatever
        // the engine's pool had been doing.
        using var arena = new ExecutionArena(new ArenaOptions { RetainBytes = plan.RetainBytes });

        // Warm up: the JIT, the operator tree, the arena's pools and the sidecar's plan cache.
        var warm = await DrainAsync(engine, query, arena).ConfigureAwait(false);

        using var listener = new AllocationTickListener();
        var beforeRentals = arena.Rentals;
        var beforeRentedBytes = arena.RentedBytes;
        var beforeFallbacks = arena.HeapFallbacks;
        var beforeFallbackBytes = arena.HeapFallbackBytes;
        var beforeUnpooled = arena.UnpooledReturnBytes;
        var beforeTrimmed = arena.TrimmedBytes;

        listener.Start();
        var beforeThread = GC.GetAllocatedBytesForCurrentThread();
        var beforeProcess = GC.GetTotalAllocatedBytes(precise: true);
        var run = await DrainAsync(engine, query, arena).ConfigureAwait(false);
        var thread = GC.GetAllocatedBytesForCurrentThread() - beforeThread;
        var process = GC.GetTotalAllocatedBytes(precise: true) - beforeProcess;
        listener.Stop();

        // Closed here, before the timed runs below: the arena's counters are cumulative over its
        // life, so a snapshot taken after four more executions would report four executions' worth.
        var rentals = arena.Rentals - beforeRentals;
        var rentedBytes = arena.RentedBytes - beforeRentedBytes;
        var fallbacks = arena.HeapFallbacks - beforeFallbacks;
        var fallbackBytes = arena.HeapFallbackBytes - beforeFallbackBytes;
        var unpooled = arena.UnpooledReturnBytes - beforeUnpooled;
        var trimmed = arena.TrimmedBytes - beforeTrimmed;

        // Reported and asserted nowhere: a path that allocates less but takes twice as long is not
        // the better path, and the only way to see that is to print it.
        var best = TimeSpan.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await DrainAsync(engine, query, arena).ConfigureAwait(false);
            var taken = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (taken < best)
            {
                best = taken;
            }
        }

        var rows = Math.Max(run.Produced, 1);
        Console.WriteLine($"plan {plan.Name}: {plan.What}");
        Console.WriteLine($"  sql: {plan.Sql}");
        Console.WriteLine(
            $"  configuration: output={plan.Output} retain={Mb(plan.RetainBytes)} "
            + $"batch={plan.BatchSize} verify_input_order={plan.VerifyInputOrder} "
            + $"wide128={plan.Wide128} "
            + $"source_ordered_by_symbol_ts={plan.WindowOrderedSource} "
            + $"symbol_ts_index={!plan.WindowWithoutIndex} "
            + $"lookup_row_cost={(plan.LookupRowCost > 0 ? plan.LookupRowCost : 4.0)}");
        Console.WriteLine($"  plan shape: {Shape(query.Plan)}");
        Console.WriteLine(
            $"  wall clock: {best.TotalMilliseconds:F1} ms, best of three (reported)");
        Console.WriteLine(
            $"  rows: {run.Produced} produced, {run.Scanned} scanned, {run.Batches} batches "
            + $"(warm-up produced {warm.Produced})");
        Console.WriteLine(
            $"  allocated per execution: {thread} bytes on this thread, {process} process-wide");
        Console.WriteLine(
            $"  per output row: {(double)thread / rows:F4} bytes "
            + $"({(double)process / rows:F4} process-wide)");
        Console.WriteLine(
            $"  arena: {rentals} rentals, {rentedBytes} bytes rented, "
            + $"{fallbacks} heap fallbacks costing {fallbackBytes} bytes, "
            + $"{unpooled} bytes returned unpooled, {trimmed} bytes trimmed");
        Console.WriteLine(
            $"  arena after: {arena.OutstandingBytes} outstanding, {arena.RetainedBytes} retained, "
            + $"{arena.PeakBytes} peak");
        Console.WriteLine($"  execution stats: peak pooled {run.PeakPooledBytes} bytes");
        listener.Report(Console.Out);
        if (query.PlanText is { Length: > 0 } text)
        {
            Console.WriteLine("  --- plan text ---");
            foreach (var line in text.Split('\n'))
            {
                Console.WriteLine($"  {line.TrimEnd('\r')}");
            }
        }

        return 0;
    }

    private static string Mb(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.#}MB");

    private readonly record struct RunResult(
        long Produced, long Scanned, long Batches, long PeakPooledBytes);

    private static async Task<RunResult> DrainAsync(
        ChalkEngine engine, PreparedQuery query, ExecutionArena arena)
    {
        await using var execution = await engine
            .ExecuteAsync(query, [], arena).ConfigureAwait(false);
        long rows = 0;
        long batches = 0;
        await foreach (var batch in execution.Batches.ConfigureAwait(false))
        {
            rows += batch.Length;
            batches++;
            batch.Dispose();
        }

        return new RunResult(
            rows, execution.Stats.RowsScanned, batches, execution.Stats.PeakPooledBytes);
    }

    /// <summary>
    /// The plan as a chain of node kinds, so a report can say whether the window was given a sort,
    /// an index permutation or neither without anyone reading the planner's mind.
    /// </summary>
    internal static string Shape(Plan plan)
    {
        var text = new System.Text.StringBuilder();
        Append(plan.Root, text);
        return text.ToString();
    }

    private static void Append(Rel? rel, System.Text.StringBuilder text)
    {
        if (rel is null)
        {
            return;
        }

        if (text.Length != 0)
        {
            text.Append(" <- ");
        }

        text.Append(rel.KindCase);

        // How many calls a window computes, because each one rents a lane array and a validity
        // array the size of the whole input: AVG arrives here as two calls, not one.
        if (rel.KindCase == Rel.KindOneofCase.Window)
        {
            text.Append('[').Append(rel.Window.Calls.Count).Append(" calls: ")
                .Append(string.Join(", ", rel.Window.Calls.Select(Call))).Append(']');
        }

        var children = Inputs(rel).ToList();
        if (children.Count == 1)
        {
            Append(children[0], text);
            return;
        }

        for (var i = 0; i < children.Count; i++)
        {
            text.Append(i == 0 ? " (" : " | ");
            var branch = new System.Text.StringBuilder();
            Append(children[i], branch);
            text.Append(branch);
        }

        if (children.Count > 1)
        {
            text.Append(')');
        }
    }

    private static string Call(WindowCall call) => call.FunctionCase switch
    {
        WindowCall.FunctionOneofCase.Aggregate => call.Aggregate.ToString(),
        WindowCall.FunctionOneofCase.WindowFunction => call.WindowFunction.ToString(),
        _ => call.UserFunction,
    };

    /// <summary>
    /// The <see cref="Rel"/>-typed fields of whichever node kind this <see cref="Rel"/> carries.
    /// Read through protobuf reflection rather than a switch over forty node types, which would be
    /// forty chances to forget one.
    /// </summary>
    private static IEnumerable<Rel> Inputs(Rel rel)
    {
        var oneof = Rel.Descriptor.Oneofs[0].Accessor;
        if (oneof.GetCaseFieldDescriptor(rel) is not { } field
            || field.Accessor.GetValue(rel) is not IMessage node)
        {
            yield break;
        }

        foreach (var member in node.Descriptor.Fields.InDeclarationOrder())
        {
            if (member.FieldType != FieldType.Message
                || member.MessageType.ClrType != typeof(Rel))
            {
                continue;
            }

            if (member.IsRepeated)
            {
                foreach (var child in (IList)member.Accessor.GetValue(node))
                {
                    yield return (Rel)child;
                }
            }
            else if (member.Accessor.GetValue(node) is Rel child)
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// The benchmark fixture's bars, physically sorted by (symbol, ts) and declared that way, so the
    /// window's input arrives in window order straight from the source.
    /// </summary>
    /// <remarks>
    /// The default fixture stores bars by (ts, symbol) — which is what makes <c>ORDER BY ts</c> lose
    /// its sort (<c>Fixtures.Bars</c>) — and reaches window order through the declared (symbol, ts)
    /// unique index. Declaring an ordering the rows do not have would be a wrong answer rather than a
    /// faster one, so this variant sorts the rows first.
    /// </remarks>
    private static class BarsOrderedBySymbolThenTs
    {
        static BarsOrderedBySymbolThenTs()
        {
            var bars = ExecutionBenchmarks.ExecutionBenchmarksSettings.Bars
                .OrderBy(b => b.Symbol.ToString(), StringComparer.Ordinal)
                .ThenBy(b => b.Ts)
                .ToList();

            var source = new PocoSourceBuilder("mem")
                .NamingPolicy(PocoNamingPolicy.SnakeCase)
                .AddTable("bars", bars, t => t
                    .OrderedBy(b => b.Symbol)
                    .ThenBy(b => b.Ts)
                    .UniqueKey(b => b.Symbol, b => b.Ts)
                    .ForeignKey(b => b.Symbol)
                    .References<SymbolRow>(s => s.Symbol, verify: false))
                .AddTable("symbols", ExecutionBenchmarks.ExecutionBenchmarksSettings.SymbolRows, t => t
                    .OrderedBy(s => s.Symbol)
                    .UniqueKey(s => s.Symbol))
                .Build();

            Fixture = [source];
        }

        public static readonly IReadOnlyList<PocoSource> Fixture;
    }

    /// <summary>
    /// The benchmark fixture's bars with the <c>(symbol, ts)</c> unique index withdrawn: the same
    /// rows in the same physical (ts, symbol) order, declared as the source really is and nothing
    /// more, so the planner has no permutation to take a window's ordering from and must sort.
    /// </summary>
    /// <remarks>
    /// This is the variant that answers "is the window's memory the window's, or the index-ordered
    /// scan's?" — the only difference between it and the default fixture is what the catalog
    /// promises, so whatever changes between the two profiles is the index path and nothing else.
    /// </remarks>
    private static class BarsWithoutTheSymbolTsIndex
    {
        static BarsWithoutTheSymbolTsIndex()
        {
            var source = new PocoSourceBuilder("mem")
                .NamingPolicy(PocoNamingPolicy.SnakeCase)
                .AddTable("bars", ExecutionBenchmarks.ExecutionBenchmarksSettings.Bars, t => t
                    .OrderedBy(b => b.Ts)
                    .ThenBy(b => b.Symbol)
                    .UniqueKey(b => b.Ts, b => b.Symbol)
                    .Index(b => b.Ts, b => b.Symbol)
                    .ForeignKey(b => b.Symbol)
                    .References<SymbolRow>(s => s.Symbol, verify: false))
                .AddTable("symbols", ExecutionBenchmarks.ExecutionBenchmarksSettings.SymbolRows, t => t
                    .OrderedBy(s => s.Symbol)
                    .UniqueKey(s => s.Symbol))
                .Build();

            Fixture = [source];
        }

        public static readonly IReadOnlyList<PocoSource> Fixture;
    }

    /// <summary>
    /// The benchmark fixture with one number changed: <c>bars</c> is declared with a
    /// <c>lookup_row_cost</c> of the caller's choosing instead of the planner's 4.0 (D38,
    /// <c>11-m2-index-support.md</c>). Everything else — the rows, the keys, the collations, the
    /// <c>(symbol, ts)</c> index, the statistics — is the source's own.
    /// </summary>
    /// <remarks>
    /// This is the control that <c>window-no-index</c> cannot be. Withdrawing the index changes what
    /// the planner knows; repricing a fetched row changes only what it costs, so a plan that comes
    /// out different is the cost model's answer and not a consequence of a smaller catalog.
    /// </remarks>
    private sealed class Repriced : ISourceRuntime, IColumnarBatchSource
    {
        /// <summary>
        /// The descriptors are classes with <c>init</c> setters and no <c>with</c>, so the copies
        /// below are written out by hand. These counts fail the run loudly if a property is added,
        /// rather than letting a copy drop it and a measurement quietly describe a different table.
        /// </summary>
        private const int TableProperties = 12;

        private const int SchemaProperties = 10;

        private readonly PocoSource _inner;
        private readonly double _lookupRowCost;

        private Repriced(PocoSource inner, double lookupRowCost)
        {
            _inner = inner;
            _lookupRowCost = lookupRowCost;
        }

        public static IReadOnlyList<ISourceRuntime> Fixture(double lookupRowCost)
        {
            Check(typeof(Catalog.TableDescriptor), TableProperties);
            Check(typeof(Catalog.SchemaDescriptor), SchemaProperties);
            return
            [
                .. ExecutionBenchmarks.ExecutionBenchmarksSettings.Fixture
                    .Select(source => new Repriced(source, lookupRowCost)),
            ];
        }

        private static void Check(System.Type type, int expected)
        {
            var actual = type.GetProperties().Length;
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"{type.Name} has {actual} properties where the repriced copy in "
                    + $"AllocationProfile was written for {expected}. Add the new one to the copy "
                    + "before trusting a profile taken through it.");
            }
        }

        public bool TryClaimEngine(object identity, out SourceSharing mode)
        {
            mode = SourceSharing.Exclusive;
            return true;
        }

        public void ReleaseEngine(object identity)
        {
        }

        public string SourceId => _inner.SourceId;

        public Catalog.SchemaDescriptor DescribeSchema()
        {
            var schema = _inner.DescribeSchema();
            return new Catalog.SchemaDescriptor
            {
                SourceId = schema.SourceId,
                Name = schema.Name,
                Kind = schema.Kind,
                Dialect = schema.Dialect,
                Capabilities = schema.Capabilities,
                DialectProfile = schema.DialectProfile,
                CostProfile = schema.CostProfile,
                Tables = [.. schema.Tables.Select(Reprice)],
                Functions = schema.Functions,
                TrustSourceRowLevelSecurity = schema.TrustSourceRowLevelSecurity,
            };
        }

        private Catalog.TableDescriptor Reprice(Catalog.TableDescriptor table) =>
            table.Name != "bars"
                ? table
                : new Catalog.TableDescriptor
                {
                    Name = table.Name,
                    Columns = table.Columns,
                    RowCount = table.RowCount,
                    RowCountKind = table.RowCountKind,
                    CostProfile = new Catalog.CostProfile { LookupRowCost = _lookupRowCost },
                    UniqueKeys = table.UniqueKeys,
                    Collations = table.Collations,
                    Indexes = table.Indexes,
                    ForeignKeys = table.ForeignKeys,
                    Partitioning = table.Partitioning,
                    Entitlement = table.Entitlement,
                    IsPublic = table.IsPublic,
                };

        public ValueTask RefreshAsync(CancellationToken ct) =>
            ((ISourceRuntime)_inner).RefreshAsync(ct);

        public IAsyncEnumerable<Apache.Arrow.RecordBatch> ScanAsync(
            ScanRequest request, ScanContext context, CancellationToken ct) =>
            _inner.ScanAsync(request, context, ct);

        public IAsyncEnumerable<Apache.Arrow.RecordBatch> IndexLookupAsync(
            IndexLookupRequest request, ScanContext context, CancellationToken ct) =>
            _inner.IndexLookupAsync(request, context, ct);

        IColumnarScan? IColumnarBatchSource.ColumnarScan(ScanRequest request, ScanContext context) =>
            ((IColumnarBatchSource)_inner).ColumnarScan(request, context);

        IColumnarScan? IColumnarBatchSource.ColumnarLookup(
            IndexLookupRequest request, ScanContext context) =>
            ((IColumnarBatchSource)_inner).ColumnarLookup(request, context);
    }

    /// <summary>
    /// <c>GCAllocationTick</c> from the runtime provider, collected in process. Each tick carries the
    /// type of the object that crossed the threshold and the bytes allocated since the previous one,
    /// which is what makes the totals a share of allocation rather than an object count.
    /// </summary>
    private sealed class AllocationTickListener : EventListener
    {
        private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";

        /// <summary>The runtime's <c>GCKeyword</c>. <c>GCAllocationTick</c> needs Verbose as well.</summary>
        private const EventKeywords GcKeyword = (EventKeywords)0x1;

        /// <summary><c>AllocationKind</c>: 1 is a large-object allocation, which ticks individually.</summary>
        private const int LargeObject = 1;

        private readonly Lock _gate = new();
        private readonly Dictionary<string, Tally> _byType = new(StringComparer.Ordinal);

        /// <summary>
        /// Every large-object allocation, by size. A tick over the large-object threshold names one
        /// object rather than a sample of many, so this list is the individual rentals — which is
        /// what turns "the arena fell back to the heap eighteen times" into which eighteen.
        /// </summary>
        private readonly List<(string Type, long Size)> _large = [];

        private volatile bool _collecting;

        public void Start() => _collecting = true;

        public void Stop() => _collecting = false;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == RuntimeProvider)
            {
                EnableEvents(eventSource, EventLevel.Verbose, GcKeyword);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (!_collecting
                || eventData.EventName is not { } name
                || !name.StartsWith("GCAllocationTick", StringComparison.Ordinal))
            {
                return;
            }

            var type = "(unknown)";
            long bytes = 0;
            long objectSize = 0;
            var large = false;
            var names = eventData.PayloadNames;
            var payload = eventData.Payload;
            if (names is null || payload is null)
            {
                return;
            }

            for (var i = 0; i < names.Count && i < payload.Count; i++)
            {
                switch (names[i])
                {
                    case "TypeName" when payload[i] is string text:
                        type = text;
                        break;
                    case "AllocationAmount64" when payload[i] is ulong wide && wide != 0:
                        bytes = (long)wide;
                        break;
                    case "AllocationAmount" when bytes == 0 && payload[i] is uint narrow:
                        bytes = narrow;
                        break;
                    case "AllocationKind" when payload[i] is uint kind:
                        large = kind == LargeObject;
                        break;
                    case "ObjectSize" when payload[i] is ulong size:
                        objectSize = (long)size;
                        break;
                }
            }

            lock (_gate)
            {
                var tally = _byType.TryGetValue(type, out var existing) ? existing : default;
                _byType[type] = new Tally(
                    tally.Bytes + bytes,
                    tally.Count + 1,
                    tally.LargeBytes + (large ? bytes : 0));
                if (large)
                {
                    _large.Add((type, objectSize != 0 ? objectSize : bytes));
                }
            }
        }

        public void Report(TextWriter output)
        {
            List<KeyValuePair<string, Tally>> rows;
            List<(string Type, long Size)> large;
            lock (_gate)
            {
                rows = [.. _byType.OrderByDescending(p => p.Value.Bytes)];
                large = [.. _large.OrderByDescending(p => p.Size)];
            }

            var total = rows.Sum(r => r.Value.Bytes);
            output.WriteLine(
                $"  --- allocation ticks: {rows.Count} types, {total} bytes attributed "
                + "(large-object ticks are exact; small-object ones sample ~100 KB apart) ---");
            foreach (var row in rows.Take(TopTypes))
            {
                output.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {row.Value.Bytes,14} bytes {row.Value.Bytes * 100.0 / Math.Max(total, 1),6:F2}% "
                        + $"{row.Value.Count,7} ticks {row.Value.LargeBytes,14} LOH  {row.Key}"));
            }

            if (rows.Count > TopTypes)
            {
                output.WriteLine($"  ... and {rows.Count - TopTypes} more types");
            }

            if (large.Count == 0)
            {
                return;
            }

            output.WriteLine(
                $"  --- the {Math.Min(large.Count, TopTypes)} largest of {large.Count} "
                + $"large-object allocations, {large.Sum(p => p.Size)} bytes in all ---");
            foreach (var one in large.Take(TopTypes))
            {
                output.WriteLine($"  {one.Size,14} bytes  {one.Type}");
            }
        }

        private readonly record struct Tally(long Bytes, long Count, long LargeBytes);
    }
}
