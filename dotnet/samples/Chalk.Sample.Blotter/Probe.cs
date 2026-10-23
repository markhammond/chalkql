using System.Diagnostics;
using System.Globalization;
using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Market;
using Microsoft.Extensions.Logging;

namespace Chalk.Sample.Blotter;

/// <summary>
/// <c>--probe</c>: starts the host without the web app, prints the plans ChalkQL makes, then for a
/// while reads random pages — random filters, sorts and positions — while the market runs, and
/// checks every one against the <see cref="Oracle"/>.
/// </summary>
internal static class Probe
{
    public static async Task<int> RunAsync(BlotterOptions options)
    {
        using var logging = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
        await using var host = new BlotterHost(options, logging.CreateLogger<BlotterHost>());
        await host.StartAsync(CancellationToken.None);

        var byTime = SortSpec.Default;
        var multi = SortSpec.Of(new SortColumn(Column.Client, false), new SortColumn(Column.Symbol, false), new SortColumn(Column.AvgPrice, true));
        var filtered = new FilterSpec { Statuses = ["Filled", "PartiallyFilled"], Side = "Buy" }.Canonical();
        Console.WriteLine("page plan, newest first, no filter:");
        Console.WriteLine(await host.Queries.PagePlanAsync(FilterSpec.None, byTime, CancellationToken.None));
        Console.WriteLine("page plan, three columns, filtered:");
        Console.WriteLine(await host.Queries.PagePlanAsync(filtered, multi, CancellationToken.None));

        var everything = await host.AcquireAsync(FilterSpec.None, CancellationToken.None);
        Console.WriteLine("live scope plan, no filter:");
        Console.WriteLine(everything.PlanText);

        var random = new Random(7);
        var columns = Column.All;
        var checks = 0;
        var failures = 0;
        var clock = Stopwatch.StartNew();
        var seconds = double.Parse(Environment.GetEnvironmentVariable("PROBE_SECONDS") ?? "45", CultureInfo.InvariantCulture);
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(250);
            var filter = random.Next(3) switch
            {
                0 => FilterSpec.None,
                1 => new FilterSpec { Statuses = [Reference.Statuses[random.Next(4)], Reference.Statuses[random.Next(4)]] }.Canonical(),
                _ => new FilterSpec { Side = Reference.Sides[random.Next(2)], Symbols = [.. Reference.Symbols.Take(40)] }.Canonical(),
            };
            var keys = Enumerable.Range(0, 1 + random.Next(3))
                .Select(_ => new SortColumn(columns[random.Next(columns.Count)], random.Next(2) == 0))
                .DistinctBy(k => k.Column)
                .ToArray();
            var order = SortSpec.Of(keys);
            LiveScope scope;
            try
            {
                scope = await host.AcquireAsync(filter, CancellationToken.None);
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
                var (expected, expectedTotal) = Oracle.Page(host.Store, host.Exchange.Feed, state, order, start, 50);
                var problem = Oracle.Compare(page, expected, expectedTotal);
                checks++;
                if (problem is not null)
                {
                    failures++;
                }

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{(problem is null ? "ok  " : "FAIL")} gen {state.Generation!.Number} epoch {state.Epoch} live {state.Live.Length} over {state.Overridden.Length} "
                    + $"| {filter.Name} | {order} @ {start:N0} of {page.Total:N0} in {took:F1} ms {problem}"));
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
                $"scope {scope.Filter.Name}: {scope.ViewRows:N0} trades, view {scope.ViewBytes:N0} B, {scope.ViewBytes / Math.Max(1, scope.ViewRows)} B a trade"));
        }

        var status = host.Status;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{checks} pages checked, {failures} wrong; tail {status.TailTrades:N0} trades, {status.TailVersions:N0} versions; "
            + $"turnovers {status.Turnovers}; used {BlotterHost.Megabytes(status.UsedBytes)} of {BlotterHost.Megabytes(status.Budget)}; "
            + $"database {BlotterHost.Megabytes(status.DatabaseBytes)}; chunk reads {status.PageReads}, hits {status.PageHits}"));
        return failures == 0 && checks > 0 ? 0 : 1;
    }
}
