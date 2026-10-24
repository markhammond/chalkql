namespace Chalk.Sample.Blotter.Fixtures;

/// <summary>How busy the brokerage is.</summary>
public sealed record ExchangeOptions
{
    /// <summary>Changes a second, on average, outside a burst: new orders, fills and cancels.</summary>
    public double Rate { get; init; } = 200;

    /// <summary>How often a burst comes; zero for never.</summary>
    public TimeSpan BurstEvery { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan BurstFor { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The rate in a burst, as a multiple of <see cref="Rate"/>.</summary>
    public double BurstFactor { get; init; } = 10;

    /// <summary>Of the changes, the share that are new orders rather than fills.</summary>
    public double NewShare { get; init; } = 0.3;

    /// <summary>Of new orders, the share filled at once: a marketable order, whose fill races its order.</summary>
    public double FilledAtOnce { get; init; } = 0.25;

    /// <summary>
    /// The most the order stream runs behind the fills stream: each order version is sent up to this
    /// long after it happened, while its fill is sent at once. So a fill often arrives before the order
    /// it fills, and before the version that counts it.
    /// </summary>
    public TimeSpan OrderSkew { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>How far back the streams can replay.</summary>
    public TimeSpan FeedRetention { get; init; } = TimeSpan.FromMinutes(30);

    public IngestLag Lag { get; init; } = new();

    /// <summary>
    /// How far the job that keeps <c>orders_current</c> runs behind <c>order_versions</c>, when it is
    /// a job of its own (<see cref="CurrentSync.Lagging"/>) rather than a trigger.
    /// </summary>
    public IngestLag CurrentLag { get; init; } = new()
    {
        Min = TimeSpan.FromMilliseconds(500),
        Max = TimeSpan.FromSeconds(4),
        StallEvery = TimeSpan.FromSeconds(45),
        StallFor = TimeSpan.FromSeconds(10),
    };

    public int Seed { get; init; } = 20261023;
}

/// <summary>
/// The simulated outside world: the house taking client orders and filling them; two streams, one of
/// order versions and one of fills, each sending its rows as they happen and neither waiting for the
/// other; and the ingestion jobs that write each to the database some time later, and, when
/// <c>orders_current</c> is kept by a job rather than a trigger, that job. The blotter sees all of it
/// only through the streams and the database.
/// </summary>
public sealed class Exchange : IAsyncDisposable
{
    private readonly ExchangeOptions _options;
    private readonly Random _random;
    private readonly List<Order> _open;
    private readonly IngestWriter<Order> _orderWriter;
    private readonly IngestWriter<Fill> _fillWriter;
    private readonly IngestWriter<Order>? _currentWriter;
    private readonly Queue<(Order Order, DateTime Release)> _held = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly double[] _mids;
    private readonly double[] _clientWeights;
    private Task? _running;
    private long _orders;
    private DateTime _lastEventTime;
    private DateTime _lastRelease;
    private volatile bool _paused;

    public Exchange(
        TradeStore store, ExchangeOptions options, long orders, DateTime lastEventTime, List<Order> open, CurrentSync sync)
    {
        _options = options;
        _random = new Random(options.Seed);
        _open = open;
        _orders = orders;
        _lastEventTime = lastEventTime;
        _mids = [.. Reference.Assets.Select(a => a.Price)];
        _clientWeights = [.. Reference.Clients.Select((_, i) => 1.0 / (i + 1))];
        Orders = new Feed<Order>("orders", options.FeedRetention);
        Fills = new Feed<Fill>("fills", options.FeedRetention);
        Sync = sync;

        // A lagging orders_current is worked from what order_versions has been written, some time after.
        if (sync == CurrentSync.Lagging)
        {
            var current = new IngestWriter<Order>(store.UpsertCurrent, options.CurrentLag, options.Seed + 3);
            _currentWriter = current;
            _orderWriter = new IngestWriter<Order>(
                written => { store.WriteOrders(written); current.Enqueue([.. written]); }, options.Lag, options.Seed + 1);
        }
        else
        {
            _orderWriter = new IngestWriter<Order>(store.WriteOrders, options.Lag, options.Seed + 1);
        }

        _fillWriter = new IngestWriter<Fill>(store.WriteFills, options.Lag, options.Seed + 2);
    }

    /// <summary>How <c>orders_current</c> keeps up.</summary>
    public CurrentSync Sync { get; }

    /// <summary>The stream of order versions.</summary>
    public Feed<Order> Orders { get; }

    /// <summary>The stream of fills.</summary>
    public Feed<Fill> Fills { get; }

    /// <summary>Rows the streams have sent that the database has not written yet.</summary>
    public int NotYetIngested => _orderWriter.Waiting + _fillWriter.Waiting;

    /// <summary>Versions written to <c>order_versions</c> that the lagging job has not applied to <c>orders_current</c> yet.</summary>
    public int CurrentWaiting => _currentWriter?.Waiting ?? 0;

    /// <summary>Order versions that have happened and that the order stream has not sent yet.</summary>
    public int OrdersHeld
    {
        get
        {
            lock (_gate)
            {
                return _held.Count;
            }
        }
    }

    public bool IngestionStalled => _orderWriter.Stalled || _fillWriter.Stalled;

    public bool CurrentStalled => _currentWriter?.Stalled ?? false;

    /// <summary>The database writes nothing for a while; the streams go on. Zero ends a stall.</summary>
    public void StallIngestion(TimeSpan time)
    {
        _orderWriter.StallUntil(DateTime.UtcNow + time);
        _fillWriter.StallUntil(DateTime.UtcNow + time);
    }

    /// <summary>The lagging job applies nothing to <c>orders_current</c> for a while. Zero ends a stall.</summary>
    public void StallCurrent(TimeSpan time) => _currentWriter?.StallUntil(DateTime.UtcNow + time);

    /// <summary>
    /// The house stops taking and working orders, for checks that need the streams quiet; versions
    /// held for the order stream's skew still go out, and the database goes on writing.
    /// </summary>
    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    /// <summary>Whether the streams have sent everything that has happened: paused, and nothing held for the skew.</summary>
    public bool Quiet => _paused && OrdersHeld == 0;

    /// <summary>Whether every row sent is in the database, <c>orders_current</c> included.</summary>
    public bool Written => NotYetIngested == 0 && CurrentWaiting == 0;

    /// <summary>The rate the exchange is running at now, changes a second.</summary>
    public double CurrentRate { get; private set; }

    public void Start()
    {
        _running = Task.WhenAll(
            Task.Run(() => TradeAsync(_stop.Token)),
            Task.Run(() => _orderWriter.RunAsync(_stop.Token)),
            Task.Run(() => _fillWriter.RunAsync(_stop.Token)),
            _currentWriter is { } current ? Task.Run(() => current.RunAsync(_stop.Token)) : Task.CompletedTask);
    }

    /// <summary>
    /// Makes <paramref name="count"/> changes at once, now: for tests, and for a page that wants a burst.
    /// The fills are sent at once; the order versions when the order stream's skew lets them go.
    /// </summary>
    public (Order[] Orders, Fill[] Fills) Send(int count)
    {
        var orders = new List<Order>(count + 8);
        var fills = new List<Fill>(count);
        lock (_gate)
        {
            for (var i = 0; i < count; i++)
            {
                Next(orders, fills);
            }

            // The stream first, then the database: what is in the database has always been sent.
            var batch = fills.ToArray();
            Fills.Publish(batch);
            _fillWriter.Enqueue(batch);
            foreach (var order in orders)
            {
                // Held for a skew of its own, but never sent before a version that happened before it.
                var release = order.EventTime + _options.OrderSkew * _random.NextDouble();
                _lastRelease = release > _lastRelease ? release : _lastRelease;
                _held.Enqueue((order, _lastRelease));
            }

            ReleaseOrders(DateTime.UtcNow);
        }

        return ([.. orders], [.. fills]);
    }

    /// <summary>Sends every held order version whose time has come, in the order they happened.</summary>
    private void ReleaseOrders(DateTime now)
    {
        var due = new List<Order>();
        while (_held.TryPeek(out var held) && held.Release <= now)
        {
            due.Add(_held.Dequeue().Order);
        }

        var batch = due.ToArray();
        Orders.Publish(batch);
        _orderWriter.Enqueue(batch);
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
            var count = _paused ? 0 : (int)owed;
            owed -= _paused ? owed : count;
            if (count > 0)
            {
                Send(count);
            }
            else
            {
                lock (_gate)
                {
                    ReleaseOrders(DateTime.UtcNow);
                }
            }
        }
    }

    /// <summary>One change: a new order (filled at once, now and then), a fill, or a cancel.</summary>
    private void Next(List<Order> orders, List<Fill> fills)
    {
        for (var a = 0; a < _mids.Length; a++)
        {
            _mids[a] *= 1 + (_random.NextDouble() - 0.5) * 0.0004;
        }

        var fill = _open.Count > 0 && (_random.NextDouble() >= _options.NewShare || _open.Count > 5000);
        if (!fill)
        {
            var rank = Pick(_clientWeights) + 1;
            var (reference, _, region) = Reference.Clients[rank - 1];
            var channel = Pick(Reference.ChannelsOf(rank));
            var asset = Pick([.. Reference.Assets.Select(a => a.Weight)]);
            var time = Tick();
            var order = new Order(
                Guid.CreateVersion7(new DateTimeOffset(time, TimeSpan.Zero)), 1, time, time, $"{reference}-{_orders++}", reference,
                region, channel, Reference.Instruments[asset],
                Reference.Sides[_random.Next(2)], (1 + _random.Next(6)) * Reference.Assets[asset].Lot, 0, 0,
                Reference.MarkupBps(channel), Reference.Statuses[0]);
            orders.Add(order);
            if (_random.NextDouble() < _options.FilledAtOnce)
            {
                Work(order, orders, fills, cancel: false);
            }
            else
            {
                _open.Add(order);
            }

            return;
        }

        var at = _random.Next(_open.Count);
        var before = _open[at];
        _open[at] = _open[^1];
        _open.RemoveAt(_open.Count - 1);
        Work(before, orders, fills, cancel: _random.NextDouble() < 0.03);
    }

    /// <summary>Fills or cancels an order, and keeps it open if it is still working.</summary>
    private void Work(Order before, List<Order> orders, List<Fill> fills, bool cancel)
    {
        var time = Tick();
        Order after;
        if (cancel)
        {
            after = before with { Version = before.Version + 1, EventTime = time, Status = Reference.Statuses[3] };
        }
        else
        {
            var asset = Reference.AssetOf(before.Instrument);
            var lot = Reference.Assets[asset].Lot;
            var left = (int)Math.Round((before.Qty - before.FilledQty) / lot);
            var qty = (1 + _random.Next(Math.Max(1, left))) * lot;
            var mid = Math.Round(_mids[asset], 6);
            var price = Math.Round(mid * (1 + (before.Side == "Buy" ? 1 : -1) * before.MarkupBps / 10_000), 4);
            var filled = before.FilledQty + qty;
            after = before with
            {
                Version = before.Version + 1,
                EventTime = time,
                FilledQty = filled,
                AvgPrice = Math.Round((before.AvgPrice * before.FilledQty + price * qty) / filled, 4),
                Status = filled >= before.Qty - 1e-9 ? Reference.Statuses[2] : Reference.Statuses[1],
            };
            fills.Add(new Fill(
                Guid.CreateVersion7(new DateTimeOffset(time, TimeSpan.Zero)), after.OrderId, time, after.ClOrdId, after.Client, after.Region, after.Channel,
                after.Instrument, after.Side, qty, price, mid, after.MarkupBps));
        }

        orders.Add(after);
        if (after.Status is not ("Filled" or "Cancelled"))
        {
            _open.Add(after);
        }
    }

    /// <summary>The next event time: one clock, strictly increasing, never two changes at one microsecond.</summary>
    private DateTime Tick()
    {
        var now = Cells.Micros(DateTime.UtcNow);
        _lastEventTime = now > _lastEventTime ? now : _lastEventTime.AddTicks(10);
        return _lastEventTime;
    }

    private int Pick(double[] weights)
    {
        var r = _random.NextDouble() * weights.Sum();
        for (var i = 0; i < weights.Length - 1; i++)
        {
            if ((r -= weights[i]) < 0)
            {
                return i;
            }
        }

        return weights.Length - 1;
    }

    private string Pick((string Name, double Weight)[] names) => names[Pick([.. names.Select(n => n.Weight)])].Name;
}
