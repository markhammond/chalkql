using Chalk.Sources;

namespace Chalk.Client;

/// <summary>
/// The engine's epochs as a host numbers them, its clock, and what a standing view needs to read
/// one epoch whole (D339, <c>docs/design/64-incremental-views.md</c> §3).
/// </summary>
public sealed partial class ChalkEngine
{
    /// <summary>The version and clock published with the current catalog. Replaced whole.</summary>
    private EpochStamp _stamp = EpochStamp.First;

    /// <summary>How many refreshes this engine has committed, which each stamp records (design 78 §1).</summary>
    private readonly EngineCommits _engineCommits = new();

    /// <summary>
    /// What the refresh being published wrote, set by it before it publishes and taken by
    /// <see cref="Stamped"/> (design 78 §2): null, as for a re-introspection, where it is not known.
    /// </summary>
    private ((string SourceId, string Table)[] Tables, bool MovesClock)? _writing;

    /// <summary>The last write set recorded, which the next refresh shares when it writes the same tables.</summary>
    private (string SourceId, string Table)[] _lastWrites = [];

    /// <summary>The last stamps published, each at its epoch's place, for a view to see what they wrote (design 78 §2).</summary>
    private readonly EpochStamp?[] _recent = new EpochStamp?[RecentStamps];

    private const int RecentStamps = 256;

    /// <summary>The version epoch <paramref name="epoch"/> was published at, while it is recent; null otherwise.</summary>
    public long? VersionOf(long epoch)
    {
        var stamp = Volatile.Read(ref _recent[epoch % RecentStamps]);
        return stamp?.Epoch == epoch ? stamp.Version : null;
    }

