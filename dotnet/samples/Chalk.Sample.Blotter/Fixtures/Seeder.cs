using System.Diagnostics;
using System.Globalization;

namespace Chalk.Sample.Blotter.Fixtures;

/// <summary>
/// Seeds a <see cref="TradeStore"/> with a generated corpus: client orders worked over a span of time,
/// with their versions and fills (<see cref="Corpus"/>), written with binary <c>COPY</c> on several
/// connections at once before any index or trigger exists; then indexed, and kept current from then on.
/// </summary>
public sealed class Seeder(TradeStore store)
{
    private long _written;

    public TradeStore Store => store;

    /// <summary>How long <see cref="GenerateAsync"/> took to write the corpus, and then to index it.</summary>
    public TimeSpan LoadTime { get; private set; }

    public TimeSpan IndexTime { get; private set; }

    /// <summary>What the corpus holds, for the planner's estimates.</summary>
    public long Orders { get; private set; }

    public long Versions { get; private set; }

    public long Fills { get; private set; }

    /// <summary>The latest event in the corpus: everything after it, the exchange wrote.</summary>
    public DateTime LoadedThrough { get; private set; }

    /// <summary>What <see cref="GenerateAsync"/> is doing now, for a console to show while it waits.</summary>
    public string Stage { get; private set; } = "";

    /// <summary>Orders <see cref="GenerateAsync"/> has written so far.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>
    /// Creates the tables and fills them with <paramref name="orders"/> client orders worked over the
    /// <paramref name="span"/> before <paramref name="end"/>, the newest <paramref name="openTail"/> left
    /// new; indexes them; and from then on keeps <c>orders_current</c> as <paramref name="sync"/> says.
    /// </summary>
    public async Task GenerateAsync(
        long orders, DateTime end, TimeSpan span, int openTail, CurrentSync sync, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        Stage = "creating the tables";
        store.CreateTables();

        var spacing = Math.Max(1, (long)(span.TotalMicroseconds / Math.Max(1, orders)));
        var origin = Cells.Micros(end - TimeSpan.FromMicroseconds(spacing * orders));
        var parts = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        Stage = string.Create(CultureInfo.InvariantCulture, $"writing {orders:N0} orders, their versions and fills with COPY");
        var size = (orders + parts - 1) / parts;
        var counted = await Task.WhenAll(Enumerable.Range(0, parts).Select(p => Task.Run(() =>
        {
            // Each part on three connections, one per table, so the three COPYs stream side by side.
            using var versions = store.Open();
            using var current = store.Open();
            using var fills = store.Open();
            using var versionsIn = versions.BeginBinaryImport("COPY order_versions FROM STDIN (FORMAT BINARY)");
            using var currentIn = current.BeginBinaryImport("COPY orders_current FROM STDIN (FORMAT BINARY)");
            using var fillsIn = fills.BeginBinaryImport("COPY fills FROM STDIN (FORMAT BINARY)");
            long versionCount = 0, fillCount = 0;
            var latest = DateTime.MinValue;
            for (var n = p * size; n < Math.Min(orders, (p + 1) * size); n++)
            {
                ct.ThrowIfCancellationRequested();
                var (made, filled) = Corpus.Make(n, origin, spacing, open: n >= orders - openTail);
                foreach (var version in made)
                {
                    TradeStore.WriteOrder(versionsIn, version);
                }

                TradeStore.WriteOrder(currentIn, made[^1]);
                foreach (var fill in filled)
                {
                    TradeStore.WriteFill(fillsIn, fill);
                }

                versionCount += made.Length;
                fillCount += filled.Length;
                if ((n & 4095) == 4095)
                {
                    Interlocked.Add(ref _written, 4096);
                }

                latest = made[^1].EventTime > latest ? made[^1].EventTime : latest;
            }

            versionsIn.Complete();
            currentIn.Complete();
            fillsIn.Complete();
            return (versionCount, fillCount, latest);
        }, ct))).ConfigureAwait(false);
        Orders = orders;
        Versions = counted.Sum(c => c.versionCount);
        Fills = counted.Sum(c => c.fillCount);
        LoadedThrough = counted.Max(c => c.latest);
        LoadTime = Stopwatch.GetElapsedTime(started);

        Interlocked.Exchange(ref _written, orders);
        Stage = "building seven indexes, then analysing";
        started = Stopwatch.GetTimestamp();
        await store.CreateIndexesAsync(ct).ConfigureAwait(false);
        IndexTime = Stopwatch.GetElapsedTime(started);
        store.KeepCurrent(sync);
    }
}
