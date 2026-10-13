using System.Globalization;
using Apache.Arrow;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// <c>GROUPING SETS</c>, <c>ROLLUP</c> and <c>CUBE</c> in one pass (D338, design 63 §1, F179). Each
/// was refused: "no rule that plans this statement". Calcite expands every form, two rollups side by
/// side included, into one aggregate with a list of sets, and the executor now keeps a table for
/// each set and feeds every input batch to each, so the input is read once. Both engines answer,
/// and the six-set rollup is checked against the same statement written as one <c>GROUP BY</c> for
/// each set, joined by <c>UNION ALL</c>.
/// </summary>
public sealed class GroupingSetsTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    /// <summary>
    /// Two rollups side by side are their product: six sets, from (asset, book, venue) down to the
    /// grand total. The answer is the six <c>GROUP BY</c>s', and the table is read once, not six
    /// times.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task Two_rollups_side_by_side_are_six_sets_from_one_read(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);

        var (rows, scanned) = await RowsAsync(engine, await engine.PrepareAsync(SixSets));
        var (oracle, _) = await RowsAsync(engine, await engine.PrepareAsync(SixGroupBys));

        Assert.Equal(oracle.Order(StringComparer.Ordinal).ToArray(), rows.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(7 + 6 + 5 + 3 + 2 + 1, rows.Count);
        Assert.Equal(Positions.Length, scanned);
    }

    /// <summary>
    /// Many batches in and out: two thousand rows read 64 at a time, and several hundred groups,
    /// more than a batch holds. The answer is the oracle's, and a second execution of the same plan,
    /// whose tables start again, gives it again.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task Many_batches_and_a_second_execution_agree_with_the_oracle(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor, Generated, batchSize: 64);

        var prepared = await engine.PrepareAsync(SixSets);
        var (first, scanned) = await RowsAsync(engine, prepared);
        var (second, _) = await RowsAsync(engine, prepared);
        var (oracle, _) = await RowsAsync(engine, await engine.PrepareAsync(SixGroupBys));

        Assert.True(first.Count > 64 * 4, $"{first.Count} groups");
        Assert.Equal(oracle.Order(StringComparer.Ordinal).ToArray(), first.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(first.Order(StringComparer.Ordinal).ToArray(), second.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(Generated.Length, scanned);
    }

    private const string SixSets =
        """
        SELECT asset, book, venue, GROUPING(asset, book, venue) AS g, SUM(qty) AS qty, COUNT(*) AS n
        FROM positions
        GROUP BY ROLLUP(asset, book), ROLLUP(venue)
        """;

    /// <summary>The same six sets, one <c>GROUP BY</c> each, with each set's GROUPING written out.</summary>
    private const string SixGroupBys =
        """
        SELECT asset, book, venue, 0 AS g, SUM(qty) AS qty, COUNT(*) AS n
        FROM positions GROUP BY asset, book, venue
        UNION ALL
        SELECT asset, book, CAST(NULL AS VARCHAR), 1, SUM(qty), COUNT(*)
        FROM positions GROUP BY asset, book
        UNION ALL
        SELECT asset, CAST(NULL AS VARCHAR), venue, 2, SUM(qty), COUNT(*)
        FROM positions GROUP BY asset, venue
        UNION ALL
        SELECT asset, CAST(NULL AS VARCHAR), CAST(NULL AS VARCHAR), 3, SUM(qty), COUNT(*)
        FROM positions GROUP BY asset
        UNION ALL
        SELECT CAST(NULL AS VARCHAR), CAST(NULL AS VARCHAR), venue, 6, SUM(qty), COUNT(*)
        FROM positions GROUP BY venue
        UNION ALL
        SELECT CAST(NULL AS VARCHAR), CAST(NULL AS VARCHAR), CAST(NULL AS VARCHAR), 7, SUM(qty), COUNT(*)
        FROM positions
        """;

    /// <summary>
    /// <c>CUBE</c> is every subset of its keys. A NULL book groups with the other NULL books, and
    /// <c>GROUPING</c> tells that group apart from the subtotal in which book is rolled up, whose book
    /// is NULL too. With several arguments the first is the most significant bit.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_cube_tells_a_null_key_from_a_rolled_up_one(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);

        var (rows, _) = await RowsAsync(
            engine,
            """
            SELECT book, venue, GROUPING(book, venue) AS g, GROUPING(venue) AS gv, COUNT(*) AS n
            FROM positions
            WHERE asset = 'GBPUSD'
            GROUP BY CUBE(book, venue)
            """);

        string[] expected =
        [
            // (book, venue): the NULL book is one group of two.
            "B1|LMAX|0|0|1",
            "∅|EBS|0|0|2",
            // (book): venue rolled up.
            "B1|∅|1|1|1",
            "∅|∅|1|1|2",
            // (venue): book rolled up; its NULL is not the NULL book's.
            "∅|EBS|2|0|2",
            "∅|LMAX|2|0|1",
            // (): both.
            "∅|∅|3|1|3",
        ];
        Assert.Equal(
            expected,
            rows.Order(StringComparer.Ordinal).OrderBy(r => r.Split('|')[2], StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The first form asked for: <c>GROUPING</c> in the select list labels the subtotals, and in
    /// <c>HAVING</c> it keeps or drops a whole set.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task Grouping_labels_subtotals_and_selects_sets(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);

        var (rows, _) = await RowsAsync(
            engine,
            """
            SELECT CASE WHEN GROUPING(asset) = 1 THEN 'all' ELSE asset END AS a,
                   CASE WHEN GROUPING(venue) = 1 THEN 'all' ELSE venue END AS v,
                   SUM(qty) AS qty
            FROM positions
            GROUP BY GROUPING SETS ((asset, venue), (asset), (venue), ())
            HAVING GROUPING(asset) = 1 OR asset = 'USDJPY'
            ORDER BY a, v
            """);

        Assert.Equal(
            [
                "USDJPY|LMAX|1500",
                "USDJPY|all|1500",
                "all|EBS|1300",
                "all|LMAX|2300",
                "all|all|3600",
            ],
            rows);
    }

    /// <summary>
    /// A set of no keys is the grand total, and is one row even when no row is read, as <c>GROUP BY
    /// ()</c> is; the other sets then have none.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task The_grand_total_is_a_row_over_no_rows(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);

        var (rows, _) = await RowsAsync(
            engine,
            "SELECT asset, COUNT(*) AS n, SUM(qty) AS qty FROM positions WHERE qty < 0 GROUP BY ROLLUP(asset)");

        Assert.Equal(["∅|0|∅"], rows);
    }

    /// <summary>Under a plain <c>GROUP BY</c>, <c>GROUPING</c> is 0 for every row.</summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task Grouping_under_one_grouping_is_zero(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);

        var (rows, _) = await RowsAsync(
            engine, "SELECT asset, GROUPING(asset) AS g FROM positions GROUP BY asset ORDER BY asset");

        Assert.Equal(["EURUSD|0", "GBPUSD|0", "USDJPY|0"], rows);
    }

    /// <summary>
    /// A DISTINCT aggregate under grouping sets is refused, naming the aggregate (design 63 §8 a),
    /// and how to write it instead.
    /// </summary>
    [Fact]
    public async Task A_distinct_aggregate_with_grouping_sets_is_refused_by_name()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);

        var refusal = await Assert.ThrowsAsync<PlanningException>(
            () => engine.PrepareAsync(
                "SELECT asset, COUNT(DISTINCT venue) AS venues FROM positions GROUP BY ROLLUP(asset)").AsTask());

        Assert.Equal(PlanErrorKinds.Unsupported, refusal.Kind);
        Assert.Contains("COUNT(DISTINCT …) with GROUPING SETS, ROLLUP or CUBE", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("UNION ALL", refusal.Message, StringComparison.Ordinal);
    }

    private static readonly Position[] Positions =
    [
        new(1, "EURUSD", "B1", "LMAX", 100),
        new(2, "EURUSD", "B1", "EBS", 200),
        new(3, "EURUSD", "B2", "LMAX", 300),
        new(4, "GBPUSD", "B1", "LMAX", 400),
        new(5, "GBPUSD", null, "EBS", 500),
        new(6, "GBPUSD", null, "EBS", 600),
        new(7, "USDJPY", "B2", "LMAX", 700),
        new(8, "USDJPY", null, "LMAX", 800),
    ];

    /// <summary>Two thousand positions over fifty assets, six books (one of them NULL) and three venues.</summary>
    private static readonly Position[] Generated =
    [
        .. Enumerable.Range(1, 2_000).Select(i => new Position(
            i,
            $"A{i % 50:D2}",
            i % 7 == 0 ? null : $"B{i % 5}",
            (i % 3) switch { 0 => "LMAX", 1 => "EBS", _ => "CBOE" },
            i)),
    ];

    private async Task<ChalkEngine> CreateEngineAsync(
        ExecutionEngine executor, Position[]? positions = null, int batchSize = 0)
    {
        var execution = new ExecutionOptions { Engine = executor };
        if (batchSize > 0)
        {
            execution = new ExecutionOptions { Engine = executor, BatchSize = batchSize };
        }

        return await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "grouping-sets",
            Sources =
            [
                new PocoSourceBuilder("mem")
                    .NamingPolicy(PocoNamingPolicy.SnakeCase)
                    .AddTable("positions", positions ?? Positions)
                    .Build(),
            ],
            Planner = sidecar.CreatePlanner(),
            Execution = execution,
        });
    }

    private static async Task<(List<string> Rows, long Scanned)> RowsAsync(ChalkEngine engine, string sql) =>
        await RowsAsync(engine, await engine.PrepareAsync(sql));

    /// <summary>Each row as its values joined by '|', NULL as '∅', and the rows the source read.</summary>
    private static async Task<(List<string> Rows, long Scanned)> RowsAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[]);
        await foreach (var batch in execution.Batches)
        {
            for (var i = 0; i < batch.Length; i++)
            {
                var values = new string[batch.ColumnCount];
                for (var c = 0; c < batch.ColumnCount; c++)
                {
                    values[c] = Text(batch.Column(c), i);
                }

                rows.Add(string.Join('|', values));
            }

            batch.Dispose();
        }

        return (rows, execution.Stats.RowsScanned);
    }

    private static string Text(IArrowArray column, int row) =>
        column.IsNull(row)
            ? "∅"
            : column switch
            {
                StringArray text => text.GetString(row),
                StringViewArray view => view.GetString(row),
                Int32Array ints => ints.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
                Int64Array longs => longs.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
                var other => other.GetType().Name,
            };

    public sealed record Position(int Id, string Asset, string? Book, string Venue, long Qty);
}