    /// <summary>
    /// Whether every epoch after <paramref name="from"/>, up to <paramref name="to"/>, wrote nothing a reader
    /// of <paramref name="reads"/> reads (design 78 §2): not one of its tables, and not the clock where it
    /// reads the clock. False where an epoch is no longer recent, or did not say what it wrote.
    /// </summary>
    internal bool Untouched(long from, long to, Func<(string SourceId, string Table), bool> reads, bool readsClock)
    {
        if (to - from >= RecentStamps)
        {
            return false;
        }

        for (var epoch = from + 1; epoch <= to; epoch++)
        {
            var stamp = Volatile.Read(ref _recent[epoch % RecentStamps]);
            if (stamp?.Epoch != epoch || stamp.Writes is not { } writes || (readsClock && stamp.MovesClock))
            {
                return false;
            }

            foreach (var table in writes)
            {
                if (reads(table))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The tables <paramref name="entries"/> write, each once (design 78 §2). A refresh that writes the tables
    /// the last one wrote shares its array, so a feed that writes the same tables each time records them
    /// without allocating. Not pooled: a view may still be reading a stamp's array when the stamp's place in
    /// the ring is taken. The caller holds the refresh lock.
    /// </summary>
    private (string SourceId, string Table)[] WriteSet(IReadOnlyList<RefreshBuilder.RefreshEntry> entries)
    {
        var last = _lastWrites;
        var distinct = 0;
        var same = true;
        for (var i = 0; i < entries.Count; i++)
        {
            if (!FirstWrite(entries, i))
            {
                continue;
            }

            distinct++;
            same = same && Contains(last, entries[i].Source.SourceId, entries[i].Entry.Table);
        }

        if (same && distinct == last.Length)
        {
            return last;
        }

        var tables = new (string SourceId, string Table)[distinct];
        var at = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            if (FirstWrite(entries, i))
            {
                tables[at++] = (entries[i].Source.SourceId, entries[i].Entry.Table);
            }
        }

        _lastWrites = tables;
        return tables;
    }

    /// <summary>Whether entry <paramref name="index"/> is the first to write its table.</summary>
    private static bool FirstWrite(IReadOnlyList<RefreshBuilder.RefreshEntry> entries, int index)
    {
        var (source, table) = (entries[index].Source.SourceId, entries[index].Entry.Table);
        for (var i = 0; i < index; i++)
        {
            if (TableNames.Instance.Equals((entries[i].Source.SourceId, entries[i].Entry.Table), (source, table)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains((string SourceId, string Table)[] tables, string sourceId, string table)
    {
        foreach (var held in tables)
        {
            if (TableNames.Instance.Equals(held, (sourceId, table)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Completes when the next epoch is published.</summary>
    /// <remarks>
    /// Made only when someone waits, so a refresh that nobody waits on allocates nothing for it. A waiter
    /// takes the task before it reads the stamp, and a publish writes the stamp before it completes the task,
    /// so a waiter sees the new stamp or is woken by it.
    /// </remarks>
    internal Task NextPublished
    {
        get
        {
            if (Volatile.Read(ref _published) is { } waited)
            {
                return waited.Task;
            }

            var made = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return (Interlocked.CompareExchange(ref _published, made, null) ?? made).Task;
        }
    }

    /// <summary>The views' holds on refreshes they have yet to publish (design 78 §4, §5). Replaced whole.</summary>
    private EpochRetainer[] _retainers = [];

    private readonly Lock _retainersGate = new();

    /// <summary>
    /// Holds, for a standing view, each later barrier and, with <paramref name="every"/>, each later refresh
    /// (design 78 §4, §5): the states each commits of <paramref name="tables"/>, until the view has published
    /// it. Under the gate, so that every refresh after <see cref="EpochRetainer.From"/> is held as it must be.
    /// </summary>
    internal EpochRetainer Retain(
        (string SourceId, string Table, ITrackedSource Source)[] tables, bool every, int maxBacklog)
    {
        var retainer = new EpochRetainer(this, tables, every, maxBacklog);
        lock (ExecutionReadSet.Gate)
        {
            retainer.From = _engineCommits.Committed;
            lock (_retainersGate)
            {
                _retainers = [.. _retainers, retainer];
            }
        }

        return retainer;
    }

    /// <summary>Stops holding refreshes for <paramref name="retainer"/>.</summary>
    internal void LetGo(EpochRetainer retainer)
    {
        lock (_retainersGate)
        {
            _retainers = [.. _retainers.Where(r => !ReferenceEquals(r, retainer))];
        }
    }

    /// <summary>
    /// Holds the refresh that has just committed for each view that must publish it itself: every view for a
    /// barrier, and a view that publishes every version for any. The caller holds the gate.
    /// </summary>
    private void HoldCommitted(bool barrier)
    {
        var retainers = Volatile.Read(ref _retainers);
        if (retainers.Length == 0)
        {
            return;
        }

        var commit = _engineCommits.Committed;
        var clock = _engineClock?.Committed;
        foreach (var retainer in retainers)
        {
            if (barrier || retainer.Every)
            {
                retainer.Hold(commit, clock, barrier);
            }
        }
    }

    /// <summary>Lets go, after the gate, of what views that fell too far behind held.</summary>
    private void LetGoOfOverrun()
    {
        foreach (var retainer in Volatile.Read(ref _retainers))
        {
            retainer.LetGoIfOverrun();
        }
    }

    /// <summary>
    /// Odd while a refresh is making new rows visible — swapping snapshots and publishing the next
    /// catalog — and even otherwise. A reader that saw one even value before it read and the same
    /// value after has read one epoch, whatever order its scans ran in.
    /// </summary>
    private long _changes;

    /// <summary>What a waiter for the next epoch awaits, completed and let go when it is published; null while none waits.</summary>
    private TaskCompletionSource? _published;

    /// <summary>
    /// The version of the data this engine serves (D339). A refresh numbers its epoch with
    /// <see cref="RefreshBuilder.Version"/>, which must exceed this; any other refresh adds one. Zero
    /// until the first refresh.
    /// </summary>
    public long Version => Volatile.Read(ref _stamp).Version;

    /// <summary>
    /// The time on the engine's clock (<see cref="ChalkEngineOptions.Clock"/>) as of the current
    /// epoch, or null before an epoch has advanced it. It moves only forward, and only in a refresh
    /// (<see cref="RefreshBuilder.AdvanceClock"/>).
    /// </summary>
    public DateTimeOffset? ClockTime => Volatile.Read(ref _stamp).Clock;

    /// <summary>The epoch, version and clock a reader sees together.</summary>
    internal EpochStamp Stamp => Volatile.Read(ref _stamp);

    /// <summary>The name the engine's clock answers to in a view, or null when it has none.</summary>
    internal string? ClockName => _options.Clock;

    /// <summary>See <see cref="_changes"/>.</summary>
    internal long Changes => Interlocked.Read(ref _changes);

    /// <summary>A source this engine serves, by its id, or null.</summary>
    internal ISourceRuntime? SourceById(string sourceId) =>
        _sources.TryGetValue(sourceId, out var source) ? source : null;

    /// <summary>Completes when no refresh is publishing.</summary>
    internal async Task WhenQuietAsync(CancellationToken ct)
    {
        await _refreshing.WaitAsync(ct).ConfigureAwait(false);
        _refreshing.Release();
    }

    /// <summary>Runs <paramref name="body"/> with no refresh able to start until it is done.</summary>
    internal async ValueTask<T> WithoutRefreshAsync<T>(Func<ValueTask<T>> body, CancellationToken ct)
    {
        await _refreshing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await body().ConfigureAwait(false);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>Completes once the engine has published <paramref name="version"/> or a later one.</summary>
    internal async Task WhenVersionAsync(long version, CancellationToken ct)
    {
        while (true)
        {
            var published = NextPublished;
            if (Stamp.Version >= version)
            {
                return;
            }

            await published.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Marks the start of a refresh's visible changes. The caller holds the refresh lock.</summary>
    private void BeginChanges() => Interlocked.Increment(ref _changes);

    /// <summary>Marks their end, whether the refresh published or failed.</summary>
    private void EndChanges() => Interlocked.Increment(ref _changes);

    /// <summary>
    /// Refuses a version or clock time a refresh cannot take, before anything is applied. The caller
    /// holds the refresh lock, so the stamp it reads is the one the refresh would follow.
    /// </summary>
    private void CheckStamp(long? version, DateTimeOffset? clock)
    {
        var current = Stamp;
        if (version is { } next && next <= current.Version)
        {
            throw new ArgumentException(
                $"the refresh is numbered version {next}, and the engine is already at version "
                + $"{current.Version}. Each refresh's version must exceed the last; a refresh without "
                + "one is numbered one past it, so number every refresh or none. Nothing was applied.",
                nameof(version));
        }

        if (clock is { } time && current.Clock is { } now && time < now)
        {
            throw new ArgumentException(
                $"the refresh would move the clock back, from {now:O} to {time:O}. The clock only "
                + "moves forward. Nothing was applied.",
                nameof(clock));
        }
    }

    /// <summary>Publishes the stamp of the epoch <paramref name="epoch"/>, and wakes its waiters.</summary>
    private void Stamped(long epoch, long? version, DateTimeOffset? clock)
    {
        var current = Stamp;
        var stamp = new EpochStamp(epoch, version ?? current.Version + 1, clock?.ToUniversalTime() ?? current.Clock, _engineCommits.Committed)
        {
            Writes = _writing?.Tables,
            MovesClock = _writing?.MovesClock ?? true,
        };
        _writing = null;

        // A held refresh is stamped first, so a view that reads this stamp finds it ready (design 78 §4).
        foreach (var retainer in Volatile.Read(ref _retainers))
        {
            retainer.Published(stamp.Commit, stamp);
        }

        Volatile.Write(ref _stamp, stamp);
        Volatile.Write(ref _recent[epoch % RecentStamps], stamp);
        Interlocked.Exchange(ref _published, null)?.TrySetResult();
    }
}

/// <summary>One published epoch: the catalog's number, the host's version, and the clock.</summary>
internal sealed class EpochStamp(long epoch, long version, DateTimeOffset? clock, long commit = 0)
{
    public static EpochStamp First { get; } = new(1, 0, null);

    /// <summary>
    /// How many refreshes the engine had committed when this epoch was published (design 78 §1): an
    /// execution that pinned at this count read this epoch's tables, whatever was published since.
    /// </summary>
    public long Commit { get; } = commit;

    /// <summary>The tables the epoch's refresh wrote, by source (design 78 §2); null where it is not known.</summary>
    public (string SourceId, string Table)[]? Writes { get; init; }

    /// <summary>Whether the epoch's refresh moved the clock.</summary>
    public bool MovesClock { get; init; } = true;

    public long Epoch { get; } = epoch;

    public long Version { get; } = version;

    public DateTimeOffset? Clock { get; } = clock;
}
