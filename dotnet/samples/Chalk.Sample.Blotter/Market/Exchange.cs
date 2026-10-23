namespace Chalk.Sample.Blotter.Market;

/// <summary>How busy the brokerage is.</summary>
public sealed record ExchangeOptions
{
    /// <summary>Versions a second, on average, outside a burst.</summary>
    public double Rate { get; init; } = 200;

    /// <summary>How often a burst comes; zero for never.</summary>
    public TimeSpan BurstEvery { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan BurstFor { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The rate in a burst, as a multiple of <see cref="Rate"/>.</summary>
    public double BurstFactor { get; init; } = 10;

    /// <summary>Of the versions the exchange sends, the share that are new trades rather than fills.</summary>
    public double NewShare { get; init; } = 0.3;

    /// <summary>How far back the feed can replay.</summary>
    public TimeSpan FeedRetention { get; init; } = TimeSpan.FromMinutes(30);

    public IngestLag Lag { get; init; } = new();

    public int Seed { get; init; } = 20261023;
}

/// <summary>
/// The simulated outside world: an exchange that takes new orders and fills them, a feed that
/// sends every version the moment it exists, and an ingestion job that writes each to the
/// database some time later. The blotter sees the first two only through the feed and the database.
/// </summary>
public sealed class Exchange : IAsyncDisposable
{
    private readonly ExchangeOptions _options;
    private readonly Random _random;
    private readonly List<TradeVersion> _open;
    private readonly IngestWriter _writer;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _truthGate = new();
    private readonly Dictionary<long, TradeVersion> _changed = [];
    private Task? _running;
    private long _lastTradeId;
    private DateTime _lastEventTime;

    public Exchange(TradeStore store, ExchangeOptions options, long lastTradeId, DateTime lastEventTime, List<TradeVersion> open)
    {
        _options = options;
        _random = new Random(options.Seed);
        _open = open;
        _lastTradeId = lastTradeId;
        _lastEventTime = lastEventTime;
        Feed = new TradeFeed(lastEventTime, options.FeedRetention);
        _writer = new IngestWriter(store, options.Lag, lastEventTime, options.Seed + 1);
    }

    public TradeFeed Feed { get; }

    /// <summary>The database's watermark, as the ingestion last wrote it.</summary>
    public DateTime CompleteThrough => _writer.CompleteThrough;

    /// <summary>Versions the feed has sent that the database does not show yet.</summary>
    public int NotYetIngested => _writer.Waiting;

    public bool IngestionStalled => _writer.Stalled;

    /// <summary>The database writes nothing for a while; the feed goes on.</summary>
    public void StallIngestion(TimeSpan time) => _writer.StallUntil(DateTime.UtcNow + time);

    /// <summary>The rate the exchange is running at now, versions a second.</summary>
    public double CurrentRate { get; private set; }

    /// <summary>The trades open at the exchange, which fills come from.</summary>
    public int OpenTrades => _open.Count;

    /// <summary>
    /// The truth, for checking: the latest version of every trade the exchange has changed since it
    /// started. Trades it has not touched are as the corpus left them.
    /// </summary>
    public IReadOnlyDictionary<long, TradeVersion> ChangedSinceStart()
    {
        lock (_truthGate)
        {
            return new Dictionary<long, TradeVersion>(_changed);
        }
    }

    public void Start()
    {
        _running = Task.WhenAll(
            Task.Run(() => TradeAsync(_stop.Token)),
            Task.Run(() => _writer.RunAsync(_stop.Token)));
    }

    /// <summary>Sends <paramref name="count"/> versions at once, now: for tests, and for a page that wants a burst.</summary>
    public TradeVersion[] Send(int count)
    {
        var batch = new TradeVersion[count];
        lock (_truthGate)
        {
            for (var i = 0; i < count; i++)
            {
                batch[i] = Next();
                _changed[batch[i].TradeId] = batch[i];
            }

            // The feed first, then the database: what is in the database has always been sent.
            Feed.Publish(batch);
            _writer.Enqueue(batch);
        }

        return batch;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_running is not null)
        {
            try
            {
                await _running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task TradeAsync(CancellationToken ct)
    {
        var tick = TimeSpan.FromMilliseconds(10);
        using var timer = new PeriodicTimer(tick);
        var started = DateTime.UtcNow;
        var owed = 0.0;
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var into = _options.BurstEvery > TimeSpan.Zero ? (DateTime.UtcNow - started).Ticks % _options.BurstEvery.Ticks : 0;
            var bursting = _options.BurstEvery > TimeSpan.Zero && into < _options.BurstFor.Ticks;
            CurrentRate = _options.Rate * (bursting ? _options.BurstFactor : 1);
            owed += CurrentRate * tick.TotalSeconds;
            var count = (int)owed;
            owed -= count;
            if (count > 0)
            {
                Send(count);
            }
        }
    }

    private TradeVersion Next()
    {
        // Strictly increasing event times: one clock, never two versions at one microsecond.
        var now = TradeVersion.Micros(DateTime.UtcNow);
        _lastEventTime = now > _lastEventTime ? now : _lastEventTime.AddTicks(10);

        var fill = _open.Count > 0 && (_random.NextDouble() >= _options.NewShare || _open.Count > 5000);
        if (!fill)
        {
            var qty = (100 + _random.Next(99)) * 100L;
            var trade = new TradeVersion(
                ++_lastTradeId,
                1,
                _lastEventTime,
                _lastEventTime,
                Pick(Reference.Clients),
                Pick(Reference.Symbols),
                Pick(Reference.Sides),
                qty,
                0,
                0,
                Reference.Statuses[0],
                Pick(Reference.Venues));
            _open.Add(trade);
            return trade;
        }

        var at = _random.Next(_open.Count);
        var before = _open[at];
        TradeVersion after;
        if (_random.NextDouble() < 0.03)
        {
            after = before with { Version = before.Version + 1, EventTime = _lastEventTime, Status = Reference.Statuses[3] };
        }
        else
        {
            var chunk = Math.Max(100, (long)(before.OrderQty * (0.1 + _random.NextDouble() * 0.5)) / 100 * 100);
            var filled = Math.Min(before.OrderQty, before.FilledQty + chunk);
            var price = before.AvgPrice > 0 ? before.AvgPrice : 10 + _random.Next(100_000) / 100.0;
            var fillPrice = price * (1 + (_random.NextDouble() - 0.5) * 0.002);
            var average = Math.Round((before.AvgPrice * before.FilledQty + fillPrice * (filled - before.FilledQty)) / filled, 4);
            after = before with
            {
                Version = before.Version + 1,
                EventTime = _lastEventTime,
                FilledQty = filled,
                AvgPrice = average,
                Status = filled == before.OrderQty ? Reference.Statuses[2] : Reference.Statuses[1],
                Venue = Pick(Reference.Venues),
            };
        }

        if (after.Status is "Filled" or "Cancelled")
        {
            _open[at] = _open[^1];
            _open.RemoveAt(_open.Count - 1);
        }
        else
        {
            _open[at] = after;
        }

        return after;
    }

    private string Pick(IReadOnlyList<string> values) => values[_random.Next(values.Count)];
}
