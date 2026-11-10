using Chalk.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// On an engine that names a clock, the parameter named after it is the clock when the host binds
/// none, however the host runs the statement (D341).
/// </summary>
/// <remarks>
/// Only the overloads that bind by name gave it the clock. A run with no values, or with a list,
/// and every run with a principal's context, was refused for a value it was never meant to need:
/// "the statement has 1 '?' placeholders but 0 values were bound". A list now leaves the clock's
/// parameter out to be given the clock, and binds it as the host's value when it gives every one.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class ClockParameterTests(SharedSidecar sidecar)
{
    /// <summary>How the host runs the statement.</summary>
    public enum Run
    {
        /// <summary><c>ExecuteAsync(query)</c>: no values at all.</summary>
        NoValues,

        /// <summary><c>ExecuteAsync(query, [floor])</c>: a list that leaves the clock out.</summary>
        List,

        /// <summary><c>ExecuteAsync(query, [floor], pool)</c>: the same, on a host's arena pool.</summary>
        ListOnPool,

        /// <summary><c>QueryAsync(sql)</c>: prepared and run in one call.</summary>
        Query,

        /// <summary><c>ExecuteAsync(query, context)</c>: prepared once for a shape, run for a principal.</summary>
        Context,

        /// <summary><c>ExecuteAsync(query, context, [floor], pool)</c>: the same, with a list, on a pool.</summary>
        ContextListOnPool,
    }

    public static TheoryData<Run> Runs =>
        new() { Run.NoValues, Run.List, Run.ListOnPool, Run.Query, Run.Context, Run.ContextListOnPool };

    private sealed record Track(int Id, string Mission, DateTime ReportedAt);

    private static readonly DateTime Day = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Unspecified);

    private static readonly Track[] Tracks = [new(1, "K", At(10, 0)), new(2, "K", At(10, 5)), new(3, "S", At(9, 40))];

    private const string Overdue = "SELECT Id FROM s.tracks WHERE ReportedAt < @now - INTERVAL '10' MINUTE ORDER BY Id";

    private const string OverdueFrom =
        "SELECT Id FROM s.tracks WHERE Id >= @floor AND ReportedAt < @now - INTERVAL '10' MINUTE ORDER BY Id";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The clock moves the result with no row changed, whichever way the statement is run: at 10:00
    /// only what reported before 09:50, at 10:11 what reported by 10:00, at 10:16 by 10:05.
    /// </summary>
    [Theory]
    [MemberData(nameof(Runs))]
    public async Task The_clocks_parameter_left_unbound_is_the_clock_on_every_path(Run run)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar, entitled: IsEntitled(run));

        int[][] expected = run switch
        {
            Run.NoValues or Run.Query => [[3], [1, 3], [1, 2, 3]],
            Run.List or Run.ListOnPool => [[3], [3], [2, 3]],
            Run.Context => [[], [1], [1, 2]],
            _ => [[], [], [2]],
        };

        var at = new[] { At(10, 0), At(10, 11), At(10, 16) };
        for (var i = 0; i < at.Length; i++)
        {
            await fixture.Engine.RefreshAsync(r => r.AdvanceClock(Utc(at[i])), Ct);
            Assert.Equal(expected[i], await fixture.IdsAsync(run, values: [2]));
        }
    }

    /// <summary>A list that gives every parameter gives the clock's too, as the host's value.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_list_that_binds_the_clocks_parameter_binds_the_hosts_value(bool entitled)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar, entitled);
        await fixture.Engine.RefreshAsync(r => r.AdvanceClock(Utc(At(10, 0))), Ct);

        var later = At(11, 0);
        if (entitled)
        {
            Assert.Equal([1, 2], await fixture.IdsAsync(Run.Context, values: [later]));
            Assert.Equal([2], await fixture.IdsAsync(Run.ContextListOnPool, values: [2, later]));
        }
        else
        {
            Assert.Equal([1, 2, 3], await fixture.IdsAsync(Run.NoValues, values: [later]));
            Assert.Equal([2, 3], await fixture.IdsAsync(Run.List, values: [2, later]));
            Assert.Equal([2, 3], await fixture.IdsAsync(Run.ListOnPool, values: [2, later]));
        }
    }

    /// <summary>
    /// Before any refresh sets the clock, a run that leaves the clock's parameter to it is refused,
    /// naming the clock, as one that reads <c>CURRENT_TIMESTAMP</c> is.
    /// </summary>
    [Theory]
    [MemberData(nameof(Runs))]
    public async Task Before_the_clock_is_set_the_clocks_parameter_is_refused(Run run)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar, entitled: IsEntitled(run));

        var refused = await Assert.ThrowsAsync<ClockRequiredException>(() => fixture.IdsAsync(run, values: [2]));
        Assert.Equal(ChalkErrorCodes.ClockRequired, refused.Code);
        Assert.Equal("now", refused.Clock);
    }

    /// <summary>
    /// A list of the wrong length for a named statement is refused naming its parameters in the order
    /// a list binds them, rather than as '?' placeholders; an empty list is refused too, not taken for
    /// a statement without parameters.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task A_list_of_the_wrong_length_names_the_statements_parameters(int count)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar, entitled: false);
        await fixture.Engine.RefreshAsync(r => r.AdvanceClock(Utc(At(10, 0))), Ct);
        var prepared = await fixture.Engine.PrepareAsync(OverdueFrom, ct: Ct);

        var refused = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await using var execution = await fixture.Engine.ExecuteAsync(
                prepared, Enumerable.Repeat<object?>(2, count).ToList(), ct: Ct);
        });
        Assert.Equal(
            $"the statement has 2 named parameters (@floor, @now) but {count} values were bound, in that order",
            refused.Message);
    }

    private static bool IsEntitled(Run run) => run is Run.Context or Run.ContextListOnPool;

    private static DateTime At(int hour, int minute) => Day.AddHours(hour).AddMinutes(minute);

    private static DateTimeOffset Utc(DateTime wall) => new(DateTime.SpecifyKind(wall, DateTimeKind.Utc));

    /// <summary>
    /// An engine whose clock is named "now", over the tracks; entitled, its table restricted by mission
    /// and a principal granted mission K, its statements prepared once for that principal's shape.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ArenaPool _pool = new(new ArenaOptions(), capacity: 1, name: "clock");

        private EntitledEngine? _entitled;
        private RequestContext? _context;

        public ChalkEngine Engine { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(SharedSidecar sidecar, bool entitled)
        {
            var fixture = new Fixture();
            TenancyEntitlements? entitlements = null;
            Grant? grant = null;
            if (entitled)
            {
                var catalog = new CatalogContext { ContextId = "clock-parameter", Schemas = [Source(null).DescribeSchema()] };
                var policy = TenancyPolicy.Declare(catalog);
                var mission = policy.Tenancy("mission");
                var reader = policy.Role("reader");
                var table = policy.Source("s").Table("tracks");
                table.Tenancy(t => t.Direct(mission, table.Column("Mission")));
                entitlements = policy.Compile(catalog);
                grant = Grant.ForTenancy(mission, "K", reader);
            }

            fixture.Engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "clock-parameter",
                Sources = [Source(entitlements)],
                Planner = sidecar.CreatePlanner(),
                Clock = "now",
            }, Ct);

            if (entitlements is not null)
            {
                fixture._entitled = fixture.Engine.WithEntitlements();
                fixture._context = entitlements.Bind(new TenancyPrincipal { User = "u", Grants = [grant!] });
            }

            return fixture;
        }

        /// <summary>
        /// The ids the run returns. Runs with a list bind <paramref name="values"/>, and run the
        /// statement with <c>@floor</c>; the others run the one with <c>@now</c> alone, and bind
        /// <paramref name="values"/> only where a list can carry them, for a value of the clock's own.
        /// </summary>
        public async Task<List<int>> IdsAsync(Run run, IReadOnlyList<object?> values)
        {
            var hostsClock = values.Count > 0 && values[^1] is DateTime;
            var execution = run switch
            {
                Run.NoValues => await Engine.ExecuteAsync(
                    await Engine.PrepareAsync(Overdue, ct: Ct), hostsClock ? values : null, ct: Ct),
                Run.List => await Engine.ExecuteAsync(await Engine.PrepareAsync(OverdueFrom, ct: Ct), values, ct: Ct),
                Run.ListOnPool => await Engine.ExecuteAsync(
                    await Engine.PrepareAsync(OverdueFrom, ct: Ct), values, _pool, Ct),
                Run.Context => await Engine.ExecuteAsync(
                    await _entitled!.PrepareAsync(Overdue, _context!.Shape(), ct: Ct), _context, hostsClock ? values : null, ct: Ct),
                Run.ContextListOnPool => await Engine.ExecuteAsync(
                    await _entitled!.PrepareAsync(OverdueFrom, _context!.Shape(), ct: Ct), _context, values, _pool, Ct),
                _ => null,
            };

            var ids = new List<int>();
            if (execution is null)
            {
                await foreach (var batch in Engine.QueryAsync(Overdue, ct: Ct))
                {
                    using (batch)
                    {
                        ids.AddRange(batch.ToRows().Select(r => (int)r[0]!));
                    }
                }

                return ids;
            }

            await using (execution)
            {
                await foreach (var batch in execution.Batches.WithCancellation(Ct))
                {
                    using (batch)
                    {
                        ids.AddRange(batch.ToRows().Select(r => (int)r[0]!));
                    }
                }
            }

            return ids;
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            _pool.Dispose();
        }

        private static PocoSource Source(TenancyEntitlements? entitlements) =>
            new PocoSourceBuilder("s", "s")
                .AddTable("tracks", Tracks, t =>
                {
                    t.UniqueKey(x => x.Id);
                    if (entitlements?.For("s", "tracks") is { } d) t.Entitlement(d);
                })
                .Build();
    }
}
