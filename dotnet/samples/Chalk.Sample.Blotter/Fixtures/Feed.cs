using System.Threading.Channels;

namespace Chalk.Sample.Blotter.Fixtures;

/// <summary>
///     A real-time stream: every row, as it happens, to every subscriber. It is reliable — a subscriber
///     misses nothing from the moment it subscribes — but it does not replay: what was sent before a
///     subscription is the database's to tell. Order versions and fills come on two streams of their
///     own, neither waiting for the other, so a fill can arrive before the order it fills; each mostly in
///     time order, but a row sent late arrives after rows that happened later.
/// </summary>
/// <remarks>
///     The stream keeps what it has sent for a while, but only for checking: the oracle reads it to know
///     what a subscriber has been sent. A subscriber never sees it.
/// </remarks>
public sealed class Feed<T> where T : IRow
{
    private readonly Lock _gate = new();
    private readonly List<T> _log = [];
    private readonly TimeSpan _retention;
    private readonly List<Channel<T[]>> _subscribers = [];

    // Rows trimmed from the front of the log: the log's first row is the stream's row this many in.
    private long _dropped;

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
    ///     Subscribes from now: every batch the reader gets was published after this call, and
    ///     <paramref name="before" /> is how many rows were published before it.
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

    /// <summary>
    ///     The first <paramref name="count" /> rows the stream published, counted from its start, as far as
    ///     it still keeps them: for checking. By count, not by time, since a row sent late is published
    ///     after rows that happened later.
    /// </summary>
    public T[] Read(long count)
    {
        lock (_gate)
        {
            var kept = (int)Math.Clamp(count - _dropped, 0, _log.Count);
            return _log.GetRange(0, kept).ToArray();
        }
    }

    public void Unsubscribe(ChannelReader<T[]> reader)
    {
        lock (_gate)
        {
            _subscribers.RemoveAll(c => c.Reader == reader);
        }
    }

    /// <summary>Publishes a batch, after everything published before, to every subscriber at once.</summary>
    internal void Publish(T[] batch)
    {
        if (batch.Length == 0) return;

        lock (_gate)
        {
            _log.AddRange(batch);
            Published += batch.Length;
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(batch);

            // Trimmed in bulk, half at a time once the middle is past the retention, so the log is
            // copied down rarely rather than on every batch; what is dropped is counted.
            var horizon = batch[^1].EventTime - _retention;
            if (_log.Count > 4096 && _log[_log.Count / 2].EventTime < horizon)
            {
                var dropped = _log.Count / 2;
                _log.RemoveRange(0, dropped);
                _dropped += dropped;
            }
        }
    }
}