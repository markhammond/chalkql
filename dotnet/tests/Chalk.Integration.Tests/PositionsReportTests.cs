using System.Globalization;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The two statements design 63 was written for (D338). The first values each position as of each
/// day's close and totals it by asset, book and venue: a <c>VALUES</c> list of days, today's close
/// <c>LEAST(…, @now)</c>, five ASOF joins on it — one on a row of keys and one on a constant key —
/// DECIMAL(28,8) prices, and <c>GROUP BY ROLLUP(asset, book), ROLLUP(venue)</c>. The second derives
/// the day from local cut-offs and a zone table. Neither ran before; now both engines answer, and
/// agree, and the answer is the same whether the ASOF joins seek indexes or read tables, and the
/// same as the six <c>GROUP BY</c>s the rollup stands for, joined by <c>UNION ALL</c>.
/// </summary>
public sealed class PositionsReportTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    private static readonly DateTime Start = new(2026, 10, 10);
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0);

    private const string Days =
        """
        WITH days (trading_day, opens, closes) AS (
          VALUES ('2026-10-11', TIMESTAMP '2026-10-11 00:00:00', TIMESTAMP '2026-10-12 00:00:00'),
                 ('2026-10-12', TIMESTAMP '2026-10-12 00:00:00', LEAST(TIMESTAMP '2026-10-13 00:00:00', CAST(@now AS TIMESTAMP(9))))
        ),
        """;

    /// <summary>One row per position per day, as of the day's close, from <c>{0}</c>.</summary>
    private const string Positions =
        """
        p AS (
          SELECT d.trading_day, k.book, k.asset, k.venue, k.counter_symbol,
                 CASE WHEN c.qty > 0 THEN 'long' WHEN c.qty < 0 THEN 'short' ELSE 'flat' END AS direction,
                 c.qty,
                 c.qty * m.usd_price / r.usd_rate                                AS notional,
                 (c.realized - COALESCE(o.realized, 0)) * q.usd_rate / r.usd_rate AS realized,
                 c.qty * (m.usd_price - c.avg_price * q.usd_rate) / r.usd_rate     AS unrealized
          FROM {0} d
          CROSS JOIN positions k
          ASOF JOIN position_changes c
            MATCH_CONDITION c.ts <= d.closes
            ON (c.book, c.asset, c.venue, c.counter_symbol) = (k.book, k.asset, k.venue, k.counter_symbol)
          LEFT ASOF JOIN position_changes o
            MATCH_CONDITION o.ts < d.opens
            ON (o.book, o.asset, o.venue, o.counter_symbol) = (k.book, k.asset, k.venue, k.counter_symbol)
          ASOF JOIN marks m    MATCH_CONDITION m.ts <= d.closes ON m.asset = k.asset
          ASOF JOIN fx_rates q MATCH_CONDITION q.ts <= d.closes ON q.symbol = k.counter_symbol
          ASOF JOIN fx_rates r MATCH_CONDITION r.ts <= d.closes ON r.symbol = 'EUR'
          WHERE k.book IN ('Basis', 'Delta One')
            AND NOT (c.qty = 0 AND c.realized = COALESCE(o.realized, 0))
        )
        """;

    /// <summary>
    /// The report's select list, with each <c>GROUPING</c> and each key a placeholder: the rollup
    /// fills them with themselves, and a <c>UNION ALL</c> branch with the set's constants.
    /// </summary>
    private const string Totals =
        """
        SELECT CASE WHEN {0} = 1 THEN ''
                    WHEN {1} = 1 THEN {3}
                    ELSE {3} || ' · ' || {4} END                                     AS row_key,
               CASE WHEN {2} = 1 THEN '' ELSE {5} END                                AS column_key,
               CASE WHEN MIN(asset) = MAX(asset) AND MIN(trading_day) = MAX(trading_day)
                    THEN CAST(ROUND(SUM(qty) * 1000000) AS BIGINT) END                AS quantity,
               CASE WHEN MIN(trading_day) = MAX(trading_day)
                    THEN CAST(ROUND(SUM(notional) * 100) AS BIGINT) END              AS notional,
               CAST(ROUND(SUM(realized) * 100) AS BIGINT)                            AS realized_pnl,
               CASE WHEN MIN(trading_day) = MAX(trading_day)
                    THEN CAST(ROUND(SUM(unrealized) * 100) AS BIGINT) END            AS unrealized_pnl
        FROM p
        {6}
        """;

    private static string Rollup(string days) =>
        days + string.Format(CultureInfo.InvariantCulture, Positions, "days") + "\n"
        + string.Format(
            CultureInfo.InvariantCulture,
            Totals,
            "GROUPING(asset)",
            "GROUPING(book)",
            "GROUPING(venue)",
            "asset",
            "book",
            "venue",
            "GROUP BY ROLLUP(asset, book), ROLLUP(venue)");

    /// <summary>The six sets <c>ROLLUP(asset, book), ROLLUP(venue)</c> stands for, one branch each.</summary>
    private static string SixBranches()
    {
        string[][] sets = [["asset", "book", "venue"], ["asset", "book"], ["asset", "venue"], ["asset"], ["venue"], []];
        var branches = sets.Select(set =>
        {
            string Bit(string key) => set.Contains(key) ? "0" : "1";
            string Key(string key) => set.Contains(key) ? key : "CAST(NULL AS VARCHAR)";
            return string.Format(
                CultureInfo.InvariantCulture,
                Totals,
                Bit("asset"),
                Bit("book"),
                Bit("venue"),
                Key("asset"),
                Key("book"),
                Key("venue"),
                set.Length == 0 ? string.Empty : "GROUP BY " + string.Join(", ", set));
        });
        return Days + string.Format(CultureInfo.InvariantCulture, Positions, "days") + "\n"
            + string.Join("\nUNION ALL\n", branches);
    }

    [Fact]
    public async Task The_report_runs_and_both_engines_agree()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var vectorised = await CreateEngineAsync(ExecutionEngine.Vectorised, indexed: true);
        await using var reference = await CreateEngineAsync(ExecutionEngine.Reference, indexed: true);

        var rows = await RowsAsync(vectorised, Rollup(Days));

        Assert.Equal(await RowsAsync(reference, Rollup(Days)), rows);
        Assert.Contains(rows, r => r.StartsWith("||", StringComparison.Ordinal));   // the grand total
        Assert.Contains(rows, r => r.StartsWith("BTC · Basis|LMAX|", StringComparison.Ordinal));
        Assert.Equal(2 * 3 * 2 + 2 * 3 + 3 * 2 + 3 + 2 + 1, rows.Count);
    }

    /// <summary>The ASOF joins seek the indexes, and give the rows reading the tables gives.</summary>
    [Fact]
    public async Task Seeking_gives_the_rows_reading_gives()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var indexed = await CreateEngineAsync(ExecutionEngine.Vectorised, indexed: true);
        await using var plain = await CreateEngineAsync(ExecutionEngine.Vectorised, indexed: false);

        var sought = await indexed.PrepareAsync(Rollup(Days));
        Assert.True(PlanWalker.Count(sought.Plan, Rel.KindOneofCase.AsOfLookupJoin) >= 3, sought.Plan.ToPlanText());

        Assert.Equal(await RowsAsync(plain, Rollup(Days)), await RowsAsync(indexed, Rollup(Days)));
    }

    /// <summary>
    /// The rollup is its six sets: the form a host wrote while <c>ROLLUP</c> was refused, one
    /// <c>UNION ALL</c> branch for each, gives the same rows.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task The_rollup_is_its_six_sets(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor, indexed: true);

        var rollup = await RowsAsync(engine, Rollup(Days));
        var branches = await RowsAsync(engine, SixBranches());

        Assert.Equal(rollup.Order(StringComparer.Ordinal), branches.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The second statement: the day's cut-offs are local, and the zone table says which offset was
    /// in force when each fell. 17:00 in New York on 11 October, daylight time, is 21:00 UTC.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task The_day_is_derived_from_local_cut_offs(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor, indexed: true);

        const string today =
            """
            WITH d AS (
              SELECT x.trading_day,
                     x.opens_local - z1.utc_offset_minutes * INTERVAL '1' MINUTE AS opens,
                     LEAST(x.closes_local - z2.utc_offset_minutes * INTERVAL '1' MINUTE, CAST(@now AS TIMESTAMP(9))) AS closes
              FROM (SELECT '2026-10-12' AS trading_day,
                           TIMESTAMP '2026-10-11 17:00:00' AS opens_local, TIMESTAMP '2026-10-12 17:00:00' AS closes_local) x
              JOIN time_zones z1 ON z1.zone = 'America/New_York'
                AND x.opens_local - z1.utc_offset_minutes * INTERVAL '1' MINUTE >= z1.starts
                AND x.opens_local - z1.utc_offset_minutes * INTERVAL '1' MINUTE < z1.ends
              JOIN time_zones z2 ON z2.zone = 'America/New_York'
                AND x.closes_local - z2.utc_offset_minutes * INTERVAL '1' MINUTE >= z2.starts
                AND x.closes_local - z2.utc_offset_minutes * INTERVAL '1' MINUTE < z2.ends
            ),
            """;

        var day = await RowsAsync(engine, today[..today.LastIndexOf(',')] + " SELECT trading_day, opens, closes FROM d");
        Assert.Equal(["2026-10-12|2026-10-11 21:00:00|2026-10-12 12:00:00"], day);

        var report = await RowsAsync(engine, today + string.Format(CultureInfo.InvariantCulture, Positions, "d") + "\n"
            + string.Format(
                CultureInfo.InvariantCulture,
                Totals,
                "GROUPING(asset)",
                "GROUPING(book)",
                "GROUPING(venue)",
                "asset",
                "book",
                "venue",
                "GROUP BY ROLLUP(asset, book), ROLLUP(venue)"));

        // One day, on which Basis's SOL on LMAX is flat and realizes nothing, so its leaf is left out;
        // its subtotals still have the other book's and venue's.
        Assert.Equal((2 * 3 * 2) - 1 + (2 * 3) + (3 * 2) + 3 + 2 + 1, report.Count);
    }

    // ---------------------------------------------------------------- the data

    private static readonly string[] Books = ["Basis", "Delta One", "Macro"];
    private static readonly string[] Assets = ["BTC", "ETH", "SOL"];
    private static readonly string[] Venues = ["LMAX", "CBOE"];

    /// <summary>Every book, asset and venue; LMAX trades against the dollar and CBOE the pound.</summary>
    private static readonly PositionKey[] Keys =
    [
        .. from book in Books
           from asset in Assets
           from venue in Venues
           select new PositionKey(book, asset, venue, venue == "LMAX" ? "USD" : "GBP"),
    ];

    /// <summary>
    /// A change to each position every four hours from 10 October. One position goes flat and
    /// realizes nothing on the 12th, which the report leaves out.
    /// </summary>
    private static readonly PositionChange[] Changes =
    [
        .. Keys.SelectMany((k, i) => Enumerable.Range(0, 17).Select(step =>
        {
            var flat = i == 4 && step >= 10;
            return new PositionChange(
                k.Book,
                k.Asset,
                k.Venue,
                k.CounterSymbol,
                Start.AddHours(4 * step),
                flat ? 0m : ((i % 5) - 2) * 0.5m + (step * 0.125m),
                flat ? 400m : (step * 10.25m) + i,
                100m + i + step);
        })),
    ];

    private static readonly Mark[] Marks =
    [
        .. Assets.SelectMany((asset, a) => Enumerable.Range(0, 72).Select(h =>
            new Mark(asset, Start.AddHours(h), (1_000m * (a + 1)) + (h * 0.5m)))),
    ];

    private static readonly FxRate[] Rates =
    [
        .. new[] { ("USD", 1m, 0m), ("EUR", 1.08m, 0.0001m), ("GBP", 1.27m, 0.0001m) }
            .SelectMany(fx => Enumerable.Range(0, 72).Select(h =>
                new FxRate(fx.Item1, Start.AddHours(h), fx.Item2 + (h * fx.Item3)))),
    ];

    /// <summary>New York's two spans around the report: daylight time, then standard time.</summary>
    private static readonly ZoneSpan[] Zones =
    [
        new("America/New_York", new DateTime(2026, 3, 8, 7, 0, 0), new DateTime(2026, 11, 1, 6, 0, 0), -240),
        new("America/New_York", new DateTime(2026, 11, 1, 6, 0, 0), new DateTime(2027, 3, 14, 7, 0, 0), -300),
    ];

    private async Task<ChalkEngine> CreateEngineAsync(ExecutionEngine executor, bool indexed)
    {
        var source = new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase).AddTable("positions", Keys);
        source = indexed
            ? source
                .AddTable("position_changes", Changes, t => t.Index(c => c.Book, c => c.Asset, c => c.Venue, c => c.CounterSymbol, c => c.Ts))
                .AddTable("marks", Marks, t => t.Index(m => m.Asset, m => m.Ts))
                .AddTable("fx_rates", Rates, t => t.Index(r => r.Symbol, r => r.Ts))
            : source.AddTable("position_changes", Changes).AddTable("marks", Marks).AddTable("fx_rates", Rates);

        return await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "positions-report",
            Sources = [source.AddTable("time_zones", Zones).Build()],
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = executor },
        });
    }

    /// <summary>Each row as its values joined by '|', NULL as empty, in the order the engine gave them.</summary>
    private static async Task<List<string>> RowsAsync(ChalkEngine engine, string sql)
    {
        var prepared = await engine.PrepareAsync(sql);
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared, new { now = Now });
        await foreach (var batch in execution.Batches)
        {
            for (var i = 0; i < batch.Length; i++)
            {
                var values = new string[batch.ColumnCount];
                for (var c = 0; c < batch.ColumnCount; c++)
                {
                    values[c] = batch.Column(c) switch
                    {
                        var column when column.IsNull(i) => string.Empty,
                        StringArray text => text.GetString(i),
                        StringViewArray view => view.GetString(i),
                        Int64Array longs => longs.GetValue(i)!.Value.ToString(CultureInfo.InvariantCulture),
                        TimestampArray stamps => stamps.GetTimestamp(i)!.Value.UtcDateTime.ToString(
                            "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        var other => other.GetType().Name,
                    };
                }

                rows.Add(string.Join('|', values));
            }

            batch.Dispose();
        }

        return [.. rows.Order(StringComparer.Ordinal)];
    }

    public sealed record PositionKey(string Book, string Asset, string Venue, string CounterSymbol);

    public sealed record PositionChange(
        string Book,
        string Asset,
        string Venue,
        string CounterSymbol,
        DateTime Ts,
        [property: ChalkColumn(Precision = 28, Scale = 8)] decimal Qty,
        [property: ChalkColumn(Precision = 28, Scale = 8)] decimal Realized,
        [property: ChalkColumn(Precision = 28, Scale = 8)] decimal AvgPrice);

    public sealed record Mark(string Asset, DateTime Ts, [property: ChalkColumn(Precision = 28, Scale = 8)] decimal UsdPrice);

    public sealed record FxRate(string Symbol, DateTime Ts, [property: ChalkColumn(Precision = 28, Scale = 8)] decimal UsdRate);

    public sealed record ZoneSpan(string Zone, DateTime Starts, DateTime Ends, int UtcOffsetMinutes);
}
