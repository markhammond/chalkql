using System.Diagnostics;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A pivot grid's report over a trading desk, kept by incremental maintenance (D343,
/// <c>docs/design/68-incremental-maintenance.md</c> §0, §9): the clients' positions and the house's
/// hedges as of the day's close, each marked and converted by as-of joins, two legs under a
/// <c>ROLLUP</c> of each axis. Market ticks append prices and rates and move the clock, trades append
/// positions, and a compaction rewrites a log; after every tick the maintained report is the
/// statement's, and the time of each is reported.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class MaintainedPivotTests(SharedSidecar sidecar, ITestOutputHelper output)
{
    private sealed record Client(long ClientId, string Name);

    private sealed record Account(long AccountId, long ClientId, string Asset, string Quote);

    private sealed record Position(long AccountId, string Channel, decimal Amount, double AverageUsd, DateTime UpdateTime, double Realized);

    private sealed record Hedge(DateTime UpdateTime, string Book, string Venue, string Asset, string Counter, decimal Amount, double Average, decimal Realized);

    private sealed record Mark(DateTime Ts, string Asset, double UsdPrice);

    private sealed record PerpMark(DateTime Ts, string Asset, string Counter, double Price);

    private sealed record Rate(DateTime Ts, string Symbol, double UsdRate);

    private static readonly string[] Assets = ["ADA", "BTC", "ETH", "SOL", "XRP"];
    private static readonly string[] Counters = ["EUR", "USD", "USDC", "USDT"];
    private static readonly string[] Channels = ["API", "FIX", "GUI"];
    private static readonly string[] Books = ["Basis", "Delta", "Macro"];
    private static readonly string[] Venues = ["East", "North", "West"];

    private const string Report =
        """
        WITH days AS (
          SELECT '2026-10-13' AS trading_day,
                 TIMESTAMP '2026-10-12 21:00:00' AS opens,
                 LEAST(TIMESTAMP '2026-10-13 21:00:00', CAST(@now AS TIMESTAMP(9))) AS closes
          FROM (VALUES (1)) AS x (k)
        ),
        p AS (
          SELECT d.trading_day, CAST('client' AS VARCHAR(16)) AS leg, k.asset, k.counter,
                 -c.amount AS qty,
                 -CAST(c.amount * m.usd_price AS DOUBLE) AS notional,
                 -(c.realized - COALESCE(o.realized, 0)) * q.usd_rate AS realized,
                 -CAST(c.amount AS DOUBLE) * (m.usd_price - c.average_usd) AS unrealized
          FROM days d
          CROSS JOIN (SELECT p.account_id, p.channel, a.asset, a.quote AS counter
                      FROM (SELECT account_id, channel FROM positions GROUP BY account_id, channel) p
                      JOIN accounts a ON a.account_id = p.account_id JOIN clients x ON x.client_id = a.client_id) k
          ASOF JOIN positions c MATCH_CONDITION c.update_time <= d.closes ON c.account_id = k.account_id AND c.channel = k.channel
          LEFT ASOF JOIN positions o MATCH_CONDITION o.update_time < d.opens ON o.account_id = k.account_id AND o.channel = k.channel
          ASOF JOIN marks m MATCH_CONDITION m.ts <= d.closes ON m.asset = k.asset
          ASOF JOIN rates q MATCH_CONDITION q.ts <= d.closes ON q.symbol = k.counter
          WHERE NOT (c.amount = 0 AND c.realized = 0)
          UNION ALL
          SELECT d.trading_day, CAST('hedge' AS VARCHAR(16)) AS leg, k.asset, k.counter,
                 h.amount AS qty,
                 CAST(h.amount AS DOUBLE) * pm.price * q.usd_rate AS notional,
                 CAST(h.realized - COALESCE(o.realized, 0) AS DOUBLE) * q.usd_rate AS realized,
                 CAST(h.amount AS DOUBLE) * (pm.price - h.average) * q.usd_rate AS unrealized
          FROM days d
          CROSS JOIN (SELECT book, venue, asset, counter FROM hedges GROUP BY book, venue, asset, counter) k
          ASOF JOIN hedges h MATCH_CONDITION h.update_time <= d.closes
            ON h.book = k.book AND h.venue = k.venue AND h.asset = k.asset AND h.counter = k.counter
          LEFT ASOF JOIN hedges o MATCH_CONDITION o.update_time < d.opens
            ON o.book = k.book AND o.venue = k.venue AND o.asset = k.asset AND o.counter = k.counter
          ASOF JOIN perp_marks pm MATCH_CONDITION pm.ts <= d.closes ON pm.asset = k.asset AND pm.counter = k.counter
          ASOF JOIN rates q MATCH_CONDITION q.ts <= d.closes ON q.symbol = k.counter
          WHERE NOT (h.amount = 0 AND h.realized = 0)
        )
        SELECT CASE WHEN GROUPING(asset) = 1 THEN '' ELSE asset END AS row_key,
               CASE WHEN GROUPING(leg) = 1 THEN '' ELSE leg END AS column_key,
               CASE WHEN MIN(asset) = MAX(asset) THEN SUM(qty) END AS quantity,
               SUM(notional) AS notional,
               SUM(realized) AS realized,
               SUM(unrealized) AS unrealized,
               COUNT(*) AS positions
        FROM p
        GROUP BY ROLLUP(asset), ROLLUP(leg)
        """;

    [Fact]
    public async Task Every_tick_the_maintained_report_is_the_statement()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var random = new Random(5);
        var now = new DateTime(2026, 10, 13, 15, 0, 0);
        var desk = Desk.Make(random, now, hours: 72);
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("clients", desk.Clients, t => t.OrderedBy(x => x.ClientId))
            .AddTable("accounts", desk.Accounts, t => t.OrderedBy(x => x.AccountId))
            .AddTable("positions", desk.Positions, t => t.OrderedBy(x => x.UpdateTime))
            .AddTable("hedges", desk.Hedges, t => t.OrderedBy(x => x.UpdateTime))
            .AddTable("marks", desk.Marks, t => t.OrderedBy(x => x.Ts).ThenBy(x => x.Asset))
            .AddTable("perp_marks", desk.PerpMarks, t => t.OrderedBy(x => x.Ts).ThenBy(x => x.Asset).ThenBy(x => x.Counter))
            .AddTable("rates", desk.Rates, t => t.OrderedBy(x => x.Ts).ThenBy(x => x.Symbol).Index(x => x.Symbol, x => x.Ts))
            .Build();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
            Clock = "now",
        }, Ct);
        await engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(now, TimeSpan.Zero)), Ct);
        var prepared = await engine.PrepareAsync(Report, ct: Ct);
        await using var maintained = await prepared.MaintainAsync([now], 0, [0, 1], Ct);
        var held = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        var watch = Stopwatch.StartNew();
        Apply(await maintained.UpdateAsync([now], new DateTimeOffset(now, TimeSpan.Zero), whole: false, Ct), held);
        output.WriteLine($"first update: {watch.Elapsed.TotalMilliseconds:F1} ms; holds {maintained.StateBytes / 1024} KB");
        Compare(held, await StatementAsync(engine, prepared, now), 0);

        var prices = Assets.ToDictionary(a => a, a => desk.Marks.Last(m => m.Asset == a).UsdPrice);
        var updates = new List<double>();
        var recomputes = new List<double>();
        for (var tick = 1; tick <= 120; tick++)
        {
            now = now.AddMilliseconds(250);
            var at = now;
            var marks = new List<Mark>();
            var perps = new List<PerpMark>();
            foreach (var asset in Assets)
            {
                prices[asset] *= 1 + ((random.NextDouble() - 0.5) * 0.001);
                marks.Add(new Mark(at, asset, prices[asset]));
                perps.AddRange(Counters.Select(c => new PerpMark(at, asset, c, prices[asset] * (1 + (random.NextDouble() * 0.001)))));
            }

            var rates = Counters.Select(c => new Rate(at, c, c.StartsWith("USD", StringComparison.Ordinal) ? 1 : 0.9 + (random.NextDouble() * 0.2))).ToList();
            var positions = new List<Position>();
            if (random.Next(3) == 0)
            {
                var (account, channel) = desk.PositionKeys[random.Next(desk.PositionKeys.Count)];
                positions.Add(new Position(account, channel, random.Next(-40, 40), 100 * random.NextDouble(), at, random.NextDouble() * 500));
            }

            desk.Positions.AddRange(positions);
            var compact = tick % 60 == 45;
            await engine.RefreshAsync(r =>
            {
                r.AdvanceClock(new DateTimeOffset(at, TimeSpan.Zero))
                    .Append(source, "marks", marks).Append(source, "perp_marks", perps).Append(source, "rates", rates);
                if (compact)
                {
                    // A compaction: a third of the older rows gone, the log replaced whole.
                    desk.Positions = [.. desk.Positions.Where((x, i) => i % 3 != 0 || x.UpdateTime > at.AddHours(-24))];
                    r.Replace(source, "positions", desk.Positions);
                }
                else if (positions.Count > 0)
                {
                    r.Append(source, "positions", positions);
                }
            }, Ct);

            watch.Restart();
            var update = await maintained.UpdateAsync([at], new DateTimeOffset(at, TimeSpan.Zero), whole: false, Ct);
            updates.Add(watch.Elapsed.TotalMilliseconds);
            Apply(update, held);
            watch.Restart();
            var expected = await StatementAsync(engine, prepared, at);
            recomputes.Add(watch.Elapsed.TotalMilliseconds);
            Compare(held, expected, tick);
        }

        // Once warm: the first sixty ticks run code the JIT has yet to tier up.
        var maintainedWarm = updates.Skip(60).Order().ToList();
        var recomputedWarm = recomputes.Skip(60).Order().ToList();
        output.WriteLine(
            $"ticks 61-120: maintained update median {maintainedWarm[maintainedWarm.Count / 2]:F2} ms "
            + $"(p90 {maintainedWarm[maintainedWarm.Count * 9 / 10]:F2}), recompute median "
            + $"{recomputedWarm[recomputedWarm.Count / 2]:F2} ms; holds {maintained.StateBytes / 1024} KB");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Apply(MaintainedUpdate update, Dictionary<string, object?[]> held)
    {
        if (update.Whole)
        {
            held.Clear();
        }

        foreach (var row in Rows(update.Before))
        {
            held.Remove($"{row[0]}|{row[1]}");
        }

        foreach (var row in Rows(update.Now))
        {
            held[$"{row[0]}|{row[1]}"] = row;
        }
    }

    private static IEnumerable<object?[]> Rows(Apache.Arrow.RecordBatch batch)
    {
        for (var i = 0; i < batch.Length; i++)
        {
            yield return [.. Enumerable.Range(0, batch.ColumnCount).Select(c => Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(c), i))];
        }
    }

    private static async Task<Dictionary<string, object?[]>> StatementAsync(ChalkEngine engine, PreparedQuery prepared, DateTime now)
    {
        var rows = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        await using var execution = await engine.ExecuteAsync(prepared, [now], ct: Ct);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                foreach (var row in Rows(batch))
                {
                    rows.Add($"{row[0]}|{row[1]}", row);
                }
            }
        }

        return rows;
    }

    /// <summary>The same keys; integers and decimals equal; sums of floating point within rounding.</summary>
    private static void Compare(Dictionary<string, object?[]> held, Dictionary<string, object?[]> expected, int tick)
    {
        Assert.True(
            expected.Count == held.Count,
            $"tick {tick}: {held.Count} cells held, {expected.Count} expected");
        foreach (var (key, row) in expected)
        {
            Assert.True(held.TryGetValue(key, out var mine), $"tick {tick}: cell {key} missing");
            for (var c = 0; c < row.Length; c++)
            {
                if (row[c] is double a && mine![c] is double b)
                {
                    Assert.True(Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(a)), $"tick {tick}: {key} column {c}: {b:R} held, {a:R} expected");
                }
                else
                {
                    Assert.True(Equals(row[c], mine![c]), $"tick {tick}: {key} column {c}: {mine[c]} held, {row[c]} expected");
                }
            }
        }
    }

    /// <summary>The desk's rows: hourly history before the clock, as a compacted log keeps it.</summary>
    private sealed class Desk
    {
        public List<Client> Clients { get; } = [];

        public List<Account> Accounts { get; } = [];

        public List<Position> Positions { get; set; } = [];

        public List<Hedge> Hedges { get; private set; } = [];

        public List<Mark> Marks { get; private set; } = [];

        public List<PerpMark> PerpMarks { get; private set; } = [];

        public List<Rate> Rates { get; private set; } = [];

        public List<(long Account, string Channel)> PositionKeys { get; } = [];

        public static Desk Make(Random random, DateTime now, int hours)
        {
            var desk = new Desk();
            var account = 1L;
            for (var c = 1; c <= 8; c++)
            {
                desk.Clients.Add(new Client(c, $"Client {c}"));
                foreach (var asset in Assets.Where((_, i) => (i + c) % 3 != 0))
                {
                    desk.Accounts.Add(new Account(account, c, asset, "USD"));
                    foreach (var channel in Channels.Take(1 + (c % 3)))
                    {
                        desk.PositionKeys.Add((account, channel));
                    }

                    account++;
                }
            }

            var hedgeKeys = (from book in Books from venue in Venues from asset in Assets from counter in Counters.Take(2)
                             select (book, venue, asset, counter)).Where((_, i) => i % 2 == 0).ToList();
            var start = now.AddHours(-hours);
            var prices = Assets.ToDictionary(a => a, _ => 10 + (random.NextDouble() * 1000));
            var hedges = new List<Hedge>();
            for (var h = 0; h <= hours; h++)
            {
                var t = start.AddHours(h);
                foreach (var asset in Assets)
                {
                    prices[asset] *= 1 + ((random.NextDouble() - 0.5) * 0.01);
                    desk.Marks.Add(new Mark(t, asset, prices[asset]));
                    desk.PerpMarks.AddRange(Counters.Select(c => new PerpMark(t, asset, c, prices[asset] * (1 + (random.NextDouble() * 0.001)))));
                }

                desk.Rates.AddRange(Counters.Select(c => new Rate(t, c, c.StartsWith("USD", StringComparison.Ordinal) ? 1 : 0.9 + (random.NextDouble() * 0.2))));
                foreach (var (key, channel) in desk.PositionKeys.Where(_ => random.NextDouble() < 0.3))
                {
                    desk.Positions.Add(new Position(key, channel, random.Next(-40, 40), 100 * random.NextDouble(), t.AddMinutes(random.Next(60)), random.NextDouble() * 500));
                }

                foreach (var (book, venue, asset, counter) in hedgeKeys.Where(_ => random.NextDouble() < 0.3))
                {
                    hedges.Add(new Hedge(t.AddMinutes(random.Next(60)), book, venue, asset, counter, random.Next(-40, 40), 100 * random.NextDouble(), Math.Round((decimal)(random.NextDouble() * 500), 6)));
                }
            }

            desk.Positions = [.. desk.Positions.Where(p => p.UpdateTime <= now).OrderBy(p => p.UpdateTime)];
            desk.Hedges = [.. hedges.Where(x => x.UpdateTime <= now).OrderBy(x => x.UpdateTime)];
            desk.Marks = [.. desk.Marks.OrderBy(x => x.Ts).ThenBy(x => x.Asset, StringComparer.Ordinal)];
            desk.PerpMarks = [.. desk.PerpMarks.OrderBy(x => x.Ts).ThenBy(x => x.Asset, StringComparer.Ordinal).ThenBy(x => x.Counter, StringComparer.Ordinal)];
            desk.Rates = [.. desk.Rates.OrderBy(x => x.Ts).ThenBy(x => x.Symbol, StringComparer.Ordinal)];
            return desk;
        }
    }
}
