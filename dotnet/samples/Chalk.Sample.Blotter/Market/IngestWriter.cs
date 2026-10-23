namespace Chalk.Sample.Blotter.Market;

/// <summary>When the database's writes land: a lag per version, and now and then a stall.</summary>
public sealed record IngestLag
{
    /// <summary>The least a version waits before it is visible in the database.</summary>
    public TimeSpan Min { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>The most it waits, outside a stall. Lags in between are spread towards the low end.</summary>
    public TimeSpan Max { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How often the ingestion stalls; zero for never.</summary>
    public TimeSpan StallEvery { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>How long a stall holds every write back.</summary>
    public TimeSpan StallFor { get; init; } = TimeSpan.FromSeconds(8);
}

/// <summary>
/// The database's ingestion: versions land some time after the feed has sent them, in no
/// particular order, each with its own lag. After every write the watermark is the latest time
/// through which nothing is still waiting, and it is written in the same transaction.
/// </summary>
internal sealed class IngestWriter
{
    private readonly Lock _gate = new();
    private readonly PriorityQueue<TradeVersion, DateTime> _due = new();
    private readonly SortedSet<DateTime> _waiting = [];
    private readonly TradeStore _store;
    private readonly IngestLag _lag;
    private readonly Random _random;
    private readonly DateTime _started = DateTime.UtcNow;
    private DateTime _lastEnqueued;
    private DateTime _stalledUntil;

    public IngestWriter(TradeStore store, IngestLag lag, DateTime completeThrough, int seed)
    {
        _store = store;
        _lag = lag;
        _random = new Random(seed);
        _lastEnqueued = completeThrough;
        CompleteThrough = completeThrough;
    }

    /// <summary>The watermark last written.</summary>
    public DateTime CompleteThrough { get; private set; }

    /// <summary>Versions sent on the feed and not yet in the database.</summary>
    public int Waiting
    {
        get
        {
            lock (_gate)
            {
                return _due.Count;
            }
        }
    }

    /// <summary>Whether the ingestion is stalled now.</summary>
    public bool Stalled => StallEnds(DateTime.UtcNow) is not null;

    /// <summary>Holds every write back until <paramref name="until"/>, versions already waiting included.</summary>
    public void StallUntil(DateTime until)
    {
        lock (_gate)
        {
            _stalledUntil = until;
        }
    }

    public void Enqueue(TradeVersion[] batch)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (var version in batch)
            {
                var lag = _lag.Min + (_lag.Max - _lag.Min) * Math.Pow(_random.NextDouble(), 3);
                var due = now + lag;
                if (StallEnds(due) is { } end)
                {
                    due = end + lag;
                }

                _due.Enqueue(version, due);
                _waiting.Add(version.EventTime);
                _lastEnqueued = version.EventTime;
            }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var now = DateTime.UtcNow;
            List<TradeVersion> ready = [];
            DateTime completeThrough;
            lock (_gate)
            {
                while (now >= _stalledUntil && _due.TryPeek(out var version, out var due) && due <= now)
                {
                    _due.Dequeue();
                    _waiting.Remove(version.EventTime);
                    ready.Add(version);
                }

                // Event times are unique and only increase, so nothing enqueued after this has a
                // time at or before the watermark.
                completeThrough = _waiting.Count == 0 ? _lastEnqueued : _waiting.Min.AddTicks(-10);
            }

            if (ready.Count > 0 || completeThrough > CompleteThrough)
            {
                _store.Write(ready, completeThrough);
                CompleteThrough = completeThrough;
            }
        }
    }

    private DateTime? StallEnds(DateTime at)
    {
        if (at < _stalledUntil)
        {
            return _stalledUntil;
        }

        if (_lag.StallEvery <= TimeSpan.Zero)
        {
            return null;
        }

        var into = (at - _started).Ticks % _lag.StallEvery.Ticks;
        var stallStarts = _lag.StallEvery - _lag.StallFor;
        return into >= stallStarts.Ticks ? at + TimeSpan.FromTicks(_lag.StallEvery.Ticks - into) : null;
    }
}
