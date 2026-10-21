using Akade.IndexedSet;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources.Poco;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// D338, design 63 §4 — an ASOF join seeks an Akade range index on its keys and then its time,
/// reading forwards. An Akade index reads backwards only from its top (D283), not from a bound, so a
/// seek that must read backwards keeps the plain join; both give the same rows.
/// </summary>
public sealed class AkadeAsofSeekTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0);

    /// <summary>One trade every ten minutes, back from <see cref="Now"/>, on two alternating shards.</summary>
    private sealed record Trade(int Id, int Shard, DateTime Ts);

    private sealed record Probe(int Id, int Shard, DateTime AsOf);

    private static readonly Probe[] Probes =
    [
        new(1, 0, Now.AddMinutes(-25)),
        new(2, 1, Now.AddMinutes(-25)),
        new(3, 0, Now.AddHours(1)),
    ];

    /// <summary>The first trade at or after each probe, by the shard's tuple index (shard, ts).</summary>
    [Fact]
    public async Task A_forward_seek_reads_the_index()
    {
        await using var fixture = await Fixture.CreateAsync();

        var (rows, plan) = await fixture.RunAsync(
            "SELECT p.id, t.id FROM lab.probes p ASOF JOIN trades t MATCH_CONDITION p.as_of <= t.ts "
            + "ON p.shard = t.shard ORDER BY p.id");

        Assert.True(PlanWalker.Has(plan, Rel.KindOneofCase.AsOfLookupJoin), "the forward seek reads the index");
        Assert.Equal(["1|2", "2|1"], rows);
    }

    /// <summary>The last trade at or before each probe, by reading the table.</summary>
    [Fact]
    public async Task A_backward_seek_keeps_the_plain_join()
    {
        await using var fixture = await Fixture.CreateAsync();

        var (rows, plan) = await fixture.RunAsync(
            "SELECT p.id, t.id FROM lab.probes p ASOF JOIN trades t MATCH_CONDITION p.as_of >= t.ts "
            + "ON p.shard = t.shard ORDER BY p.id");

        Assert.False(PlanWalker.Has(plan, Rel.KindOneofCase.AsOfLookupJoin), "Akade reads backwards only from its top");
        Assert.Equal(["1|4", "2|3", "3|0"], rows);
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
                    .Select(i => new Trade(i, i & 1, Now.AddMinutes(-10 * i)))
                    .ToIndexedSet()
                    .WithRangeIndex(x => (x.Shard, x.Ts))
                    .Build();

                var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
                {
                    ContextId = "akade-asof-seek",
                    Sources =
                    [
                        AkadeSource.From("trades", trades)
                            .TableName("trades")
                            .NamingPolicy(PocoNamingPolicy.SnakeCase)
                            .Build(),
                        new PocoSourceBuilder("probes", "lab")
                            .NamingPolicy(PocoNamingPolicy.SnakeCase)
                            .AddTable("probes", Probes)
                            .Build(),
                    ],
                    Planner = sidecar.CreatePlanner(),
                });
                return new Fixture(sidecar, engine);
            }
            catch
            {
                await sidecar.DisposeAsync();
                throw;
            }
        }

        public async Task<(List<string> Rows, Plan Plan)> RunAsync(string sql)
        {
            var query = await _engine.PrepareAsync(sql);
            await using var execution = await _engine.ExecuteAsync(query, []);
            var rows = new List<string>();
            await foreach (var batch in execution.Batches)
            {
                var probe = (Apache.Arrow.Int32Array)batch.Column(0);
                var trade = (Apache.Arrow.Int32Array)batch.Column(1);
                for (var i = 0; i < batch.Length; i++)
                {
                    rows.Add($"{probe.GetValue(i)}|{trade.GetValue(i)}");
                }

                batch.Dispose();
            }

            return (rows, query.Plan);
        }

        public async ValueTask DisposeAsync()
        {
            await _engine.DisposeAsync();
            await _sidecar.DisposeAsync();
        }
    }
}
