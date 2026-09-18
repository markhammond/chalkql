using Akade.IndexedSet;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Poco;
using IndexKind = Chalk.Ir.IndexKind;

namespace Chalk.Sample.AkadeIndexedSet;

/// <summary>A bar, as a host would write it. Ts is a wall-clock timestamp; the index key is its storage form.</summary>
public sealed record Bar(Utf8String Symbol, DateTime Ts, double Close, long Volume);

/// <summary>
/// The index extension point, demonstrated: a structure Chalk knows nothing about — here
/// <see href="https://github.com/akade/Akade.IndexedSet">Akade.IndexedSet</see> 1.5.0 (MIT) —
/// registered as the index behind a SQL table, and a query planned into a lookup against it.
///
/// <para>
/// The rows are an <c>IndexedSet&lt;Bar&gt;</c>, which implements no collection interface at all, so
/// they are registered through the <c>IReadOnlyCollection&lt;T&gt;</c> overload and scanned by
/// enumeration. The two indexes are Akade's, wrapped by <see cref="AkadeIndex{T,TKey}"/>.
/// </para>
///
/// <para>
/// Run it with <c>dotnet run --project dotnet/samples/Chalk.Sample.AkadeIndexedSet</c>; it starts its
/// own sidecar over a Unix domain socket, so there is nothing to configure. The same wrappers back
/// the <c>bars-akade</c> fixture the integration suite runs the whole corpus against.
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var current = new AkadeRows<Bar>(BarsIndexedSet.Build(GenerateBars()));
        var source = BarsIndexedSet.AsSource(() => current, "mem");
        source.SnapshotReleased += release =>
            Console.WriteLine(
                $"released: the set of {((AkadeRows<Bar>)release.Collection).Count} rows behind {release.Table}");

        var table = source.DescribeSchema().FindTable("bars")!;
        Console.WriteLine(
            $"bars: {table.RowCount} rows, indexes "
            + string.Join(", ", table.Indexes.Select(i => $"{i.Name}({string.Join(",", i.Columns)})")));

        // The claim an index's descriptor makes is one Chalk never rechecks at query time, so an
        // adapter author proves it once. Chalk.TestKit ships the same battery as
        // PocoIndexConformance.Verify; this is the shape of what it does.
        Console.WriteLine("conformance: " + BarsIndexedSet.SelfCheck(current.Set));

        await using var sidecar = await PlannerProcess.StartAsync();
        
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "akade",
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
        });

        var query = await engine.PrepareAsync(
            "SELECT symbol, ts, \"close\" FROM bars WHERE symbol = ? AND ts >= ?");
        Console.WriteLine("plan:" + Environment.NewLine + PlanPrinter.Print(query.Plan).TrimEnd());

        var (rows, scanned) = await CountAsync(engine, query);
        Console.WriteLine($"{rows} rows, {scanned} scanned of {table.RowCount}");

        // A replacement set, built off the execution path and published through a refresh: the
        // snapshot, its columns and its Akade indexes are replaced together, an execution in flight
        // would keep the old ones, and the old set comes back through SnapshotReleased once nothing
        // reads it any more.
        var replacement = BarsIndexedSet.Build(GenerateBars().Append(
            new Bar(Utf8String.FromString("BTCUSDT"), new DateTime(2026, 1, 1, 1, 0, 0), 60_060d, 160)));
        current = new AkadeRows<Bar>(replacement);
        await engine.RefreshAsync();
        var (after, scannedAfter) = await CountAsync(engine, query);
        Console.WriteLine($"after the refresh: {after} rows, {scannedAfter} scanned of {replacement.Count}");
        return 0;
    }

    private static async Task<(int Rows, long Scanned)> CountAsync(ChalkEngine engine, PreparedQuery query)
    {
        await using var execution = await engine.ExecuteAsync(
            query, ["BTCUSDT", new DateTime(2026, 1, 1, 0, 5, 0)]);
        var rows = 0;
        await foreach (var batch in execution.Batches)
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return (rows, (long)execution.Stats.RowsScanned);
    }

    private static List<Bar> GenerateBars()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var rows = new List<Bar>();
        for (var minute = 0; minute < 60; minute++)
        {
            foreach (var symbol in new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT" })
            {
                var close = symbol =="BTCUSDT" ? 60_000d + minute : 3_000d + minute;
                rows.Add(new Bar(Utf8String.FromString(symbol), start.AddMinutes(minute), close, 100 + minute));
            }
        }

        return rows;
    }
}

/// <summary>
/// The reusable half: an <c>IndexedSet&lt;Bar&gt;</c> with two Akade indexes, and the Chalk source
/// that reads it. The integration suite builds the corpus's <c>bars-akade</c> configuration with
/// exactly these functions, over its own row type.
/// </summary>
public static class BarsIndexedSet
{
    /// <summary>TIMESTAMP(9) storage: nanoseconds since the epoch, which is what a bound carries.</summary>
    public static long Nanoseconds(DateTime value) =>
        (value.Ticks - DateTime.UnixEpoch.Ticks) * 100L;

    /// <summary>An Akade set with a range index on <c>(symbol, ts)</c> and another on <c>(ts, symbol)</c>.</summary>
    public static IndexedSet<Bar> Build(IEnumerable<Bar> rows) =>
        IndexedSetBuilder.Create(rows)
            .WithRangeIndex(SymbolTs, SymbolTsComparer)
            .WithRangeIndex(TsSymbol, TsSymbolComparer)
            .Build();

