#pragma warning disable CHALK003
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A maintained view's result finds each key's row by the key. A key holds one row except when the
/// view faults on it, so the index keeps one slot a key, and a key of every column of the result is
/// the row itself, which the result already finds. A filter view of keys over a keyed table, its rows
/// moved in and out by upserts, holds a few dozen bytes a row for its result rather than a few
/// hundred, and gives the statement's rows throughout; a key given twice still faults it.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class ResultKeyIndexTests(SharedSidecar sidecar, ITestOutputHelper output)
{
    private sealed record Order(long Id, long Status, long Qty);

    private struct K
    {
        public long Id;
    }

    private struct V
    {
        public long Status;
    }

    private const int Orders = 20_000;

    public static TheoryData<string, string, long> Views() => new()
    {
        // The key is the whole row: the result's own identity map finds it.
        { "keys only", "SELECT id FROM orders WHERE status <> 3", 260 },

        // The key is one column of two: one slot a key, in a map of encoded keys.
        { "a key and a value", "SELECT id, status FROM orders WHERE status <> 3", 330 },
    };

    /// <summary>
    /// Twenty thousand orders, three quarters passing the filter, then twenty batches of upserts that
    /// move a tenth of them across it each time: the view gives the statement's keys after every one,
    /// and holds no more than the bound a row it passes. Before the index kept one slot a key, the
    /// view held 373 bytes a row for keys alone and 402 for a key and a value; it holds 223 and 305.
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public async Task A_filter_view_holds_one_slot_a_key(string what, string sql, long bound)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;
        var orders = Enumerable.Range(0, Orders).Select(i => new Order(i, i % 4, i)).ToArray();
        var source = new PocoSourceBuilder("desk").NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("orders", (IReadOnlyList<Order>)orders.ToList(), out var table, t => t.Keyed(x => x.Id))
            .Build();
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner() }, ct);
        await using var view = await engine.MaterializeAsync(sql, Options(sql.Contains("status FROM", StringComparison.Ordinal)), ct);
        var replica = new Replica();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, replica);
        Assert.Equal(Passing(orders), replica.Keys.Order());

        var random = new Random(283);
        for (var batch = 0; batch < 20; batch++)
        {
            var moved = Enumerable.Range(0, Orders / 10)
                .Select(_ => random.Next(Orders)).Distinct()
                .Select(id => orders[id] = orders[id] with { Status = random.Next(4), Qty = orders[id].Qty + 1 })
                .ToArray();
            await engine.RefreshAsync(r => r.Upsert(table, moved), ct);
            await view.UpdateAsync(ct);
            view.Drain(ref cursor, replica);
            Assert.Equal(Passing(orders), replica.Keys.Order());
        }

        var perRow = view.StateBytes / view.Count;
        output.WriteLine($"{what}: {view.Count:N0} rows, {view.StateBytes:N0} B held, {perRow} B a row");
        Assert.InRange(perRow, 1, bound);
    }

    /// <summary>
    /// Maintained, a key given by two rows faults the view, as a recompute does: two orders of one id
    /// in a view keyed by the id, and one row given twice in a view whose key is the whole row.
    /// </summary>
    [Theory]
    [InlineData("SELECT o.id, p.status FROM orders o JOIN orders p ON p.qty = o.qty", false)]
    [InlineData("SELECT o.id FROM orders o JOIN orders p ON p.qty = o.qty", true)]
    public async Task A_key_given_twice_faults_a_maintained_view(string sql, bool keysOnly)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;
        var orders = Enumerable.Range(0, 100).Select(i => new Order(i, i % 4, i)).ToList();
        var source = new PocoSourceBuilder("desk").NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("orders", (IReadOnlyList<Order>)orders, out var table, t => t.Keyed(x => x.Id))
            .Build();
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner() }, ct);
        await using var view = await engine.MaterializeAsync(sql, Options(!keysOnly), ct);

        // A second order of order 7's quantity: id 7 now joins two rows, and so does id 100.
        await engine.RefreshAsync(r => r.Upsert(table, [new Order(100, 1, 7)]), ct);
        var fault = await Assert.ThrowsAsync<StandingViewException>(async () => await view.UpdateAsync(ct));
        Assert.Equal(ChalkErrorCodes.DeclarationNotHeld, fault.Code);
        Assert.Contains("is given by two rows of the result", fault.Message, StringComparison.Ordinal);
    }

    private static IEnumerable<long> Passing(Order[] orders) => orders.Where(o => o.Status != 3).Select(o => o.Id).Order();

    private static ViewOptions<K, V> Options(bool withStatus) => new()
    {
        Key = ["id"],
        Maintenance = ViewMaintenance.Incremental,
        ResetWhen = double.PositiveInfinity,
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Map = withStatus
            ? static (in ViewRow row, out K key, out V value) =>
            {
                key = new K { Id = row.GetInt64(0) };
                value = new V { Status = row.GetInt64(1) };
            }
            : static (in ViewRow row, out K key, out V value) =>
            {
                key = new K { Id = row.GetInt64(0) };
                value = default;
            },
    };

    private sealed class Replica : IViewSink<K, V>
    {
        public HashSet<long> Keys { get; } = [];

        public void Reset() => Keys.Clear();

        public void Upsert(in K key, in V row) => Keys.Add(key.Id);

        public void Delete(in K key) => Keys.Remove(key.Id);

        public void Commit(long version, uint checksum)
        {
        }
    }
}
