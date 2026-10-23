using System.Diagnostics;
using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Market;
using Chalk.Sample.Blotter.Trades;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>
/// The host end to end, with ChalkQL's planner, a DuckDB corpus and a running market: every page it
/// serves, of either tab, is checked against the oracle, which works the answer out in DuckDB alone;
/// fills that arrive before their orders are shown as they come; no statement runs past the time
/// limit; and what the host refuses, it refuses with a reason.
/// </summary>
public sealed class BlotterHostTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Every_page_of_either_tab_matches_the_oracle_through_bursts_stalls_and_turnovers()
    {
        await using var host = await StartAsync(new BlotterOptions
        {
            Orders = 40_000,
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

            var tab = Tab.All[checks % 2];
            var filter = Probe.RandomFilter(tab, random);
            var order = Probe.RandomSort(tab, random);
            var scope = await host.AcquireAsync(tab, filter, CancellationToken.None);
            try
            {
                var state = scope.State;
                var total = await host.Pages.TotalAsync(state, CancellationToken.None);
                var start = random.Next(3) == 0 ? 0 : (long)(random.NextDouble() * Math.Max(0, total - 40));
                var page = await host.Pages.GetAsync(state, order, start, 40, CancellationToken.None);
                var (expected, expectedTotal) = Oracle.Page(host.Store, host.Exchange, state, order, start, 40);

                var problem = Oracle.Compare(page, expected, expectedTotal);
                Assert.True(problem is null, $"{tab} generation {state.Generation!.Number}, {filter.Name}, {order} at {start}: {problem}");
                checks++;
            }
            finally
            {
                await host.ReleaseAsync(scope);
            }
        }

        output.WriteLine($"{checks} pages, {host.Status.Turnovers} turnovers, tail {host.Status.TailOrders} orders, {host.Status.TailFills} fills");
        Assert.True(checks >= 20, $"only {checks} pages were checked");
        Assert.True(host.Status.Turnovers >= 2, $"only {host.Status.Turnovers} generations were taken over");
    }

    [Fact]
    public async Task A_fill_that_arrives_before_its_order_is_shown_and_nests_once_the_order_comes()
    {
        await using var host = await StartAsync(new BlotterOptions
        {
            Orders = 20_000,
            Exchange = Quiet() with { NewShare = 1, FilledAtOnce = 1, OrderSkew = TimeSpan.FromSeconds(10) },
        });
        var orders = await host.AcquireAsync(Tab.Orders, FilterSpec.None, CancellationToken.None);
        var fills = await host.AcquireAsync(Tab.Fills, FilterSpec.None, CancellationToken.None);

        // New orders, each filled at once: the fills go out now, the orders up to ten seconds later.
        var (sent, filled) = host.Exchange.Send(50);
        var early = filled[^1];
        await UntilAsync(() => fills.State.Tail.ContainsKey(early.FillId));

        Assert.False(orders.State.Tail.ContainsKey(early.OrderId), "the order has not arrived yet");
        var page = await host.Pages.GetAsync(fills.State, Tab.Fills.DefaultSort, 0, 100, CancellationToken.None);
        Assert.Contains(page.Rows, r => r.Id == early.FillId);
        Assert.Contains(await host.Queries.AllFillsOfAsync(early.OrderId, CancellationToken.None), f => f.FillId == early.FillId);
        Assert.Empty(await host.Queries.HistoryAsync(early.OrderId, CancellationToken.None));

        // Then the order comes, and the fill that came first is among its nested fills.
        await UntilAsync(() => orders.State.Tail.TryGetValue(early.OrderId, out var o) && o.Version >= 2, TimeSpan.FromSeconds(15));
        var nested = await host.Pages.FillsOfAsync(orders.State, early.OrderId, CancellationToken.None);
        Assert.Contains(nested, f => f.FillId == early.FillId);
        Assert.Contains(sent, o => o.OrderId == early.OrderId && o.Version == 1);
        await host.ReleaseAsync(orders);
        await host.ReleaseAsync(fills);
    }

    [Fact]
    public async Task A_statement_past_the_time_limit_is_stopped_saying_so()
    {
        await using var host = await StartAsync(new BlotterOptions { Orders = 20_000, Exchange = Quiet() });
        var hasty = new SnapshotQueries(host.Engine, TimeSpan.FromTicks(1));

        var stopped = await Assert.ThrowsAsync<QueryTimeLimitException>(() =>
            hasty.CountAsync(Tab.Orders, host.Generation!, FilterSpec.None, CancellationToken.None));

        Assert.Equal("a count took longer than the 0 s time limit and was stopped", stopped.Message);
        Assert.Equal(1, hasty.TimedOut);
    }

    [Fact]
    public async Task A_row_keeps_its_place_by_key_while_rows_arrive_above_it()
    {
        await using var host = await StartAsync(new BlotterOptions { Orders = 20_000, Exchange = Quiet() });
        var scope = await host.AcquireAsync(Tab.Orders, FilterSpec.None, CancellationToken.None);
        var order = Tab.Orders.DefaultSort;
        var state = scope.State;
        var anchor = (await host.Pages.GetAsync(state, order, 100, 1, CancellationToken.None)).Rows[0];
        var before = await host.Pages.RankAsync(state, order, anchor, CancellationToken.None);

        host.Exchange.Send(300);
        await UntilAsync(() => scope.State.Live.Count(r => r.Version == 1) >= 50);
        state = scope.State;
        var after = await host.Pages.RankAsync(state, order, anchor, CancellationToken.None);
        var page = await host.Pages.GetAsync(state, order, after, 1, CancellationToken.None);

        Assert.Equal(100, before);
        Assert.True(after > before, "new orders sort above the anchor");
        Assert.Equal(anchor.Id, page.Rows[0].Id);
        await host.ReleaseAsync(scope);
    }

    [Fact]
    public async Task A_scope_that_would_not_fit_the_budget_is_refused_saying_so()
    {
        await using var host = await StartAsync(new BlotterOptions { Orders = 20_000, BudgetBytes = 1 << 20, Exchange = Quiet() });
        var scopes = new List<LiveScope>();
        var refused = await Assert.ThrowsAsync<BlotterRefusedException>(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var filter = FilterSpec.Of([new ColumnFilter { Column = Tab.Orders.Named("qty"), Min = i * 0.001 }]);
                scopes.Add(await host.AcquireAsync(Tab.Orders, filter, CancellationToken.None));
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
            Orders = 20_000,
            BudgetBytes = 2 << 20,
            // Within the budget while the database keeps up; past it once the database stalls.
            Exchange = Quiet() with { Rate = 600 },
        });
        await host.AcquireAsync(Tab.Orders, FilterSpec.None, CancellationToken.None);

        host.Exchange.StallIngestion(TimeSpan.FromSeconds(10));
        await UntilAsync(() => host.State == BlotterState.Refusing, TimeSpan.FromSeconds(20));

        Assert.Contains("behind the feed", host.Refusal, StringComparison.Ordinal);
        Assert.Contains("more than the 2.0 MB budget", host.Refusal, StringComparison.Ordinal);
        await Assert.ThrowsAsync<BlotterRefusedException>(() => host.AcquireAsync(Tab.Orders, FilterSpec.None, CancellationToken.None));

        await UntilAsync(() => host.State == BlotterState.Live, TimeSpan.FromSeconds(30));
        foreach (var tab in Tab.All)
        {
            var again = await AcquireWhenServingAsync(host, tab);
            var state = again.State;
            var page = await host.Pages.GetAsync(state, tab.DefaultSort, 0, 20, CancellationToken.None);
            var (expected, total) = Oracle.Page(host.Store, host.Exchange, state, tab.DefaultSort, 0, 20);
            Assert.Null(Oracle.Compare(page, expected, total));
        }
    }

    [Fact]
    public async Task An_orders_history_and_fills_span_the_database_and_the_tail_in_one_statement()
    {
        await using var host = await StartAsync(new BlotterOptions { Orders = 20_000, Exchange = Quiet() with { NewShare = 0, OrderSkew = TimeSpan.Zero } });
        var scope = await host.AcquireAsync(Tab.Orders, FilterSpec.None, CancellationToken.None);
        host.Exchange.StallIngestion(TimeSpan.FromSeconds(30));

        // Fills for open orders, which the database will not write for a while.
        var (_, filled) = host.Exchange.Send(200);
        Assert.NotEmpty(filled);
        var fill = filled[^1];
        await UntilAsync(() => scope.State.Tail.TryGetValue(fill.OrderId, out var o) && o.EventTime >= fill.EventTime);

        var history = await host.Queries.HistoryAsync(fill.OrderId, CancellationToken.None);
        var tailOnly = await host.Queries.TailOnlyAsync(fill.OrderId, CancellationToken.None);
        var fills = await host.Queries.AllFillsOfAsync(fill.OrderId, CancellationToken.None);

        Assert.Equal(Enumerable.Range(1, history[0].Version).Reverse(), history.Select(v => v.Version));
        Assert.DoesNotContain(1, tailOnly);
        Assert.Contains(history[0].Version, tailOnly);
        Assert.Contains(fills, f => f.FillId == fill.FillId);
        await host.ReleaseAsync(scope);
    }

    /// <summary>A scope, once the host serves again: a page asks again after a refusal, as this does.</summary>
    private static async Task<LiveScope> AcquireWhenServingAsync(BlotterHost host, Tab tab)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return await host.AcquireAsync(tab, FilterSpec.None, CancellationToken.None);
            }
            catch (BlotterRefusedException) when (clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(250);
            }
        }
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
