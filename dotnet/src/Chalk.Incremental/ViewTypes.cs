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

    /// <summary>
    /// The reader is level with the view as of <paramref name="stamp"/> (design 78 §3): the version, and
    /// how many times the view has been rebound, which changes its rows without moving the version. By
    /// default, the version alone, to <see cref="Commit(long, uint)"/>.
    /// </summary>
    void Commit(in ViewStamp stamp, uint checksum) => Commit(stamp.Version, checksum);
}

/// <summary>
/// Where a view's rows stand (design 78 §3): the version of the epoch they are the statement's rows at,
/// and the view's generation, how many times it has been rebound since it was made. A rebind changes the
/// rows without moving the version, so the two together name one result. Stamps order by version, then
/// generation.
/// </summary>
/// <param name="Version">The engine's version the rows are at.</param>
/// <param name="Generation">How many times the view has been rebound.</param>
public readonly record struct ViewStamp(long Version, long Generation) : IComparable<ViewStamp>
{
    /// <summary>Orders by version, then generation.</summary>
    public int CompareTo(ViewStamp other) =>
        Version != other.Version ? Version.CompareTo(other.Version) : Generation.CompareTo(other.Generation);

    public static bool operator <(ViewStamp left, ViewStamp right) => left.CompareTo(right) < 0;

    public static bool operator >(ViewStamp left, ViewStamp right) => left.CompareTo(right) > 0;

    public static bool operator <=(ViewStamp left, ViewStamp right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ViewStamp left, ViewStamp right) => left.CompareTo(right) >= 0;
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
    /// <remarks>
    /// Experimental under <c>CHALK003</c>: a host acknowledges it to ask for incremental maintenance,
    /// in its project with <c>&lt;NoWarn&gt;$(NoWarn);CHALK003&lt;/NoWarn&gt;</c> or where it asks with
    /// <c>#pragma warning disable CHALK003</c>. Until it is declared stable, its behaviour and its API may
    /// change in any release; check what a maintained view gives against its statement before relying
    /// on it. A view that recomputes carries no such mark.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.Experimental("CHALK003")]
    Incremental = 1,
}

/// <summary>
/// How an incremental view moves to a plan its statement takes after the view is made (design 70 §2).
/// </summary>
public enum ViewPlanSwap
{
    /// <summary>
    /// The view's next update computes the new plan's state from nothing, as its first update did: the
    /// slowest update the view has.
    /// </summary>
    AtNextUpdate = 0,

    /// <summary>
    /// A second instance of the view is built on the new plan in the background, from the moment the
    /// statement swaps the plan in, while the view keeps updating the one it has, so that no update
    /// stalls on the new plan: low jitter, paid for in memory. The first update after the build swaps
    /// the new instance in: it catches up to that update's epoch, and readers keep their place, sent
    /// only the keys whose rows differ. Until then the view holds both instances' state. Refused for a
    /// view that recomputes, which takes a new plan at no extra cost.
    /// </summary>
    LowJitter = 1,
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
    internal long CommittedGeneration;

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
    /// The most the view may hold, in bytes: its keyed table, what it keeps of its reads of databases
    /// (D340), and what incremental maintenance keeps, as <c>StateBytes</c> estimates it. An update that
    /// would hold more fails, and the view keeps its last epoch. 64 MB by default.
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
    /// Told of each update that moves the view (design 70 §1): how long it took, the bytes allocated in
    /// the process meanwhile, whether it computed the view's rows from nothing, the keys it changed,
    /// and the plan it ran. Null, the default, tells nothing.
    /// </summary>
    public IViewCostTracker? Tracker { get; init; }

    /// <summary>
    /// Asked before a new plan is requested for the view's statement because the numbers it was planned
    /// on have drifted (design 70 §1): true requests one; false leaves the statement on its plan for now,
    /// and a later run asks again. So a host vetoes a new plan while it cannot afford the view's move to
    /// one, and refreshes when it can. Null, the default, requests a plan whenever the numbers drift.
    /// </summary>
    /// <remarks>
    /// Asked by the background run that would plan the statement again: one a refresh that defers its
    /// planning starts, and one the view asks for while a tenfold drift leaves its statement stale
    /// (§2). A statement the background cannot plan again is prepared anew for such a drift on the
    /// update's thread, and asks there. So it must be quick, safe on any thread, and leave the view
    /// alone. One that throws says not now, and is logged. A change in the shape of a table the
    /// statement reads requests a plan without asking: the plan the view runs no longer fits. A
    /// statement keeps one view's: a second view with one over a statement the host prepared is
    /// refused while the first exists.
    /// </remarks>
    public Func<bool>? RefreshQueryPlan { get; init; }

