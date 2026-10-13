using Chalk.Client;

namespace Chalk.Incremental;

/// <summary>
/// Turns a row of a view's result into the host's own key and row (D339,
/// <c>docs/design/64-incremental-views.md</c> §4). Called once for each row of each update; the view
/// keeps what it returns.
/// </summary>
/// <remarks>
/// The view compares and hashes the two structs as bytes, as a host's own replica does, so a struct
/// with padding must be written whole: a value made with <c>new</c> or <c>default</c>, then its fields.
/// </remarks>
public delegate void ViewMapper<TKey, TRow>(in ViewRow row, out TKey key, out TRow value)
    where TKey : unmanaged
    where TRow : unmanaged;

/// <summary>
/// A hash of one mapped key and row, over their bytes, as the host hashes them (D339). The view
/// keeps the sum of its live rows' hashes, mod 2³², and hands it to the sink at each commit.
/// </summary>
public delegate uint ViewRowHash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> row);

/// <summary>Where a drain delivers what changed (D339, design 64 §5). The host's.</summary>
public interface IViewSink<TKey, TRow>
    where TKey : unmanaged
    where TRow : unmanaged
{
    /// <summary>The whole result follows. Drop anything staged since the last commit.</summary>
    void Reset();

    /// <summary>The key's row is now <paramref name="row"/>.</summary>
    void Upsert(in TKey key, in TRow row);

    /// <summary>The key is gone.</summary>
    void Delete(in TKey key);

    /// <summary>
    /// The reader is level with the view as of <paramref name="version"/>, whose live rows' hashes
    /// sum to <paramref name="checksum"/>.
    /// </summary>
    void Commit(long version, uint checksum);
}

/// <summary>What a drain did.</summary>
public enum DrainOutcome
{
    /// <summary>Level with the view: everything changed was sent, and committed.</summary>
    Complete = 0,

    /// <summary>Out of budget before the end. Nothing was committed; the next drain carries on.</summary>
    Partial = 1,
}

/// <summary>Whether a view may hold values that differ from a recompute by rounding (D339).</summary>
public enum ViewExactness
{
    /// <summary>
    /// Every value equals what the statement returns at the view's epoch. A column computed by
    /// accumulating floating point is refused when the view is made.
    /// </summary>
    Exact = 0,

    /// <summary>
    /// A column computed by accumulating floating point is admitted, and may differ from a recompute
    /// by rounding: a sum moves with the order its rows are added in.
    /// </summary>
    EqualUpToRounding = 1,
}

/// <summary>How a view computes an update (D343, <c>docs/design/68-incremental-maintenance.md</c>).</summary>
public enum ViewMaintenance
{
    /// <summary>Every update runs the statement in full and compares the result with the view's rows.</summary>
    Recompute = 0,

    /// <summary>
    /// Every update computes only what changed since the last: the statement's plan runs on the
    /// changes to its tables, and its joins, as-of joins and aggregates keep state between updates. A
    /// statement that cannot be maintained so is refused when the view is made, naming what and why.
    /// A <c>DOUBLE</c> or <c>REAL</c> sum is kept exactly and rounded once, so it may differ from a
    /// recompute's sum, which adds in input order, in the last bits.
    /// </summary>
    Incremental = 1,
}

/// <summary>
/// Whether a view may read a source over a database, whose rows change outside the engine's
/// refreshes (D340, design 65).
/// </summary>
public enum ViewDatabaseReads
{
    /// <summary>The view reads no database: a statement that would is refused when it is made.</summary>
    None = 0,

    /// <summary>
    /// The view reads a database table when it is made, keeps what it read, and reads it again only at
    /// its first update after a refresh names the table: by itself, by its source, or with every
    /// source, as a plain or scheduled refresh does. Between refreshes it replays what it kept.
    /// </summary>
    WhenRefreshed = 1,
}

/// <summary>
/// What a view keeps of one database source it reads (D340): when it last read it, for which
/// version, how many times it has, and the rows it keeps of it.
/// </summary>
public sealed class ViewDatabaseRead
{
    /// <summary>The source read.</summary>
    public required string SourceId { get; init; }

    /// <summary>When the view last read it, by the system clock.</summary>
    public required DateTimeOffset LastReadAt { get; init; }

    /// <summary>The version the view was updating to when it last read it.</summary>
    public required long LastReadVersion { get; init; }

    /// <summary>How many reads of the source the view has made, over its life: one per round trip.</summary>
    public required long Reads { get; init; }

    /// <summary>The rows the view keeps of the source now, over every read of it.</summary>
    public required long Rows { get; init; }
}

