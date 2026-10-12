using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The report a pivot grid serves (D339, <c>docs/design/64-incremental-views.md</c> §0–§5) as a
/// standing view, drained into a sink shaped as a streaming table's ingest is — fixed-layout keys
/// with inline UTF-8 buffers, cells with <see cref="long.MinValue"/> for NULL, one unit staged per
/// drain and checked against its checksum when it commits — with no dependency on any such library.
/// Positions as of the current close, marked to the latest price, rolled up by asset, book and venue.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class StandingViewPivotTests(SharedSidecar sidecar)
{
    private sealed record PositionChange(string Book, string Asset, string Venue, DateTime Ts, decimal Qty);

    private sealed record Position(string Book, string Asset, string Venue);

    private sealed record Mark(string Asset, DateTime Ts, decimal UsdPrice);

    [InlineArray(64)]
    private struct Utf8x64
    {
        private byte _first;
    }

    [InlineArray(32)]
    private struct Utf8x32
    {
        private byte _first;
    }

    /// <summary>A grid key: the row and column labels, inline, as a streaming table keys a cell.</summary>
    private struct PivotKey
    {
        public Utf8x64 Row;
        public Utf8x32 Column;
        public byte RowLength;
        public byte ColumnLength;

        public readonly string Text =>
            Encoding.UTF8.GetString(((ReadOnlySpan<byte>)Row)[..RowLength]) + " | "
            + Encoding.UTF8.GetString(((ReadOnlySpan<byte>)Column)[..ColumnLength]);
    }

    /// <summary>A grid cell, with <see cref="None"/> for a figure that does not add up.</summary>
    private struct PivotCell
    {
        public const long None = long.MinValue;

        public long Quantity;
        public long Notional;
    }

    private const string Report =
        """
        WITH days (trading_day, closes) AS (
          VALUES ('2026-10-12', LEAST(TIMESTAMP '2026-10-13 00:00:00', CAST(@now AS TIMESTAMP(9))))
        ),
        p AS (
          SELECT k.book, k.asset, k.venue, c.qty, c.qty * m.usd_price AS notional
          FROM days d
          CROSS JOIN positions k
          ASOF JOIN position_changes c
            MATCH_CONDITION c.ts <= d.closes
            ON (c.book, c.asset, c.venue) = (k.book, k.asset, k.venue)
          ASOF JOIN marks m MATCH_CONDITION m.ts <= d.closes ON m.asset = k.asset
        )
        SELECT CASE WHEN GROUPING(asset) = 1 THEN 'Total'
                    WHEN GROUPING(book) = 1 THEN asset
                    ELSE asset || ' · ' || book END                                    AS row_key,
               CASE WHEN GROUPING(venue) = 1 THEN 'Total' ELSE venue END              AS column_key,
               CASE WHEN MIN(asset) = MAX(asset)
                    THEN CAST(ROUND(SUM(qty) * 1000000) AS BIGINT) END                AS quantity,
               CAST(ROUND(SUM(notional) * 100) AS BIGINT)                             AS notional
        FROM p
        GROUP BY ROLLUP(asset, book), ROLLUP(venue)
        """;

    private static readonly DateTime Open = new(2026, 10, 12, 9, 0, 0);

    /// <summary>
    /// Each tick's unit carries only the cells that moved, under the host's version, and the grid the
    /// ingest holds after it is the report as of that version, checksum and all.
    /// </summary>
    [Fact]
    public async Task Each_tick_posts_the_cells_that_moved()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var desk = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable(
                "position_changes",
                (IReadOnlyList<PositionChange>)
                [
                    new("Basis", "BTC", "LMAX", Open, 2m),
                    new("Basis", "ETH", "EBS", Open, 10m),
                    new("Delta One", "BTC", "EBS", Open, -1m),
                ],
                t => t.AppendOnly(x => x.Ts))
            .AddTable(
                "positions",
                (IReadOnlyList<Position>)
                [
                    new("Basis", "BTC", "LMAX"),
                    new("Basis", "ETH", "EBS"),
                    new("Delta One", "BTC", "EBS"),
                ],
                t => t.Keyed(x => x.Book, x => x.Asset, x => x.Venue))
            .AddTable(
                "marks",
                (IReadOnlyList<Mark>)[new("BTC", Open, 60000m), new("ETH", Open, 3000m)],
                t => t.AsOf(x => x.Ts, x => x.Asset))
            .Build();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [desk],
            Planner = sidecar.CreatePlanner(),
            Clock = "now",
        }, Ct);
        var now = Open.AddMinutes(1);
        await engine.RefreshAsync(r => r.Version(100).AdvanceClock(new DateTimeOffset(now, TimeSpan.Zero)), Ct);

        await using var view = await engine.MaterializeAsync(Report, new ViewOptions<PivotKey, PivotCell>
        {
            Key = ["row_key", "column_key"],
            Map = Map,
            RowHash = Hash,
        }, Ct);
        var ingest = new Ingest();
        var cursor = ViewCursor.Start;
        Drain(view, ref cursor, ingest);
        Assert.True(ingest.LastWasSnapshot);
        Assert.Equal(100, ingest.UpstreamVersion);
        Assert.Equal(await ReportAsync(engine, now), ingest.Grid());

        // A tick that moves one price: only the cells that hold ETH move, and the totals above them.
        now = now.AddSeconds(1);
        var at = now;
        await engine.RefreshAsync(
            r => r.Version(101).Append(desk, "marks", (IReadOnlyList<Mark>)[new("ETH", at, 3100m)]).AdvanceClock(new DateTimeOffset(at, TimeSpan.Zero)),
            Ct);
        await view.UpdateAsync(Ct);
        Drain(view, ref cursor, ingest);
        Assert.False(ingest.LastWasSnapshot);
        Assert.Equal(101, ingest.UpstreamVersion);
        Assert.Equal(await ReportAsync(engine, now), ingest.Grid());
        Assert.Equal(
            ["ETH | EBS", "ETH | Total", "ETH · Basis | EBS", "ETH · Basis | Total", "Total | EBS", "Total | Total"],
            ingest.LastUnit.Order(StringComparer.Ordinal));

        // A fill on a new position, a new mark, and the clock past both: the position joins the grid.
        now = now.AddSeconds(5);
        at = now;
        await engine.RefreshAsync(
            r => r
                .Version(102)
                .Append(desk, "position_changes", (IReadOnlyList<PositionChange>)[new("Delta One", "ETH", "LMAX", at.AddSeconds(-1), 4m)])
                .Upsert(desk, "positions", (IReadOnlyList<Position>)[new("Delta One", "ETH", "LMAX")])
                .Append(desk, "marks", (IReadOnlyList<Mark>)[new("BTC", at.AddSeconds(-1), 61000m)])
                .AdvanceClock(new DateTimeOffset(at, TimeSpan.Zero)),
            Ct);
        await view.UpdateAsync(Ct);
        Drain(view, ref cursor, ingest);
        Assert.Equal(102, ingest.UpstreamVersion);
        Assert.Equal(await ReportAsync(engine, now), ingest.Grid());
        Assert.Contains("ETH · Delta One | LMAX", ingest.LastUnit);
        Assert.Contains("ETH | Total", ingest.Grid().Keys);
        Assert.Equal(PivotCell.None, ingest.Cell("Total | Total").Quantity);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Map(in ViewRow row, out PivotKey key, out PivotCell cell)
    {
        key = default;
        var rowLabel = row.GetUtf8(0);
        var columnLabel = row.GetUtf8(1);
        rowLabel.CopyTo(key.Row);
        columnLabel.CopyTo(key.Column);
        key.RowLength = (byte)rowLabel.Length;
        key.ColumnLength = (byte)columnLabel.Length;
        cell = new PivotCell
        {
            Quantity = row.IsNull(2) ? PivotCell.None : row.GetInt64(2),
            Notional = row.IsNull(3) ? PivotCell.None : row.GetInt64(3),
        };
    }

    private static uint Hash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> row)
    {
        var hash = 2166136261u;
        foreach (var b in key)
        {
            hash = (hash ^ b) * 16777619u;
        }

        foreach (var b in row)
        {
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }

    /// <summary>Drains in units of at most eight cells, as a metered slice would.</summary>
    private static void Drain(StandingView<PivotKey, PivotCell> view, ref ViewCursor cursor, Ingest ingest)
    {
        while (view.Drain(ref cursor, ingest, budget: 8) == DrainOutcome.Partial)
        {
        }
    }

    /// <summary>The report as the statement returns it at <paramref name="now"/>, cell by cell.</summary>
    private static async Task<Dictionary<string, (long, long)>> ReportAsync(ChalkEngine engine, DateTime now)
    {
        var grid = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        await foreach (var batch in engine.QueryAsync(Report, new Dictionary<string, object?> { ["now"] = now }, ct: Ct))
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    var values = Enumerable.Range(0, 4)
                        .Select(c => Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(c), i))
                        .ToArray();
                    grid.Add(
                        $"{values[0]} | {values[1]}",
                        (values[2] is null ? PivotCell.None : Convert.ToInt64(values[2]),
                            values[3] is null ? PivotCell.None : Convert.ToInt64(values[3])));
                }
            }
        }

        return grid;
    }

    /// <summary>
    /// A sink shaped as a streaming table's ingest: one unit staged per drain — a snapshot after a
    /// reset, deltas otherwise — applied at its commit, which checks the table's checksum against
    /// the one the view sends and keeps the unit's upstream version.
    /// </summary>
    private sealed class Ingest : IViewSink<PivotKey, PivotCell>
    {
        private readonly Dictionary<string, (PivotKey Key, PivotCell Cell)> _table = new(StringComparer.Ordinal);
        private readonly List<(PivotKey Key, PivotCell? Cell)> _staged = [];
        private bool _snapshot;

        public long UpstreamVersion { get; private set; }

        public bool LastWasSnapshot { get; private set; }

        public List<string> LastUnit { get; } = [];

        public void Reset()
        {
            _snapshot = true;
            _staged.Clear();
        }

        public void Upsert(in PivotKey key, in PivotCell row) => _staged.Add((key, row));

        public void Delete(in PivotKey key) => _staged.Add((key, null));

        public void Commit(long version, uint checksum)
        {
            Assert.True(version >= UpstreamVersion, "a unit's upstream version never goes back");
            if (_snapshot)
            {
                _table.Clear();
            }

            LastUnit.Clear();
            foreach (var (key, cell) in _staged)
            {
                LastUnit.Add(key.Text);
                if (cell is { } live)
                {
                    _table[key.Text] = (key, live);
                }
                else
                {
                    _table.Remove(key.Text);
                }
            }

            var sum = 0u;
            foreach (var (key, cell) in _table.Values)
            {
                sum += Hash(
                    MemoryMarshal.AsBytes(new ReadOnlySpan<PivotKey>(in key)),
                    MemoryMarshal.AsBytes(new ReadOnlySpan<PivotCell>(in cell)));
            }

            Assert.Equal(sum, checksum);
            LastWasSnapshot = _snapshot;
            UpstreamVersion = version;
            _snapshot = false;
            _staged.Clear();
        }

        public PivotCell Cell(string key) => _table[key].Cell;

        public Dictionary<string, (long, long)> Grid() =>
            _table.ToDictionary(
                pair => pair.Key,
                pair => (pair.Value.Cell.Quantity, pair.Value.Cell.Notional),
                StringComparer.Ordinal);
    }
}
