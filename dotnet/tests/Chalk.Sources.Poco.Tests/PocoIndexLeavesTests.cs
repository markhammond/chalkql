using Chalk.Catalog;
using IndexKind = Chalk.Ir.IndexKind;
using SortDirection = Chalk.Ir.SortDirection;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// An append extends an index by what it appends (F202): after every append, the index's order is the
/// order an index built whole over the same rows has — for a key's latest rows, for keys anywhere,
/// for descending keys and NULLs — its distinct counts are the whole one's, and a unique index refuses
/// what the whole one refuses, in the same words.
/// </summary>
public sealed class PocoIndexLeavesTests
{
    private sealed record Mark(int Id, string Symbol, DateTime Ts, int? Lot);

    private static readonly string[] Symbols = ["ACME", "BRIX", "CIRRUS", "DELTA", "ECHO", "FOXTROT"];

    private static readonly DateTime Start = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Every symbol's next mark, one tick on: rows that land at the end of their symbol's run.</summary>
    private static Mark[] Tick(int tick, int from) =>
        [.. Symbols.Select((symbol, i) => new Mark(from + i, symbol, Start.AddSeconds(tick), (from + i) % 5 == 0 ? null : from + i))];

    [Theory]
    [InlineData("time")]
    [InlineData("anywhere")]
    [InlineData("descending")]
    public async Task An_appended_index_is_the_index_built_whole(string shape)
    {
        var random = new Random(11);
        var rows = new List<Mark>();
        for (var tick = 0; tick < 300; tick++)
        {
            rows.AddRange(Tick(tick, rows.Count));
        }

        var source = Source(rows, shape);
        for (var tick = 300; tick < 420; tick++)
        {
            var batch = shape == "anywhere"
                ? [.. Enumerable.Range(0, random.Next(1, 12)).Select(i => new Mark(
                    rows.Count + i, Symbols[random.Next(Symbols.Length)], Start.AddSeconds(random.Next(tick)), random.Next(3) == 0 ? null : random.Next(50)))]
                : Tick(tick, rows.Count);
            rows.AddRange(batch);
            await AppendAsync(source, batch);

            if (tick % 20 == 0)
            {
                AssertBuiltWhole(source, Source(rows, shape));
            }
        }

        AssertBuiltWhole(source, Source(rows, shape));
    }

    /// <summary>
    /// A key's latest rows land at a leaf's end once the run has been cut there, so most appends write
    /// in place: the index holds far fewer leaves than one per append.
    /// </summary>
    [Fact]
    public async Task A_keys_latest_rows_are_written_where_its_run_ends()
    {
        var rows = new List<Mark>();
        for (var tick = 0; tick < 2_000; tick++)
        {
            rows.AddRange(Tick(tick, rows.Count));
        }

        var source = Source(rows, "time");
        for (var tick = 2_000; tick < 2_200; tick++)
        {
            var batch = Tick(tick, rows.Count);
            rows.AddRange(batch);
            await AppendAsync(source, batch);
        }

        var before = Index(source).Positions!;
        var last = Tick(2_200, rows.Count);
        rows.AddRange(last);
        await AppendAsync(source, last);
        var after = Index(source).Positions!;

        // The append wrote into the leaves it found, and made none: every leaf array is the one before.
        Assert.Equal(before.LeafCount, after.LeafCount);
        Assert.Equal(after.LeafCount, Shared(before, after));
        Assert.True(after.LeafCount < 40, $"{after.LeafCount} leaves");
        AssertBuiltWhole(source, Source(rows, "time"));
    }

    /// <summary>Leaves of <paramref name="after"/> whose arrays <paramref name="before"/> held too.</summary>
    private static int Shared(PositionLeaves before, PositionLeaves after)
    {
        var held = new HashSet<int[]>(ReferenceEqualityComparer.Instance);
        for (var leaf = 0; leaf < before.LeafCount; leaf++)
        {
            held.Add(before.ArrayOf(leaf));
        }

        var shared = 0;
        for (var leaf = 0; leaf < after.LeafCount; leaf++)
        {
            shared += held.Contains(after.ArrayOf(leaf)) ? 1 : 0;
        }

        return shared;
    }

