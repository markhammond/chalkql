using System.Threading.Channels;

namespace Chalk.Sample.Blotter.Market;

/// <summary>
/// The real-time trade feed: every version, as it happens, to every subscriber. It is reliable — a
/// subscriber misses nothing from the moment it subscribes — and it replays: a subscriber names a
/// time and is sent every version after it first, then the live stream, with nothing missed or
/// repeated between the two.
/// </summary>
/// <remarks>
/// Replay is what makes a snapshot plus the stream whole. The database is complete only through
/// its watermark, so a subscriber that snapshots the database and replays from the watermark has
/// every version: those at or before it in the snapshot, those after it from the feed, and those
/// that are both, the in-flight crossing, told apart by version.
/// </remarks>
public sealed class TradeFeed
{
    private readonly Lock _gate = new();
    private readonly List<TradeVersion> _log = [];
    private readonly List<Channel<TradeVersion[]>> _subscribers = [];
    private readonly TimeSpan _retention;
    private DateTime _retainedFrom;

    public TradeFeed(DateTime start, TimeSpan retention)
    {
        _retainedFrom = start;
        _retention = retention;
    }

    /// <summary>How many versions the feed has published.</summary>
    public long Published { get; private set; }

    /// <summary>The earliest time a subscriber may replay from.</summary>
    public DateTime RetainedFrom
    {
        get
        {
            lock (_gate)
            {
                return _retainedFrom;
            }
        }
    }

    /// <summary>
    /// Subscribes from <paramref name="after"/>: the reader's first batch is every version with an
    /// event time after it, possibly none, and every batch after that is live.
    /// </summary>
    /// <exception cref="FeedReplayUnavailableException">The feed no longer holds versions that far back.</exception>
    public ChannelReader<TradeVersion[]> Subscribe(DateTime after)
    {
        lock (_gate)
        {
            if (after < _retainedFrom)
            {
                throw new FeedReplayUnavailableException(after, _retainedFrom);
            }

            var channel = Channel.CreateUnbounded<TradeVersion[]>(new UnboundedChannelOptions { SingleReader = true });
            var first = FirstAfter(after);
            channel.Writer.TryWrite(_log.GetRange(first, _log.Count - first).ToArray());
            _subscribers.Add(channel);
            return channel.Reader;
        }
    }

    /// <summary>How many versions a subscription from <paramref name="after"/> would be replayed.</summary>
    public long CountAfter(DateTime after)
    {
        lock (_gate)
        {
            return _log.Count - FirstAfter(after);
        }
    }

    /// <summary>Every version the feed still holds sent at or before <paramref name="through"/>: for checking.</summary>
    public TradeVersion[] Through(DateTime through)
    {
        lock (_gate)
        {
            return _log.GetRange(0, FirstAfter(through)).ToArray();
        }
    }

    public void Unsubscribe(ChannelReader<TradeVersion[]> reader)
    {
        lock (_gate)
        {
            _subscribers.RemoveAll(c => c.Reader == reader);
        }
    }

    /// <summary>Publishes a batch, in event-time order, to every subscriber at once.</summary>
    internal void Publish(TradeVersion[] batch)
    {
        if (batch.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _log.AddRange(batch);
            Published += batch.Length;
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(batch);
            }

            // Trimmed in bulk, so the log is copied down rarely rather than on every batch.
            var horizon = batch[^1].EventTime - _retention;
            if (_log.Count > 4096 && _log[_log.Count / 2].EventTime < horizon)
            {
                // Dropping a version at time t leaves the feed whole only for replays from t on.
                var dropped = FirstAfter(horizon);
                _retainedFrom = _log[dropped - 1].EventTime;
                _log.RemoveRange(0, dropped);
            }
        }
    }

    private int FirstAfter(DateTime after)
    {
        int low = 0, high = _log.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (_log[middle].EventTime <= after)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}

/// <summary>A replay from before what the feed still holds: the subscriber cannot be made whole.</summary>
public sealed class FeedReplayUnavailableException(DateTime after, DateTime retainedFrom)
    : InvalidOperationException(
        $"the feed cannot replay from {after:O}: it holds versions from {retainedFrom:O} only, "
        + "so a snapshot that old cannot be made current")
{
    public DateTime After { get; } = after;

    public DateTime RetainedFrom { get; } = retainedFrom;
}
