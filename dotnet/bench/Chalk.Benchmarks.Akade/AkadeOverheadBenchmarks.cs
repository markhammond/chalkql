using Akade.IndexedSet;
using Apache.Arrow;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Chalk.Client;
using Chalk.Sources.Akade;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Benchmarks.Akade;

/// <summary>
/// What a ChalkQL statement costs over an <c>Akade.IndexedSet</c>, against the same question asked of
/// the set directly. Not run in CI: it exists so that the overhead of planning-free execution — the
/// operator pipeline, the Arrow batches, the index adapter — is a number rather than a guess.
/// </summary>
/// <remarks>
/// <para>
/// Each pair asks the same thing twice. The baseline is Akade's own call, consumed with a
/// <c>foreach</c>; the measured case is a prepared ChalkQL statement, executed with the same
/// values and consumed batch by batch. The statement is prepared once, in setup, so nothing here
/// measures the planner: this is the cost of running a plan, which is what a host pays per request.
/// </para>
/// <para>
/// The set is two hundred thousand purchases: <c>amount</c> equal to the row number, so a band is a
/// known share of the table; <c>unit_price</c> a permutation of the same range, so the ordering is
/// total; five hundred products, so a point lookup returns four hundred rows.
/// </para>
/// <para>
/// The columns that matter are <c>Mean</c>, <c>Ratio</c> and <c>Allocated</c>. Wall-clock differs
/// by machine; the ratio between a pair does not much, and <c>Allocated</c> is what says a path
/// costs nothing per row.
/// </para>
/// </remarks>
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[MemoryDiagnoser]
[HideColumns("Job", "Error", "StdDev", "Median", "RatioSD")]
public class AkadeOverheadBenchmarks
{
    public sealed record Purchase(int Id, int ProductId, int Amount, int UnitPrice);

    private const int Rows = 200_000;
    private const int Products = 500;
    private const int PointProduct = 123;
    private const int BandLow = 100_000;
    private const int BandHigh = 100_999;

    private IndexedSet<int, Purchase> _set = null!;
    private SidecarFixture _sidecar = null!;
    private ChalkEngine _engine = null!;
    private PreparedQuery _point = null!;
    private PreparedQuery _band = null!;
    private PreparedQuery _cheapest = null!;
    private PreparedQuery _sum = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var rows = new Purchase[Rows];
        for (var i = 0; i < Rows; i++)
        {
            // 7919 is coprime with 200 000, so the unit prices are a permutation and no two rows tie.
            rows[i] = new Purchase(
                Id: i, ProductId: i % Products, Amount: i, UnitPrice: (int)((long)i * 7919 % Rows));
        }

        _set = rows.ToIndexedSet(x => x.Id)
            .WithIndex(x => x.ProductId)
            .WithRangeIndex(x => x.Amount)
            .WithRangeIndex(x => x.UnitPrice)
            .Build();

        var source = AkadeSource.From("purchases", _set)
            .TableName("purchases")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .Build();

        _sidecar = await SidecarFixture.StartAsync();
        if (!_sidecar.IsAvailable)
        {
            throw new InvalidOperationException(_sidecar.SkipReason);
        }

