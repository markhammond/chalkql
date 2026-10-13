using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Epochs as a host numbers them, and the engine's clock (D339,
/// <c>docs/design/64-incremental-views.md</c> §3), through the engine's own refresh: what a query
/// reads after each, and what a refusal leaves unchanged.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class EpochVersionTests(SharedSidecar sidecar)
{
    private sealed record Fill(int Seq, string Book, DateTime Ts);

    private sealed record Position(string Book, string Asset, int Qty);

    private static readonly DateTime Open = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A refresh numbered with a version takes it; one without is numbered one past the last; one
    /// whose version does not exceed the engine's is refused and changes nothing.
    /// </summary>
    [Fact]
    public async Task A_refresh_numbers_its_epoch_with_the_hosts_version()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (engine, desk) = await DeskAsync();
        await using var _ = engine;
        Assert.Equal(0, engine.Version);

        await engine.RefreshAsync(r => r.Version(7).Append(desk, "fills", (IReadOnlyList<Fill>)[new(2, "b1", Open.AddSeconds(1))]), Ct);
        Assert.Equal(7, engine.Version);

        await engine.RefreshAsync(r => r.Append(desk, "fills", (IReadOnlyList<Fill>)[new(3, "b1", Open.AddSeconds(2))]), Ct);
        Assert.Equal(8, engine.Version);

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => engine.RefreshAsync(r => r.Version(8).Append(desk, "fills", (IReadOnlyList<Fill>)[new(4, "b1", Open.AddSeconds(3))]), Ct).AsTask());
        Assert.Contains("the engine is already at version 8", refused.Message, StringComparison.Ordinal);
        Assert.Equal(8, engine.Version);
        await AssertIntsAsync(engine, "SELECT seq FROM fills ORDER BY seq", 1, 2, 3);
    }

    /// <summary>
    /// The clock moves in an epoch and only forward. A refresh that only numbers a version or moves
    /// the clock still publishes an epoch.
    /// </summary>
    [Fact]
    public async Task The_clock_moves_forward_in_an_epoch()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (engine, _) = await DeskAsync();
        await using var __ = engine;
        Assert.Null(engine.ClockTime);

        var tick = new DateTimeOffset(Open);
        await engine.RefreshAsync(r => r.AdvanceClock(tick), Ct);
        Assert.Equal(tick, engine.ClockTime);
        Assert.Equal(1, engine.Version);

        var back = await Assert.ThrowsAsync<ArgumentException>(
            () => engine.RefreshAsync(r => r.AdvanceClock(tick.AddSeconds(-1)), Ct).AsTask());
        Assert.Contains("would move the clock back", back.Message, StringComparison.Ordinal);
        Assert.Equal(tick, engine.ClockTime);

        await engine.RefreshAsync(r => r.Version(5), Ct);
        Assert.Equal(5, engine.Version);
        Assert.Equal(tick, engine.ClockTime);
    }

    /// <summary>An engine without a clock refuses a refresh that advances one, before anything happens.</summary>
    [Fact]
    public async Task An_engine_without_a_clock_refuses_to_advance_one()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (engine, _) = await DeskAsync(clock: null);
        await using var __ = engine;

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(Open)), Ct).AsTask());
        Assert.Contains("ChalkEngineOptions.Clock", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, engine.Version);
    }

    /// <summary>A clock is named as a parameter is, after its '@'.</summary>
    [Theory]
    [InlineData("@now")]
    [InlineData("")]
    [InlineData("the time")]
    public async Task A_clock_is_named_as_a_parameter_is(string name)
    {
        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                Sources = [Desk()],
                Planner = sidecar.CreatePlanner(),
                Clock = name,
            }, Ct).AsTask());
        Assert.Contains("a clock is named as a parameter is", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A keyed table's upserts and deletes, through the engine, are what the next query reads.</summary>
    [Fact]
    public async Task Upserts_and_deletes_reach_the_next_query()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (engine, desk) = await DeskAsync();
        await using var _ = engine;

        await engine.RefreshAsync(
            r => r
                .Upsert(desk, "positions", (IReadOnlyList<Position>)[new("b1", "BRIX", 20), new("b3", "CIRRUS", 4)])
                .Delete(desk, "positions", (IReadOnlyList<Position>)[new("b2", "ACME", 0)]),
            Ct);

        await AssertIntsAsync(engine, "SELECT qty FROM positions ORDER BY qty", 1, 4, 20);
    }

    /// <summary>
    /// A late row refuses the whole refresh through the engine too: the rows, the version and the
    /// other tables of the refresh stay as they were.
    /// </summary>
    [Fact]
    public async Task A_late_row_refuses_the_whole_refresh()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (engine, desk) = await DeskAsync();
        await using var _ = engine;
        await engine.RefreshAsync(r => r.Advance(desk, "fills", Open.AddMinutes(1)), Ct);

        var refused = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => engine.RefreshAsync(
                r => r
                    .Upsert(desk, "positions", (IReadOnlyList<Position>)[new("b9", "NEW", 9)])
                    .Append(desk, "fills", (IReadOnlyList<Fill>)[new(2, "b1", Open.AddSeconds(30))]),
                Ct).AsTask());

        Assert.Equal(ChalkErrorCodes.DeclarationNotHeld, refused.Code);
        Assert.Equal(1, engine.Version);
        await AssertIntsAsync(engine, "SELECT seq FROM fills", 1);
        await AssertIntsAsync(engine, "SELECT qty FROM positions ORDER BY qty", 1, 2, 3);
    }

    /// <summary>
    /// The engine's change count is odd only while a refresh makes rows visible: each refresh moves
    /// it by two and leaves it even, and a refused one leaves it even too.
    /// </summary>
    [Fact]
    public async Task Each_refresh_moves_the_change_count_by_two()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (engine, desk) = await DeskAsync();
        await using var _ = engine;
        var before = engine.Changes;
        Assert.Equal(0, before % 2);

        await engine.RefreshAsync(r => r.Append(desk, "fills", (IReadOnlyList<Fill>)[new(2, "b1", Open.AddSeconds(5))]), Ct);
        Assert.Equal(before + 2, engine.Changes);

        await Assert.ThrowsAsync<CatalogVerificationException>(
            () => engine.RefreshAsync(r => r.Append(desk, "fills", (IReadOnlyList<Fill>)[new(3, "b1", Open)]), Ct).AsTask());
        Assert.Equal(0, engine.Changes % 2);
    }

    // ---- the scaffolding ----

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PocoSource Desk() =>
        new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("fills", (IReadOnlyList<Fill>)[new(1, "b1", Open)], t => t.AppendOnly(x => x.Ts))
            .AddTable(
                "positions",
                (IReadOnlyList<Position>)[new("b1", "ACME", 1), new("b1", "BRIX", 2), new("b2", "ACME", 3)],
                t => t.Keyed(x => x.Book, x => x.Asset))
            .Build();

    private async Task<(ChalkEngine Engine, PocoSource Desk)> DeskAsync(string? clock = "now")
    {
        var desk = Desk();
        var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [desk],
            Planner = sidecar.CreatePlanner(),
            Clock = clock,
        }, Ct);
        return (engine, desk);
    }

    private static async Task AssertIntsAsync(ChalkEngine engine, string sql, params int[] expected) =>
        Assert.Equal(expected, await IntsAsync(engine, sql));

    private static async Task<int[]> IntsAsync(ChalkEngine engine, string sql)
    {
        var values = new List<int>();
        await foreach (var batch in engine.QueryAsync(sql, (IReadOnlyList<object?>)[], ct: Ct))
        {
            using (batch)
            {
                var column = batch.Column(0);
                for (var i = 0; i < batch.Length; i++)
                {
                    values.Add(column switch
                    {
                        Int32Array ints => ints.GetValue(i)!.Value,
                        Int64Array longs => (int)longs.GetValue(i)!.Value,
                        _ => throw new InvalidOperationException(column.GetType().Name),
                    });
                }
            }
        }

        return [.. values];
    }
}
