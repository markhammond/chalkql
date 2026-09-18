using System.Collections.Concurrent;
using System.Globalization;

namespace Chalk.Sources;

/// <summary>
/// Machine-independent counters, reported alongside wall-clock everywhere Chalk reports anything
/// (rev 3 §7). These are the numbers that stay comparable when the hardware changes, and the
/// numbers M2's "the index lookup touched fewer rows" assertions read.
/// </summary>
/// <remarks>Thread-safe: every mutation is interlocked, so a source may count from its own threads.</remarks>
public sealed class ExecutionStats
{
    private long _rowsScanned;
    private long _rowsProduced;
    private long _batchesProduced;
    private long _remoteCalls;
    private long _rowsFetched;
    private long _bytesFetched;
    private long _peakPooledBytes;
    private long _windowsStreamed;
    private long _windowsBuffered;
    private long _sortsCompared;
    private long _sortsEncoded64;
    private long _sortsEncoded128;
    private long _sortsTieBroken;
    private long _elapsedTicks;
    // Both are created on first use, not with the object. An ExecutionStats is allocated per
    // execution and most executions touch no source and take no adaptive decision; a
    // ConcurrentDictionary costs about a kilobyte to exist, which is a quarter of the fixed
    // per-execution budget of 15-zero-allocation-execution.md §5 spent on an empty map.
    private ConcurrentDictionary<string, SourceFetchStats>? _sourceFetches;
    private ConcurrentQueue<AdaptiveDecision>? _adaptiveDecisions;
    private ConcurrentDictionary<string, string>? _sourcePaths;
    private string? _arenaPoolName;

    /// <summary>Rows read out of sources. The M1 baseline is "all of them"; M2 lowers it.</summary>
    public long RowsScanned => Volatile.Read(ref _rowsScanned);

    /// <summary>Rows handed to the host.</summary>
    public long RowsProduced => Volatile.Read(ref _rowsProduced);

    /// <summary>Batches handed to the host.</summary>
    public long BatchesProduced => Volatile.Read(ref _batchesProduced);

    /// <summary>Round trips to remote sources. Always zero in M1.</summary>
    public long RemoteCalls => Volatile.Read(ref _remoteCalls);

    /// <summary>
    /// Rows a remote source actually handed back (M4). The number that says whether pushdown
    /// worked: a pushed filter shows up here as fewer rows fetched for the same answer, and the
    /// property test of §5 asserts it moves the right way as capabilities are turned off.
    /// </summary>
    public long RowsFetched => Volatile.Read(ref _rowsFetched);

    /// <summary>Bytes pulled over a network or off disk. Always zero for in-process POCO sources.</summary>
    public long BytesFetched => Volatile.Read(ref _bytesFetched);

    /// <summary>High-water mark of the pooled allocator, for the benchmark suite.</summary>
    public long PeakPooledBytes => Volatile.Read(ref _peakPooledBytes);

    /// <summary>
    /// Window operators that ran on the streaming path (D258.4): the frame ended at or before the
    /// current row and every call over it could be answered from the rows still in hand, so the
    /// operator held the frame rather than the input.
    /// </summary>
    public long WindowsStreamed => Volatile.Read(ref _windowsStreamed);

    /// <summary>
    /// Window operators that buffered their whole input — a frame that reaches forwards, an
    /// <c>EXCLUDE</c>, or a call the streaming path does not cover
    /// (<c>docs/design/13-window-functions.md</c> §4.1).
    /// </summary>
    public long WindowsBuffered => Volatile.Read(ref _windowsBuffered);

    /// <summary>
    /// Blocking sorts that kept the multi-key comparer (D267 b): the ordering's first key has no
    /// order-preserving code, so there was no composite to sort on.
    /// </summary>
    public long SortsCompared => Volatile.Read(ref _sortsCompared);

    /// <summary>Blocking sorts whose whole ordering fitted one encoded word.</summary>
    public long SortsEncoded64 => Volatile.Read(ref _sortsEncoded64);

    /// <summary>Blocking sorts whose ordering needed two encoded words.</summary>
    public long SortsEncoded128 => Volatile.Read(ref _sortsEncoded128);

    /// <summary>
    /// Encoded sorts whose composite was a prefix of the ordering rather than the whole of it, so
    /// the comparer settled the rows it left equal — the runs, not the input.
    /// </summary>
    public long SortsTieBroken => Volatile.Read(ref _sortsTieBroken);

    /// <summary>
    /// The name of the arena pool this execution's memory came from, or null (D258): null for the
    /// engine's own pool, which is unnamed, and null for an execution given a single arena of its
    /// own, which came from no pool at all. A host that runs several workloads on pools of its own
    /// reads this to tell which one a query was charged to.
    /// </summary>
    public string? ArenaPoolName => Volatile.Read(ref _arenaPoolName);

