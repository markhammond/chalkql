using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using ColumnStatistics = Chalk.Catalog.ColumnStatistics;

namespace Chalk.Execution.Tests;

/// <summary>
/// The low-cardinality perfect hash of D255: the search itself, and the aggregate that uses it —
/// including the two ways a declared distinct count can be wrong, both of which must cost speed and
/// never the answer.
/// </summary>
public sealed class PerfectKeyTests
{
    private static readonly string[] Names =
        ["BTCUSDT", "ETHUSDT", "SOLUSDT", "ADAUSDT", "XRPUSDT", "DOGEUSDT"];

    /// <summary>
    /// The declared counts a run is tried with: none at all (-1, the statistic every source may
    /// leave unknown), the right one, two that are too low — the table is built over a set that
    /// turns out not to be all of the keys — one that is too high, so it is never built, and one
    /// above the threshold the design sets.
    /// </summary>
    public static TheoryData<long> DeclaredCounts() => [-1L, 6L, 3L, 1L, 64L, 10_000L];

    [Theory]
    [MemberData(nameof(DeclaredCounts))]
    public async Task A_declared_distinct_count_never_changes_a_string_key_answer(long declared)
    {
        var table = Table("symbol_rows", ChalkType.String(), i => Names[i % Names.Length], declared);
        var source = TestData.Source(table);
        var plan = GroupPlan(table);

        foreach (var batchSize in Runner.BatchSizes)
        {
            var rows = await Runner.RowsAsync(plan, source, batchSize);

            Assert.Equal(Names.Length, rows.Count);
            Assert.Equal(Names, rows.Select(r => r[0] as string));
            Assert.All(rows, r => Assert.Equal(100L, r[1]));
            Assert.Equal(600L * 599L / 2L, rows.Sum(r => (long)r[2]!));
        }
    }

    [Theory]
    [MemberData(nameof(DeclaredCounts))]
    public async Task A_declared_distinct_count_never_changes_an_integer_key_answer(long declared)
    {
        var table = Table("code_rows", ChalkType.Int64(), i => (long)((i % Names.Length) * 1_000_003), declared);
        var source = TestData.Source(table);
        var plan = GroupPlan(table);

        foreach (var batchSize in Runner.BatchSizes)
        {
            var rows = await Runner.RowsAsync(plan, source, batchSize);

            Assert.Equal(Names.Length, rows.Count);
            Assert.All(rows, r => Assert.Equal(100L, r[1]));
            Assert.Equal(600L * 599L / 2L, rows.Sum(r => (long)r[2]!));
        }
    }

    /// <summary>
    /// A NULL key is not one of the declared values and is not in the perfect table: the run has to
    /// group it on its own whether the table was built before the first NULL arrived or after.
    /// </summary>
    [Theory]
    [InlineData(-1L)]
    [InlineData(2L)]
    [InlineData(3L)]
    public async Task A_null_key_groups_beside_a_perfect_table(long declared)
    {
        var table = new TestTable
        {
            Name = "nullable_keys",
            Columns = [("k", ChalkType.String(nullable: true)), ("n", ChalkType.Int64())],
            Statistics = Declare(declared),
            Rows =
            [
                ["aa", 1L], ["bb", 2L], [null, 4L], ["aa", 8L], ["cc", 16L], [null, 32L], ["bb", 64L],
            ],
        };
        var source = TestData.Source(table);

        foreach (var batchSize in Runner.BatchSizes)
        {
            var rows = await Runner.RowsAsync(GroupPlan(table), source, batchSize);

            Assert.Equal(4, rows.Count);
            Assert.Equal(9L, rows.Single(r => (r[0] as string) == "aa")[2]);
            Assert.Equal(66L, rows.Single(r => (r[0] as string) == "bb")[2]);
            Assert.Equal(16L, rows.Single(r => (r[0] as string) == "cc")[2]);
            Assert.Equal(36L, rows.Single(r => r[0] is null)[2]);
        }
    }

    [Theory]
    [MemberData(nameof(DeclaredCounts))]
    public async Task A_declared_distinct_count_agrees_with_the_reference_engine(long declared)
    {
        var table = Table("symbol_rows", ChalkType.String(), i => Names[i % Names.Length], declared);
        await Runner.AssertEnginesAgreeAsync(GroupPlan(table), TestData.Source(table));
    }

    /// <summary>
    /// The search itself over the five-symbol case the design is written against: it finds a
    /// function, and that function sends each key to its own slot.
    /// </summary>
    [Fact]
    public void The_search_separates_the_five_symbol_case()
    {
        string[] symbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT", "ADAUSDT", "XRPUSDT"];
        var store = GroupKeyStore.Create(Vectors.ColumnKind.Utf8);
        using var arena = new ExecutionArena();
        store.Begin(arena);
        var low = new ulong[symbols.Length];
        var high = new ulong[symbols.Length];
        var lengths = new int[symbols.Length];
        for (var i = 0; i < symbols.Length; i++)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(symbols[i]);
            var image = KeyImage.OfBytes(bytes);
            low[i] = image.Low;
            high[i] = image.High;
            lengths[i] = image.Length;
            store.Append(bytes, valid: true);
        }

