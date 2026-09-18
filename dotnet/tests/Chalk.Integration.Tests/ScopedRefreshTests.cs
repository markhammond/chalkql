using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// The scoped refresh verb of D271 (f) and the typed targets of (h), composed as design 44 §6 writes
/// them: a source re-introspected, a table re-described, and a micro-batch landed, in one
/// transaction under one epoch.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class ScopedRefreshTests(SharedSidecar sidecar)
{
    private sealed record Line(int Id, int OrderId, int Quantity);

    /// <summary>
    /// Design 44 §6's example, verbatim but for the names the fixture has: a D270 <c>Source</c>
    /// handle, a D270 <c>Table</c> handle and a <c>PocoTable&lt;T&gt;</c>, all three accepted by the
    /// one builder. That it compiles is the assertion; that the epoch moved once is the other.
    /// </summary>
    [Fact]
    public async Task The_design_s_example_composes_a_scope_a_table_and_an_append_under_one_epoch()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        IReadOnlyList<Line> lines = [new(1, 1, 2)];
        var source = new PocoSourceBuilder("mem", "m")
            .AddTable("orders", TenancyFixture.Orders)
            .AddTable("order_details", () => lines, out var orderDetails)
            .Build();

        var catalog = new CatalogContext
        {
            ContextId = $"d271-scope-{Guid.NewGuid():n}",
            Epoch = 1,
            Schemas = [source.DescribeSchema()],
        };
        var policy = TenancyPolicy.Declare(catalog);
        var sqlite = policy.Source("m");
        var orders = sqlite.Table("orders");

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = catalog.ContextId,
            Sources = [source],
            Planner = sidecar.Sidecar.CreatePlanner(),
        });

        var before = engine.Catalog.Epoch;

        // ---- design 44 §6, as written ----
        var epoch = await engine.RefreshAsync(
            r =>
            {
                r.Refresh(sqlite);                                  // re-introspect one source
                r.Refresh(orders);                                  // re-describe one table, statistics included
                r.Append(orderDetails, [new Line(2, 1, 3)]);        // and land a micro-batch, same epoch
            },
            TestContext.Current.CancellationToken);

        // One transaction, one epoch, and the appended row is there.
        Assert.Equal(before + 1, epoch);
        Assert.Equal(epoch, engine.Catalog.Epoch);
        Assert.Equal(2, RowCount(engine, "order_details"));
        // No shape moved, so nothing prepared against this catalog is stranded.
        Assert.Equal(1, engine.ShapeEpoch);
    }

    /// <summary>
    /// The string spellings reach the same places, and a table is always named with its schema: there
    /// is no schema-less table target to write (D271 (h)).
    /// </summary>
    [Fact]
    public async Task The_string_targets_name_a_source_by_its_id_and_a_table_by_its_schema()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        IReadOnlyList<Line> lines = [new(1, 1, 2)];
        var source = new PocoSourceBuilder("mem", "m")
            .AddTable("order_details", () => lines)
            .Build();

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = $"d271-strings-{Guid.NewGuid():n}",
            Sources = [source],
            Planner = sidecar.Sidecar.CreatePlanner(),
        });

        lines = [new(1, 1, 2), new(2, 1, 3)];
        await engine.RefreshAsync(
            r => r.Refresh(RefreshTarget.Table("m", "order_details")),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, RowCount(engine, "order_details"));

        lines = [new(1, 1, 2)];
        await engine.RefreshAsync(
            r => r.Refresh(RefreshTarget.Source("mem")), TestContext.Current.CancellationToken);
        Assert.Equal(1, RowCount(engine, "order_details"));
    }

    /// <summary>A target this engine does not serve is refused before the lock is taken.</summary>
    [Fact]
    public async Task A_target_this_engine_does_not_serve_is_refused_by_name()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var source = new PocoSourceBuilder("mem", "m")
            .AddTable("order_details", (IReadOnlyList<Line>)[new Line(1, 1, 2)])
            .Build();

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = $"d271-refused-{Guid.NewGuid():n}",
            Sources = [source],
            Planner = sidecar.Sidecar.CreatePlanner(),
        });

        var unknownSource = await Assert.ThrowsAsync<ArgumentException>(
            () => engine.RefreshAsync(
                r => r.Refresh(RefreshTarget.Source("nope")),
                TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("nope", unknownSource.Message, StringComparison.Ordinal);

        var unknownTable = await Assert.ThrowsAsync<ArgumentException>(
            () => engine.RefreshAsync(
                r => r.Refresh(RefreshTarget.Table("m", "nope")),
                TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("nope", unknownTable.Message, StringComparison.Ordinal);
    }

    private static long RowCount(ChalkEngine engine, string table) =>
        engine.Catalog.Schemas.SelectMany(s => s.Tables).Single(t => t.Name == table).RowCount;
}
