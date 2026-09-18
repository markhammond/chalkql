using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Ir;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// The <c>utf8_symbols</c> fixture (D145, <c>docs/design/24-zero-gc.md</c> §3 and §9): a
/// <c>Utf8String</c> key, a nullable <c>Utf8String</c>, and a <c>ReadOnlyMemory&lt;byte&gt;</c>
/// beside them that must still be BINARY.
/// </summary>
public sealed record Utf8SymbolRow(
    Utf8String Symbol, Utf8String? Label, ReadOnlyMemory<byte> Payload, long Rank);

/// <summary>A child row whose <c>symbol</c> is a foreign key into <see cref="Utf8SymbolRow"/>.</summary>
public sealed record Utf8QuoteRow(Utf8String Symbol, long Seq);

/// <summary>
/// A <c>Utf8String</c> member maps to STRING with a byte copy, and everything the POCO source claims
/// about a key column — order, uniqueness, index lookup, foreign keys, the D17 verification — holds
/// over bytes.
/// </summary>
public sealed class PocoUtf8Tests
{
    /// <summary>
    /// Ordinal byte order, which is not culture order and is not <c>string.CompareOrdinal</c> order
    /// for astral characters: uppercase before lowercase, ASCII before anything multi-byte.
    /// </summary>
    private static IReadOnlyList<Utf8SymbolRow> Symbols() =>
    [
        Row("ADAUSDT", "ADA", 0),
        Row("Apple", null, 1),
        Row("BTCUSDT", "BTC", 2),
        Row("Zebra", "Z", 3),
        Row("A_VERY_LONG_SYMBOL_NAME", "long", 4),
        Row("apple", null, 5),
        Row("zebra", "z", 6),
        Row("ÉTOILE", "É", 7),
        Row("日経", null, 8),
    ];

    private static Utf8SymbolRow Row(string symbol, string? label, long rank)
    {
        // Written out: a null literal beside a Utf8String binds to the implicit byte[] conversion
        // and gives the empty value rather than a NULL (V43).
        Utf8String? text = default;
        if (label is not null)
        {
            text = Utf8String.FromString(label);
        }

        return new Utf8SymbolRow(
            Utf8String.FromString(symbol), text, new ReadOnlyMemory<byte>([(byte)rank, 0xFF]), rank);
    }

    private static IReadOnlyList<Utf8SymbolRow> Sorted()
    {
        var rows = Symbols().ToList();
        rows.Sort(static (a, b) => a.Symbol.CompareTo(b.Symbol));
        return rows;
    }

    private static PocoSource Build(IReadOnlyList<Utf8SymbolRow>? rows = null) =>
        new PocoSourceBuilder("mem")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("utf8_symbols", rows ?? Sorted(), t => t
                .OrderedBy(s => s.Symbol)
                .UniqueKey(s => s.Symbol)
                .Index(s => s.Rank))
            .Build();

    [Fact]
    public void A_Utf8String_member_is_STRING_and_a_byte_member_beside_it_is_still_BINARY()
    {
        var table = PocoTestSupport.Describe(Build(), "utf8_symbols");

        Assert.Equal(TypeKind.String, table.Columns[0].Type.Kind);
        Assert.False(table.Columns[0].Type.Nullable);
        Assert.Equal("symbol", table.Columns[0].Name);

        Assert.Equal(TypeKind.String, table.Columns[1].Type.Kind);
        Assert.True(table.Columns[1].Type.Nullable);

        // The marker type is what says "this is text": the memory beside it stays BINARY.
        Assert.Equal(TypeKind.Binary, table.Columns[2].Type.Kind);
    }

    [Fact]
    public async Task A_scan_emits_the_bytes_the_member_held()
    {
        var rows = Sorted();
        var source = Build(rows);
        var batches = await PocoTestSupport.ScanAsync(source, "utf8_symbols", 4);
        try
        {
            var symbols = new List<string>();
            var labels = new List<string?>();
            foreach (var batch in batches)
            {
                var symbol = (StringArray)batch.Column(0);
                var label = (StringArray)batch.Column(1);
                for (var i = 0; i < batch.Length; i++)
                {
                    // Read as bytes rather than as a string: GetUtf8 is what a host now has.
                    symbols.Add(symbol.GetUtf8(i).ToString());
                    labels.Add(label.IsNull(i) ? null : label.GetUtf8(i).ToString());
                }
            }

            Assert.Equal(rows.Select(r => r.Symbol.ToString()), symbols);
            Assert.Equal(rows.Select(r => r.Label?.ToString()), labels);
        }
        finally
        {
            PocoTestSupport.Dispose(batches);
        }
    }

