using System.Threading.Channels;

namespace Chalk.Sample.Blotter.Market;

/// <summary>
/// A real-time stream: every row, as it happens, to every subscriber. It is reliable — a subscriber
/// misses nothing from the moment it subscribes — and it replays: a subscriber names a time and is
/// sent every row after it first, then the live stream, with nothing missed or repeated between the
/// two. Order versions and fills come on two streams of their own, each in its own time order and
/// neither waiting for the other, so a fill can arrive before the order it fills.
/// </summary>
/// <remarks>
/// Replay is what makes a snapshot plus the stream whole. The database is complete only through its
/// watermark for the stream, so a subscriber that snapshots the database and replays from that
/// watermark has every row: those at or before it in the snapshot, those after it from the stream,
/// and those that are both, the in-flight crossing, told apart by version or by watermark.
/// </remarks>
public sealed class Feed<T> where T : IRow
{
    private readonly Lock _gate = new();
    private readonly List<T> _log = [];
    private readonly List<Channel<T[]>> _subscribers = [];
    private readonly TimeSpan _retention;
    private DateTime _retainedFrom;

    public Feed(string name, DateTime start, TimeSpan retention)
    {
        Name = name;
        _retainedFrom = start;
        _retention = retention;
    }

    /// <summary>"orders" or "fills", for messages.</summary>
    public string Name { get; }

    /// <summary>How many rows the stream has published.</summary>
    public long Published { get; private set; }

    /// <summary>
    /// Subscribes from <paramref name="after"/>: the reader's first batch is every row with an event time
    /// after it, possibly none, and every batch after that is live.
    /// </summary>
    /// <exception cref="FeedReplayUnavailableException">The stream no longer holds rows that far back.</exception>
    public ChannelReader<T[]> Subscribe(DateTime after)
    {
        lock (_gate)
        {
            if (after < _retainedFrom)
            {
                throw new FeedReplayUnavailableException(Name, after, _retainedFrom);
            }

            var channel = Channel.CreateUnbounded<T[]>(new UnboundedChannelOptions { SingleReader = true });
            var first = FirstAfter(after);
            channel.Writer.TryWrite(_log.GetRange(first, _log.Count - first).ToArray());
            _subscribers.Add(channel);
            return channel.Reader;
        }
    }

    /// <summary>How many rows a subscription from <paramref name="after"/> would be replayed.</summary>
    public long CountAfter(DateTime after)
    {
        lock (_gate)
        {
            return _log.Count - FirstAfter(after);
        }
    }

    /// <summary>Every row the stream still holds sent at or before <paramref name="through"/>: for checking.</summary>
    public T[] Through(DateTime through)
    {
        lock (_gate)
        {
            return _log.GetRange(0, FirstAfter(through)).ToArray();
        }
    }

    public void Unsubscribe(ChannelReader<T[]> reader)
    {
        lock (_gate)
        {
            _subscribers.RemoveAll(c => c.Reader == reader);
        }
    }

    /// <summary>Publishes a batch, in event-time order and after everything published before, to every subscriber at once.</summary>
    internal void Publish(T[] batch)
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
                // Dropping a row at time t leaves the stream whole only for replays from t on.
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

/// <summary>A replay from before what a stream still holds: the subscriber cannot be made whole.</summary>
public sealed class FeedReplayUnavailableException(string stream, DateTime after, DateTime retainedFrom)
    : InvalidOperationException(
        $"the {stream} stream cannot replay from {after:O}: it holds rows from {retainedFrom:O} only, "
        + "so a snapshot that old cannot be made current")
{
    public DateTime After { get; } = after;

    public DateTime RetainedFrom { get; } = retainedFrom;
}
