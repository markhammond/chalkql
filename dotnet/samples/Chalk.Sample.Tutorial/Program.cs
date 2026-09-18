using System.Globalization;
using Chalk.Client;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// The tutorial, runnable. Eighteen chapters in order, each in a file of its own, each printing what it
/// does, the SQL, the plan where the plan is the point, and the result. <c>docs/tutorial.md</c> is
/// the written walkthrough and quotes this program's output verbatim, so nothing here is random and
/// nothing here is timed.
/// </summary>
/// <remarks>
/// It starts its own sidecar over a Unix domain socket (<see cref="PlannerProcess"/>), so there is
/// no port to pick and nothing to configure; set <c>CHALK_PLANNER_ADDRESS</c> to use one that is
/// already running. Run one chapter on its own by passing its number:
/// <c>dotnet run --project dotnet/samples/Chalk.Sample.Tutorial -- 3</c>.
/// </remarks>
internal static class Program
{
    private static readonly (int Number, string Title, Func<Func<IQueryPlanner>, Task> Run)[] Chapters =
    [
        (1, "A list is a table", Chapter01Tables.RunAsync),
        (2, "What the planner did", Chapter02Plans.RunAsync),
        (3, "Two sources, one query", Chapter03TwoSources.RunAsync),
        (4, "Strings without allocation", Chapter04Utf8.RunAsync),
        (5, "Time series", Chapter05TimeSeries.RunAsync),
        (6, "Your functions", Chapter06Functions.RunAsync),
        (7, "One table, many places", Chapter07Partitions.RunAsync),
        (8, "Parameters and prepared statements", Chapter08Parameters.RunAsync),
        (9, "When the source cannot", Chapter09Residual.RunAsync),
        (10, "Who's asking", Chapter10WhosAsking.RunAsync),
        (11, "Through the parent", Chapter11ThroughTheParent.RunAsync),
        (12, "Through the lines", Chapter12ThroughTheLines.RunAsync),
        (13, "Replanning", Chapter13Replanning.RunAsync),
        (14, "Planning on a budget", Chapter14PlanningOnABudget.RunAsync),
        (15, "Live pivot", Chapter15LivePivot.RunAsync),
        (16, "Streaming live pivot", Chapter16Streaming.RunAsync),
        (17, "Temporal streaming live pivot", Chapter17Temporal.RunAsync),
        (18, "Advanced topics: runtime policy configuration", Chapter18Advanced.RunAsync),
    ];

    private static async Task<int> Main(string[] args)
    {
        var only = args.Length > 0 && int.TryParse(args[0], CultureInfo.InvariantCulture, out var n) ? n : 0;

        var configured = Environment.GetEnvironmentVariable("CHALK_PLANNER_ADDRESS");
        await using var sidecar = string.IsNullOrWhiteSpace(configured)
            ? await PlannerProcess.StartAsync()
            : null;

        var address = sidecar is null
            ? new Uri(configured!)
            : null;
        
        Func<IQueryPlanner> planner = sidecar is not null
            ? () => sidecar.CreatePlanner()
            : () => new GrpcQueryPlanner(new GrpcPlannerOptions { Address = address! });

        Console.WriteLine("Chalk tutorial");
        Console.WriteLine($"planner at {sidecar?.Address.ToString() ?? configured}");

        // One engine per chapter, so every chapter's catalog is only what that chapter registered;
        // this one exists to print what the sidecar says about itself.
        await using (var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial",
            Sources = [Marketplace.Poco()],
            Planner = planner(),
        }))
        {
            Console.WriteLine($"planner {engine.PlannerInfo.PlannerVersion} on Calcite {engine.PlannerInfo.CalciteVersion}");
        }

        foreach (var (number, title, run) in Chapters)
        {
            if (only != 0 && only != number)
            {
                continue;
            }

            try
            {
                await run(planner);
            }
            catch (Exception failure)
            {
                Console.Error.WriteLine($"chapter {number} ({title}) failed: {failure}");
                return 1;
            }
        }

        Console.WriteLine();
        Console.WriteLine("Done. docs/tutorial.md walks through this output.");
        return 0;
    }
}
