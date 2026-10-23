using System.Diagnostics;
using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Market;
using Chalk.Sample.Blotter.Trades;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>
/// The host end to end, with ChalkQL's planner, a DuckDB corpus and a running market: every page
/// it serves is checked against the oracle, which works the answer out in DuckDB alone; and what it
/// refuses, it refuses with a reason.
/// </summary>
public sealed class BlotterHostTests(ITestOutputHelper output)
{
    private static readonly FilterSpec[] Filters =
    [
        FilterSpec.None,
        new FilterSpec { Statuses = ["New", "PartiallyFilled"] }.Canonical(),
        new FilterSpec { Side = "Sell", Symbols = [.. Reference.Symbols.Take(25)] }.Canonical(),
    ];

    [Fact]
    public async Task Every_page_matches_the_oracle_through_bursts_stalls_and_turnovers()
    {
        await using var host = await StartAsync(new BlotterOptions
        {
            Trades = 40_000,
            TurnoverEvery = TimeSpan.FromSeconds(2),
            Exchange = new ExchangeOptions
            {
                Rate = 800,
                BurstEvery = TimeSpan.Zero,
                Lag = new IngestLag { Max = TimeSpan.FromSeconds(1.5), StallEvery = TimeSpan.Zero },
            },
        });
        var random = new Random(11);
        var checks = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (checks % 8 == 0)
            {
                host.Exchange.Send(2000);
            }

            if (checks % 25 == 12)
            {
                host.Exchange.StallIngestion(TimeSpan.FromSeconds(2));
            }

            var filter = Filters[random.Next(Filters.Length)];
            var order = SortSpec.Of(
            [
                .. Enumerable.Range(0, 1 + random.Next(3))
                    .Select(_ => new SortColumn(Column.All[random.Next(Column.All.Count)], random.Next(2) == 0))
                    .DistinctBy(k => k.Column),
            ]);
            var scope = await host.AcquireAsync(filter, CancellationToken.None);
            try
            {
                var state = scope.State;
                var total = await host.Pages.TotalAsync(state, CancellationToken.None);
                var start = random.Next(3) == 0 ? 0 : (long)(random.NextDouble() * Math.Max(0, total - 40));
                var page = await host.Pages.GetAsync(state, order, start, 40, CancellationToken.None);
                var (expected, expectedTotal) = Oracle.Page(host.Store, host.Exchange.Feed, state, order, start, 40);

                Assert.True(
                    Oracle.Compare(page, expected, expectedTotal) is null,
                    $"generation {state.Generation!.Number}, {filter.Name}, {order} at {start}: {Oracle.Compare(page, expected, expectedTotal)}");
                checks++;
            }
            finally
            {
                await host.ReleaseAsync(scope);
            }
        }

