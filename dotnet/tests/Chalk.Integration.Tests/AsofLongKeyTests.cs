using System.Diagnostics;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// An ASOF join's probe costs one binary search however many rows its key has. The join's hash
/// table chains a key's rows in input order, and it used to find each new row's place by walking
/// the key's whole chain, so building it grew with the square of the rows per key. The head of each
/// key's chain now keeps its tail. Both tables here hold 160 000 marks, dealt to four keys or to
/// 40 000, and each join probes them 40 000 times.
/// </summary>
public sealed class AsofLongKeyTests(SharedSidecar sidecar, ITestOutputHelper output) : IClassFixture<SharedSidecar>
{
    private const int Rows = 160_000;
    private const int Points = 40_000;
    private static readonly DateTime Day = new(2026, 10, 12);

    /// <summary>
    /// Four keys of 40 000 rows cost no more than 40 000 keys of four, both when the join reads the
    /// whole table and when it seeks an index. Before, the long keys took 4,425 ms read whole and
    /// 342 ms sought, against 20 ms and 49 ms for the short ones; now they take 53 ms and 56 ms.
    /// </summary>
    [Theory]
    [InlineData("plain", false)]
    [InlineData("indexed", true)]
    public async Task A_key_with_many_rows_costs_a_probe_what_a_key_with_few_does(string table, bool sought)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();

        var longKeys = await BestAsync(engine, "long", table, sought);
        var shortKeys = await BestAsync(engine, "short", table, sought);

        output.WriteLine($"{table}: four keys of 40 000 rows took {longKeys} ms, 40 000 keys of four {shortKeys} ms");
        Assert.True(
            longKeys <= (3 * shortKeys) + 100,
            $"four keys of 40 000 rows took {longKeys} ms against {shortKeys} ms for 40 000 keys of four");
    }

    /// <summary>The fastest of three warm runs, in milliseconds, after checking the plan and the answer.</summary>
    private static async Task<long> BestAsync(ChalkEngine engine, string keys, string table, bool sought)
    {
        var prepared = await engine.PrepareAsync(
            $"SELECT COUNT(*), COUNT(m.price) FROM probes_{keys} p "
            + $"ASOF JOIN {keys}_{table} m MATCH_CONDITION m.ts <= p.ts ON m.symbol = p.symbol");
        Assert.Equal(sought, PlanWalker.Has(prepared.Plan, Rel.KindOneofCase.AsOfLookupJoin));

        var best = long.MaxValue;
        for (var run = 0; run < 4; run++)
        {
            var clock = Stopwatch.StartNew();
            await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[]);
            await foreach (var batch in execution.Batches)
            {
                Assert.Equal(1, batch.Length);
                batch.Dispose();
            }

            if (run > 0)
            {
                best = Math.Min(best, clock.ElapsedMilliseconds);
            }
        }

        return best;
    }

    private async Task<ChalkEngine> CreateEngineAsync()
    {
        // The same 160 000 times, a second apart, dealt to four keys or to 40 000, and 40 000 probes
        // between them, dealt the same way.
        Mark[] Deal(int keys) =>
            [.. Enumerable.Range(0, Rows).Select(i => new Mark($"k{i % keys}", Day.AddSeconds(i), i))];
        Point[] Probe(int keys) =>
            [.. Enumerable.Range(0, Points).Select(i => new Point($"k{i % keys}", Day.AddSeconds((4 * i) + 2.5)))];
        var longKeys = Deal(4);
        var shortKeys = Deal(Points);

        return await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "asof-long-keys",
            Sources =
            [
                new PocoSourceBuilder("mem")
                    .NamingPolicy(PocoNamingPolicy.SnakeCase)
                    .AddTable("probes_long", Probe(4))
                    .AddTable("probes_short", Probe(Points))
                    .AddTable("long_plain", longKeys)
                    .AddTable("long_indexed", longKeys, t => t.Index(m => m.Symbol, m => m.Ts))
                    .AddTable("short_plain", shortKeys)
                    .AddTable("short_indexed", shortKeys, t => t.Index(m => m.Symbol, m => m.Ts))
                    .Build(),
            ],
            Planner = sidecar.CreatePlanner(),
        });
    }

    public sealed record Point(string Symbol, DateTime Ts);

    public sealed record Mark(string Symbol, DateTime Ts, double Price);
}