        _engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "akade-overhead",
            Sources = [source],
            Planner = _sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { BatchSize = 4096, OutputMemory = OutputMemory.Pooled },
        });

        _point = await _engine.PrepareAsync("SELECT id, amount FROM purchases WHERE product_id = ?");
        _band = await _engine.PrepareAsync("SELECT id, amount FROM purchases WHERE amount BETWEEN ? AND ?");
        _cheapest = await _engine.PrepareAsync(
            "SELECT id, unit_price FROM purchases ORDER BY unit_price LIMIT 1");
        // The sum of two hundred thousand row numbers does not fit an INTEGER, and SUM keeps its
        // argument's type, so the accumulator is widened by hand rather than overflowing by name.
        _sum = await _engine.PrepareAsync("SELECT SUM(CAST(amount AS BIGINT)) FROM purchases");
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _engine.DisposeAsync();
        await _sidecar.DisposeAsync();
    }

    // ---- a point lookup through the hash index: four hundred rows ----

    [BenchmarkCategory("point lookup")]
    [Benchmark(Baseline = true, Description = "akade Where(product_id)")]
    public long AkadePointLookup()
    {
        long total = 0;
        foreach (var row in _set.Where(x => x.ProductId, PointProduct))
        {
            total += row.Amount;
        }

        return total;
    }

    [BenchmarkCategory("point lookup")]
    [Benchmark(Description = "chalkql WHERE product_id = ?")]
    public Task<long> ChalkPointLookup() => SumInt32ColumnAsync(_point, [PointProduct], column: 1);

    // ---- a range through the ordered index: a thousand rows ----

    [BenchmarkCategory("band")]
    [Benchmark(Baseline = true, Description = "akade Range(amount)")]
    public long AkadeBand()
    {
        long total = 0;
        foreach (var row in _set.Range(x => x.Amount, BandLow, BandHigh, inclusiveStart: true, inclusiveEnd: true))
        {
            total += row.Amount;
        }

        return total;
    }

    [BenchmarkCategory("band")]
    [Benchmark(Description = "chalkql WHERE amount BETWEEN ? AND ?")]
    public Task<long> ChalkBand() => SumInt32ColumnAsync(_band, [BandLow, BandHigh], column: 1);

    // ---- the cheapest row: one walk of the ordered index, one row ----

    [BenchmarkCategory("cheapest")]
    [Benchmark(Baseline = true, Description = "akade OrderBy(unit_price).First()")]
    public long AkadeCheapest() => _set.OrderBy(x => x.UnitPrice, 0).First().Id;

    [BenchmarkCategory("cheapest")]
    [Benchmark(Description = "chalkql ORDER BY unit_price LIMIT 1")]
    public Task<long> ChalkCheapest() => SumInt32ColumnAsync(_cheapest, [], column: 0);

    // ---- a full scan: every row through the pipeline, one row out ----

    [BenchmarkCategory("sum")]
    [Benchmark(Baseline = true, Description = "akade foreach sum")]
    public long AkadeSum()
    {
        long total = 0;
        foreach (var row in _set.FullScan())
        {
            total += row.Amount;
        }

        return total;
    }

    [BenchmarkCategory("sum")]
    [Benchmark(Description = "chalkql SUM(CAST(amount AS BIGINT))")]
    public Task<long> ChalkSum() => ReadSumAsync(_sum);

    // ---- support ----

    private async Task<long> SumInt32ColumnAsync(PreparedQuery query, object?[] values, int column)
    {
        long total = 0;
        await using var execution = await _engine.ExecuteAsync(query, values);
        await foreach (var batch in execution.Batches)
        {
            var array = (Int32Array)batch.Column(column);
            for (var i = 0; i < array.Length; i++)
            {
                total += array.GetValue(i)!.Value;
            }

            batch.Dispose();
        }

        return total;
    }

    /// <summary>The one value a <c>SUM</c> answers, whatever integer width the planner gave it.</summary>
    private async Task<long> ReadSumAsync(PreparedQuery query)
    {
        long total = 0;
        await using var execution = await _engine.ExecuteAsync(query);
        await foreach (var batch in execution.Batches)
        {
            switch (batch.Column(0))
            {
                case Int64Array longs:
                    for (var i = 0; i < longs.Length; i++)
                    {
                        total += longs.GetValue(i)!.Value;
                    }

                    break;
                case Int32Array ints:
                    for (var i = 0; i < ints.Length; i++)
                    {
                        total += ints.GetValue(i)!.Value;
                    }

                    break;
                case Decimal128Array decimals:
                    for (var i = 0; i < decimals.Length; i++)
                    {
                        total += (long)decimals.GetValue(i)!.Value;
                    }

                    break;
                default:
                    throw new InvalidOperationException(
                        $"SUM answered a {batch.Column(0).GetType().Name}, which this consumer does not read");
            }

            batch.Dispose();
        }

        return total;
    }
}