/// <summary>
/// A reader's place in one view (D339, design 64 §5): where its last drain stopped, and what it last
/// committed. Start each reader at <see cref="Start"/>; its first drain is a reset.
/// </summary>
public struct ViewCursor
{
    internal object? Owner;
    internal long Generation;
    internal long Position;
    internal long ResetFrom;
    internal long Committed;

    /// <summary>A reader that has read nothing.</summary>
    public static ViewCursor Start => default;

    /// <summary>Whether this cursor has read nothing yet.</summary>
    public readonly bool IsStart => Owner is null;

    /// <summary>The version this reader last committed, or zero before its first commit.</summary>
    public readonly long Version => Committed;
}

/// <summary>How a standing view is made (D339, design 64 §4).</summary>
/// <typeparam name="TKey">The host's key struct.</typeparam>
/// <typeparam name="TRow">The host's row struct.</typeparam>
public sealed class ViewOptions<TKey, TRow>
    where TKey : unmanaged
    where TRow : unmanaged
{
    /// <summary>The result's key columns: each row of the result is identified by them.</summary>
    public required IReadOnlyList<string> Key { get; init; }

    /// <summary>Turns a result row into the host's key and row.</summary>
    public required ViewMapper<TKey, TRow> Map { get; init; }

    /// <summary>A hash of each mapped key and row, for the checksum. Null: the checksum is zero.</summary>
    public ViewRowHash? RowHash { get; init; }

    /// <summary>
    /// The most the view may hold, in bytes: its keyed table, and what it keeps of its reads of
    /// databases (D340). An update that would hold more fails, and the view keeps its last epoch.
    /// 64 MB by default.
    /// </summary>
    public long StateBudget { get; init; } = 64L << 20;

    /// <summary>
    /// The most rows a cross-source join in the view may fetch to join locally, by the planner's
    /// estimate: a statement whose plan would fetch more is refused when the view is made (D340).
    /// Required, so that every view states it; zero or less is refused.
    /// <see cref="StandingViews.DefaultLocalJoinMaxRows"/> is the value to pass without a better one.
    /// </summary>
    public required long LocalJoinMaxRows { get; init; }

    /// <summary>
    /// Whether the view may read a database, and how (D340). <see cref="ViewDatabaseReads.None"/>,
    /// the default, refuses a statement that does.
    /// </summary>
    public ViewDatabaseReads DatabaseReads { get; init; } = ViewDatabaseReads.None;

    /// <summary>
    /// When a reader is sent the whole result rather than what changed: once its changes reach this
    /// fraction of the view's live rows (D341). 1, the default, is no fewer changes than rows; a half
    /// resets sooner; <see cref="double.PositiveInfinity"/> never resets for size. Zero or less is
    /// refused.
    /// </summary>
    /// <remarks>
    /// A reader is reset besides when it is new, when the statement was prepared anew, and when it
    /// needs a delete the view has dropped. A host resets one at any moment by starting its cursor
    /// over, at <see cref="ViewCursor.Start"/>.
    /// </remarks>
    public double ResetWhen { get; init; } = 1;

    /// <summary>Whether a column computed by accumulating floating point is admitted.</summary>
    public ViewExactness Exactness { get; init; } = ViewExactness.Exact;

    /// <summary>
    /// How the view computes an update (D343): <see cref="ViewMaintenance.Recompute"/>, the default,
    /// runs the statement in full; <see cref="ViewMaintenance.Incremental"/> computes only what changed.
    /// </summary>
    public ViewMaintenance Maintenance { get; init; } = ViewMaintenance.Recompute;

    /// <summary>
    /// Admit only a host function registered with <c>Immutable()</c>, rather than any function
    /// whose volatility is <c>IMMUTABLE</c>, today's default included.
    /// </summary>
    public bool RequireExplicitImmutable { get; init; }

    /// <summary>
    /// The statement's parameter values, bound once for the life of the view: a dictionary, or an
    /// object whose properties name them, for <c>@name</c> parameters; a list for <c>?</c> or
    /// <c>$n</c> ones. The parameter named as the engine's clock takes none: it reads the clock.
    /// </summary>
    public object? Parameters { get; init; }

    /// <summary>How the statement is prepared: its parameter types, its pushdown, its options.</summary>
    public PrepareOptions? Prepare { get; init; }
}

/// <summary>A standing view refused, or failed, saying what to do (D339).</summary>
public sealed class StandingViewException : ChalkException
{
    public StandingViewException(string code, string message)
        : base(code, message)
    {
    }

    public StandingViewException(string code, string message, Exception? innerException)
        : base(code, message, innerException)
    {
    }
}
