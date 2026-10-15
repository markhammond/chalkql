using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Execution.Maintenance;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A maintained join whose left side is a table under an access policy, read through filters and
/// projections, with an index that begins with the join's keys (D361, design 74 §6): it holds its right
/// side alone, and seeks the left rows of a key through the index when a right row of the key arrives or
/// leaves. Inner, left, semi and anti joins, each held to the statement through random epochs, and the
/// state and the reads that follow the right side rather than the table.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class SeekingJoinTests(SharedSidecar sidecar)
{
    private sealed record Item(long Seq, int? KindId, string Name);

    private sealed record Kind(int Id, string Label);

    /// <summary>The four joins, each with the left side a table under a policy, whose rows it seeks.</summary>
    public static TheoryData<string, string> Statements() => new()
    {
        { "inner", "SELECT i.seq, g.label FROM items i JOIN kinds g ON i.kind_id = g.id" },
        { "left", "SELECT i.seq, g.label FROM items i LEFT JOIN kinds g ON i.kind_id = g.id" },
        { "semi", "SELECT i.seq, i.name FROM items i WHERE i.kind_id IN (SELECT id FROM kinds)" },
        { "anti", "SELECT i.seq, i.name FROM items i WHERE NOT EXISTS (SELECT 1 FROM kinds g WHERE g.id = i.kind_id)" },
    };

    /// <summary>
    /// Each join, with and without an index on its left key, kept over 40 random epochs that add, move
    /// and take out items and kinds, is the statement after every one. With the index it seeks; without
    /// it, it holds the table as before.
    /// </summary>
    [Theory]
    [MemberData(nameof(Statements))]
    public async Task Every_epoch_a_seeking_join_is_the_statement(string name, string sql)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        _ = name;
        foreach (var indexed in new[] { true, false })
        {
            foreach (var seed in new[] { 5, 17 })
            {
                await RunAsync(sql, indexed, seed);
            }
        }
    }

    /// <summary>
    /// Five thousand items in three kinds, kept by a left join to the kinds: with an index on the key
    /// the join holds the three kinds and none of the items, without it every item.
    /// </summary>
    [Fact]
    public async Task A_seeking_join_holds_its_right_side_and_not_the_table()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var items = Enumerable.Range(1, 5_000).Select(i => new Item(i, i % 3, $"item-{i}")).ToList();
        Kind[] kinds = [new(0, "zero"), new(1, "one"), new(2, "two")];
        const string Sql = "SELECT i.seq, g.label FROM items i LEFT JOIN kinds g ON i.kind_id = g.id";

        long seeking;
        await using (var desk = await DeskAsync(items, kinds, indexed: true))
        await using (var maintained = await MaintainAsync(desk, Sql))
        {
            await maintained.UpdateAsync([], default, desk.Engine.Version, whole: false, Ct);
            var join = Assert.Single(maintained.States.OfType<JoinSeekState>());
            Assert.Empty(maintained.States.OfType<JoinState>());
            Assert.Equal(3, join.Right.Rows.Count);
            seeking = join.Bytes;
        }

        long holding;
        await using (var desk = await DeskAsync(items, kinds, indexed: false))
        await using (var maintained = await MaintainAsync(desk, Sql))
        {
            await maintained.UpdateAsync([], default, desk.Engine.Version, whole: false, Ct);
            Assert.Empty(maintained.States.OfType<JoinSeekState>());
            var join = Assert.Single(maintained.States.OfType<JoinState>());
            Assert.Equal(5_000, join.Left.Rows.Count);
            holding = join.Bytes;
        }

        Assert.True(seeking * 20 < holding, $"the seeking join holds {seeking} bytes, the holding join {holding}");
    }

    /// <summary>
    /// A kind that arrives is one key sought, and its items the only rows read; a kind taken out, the
    /// same. An item that arrives is read against the kinds held, and seeks nothing.
    /// </summary>
    [Fact]
    public async Task A_right_change_seeks_only_its_key()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var items = Enumerable.Range(1, 3_000).Select(i => new Item(i, i % 30, $"item-{i}")).ToList();
        await using var desk = await DeskAsync(items, [new(1, "one")], indexed: true);
        const string Sql = "SELECT i.seq, i.name FROM items i WHERE i.kind_id IN (SELECT id FROM kinds)";
        await using var maintained = await MaintainAsync(desk, Sql);
        var join = Assert.Single(maintained.States.OfType<JoinSeekState>());
        var held = new Dictionary<long, object?>();
        Apply(await maintained.UpdateAsync([], default, desk.Engine.Version, whole: false, Ct), held);
        Assert.Equal(100, held.Count);
        Assert.Equal(0, join.Sought);

        await desk.Engine.RefreshAsync(r => r.Upsert(desk.Source, "kinds", (IReadOnlyList<Kind>)[new(7, "seven")]), Ct);
        Apply(await maintained.UpdateAsync([], default, desk.Engine.Version, whole: false, Ct), held);
        Assert.Equal((1, 100L), (join.Sought, join.Read));
        Assert.Equal(200, held.Count);

        await desk.Engine.RefreshAsync(r => r.Delete(desk.Source, "kinds", (IReadOnlyList<Kind>)[new(1, "one")]), Ct);
        Apply(await maintained.UpdateAsync([], default, desk.Engine.Version, whole: false, Ct), held);
        Assert.Equal((1, 100L), (join.Sought, join.Read));
        Assert.Equal(100, held.Count);
        Assert.All(held.Keys, seq => Assert.Equal(7, seq % 30));

        await desk.Engine.RefreshAsync(r => r.Upsert(desk.Source, "items", (IReadOnlyList<Item>)[new(3_001, 7, "new")]), Ct);
        Apply(await maintained.UpdateAsync([], default, desk.Engine.Version, whole: false, Ct), held);
        Assert.Equal(0, join.Sought);
        Assert.Equal(101, held.Count);
        Assert.Equal(await StatementAsync(desk, Sql), held);
    }

    /// <summary>Keeps the statement through 40 random epochs, comparing it with the statement after each.</summary>
    private async Task RunAsync(string sql, bool indexed, int seed)
    {
        var random = new Random(seed);
        var items = new Dictionary<long, Item>();
        var kinds = new Dictionary<int, Kind>();
        var seq = 0L;
        for (var i = 0; i < 40; i++)
        {
            items[++seq] = NewItem(random, seq);
        }

        foreach (var id in new[] { 1, 2, 3 })
        {
            kinds[id] = new Kind(id, $"kind-{id}");
        }

        await using var desk = await DeskAsync([.. items.Values], [.. kinds.Values], indexed);
        await using var maintained = await MaintainAsync(desk, sql);
        Assert.Equal(indexed, maintained.States.OfType<JoinSeekState>().Any());
        var held = new Dictionary<long, object?>();
        for (var epoch = 0; epoch < 40; epoch++)
        {
            if (epoch > 0)
            {
                var upserted = new Dictionary<long, Item>();
                var deleted = new List<Item>();
                var added = new List<Kind>();
                var removed = new List<Kind>();
                for (var i = random.Next(4); i > 0; i--)
                {
                    upserted[++seq] = items[seq] = NewItem(random, seq);
                }

                for (var i = random.Next(4); i > 0; i--)
                {
                    var item = items.Values.ElementAt(random.Next(items.Count));
                    upserted[item.Seq] = items[item.Seq] = item with { KindId = NewKindId(random), Name = $"item-{item.Seq}-{epoch}" };
                }

                if (random.Next(3) == 0 && items.Count > 5)
                {
                    var item = items.Values.ElementAt(random.Next(items.Count));
                    items.Remove(item.Seq);
                    upserted.Remove(item.Seq);
                    deleted.Add(item);
                }

                // A kind taken out or brought in: its items stop or start meeting it.
                if (random.Next(2) == 0 && kinds.Count > 0)
                {
                    var kind = kinds.Values.ElementAt(random.Next(kinds.Count));
                    kinds.Remove(kind.Id);
                    removed.Add(kind);
                }

                if (random.Next(2) == 0)
                {
                    var id = 1 + random.Next(6);
                    if (!kinds.ContainsKey(id) && !removed.Any(g => g.Id == id))
                    {
                        added.Add(kinds[id] = new Kind(id, $"kind-{id}-{epoch}"));
                    }
                }

                await desk.Engine.RefreshAsync(
                    r =>
                    {
                        if (upserted.Count > 0)
                        {
                            r.Upsert(desk.Source, "items", (IReadOnlyList<Item>)[.. upserted.Values]);
                        }

                        if (deleted.Count > 0)
                        {
                            r.Delete(desk.Source, "items", (IReadOnlyList<Item>)deleted);
                        }

                        if (removed.Count > 0)
                        {
                            r.Delete(desk.Source, "kinds", (IReadOnlyList<Kind>)removed);
                        }

                        if (added.Count > 0)
                        {
                            r.Upsert(desk.Source, "kinds", (IReadOnlyList<Kind>)added);
                        }
                    },
                    Ct);
            }

            var update = await maintained.UpdateAsync([], default, desk.Engine.Version, whole: random.Next(10) == 0, Ct);
            Apply(update, held);
            var expected = await StatementAsync(desk, sql);
            Assert.True(
                expected.Count == held.Count && expected.All(e => held.TryGetValue(e.Key, out var mine) && Equals(mine, e.Value)),
                $"{sql}, indexed {indexed}, seed {seed}, epoch {epoch}: held [{Describe(held)}], expected [{Describe(expected)}]");
        }
    }

    private static Item NewItem(Random random, long seq) => new(seq, NewKindId(random), $"item-{seq}");

    /// <summary>A kind of 1 to 6, or none: kinds 4 to 6 come and go, and an item with none meets nothing.</summary>
    private static int? NewKindId(Random random) => random.Next(8) switch
    {
        0 => null,
        var n => 1 + (n % 6),
    };

    private static string Describe(Dictionary<long, object?> rows) =>
        string.Join(", ", rows.OrderBy(r => r.Key).Select(r => $"{r.Key}:{r.Value ?? "NULL"}"));

    private static void Apply(MaintainedUpdate update, Dictionary<long, object?> held)
    {
        if (update.Whole)
        {
            held.Clear();
        }

        foreach (var (key, _) in Rows(update.Before))
        {
            held.Remove(key);
        }

        foreach (var (key, value) in Rows(update.Now))
        {
            held[key] = value;
        }
    }

    /// <summary>Each row as its key, the first column, and its second.</summary>
    private static IEnumerable<(long Key, object? Value)> Rows(Apache.Arrow.RecordBatch batch)
    {
        for (var i = 0; i < batch.Length; i++)
        {
            yield return (
                Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(0), i)),
                Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(1), i));
        }
    }

    private static async Task<Dictionary<long, object?>> StatementAsync(Desk desk, string sql)
    {
        var statement = await desk.Entitled.PrepareAsync(sql, Floor, ct: Ct);
        var rows = new Dictionary<long, object?>();
        await using var execution = await desk.Engine.ExecuteAsync(statement, ct: Ct);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                foreach (var (key, value) in Rows(batch))
                {
                    rows.Add(key, value);
                }
            }
        }

        return rows;
    }

    private static async Task<MaintainedQuery> MaintainAsync(Desk desk, string sql)
    {
        var statement = await desk.Entitled.PrepareAsync(sql, Floor, ct: Ct);
        return await statement.Query.MaintainAsync([], -1, [0], Ct);
    }

    /// <summary>The policy's one value: every item has a sequence number above it.</summary>
    private static RequestContext Floor => new()
    {
        Scalars = new Dictionary<string, object?>(StringComparer.Ordinal) { ["floor"] = 0L },
        ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal) { ["floor"] = ChalkType.Int64() },
    };

    private sealed class Desk : IAsyncDisposable
    {
        public required ChalkEngine Engine { get; init; }

        public required EntitledEngine Entitled { get; init; }

        public required PocoSource Source { get; init; }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    /// <summary>Items under a policy that keeps the items above a floor, every one here, and kinds under none.</summary>
    private async Task<Desk> DeskAsync(IReadOnlyList<Item> items, IReadOnlyList<Kind> kinds, bool indexed)
    {
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("items", items, t =>
            {
                t.Keyed(x => x.Seq);
                if (indexed)
                {
                    t.Index(x => x.KindId);
                }

                t.Entitlement(new TableEntitlementDescriptor { RowPredicate = "seq > @ctx.floor" });
            })
            .AddTable("kinds", kinds, t => t.Keyed(x => x.Id))
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner() }, Ct);
        return new Desk { Engine = engine, Entitled = engine.WithEntitlements(new EntitlementsOptions()), Source = source };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
