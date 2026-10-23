using Chalk.Sample.Blotter.Live;

namespace Chalk.Sample.Blotter;

/// <summary>Starts the blotter host with the web app, and stops it with it.</summary>
internal sealed class BlotterService(BlotterHost host) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => host.StartAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken) => await host.DisposeAsync();
}
