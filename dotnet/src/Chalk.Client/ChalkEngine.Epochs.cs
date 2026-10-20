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
    /// Odd while a refresh is making new rows visible — swapping snapshots and publishing the next
    /// catalog — and even otherwise. A reader that saw one even value before it read and the same
    /// value after has read one epoch, whatever order its scans ran in.
    /// </summary>
    private long _changes;

    /// <summary>Completed, and replaced, each time an epoch is published.</summary>
    private TaskCompletionSource _published = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            var published = Volatile.Read(ref _published);
            if (Stamp.Version >= version)
            {
                return;
            }

            await published.Task.WaitAsync(ct).ConfigureAwait(false);
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
        Volatile.Write(
            ref _stamp,
            new EpochStamp(epoch, version ?? current.Version + 1, clock?.ToUniversalTime() ?? current.Clock, _engineCommits.Committed));
        Interlocked.Exchange(
            ref _published,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
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

    public long Epoch { get; } = epoch;

    public long Version { get; } = version;

    public DateTimeOffset? Clock { get; } = clock;
}
