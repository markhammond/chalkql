namespace Chalk.Sample.Blotter.Live;

/// <summary>
/// A snapshot of the database: the latest version of each trade as of <see cref="CompleteThrough"/>
/// at least, written once into <c>db.snapshots</c> under its number.
/// </summary>
/// <param name="Number">The generation, which page queries bind.</param>
/// <param name="CompleteThrough">The watermark read before the snapshot was taken: every version at or before it is in.</param>
/// <param name="Rows">One per trade.</param>
/// <param name="MaxTradeId">The highest trade id it holds: a trade with a higher id is not in it, and needs no lookup.</param>
/// <param name="BuildTime">How long the database took to write it.</param>
public sealed record Generation(long Number, DateTime CompleteThrough, long Rows, long MaxTradeId, TimeSpan BuildTime);

/// <summary>One trade in the tail.</summary>
public sealed class TailEntry(TradeVersion live, TradeVersion? snapshot)
{
    /// <summary>The latest version the feed has sent.</summary>
    public TradeVersion Live { get; internal set; } = live;

    /// <summary>The trade's row in the current generation, or null when the generation does not hold it.</summary>
    public TradeVersion? Base { get; internal set; } = snapshot;

    /// <summary>The versions the feed has sent that are newer than <see cref="Base"/>, oldest first.</summary>
    public List<TradeVersion> Versions { get; } = [];
}

/// <summary>
/// The live tail: every trade whose latest version the current generation does not have, with that
/// version and the generation's own row for it. Changed only by the host's ingest loop; the scopes
/// read it from that loop too, after each refresh.
/// </summary>
public sealed class Tail
{
    private readonly Dictionary<long, TailEntry> _entries = [];

    public int Count => _entries.Count;

    /// <summary>Versions held, the latest ones and those before them.</summary>
    public long VersionCount { get; private set; }

    /// <summary>What the tail holds, in bytes: its rows here and in ChalkQL, and its versions in both.</summary>
    public long Bytes => _entries.Count * (long)TailRow.Bytes + VersionCount * 2L * TradeVersion.Bytes;

    public TailEntry? Get(long tradeId) => _entries.GetValueOrDefault(tradeId);

    public IEnumerable<TailEntry> Entries => _entries.Values;

    internal void Set(TailEntry entry, TradeVersion version)
    {
        _entries[entry.Live.TradeId] = entry;
        entry.Versions.Add(version);
        VersionCount++;
    }

    internal void Remove(long tradeId)
    {
        if (_entries.Remove(tradeId, out var entry))
        {
            VersionCount -= entry.Versions.Count;
        }
    }

    /// <summary>Drops the versions at or before <paramref name="version"/>, which the new generation has.</summary>
    internal List<TradeVersion> Trim(TailEntry entry, int version)
    {
        var gone = entry.Versions.Where(v => v.Version <= version).ToList();
        entry.Versions.RemoveAll(v => v.Version <= version);
        VersionCount -= gone.Count;
        return gone;
    }

    internal void Clear()
    {
        _entries.Clear();
        VersionCount = 0;
    }
}