    /// <summary>
    /// Rows put anywhere rewrite only the leaves they land in, and the order is still the whole one's:
    /// an append is extended, not built again.
    /// </summary>
    [Fact]
    public async Task Rows_put_anywhere_rewrite_only_the_leaves_they_land_in()
    {
        var random = new Random(5);
        var rows = Enumerable.Range(0, 20_000)
            .Select(i => new Mark(i, Symbols[i % Symbols.Length], Start, random.Next(1_000)))
            .ToList();
        var source = Source(rows, "anywhere");
        var before = Index(source).Positions!;

        Mark[] batch = [.. Enumerable.Range(0, 3).Select(i => new Mark(20_000 + i, "ACME", Start, random.Next(1_000)))];
        rows.AddRange(batch);
        await AppendAsync(source, batch);

        var after = Index(source).Positions!;
        Assert.True(Shared(before, after) >= before.LeafCount - 3, $"{Shared(before, after)} of {before.LeafCount} leaves kept");
        AssertBuiltWhole(source, Source(rows, "anywhere"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_unique_index_refuses_an_appended_key_as_a_whole_build_does(bool againstHeld)
    {
        var rows = Enumerable.Range(0, 50).Select(i => new Mark(i, "ACME", Start.AddSeconds(i), i)).ToList();
        var source = new PocoSourceBuilder("mem").AddTable("marks", rows, t => t.UniqueIndex(x => x.Id)).Build();
        Mark[] batch = againstHeld
            ? [new Mark(60, "B", Start, 1), new Mark(20, "C", Start, 2)]
            : [new Mark(70, "B", Start, 1), new Mark(70, "C", Start, 2)];

        var appended = await Assert.ThrowsAsync<CatalogVerificationException>(() => AppendAsync(source, batch));
        var whole = Assert.Throws<CatalogVerificationException>(
            () => new PocoSourceBuilder("mem").AddTable("marks", [.. rows, .. batch], t => t.UniqueIndex(x => x.Id)).Build());

        Assert.Equal(whole.Message, appended.Message);
    }

    /// <summary>A key that held only NULLs and is appended a value is indexed whole, typed from then on.</summary>
    [Fact]
    public async Task A_key_that_held_only_nulls_takes_its_first_value_whole()
    {
        var rows = Enumerable.Range(0, 30).Select(i => new Mark(i, "ACME", Start, null)).ToList();
        var source = new PocoSourceBuilder("mem").AddTable("marks", rows, t => t.Index(x => x.Lot, x => x.Id)).Build();

        Mark[] batch = [new Mark(30, "ACME", Start, 5), new Mark(31, "ACME", Start, null), new Mark(32, "ACME", Start, 2)];
        await AppendAsync(source, batch);

        var whole = new PocoSourceBuilder("mem").AddTable("marks", [.. rows, .. batch], t => t.Index(x => x.Lot, x => x.Id)).Build();
        Assert.Equal(Positions(whole, "ix_marks_Lot_Id"), Positions(source, "ix_marks_Lot_Id"));
    }

    private static PocoSource Source(IReadOnlyList<Mark> rows, string shape) =>
        new PocoSourceBuilder("mem")
            .AddTable("marks", rows, t => _ = shape switch
            {
                "descending" => t.Index(
                    "ix_marks", IndexKind.Ordered, unique: false,
                    [SortDirection.DescNullsFirst, SortDirection.AscNullsLast], x => x.Lot, x => x.Ts),
                "anywhere" => t.Index("ix_marks", IndexKind.Ordered, unique: false, x => x.Lot, x => x.Symbol),
                _ => t.Index("ix_marks", IndexKind.Ordered, unique: false, x => x.Symbol, x => x.Ts),
            })
            .Build();

    private static PermutationIndex<Mark> Index(PocoSource source, string name = "ix_marks") =>
        (PermutationIndex<Mark>)source.FindIndex<Mark>("marks", name)!;

    private static int[] Positions(PocoSource source, string name) =>
        Index(source, name).Positions?.ToArray() ?? [];

    /// <summary>The order, the distinct counts and the walks of an appended index, against one built whole.</summary>
    private static void AssertBuiltWhole(PocoSource appended, PocoSource whole)
    {
        var (mine, theirs) = (Index(appended), Index(whole));
        Assert.Equal(Positions(whole, "ix_marks"), Positions(appended, "ix_marks"));
        Assert.Equal(theirs.DistinctCount(1), mine.DistinctCount(1));
        Assert.Equal(theirs.DistinctCount(2), mine.DistinctCount(2));

        var count = mine.RowCount;
        Assert.Equal(
            Enumerable.Range(0, count).Select(theirs.GetPosition),
            mine.PositionsBetween(0, count, reverse: false));
        Assert.Equal(
            Enumerable.Range(count / 3, count / 3).Reverse().Select(theirs.GetPosition),
            mine.PositionsBetween(count / 3, 2 * (count / 3), reverse: true));
    }

    private static async Task AppendAsync(PocoSource source, IReadOnlyList<Mark> rows)
    {
        var commit = await ((IRefreshableSource)source).PrepareRefreshAsync(
            [
                new SourceRefreshEntry
                {
                    Table = "marks",
                    Kind = SourceRefreshKind.Append,
                    RowType = typeof(Mark),
                    Rows = rows,
                },
            ],
            TestContext.Current.CancellationToken);
        commit.Commit();
    }
}
