using System.Diagnostics;
using System.Globalization;
using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Market;
using Microsoft.Extensions.Logging;

namespace Chalk.Sample.Blotter;

/// <summary>
/// <c>--probe</c>: starts the host without the web app, prints the plans and estimates ChalkQL makes,
/// then for a while reads random pages of both tabs — random filters on random columns, sorts and
/// positions — while the market runs, and checks every one against the <see cref="Oracle"/>.
/// </summary>
internal static class Probe
{
    public static async Task<int> RunAsync(BlotterOptions options)
    {
        using var logging = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
        await using var host = new BlotterHost(options, logging.CreateLogger<BlotterHost>());
        await host.StartAsync(CancellationToken.None);

        foreach (var tab in Tab.All)
        {
            var estimate = await host.Queries.EstimateAsync(FilterSpec.None, tab.DefaultSort, CancellationToken.None);
            Console.WriteLine($"{tab.Title}, newest first: reads {estimate.RowsRead:N0}, sorts {estimate.RowsSorted:N0} "
                + $"{(estimate.SortInDatabase ? "in the database" : "IN THE HOST")}");
            Console.WriteLine(estimate.PlanText);
        }

        var random = new Random(7);
        var checks = 0;
        var failures = 0;
        var clock = Stopwatch.StartNew();
        var seconds = double.Parse(Environment.GetEnvironmentVariable("PROBE_SECONDS") ?? "45", CultureInfo.InvariantCulture);
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(250);
            var tab = Tab.All[random.Next(Tab.All.Count)];
            var filter = RandomFilter(tab, random);
            var order = RandomSort(tab, random);
            LiveScope scope;
            try
            {
                scope = await host.AcquireAsync(tab, filter, CancellationToken.None);
            }
            catch (BlotterRefusedException refused)
            {
                Console.WriteLine($"refused: {refused.Message}");
                continue;
            }

            try
            {
                var state = scope.State;
                var total = await host.Pages.TotalAsync(state, CancellationToken.None);
                var start = random.Next(4) switch
                {
                    0 => 0,
                    1 => random.Next(200),
                    _ => (long)(random.NextDouble() * Math.Max(0, total - 50)),
                };
                var timer = Stopwatch.StartNew();
                var page = await host.Pages.GetAsync(state, order, start, 50, CancellationToken.None);
                var took = timer.Elapsed.TotalMilliseconds;
                var (expected, expectedTotal) = Oracle.Page(host.Store, host.Exchange, state, order, start, 50);
                var problem = Oracle.Compare(page, expected, expectedTotal);
                checks++;
                if (problem is not null)
                {
                    failures++;
                }

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{(problem is null ? "ok  " : "FAIL")} {tab.Name} gen {state.Generation!.Number} live {state.Live.Length} over {state.Overridden.Length} "
                    + $"| {filter.Name} | {order} @ {start:N0} of {page.Total:N0} in {took:F1} ms {problem}"));
            }
            catch (QueryTimeLimitException limit)
            {
                Console.WriteLine($"stopped: {limit.Message}");
            }
            finally
            {
                await host.ReleaseAsync(scope);
            }

            if (Environment.GetEnvironmentVariable("PROBE_BURSTS") is not null && checks % 10 == 0)
            {
                // Bursts and stalls between checks: crossings, deep tails and turnovers under load.
                host.Exchange.Send(5000);
                if (checks % 30 == 0)
                {
                    host.Exchange.StallIngestion(TimeSpan.FromSeconds(4));
                }
            }
        }

        foreach (var scope in host.Scopes)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"scope {scope.Name}: {scope.ViewRows:N0} rows, view {scope.ViewBytes:N0} B, {scope.ViewBytes / Math.Max(1, scope.ViewRows)} B a row"));
        }

        var status = host.Status;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{checks} pages checked, {failures} wrong; tail {status.TailOrders:N0} orders, {status.TailVersions:N0} versions, "
            + $"{status.TailFills:N0} fills; turnovers {status.Turnovers}; used {BlotterHost.Megabytes(status.UsedBytes)} of "
            + $"{BlotterHost.Megabytes(status.Budget)}; database {BlotterHost.Megabytes(status.DatabaseBytes)}; "
            + $"chunk reads {status.PageReads}, hits {status.PageHits}; stopped at the time limit {status.TimedOut}"));
        return failures == 0 && checks > 0 ? 0 : 1;
    }

    /// <summary>One to three columns, each filtered as its kind is.</summary>
    internal static FilterSpec RandomFilter(Tab tab, Random random)
    {
        if (random.Next(4) == 0)
        {
            return FilterSpec.None;
        }

        var filters = new List<ColumnFilter>();
        foreach (var column in Enumerable.Range(0, 1 + random.Next(2)).Select(_ => tab.Columns[random.Next(tab.Columns.Count)]).Distinct())
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
                ColumnKind.Time => new ColumnFilter { Column = column, From = DateTime.UtcNow.AddHours(-random.Next(1, 72)) },
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
                .Select(_ => new SortColumn(tab.Columns[random.Next(tab.Columns.Count)], random.Next(2) == 0))
                .DistinctBy(k => k.Column),
        ]);
}
