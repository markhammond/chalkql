using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources.Poco;

namespace Chalk.Sample.Quickstart;

/// <summary>
/// One day's rate for one currency — USD per one unit of it — as a host would write it.
/// </summary>
public sealed record UsdRate(string Currency, DateOnly Ts, double Rate);

/// <summary>
/// The README quickstart, runnable. It starts its own sidecar over a Unix domain socket
/// (<see cref="PlannerProcess"/>), so there is no port to
/// pick and nothing to export — the thing the README promises is a thing CI could run, not a snippet
/// nobody compiles. Set <c>CHALK_PLANNER_ADDRESS</c> to use a sidecar that is already running.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var rows = GenerateRates();

        var rates = new PocoSourceBuilder("mem")                   // source id; schema name defaults to "main"
            .AddTable("usd_rates", rows, t => t
                .OrderedBy(r => r.Ts).ThenBy(r => r.Currency)      // declared collation — this is what deletes sorts
                .UniqueKey(r => r.Ts, r => r.Currency))
            .Build();
        
        await using var sidecar = await PlannerProcess.StartAsync();
        
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "demo",
            Sources = [rates],
            Planner = sidecar.CreatePlanner(),
        });

        Console.WriteLine($"planner {engine.PlannerInfo.PlannerVersion} on Calcite {engine.PlannerInfo.CalciteVersion}");

        var q = await engine.PrepareAsync(
            "SELECT currency, ts, rate FROM usd_rates WHERE currency = ? AND ts >= ?");
        Console.WriteLine($"plan {q.PlanDigest:x16}: {string.Join(", ", q.OutputSchema.FieldsList.Select(f => f.Name))}");

        await using (var exec = await engine.ExecuteAsync(q, ["EUR", new DateOnly(2026, 1, 3)]))
        {
            var rowsOut = 0;
            await foreach (var batch in exec.Batches)
            {
                rowsOut += batch.Length;
                batch.Dispose();
            }

            Console.WriteLine($"positional: {rowsOut} rows, {exec.Stats.RowsScanned} scanned");
        }

        // Dapper-style named parameters, including a list expanded into the IN clause.
        var byName = await engine.PrepareAsync(
            "SELECT currency, ts, rate FROM usd_rates WHERE currency IN @currencies AND ts >= @since");
        await using (var exec2 = await engine.ExecuteAsync(
            byName,
            new { currencies = new[] { "EUR", "JPY" }, since = new DateOnly(2026, 1, 1) }))
        {
            var rowsOut = 0;
            await foreach (var batch in exec2.Batches)
            {
                rowsOut += batch.Length;
                batch.Dispose();
            }

            Console.WriteLine($"named + list: {rowsOut} rows, {exec2.Stats.RowsScanned} scanned");
        }

        // `ORDER BY ts` costs nothing: the declared collation means the plan carries no Sort at all.
        var ordered = await engine.PrepareAsync("SELECT currency, ts FROM usd_rates ORDER BY ts");
        Console.WriteLine(
            "ORDER BY ts plan:" + Environment.NewLine + ordered.Plan.ToPlanText().TrimEnd());

        return 0;
    }

    /// <summary>Ten days of rates for two currencies, sorted by <c>(ts, currency)</c> as declared.</summary>
    private static List<UsdRate> GenerateRates()
    {
        var start = new DateOnly(2026, 1, 1);
        var rows = new List<UsdRate>();
        for (var day = 0; day < 10; day++)
        {
            foreach (var currency in new[] { "EUR", "JPY" })
            {
                var basis = currency == "EUR" ? 1.085d : 0.00661d;
                rows.Add(new UsdRate(currency, start.AddDays(day), Math.Round(basis * (1d + (day / 1000d)), 6)));
            }
        }

        return rows;
    }
}
