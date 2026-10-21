using Chalk.Sources;

namespace Chalk.Client;

/// <summary>
/// The refreshes the engine holds for one standing view until it has published them (design 78 §4, §5):
/// each barrier, and, for a view that publishes every version, every refresh. Each is held as the states
/// it committed of the view's tables, its count of commits and its clock, and, once published, its stamp.
/// Made with <see cref="ChalkEngine.Retain"/>, and let go with <see cref="Dispose"/>.
/// </summary>
internal sealed class EpochRetainer : IDisposable
{
    private readonly ChalkEngine _engine;
    private readonly Queue<HeldEpoch> _held = new();
    private (string SourceId, string Table, ITrackedSource Source)[] _tables;
    private bool _overrun;
    private bool _disposed;

    internal EpochRetainer(
        ChalkEngine engine, (string SourceId, string Table, ITrackedSource Source)[] tables, bool every, int maxBacklog)
    {
        _engine = engine;
        _tables = tables;
        Every = every;
        MaxBacklog = maxBacklog;
    }

    /// <summary>Whether every refresh is held, not only barriers.</summary>
    public bool Every { get; }

    /// <summary>The most refreshes held at once for a view that publishes every version.</summary>
    public int MaxBacklog { get; }

    /// <summary>How many refreshes the engine had committed when this began: each later one is held as it must be.</summary>
    public long From { get; internal set; }

    /// <summary>
    /// Whether <see cref="MaxBacklog"/> refreshes were held and another came: the view cannot publish every
    /// version, and nothing more is held for it.
    /// </summary>
    public bool Overrun => Volatile.Read(ref _overrun);

    /// <summary>The tables to hold from the next refresh on: the statement's, when it is prepared anew.</summary>
    public void Reads((string SourceId, string Table, ITrackedSource Source)[] tables) => Volatile.Write(ref _tables, tables);

    /// <summary>Holds the refresh that has just committed. The caller holds the gate.</summary>
    internal void Hold(long commit, DateTimeOffset? clock, bool barrier)
    {
        lock (_held)
        {
            if (_overrun || _disposed)
            {
                return;
            }

            if (Every && _held.Count >= MaxBacklog)
            {
                // Let go after the gate, by LetGoIfOverrun: a source may tell its host.
                Volatile.Write(ref _overrun, true);
                return;
            }

            _held.Enqueue(new HeldEpoch(new RetainedTables(commit, clock, Volatile.Read(ref _tables)), barrier));
        }
    }

    /// <summary>
    /// Gives the held refresh numbered <paramref name="commit"/> its stamp, before the stamp is published,
    /// so a view that reads the stamp finds every refresh held up to it ready to read.
    /// </summary>
    internal void Published(long commit, EpochStamp stamp)
    {
        lock (_held)
        {
            foreach (var held in _held)
            {
                if (held.Tables.Commit == commit && held.Stamp is null)
                {
                    held.Stamp = stamp;
                }
            }
        }
    }

    /// <summary>Lets go of what was held, once overrun.</summary>
    internal void LetGoIfOverrun()
    {
        if (Overrun)
        {
            LetGoOfAll();
        }
    }

    /// <summary>
    /// The oldest refresh held after commit <paramref name="after"/>, once its stamp is published; null when
    /// there is none. Those at or before it, which the view has passed, are let go; and so is one whose
    /// refresh never published, once a later one has.
    /// </summary>
    public HeldEpoch? Next(long after)
    {
        List<HeldEpoch>? passed = null;
        HeldEpoch? next = null;
        lock (_held)
        {
            while (_held.TryPeek(out var head))
            {
                var unpublished = head.Stamp is null;
                if (head.Tables.Commit > after && (!unpublished || !_held.Any(h => h.Stamp is not null)))
                {
                    next = unpublished ? null : head;
                    break;
                }

                (passed ??= []).Add(_held.Dequeue());
            }
        }

        if (passed is not null)
        {
            foreach (var held in passed)
            {
                held.Tables.Release();
            }
        }

        return next;
    }

    /// <summary>Lets go of <paramref name="held"/>, which the view has published.</summary>
    public void Taken(HeldEpoch held)
    {
        lock (_held)
        {
            if (!_held.TryPeek(out var head) || !ReferenceEquals(head, held))
            {
                return;
            }

            _held.Dequeue();
        }

        held.Tables.Release();
    }

    /// <summary>Stops holding, and lets go of everything held.</summary>
    public void Dispose()
    {
        lock (_held)
        {
            _disposed = true;
        }

        _engine.LetGo(this);
        LetGoOfAll();
    }

    private void LetGoOfAll()
    {
        HeldEpoch[] all;
        lock (_held)
        {
            all = [.. _held];
            _held.Clear();
        }

        foreach (var held in all)
        {
            held.Tables.Release();
        }
    }
}

/// <summary>One refresh held for a view: the states it committed, whether it is a barrier, and its stamp once published.</summary>
internal sealed class HeldEpoch(RetainedTables tables, bool barrier)
{
    public RetainedTables Tables { get; } = tables;

    public bool Barrier { get; } = barrier;

    /// <summary>Null until the refresh publishes its epoch.</summary>
    public EpochStamp? Stamp { get; set; }
}
