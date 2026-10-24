using Chalk.Sample.Blotter.Domain;
using Chalk.Sample.Blotter.Live;

namespace Chalk.Sample.Blotter.Fixtures;

/// <summary>
/// The simulated house's database in PostgreSQL: a server the sample starts for itself, or one it is
/// given a connection string to (<see cref="PgServer"/>), written through <see cref="PostgresTradeRepository"/>
/// and read through <see cref="PostgresBlotterProvider"/>.
/// </summary>
public sealed class PostgresHouseDatabase : IHouseDatabase
{
    private readonly PgServer _server;

    private PostgresHouseDatabase(PgServer server)
    {
        _server = server;
        Store = new PostgresTradeRepository(server.ConnectionString);
        Provider = new PostgresBlotterProvider(server.ConnectionString);
    }

    public string Name => "PostgreSQL";

    public ITradeRepository Store { get; }

    public IBlotterProvider Provider { get; }

    /// <summary>
    /// The server <paramref name="connectionString"/> names, if given; otherwise one started from
    /// <paramref name="binaries"/>, or from the first PostgreSQL 16 or later found on this machine.
    /// </summary>
    public static async Task<PostgresHouseDatabase> StartAsync(string? connectionString, string? binaries, CancellationToken ct) =>
        new(await PgServer.StartAsync(connectionString, binaries, ct).ConfigureAwait(false));

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync().ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
