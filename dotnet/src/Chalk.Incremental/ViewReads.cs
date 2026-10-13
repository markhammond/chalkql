using Apache.Arrow;
using Chalk.Client;
using Chalk.Sources;

namespace Chalk.Incremental;

/// <summary>
/// What a view keeps of its reads of databases (D340, <c>docs/design/65-views-over-databases.md</c>
/// §4): for each read, by its place in the plan, the batches it returned, copied out of the
/// execution's memory, with the values it was bound with and the epoch it was read at. A read is
/// replayed until a refresh names one of its tables, or its values change.
/// </summary>
/// <remarks>
/// An update stages what it reads, and the view keeps it only when the update applies: an update
/// that fails keeps nothing new. Updates are one at a time, so a replayed read's batches are never
/// let go while an execution views them.
/// </remarks>
internal sealed class ViewReads : IReadKeeper, IDisposable
{
    private readonly ChalkEngine _engine;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Read> _kept = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Read> _staged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _reads = new(StringComparer.Ordinal);
    private EpochStamp? _stamp;
    private long _budget;

    public ViewReads(ChalkEngine engine) => _engine = engine;

    /// <summary>What the view keeps, in bytes: every whole read it holds.</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _kept.Values.Sum(read => read.Bytes);
            }
        }
    }

    /// <summary>
    /// What the view would keep were the staged reads kept: each staged whole read in place of what
    /// it replaces, and every other read as it is.
    /// </summary>
    public long BytesKept
    {
        get
        {
            lock (_gate)
            {
                return Holding();
            }
        }
    }

    /// <summary>Starts an update's attempt at <paramref name="stamp"/>, with <paramref name="budget"/> bytes for what it keeps.</summary>
    public void Begin(EpochStamp stamp, long budget)
    {
        lock (_gate)
        {
            _stamp = stamp;
            _budget = budget;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A read staged by an earlier attempt of this update is replayed like a kept one. Returning
    /// null starts the read afresh: what was staged for its place gives way.
    /// </remarks>
    public IReadOnlyList<RecordBatch>? Replay(ReadSite site, object?[] values)
    {
        lock (_gate)
        {
            if (_staged.TryGetValue(site.Path, out var staged))
            {
                if (staged.Whole && Current(staged, values))
                {
                    return staged.Batches;
                }

                _staged.Remove(site.Path);
                staged.Dispose();
            }

            if (_kept.TryGetValue(site.Path, out var kept) && Current(kept, values))
            {
                return kept.Batches;
            }

            var stamp = _stamp ?? throw new InvalidOperationException("a view's read was made outside an update.");
            _staged[site.Path] = new Read(site, values, stamp.Epoch, stamp.Version, DateTimeOffset.UtcNow);
            _reads[site.SourceId] = _reads.GetValueOrDefault(site.SourceId) + 1;
            return null;
        }
    }

    /// <inheritdoc />
    public void Keep(ReadSite site, object?[] values, RecordBatch batch)
    {
        lock (_gate)
        {
            if (!_staged.TryGetValue(site.Path, out var read))
            {
                return;
            }

            var copy = batch.Clone();
            read.Batches.Add(copy);
            read.Bytes += BytesOf(copy);
            read.Rows += copy.Length;

            var holding = Holding();
            if (holding > _budget)
            {
                throw new StandingViewException(
                    ChalkErrorCodes.MemoryBudgetExceeded,
                    $"The view would keep about {holding} bytes of what it reads from source "
                    + $"'{site.SourceId}' at version {read.Version}, past the {_budget} bytes its StateBudget "
                    + "leaves beside its rows. It keeps the epoch it held. Raise ViewOptions.StateBudget, or "
                    + "read less from the database: filter what the statement reads from it.");
            }
        }
    }

    /// <inheritdoc />
    public void Kept(ReadSite site, object?[] values)
    {
        lock (_gate)
        {
            if (_staged.TryGetValue(site.Path, out var read))
            {
                read.Whole = true;
            }
        }
    }

    /// <summary>Keeps every whole read the update staged, in place of what it replaces.</summary>
    public void Commit()
    {
        lock (_gate)
        {
            foreach (var (path, read) in _staged)
            {
                if (!read.Whole)
                {
                    read.Dispose();
                    continue;
                }

                if (_kept.Remove(path, out var replaced))
                {
                    replaced.Dispose();
                }

                _kept[path] = read;
            }

            _staged.Clear();
            _stamp = null;
        }
    }

    /// <summary>Lets go of what a failed update staged: the view keeps what it held.</summary>
    public void Abandon()
    {
        lock (_gate)
        {
            foreach (var read in _staged.Values)
            {
                read.Dispose();
            }

            _staged.Clear();
            _stamp = null;
        }
    }

    /// <summary>Lets go of everything: the statement was prepared anew, and its reads are elsewhere.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var read in _kept.Values.Concat(_staged.Values))
            {
                read.Dispose();
            }

            _kept.Clear();
            _staged.Clear();
        }
    }

    public void Dispose() => Clear();

    /// <summary>What the view keeps of each source it reads, by source.</summary>
    public IReadOnlyList<ViewDatabaseRead> Summary()
    {
        lock (_gate)
        {
            return
            [
                .. _kept.Values
                    .GroupBy(read => read.Site.SourceId, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new ViewDatabaseRead
                    {
                        SourceId = group.Key,
                        LastReadAt = group.Max(read => read.At),
                        LastReadVersion = group.Max(read => read.Version),
                        Reads = _reads.GetValueOrDefault(group.Key),
                        Rows = group.Sum(read => read.Rows),
                    }),
            ];
        }
    }

    /// <summary>
    /// Whether a kept read still stands for the read about to run: bound with the same values, and
    /// none of its tables named by a refresh since it was read.
    /// </summary>
    private bool Current(Read read, object?[] values)
    {
        if (!Same(read.Values, values))
        {
            return false;
        }

        foreach (var (sourceId, table) in read.Site.Tables)
        {
            if (_engine.NamedAt(sourceId, table) > read.Epoch)
            {
                return false;
            }
        }

        return true;
    }

    private long Holding()
    {
        var bytes = 0L;
        foreach (var (path, read) in _kept)
        {
            if (!_staged.ContainsKey(path))
            {
                bytes += read.Bytes;
            }
        }

        foreach (var read in _staged.Values)
        {
            bytes += read.Bytes;
        }

        return bytes;
    }

    private static bool Same(object?[] kept, object?[] values)
    {
        if (ReferenceEquals(kept, values))
        {
            return true;
        }

        if (kept.Length != values.Length)
        {
            return false;
        }

        for (var i = 0; i < kept.Length; i++)
        {
            if (!SameValue(kept[i], values[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameValue(object? kept, object? value) => (kept, value) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
        (System.Collections.IEnumerable a, System.Collections.IEnumerable b) =>
            a.Cast<object?>().SequenceEqual(b.Cast<object?>()),
        _ => kept.Equals(value),
    };

    /// <summary>A batch's buffers, in bytes: what keeping it costs.</summary>
    private static long BytesOf(RecordBatch batch)
    {
        var bytes = 0L;
        for (var c = 0; c < batch.ColumnCount; c++)
        {
            bytes += BytesOf(batch.Column(c).Data);
        }

        return bytes;
    }

    private static long BytesOf(Apache.Arrow.ArrayData data)
    {
        // A primitive array has no children, and Arrow leaves the list null rather than empty.
        var bytes = 0L;
        foreach (var buffer in data.Buffers ?? [])
        {
            bytes += buffer.Length;
        }

        foreach (var child in data.Children ?? [])
        {
            bytes += BytesOf(child);
        }

        if (data.Dictionary is { } dictionary)
        {
            bytes += BytesOf(dictionary);
        }

        return bytes;
    }

    /// <summary>One read: where it is, what it was bound with, when it was made, and what it returned.</summary>
    private sealed class Read(ReadSite site, object?[] values, long epoch, long version, DateTimeOffset at)
    {
        public ReadSite Site { get; } = site;
        public object?[] Values { get; } = values;
        public long Epoch { get; } = epoch;
        public long Version { get; } = version;
        public DateTimeOffset At { get; } = at;
        public List<RecordBatch> Batches { get; } = [];
        public long Bytes { get; set; }
        public long Rows { get; set; }
        public bool Whole { get; set; }

        public void Dispose()
        {
            foreach (var batch in Batches)
            {
                batch.Dispose();
            }

            Batches.Clear();
        }
    }
}
