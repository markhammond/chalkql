using System.Globalization;
using Chalk.Client;
using Chalk.Sample.Blotter.Fixtures;
using Chalk.Sample.Blotter.Postgres;
using Chalk.Sources.Poco;

namespace Chalk.Sample.Blotter.Live;

/// <summary>The host's own tables in ChalkQL, beside the database's: the tail's three, and the alerts'.</summary>
internal sealed record LiveTables(
    PocoTable<TailRow> Tail,
    PocoTable<Order> Versions,
    PocoTable<Fill> Fills,
    AlertTables Alerts);

/// <summary>What the host runs on, started: the house and its database, the planner, and the engine over both sides.</summary>
internal sealed record HostParts(
    IHouseDatabase House,
    Seeder Seeded,
    Exchange Exchange,
    PlannerProcess Planner,
    ChalkEngine Engine,
    LiveTables Tables,
    SourceQueries Queries);

/// <summary>
///     Starts what the host runs on, in order: the house's database, seeded; the simulated market, which
///     starts before the blotter so that events are in flight when it subscribes, as at any real start;
///     the planner; and the engine over the database's tables and the host's own. Should a step fail, what
///     was started before it is stopped again.
/// </summary>
internal static class HostStart
{
    public static async Task<HostParts> StartAsync(BlotterOptions options, ILogger log, CancellationToken ct)
    {
        List<IAsyncDisposable> started = [];
        try
        {
            IHouseDatabase? house = null;
            Seeder? seeding = null;
            await using (Spinner.Start(() => seeding is null
                             ? "Starting the database"
                             : seeding.Written < options.Orders
                                 ? string.Create(CultureInfo.InvariantCulture,
                                     $"Seeding {house!.Name}: {seeding.Stage}, {seeding.Written:N0} of {options.Orders:N0} ({100.0 * seeding.Written / Math.Max(1, options.Orders):F0}%)")
                                 : $"Seeding {house!.Name}: {seeding.Stage}"))
            {
                house = options.House is { } open
                    ? await open(ct).ConfigureAwait(false)
                    : await PostgresHouseDatabase.StartAsync(options.Postgres, options.PostgresBinaries, ct)
                        .ConfigureAwait(false);
                started.Add(house);
                seeding = new Seeder(house.Repository);
                await seeding.GenerateAsync(
                        options.Orders, Cells.Micros(DateTime.UtcNow), TimeSpan.FromDays(5), 2000, options.Current, ct)
                    .ConfigureAwait(false);
            }

            var provider = house.Provider;
            var repository = seeding.Repository;
            log.LogInformation(
                "loaded {Orders:N0} orders, {Versions:N0} versions and {Fills:N0} fills into {Database} in {Load:F1} s, indexed in {Index:F1} s",
                seeding.Orders, seeding.Versions, seeding.Fills, provider.Name, seeding.LoadTime.TotalSeconds,
                seeding.IndexTime.TotalSeconds);

            var exchange = new Exchange(repository, options.Exchange, options.Orders, seeding.LoadedThrough,
                repository.OpenOrders(), options.Current);
            started.Add(exchange);
            exchange.Start();

            var planner = await PlannerProcess.StartAsync(new PlannerProcessOptions()).ConfigureAwait(false);
            started.Add(planner);

            var reads = provider.Open(new TableRows(seeding.Orders, seeding.Versions, seeding.Fills),
                options.QueryTimeLimit);
            var live = new PocoSourceBuilder("live", "live")
                .NamingPolicy(PocoNamingPolicy.SnakeCase)
                .AddTable("tail", (IReadOnlyList<TailRow>)[], out var tail, t => t.Keyed(r => r.OrderId))
                .AddTable(
                    "order_versions",
                    (IReadOnlyList<Order>)[],
                    out var versions,
                    t => t.Keyed(v => v.OrderId, v => v.Version))
                .AddTable("tail_fills", (IReadOnlyList<Fill>)[], out var fills, t => t.Keyed(f => f.FillId))
                .AddTable("arrived_fills", (IReadOnlyList<Fill>)[], out var arrivedFills, t => t.Keyed(f => f.FillId))
                .AddTable("latest_orders", (IReadOnlyList<Order>)[], out var latestOrders, t => t.Keyed(o => o.OrderId))
                .AddTable("waits", (IReadOnlyList<Wait>)[], out var waits, t => t.Keyed(w => w.Id))
                .AddTable("match_counts", (IReadOnlyList<MatchCount>)[], out var matchCounts,
                    t => t.Keyed(c => c.Counter, c => c.SliceStart))
                .Build();
            var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "blotter",
                Sources = [reads.Source, live],
                Planner = planner.CreatePlanner(),

                // Alert views of the wait, and count views, read the time as @now: the clock every refresh moves.
                Clock = "now"
            }, ct).ConfigureAwait(false);
            started.Add(engine);

            var queries = new SourceQueries(engine, options.QueryTimeLimit, reads.Estimates,
                provider.StoppedAtTimeLimit, provider.Name);
            return new HostParts(
                house, seeding, exchange, planner, engine,
                new LiveTables(tail, versions, fills, new AlertTables(arrivedFills, latestOrders, waits, matchCounts)),
                queries);
        }
        catch
        {
            for (var at = started.Count - 1; at >= 0; at--) await started[at].DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }
}