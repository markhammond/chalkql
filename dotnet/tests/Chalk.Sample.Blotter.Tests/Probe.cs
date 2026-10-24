using System.Diagnostics;
using System.Globalization;
using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Market;
using Chalk.Sample.Blotter.Trades;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>
/// Random pages of both tabs, checked: random filters on random columns, sorts and positions. While the
/// market runs, each page is checked for what must hold however things interleave; then, in rounds, the
/// market is paused and the database held still, once behind the streams and once caught up, and each
/// page is checked against the <see cref="Oracle"/> row for row.
/// </summary>
internal static class Probe
{
    /// <summary>
    /// The whole check, for <paramref name="time"/>: PostgreSQL's estimates for each tab first, then half
    /// the time under load and half in rounds held still, then what the host held.
    /// </summary>
    public static async Task<(int Loaded, int LoadedWrong, int Exact, int ExactWrong)> RunAsync(BlotterHost host, TimeSpan time, Action<string> log)
    {
        host.WatchVitals(true);
        foreach (var tab in Tab.All)
        {
            var estimate = await host.Queries.EstimateAsync(FilterSpec.None, tab.DefaultSort, CancellationToken.None);
            log(string.Create(CultureInfo.InvariantCulture,
                $"{tab.Title}, newest first: reads {estimate.RowsRead:N0} by {estimate.EstimatedBy}, sorts {estimate.RowsSorted:N0} "
                + $"{(estimate.SortInDatabase ? "in the database" : "IN THE HOST")}"));
            log(estimate.PlanText);
        }

        var random = new Random(7);
        var (loaded, loadedWrong) = await UnderLoadAsync(host, random, time / 2, log);
        var (exact, exactWrong) = (0, 0);
        var rounds = Stopwatch.StartNew();
        while (rounds.Elapsed < time / 2)
        {
            var (checks, wrong) = await QuietAsync(host, random, 8, log);
            exact += checks;
            exactWrong += wrong;
        }

        foreach (var scope in host.Scopes)
        {
            log(string.Create(CultureInfo.InvariantCulture,
                $"scope {scope.Name}: {scope.ViewRows:N0} rows, view {scope.ViewBytes:N0} B, {scope.ViewBytes / Math.Max(1, scope.ViewRows)} B a row"));
        }

        var status = host.Status;
        log(string.Create(CultureInfo.InvariantCulture,
            $"{loaded} pages under load, {loadedWrong} breaking an invariant; {exact} pages held still, {exactWrong} differing "
            + $"from the oracle; tail {status.TailOrders:N0} orders, {status.TailFills:N0} fills; {status.Evicted:N0} confirmed and gone; "
            + $"gap fill took {status.GapFilled:N0}; used {BlotterHost.Megabytes(status.UsedBytes)} of {BlotterHost.Megabytes(status.Budget)}; "
            + $"database {BlotterHost.Megabytes(status.DatabaseBytes)}; reads {status.PageReads:N0}, lookups {status.PageLookups:N0}; "
            + $"stopped at the time limit {status.TimedOut}"));
        return (loaded, loadedWrong, exact, exactWrong);
    }

