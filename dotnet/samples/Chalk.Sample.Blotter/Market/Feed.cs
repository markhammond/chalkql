using System.Threading.Channels;

namespace Chalk.Sample.Blotter.Market;

/// <summary>
/// A real-time stream: every row, as it happens, to every subscriber. It is reliable — a subscriber
/// misses nothing from the moment it subscribes — but it does not replay: what was sent before a
/// subscription is the database's to tell. Order versions and fills come on two streams of their
/// own, each in its own time order and neither waiting for the other, so a fill can arrive before the
/// order it fills.
/// </summary>
/// <remarks>
/// The stream keeps what it has sent for a while, but only for checking: the oracle reads it to know
/// what a subscriber has been sent. A subscriber never sees it.
/// </remarks>
public sealed class Feed<T> where T : IRow
{
    private readonly Lock _gate = new();
    private readonly List<T> _log = [];
    private readonly List<Channel<T[]>> _subscribers = [];
    private readonly TimeSpan _retention;

    public Feed(string name, TimeSpan retention)
    {
        Name = name;
        _retention = retention;
    }

    /// <summary>"orders" or "fills", for messages.</summary>
    public string Name { get; }

    /// <summary>How many rows the stream has published.</summary>
    public long Published { get; private set; }

    /// <summary>
    /// Subscribes from now: every batch the reader gets was published after this call, and
    /// <paramref name="before"/> is how many rows were published before it.
    /// </summary>
    public ChannelReader<T[]> Subscribe(out long before)
    {
        lock (_gate)
        {
            var channel = Channel.CreateUnbounded<T[]>(new UnboundedChannelOptions { SingleReader = true });
            _subscribers.Add(channel);
            before = Published;
            return channel.Reader;
        }
    }

    /// <summary>Every row the stream still keeps sent at or before <paramref name="through"/>: for checking.</summary>
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
                _log.RemoveRange(0, FirstAfter(horizon));
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
