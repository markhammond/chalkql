using Akade.IndexedSet;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources.Poco;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// D337, F176 — an Akade range index over a temporal member serves a range on it. A bound arrives as
/// the column's storage units, and the adapter turns it back into the <see cref="DateTime"/> or
/// <see cref="DateOnly"/> the index is keyed by, in the column's own precision. Before, every such
/// lookup failed: the index could not take a bound of type <see cref="long"/>.
/// </summary>
public sealed class AkadeTemporalIndexTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0);

    /// <summary>One trade every ten minutes, back from <see cref="Now"/>, on two alternating shards.</summary>
    private sealed record Trade(
        int Id,
        int Shard,
        DateTime Ts,
        [property: ChalkColumn(Precision = 6)] DateTime Booked,
        DateOnly TradeDate);

    [Theory]
    [InlineData("ts >= TIMESTAMP '2026-10-12 11:35:00' AND ts <= TIMESTAMP '2026-10-12 12:00:00'")]
    [InlineData("ts >= CURRENT_TIMESTAMP - INTERVAL '25' MINUTE AND ts <= CURRENT_TIMESTAMP")]
    [InlineData("booked >= TIMESTAMP '2026-10-12 11:35:00' AND booked <= TIMESTAMP '2026-10-12 12:00:00'")]
    public async Task A_range_on_a_DateTime_key_reads_the_index(string range)
    {
        await using var fixture = await Fixture.CreateAsync();

        var (ids, scanned) = await fixture.RunAsync($"SELECT id FROM trades WHERE {range} ORDER BY id");

        Assert.Equal([0, 1, 2], ids);
        Assert.Equal(3, scanned);
    }

    [Fact]
    public async Task An_equality_on_a_DateOnly_key_reads_the_index()
    {
        await using var fixture = await Fixture.CreateAsync();

        var (ids, scanned) = await fixture.RunAsync("SELECT id FROM trades WHERE trade_date = DATE '2026-10-11' ORDER BY id");

        var expected = Enumerable.Range(0, 2_000)
            .Where(i => DateOnly.FromDateTime(Now.AddMinutes(-10 * i)) == new DateOnly(2026, 10, 11))
            .ToList();
        Assert.Equal(144, expected.Count);
        Assert.Equal(expected, ids);
        Assert.Equal(144, scanned);
    }

    [Fact]
    public async Task A_compound_key_with_a_DateTime_component_reads_the_index()
    {
        await using var fixture = await Fixture.CreateAsync();

        var (ids, scanned) = await fixture.RunAsync(
            "SELECT id FROM trades WHERE shard = 0 AND ts >= TIMESTAMP '2026-10-12 11:00:00' ORDER BY id");

        Assert.Equal([0, 2, 4, 6], ids);
        Assert.Equal(4, scanned);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PlannerProcess _sidecar;
        private readonly ChalkEngine _engine;

        private Fixture(PlannerProcess sidecar, ChalkEngine engine)
        {
            _sidecar = sidecar;
            _engine = engine;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var sidecar = await PlannerProcess.StartAsync();
            try
            {
                var trades = Enumerable.Range(0, 2_000)
                    .Select(i =>
                    {
                        var ts = Now.AddMinutes(-10 * i);
                        return new Trade(i, i % 2, ts, ts, DateOnly.FromDateTime(ts));
                    })
                    .ToIndexedSet()
                    .WithRangeIndex(x => x.Ts)
                    .WithRangeIndex(x => x.Booked)
                    .WithRangeIndex(x => x.TradeDate)
                    .WithRangeIndex(x => (x.Shard, x.Ts))
                    .Build();

                var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
                {
                    ContextId = "akade-temporal",
                    Sources =
                    [
                        AkadeSource.From("trades", trades)
                            .TableName("trades")
                            .NamingPolicy(PocoNamingPolicy.SnakeCase)
                            .Build(),
                    ],
                    Planner = sidecar.CreatePlanner(),
                    Execution = new ExecutionOptions { TimeProvider = new Fixed(new DateTimeOffset(Now, TimeSpan.Zero)) },
                });
                return new Fixture(sidecar, engine);
            }
            catch
            {
                await sidecar.DisposeAsync();
                throw;
            }
        }

        public async Task<(List<int> Ids, long Scanned)> RunAsync(string sql)
        {
            var query = await _engine.PrepareAsync(sql);
            Assert.True(PlanWalker.Has(query.Plan, Rel.KindOneofCase.IndexLookup), $"{sql} reads an index");

            await using var execution = await _engine.ExecuteAsync(query, []);
            var ids = new List<int>();
            await foreach (var batch in execution.Batches)
            {
                var column = (Apache.Arrow.Int32Array)batch.Column(0);
                for (var i = 0; i < batch.Length; i++)
                {
                    ids.Add(column.GetValue(i)!.Value);
                }

                batch.Dispose();
            }

            return (ids, (long)execution.Stats.RowsScanned);
        }

        public async ValueTask DisposeAsync()
        {
            await _engine.DisposeAsync();
            await _sidecar.DisposeAsync();
        }
    }

    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
