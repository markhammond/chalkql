using Chalk.Arrow;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A TIMESTAMP read back through ChalkQL's own readers is the wall clock it is (F189): a
/// <see cref="DateTime"/> of kind Unspecified, whatever the machine's offset, and a TIMESTAMP WITH TIME
/// ZONE a UTC <see cref="DateTimeOffset"/>. Before, the reader widened the DateTime to a
/// DateTimeOffset converted as local time, so the type was not the documented one and, away from UTC,
/// the instant moved by the machine's offset. These hold on any machine: they check the type too.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class TimestampReadBackTests(SharedSidecar sidecar)
{
    private sealed record Stamp(int Id, DateTime Seen, DateTimeOffset SeenZoned);

    private struct Key
    {
        public int Id;
    }

    private struct Row
    {
        public long Ticks;
    }

    private static readonly DateTime Noon = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Unspecified);

    private static readonly Stamp[] Stamps = [new(1, Noon, new DateTimeOffset(Noon, TimeSpan.FromHours(2)))];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ToRows_reads_a_timestamp_as_its_wall_clock_and_a_zoned_one_as_utc()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [Source()],
            Planner = sidecar.CreatePlanner(),
        }, Ct);

        var rows = new List<object?[]>();
        await foreach (var batch in engine.QueryAsync("SELECT seen, seen_zoned FROM stamps", ct: Ct))
        {
            using (batch)
            {
                rows.AddRange(batch.ToRows());
            }
        }

        var seen = Assert.IsType<DateTime>(rows.Single()[0]);
        Assert.Equal(DateTimeKind.Unspecified, seen.Kind);
        Assert.Equal(Noon, seen);

        var zoned = Assert.IsType<DateTimeOffset>(rows.Single()[1]);
        Assert.Equal(TimeSpan.Zero, zoned.Offset);
        Assert.Equal(new DateTimeOffset(Noon, TimeSpan.FromHours(2)).UtcDateTime, zoned.UtcDateTime);
    }

    /// <summary>
    /// A view's <c>GetValue</c> reads the same way: its clock, at noon UTC, comes back as noon, a
    /// DateTime of kind Unspecified, as <c>GetDateTime</c> already did.
    /// </summary>
    [Fact]
    public async Task A_views_GetValue_reads_its_clock_as_the_wall_clock()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [Source()],
            Planner = sidecar.CreatePlanner(),
            Clock = "now",
        }, Ct);
        await engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(Noon, TimeSpan.Zero)), Ct);

        object? read = null;
        DateTime viaGetter = default;
        await using var view = await engine.MaterializeAsync(
            "SELECT id, CURRENT_TIMESTAMP(9) AS now FROM stamps",
            new ViewOptions<Key, Row>
            {
                Key = ["id"],
                LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
                Map = (in ViewRow row, out Key key, out Row value) =>
                {
                    read = row.GetValue(1);
                    viaGetter = row.GetDateTime(1);
                    key = new Key { Id = row.GetInt32(0) };
                    value = new Row { Ticks = viaGetter.Ticks };
                },
            },
            Ct);

        var now = Assert.IsType<DateTime>(read);
        Assert.Equal(DateTimeKind.Unspecified, now.Kind);
        Assert.Equal(Noon, now);
        Assert.Equal(viaGetter, now);
    }

    private static PocoSource Source() => new PocoSourceBuilder("stamps")
        .NamingPolicy(PocoNamingPolicy.SnakeCase)
        .AddTable("stamps", (IReadOnlyList<Stamp>)Stamps)
        .Build();
}