    /// <summary>
    /// What each source was asked for and when, by source id (D101, M5). There is no cross-source
    /// snapshot: a federated result reflects each source at the moment it was read, and this is
    /// where a host reads <em>which</em> moment that was.
    /// </summary>
    public IReadOnlyDictionary<string, SourceFetchStats> SourceFetches =>
        Volatile.Read(ref _sourceFetches) ?? EmptyFetches;

    /// <summary>
    /// The branch each adaptive join took, in the order the decisions were made (D97, M5). The plan
    /// carries both branches and says nothing about which one runs — that is a property of the data,
    /// and this is the only place it is recorded.
    /// </summary>
    public IReadOnlyList<AdaptiveDecision> AdaptiveDecisions =>
        Volatile.Read(ref _adaptiveDecisions) is { } decisions ? [.. decisions] : [];

    /// <summary>
    /// Which reader each source used, by source id (D148, M6): <c>"DbDataReader"</c> for the
    /// provider's own reader, <c>"duckdb-native"</c> for the DuckDB data-chunk path, and the two
    /// joined when one query fell back to the other. A source records this; nothing asserts on it,
    /// and it is how a host tells which path a query actually ran on.
    /// </summary>
    public IReadOnlyDictionary<string, string> SourcePaths =>
        Volatile.Read(ref _sourcePaths) ?? EmptyPaths;

    /// <summary>Wall clock for the execution. Reported, never asserted on.</summary>
    public TimeSpan Elapsed
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref _elapsedTicks));
        set => Volatile.Write(ref _elapsedTicks, value.Ticks);
    }

    /// <summary>Records which arena pool served this execution. Called once, before it starts.</summary>
    public void RecordArenaPool(string? name) => Volatile.Write(ref _arenaPoolName, name);

    /// <summary>
    /// Records which path one window operator took (D258.4). Two counters rather than a list: the
    /// question a caller asks is "did this statement stream", and a counter answers it without
    /// costing an execution that never ran a window anything at all.
    /// </summary>
    public void RecordWindowPath(WindowPath path)
    {
        if (path == WindowPath.Streaming)
        {
            Interlocked.Increment(ref _windowsStreamed);
            return;
        }

        Interlocked.Increment(ref _windowsBuffered);
    }

    /// <summary>
    /// Records which key path one blocking sort took, and whether a tie-break followed (D267 b).
    /// Counters for the same reason the window's are counters: the question is "did this statement
    /// sort on encoded keys", and an execution with no sort in it pays nothing to answer it.
    /// </summary>
    public void RecordSortPath(SortPath path, bool tieBroken)
    {
        switch (path)
        {
            case SortPath.Encoded64:
                Interlocked.Increment(ref _sortsEncoded64);
                break;
            case SortPath.Encoded128:
                Interlocked.Increment(ref _sortsEncoded128);
                break;
            default:
                Interlocked.Increment(ref _sortsCompared);
                break;
        }

        if (tieBroken)
        {
            Interlocked.Increment(ref _sortsTieBroken);
        }
    }

    public void AddRowsScanned(long count) => Interlocked.Add(ref _rowsScanned, count);

    public void AddRowsProduced(long count) => Interlocked.Add(ref _rowsProduced, count);

    public void AddBatchesProduced(long count) => Interlocked.Add(ref _batchesProduced, count);

    public void AddRemoteCalls(long count) => Interlocked.Add(ref _remoteCalls, count);

    public void AddRowsFetched(long count) => Interlocked.Add(ref _rowsFetched, count);

    public void AddBytesFetched(long count) => Interlocked.Add(ref _bytesFetched, count);

    /// <summary>
    /// Records one fetch from one source: the call, the rows it returned, and when it happened.
    /// Called by the executor, so an adapter counts nothing it does not already count.
    /// </summary>
    public void RecordFetch(string sourceId, long rows, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        Fetches().AddOrUpdate(
            sourceId,
            _ => new SourceFetchStats
            {
                SourceId = sourceId,
                Calls = 1,
                Rows = rows,
                FirstFetch = at,
                LastFetch = at,
            },
            (_, existing) => new SourceFetchStats
            {
                SourceId = sourceId,
                Calls = existing.Calls + 1,
                Rows = existing.Rows + rows,
                FirstFetch = at < existing.FirstFetch ? at : existing.FirstFetch,
                LastFetch = at > existing.LastFetch ? at : existing.LastFetch,
            });
    }

    /// <summary>
    /// Records which reader a source used for one query. Called by the adapter, once per query;
    /// a source that used more than one path over an execution reports them joined with <c>'+'</c>.
    /// </summary>
    public void RecordSourcePath(string sourceId, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(path);
        Paths().AddOrUpdate(
            sourceId,
            _ => path,
            (_, existing) => existing == path
                || existing.Split('+').Contains(path, StringComparer.Ordinal)
                    ? existing
                    : existing + "+" + path);
    }

    /// <summary>Records which branch an adaptive join took, and on what evidence.</summary>
    public void RecordAdaptiveDecision(AdaptiveDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        Decisions().Enqueue(decision);
    }

    private ConcurrentDictionary<string, SourceFetchStats> Fetches()
    {
        var existing = Volatile.Read(ref _sourceFetches);
        if (existing is not null)
        {
            return existing;
        }

        var created = new ConcurrentDictionary<string, SourceFetchStats>(StringComparer.Ordinal);
        return Interlocked.CompareExchange(ref _sourceFetches, created, null) ?? created;
    }

    private ConcurrentQueue<AdaptiveDecision> Decisions()
    {
        var existing = Volatile.Read(ref _adaptiveDecisions);
        if (existing is not null)
        {
            return existing;
        }

        var created = new ConcurrentQueue<AdaptiveDecision>();
        return Interlocked.CompareExchange(ref _adaptiveDecisions, created, null) ?? created;
    }

    private ConcurrentDictionary<string, string> Paths()
    {
        var existing = Volatile.Read(ref _sourcePaths);
        if (existing is not null)
        {
            return existing;
        }

        var created = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        return Interlocked.CompareExchange(ref _sourcePaths, created, null) ?? created;
    }

    private static readonly IReadOnlyDictionary<string, SourceFetchStats> EmptyFetches =
        new Dictionary<string, SourceFetchStats>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> EmptyPaths =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Raises the pooled high-water mark to <paramref name="bytes"/> if it is higher.</summary>
    public void ObservePooledBytes(long bytes)
    {
        long current;
        do
        {
            current = Volatile.Read(ref _peakPooledBytes);
            if (bytes <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _peakPooledBytes, bytes, current) != current);
    }

    /// <summary>A snapshot for logging. Deliberately does not include timings first.</summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"rows_scanned={RowsScanned} rows_produced={RowsProduced} batches={BatchesProduced} "
        + $"remote_calls={RemoteCalls} rows_fetched={RowsFetched} bytes_fetched={BytesFetched} "
        + $"peak_pooled_bytes={PeakPooledBytes} "
        + $"sources={SourceFetches.Count} adaptive_decisions={AdaptiveDecisions.Count} "
        + $"elapsed_ms={Elapsed.TotalMilliseconds:0.###}");
}

