using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Maintenance;
using Chalk.Sources;

namespace Chalk.Client;

/// <summary>
/// A prepared statement kept by incremental maintenance (D343, <c>docs/design/68-incremental-maintenance.md</c>),
/// for the standing view that owns it: each update brings it to the epoch the engine's tables are at
/// now, and says which keys' rows changed. The one hook into the executor a maintained view needs,
/// opened to <c>Chalk.Incremental</c> as an execution is.
/// </summary>
/// <remarks>Updates are taken one at a time, by the view.</remarks>
internal sealed class MaintainedQuery : IAsyncDisposable
{
    private readonly PreparedQuery _query;
    private readonly MaintainedPlan _plan;
    private readonly IReadOnlyList<PlaceholderSlot> _slots;
    private readonly IReadOnlyList<ChalkType> _parameterTypes;
    private readonly ExecutionArena _arena = new();

    internal MaintainedQuery(
        PreparedQuery query,
        MaintainedPlan plan,
        IReadOnlyList<PlaceholderSlot> slots,
        IReadOnlyList<ChalkType> parameterTypes,
        PreparedQuery.PlanState plans)
    {
        _query = query;
        _plan = plan;
        _slots = slots;
        _parameterTypes = parameterTypes;
        PlannedWith = plans;
        PlanDigest = plans.Plan.PlanDigest;
    }

    /// <summary>The digest of the plan this query was made from: the plan its updates run.</summary>
    internal ulong PlanDigest { get; }

    /// <summary>
    /// The statement's plans this query was made from (<see cref="PreparedQuery.Plans"/>), or the ones
    /// it was told to stay on when it could not follow newer ones.
    /// </summary>
    internal object PlannedWith { get; set; }

    /// <summary>
    /// Whether the statement still runs the plans this query was made from: false once plans the
    /// background made have swapped in (design 69 §4). The query answers the same rows either way.
    /// </summary>
    internal bool IsCurrent => ReferenceEquals(PlannedWith, _query.Plans);

    /// <summary>Bytes the statement's operators and its result hold, against the view's state budget.</summary>
    public long StateBytes => _plan.StateBytes;

    /// <summary>
    /// Brings the statement to the epoch its tables are pinned at now, with <paramref name="values"/>
    /// bound as an execution binds them and <paramref name="now"/> its current time. A key that comes to
    /// hold two rows throws <see cref="MaintainedKeyFault"/>; any other failure leaves the statement to
    /// start again from nothing at its next update.
    /// </summary>
    /// <param name="values">The view's parameter values, the clock's among them.</param>
    /// <param name="now">The epoch's time, which the statement reads as the current one.</param>
    /// <param name="whole">Give every row the result holds, for a view whose rows lag the statement's.</param>
    /// <param name="ct">Cancels the update, which leaves the statement to start again at the next.</param>
    public async ValueTask<MaintainedUpdate> UpdateAsync(
        object?[] values, DateTimeOffset now, bool whole, CancellationToken ct)
    {
        var flat = _query.Flatten(values, _slots, _parameterTypes);
        MaintainedChanges changes;
        try
        {
            changes = await _plan.UpdateAsync(flat, now, whole, _arena, new ExecutionStats(), ct).ConfigureAwait(false);
        }
        catch (MaintainedKeyException duplicate)
        {
            throw new MaintainedKeyFault(duplicate.Key);
        }

        return new MaintainedUpdate
        {
            Whole = changes.Whole,
            Restarted = changes.Restarted,
            Now = changes.Now,
            Before = changes.Before,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _plan.DisposeAsync().ConfigureAwait(false);
        _arena.Dispose();
    }
}

/// <summary>What one maintained update changed: as <see cref="MaintainedChanges"/>, in the client's terms.</summary>
internal sealed class MaintainedUpdate
{
    /// <summary>True when <see cref="Now"/> is the whole result: the first update, or one after a failure.</summary>
    public required bool Whole { get; init; }

    /// <summary>True when the update computed the result from nothing, rather than catching up.</summary>
    public required bool Restarted { get; init; }

    /// <summary>The rows the changed keys hold now; every row when <see cref="Whole"/>.</summary>
    public required RecordBatch Now { get; init; }

    /// <summary>The rows the changed keys held before.</summary>
    public required RecordBatch Before { get; init; }
}

/// <summary>A statement incremental maintenance cannot keep, and why (design 68 §8).</summary>
internal sealed class NotMaintainableException(string reason, string path) : Exception(reason)
{
    /// <summary>Where in the plan, as the executor names an operator.</summary>
    public string Path { get; } = path;
}

/// <summary>A maintained result whose key came to hold two rows: the key, rendered.</summary>
internal sealed class MaintainedKeyFault(string key) : Exception($"the key ({key}) holds two rows.")
{
    public string Key { get; } = key;
}