    /// <summary>Random pages while the market runs, with bursts and stalls between them: each must hold <see cref="Oracle.Invariants"/>.</summary>
    internal static async Task<(int Checks, int Wrong)> UnderLoadAsync(BlotterHost host, Random random, TimeSpan time, Action<string>? log = null)
    {
        log ??= _ => { };
        var (checks, wrong) = (0, 0);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < time)
        {
            await Task.Delay(100);
            var tab = Tab.All[random.Next(Tab.All.Count)];
            var filter = RandomFilter(tab, random);
            var order = RandomSort(tab, random);
            if (await TryAcquireAsync(host, tab, filter, log) is not { } scope)
            {
                continue;
            }

            try
            {
                var view = host.Pages.View(order);
                var total = await host.Pages.TotalAsync(scope.State, CancellationToken.None);
                var start = RandomStart(random, total);
                var timer = Stopwatch.StartNew();
                var page = await view.GetAsync(scope.State, start, 50, CancellationToken.None);
                var problem = Oracle.Invariants(page, order, filter);
                checks++;
                wrong += problem is null ? 0 : 1;
                log(string.Create(CultureInfo.InvariantCulture,
                    $"{(problem is null ? "ok  " : "FAIL")} load  {tab.Name} tail {scope.State.Live.Length} over {scope.State.Overridden.Length} "
                    + $"| {filter.Name} | {order} @ {start:N0} of ~{page.Total:N0} in {timer.Elapsed.TotalMilliseconds:F1} ms {problem}"));
            }
            catch (QueryTimeLimitException limit)
            {
                log($"stopped: {limit.Message}");
            }
            finally
            {
                await host.ReleaseAsync(scope);
            }

            if (checks % 10 == 0)
            {
                host.Exchange.Send(3000);
                if (checks % 30 == 0)
                {
                    host.Exchange.StallIngestion(TimeSpan.FromSeconds(3));
                }
            }
        }