/// <summary>
/// What one source was asked for during one execution (D101). There is no cross-source snapshot in
/// Chalk, so a result is each source at the moment it was read; these are those moments.
/// </summary>
public sealed class SourceFetchStats
{
    public required string SourceId { get; init; }

    /// <summary>How many times the source was asked — one per remote call or scan.</summary>
    public required long Calls { get; init; }

    /// <summary>Rows it handed back, over every call.</summary>
    public required long Rows { get; init; }

    /// <summary>When the first call to this source returned its first batch.</summary>
    public required DateTimeOffset FirstFetch { get; init; }

    /// <summary>When the last one did. The window a result's view of this source spans.</summary>
    public required DateTimeOffset LastFetch { get; init; }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{SourceId}: calls={Calls} rows={Rows} first={FirstFetch:O} last={LastFetch:O}");
}

/// <summary>Which branch one adaptive join took, and the measurement it took it on (D97).</summary>
public sealed class AdaptiveDecision
{
    /// <summary>The operator's path in the plan, as an execution error would name it.</summary>
    public required string OperatorPath { get; init; }

    /// <summary>The branch that ran.</summary>
    public required AdaptiveBranch Branch { get; init; }

    /// <summary>Distinct non-NULL keys counted in the materialised small side.</summary>
    public required long DistinctKeys { get; init; }

    /// <summary>The threshold they were compared against: <c>max_in_list × lookup_max_calls</c>.</summary>
    public required long MaxKeys { get; init; }

    /// <summary>Rows the small side turned out to have.</summary>
    public required long SmallRows { get; init; }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{OperatorPath}: {Branch} ({DistinctKeys} distinct keys of {SmallRows} rows, max {MaxKeys})");
}

/// <summary>
/// The two paths a window operator can take (D258.4). Which one a plan gets is decided when it is
/// compiled, from the frame's shape and the calls over it.
/// </summary>
public enum WindowPath
{
    /// <summary>The whole input is read into the operator before anything is computed.</summary>
    Buffered = 0,

    /// <summary>Batches flow through and the operator holds the frame, not the input.</summary>
    Streaming = 1,
}

/// <summary>
/// The three key paths a blocking sort can take (D267 b). Which one a sort gets is decided from the
/// data it has concatenated, not from the plan: the same statement over a narrower range of values
/// may need one word where it needed two.
/// </summary>
public enum SortPath
{
    /// <summary>The multi-key comparer, one call per comparison per key.</summary>
    Comparer = 0,

    /// <summary>The whole ordering encoded into one word per row.</summary>
    Encoded64 = 1,

    /// <summary>The ordering encoded into two words per row.</summary>
    Encoded128 = 2,
}

/// <summary>The two branches an adaptive join chooses between (D97).</summary>
public enum AdaptiveBranch
{
    /// <summary>The keys fit: the other source was asked for them.</summary>
    Lookup = 0,

    /// <summary>Too many keys: both sides were fetched and joined locally.</summary>
    Local = 1,
}
