using System.Diagnostics;
using System.Globalization;
using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Fixtures;
using Chalk.Sample.Blotter.Domain;
using Chalk.Sample.Blotter.Grid;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>
/// The host end to end, with ChalkQL's planner, a PostgreSQL corpus and a running market. Pages read
/// while the market is held still match the oracle, which works the answer out in PostgreSQL alone,
/// row for row, whether the database is behind the streams or caught up, and whether a trigger or a
/// lagging job keeps <c>orders_current</c>; pages read while it runs keep their invariants. Fills that
/// arrive before their orders are shown as they come; the gap fill brings in what the subscription
/// missed; no statement runs past the time limit; and what the host refuses, it refuses with a reason.
/// </summary>
/// <remarks>
/// Each test starts a PostgreSQL server of its own from an installation on the machine, or uses the
/// one <c>CHALK_TEST_POSTGRES</c> names; with neither, the tests are skipped.
/// </remarks>
public sealed class BlotterHostTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(CurrentSync.Trigger)]
    [InlineData(CurrentSync.Lagging)]
    public async Task Pages_held_still_match_the_oracle_row_for_row_behind_and_caught_up(CurrentSync sync)
    {
        await using var host = await StartAsync(Roomy() with { Orders = 40_000, Current = sync, Exchange = Busy() });
        var random = new Random(11);
        var (checks, wrong) = (0, 0);
        for (var round = 0; round < 3; round++)
        {
            await Probe.UnderLoadAsync(host, random, TimeSpan.FromSeconds(1));
            var (c, w) = await Probe.QuietAsync(host, random, 6, output.WriteLine);
            (checks, wrong) = (checks + c, wrong + w);
        }

        Assert.Equal(0, wrong);
        Assert.True(checks >= 20, $"only {checks} pages were checked");
        Assert.True(host.Status.Evicted > 0, "the tail never emptied into the database");
    }

    [Fact]
    public async Task Pages_read_while_the_market_runs_keep_their_rows_in_order_once_each_and_filtered()
    {
        await using var host = await StartAsync(Roomy() with { Orders = 40_000, Exchange = Busy() });

        var (checks, wrong) = await Probe.UnderLoadAsync(host, new Random(13), TimeSpan.FromSeconds(10));

        Assert.Equal(0, wrong);
        Assert.True(checks >= 20, $"only {checks} pages were checked");
    }

    [Fact]
    public async Task The_gap_fill_takes_versions_sent_before_the_subscription_that_orders_current_does_not_show()
    {
        // orders_current runs five seconds behind order_versions, and the host subscribes a few
        // seconds after the market starts: what the market sent in between, the stream never sends the
        // host, and orders_current does not show yet. Only order_versions has it.
        await using var host = await StartAsync(new BlotterOptions
        {
            Orders = 20_000,
            Current = CurrentSync.Lagging,
            Exchange = Quiet() with
            {
                Rate = 200,
                CurrentLag = new IngestLag { Min = TimeSpan.FromSeconds(5), Max = TimeSpan.FromSeconds(6), StallEvery = TimeSpan.Zero },
            },
        });

        Assert.True(host.Status.GapFilled > 0, "the gap fill found nothing orders_current lacked");
        var (checks, wrong) = await Probe.QuietAsync(host, new Random(17), 8, output.WriteLine);
        Assert.Equal(0, wrong);
        Assert.True(checks > 0);
    }

    [Fact]
    public async Task A_fill_that_arrives_before_its_order_is_shown_and_nests_once_the_order_comes()
    {
        await using var host = await StartAsync(new BlotterOptions
        {
            Orders = 20_000,
            Exchange = Quiet() with { NewShare = 1, FilledAtOnce = 1, OrderSkew = TimeSpan.FromSeconds(10) },
        });
        var orders = await host.AcquireAsync(Tabs.Orders, FilterSpec.None, CancellationToken.None);
        var fills = await host.AcquireAsync(Tabs.Fills, FilterSpec.None, CancellationToken.None);

        // New orders, each filled at once: the fills go out now, the orders up to ten seconds later.
        var (sent, filled) = host.Exchange.Send(50);
        var early = filled[^1];
        await UntilAsync(() => fills.State.Tail.ContainsKey(early.FillId));

        Assert.False(orders.State.Tail.ContainsKey(early.OrderId), "the order has not arrived yet");
        var page = await host.Pages.View(Tabs.Fills.DefaultSort).GetAsync(fills.State, 0, 100, CancellationToken.None);
        Assert.Contains(page.Rows, r => r.Id == early.FillId);
        Assert.Contains(await host.Queries.AllFillsOfAsync(early.OrderId, CancellationToken.None), f => f.FillId == early.FillId);
        Assert.Empty(await host.Queries.HistoryAsync(early.OrderId, CancellationToken.None));

        // Then the order comes, and the fill that came first is among its nested fills.
        await UntilAsync(
            async () => (await host.Queries.HistoryAsync(early.OrderId, CancellationToken.None)).Any(v => v.Version >= 2),
            TimeSpan.FromSeconds(15));
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
        var hasty = new SourceQueries(host.Engine, TimeSpan.FromTicks(1));

        var stopped = await Assert.ThrowsAsync<QueryTimeLimitException>(() =>
            hasty.CountAsync(Tabs.Orders, FilterSpec.None, CancellationToken.None));

        Assert.Equal("a count took longer than the 0 s time limit and was stopped", stopped.Message);
        Assert.Equal(1, hasty.TimedOut);
    }

    [Fact]
    public async Task A_held_page_keeps_its_rows_in_place_while_rows_arrive_above_them()
    {
        await using var host = await StartAsync(new BlotterOptions { Orders = 20_000, Exchange = Quiet() with { NewShare = 1 } });
        var scope = await host.AcquireAsync(Tabs.Orders, FilterSpec.None, CancellationToken.None);
        var order = Tabs.Orders.DefaultSort;
        var view = host.Pages.View(order);
        var held = (await view.GetAsync(scope.State, 100, 1, CancellationToken.None)).Rows[0];
        view.Held = true;

        host.Exchange.Send(300);
        await UntilAsync(() => scope.State.Live.Count(r => r.Version == 1) >= 50);
        var state = scope.State;
        var again = await view.GetAsync(state, 100, 1, CancellationToken.None);
        var rank = await host.Pages.RankAsync(state, order, held, CancellationToken.None);

        Assert.Equal(held.Id, again.Rows[0].Id);
        Assert.True(rank >= 150, $"new orders sort above the held row, which ranks {rank}");

        // Room made for the rows that arrived: the held row moves down by as many, and the top of the
        // view, read backwards from it, is the newest order.
        await QuiesceAsync(host);
        state = scope.State;
        var first = view.First!.Value;
        var arrived = await host.Pages.RankAsync(state, order, first.Row, CancellationToken.None) - first.At;
        view.Shift(arrived);
        var top = await view.GetAsync(state, first.At + arrived, 1, CancellationToken.None);
        var newest = await view.GetAsync(state, 0, 1, CancellationToken.None);
        Assert.Equal(first.Row.Id, top.Rows[0].Id);
        Assert.Equal((await host.Pages.View(order).GetAsync(state, 0, 1, CancellationToken.None)).Rows[0].Id, newest.Rows[0].Id);
        Assert.Equal(0, newest.Start);

        // The row held further down may have moved by an order the stream sent late, which landed
        // between it and the top; it is never lost, and never above.
        Assert.Contains(held.Id, (await view.GetAsync(state, 100 + arrived - 40, 80, CancellationToken.None)).Rows.Select(r => r.Id));
        view.Held = false;
        var live = await view.GetAsync(state, 100, 1, CancellationToken.None);
        Assert.NotEqual(held.Id, live.Rows[0].Id);
        await host.ReleaseAsync(scope);
    }

    [Fact]
    public async Task A_page_a_kept_window_covers_is_answered_from_it_and_reads_as_the_database_would()
    {
        await using var host = await StartAsync(new BlotterOptions { Orders = 20_000, Exchange = Quiet() });
        var scope = await host.AcquireAsync(Tabs.Orders, FilterSpec.None, CancellationToken.None);
        host.Exchange.Pause();
        await UntilAsync(() => host.Exchange.Quiet);
        await host.CaughtUpAsync(CancellationToken.None);
        var order = Tabs.Orders.DefaultSort;
        var view = host.Pages.View(order);

        var first = await view.GetAsync(scope.State, 0, 60, CancellationToken.None);
        var hits = host.Pages.WindowHits;
        var next = await view.GetAsync(scope.State, 40, 60, CancellationToken.None);

        // The first read kept a window larger than its page, and the scroll after it was answered from it.
        Assert.True(host.Pages.WindowHits > hits, "the second page went to the database");
        Assert.Equal(first.Rows.Skip(40).Select(r => r.Id), next.Rows.Take(20).Select(r => r.Id));
        await UntilAsync(() => host.Pages.ReadAheads > 0);

        // Once the window has gone, the same page read afresh is the same rows.
        await Task.Delay(host.Pages.WindowFor + TimeSpan.FromMilliseconds(200));
        var fresh = await host.Pages.View(order).GetAsync(scope.State, 40, 60, CancellationToken.None);
        Assert.Equal(next.Rows.Select(r => r.Id), fresh.Rows.Select(r => r.Id));
        await host.ReleaseAsync(scope);
    }

    [Fact]
    public async Task Rows_let_in_above_a_held_page_are_those_just_above_it_not_the_newest()
    {
        await using var host = await StartAsync(new BlotterOptions { Orders = 20_000, Exchange = Quiet() with { NewShare = 1 } });
        var scope = await host.AcquireAsync(Tabs.Orders, FilterSpec.None, CancellationToken.None);
        var order = Tabs.Orders.DefaultSort;
        var view = host.Pages.View(order);
        var held = (await view.GetAsync(scope.State, 0, 40, CancellationToken.None)).Rows[0];
        view.Held = true;

        host.Exchange.Send(300);
        await UntilAsync(() => scope.State.Live.Count(r => r.Version == 1) >= 100);
        await QuiesceAsync(host);
        var state = scope.State;

        // Room for only 30 of them: the first position known is now 30, and nothing is known below it.
        view.Shift(30);
        var above = await view.GetAsync(state, 0, 31, CancellationToken.None);

        var live = (await host.Pages.View(order).GetAsync(state, 0, 2_000, CancellationToken.None)).Rows;
        var at = Array.FindIndex(live, r => r.Id == held.Id);
        Assert.True(at >= 30, $"the held row is {at} rows from the top");
        Assert.Equal(live[(at - 30)..(at + 1)].Select(r => r.Id), above.Rows.Select(r => r.Id));
        await host.ReleaseAsync(scope);
    }

    [Fact]
    public async Task A_scope_that_would_not_fit_the_budget_is_refused_saying_so()
    {
        await using var host = await StartAsync(new BlotterOptions { Orders = 20_000, BudgetBytes = 1 << 20, FindBudget = false, Exchange = Quiet() });
        var scopes = new List<LiveScope>();
        var refused = await Assert.ThrowsAsync<BlotterRefusedException>(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var filter = FilterSpec.Of([new ColumnFilter { Column = Tabs.Orders.Named("qty"), Min = i * 0.001 }]);
                scopes.Add(await host.AcquireAsync(Tabs.Orders, filter, CancellationToken.None));
            }
        });

        Assert.StartsWith("no live scope for this filter", refused.Message, StringComparison.Ordinal);
        Assert.Contains("of the 1.0 MB budget", refused.Message, StringComparison.Ordinal);
        Assert.True(scopes.Count >= 2, $"refused after {scopes.Count} scopes");
    }

    [Fact]
    public async Task Until_a_budget_is_chosen_it_is_raised_rather_than_refused_up_to_the_last_step()
    {
        long[] steps = [128 << 10, 512 << 10, 4 << 20];
        await using var host = await StartAsync(new BlotterOptions
        {
            Orders = 20_000, BudgetBytes = steps[0], BudgetSteps = steps, Exchange = Quiet(),
        });

        // No scope fits in the first step: the first is opened in the next that holds it.
        var scopes = new List<LiveScope> { await host.AcquireAsync(Tabs.Orders, FilterSpec.None, CancellationToken.None) };
        Assert.Contains(host.Budget, steps[1..]);

        // Scope after scope raises it again, to the last step; only past that is one refused.
        var refused = await Assert.ThrowsAsync<BlotterRefusedException>(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var filter = FilterSpec.Of([new ColumnFilter { Column = Tabs.Orders.Named("qty"), Min = i * 0.001 }]);
                scopes.Add(await host.AcquireAsync(Tabs.Orders, filter, CancellationToken.None));
            }
        });
        // Refused for want of budget, either way: the next scope would not fit, or, with the market
        // running, the tail and the scopes outgrew it first and the host stopped serving.
        Assert.Contains("budget", refused.Message, StringComparison.Ordinal);
        Assert.Equal(steps[^1], host.Budget);
        Assert.True(scopes.Count > 4, $"refused after {scopes.Count} scopes");

        // Chosen, the budget stays as it is: what does not fit is refused.
        foreach (var scope in scopes)
        {
            await host.ReleaseAsync(scope);
        }

        await host.SetBudgetAsync(1 << 20);
        await Assert.ThrowsAsync<BlotterRefusedException>(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var filter = FilterSpec.Of([new ColumnFilter { Column = Tabs.Orders.Named("qty"), Min = 1 + i * 0.001 }]);
                await host.AcquireAsync(Tabs.Orders, filter, CancellationToken.None);
            }
        });
        Assert.Equal(1 << 20, host.Budget);
    }

    [Fact]
    public async Task A_grid_is_sized_by_the_estimate_at_once_and_by_a_count_off_the_page_path_once_held()
    {
        await using var host = await StartAsync(new BlotterOptions { Orders = 20_000, Exchange = Quiet() });
        var scope = await host.AcquireAsync(Tabs.Orders, FilterSpec.None, CancellationToken.None);
        var counted = await host.Queries.CountAsync(Tabs.Orders, FilterSpec.None, CancellationToken.None);
        var estimate = await host.Queries.EstimateRowsAsync(Tabs.Orders, FilterSpec.None, CancellationToken.None);

        // The tables are analysed when loaded: the estimate is close.
        Assert.NotNull(estimate);
        Assert.InRange(estimate.Value, counted * 0.9, counted * 1.1);

        // Following the live top, the estimate alone: nothing is counted.
        Assert.Equal(estimate, await host.Pages.DatabaseRowsAsync(scope.State, CancellationToken.None, count: false));
        await Task.Delay(500);
        Assert.False(host.Pages.IsCounted(scope.State));

        // Held, the estimate at once, and the count, off the page's path, as soon as it is in.
        Assert.Equal(estimate, await host.Pages.DatabaseRowsAsync(scope.State, CancellationToken.None));
        await UntilAsync(() => host.Pages.IsCounted(scope.State));
        var rows = await host.Pages.DatabaseRowsAsync(scope.State, CancellationToken.None);
        Assert.InRange(rows, counted, counted + 100);

        // A row's place, estimated with its key written into the SQL, every kind of value in it.
        var order = SortSpec.Of(Tabs.Orders, new SortColumn(Tabs.Orders.Named("client"), true), new SortColumn(Tabs.Orders.Named("qty"), false),
            new SortColumn(Tabs.Orders.Named("order_time"), true));
        var row = (await host.Pages.FromAsync(scope.State, order, null, true, 200, CancellationToken.None))[^1];
        var before = await host.Queries.CountBeforeAsync(FilterSpec.None, order, row, CancellationToken.None);
        var guessed = await host.Queries.EstimateBeforeAsync(FilterSpec.None, order, row, CancellationToken.None);
        Assert.NotNull(guessed);
        Assert.InRange(guessed.Value, 0, counted);
        output.WriteLine($"{before} rows before the row; PostgreSQL guessed {guessed}");
    }

    /// <summary>
    /// The long check, run by hand: <c>BLOTTER_PROBE_SECONDS=300 dotnet test --filter Random_pages_hold</c>
    /// runs <see cref="Probe"/> against a busy market for that long, over <c>BLOTTER_PROBE_ORDERS</c>
    /// orders (200,000 if not given), and prints PostgreSQL's estimates and what the host held.
    /// </summary>
    [Fact]
    public async Task Random_pages_hold_for_as_long_as_the_probe_runs()
    {
        var seconds = Environment.GetEnvironmentVariable("BLOTTER_PROBE_SECONDS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(seconds), "a long check, run by hand: set BLOTTER_PROBE_SECONDS");
        var orders = long.Parse(Environment.GetEnvironmentVariable("BLOTTER_PROBE_ORDERS") ?? "200000", CultureInfo.InvariantCulture);
        await using var host = await StartAsync(new BlotterOptions { Orders = orders });

        var (loaded, loadedWrong, exact, exactWrong) = await Probe.RunAsync(
            host, TimeSpan.FromSeconds(double.Parse(seconds!, CultureInfo.InvariantCulture)), output.WriteLine);

        Assert.Equal((0, 0), (loadedWrong, exactWrong));
        Assert.True(loaded > 0 && exact > 0, $"only {loaded} pages under load and {exact} held still were checked");
    }

    [Theory]
    [InlineData("order_time")]
    [InlineData("status")]
    [InlineData("filled_qty")]
    public async Task What_arrives_above_a_held_view_is_counted_from_the_streams_as_a_count_would(string sortedBy)
    {
        await using var host = await StartAsync(new BlotterOptions { Orders = 20_000, Exchange = Quiet() });
        var scope = await host.AcquireAsync(Tabs.Orders, FilterSpec.None, CancellationToken.None);
        await host.CaughtUpAsync(CancellationToken.None);
        var order = sortedBy == "order_time"
            ? Tabs.Orders.DefaultSort
            : SortSpec.Of(Tabs.Orders, new SortColumn(Tabs.Orders.Named(sortedBy), Descending: false));

        // Held at the 500th row, with the market still: what sorts above it is what has arrived since,
        // less what has left.
        host.Exchange.Pause();
        await UntilAsync(() => host.Exchange.Quiet, TimeSpan.FromSeconds(20));
        await host.CaughtUpAsync(CancellationToken.None);
        var held = scope.State;
        var top = (await host.Pages.FromAsync(held, order, null, true, 500, CancellationToken.None))[^1];
        var was = await AboveAsync(host, held, order, top);
        using var arrivals = new ArrivalsAbove(host, order, () => held);
        var counted = 0L;

        // Taken as the grid takes it, on every new state, while the market makes changes.
        host.Exchange.Resume();
        host.Exchange.Send(1500);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(4))
        {
            counted += await arrivals.TakeAsync(top, CancellationToken.None);
            await Task.Delay(20);
        }

        // Once the streams have sent everything (the order stream runs behind the fills, and the
        // database may hold an order it has not sent yet) and the host has read it all.
        host.Exchange.Pause();
        while (!host.Exchange.Quiet)
        {
            counted += await arrivals.TakeAsync(top, CancellationToken.None);
            await Task.Delay(20);
        }

        await host.CaughtUpAsync(CancellationToken.None);
        var last = scope.State;
        counted += await arrivals.TakeAsync(top, CancellationToken.None);
        var now = await AboveAsync(host, last, order, top);
        output.WriteLine($"by {sortedBy}: {was} above, now {now}; counted {counted} arriving");
        Assert.True(now != was, "nothing moved above the row");
        Assert.Equal(now - was, counted);
    }

    [Fact]
    public async Task A_database_too_far_behind_for_the_budget_stops_serving_then_serves_again()
    {
        await using var host = await StartAsync(new BlotterOptions
        {
            Orders = 20_000,
            BudgetBytes = 2 << 20,
            FindBudget = false,

            // Within the budget while the database keeps up; past it once the database stalls.
            Exchange = Quiet() with { Rate = 600 },
        });
        await host.AcquireAsync(Tabs.Orders, FilterSpec.None, CancellationToken.None);

        host.Exchange.StallIngestion(TimeSpan.FromSeconds(10));
        await UntilAsync(() => host.State == BlotterState.Refusing, TimeSpan.FromSeconds(20));

        Assert.Contains("behind the streams", host.Refusal, StringComparison.Ordinal);
        Assert.Contains("more than the 2.0 MB budget", host.Refusal, StringComparison.Ordinal);
        await Assert.ThrowsAsync<BlotterRefusedException>(() => host.AcquireAsync(Tabs.Orders, FilterSpec.None, CancellationToken.None));

        await UntilAsync(() => host.State == BlotterState.Live, TimeSpan.FromSeconds(30));
        foreach (var tab in Tabs.All)
        {
            var again = await AcquireWhenServingAsync(host, tab);
            var page = await host.Pages.View(tab.DefaultSort).GetAsync(again.State, 0, 20, CancellationToken.None);
            Assert.Null(Oracle.Invariants(page, tab.DefaultSort, FilterSpec.None));
            Assert.Equal(20, page.Rows.Length);
            await host.ReleaseAsync(again);
        }

        var (_, wrong) = await Probe.QuietAsync(host, new Random(19), 4, output.WriteLine);
        Assert.Equal(0, wrong);
    }

    [Fact]
    public async Task An_orders_history_and_fills_span_the_database_and_the_tail_in_one_statement()
    {
        await using var host = await StartAsync(new BlotterOptions { Orders = 20_000, Exchange = Quiet() with { NewShare = 0, OrderSkew = TimeSpan.Zero } });
        var scope = await host.AcquireAsync(Tabs.Orders, FilterSpec.None, CancellationToken.None);
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

    /// <summary>
    /// The market paused, everything it sent read and written, and the tail looked up since: counts of
    /// the rows above a key are then exact, not off by what the database wrote since the last lookup.
    /// </summary>
    private static async Task QuiesceAsync(BlotterHost host)
    {
        host.Exchange.Pause();
        await UntilAsync(() => host.Exchange.Quiet);
        await host.CaughtUpAsync(CancellationToken.None);
        await UntilAsync(() => host.Exchange.Written, TimeSpan.FromSeconds(30));
        await Task.Delay(host.Options.ConfirmEvery * 3);
        await host.CaughtUpAsync(CancellationToken.None);
    }

    private static ExchangeOptions Quiet() => new()
    {
        Rate = 50,
        BurstEvery = TimeSpan.Zero,
        Lag = new IngestLag { Max = TimeSpan.FromMilliseconds(500), StallEvery = TimeSpan.Zero },
        CurrentLag = new IngestLag { Max = TimeSpan.FromSeconds(1), StallEvery = TimeSpan.Zero },
    };

    // Room for whatever a busy market leaves the tail holding: what these check is the pages, not the budget.
    private static BlotterOptions Roomy() => new() { BudgetBytes = 64L << 20, FindBudget = false };

    private static ExchangeOptions Busy() => new()
    {
        Rate = 800,
        BurstEvery = TimeSpan.Zero,
        Lag = new IngestLag { Max = TimeSpan.FromSeconds(1.5), StallEvery = TimeSpan.Zero },
        CurrentLag = new IngestLag { Min = TimeSpan.FromMilliseconds(500), Max = TimeSpan.FromSeconds(3), StallEvery = TimeSpan.Zero },
    };

    private static async Task<BlotterHost> StartAsync(BlotterOptions options)
    {
        var given = Environment.GetEnvironmentVariable("CHALK_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(given) && !PgServer.CanStart())
        {
            Assert.Skip("no PostgreSQL: install PostgreSQL 16 or later, or set CHALK_TEST_POSTGRES to a connection string");
        }

        var host = new BlotterHost(options with { Postgres = given }, NullLogger<BlotterHost>.Instance);
        try
        {
            await host.StartAsync(CancellationToken.None);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }

        return host;
    }

    /// <summary>How many rows of the merged page, read from the top, sort above <paramref name="top"/>: what a reader scrolling up would find.</summary>
    private static async Task<long> AboveAsync(BlotterHost host, ScopeState state, SortSpec order, IRow top) =>
        (await host.Pages.FromAsync(state, order, null, true, 8192, CancellationToken.None)).Count(r => order.Compare(r, top) < 0);

    private static Task UntilAsync(Func<bool> condition, TimeSpan? within = null) =>
        UntilAsync(() => Task.FromResult(condition()), within);

    private static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan? within = null)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            if (clock.Elapsed > (within ?? TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("the condition never held");
            }

            await Task.Delay(50);
        }
    }
}