    /// <summary>
    /// The fraction of the cost of the plan the view runs that a new plan must save to be taken, both
    /// priced by the planner on the new plan's numbers (design 70 §3). The planner applies it: a plan
    /// that saves less is held back, never sent, and the statement keeps the plan it runs. 0.25 takes a
    /// plan that costs at most three quarters of the one the view runs; zero takes any that costs less.
    /// Null, the default, takes every plan.
    /// </summary>
    /// <remarks>
    /// From zero up to, not including, one; anything else is refused when the view is made. The
    /// planner keeps the plan the view runs in order to price it; one it no longer keeps, after an
    /// eviction or a restart, cannot be priced, and the new plan is taken.
    /// </remarks>
    public double? PlanImprovementMargin { get; init; }

    /// <summary>
    /// How an incremental view moves to a plan its statement takes (design 70 §2):
    /// <see cref="ViewPlanSwap.AtNextUpdate"/>, the default, computes it at the next update;
    /// <see cref="ViewPlanSwap.LowJitter"/> builds it beside the plan the view runs, and swaps it in once
    /// built. <see cref="ViewPlanSwap.LowJitter"/> is refused for a view that recomputes.
    /// </summary>
    public ViewPlanSwap PlanSwap { get; init; } = ViewPlanSwap.AtNextUpdate;

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

    /// <summary>
    /// The parameters <see cref="StandingView{TKey, TRow}.RebindAsync"/> may bind again, by name (design 77
    /// §5); none by default, when every parameter binds once. The view keeps its plan across a rebind.
    /// Kept incrementally, each operator that reads one holds what it reads and runs again over it when
    /// one moves, as an operator that reads the clock does when the clock moves, so declaring one costs
    /// what those operators hold. A list keeps the length it was bound with, unless it is given a
    /// capacity.
    /// </summary>
    /// <remarks>
    /// Kept incrementally, a parameter that a <c>LIMIT</c> reads, or that bounds an index seek (a list on
    /// an indexed key column, say), is refused when the view is made: those are fixed when the maintained
    /// plan starts.
    /// </remarks>
    public IReadOnlyList<RebindableParameter>? Rebindable { get; init; }

    /// <summary>How the statement is prepared: its parameter types, its pushdown, its options.</summary>
    public PrepareOptions? Prepare { get; init; }

    /// <summary>
    /// The values the view binds to a statement prepared for a request context's shape, for the life of
    /// the view (design 74 §3 c). One statement, prepared once from the shape, then serves every
    /// principal's view, each with its own values, and each update's audit event carries this
    /// context's purpose and actor. Null, the default, binds nothing: a statement prepared with its
    /// context's values carries them.
    /// </summary>
    /// <remarks>
    /// The values must fit the statement's shape as an execution's must: the same names, kinds and
    /// types, and for a statement that folded some names (a tenant's plan), the same values for those.
    /// A view whose values do not fit, or that gives none to a statement prepared for a shape, is
    /// refused when it is made. A view made from a statement's text prepares it with no context, so it
    /// refuses one too.
    /// </remarks>
    public RequestContext? Context { get; init; }
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

/// <summary>
/// A parameter a standing view may bind again with <see cref="StandingView{TKey, TRow}.RebindAsync"/>
/// (design 77 §5), named as the statement writes it, with or without its <c>@</c>.
/// </summary>
/// <param name="Name">The parameter's name.</param>
/// <param name="Capacity">
/// For a parameter after <c>IN</c> or <c>NOT IN</c>: the most elements a list bound to it may hold. The
/// view's plan then takes this many, and a shorter list is padded by repeating its first element, which
/// changes neither <c>IN</c> nor <c>NOT IN</c>; so any list of one element up to the capacity binds into
/// the same plan. Each row is compared with as many values as the capacity. Null keeps the list's length.
/// </param>
public sealed record RebindableParameter(string Name, int? Capacity = null);