        return (checks, wrong);
    }

    /// <summary>
    /// One round held still: the market paused, a burst the database cannot write yet, and the host
    /// caught up with the streams. Pages then must be the oracle's, first with the tail covering the
    /// database, then once the database has written everything and the tail has emptied.
    /// </summary>
    internal static async Task<(int Checks, int Wrong)> QuietAsync(BlotterHost host, Random random, int pages, Action<string> log)
    {
        var (checks, wrong) = (0, 0);
        var market = host.Exchange;
        market.Pause();
        market.StallIngestion(TimeSpan.FromMinutes(5));
        market.StallCurrent(TimeSpan.FromMinutes(5));
        try
        {
            market.Send(2000);
            await UntilAsync(() => market.Quiet, TimeSpan.FromSeconds(10));
            await host.CaughtUpAsync(CancellationToken.None);
            foreach (var behind in new[] { true, false })
            {
                if (!behind)
                {
                    market.StallIngestion(TimeSpan.Zero);
                    market.StallCurrent(TimeSpan.Zero);
                    await UntilAsync(() => market.Written, TimeSpan.FromSeconds(30));
                }

                for (var i = 0; i < pages; i++)
                {
                    var (ok, line) = await CheckExactAsync(host, random, log);
                    if (line is null)
                    {
                        continue;
                    }

                    checks++;
                    wrong += ok ? 0 : 1;
                    log($"{(ok ? "ok  " : "FAIL")} {(behind ? "still, database behind" : "still, database caught up")} {line}");
                }
            }
        }
        finally
        {
            market.StallIngestion(TimeSpan.Zero);
            market.StallCurrent(TimeSpan.Zero);
            market.Resume();
        }

        return (checks, wrong);
    }

    /// <summary>One random page, checked row for row: from the top, or from wherever it starts.</summary>
    internal static async Task<(bool Ok, string? Line)> CheckExactAsync(BlotterHost host, Random random, Action<string> log)
    {
        var tab = Tab.All[random.Next(Tab.All.Count)];
        var filter = RandomFilter(tab, random);
        var order = RandomSort(tab, random);
        if (await TryAcquireAsync(host, tab, filter, log) is not { } scope)
        {
            return (true, null);
        }

        try
        {
            var state = scope.State;
            var view = host.Pages.View(order);
            var total = await host.Pages.TotalAsync(state, CancellationToken.None);
            var start = RandomStart(random, total);
            var page = await view.GetAsync(state, start, 50, CancellationToken.None);
            var from = page.Start == 0 ? null : page.Rows.FirstOrDefault();
            if (page.Start > 0 && from is null)
            {
                return (true, null);
            }

            var (expected, expectedTotal) = Oracle.From(host.Store, host.Exchange, state, order, from, 50);
            var problem = Oracle.Compare(page.Rows, expected);
            return (problem is null, string.Create(CultureInfo.InvariantCulture,
                $"{tab.Name} tail {state.Live.Length} over {state.Overridden.Length} | {filter.Name} | {order} @ {page.Start:N0} "
                + $"of ~{page.Total:N0} (exactly {expectedTotal:N0}) {problem}"));
        }
        catch (QueryTimeLimitException limit)
        {
            return (true, $"stopped: {limit.Message}");
        }
        finally
        {
            await host.ReleaseAsync(scope);
        }
    }

    private static async Task<LiveScope?> TryAcquireAsync(BlotterHost host, Tab tab, FilterSpec filter, Action<string> log)
    {
        try
        {
            return await host.AcquireAsync(tab, filter, CancellationToken.None);
        }
        catch (BlotterRefusedException refused)
        {
            log($"refused: {refused.Message}");
            return null;
        }
    }

    /// <summary>The top, near it, or anywhere: a position deep in the tab is found by offset, the rest by key.</summary>
    private static long RandomStart(Random random, long total) => random.Next(4) switch
    {
        0 => 0,
        1 => random.Next(200),
        2 => random.Next(5000),
        _ => (long)(random.NextDouble() * Math.Max(0, total - 50)),
    };

    private static async Task UntilAsync(Func<bool> condition, TimeSpan within)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > within)
            {
                throw new TimeoutException("the condition never held");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>One to three columns, each filtered as its kind is.</summary>
    internal static FilterSpec RandomFilter(Tab tab, Random random)
    {
        if (random.Next(4) == 0)
        {
            return FilterSpec.None;
        }

        var filters = new List<ColumnFilter>();
        foreach (var column in Enumerable.Range(0, 1 + random.Next(2)).Select(_ => Sortable(tab)[random.Next(Sortable(tab).Length)]).Distinct())
        {
            if (column.Kind == ColumnKind.Id)
            {
                // An id matches one row, or none: one that does not exist, to check that too.
                filters.Add(new ColumnFilter { Column = column, Values = [Guid.CreateVersion7().ToString(), "not an id"] });
                continue;
            }

            filters.Add(column.Kind switch
            {
                ColumnKind.Choice => new ColumnFilter
                {
                    Column = column,
                    Values = [.. column.Choices.Where(_ => random.Next(2) == 0).DefaultIfEmpty(column.Choices[0])],
                },
                ColumnKind.Text when column.Name == "client" => new ColumnFilter
                {
                    Column = column,
                    Values = [.. Reference.Clients.Take(1 + random.Next(4)).Select(c => c.Ref)],
                },
                ColumnKind.Text when column.Name == "instrument" => new ColumnFilter
                {
                    Column = column,
                    Values = [Reference.Assets[random.Next(Reference.Assets.Count)].Asset[..1]],
                    Match = TextMatch.StartsWith,
                },
                ColumnKind.Text => new ColumnFilter { Column = column, Values = [Reference.Clients[random.Next(3)].Ref + "-"], Match = TextMatch.StartsWith },
                ColumnKind.Time => new ColumnFilter { Column = column, From = Cells.Micros(DateTime.UtcNow.AddHours(-random.Next(1, 72))) },
                _ when column.Name is "qty" or "filled_qty" => new ColumnFilter { Column = column, Min = random.Next(0, 50) },
                _ when column.Name is "markup_bps" => new ColumnFilter { Column = column, Max = 20 },
                _ when column.Name is "version" => new ColumnFilter { Column = column, Min = 2 },
                _ => new ColumnFilter { Column = column, Min = random.Next(1_000_000) },
            });
        }

        return FilterSpec.Of(filters);
    }

    internal static SortSpec RandomSort(Tab tab, Random random) => SortSpec.Of(
        tab,
        [
            .. Enumerable.Range(0, 1 + random.Next(3))
                .Select(_ => new SortColumn(Sortable(tab)[random.Next(Sortable(tab).Length)], random.Next(2) == 0))
                .DistinctBy(k => k.Column),
        ]);

    /// <summary>The columns a page may sort and filter by: all but those only shown.</summary>
    private static Column[] Sortable(Tab tab) => [.. tab.Columns.Where(c => !c.Derived)];
}
