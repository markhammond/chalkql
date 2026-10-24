using Chalk.Sample.Blotter.Domain;
using Chalk.Sample.Blotter.Live;

namespace Chalk.Sample.Blotter.Fixtures;

/// <summary>
/// The database the simulated house runs on, for as long as the sample does: one set of tables, which
/// the house seeds and writes its synthetic events to (<see cref="Store"/>), and the host reads live
/// (<see cref="Provider"/>). Disposed, it lets both go, and with them any server it started.
/// </summary>
/// <remarks>
/// This is the fixtures' seam, not the host's: a real deployment has no house, and gives the host an
/// <see cref="IBlotterProvider"/> over someone else's tables.
/// </remarks>
public interface IHouseDatabase : IAsyncDisposable
{
    /// <summary>What the database is called, while it starts and seeds: "PostgreSQL".</summary>
    string Name { get; }

    /// <summary>The tables, as the house writes them.</summary>
    ITradeRepository Store { get; }

    /// <summary>The same tables, as the host reads them.</summary>
    IBlotterProvider Provider { get; }
}