        output.WriteLine($"{checks} pages, {host.Status.Turnovers} turnovers, tail {host.Status.TailTrades} trades");
        Assert.True(checks >= 20, $"only {checks} pages were checked");
        Assert.True(host.Status.Turnovers >= 2, $"only {host.Status.Turnovers} generations were taken over");
    }

    [Fact]
    public async Task A_row_keeps_its_place_by_key_while_trades_arrive_above_it()
    {
        await using var host = await StartAsync(new BlotterOptions { Trades = 20_000, Exchange = Quiet() });
        var scope = await host.AcquireAsync(FilterSpec.None, CancellationToken.None);
        var order = SortSpec.Default;
        var state = scope.State;
        var anchor = (await host.Pages.GetAsync(state, order, 100, 1, CancellationToken.None)).Rows[0];
        var before = await host.Pages.RankAsync(state, order, anchor, CancellationToken.None);

        host.Exchange.Send(300);
        await UntilAsync(() => scope.State.Live.Count(t => t.Version == 1) >= 50);
        state = scope.State;
        var after = await host.Pages.RankAsync(state, order, anchor, CancellationToken.None);
        var page = await host.Pages.GetAsync(state, order, after, 1, CancellationToken.None);

        Assert.Equal(100, before);
        Assert.True(after > before, "new trades sort above the anchor");
        Assert.Equal(anchor.TradeId, page.Rows[0].TradeId);
        await host.ReleaseAsync(scope);
    }

    [Fact]
    public async Task A_scope_that_would_not_fit_the_budget_is_refused_saying_so()
    {
        await using var host = await StartAsync(new BlotterOptions { Trades = 20_000, BudgetBytes = 1 << 20, Exchange = Quiet() });
        var scopes = new List<LiveScope>();
        var refused = await Assert.ThrowsAsync<BlotterRefusedException>(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                scopes.Add(await host.AcquireAsync(new FilterSpec { Clients = [Reference.Clients[i]] }.Canonical(), CancellationToken.None));
            }
        });

        Assert.StartsWith("no live scope for this filter", refused.Message, StringComparison.Ordinal);
        Assert.Contains("of the 1.0 MB budget", refused.Message, StringComparison.Ordinal);
        Assert.True(scopes.Count >= 2, $"refused after {scopes.Count} scopes");
    }

    [Fact]
    public async Task A_database_too_far_behind_for_the_budget_stops_serving_then_serves_again()
    {
        await using var host = await StartAsync(new BlotterOptions
        {
            Trades = 20_000,
            BudgetBytes = 2 << 20,
            Exchange = Quiet() with { Rate = 1500 },
        });
        var scope = await host.AcquireAsync(FilterSpec.None, CancellationToken.None);

        host.Exchange.StallIngestion(TimeSpan.FromSeconds(8));
        await UntilAsync(() => host.State == BlotterState.Refusing, TimeSpan.FromSeconds(20));

        Assert.Contains("behind the feed", host.Refusal, StringComparison.Ordinal);
        Assert.Contains("more than the 2.0 MB budget", host.Refusal, StringComparison.Ordinal);
        await Assert.ThrowsAsync<BlotterRefusedException>(() => host.AcquireAsync(FilterSpec.None, CancellationToken.None));

        await UntilAsync(() => host.State == BlotterState.Live, TimeSpan.FromSeconds(30));
        var again = await host.AcquireAsync(FilterSpec.None, CancellationToken.None);
        var state = again.State;
        var page = await host.Pages.GetAsync(state, SortSpec.Default, 0, 20, CancellationToken.None);
        var (expected, total) = Oracle.Page(host.Store, host.Exchange.Feed, state, SortSpec.Default, 0, 20);
        Assert.Null(Oracle.Compare(page, expected, total));
    }

    [Fact]
    public async Task A_trades_history_spans_the_database_and_the_tail_in_one_statement()
    {
        await using var host = await StartAsync(new BlotterOptions { Trades = 20_000, Exchange = Quiet() });
        var scope = await host.AcquireAsync(FilterSpec.None, CancellationToken.None);
        host.Exchange.StallIngestion(TimeSpan.FromSeconds(30));

        // Fills for open trades, which the database will not write for a while.
        var sent = host.Exchange.Send(200).Where(v => v.Version > 1).ToList();
        Assert.NotEmpty(sent);
        var filled = sent[^1];
        await UntilAsync(() => scope.State.Tail.TryGetValue(filled.TradeId, out var t) && t.Version >= filled.Version);

        var history = await host.Queries.HistoryAsync(filled.TradeId, CancellationToken.None);
        var tailOnly = await host.Queries.TailOnlyAsync(filled.TradeId, CancellationToken.None);

        Assert.Equal(Enumerable.Range(1, history[0].Version).Reverse(), history.Select(v => v.Version));
        Assert.Contains(filled.Version, tailOnly);
        Assert.DoesNotContain(1, tailOnly);
        await host.ReleaseAsync(scope);
    }

    private static ExchangeOptions Quiet() => new()
    {
        Rate = 50,
        BurstEvery = TimeSpan.Zero,
        Lag = new IngestLag { Max = TimeSpan.FromMilliseconds(500), StallEvery = TimeSpan.Zero },
    };

    private static async Task<BlotterHost> StartAsync(BlotterOptions options)
    {
        var host = new BlotterHost(options, NullLogger<BlotterHost>.Instance);
        await host.StartAsync(CancellationToken.None);
        return host;
    }

    private static async Task UntilAsync(Func<bool> condition, TimeSpan? within = null)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > (within ?? TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("the condition never held");
            }

            await Task.Delay(50);
        }
    }
}
