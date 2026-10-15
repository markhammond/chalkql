using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Incremental maintenance against its oracle, operator by operator (D343,
/// <c>docs/design/68-incremental-maintenance.md</c> §9): each statement is kept by maintenance while
/// random epochs append, upsert, delete, rewrite and move the clock, and after every epoch the result
/// it holds is what the statement returns — exactly, but for floating-point sums, which a maintained
/// view keeps exactly and a recompute adds in input order.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class MaintainedStatementTests(SharedSidecar sidecar)
{
    private sealed record Trade(int Seq, int Book, int? Asset, long Qty, double Price, DateTime Ts);

    private sealed record Mark(int Asset, DateTime Ts, double Px, int Tag);

    private sealed record Book(int Id, string Name, string? Region);

    private sealed record Limit(int Book, int Asset, long Lim);

    private static readonly DateTime Open = new(2026, 10, 13, 9, 0, 0);

    private static readonly string[] Names = ["alpha", "beta", "gamma", "delta"];

    /// <summary>The statements, each with its key columns.</summary>
    public static TheoryData<string, string, int[]> Statements() => new()
    {
        {
            "inner join, sums, counts, extremes",
            """
            SELECT b.name, t.asset, SUM(t.qty) AS q, COUNT(*) AS n, MIN(t.price) AS lo, MAX(t.price) AS hi
            FROM trades t JOIN books b ON b.id = t.book
            GROUP BY b.name, t.asset
            """,
            [0, 1]
        },
        {
            "left join with a condition and NULLs",
            """
            SELECT b.id, b.name, b.region, COUNT(t.seq) AS n, SUM(t.qty) AS q
            FROM books b LEFT JOIN trades t ON t.book = b.id AND t.qty > 0
            GROUP BY b.id, b.name, b.region
            """,
            [0]
        },
        {
            "left as-of join, ties and NULL keys",
            """
            SELECT t.seq, t.asset, m.px, m.tag
            FROM trades t LEFT ASOF JOIN marks m MATCH_CONDITION m.ts <= t.ts ON m.asset = t.asset
            """,
            [0]
        },
        {
            "inner as-of join looking forward",
            """
            SELECT t.seq, m.px, m.tag
            FROM trades t ASOF JOIN marks m MATCH_CONDITION m.ts > t.ts ON m.asset = t.asset
            """,
            [0]
        },
        {
            "rollup with floating-point sums",
            """
            SELECT CASE WHEN GROUPING(asset) = 1 THEN -1 ELSE COALESCE(asset, -2) END AS a,
                   CASE WHEN GROUPING(book) = 1 THEN -1 ELSE book END AS b,
                   SUM(price * qty) AS notional, COUNT(*) AS n, MIN(asset) AS lo
            FROM trades
            GROUP BY ROLLUP(asset, book)
            """,
            [0, 1]
        },
        {
            "union all under an aggregate",
            """
            SELECT k, SUM(v) AS total, COUNT(*) AS n
            FROM (SELECT asset AS k, qty AS v FROM trades UNION ALL SELECT asset, lim FROM limits) u
            GROUP BY k
            """,
            [0]
        },
        {
            "a shared subtree read twice",
            """
            WITH s AS (SELECT book, SUM(qty) AS q FROM trades GROUP BY book)
            SELECT a.book, a.q, b.q2 FROM s a JOIN (SELECT book, q * 2 AS q2 FROM s) b ON a.book = b.book
            """,
            [0]
        },
        {
            "a filter on the clock",
            """
            SELECT asset, COUNT(*) AS n, SUM(qty) AS q FROM trades
            WHERE ts <= CAST(@now AS TIMESTAMP(9))
            GROUP BY asset
            """,
            [0]
        },
        {
            "having over string extremes",
            """
            SELECT t.asset, MIN(b.name) AS first_book, MAX(b.name) AS last_book, COUNT(*) AS n
            FROM trades t JOIN books b ON b.id = t.book
            GROUP BY t.asset
            HAVING COUNT(*) > 2
            """,
            [0]
        },
        {
            "the clock in a constant, crossed with a table",
            """
            WITH d AS (SELECT CAST(@now AS TIMESTAMP(9)) AS now FROM (VALUES (1)) AS x (k))
            SELECT t.book, COUNT(*) AS n
            FROM d CROSS JOIN trades t
            WHERE t.ts <= d.now
            GROUP BY t.book
            """,
            [0]
        },
        {
            "an as-of seek with a fixed key",
            """
            SELECT t.seq, m.px, m.tag
            FROM trades t ASOF JOIN marks m MATCH_CONDITION m.ts <= t.ts ON m.asset = 1
            """,
            [0]
        },
        {
            "a one-row table crossed in, keying an as-of join under an aggregate",
            """
            WITH pinned AS (SELECT 2 AS asset)
            SELECT t.book, COUNT(*) AS n, SUM(m.px) AS px, MAX(m.tag) AS tag
            FROM trades t CROSS JOIN pinned p ASOF JOIN marks m MATCH_CONDITION m.ts <= t.ts ON m.asset = p.asset
            GROUP BY t.book
            """,
            [0]
        },
        {
            "a three-way join with a keyed table",
            """
            SELECT t.seq, b.name, l.lim, t.qty * l.lim AS exposure
            FROM trades t JOIN books b ON b.id = t.book JOIN limits l ON l.book = t.book AND l.asset = t.asset
            """,
            [0]
        },
        {
            // A group whose rows all fail a filter stays, its sum NULL and its count 0; the asset is NULL
            // on some rows, so `asset = 2` is NULL there and the row is not counted.
            "aggregate filters, NULL in what they test",
            """
            SELECT book, SUM(qty) FILTER (WHERE qty > 0) AS bought,
                   COUNT(*) FILTER (WHERE asset IS NULL) AS unassigned,
                   MIN(price) FILTER (WHERE asset = 2) AS lo2, MAX(price) FILTER (WHERE asset = 2) AS hi2
            FROM trades
            GROUP BY book
            """,
            [0]
        },
        {
            "a CASE inside an aggregate, which the planner makes its filter",
            """
            SELECT asset, SUM(CASE WHEN qty > 0 THEN qty END) AS bought,
                   MIN(CASE WHEN book = 1 THEN ts END) AS first_alpha, COUNT(*) AS n
            FROM trades
            GROUP BY asset
            """,
            [0]
        },
        {
            // A book upserted between a region and none moves its trades in and out of both filters.
            "filters on a joined row an upsert flips",
            """
            SELECT t.asset, COUNT(*) FILTER (WHERE b.region IS NULL) AS unplaced,
                   SUM(t.qty) FILTER (WHERE b.region = 'apac') AS apac
            FROM trades t JOIN books b ON b.id = t.book
            GROUP BY t.asset
            """,
            [0]
        },
        {
            "a rollup with filters",
            """
            SELECT CASE WHEN GROUPING(book) = 1 THEN -1 ELSE book END AS b,
                   COUNT(*) FILTER (WHERE price > 500) AS dear, SUM(qty) FILTER (WHERE price > 500) AS dear_qty
            FROM trades
            GROUP BY ROLLUP(book)
            """,
            [0]
        },
    };

    /// <summary>
    /// The planner writes a <c>CASE</c> inside an aggregate as the aggregate's <c>FILTER</c>, <c>IS
    /// TRUE</c> of a column it projects, as it writes one written as a <c>FILTER</c>: so whether a
    /// statement is maintained cannot depend on which of the two it was written as, or on where a
    /// <c>CASE</c> lands once the planner has merged its projections.
    /// </summary>
    [Fact]
    public async Task A_case_inside_an_aggregate_is_planned_as_its_filter_and_maintained()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var random = new Random(7);
        var trades = Enumerable.Range(1, 12).Select(i => NewTrade(random, i, Open.AddSeconds(i))).ToList();
        var desk = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("trades", (IReadOnlyList<Trade>)trades, t => t.OrderedBy(x => x.Ts))
            .Build();
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [desk], Planner = sidecar.CreatePlanner() }, Ct);

        var prepared = await engine.PrepareAsync(
            "SELECT asset, SUM(CASE WHEN qty > 0 THEN qty END) AS bought FROM trades GROUP BY asset", ct: Ct);

        // The sum of a CASE becomes a filtered SUM0 and a filtered COUNT, which say whether it is NULL.
        var filters = PlanWalker.Rels(prepared.Plan)
            .Select(rel => rel.KindCase switch
            {
                Rel.KindOneofCase.Aggregate => rel.Aggregate,
                Rel.KindOneofCase.HashAggregate => rel.HashAggregate.Aggregate,
                Rel.KindOneofCase.StreamAggregate => rel.StreamAggregate.Aggregate,
                _ => null,
            })
            .OfType<Aggregate>()
            .SelectMany(aggregate => aggregate.Measures)
            .Where(measure => measure.Filter is not null)
            .Select(measure => measure.Filter)
            .ToList();
        Assert.NotEmpty(filters);
        Assert.All(filters, filter =>
        {
            Assert.Equal(FunctionId.IsTrue, filter.Call.Function);
            Assert.Equal(Expr.KindOneofCase.FieldRef, Assert.Single(filter.Call.Args).KindCase);
        });
        await using var maintained = await prepared.MaintainAsync([], -1, [0], Ct);
    }

    [Theory]
    [MemberData(nameof(Statements))]
    public async Task Every_epoch_the_maintained_result_is_the_statement(string name, string sql, int[] key)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        _ = name;
        foreach (var seed in new[] { 3, 17 })
        {
            await RunAsync(sql, key, seed);
        }
    }

    /// <summary>
    /// Statements over tables large enough that the planner seeks an index for an as-of join — back and
    /// forward, strict and not, with a fixed key, on an index whose key goes on past its time, at the
    /// clock, and with left rows that move with the clock — hashes a left join, and looks keys up in an
    /// index, under an as-of join and for a filter: each plan is checked to be what it means to test.
    /// </summary>
    [Theory]
    [InlineData(
        "an as-of seek, ties and NULL keys",
        "SELECT t.seq, t.asset, m.px, m.tag FROM trades t LEFT ASOF JOIN marks m MATCH_CONDITION m.ts <= t.ts ON m.asset = t.asset",
        "AsOfLookupJoin",
        false)]
    [InlineData(
        "an as-of seek with a fixed key",
        "SELECT t.seq, m.px, m.tag FROM trades t ASOF JOIN marks m MATCH_CONDITION m.ts <= t.ts ON m.asset = 2",
        "AsOfLookupJoin fixed",
        false)]
    [InlineData(
        "an as-of seek forward, ties",
        "SELECT t.seq, t.asset, m.px, m.tag FROM trades t LEFT ASOF JOIN marks m MATCH_CONDITION m.ts >= t.ts ON m.asset = t.asset",
        "AsOfLookupJoin",
        false)]
    [InlineData(
        "a strict as-of seek on an index going on past its time",
        "SELECT t.seq, t.asset, m.px, m.tag FROM trades t ASOF JOIN marks m MATCH_CONDITION m.ts < t.ts ON m.asset = t.asset",
        "AsOfLookupJoin",
        true)]
    [InlineData(
        "a strict as-of seek forward on an index going on past its time",
        "SELECT t.seq, t.asset, m.px, m.tag FROM trades t LEFT ASOF JOIN marks m MATCH_CONDITION m.ts > t.ts ON m.asset = t.asset",
        "AsOfLookupJoin",
        true)]
    [InlineData(
        "an as-of seek at the clock, with marks after it",
        "SELECT x.seq, x.asset, m.px, m.tag FROM (SELECT seq, asset, CAST(@now AS TIMESTAMP(9)) AS upto FROM trades) x LEFT ASOF JOIN marks m MATCH_CONDITION m.ts <= x.upto ON m.asset = x.asset",
        "AsOfLookupJoin",
        false)]
    [InlineData(
        "a strict as-of seek whose left rows move with the clock",
        "SELECT x.seq, x.seen, m.px, m.tag FROM (SELECT seq, asset, ts, CAST(@now AS TIMESTAMP(9)) AS seen FROM trades) x ASOF JOIN marks m MATCH_CONDITION m.ts < x.ts ON m.asset = x.asset",
        "AsOfLookupJoin",
        true)]
    [InlineData(
        "a hashed left join with a condition",
        "SELECT t.seq, l.lim FROM trades t LEFT JOIN limits l ON l.book = t.book AND l.asset = t.asset AND l.lim > t.qty",
        "HashJoin Left",
        false)]
    [InlineData(
        "an as-of join over a lookup of one key, ties",
        "SELECT t.seq, m.px, m.tag FROM trades t ASOF JOIN marks m MATCH_CONDITION m.ts <= t.ts ON m.asset = 2",
        "AsOfJoin over IndexLookup",
        false)]
    [InlineData(
        "a lookup of keys, upserted",
        "SELECT l.book * 1000 + l.asset AS k, l.lim FROM limits l WHERE l.book = 2 AND l.asset IN (1, 3)",
        "IndexLookup",
        false)]
    public async Task Every_epoch_a_seek_or_a_hashed_join_is_the_statement(string name, string sql, string planned, bool tagged)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        _ = name;
        foreach (var seed in new[] { 5, 29 })
        {
            // A seek is chosen for a few left rows over many right ones; a hash join, or a whole as-of
            // join over a lookup, for many each side.
            var seek = planned.StartsWith("AsOfLookupJoin", StringComparison.Ordinal);
            await RunAsync(sql, [0], seed, initial: seek ? 12 : 1_500, planned, marksAtStart: seek ? 4_000 : 0, tagged);
        }
    }

    /// <summary>
    /// Lookups maintenance does not keep (design 68 §19): one over a range of keys; and one under an
    /// as-of join whose index orders the rows of one key and time by a column the join does not read,
    /// so its ties are not the table's order.
    /// </summary>
    [Theory]
    [InlineData(
        "SELECT l.book * 1000 + l.asset AS k, l.lim FROM limits l WHERE l.book = 2 AND l.asset BETWEEN 5 AND 9",
        "IndexLookup",
        false,
        "an index lookup over a range of keys")]
    [InlineData(
        "SELECT t.seq, m.px, m.tag FROM trades t ASOF JOIN marks m MATCH_CONDITION m.ts <= t.ts ON m.asset = 2",
        "AsOfJoin over IndexLookup",
        true,
        "key the join on both sides")]
    public async Task A_lookup_maintenance_does_not_keep_is_refused(string sql, string planned, bool tagged, string says)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await RunAsync(sql, [0], 5, initial: 1_500, planned, marksAtStart: 0, tagged, refused: says);
    }

    private async Task RunAsync(
        string sql,
        int[] key,
        int seed,
        int initial = 12,
        string? planned = null,
        int marksAtStart = 0,
        bool tagged = false,
        string? refused = null)
    {
        var random = new Random(seed);
        var trades = new List<Trade>();
        var marks = new List<Mark>();
        var books = new Dictionary<int, Book> { [1] = new(1, "alpha", "emea"), [2] = new(2, "beta", null) };
        var limits = new Dictionary<(int, int), Limit> { [(1, 1)] = new(1, 1, 10) };
        var seq = 0;
        var tag = 0;
        var frontier = Open;
        var now = Open;
        for (var i = 0; i < initial; i++)
        {
            frontier = frontier.AddSeconds(random.Next(3));
            trades.Add(NewTrade(random, ++seq, frontier));
            marks.Add(new Mark(1 + random.Next(3), frontier, Math.Round(random.NextDouble() * 100, 3), ++tag));
        }

        // Marks of the past, before the first trade; some at one time, for ties.
        var past = Open.AddHours(-1);
        for (var i = 0; i < marksAtStart; i++)
        {
            past = past.AddMilliseconds(random.Next(3) * 300);
            marks.Add(new Mark(1 + random.Next(3), past, Math.Round(random.NextDouble() * 100, 3), ++tag));
        }

        if (initial > 12)
        {
            // Enough limits that a few keys of them are looked up in their index rather than scanned.
            for (var i = 0; i < 2_000; i++)
            {
                var limit = new Limit(1 + random.Next(3), 1 + random.Next(1_000), random.Next(1, 50));
                limits[(limit.Book, limit.Asset)] = limit;
            }
        }

        marks = [.. marks.OrderBy(m => m.Ts)];
        var desk = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("trades", (IReadOnlyList<Trade>)trades, t => t.OrderedBy(x => x.Ts))
            .AddTable("marks", (IReadOnlyList<Mark>)marks, t =>
            {
                t.OrderedBy(x => x.Ts);
                if (tagged)
                {
                    t.Index(x => x.Asset, x => x.Ts, x => x.Tag);
                }
                else
                {
                    t.Index(x => x.Asset, x => x.Ts);
                }
            })
            .AddTable("books", (IReadOnlyList<Book>)[.. books.Values], t => t.Keyed(x => x.Id))
            .AddTable("limits", (IReadOnlyList<Limit>)[.. limits.Values], t =>
            {
                t.Keyed(x => x.Book, x => x.Asset);
                t.Index(x => x.Book, x => x.Asset);
            })
            .Build();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [desk],
            Planner = sidecar.CreatePlanner(),
            Clock = "now",
        }, Ct);
        await engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(now, TimeSpan.Zero)), Ct);
        var prepared = await engine.PrepareAsync(sql, ct: Ct);
        if (planned is not null)
        {
            Assert.True(Plans(prepared.Plan, planned), $"the statement is not planned with {planned}: {prepared.Plan.Root}");
        }
        var clock = Enumerable.Range(0, prepared.Parameters.Count)
            .FirstOrDefault(i => string.Equals(prepared.Parameters[i].Name?.TrimStart('@'), "now", StringComparison.OrdinalIgnoreCase), -1);
        if (refused is not null)
        {
            var refusal = await Assert.ThrowsAsync<NotMaintainableException>(async () =>
                await prepared.MaintainAsync(Values(prepared, now), clock, key, Ct));
            Assert.Contains(refused, refusal.Message, StringComparison.Ordinal);
            return;
        }

        await using var maintained = await prepared.MaintainAsync(Values(prepared, now), clock, key, Ct);
        var held = new Dictionary<string, object?[]>(StringComparer.Ordinal);

        // A compaction of the marks prepared off the refresh lock (design 69), committed an epoch later.
        PreparedReplacement? pending = null;
        List<Mark> pendingRows = [];
        var pendingCut = 0;

        for (var epoch = 0; epoch < 30; epoch++)
        {
            if (pending is not null)
            {
                // The rows appended since its cut go onto it as it commits, and a maintained read takes
                // the commit as the rows it took out, not as a rewrite: its rows are the same objects.
                var commit = pending;
                pending = null;
                marks = [.. pendingRows, .. marks.Skip(pendingCut)];
                var pinnable = (Chalk.Sources.IPinnableSource)desk;
                var before = pinnable.Pin("marks")!;
                await engine.RefreshAsync(r => r.Commit(commit), Ct);
                commit.Dispose();
                var after = pinnable.Pin("marks")!;
                var diff = ((Chalk.Sources.ITrackedSource)desk).DiffFrom("marks", before, after);
                Assert.True(diff is { Removed.Length: > 0 }, "a committed compaction is read as the rows it took out");
                pinnable.Unpin("marks", before);
                pinnable.Unpin("marks", after);
            }
            else if (epoch > 0)
            {
                // Every run replaces each table at least twice: once compacted, keeping its rows' objects in
                // their order, which a view reads as the rows taken out and appended; and once by copies,
                // which it reads as a rewrite.
                var roll = epoch switch { 10 => 0, 15 => 2, 20 => 1, 22 => 5, 25 => 3, 27 => 4, _ => random.Next(20) };
                if (roll == 0)
                {
                    // A compaction: the oldest third of the trades gone.
                    trades = [.. trades.Skip(trades.Count / 3)];
                    var kept = trades;
                    await engine.RefreshAsync(r => r.Replace(desk, "trades", (IReadOnlyList<Trade>)kept), Ct);
                }
                else if (roll == 1 && marks.Count > 8)
                {
                    // The marks compacted as a host compacts a log: every other one of the older half gone,
                    // and new marks appended in the same replacement.
                    var half = marks.Count / 2;
                    var latest = marks[^1].Ts;
                    var fresh = Enumerable.Range(0, 1 + random.Next(3))
                        .Select(_ => new Mark(1 + random.Next(3), latest.AddSeconds(random.Next(2)), Math.Round(random.NextDouble() * 100, 3), ++tag))
                        .OrderBy(m => m.Ts);
                    marks = [.. marks.Where((_, i) => i >= half || i % 2 == 0), .. fresh];
                    var kept = marks;
                    await engine.RefreshAsync(r => r.Replace(desk, "marks", (IReadOnlyList<Mark>)kept), Ct);
                }
                else if (roll == 2)
                {
                    // A rewrite: a quarter of the trades gone and the rest copied, which no diff follows.
                    trades = [.. trades.Skip(trades.Count / 4).Select(t => t with { })];
                    var kept = trades;
                    await engine.RefreshAsync(r => r.Replace(desk, "trades", (IReadOnlyList<Trade>)kept), Ct);
                }
                else if (roll == 3 && marks.Count > 8)
                {
                    marks = [.. marks.Skip(marks.Count / 4).Select(m => m with { })];
                    var kept = marks;
                    await engine.RefreshAsync(r => r.Replace(desk, "marks", (IReadOnlyList<Mark>)kept), Ct);
                }
                else if (roll == 5 && marks.Count(m => m.Asset == marks[^1].Asset) > 1)
                {
                    // A compaction that takes out an asset's newest mark, which the latest trades match:
                    // a removal that moves matches, unlike the compactions above.
                    var newest = marks.Count - 1;
                    marks = [.. marks.Where((_, i) => i != newest)];
                    var kept = marks;
                    await engine.RefreshAsync(r => r.Replace(desk, "marks", (IReadOnlyList<Mark>)kept), Ct);
                }
                else if (roll == 4 && marks.Count > 8)
                {
                    // A compaction prepared off the lock: every other one of the older half gone. A mark
                    // is appended while it waits, and it commits at the next epoch.
                    pendingCut = marks.Count;
                    var half = pendingCut / 2;
                    pendingRows = [.. marks.Where((_, i) => i >= half || i % 2 == 0)];
                    pending = await engine.PrepareReplaceAsync(desk, "marks", (IReadOnlyList<Mark>)pendingRows, pendingCut, Ct);
                    List<Mark> since = [new Mark(1 + random.Next(3), marks[^1].Ts, Math.Round(random.NextDouble() * 100, 3), ++tag)];
                    marks.AddRange(since);
                    await engine.RefreshAsync(r => r.Append(desk, "marks", (IReadOnlyList<Mark>)since), Ct);
                }
                else
                {
                    var appended = new List<Trade>();
                    for (var i = random.Next(4); i > 0; i--)
                    {
                        frontier = frontier.AddSeconds(random.Next(3));
                        appended.Add(NewTrade(random, ++seq, frontier));
                    }

                    var newMarks = new List<Mark>();
                    for (var i = random.Next(3); i > 0; i--)
                    {
                        // Sometimes at the newest mark's time, for a tie.
                        var at = random.Next(3) == 0 && marks.Count > 0 ? marks[^1].Ts : frontier;
                        newMarks.Add(new Mark(1 + random.Next(3), at < marks[^1].Ts ? marks[^1].Ts : at, Math.Round(random.NextDouble() * 100, 3), ++tag));
                    }

                    newMarks = [.. newMarks.OrderBy(m => m.Ts)];
                    var bookUpserts = new List<Book>();
                    var bookDeletes = new List<Book>();
                    if (random.Next(3) == 0)
                    {
                        var id = 1 + random.Next(4);
                        if (random.Next(3) == 0 && books.Remove(id, out var gone))
                        {
                            bookDeletes.Add(gone);
                        }
                        else
                        {
                            var book = new Book(id, Names[random.Next(4)], random.Next(3) == 0 ? null : "apac");
                            books[id] = book;
                            bookUpserts.Add(book);
                        }
                    }

                    var limitUpserts = new List<Limit>();
                    if (random.Next(3) == 0)
                    {
                        var limit = new Limit(1 + random.Next(3), 1 + random.Next(3), random.Next(1, 50));
                        limits[(limit.Book, limit.Asset)] = limit;
                        limitUpserts.Add(limit);
                    }

                    if (random.Next(2) == 0)
                    {
                        var candidate = frontier.AddSeconds(random.Next(-3, 3));
                        now = candidate > now ? candidate : now;
                    }

                    trades.AddRange(appended);
                    marks.AddRange(newMarks);
                    var clockNow = now;
                    await engine.RefreshAsync(
                        r =>
                        {
                            r.AdvanceClock(new DateTimeOffset(clockNow, TimeSpan.Zero));
                            if (appended.Count > 0)
                            {
                                r.Append(desk, "trades", (IReadOnlyList<Trade>)appended);
                            }

                            if (newMarks.Count > 0)
                            {
                                r.Append(desk, "marks", (IReadOnlyList<Mark>)newMarks);
                            }

                            if (bookUpserts.Count > 0)
                            {
                                r.Upsert(desk, "books", (IReadOnlyList<Book>)bookUpserts);
                            }

                            if (bookDeletes.Count > 0)
                            {
                                r.Delete(desk, "books", (IReadOnlyList<Book>)bookDeletes);
                            }

                            if (limitUpserts.Count > 0)
                            {
                                r.Upsert(desk, "limits", (IReadOnlyList<Limit>)limitUpserts);
                            }
                        },
                        Ct);
                }
            }

            var whole = random.Next(10) == 0;
            var update = await maintained.UpdateAsync(
                Values(prepared, now), new DateTimeOffset(now, TimeSpan.Zero), whole, Ct);

            // Computed from nothing the first time only: a whole result asked for later is caught up, and
            // written whole.
            Assert.Equal(epoch == 0, update.Restarted);
            Assert.Equal(epoch == 0 || whole, update.Whole);
            Apply(update, held, key);
            Compare(held, await StatementAsync(engine, prepared, now, key), seed, epoch);
        }
    }

    /// <summary>Whether the plan has the node a test means to cover.</summary>
    private static bool Plans(Chalk.Ir.Plan plan, string planned)
    {
        var found = false;
        void Visit(Google.Protobuf.IMessage message)
        {
            if (message is Chalk.Ir.Rel rel)
            {
                found |= planned switch
                {
                    "AsOfLookupJoin" => rel.KindCase == Chalk.Ir.Rel.KindOneofCase.AsOfLookupJoin,
                    "AsOfLookupJoin fixed" => rel.KindCase == Chalk.Ir.Rel.KindOneofCase.AsOfLookupJoin
                        && rel.AsOfLookupJoin.FixedColumns.Count > 0,
                    "HashJoin Left" => rel.KindCase == Chalk.Ir.Rel.KindOneofCase.HashJoin
                        && rel.HashJoin.Type == Chalk.Ir.JoinType.Left,
                    "AsOfJoin over IndexLookup" => rel.KindCase == Chalk.Ir.Rel.KindOneofCase.AsOfJoin
                        && Under(rel.AsOfJoin.Right).KindCase == Chalk.Ir.Rel.KindOneofCase.IndexLookup,
                    "IndexLookup" => rel.KindCase == Chalk.Ir.Rel.KindOneofCase.IndexLookup,
                    _ => false,
                };
            }

            foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
            {
                switch (field.Accessor.GetValue(message))
                {
                    case Google.Protobuf.IMessage child:
                        Visit(child);
                        break;
                    case System.Collections.IList list:
                        foreach (var item in list)
                        {
                            if (item is Google.Protobuf.IMessage element)
                            {
                                Visit(element);
                            }
                        }

                        break;
                }
            }
        }

        Visit(plan.Root);
        return found;

        static Chalk.Ir.Rel Under(Chalk.Ir.Rel rel) => rel.KindCase switch
        {
            Chalk.Ir.Rel.KindOneofCase.Filter => Under(rel.Filter.Input),
            Chalk.Ir.Rel.KindOneofCase.Project => Under(rel.Project.Input),
            _ => rel,
        };
    }

    private static Trade NewTrade(Random random, int seq, DateTime ts) =>
        new(seq, 1 + random.Next(3), random.Next(6) == 0 ? null : 1 + random.Next(3), random.Next(-5, 9),
            Math.Round(random.NextDouble() * 1000, 4) * (random.Next(20) == 0 ? 1e12 : 1), ts);

    private static object?[] Values(PreparedQuery prepared, DateTime now) =>
        [.. prepared.Parameters.Select(_ => (object?)DateTime.SpecifyKind(now, DateTimeKind.Unspecified))];

    private static void Apply(MaintainedUpdate update, Dictionary<string, object?[]> held, int[] key)
    {
        if (update.Whole)
        {
            held.Clear();
        }

        foreach (var row in Rows(update.Before))
        {
            held.Remove(KeyOf(row, key));
        }

        foreach (var row in Rows(update.Now))
        {
            held[KeyOf(row, key)] = row;
        }
    }

    private static string KeyOf(object?[] row, int[] key) => string.Join("|", key.Select(k => row[k]?.ToString() ?? "∅"));

    private static IEnumerable<object?[]> Rows(Apache.Arrow.RecordBatch batch)
    {
        for (var i = 0; i < batch.Length; i++)
        {
            yield return [.. Enumerable.Range(0, batch.ColumnCount).Select(c => Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(c), i))];
        }
    }

    private static async Task<Dictionary<string, object?[]>> StatementAsync(
        ChalkEngine engine, PreparedQuery prepared, DateTime now, int[] key)
    {
        var rows = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        await using var execution = await engine.ExecuteAsync(prepared, Values(prepared, now), ct: Ct);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                foreach (var row in Rows(batch))
                {
                    rows.Add(KeyOf(row, key), row);
                }
            }
        }

        return rows;
    }

    private static void Compare(Dictionary<string, object?[]> held, Dictionary<string, object?[]> expected, int seed, int epoch)
    {
        var where = $"seed {seed}, epoch {epoch}";
        Assert.True(
            expected.Count == held.Count,
            $"{where}: {held.Count} rows held, {expected.Count} expected; extra [{string.Join("; ", held.Keys.Except(expected.Keys))}], missing [{string.Join("; ", expected.Keys.Except(held.Keys))}]");
        foreach (var (key, row) in expected)
        {
            Assert.True(held.TryGetValue(key, out var mine), $"{where}: key {key} missing");
            for (var c = 0; c < row.Length; c++)
            {
                if (row[c] is double a && mine![c] is double b)
                {
                    Assert.True(
                        a.Equals(b) || Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(a)),
                        $"{where}: key {key}, column {c}: {b:R} held, {a:R} expected");
                }
                else
                {
                    Assert.True(Equals(row[c], mine![c]), $"{where}: key {key}, column {c}: {mine[c] ?? "NULL"} held, {row[c] ?? "NULL"} expected");
                }
            }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