        var slots = new int[16];
        var found = PerfectKeys.TryBuild(
            low, high, lengths, symbols.Length, store, strings: true, slots, slots.Length,
            out var function);

        Assert.True(found);
        Assert.True(function.Strings);
        var seen = new HashSet<int>();
        for (var i = 0; i < symbols.Length; i++)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(symbols[i]);
            var slot = PerfectKeys.Slot(
                PerfectKeys.Selector(bytes, function.Position1, function.Position2),
                function.Seed,
                function.Shift);

            Assert.InRange(slot, 0, slots.Length - 1);
            Assert.Equal(i, slots[slot]);
            Assert.True(seen.Add(slot));
        }

        store.Release();
    }

    /// <summary>A key too long for its image is never given to the search, because the slot's own
    /// compare could not validate it.</summary>
    [Fact]
    public void The_search_refuses_a_key_longer_than_its_image()
    {
        var store = GroupKeyStore.Create(Vectors.ColumnKind.Utf8);
        using var arena = new ExecutionArena();
        store.Begin(arena);
        string[] keys = ["short", "0123456789abcdefghij"];
        var low = new ulong[keys.Length];
        var high = new ulong[keys.Length];
        var lengths = new int[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(keys[i]);
            var image = KeyImage.OfBytes(bytes);
            low[i] = image.Low;
            high[i] = image.High;
            lengths[i] = image.Length;
            store.Append(bytes, valid: true);
        }

        var slots = new int[8];
        Assert.False(PerfectKeys.TryBuild(
            low, high, lengths, keys.Length, store, strings: true, slots, slots.Length, out _));
        store.Release();
    }

    /// <summary>
    /// The chain the benchmark's <c>GROUP BY symbol</c> goes down, end to end: a POCO table whose
    /// index covers the key column declares an exact distinct count, the compiler reads it off the
    /// catalog, and the aggregate's answer is the one it gives without a statistic at all.
    /// </summary>
    [Fact]
    public async Task A_poco_index_declares_the_distinct_count_the_aggregate_reads()
    {
        var bars = new Bar[600];
        for (var i = 0; i < bars.Length; i++)
        {
            bars[i] = new Bar(Names[i % 5], i, i * 1.5d);
        }

        var indexed = new PocoSourceBuilder("mem")
            .AddTable("bars", bars, t => t.UniqueIndex(b => b.Symbol, b => b.Volume))
            .Build();
        var plain = new PocoSourceBuilder("mem").AddTable("bars", bars).Build();

        // The statistic the aggregate's perfect hash is built on, and its absence on the other side.
        Assert.Equal(5, indexed.DescribeSchema().Tables[0].Columns[0].Statistics.DistinctCount);
        Assert.Equal(-1, plain.DescribeSchema().Tables[0].Columns[0].Statistics.DistinctCount);

        var withStatistic = await PocoGroupsAsync(indexed);
        var without = await PocoGroupsAsync(plain);

        Assert.Equal(5, withStatistic.Count);
        Assert.Equal(without, withStatistic);
        Assert.All(withStatistic, group => Assert.Equal(120L, group.Count));
    }

    private static async Task<List<(string Symbol, long Count)>> PocoGroupsAsync(PocoSource source)
    {
        var schema = source.DescribeSchema();
        var catalog = new Chalk.Catalog.CatalogContext
        {
            ContextId = "test",
            Epoch = 1,
            Schemas = [schema],
        };

        var row = new RowType();
        foreach (var column in schema.Tables[0].Columns)
        {
            row.Fields.Add(new Field { Name = column.Name, Type = column.Type.ToProto() });
        }

        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read("bars", row),
            [0],
            [("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64()))]));

        var compiled = PlanCompiler.Compile(
            plan,
            catalog,
            new Dictionary<string, ISourceRuntime> { ["mem"] = source },
            new ExecutionSettings { BatchSize = 64 });

        var batches = await Runner.CollectAsync(compiled);
        var rows = ResultComparer.Rows(batches);
        foreach (var batch in batches)
        {
            batch.Dispose();
        }

        return [.. rows.Select(r => ((string)r[0]!, (long)r[1]!))];
    }

    private sealed record Bar(string Symbol, long Volume, double Close);

    private static Plan GroupPlan(TestTable table)
    {
        var row = table.RowType();
        return IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(table.Name, row),
            [0],
            [
                ("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64())),
                ("s", IrBuilder.Agg(
                    AggregateFunctionId.Sum, IrBuilder.I64(true), IrBuilder.Ref(row, 1))),
            ]));
    }

    private static TestTable Table(
        string name, ChalkType keyType, Func<int, object> key, long declared) => new()
    {
        Name = name,
        Columns = [("k", keyType), ("n", ChalkType.Int64())],
        Statistics = Declare(declared),
        Rows = [.. Enumerable.Range(0, 600).Select(i => new object?[] { key(i), (long)i })],
    };

    private static Dictionary<int, ColumnStatistics> Declare(long declared) => declared is { } count
        ? new Dictionary<int, ColumnStatistics>
        {
            [0] = new() { Level = StatisticsLevel.Basic, DistinctCount = count },
        }
        : [];
}