    /// <summary>
    /// The Chalk table: rows by enumeration, indexes by the Akade wrappers — made once per snapshot
    /// over that snapshot's set, so a refresh that hands over a new set brings its indexes with it
    /// and an execution in flight keeps the old ones.
    /// </summary>
    public static PocoSource AsSource(Func<AkadeRows<Bar>> current, string sourceId) =>
        new PocoSourceBuilder(sourceId)
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable(
                "bars",
                () => current(),
                t => t
                    .Index(SymbolTsDescriptor, rows => SymbolTsIndex(SetOf(rows)))
                    .Index(TsSymbolDescriptor, rows => TsSymbolIndex(SetOf(rows))))
            .Build();

    /// <summary>
    /// The set a snapshot's indexes are built over: the registered set when the snapshot is over
    /// the rows the host handed in, else — rows Chalk appended — a set built from those rows.
    /// </summary>
    public static IndexedSet<Bar> SetOf(IReadOnlyCollection<Bar> rows) =>
        rows is AkadeRows<Bar> registered ? registered.Set : Build(rows);

    /// <summary>The <c>(symbol, ts)</c> index, unique because a symbol has one bar per minute.</summary>
    /// <summary>What the catalog publishes for the <c>(symbol, ts)</c> index; every snapshot's product carries it.</summary>
    public static readonly IndexDescriptor SymbolTsDescriptor = new()
    {
        Name = "ix_bars_symbol_ts",
        Kind = IndexKind.Ordered,
        Columns = [0, 1],
        Unique = true,
    };

    /// <summary>What the catalog publishes for the <c>(ts, symbol)</c> index.</summary>
    public static readonly IndexDescriptor TsSymbolDescriptor = new()
    {
        Name = "ix_bars_ts_symbol",
        Kind = IndexKind.Ordered,
        Columns = [1, 0],
        Unique = false,
    };

    public static AkadeIndex<Bar, (Utf8String Symbol, long Ts)> SymbolTsIndex(IndexedSet<Bar> set) =>
        new(
            SymbolTsDescriptor,
            set,
            SymbolTs,
            "SymbolTs",
            new AkadeKey<Bar, (Utf8String, long)>(
                (bounds, max) => (
                    bounds[0].ToUtf8String(),
                    bounds.Count > 1 ? (long)bounds[1]! : max ? long.MaxValue : long.MinValue)),
            SymbolTsComparer);

    /// <summary>
    /// The <c>(ts, symbol)</c> index — the same rows, the other key order.</summary>
    /// <remarks>
    /// Converts Chalk logical bound values into this index's native TKey
    /// representation. Bound CLR types are not required to match the CLR types
    /// used by the indexed source; for example SQL STRING may arrive as either
    /// string or Utf8String.
    /// </remarks>
    private static AkadeIndex<Bar, (long Ts, Utf8String Symbol)> TsSymbolIndex(
        IndexedSet<Bar> set) =>
        new(
            TsSymbolDescriptor,
            set,
            TsSymbol,
            "TsSymbol",
            new AkadeKey<Bar, (long, Utf8String)>(
                (bounds, max) => (
                    (long)bounds[0]!,
                    bounds.Count > 1
                        ? bounds[1].ToUtf8String()
                        : max
                            ? UpperBound
                            : LowerBound)),
            TsSymbolComparer);

    /// <summary>A quick self-check for the demo; the real battery is <c>PocoIndexConformance.Verify</c>.</summary>
    public static string SelfCheck(IndexedSet<Bar> set)
    {
        var index = SymbolTsIndex(set);
        var rows = set.FullScan().ToList();
        var range = IndexKeyRange.Equality("BTCUSDT");
        var byIndex = index.Lookup(range).Count();
        var byScan = rows.Count(b => b.Symbol == "BTCUSDT"u8);
        return byIndex == byScan
            ? $"ix_bars_symbol_ts agrees with the full scan ({byIndex} rows)"
            : $"MISMATCH: index {byIndex}, scan {byScan}";
    }

    /// <summary>
    /// Above every string the fixture holds, so it stands in for "no upper bound on the symbol".
    /// A production adapter would use a key type with a real maximum instead.
    /// </summary>
    private static readonly Utf8String LowerBound = Utf8String.Empty;
    private static readonly Utf8String UpperBound = "\uFFFF".ToUtf8String();
    
    // The accessors are static so that Akade's CallerArgumentExpression records the same text at
    // registration and at query time: "SymbolTs" and "TsSymbol".
    private static readonly Func<Bar, (Utf8String Symbol, long Ts)> SymbolTs =
        bar => (bar.Symbol, Nanoseconds(bar.Ts));

    private static readonly Func<Bar, (long Ts, Utf8String Symbol)> TsSymbol =
        bar => (Nanoseconds(bar.Ts), bar.Symbol);

    private static readonly AkadeKeyComparer<(Utf8String Symbol, long Ts)> SymbolTsComparer =
        new(key => [key.Symbol, key.Ts]);

    private static readonly AkadeKeyComparer<(long Ts, Utf8String Symbol)> TsSymbolComparer =
        new(key => [key.Ts, key.Symbol]);
}
