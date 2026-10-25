using System.Diagnostics;
using Chalk.Sample.Blotter.Live;

namespace Chalk.Sample.Blotter;

/// <summary>
///     Starts the blotter host with the web app, and stops it with it; and stops the web app if the planner
///     goes while it runs.
/// </summary>
/// <remarks>
///     Ctrl+C in a terminal reaches every process of the foreground job, the planner among them, and it goes
///     at once; a launcher that does not pass it on, as an IDE's console or <c>dotnet run</c> may not, leaves
///     the web app running with no planner, its catch-up loop failing every second. The web app cannot serve
///     without one, so it stops too, saying why.
/// </remarks>
internal sealed class BlotterService(BlotterHost host, IHostApplicationLifetime lifetime, ILogger<BlotterService> log)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await host.StartAsync(cancellationToken);
        _ = StopWithThePlannerAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await host.DisposeAsync();

    private async Task StopWithThePlannerAsync()
    {
        if (host.PlannerProcessId is not { } id)
        {
            return;
        }

        try
        {
            using var planner = Process.GetProcessById(id);
            await planner.WaitForExitAsync(lifetime.ApplicationStopping);
        }
        catch (OperationCanceledException)
        {
            // The web app is stopping, and stops the planner itself.
            return;
        }
        catch (ArgumentException)
        {
            // Gone already.
        }

        if (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return;
        }

        log.LogError("the planner has stopped, so the blotter stops too: it cannot serve without one");
        lifetime.StopApplication();
    }
}