    [Fact]
    public void The_declared_collation_is_verified_in_byte_order()
    {
        // In byte order it builds: uppercase before lowercase, ASCII before anything multi-byte.
        var sorted = Sorted();
        _ = Build(sorted);
        Assert.Equal(
            ["ADAUSDT", "A_VERY_LONG_SYMBOL_NAME", "Apple", "BTCUSDT", "Zebra", "apple", "zebra", "ÉTOILE", "日経"],
            sorted.Select(r => r.Symbol.ToString()));

        // "apple" before "Apple" is a perfectly ordinary case-insensitive order and is backwards in
        // bytes, which is what the column is collated in.
        var wrong = sorted.ToList();
        var apple = wrong.FindIndex(r => r.Symbol == Utf8String.FromString("apple"));
        wrong.Insert(0, wrong[apple]);
        wrong.RemoveAt(apple + 1);

        var failure = Assert.Throws<CatalogVerificationException>(() => Build(wrong));
        Assert.Contains("symbol", failure.Message, StringComparison.Ordinal);
        Assert.Contains("goes backwards", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_key_fails_uniqueness_over_bytes()
    {
        // The same characters from two different arrays: equal by bytes, and therefore a duplicate.
        var rows = new List<Utf8SymbolRow>
        {
            Row("BTCUSDT", "a", 0),
            new(
                Utf8String.FromBytes("BTCUSDT"u8.ToArray()),
                Utf8String.FromString("b"),
                default,
                1),
        };

        var failure = Assert.Throws<CatalogVerificationException>(() => Build(rows));
        Assert.Contains("unique key (symbol)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("BTCUSDT", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>PermutationIndex</c> over a <c>Utf8String</c> key: the lookup's bounds arrive as STRING
    /// literals, and the comparison happens in the column's own byte order.
    /// </summary>
    [Fact]
    public async Task An_index_lookup_over_a_Utf8String_key_finds_the_row()
    {
        var rows = Sorted();
        var source = new PocoSourceBuilder("mem")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("utf8_symbols", rows, t => t
                .UniqueIndex(s => s.Symbol))
            .Build();

        var table = PocoTestSupport.Describe(source, "utf8_symbols");
        var index = table.Indexes.Single();

        Assert.Equal(["ÉTOILE"], await LookupAsync(source, index.Name, "ÉTOILE"));
        Assert.Equal(["A_VERY_LONG_SYMBOL_NAME"], await LookupAsync(source, index.Name, "A_VERY_LONG_SYMBOL_NAME"));
        Assert.Empty(await LookupAsync(source, index.Name, "nope"));

        // A range in bytes: from "Z" (0x5A) up to but not including "a" (0x61). "Zebra" is in;
        // "apple" is not, because "a" is a prefix of it and therefore sorts before it. A culture
        // comparison would have put "apple" and "Apple" together and got a different answer.
        Assert.Equal(["Zebra"], await LookupRangeAsync(source, index.Name, "Z", "a"));
    }

    [Fact]
    public void A_foreign_key_over_bytes_is_verified()
    {
        var symbols = Sorted();
        var quotes = new List<Utf8QuoteRow>
        {
            new(Utf8String.FromString("BTCUSDT"), 0),
            new(Utf8String.FromBytes("ÉTOILE"u8.ToArray()), 1),
        };

        _ = new PocoSourceBuilder("mem")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("utf8_symbols", symbols, t => t.UniqueKey(s => s.Symbol))
            .AddTable("utf8_quotes", quotes, t => t
                .ForeignKey(q => q.Symbol).References<Utf8SymbolRow>(s => s.Symbol, verify: true))
            .Build();

        var orphan = new List<Utf8QuoteRow> { new(Utf8String.FromString("NOPEUSDT"), 0) };
        var failure = Assert.Throws<CatalogVerificationException>(() =>
            new PocoSourceBuilder("mem")
                .NamingPolicy(PocoNamingPolicy.SnakeCase)
                .AddTable("utf8_symbols", symbols, t => t.UniqueKey(s => s.Symbol))
                .AddTable("utf8_quotes", orphan, t => t
                    .ForeignKey(q => q.Symbol).References<Utf8SymbolRow>(s => s.Symbol, verify: true))
                .Build());
        Assert.Contains("NOPEUSDT", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bytes that are not UTF-8 are refused where they enter the column, naming the column and the
    /// row — so an Arrow buffer never holds invalid UTF-8 (§2).
    /// </summary>
    [Fact]
    public async Task Invalid_UTF_8_is_refused_at_the_chunk_writer()
    {
        var validateUtf = !PocoUtf8Column<int>.TrustSourceUtf8Validation;
        if (validateUtf)
        {
            var rows = new List<Utf8SymbolRow>
            {
                Row("ok", null, 0),
                new(Utf8String.FromBytes(new byte[] { 0xC3, 0x28 }), null, default, 1),
            };

            var source = new PocoSourceBuilder("mem")
                .NamingPolicy(PocoNamingPolicy.SnakeCase)
                .AddTable("utf8_symbols", rows)
                .Build();

            var failure =
                await Assert.ThrowsAsync<InvalidUtf8Exception>(() =>
                    PocoTestSupport.ScanAsync(source, "utf8_symbols", 8));
            Assert.Equal(1, failure.Row);
            Assert.Contains("utf8_symbols.symbol", failure.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The gate of §9: a <c>Utf8String</c> column scans at zero bytes per <em>row</em>. Measured as a
    /// slope, the way the benchmark's per-batch gate is: the same number of batches over three times
    /// the rows, so what is left is the per-row cost and not the per-batch Arrow graph. Each point of
    /// the slope is the <em>minimum</em> of several measured runs (<see cref="Samples"/>): an
    /// allocation counter can only ever be inflated by work that is not the scan's — a per-thread
    /// cache filled on first use, a continuation that resumed on a colder thread — so the floor is
    /// the scan's own figure and a single sample is not.
    /// </summary>
    [Fact]
    public async Task A_Utf8String_scan_allocates_nothing_per_row()
    {
        const int Small = 20_000;
        const int Large = 60_000;
        const int Batches = 5;

        using var arena = new ExecutionArena();
        var small = await MeasureAsync(Rows(Small), Small / Batches, arena);
        var large = await MeasureAsync(Rows(Large), Large / Batches, arena);

        var slope = (large - small) / (double)(Large - Small);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Utf8String column: {small} bytes for {Small} rows, {large} for {Large} "
            + $"in {Batches} batches either way = {slope:0.0000} bytes/row");
        Assert.Equal(0, arena.OutstandingBytes);
        Assert.True(
            Math.Abs(slope) < 0.01,
            $"a Utf8String scan allocated {slope:0.0000} bytes per row ({small} → {large})");
    }

    private static List<Utf8SymbolRow> Rows(int count)
    {
        var symbols = Sorted();
        var rows = new List<Utf8SymbolRow>(count);
        for (var i = 0; i < count; i++)
        {
            rows.Add(symbols[i % symbols.Count] with { Rank = i });
        }

        return rows;
    }

    private static async Task<long> MeasureAsync(
        IReadOnlyList<Utf8SymbolRow> rows, int batchSize, ExecutionArena arena)
    {
        var poco = new PocoSourceBuilder("mem")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("utf8_symbols", rows)
            .Build();

        // The first run warms the compiled loops, the JIT and the arena's pools.
        await ScanAsync(poco, batchSize, arena);
        var best = long.MaxValue;
        var sampled = 0;
        for (var attempt = 0; attempt < Samples * 2 && sampled < Samples; attempt++)
        {
            // A per-thread cache -- ArrayPool.Shared's buckets, a lazily built stub -- is warm only on
            // the thread that filled it, and an await may resume anywhere. So warm again immediately
            // before each measured run, and discard a run that did not finish on the thread it
            // started on: its two readings are two threads' counters, not a difference.
            await ScanAsync(poco, batchSize, arena);
            var thread = Environment.CurrentManagedThreadId;
            var before = GC.GetAllocatedBytesForCurrentThread();
            await ScanAsync(poco, batchSize, arena);
            var after = GC.GetAllocatedBytesForCurrentThread();
            if (Environment.CurrentManagedThreadId != thread)
            {
                continue;
            }

            sampled++;
            best = Math.Min(best, after - before);
        }

        Assert.True(sampled > 0, "every measured scan resumed on another thread; nothing was measured");
        return best;
    }

    /// <summary>Measured runs per point of the slope; the minimum is the point.</summary>
    private const int Samples = 5;

    private static async Task<int> ScanAsync(PocoSource source, int batchSize, ExecutionArena arena)
    {
        var scanned = 0;
        var request = PocoTestSupport.Request(source, "utf8_symbols", batchSize);
        await foreach (var batch in source.ScanAsync(
            request, PocoTestSupport.Context(arena: arena), TestContext.Current.CancellationToken))
        {
            scanned += batch.Length;
            batch.Dispose();
        }

        return scanned;
    }

    private static async Task<string[]> LookupAsync(PocoSource source, string index, string key) =>
        await LookupCoreAsync(
            source,
            index,
            IndexKeyRange.Equality(key));

    private static async Task<string[]> LookupRangeAsync(
        PocoSource source, string index, string from, string to) =>
        await LookupCoreAsync(
            source,
            index,
            new IndexKeyRange
            {
                Lower = [from],
                Upper = [to],
                LowerInclusive = true,
                UpperInclusive = false,
            });

    private static async Task<string[]> LookupCoreAsync(
        PocoSource source, string index, IndexKeyRange range)
    {
        var descriptor = PocoTestSupport.Describe(source, "utf8_symbols");
        var projection = Enumerable.Range(0, descriptor.Columns.Count).ToArray();
        var request = new IndexLookupRequest
        {
            Table = "utf8_symbols",
            Index = index,
            Projection = projection,
            OutputSchema = ArrowTypeMapping.ToArrowSchema(descriptor, projection),
            BatchSize = 64,
            Ranges = [range],
        };

        var found = new List<string>();
        await foreach (var batch in source.IndexLookupAsync(
            request, PocoTestSupport.Context(), TestContext.Current.CancellationToken))
        {
            var symbol = (StringArray)batch.Column(0);
            for (var i = 0; i < batch.Length; i++)
            {
                found.Add(symbol.GetUtf8(i).ToString());
            }

            batch.Dispose();
        }

        return [.. found];
    }
}
