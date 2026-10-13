using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The contract every view keeps (D339, <c>docs/design/64-incremental-views.md</c> §6), checked
/// against its oracle: random epochs — appends, upserts, deletes, rewrites and clock moves — drive a
/// view, and after each one every reader's replica, drained in random budgets, must be exactly what
/// the statement returns at that epoch, with the view's checksum. One reader drains every epoch and
/// one only every third, so catching up across epochs is checked too. Both engines.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class StandingViewOracleTests(SharedSidecar sidecar)
{
    private sealed record Fill(int Seq, int Book, int Asset, long Qty, DateTime Ts);

    private sealed record Position(int Book, int Asset, long Limit);

    private struct Key
    {
        public int Book;
        public int Asset;
    }

    private struct Row
    {
        public long Qty;
        public long Fills;
        public long Limit;
    }

    private const string Exposure =
        """
        SELECT k.book, k.asset, SUM(f.qty) AS qty, COUNT(*) AS fills, MAX(k."limit") AS "limit"
        FROM positions k
        JOIN fills f ON f.book = k.book AND f.asset = k.asset
        WHERE f.ts <= CAST(@now AS TIMESTAMP(9))
        GROUP BY k.book, k.asset
        HAVING SUM(f.qty) <> 0
        """;

    private static readonly DateTime Open = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ExecutionEngine.Vectorised, 11)]
    [InlineData(ExecutionEngine.Vectorised, 23)]
    [InlineData(ExecutionEngine.Reference, 37)]
    public Task Every_epoch_the_readers_are_the_statement(ExecutionEngine executor, int seed) =>
        EveryEpochAsync(executor, seed, ViewMaintenance.Recompute);

    /// <summary>
    /// The same oracle over a view kept by incremental maintenance (D343, design 68 §9): its join, its
    /// clocked filter, its aggregate and its <c>HAVING</c> run on each epoch's changes, and after every
    /// epoch the readers hold exactly what the statement returns.
    /// </summary>
    [Theory]
    [InlineData(11)]
    [InlineData(23)]
    [InlineData(37)]
    [InlineData(53)]
    public Task Every_epoch_a_maintained_view_is_the_statement(int seed) =>
        EveryEpochAsync(ExecutionEngine.Vectorised, seed, ViewMaintenance.Incremental);

    private async Task EveryEpochAsync(ExecutionEngine executor, int seed, ViewMaintenance maintenance)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var random = new Random(seed);
        var desk = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("fills", (IReadOnlyList<Fill>)[new(0, 1, 1, 5, Open)], t => t.AppendOnly(x => x.Ts))
            .AddTable("positions", (IReadOnlyList<Position>)[new(1, 1, 100)], t => t.Keyed(x => x.Book, x => x.Asset))
            .Build();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [desk],
            Planner = sidecar.CreatePlanner(),
            Clock = "now",
            Execution = new ExecutionOptions { Engine = executor },
        }, Ct);

        var now = Open;
        await engine.RefreshAsync(r => r.Version(1).AdvanceClock(new DateTimeOffset(now)), Ct);
        await using var view = await engine.MaterializeAsync(Exposure, new ViewOptions<Key, Row>
        {
            Key = ["book", "asset"],
            LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
            Maintenance = maintenance,
            Map = static (in ViewRow row, out Key key, out Row value) =>
            {
                key = new Key { Book = row.GetInt32(0), Asset = row.GetInt32(1) };
                value = new Row { Qty = row.GetInt64(2), Fills = row.GetInt64(3), Limit = row.GetInt64(4) };
            },
            RowHash = Hash,
        }, Ct);

        var eager = new Reader();
        var lagging = new Reader();
        var fills = new List<Fill> { new(0, 1, 1, 5, Open) };
        var positions = new Dictionary<(int, int), Position> { [(1, 1)] = new(1, 1, 100) };
        var seq = 1;
        var frontier = Open;

        for (var epoch = 2; epoch < 42; epoch++)
        {
            var version = epoch;
            var roll = random.Next(10);
            if (roll == 0)
            {
                // A rewrite: the log trimmed to its newer half.
                var kept = fills.Skip(fills.Count / 2).ToList();
                fills = kept;
                await engine.RefreshAsync(r => r.Version(version).Replace(desk, "fills", (IReadOnlyList<Fill>)kept), Ct);
            }
            else
            {
                var appended = new List<Fill>();
                for (var i = random.Next(4); i > 0; i--)
                {
                    frontier = frontier.AddSeconds(random.Next(3));
                    appended.Add(new Fill(seq++, 1 + random.Next(3), 1 + random.Next(3), random.Next(-3, 6), frontier));
                }

                var upserts = new List<Position>();
                var deletes = new List<Position>();
                for (var i = random.Next(3); i > 0; i--)
                {
                    var key = (1 + random.Next(3), 1 + random.Next(3));
                    if (upserts.Any(p => (p.Book, p.Asset) == key) || deletes.Any(p => (p.Book, p.Asset) == key))
                    {
                        continue;
                    }

                    if (random.Next(4) == 0 && positions.ContainsKey(key))
                    {
                        deletes.Add(positions[key]);
                        positions.Remove(key);
                    }
                    else
                    {
                        var position = new Position(key.Item1, key.Item2, random.Next(50, 500));
                        upserts.Add(position);
                        positions[key] = position;
                    }
                }

                // The clock moves forward, sometimes behind the newest fill and sometimes past it.
                var candidate = frontier.AddSeconds(random.Next(-2, 4));
                if (random.Next(3) == 0 && candidate > now)
                {
                    now = candidate;
                }

                fills.AddRange(appended);
                var clock = now;
                await engine.RefreshAsync(
                    r =>
                    {
                        r.Version(version).AdvanceClock(new DateTimeOffset(clock));
                        if (appended.Count > 0)
                        {
                            r.Append(desk, "fills", (IReadOnlyList<Fill>)appended);
                        }

                        if (upserts.Count > 0)
                        {
                            r.Upsert(desk, "positions", (IReadOnlyList<Position>)upserts);
                        }

                        if (deletes.Count > 0)
                        {
                            r.Delete(desk, "positions", (IReadOnlyList<Position>)deletes);
                        }
                    },
                    Ct);
            }

            Assert.Equal(version, await view.UpdateAsync(Ct));
            var expected = await StatementAsync(engine, now);

            eager.DrainAll(view, random);
            Assert.Equal(expected, eager.Rows);
            Assert.Equal(version, eager.Committed);
            Assert.Equal(eager.Checksum(), eager.CommittedChecksum);
            Assert.Equal(view.Checksum, eager.CommittedChecksum);
            Assert.Equal(expected.Count, view.Count);

            if (epoch % 3 == 0)
            {
                lagging.DrainAll(view, random);
                Assert.Equal(expected, lagging.Rows);
                Assert.Equal(lagging.Checksum(), lagging.CommittedChecksum);
            }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    /// <summary>What the statement returns now, with the clock bound as the view binds it.</summary>
    private static async Task<Dictionary<(int, int), (long, long, long)>> StatementAsync(ChalkEngine engine, DateTime now)
    {
        var rows = new Dictionary<(int, int), (long, long, long)>();
        await foreach (var batch in engine.QueryAsync(
            Exposure, new Dictionary<string, object?> { ["now"] = DateTime.SpecifyKind(now, DateTimeKind.Unspecified) }, ct: Ct))
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    rows.Add(
                        (Int(batch, 0, i), Int(batch, 1, i)),
                        (Long(batch, 2, i), Long(batch, 3, i), Long(batch, 4, i)));
                }
            }
        }

        return rows;

        static int Int(Apache.Arrow.RecordBatch batch, int column, int row) =>
            Convert.ToInt32(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(column), row));

        static long Long(Apache.Arrow.RecordBatch batch, int column, int row) =>
            Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(column), row));
    }

    /// <summary>A reader: its cursor, and the replica its sink keeps.</summary>
    private sealed class Reader : IViewSink<Key, Row>
    {
        private ViewCursor _cursor = ViewCursor.Start;

        public Dictionary<(int, int), (long, long, long)> Rows { get; } = [];

        public long Committed { get; private set; } = -1;

        public uint CommittedChecksum { get; private set; }

        /// <summary>Drains until level with the view, in random budgets.</summary>
        public void DrainAll(StandingView<Key, Row> view, Random random)
        {
            while (view.Drain(ref _cursor, this, budget: 1 + random.Next(3)) == DrainOutcome.Partial)
            {
            }
        }

        public void Reset() => Rows.Clear();

        public void Upsert(in Key key, in Row row) =>
            Rows[(key.Book, key.Asset)] = (row.Qty, row.Fills, row.Limit);

        public void Delete(in Key key) => Rows.Remove((key.Book, key.Asset));

        public void Commit(long version, uint checksum)
        {
            Committed = version;
            CommittedChecksum = checksum;
        }

        public uint Checksum()
        {
            var sum = 0u;
            foreach (var ((book, asset), (qty, count, limit)) in Rows)
            {
                var key = new Key { Book = book, Asset = asset };
                var row = new Row { Qty = qty, Fills = count, Limit = limit };
                sum += Hash(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<Key>(in key)),
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<Row>(in row)));
            }

            return sum;
        }
    }
}
