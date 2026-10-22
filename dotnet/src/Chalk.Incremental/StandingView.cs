using System.Diagnostics;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Sources;
using Microsoft.Extensions.Logging;

namespace Chalk.Incremental;

/// <summary>
/// A statement kept current as one row per key (D339, <c>docs/design/64-incremental-views.md</c>
/// §4–§6), read from a cursor as the latest row of each key changed since.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UpdateAsync"/> brings the view to the engine's latest epoch, and is the only thing
/// that computes: it runs the statement in full and compares the result with the rows the view
/// holds, or, under <see cref="ViewMaintenance.Incremental"/> (D343), computes only what changed since
/// the last update and applies the keys whose rows changed. <see cref="Drain"/> copies what changed
/// into a sink, and never computes. A view computes only when asked, so epochs nobody reads cost nothing.
/// </para>
/// <para>
/// An update reads one epoch whole: should a refresh land while it runs, it runs again, and holds
/// refreshes off on its third attempt. Updates are taken one at a time; drains may run beside them
/// and see each update whole or not at all.
/// </para>
/// <para>
/// Incremental maintenance is experimental under <c>CHALK003</c>, which
/// <see cref="ViewMaintenance.Incremental"/> carries: a view that recomputes carries no such mark.
/// </para>
/// </remarks>
public sealed class StandingView<TKey, TRow> : IAsyncDisposable
    where TKey : unmanaged
    where TRow : unmanaged
{
    private readonly ChalkEngine _engine;
    private readonly string _sql;

    /// <summary>
    /// What the statement is prepared with, again after a shape change: the view's own for text
    /// (design 65), or the host's statement's own for one made from a prepared statement (D341).
    /// </summary>
    private readonly PrepareOptions _prepare;

    private readonly ViewOptions<TKey, TRow> _options;
    private readonly ViewBinding _binding;

    /// <summary>True while a rebind's update runs: it stages at the epoch the view holds, as the values moved.</summary>
    private bool _rebound;

    /// <summary>How many rebinds the view has taken (design 78 §3): its stamp's generation.</summary>
    private long _rebinds;

    /// <summary>The tables the view's statement reads, for the one it was worked out for (design 78 §2).</summary>
    private (PreparedQuery For, Func<(string SourceId, string Table), bool> Reads)? _readSet;

    /// <summary>How many times a refresh that wrote nothing the view reads moved its version without an update: for a test.</summary>
    internal int Relabels { get; private set; }
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _updating = new(1, 1);
    private readonly KeyedTable<TKey, TRow> _table = new();
    private readonly ViewClock _clock = new();

    /// <summary>What the view keeps of its reads of databases (D340); null without the opt-in.</summary>
    private readonly ViewReads? _reads;

    /// <summary>The statement kept by incremental maintenance (D343); null for a view that recomputes.</summary>
    private MaintainedQuery? _maintained;

    /// <summary>
    /// Whether the view's rows may lag what the maintained statement holds — before its first update,
    /// and after one the view did not apply — so that the next update takes the whole result.
    /// </summary>
    private bool _resync = true;

    private PreparedQuery _prepared;
    private int[] _key;
    private bool _readsTime;

    /// <summary>Whether the statement names the engine's clock as a parameter: worked out once per statement.</summary>
    private bool _namesClock;

    /// <summary>
    /// The staging an update maps its rows into, kept between updates and emptied for each, so that a
    /// maintained update stages its changes without allocating (F244); null while an update uses it.
    /// Only an update, under its lock, takes it.
    /// </summary>
    private Staged? _staging;
    private long _epoch;

    /// <summary>The engine's count of commits as of the epoch the view holds (design 78 §1).</summary>
    private long _commit = -1;

    /// <summary>
    /// The refreshes the engine holds until the view has published them (design 78 §4, §5): barriers, and
    /// every refresh for a view that publishes every version. Null for a view that reads a database.
    /// </summary>
    private readonly EpochRetainer? _retainer;
    private long _version;
    private long _generation = 1;
    private long _updates;
    private Exception? _fault;
    private bool _disposed;

    /// <summary>
    /// The host's say on the view's plans — its veto on new plans and its margin — as the view puts it
    /// on its statement (design 70 §1, §3); null when it set neither.
    /// </summary>
    private readonly PreparedQuery.PlanChoice? _choice;

    /// <summary>Whether the last update computed the view's rows from nothing, and how many keys it changed.</summary>
    private bool _lastWhole;
    private long _lastChanges;

    /// <summary>
    /// The instance a low-jitter swap builds on the statement's new plan, beside the one the view updates
    /// (design 70 §2); null when none is building or waiting to be swapped in. It is built blue/green:
    /// this is the green one, and the one the view updates is blue.
    /// </summary>
    private Green? _green;

    /// <summary>Instances let go while their builds ran, each disposed once its build has stopped.</summary>
    private readonly List<Task> _lettingGo = [];

    /// <summary>
    /// Starts building a plan beside the view's own as its statement swaps the plan in (design 70 §2),
    /// for a view that swaps with low jitter; null for any other.
    /// </summary>
    private readonly Action? _buildAtSwap;

    private StandingView(
        ChalkEngine engine,
        string sql,
        PrepareOptions prepare,
        ViewOptions<TKey, TRow> options,
        ViewBinding binding,
        PreparedQuery prepared,
        int[] key)
    {
        _engine = engine;
        _sql = sql;
        _prepare = prepare;
        _options = options;
        _binding = binding;
        _prepared = prepared;
        _key = key;
        _readsTime = ViewPlan.ReadsCurrentTime(prepared.Plan);
        _namesClock = ViewBinding.NamesClock(prepared, engine.ClockName);
        _reads = options.DatabaseReads == ViewDatabaseReads.WhenRefreshed ? new ViewReads(engine) : null;
        if (_reads is null)
        {
            _retainer = engine.Retain(Held(engine, prepared), options.Versions == ViewVersions.Every, options.MaxBacklog);
            _commit = _retainer.From;
        }

        ContextDigest = (options.Context ?? prepared.Context) is { IsEmpty: false } context ? context.CanonicalHash : null;
        if (options.RefreshQueryPlan is not null || options.PlanImprovementMargin is not null)
        {
            _choice = new PreparedQuery.PlanChoice(options.RefreshQueryPlan, options.PlanImprovementMargin);
        }

        if (options.PlanSwap == ViewPlanSwap.LowJitter)
        {
            _buildAtSwap = BuildAtSwap;
        }
    }

    /// <summary>The result's key columns.</summary>
    public IReadOnlyList<string> Key => _options.Key;

    /// <summary>How the view computes an update (D343): as the host asked in its options.</summary>
    public ViewMaintenance Maintenance => _options.Maintenance;

    /// <summary>
    /// The digest of the context the view binds (<see cref="ViewOptions{TKey, TRow}.Context"/>), or of
    /// the one its statement folded when it was prepared with values; null when it has neither
    /// (design 74 §3 a).
    /// </summary>
    /// <remarks>
    /// It is <see cref="RequestContext.CanonicalHash"/>: every name, kind, type and value, and no purpose
    /// or actor. The grants a view was made with stand for its life, so a host that changes a grant finds
    /// the views it touches by comparing this with the digest of the principal's binding now, and makes
    /// those again. Grants kept in a table need none of this: a refresh revokes them.
    /// </remarks>
    public string? ContextDigest { get; }

    /// <summary>The result's columns, as a <see cref="ViewRow"/> reads them.</summary>
    public Schema Columns => Volatile.Read(ref _prepared).OutputSchema;

    /// <summary>
    /// The statement the view runs now: the host's, for a view made from a prepared statement, until a
    /// table it reads changes shape; then the one the view prepared in its place, with the same text
    /// and options (D341). A drift does not replace it: the view has it planned again in place (design
    /// 70 §2). A host that keeps the statement, to run it in full, show its plan or hand it on, takes
    /// this one after an update rather than prepare its own: it runs the view's plan exactly.
    /// </summary>
    public PreparedQuery Statement => Volatile.Read(ref _prepared);

    /// <summary>The version of the epoch the view was last updated to.</summary>
    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>How many keys the view holds a row for.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _table.Live;
            }
        }
    }

    /// <summary>
    /// What the view holds, in bytes, against <see cref="ViewOptions{TKey, TRow}.StateBudget"/>: its
    /// rows, what it keeps of its reads of databases (D340), what its incremental maintenance keeps, and
    /// an instance built beside it and waiting to be swapped in (design 70 §2), which the budget
    /// does not count, since the swap lets one of the two go.
    /// </summary>
    public long StateBytes
    {
        get
        {
            lock (_gate)
            {
                return _table.Bytes
                    + (_reads?.Bytes ?? 0)
                    + (_maintained?.StateBytes ?? 0)
                    + (_green is { Build.IsCompletedSuccessfully: true } built ? built.Build.Result.StateBytes : 0);
            }
        }
    }

    /// <summary>
    /// What the view keeps of each database it reads (D340): when it last read it, for which version,
    /// how many times it has, and the rows it keeps. Empty for a view that reads no database.
    /// </summary>
    public IReadOnlyList<ViewDatabaseRead> DatabaseReads => _reads?.Summary() ?? [];

    /// <summary>The sum of the live rows' hashes, mod 2³²: zero without a row hash.</summary>
    public uint Checksum
    {
        get
        {
            lock (_gate)
            {
                return _table.Checksum;
            }
        }
    }

    internal static async ValueTask<StandingView<TKey, TRow>> CreateAsync(
        ChalkEngine engine,
        string sql,
        ViewBinding binding,
        ViewOptions<TKey, TRow> options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        Validate(engine, binding, options);
        if (options.Context is not null)
        {
            throw new ArgumentException(
                "a view made from a statement's text prepares it with no context, so ViewOptions.Context has "
                + "nothing to bind. Prepare the statement for the context's shape, and make the view from it.",
                nameof(options));
        }

        var prepare = ViewPrepare.For(options);
        var prepared = await engine.PrepareAsync(sql, prepare, ct).ConfigureAwait(false);
        return await MakeAsync(engine, sql, prepare, prepared, binding, options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A view over a statement the host has prepared (D341, design 66 §1): it runs the host's own
    /// plan, so a run of the statement at the view's epoch gives its rows bit for bit.
    /// </summary>
    internal static async ValueTask<StandingView<TKey, TRow>> CreateAsync(
        ChalkEngine engine,
        PreparedQuery prepared,
        ViewBinding binding,
        ViewOptions<TKey, TRow> options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(prepared);
        Validate(engine, binding, options);
        if (!ReferenceEquals(prepared.Engine, engine))
        {
            throw new ArgumentException(
                "the statement was prepared on another engine. A view runs on the engine its statement was "
                + "prepared on.",
                nameof(prepared));
        }

        if (options.Prepare is not null)
        {
            throw new ArgumentException(
                "the statement is prepared already, so ViewOptions.Prepare has nothing to prepare. Leave it "
                + "unset, or make the view from the statement's text.",
                nameof(options));
        }

        // A statement prepared for a context's shape binds a principal's values at each run: the view
        // gives them, for its life, and they must fit the shape as an execution's must (design 74 §3 c).
        if (options.Context is { } context)
        {
            try
            {
                prepared.CheckBinding(context);
            }
            catch (ArgumentException misfit)
            {
                var reason = misfit.ParamName is { } name
                    ? misfit.Message.Replace($" (Parameter '{name}')", string.Empty, StringComparison.Ordinal)
                    : misfit.Message;
                throw new ArgumentException($"ViewOptions.Context does not fit the statement: {reason}", nameof(options), misfit);
            }
        }
        else if (prepared.PreparedForShape)
        {
            throw new ArgumentException(
                "the statement was prepared for a request context's shape, so each run binds a principal's "
                + "values: give the view them in ViewOptions.Context. One statement prepared for the shape then "
                + "serves every principal's view.",
                nameof(options));
        }

        // A view that reads databases is planned under its own cap on local joins; one made from a
        // prepared statement holds the statement to it.
        var cap = prepared.Options.JoinPolicy?.LocalJoinMaxRows ?? 0;
        if (options.DatabaseReads == ViewDatabaseReads.WhenRefreshed
            && ViewPlan.ReadsOutsideEpochs(engine, prepared.Plan)
            && cap != options.LocalJoinMaxRows)
        {
            throw new ArgumentException(
                $"the view reads a database and states a cap on local joins of {options.LocalJoinMaxRows} rows, "
                + $"and the statement was planned under {(cap > 0 ? $"a cap of {cap}" : "none")}. Prepare it with "
                + "the view's cap in PrepareOptions.JoinPolicy.LocalJoinMaxRows, and every cross-source join local.",
                nameof(prepared));
        }

        return await MakeAsync(engine, prepared.Sql, prepared.Options, prepared, binding, options, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Refuses options no view can be made with, before anything is prepared.</summary>
    private static void Validate(ChalkEngine engine, ViewBinding binding, ViewOptions<TKey, TRow> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Map);
        if (options.Key is not { Count: > 0 })
        {
            throw new ArgumentException(
                "a view needs a key: name the result's columns that identify a row.", nameof(options));
        }

        if (options.StateBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.StateBudget, "a view's StateBudget is a number of bytes, more than none.");
        }

        if (!(options.ResetWhen > 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ResetWhen,
                "a view's ResetWhen is a fraction of its rows, more than none: 1 resets a reader whose "
                + "changes are no fewer than the rows, and infinity never resets one for size.");
        }

        if (!Enum.IsDefined(options.Maintenance))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Maintenance,
                "a view's Maintenance is Recompute or Incremental.");
        }

        if (options.PlanImprovementMargin is { } margin && !(margin >= 0 && margin < 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                margin,
                "a view's PlanImprovementMargin is a fraction of the cost of the plan it runs, from 0 up to, not "
                + "including, 1: 1 or more would never take a plan, and less than 0 would take one that costs more.");
        }

        if (!Enum.IsDefined(options.PlanSwap))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.PlanSwap,
                "a view's PlanSwap is AtNextUpdate or LowJitter.");
        }

        if (options.PlanSwap == ViewPlanSwap.LowJitter && options.Maintenance == ViewMaintenance.Recompute)
        {
            throw new ArgumentException(
                "a view that recomputes takes a new plan at its next update at no extra cost, so PlanSwap = "
                + "LowJitter would do nothing: make the view with ViewMaintenance.Incremental, or leave PlanSwap "
                + "at AtNextUpdate.",
                nameof(options));
        }

        if (!Enum.IsDefined(options.Versions))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Versions,
                "a view's Versions is Latest or Every.");
        }

        if (options.MaxBacklog < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxBacklog,
                "a view's MaxBacklog is a number of refreshes, at least one: the most the engine holds for a view "
                + "that publishes every version while it has yet to publish them.");
        }

        if (options.Versions == ViewVersions.Every && options.DatabaseReads != ViewDatabaseReads.None)
        {
            throw new ArgumentException(
                "a view that reads a database cannot publish every version: its rows from the database are read "
                + "when it updates, and no refresh holds them. Make the view with Versions = Latest, or over "
                + "in-process tables only.",
                nameof(options));
        }

        if (options.LocalJoinMaxRows <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.LocalJoinMaxRows,
                "a view's LocalJoinMaxRows is a number of rows, more than none: the most a cross-source join "
                + "may fetch to join locally. StandingViews.DefaultLocalJoinMaxRows is the value to pass "
                + "without a better one.");
        }

        binding.Refuse(engine.ClockName);
    }

    /// <summary>Checks the prepared statement as a view, and computes it at the engine's current epoch.</summary>
    private static async ValueTask<StandingView<TKey, TRow>> MakeAsync(
        ChalkEngine engine,
        string sql,
        PrepareOptions prepare,
        PreparedQuery prepared,
        ViewBinding binding,
        ViewOptions<TKey, TRow> options,
        CancellationToken ct)
    {
        var key = KeyOf(prepared, options.Key);
        ViewChecks.Check(engine, prepared, key, options);
        binding.Declare(prepared, options.Rebindable, engine.ClockName);
        if ((options.RefreshQueryPlan is not null || options.PlanImprovementMargin is not null)
            && prepared.Choice is not null)
        {
            throw new ArgumentException(
                "the statement carries another view's RefreshQueryPlan or PlanImprovementMargin already, and a "
                + "statement keeps one view's: make this view from the statement's text, or without either.",
                nameof(prepared));
        }

        var view = new StandingView<TKey, TRow>(engine, sql, prepare, options, binding, prepared, key);
        try
        {
            await view.AttachAsync(prepared, ct).ConfigureAwait(false);
            if (options.Maintenance == ViewMaintenance.Incremental)
            {
                view._maintained = await view.MaintainAsync(prepared, key, ct).ConfigureAwait(false);
            }

            await view.UpdateAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await view.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return view;
    }

    /// <summary>
    /// Brings the view to the engine's latest epoch, and returns that epoch's version. An update at
    /// the epoch the view already holds does nothing, since its inputs have not changed.
    /// </summary>
    /// <remarks>
    /// An update that fails leaves the view at the epoch it held. One that finds a key twice in the
    /// result faults it: every call then throws that fault until the view is disposed.
    /// </remarks>
    public ValueTask<long> UpdateAsync(CancellationToken ct = default) => RunUpdateAsync(rebind: null, ct);

    /// <summary>
    /// Binds new values to parameters the view declared in <see cref="ViewOptions{TKey, TRow}.Rebindable"/>,
    /// and brings the view to the engine's latest epoch with them (design 77 §5): a dictionary, or an
    /// object whose properties name them. The view keeps its plan. Kept incrementally, it runs again only
    /// the operators that read a moved value, over what they hold, and its readers get the keys whose rows
    /// changed. Returns the epoch's version, which is the one the view held when no refresh came between.
    /// </summary>
    /// <remarks>
    /// A name the view did not declare, or a list it cannot take, is refused before anything moves. An
    /// update with the new values that fails leaves the view with the values and the epoch it held.
    /// </remarks>
    public async ValueTask<ViewStamp> RebindAsync(object parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Usable();
        var binding = ViewBinding.Of(parameters);
        await RunUpdateAsync(binding.Named ?? throw new ArgumentException(
            "a rebind binds parameters by name: give a dictionary, or an object whose properties name them.",
            nameof(parameters)), ct).ConfigureAwait(false);
        return Stamp;
    }

    /// <summary>Where the view's rows stand: its version, and how many times it has been rebound (design 78 §3).</summary>
    public ViewStamp Stamp
    {
        get
        {
            lock (_gate)
            {
                return new ViewStamp(_version, _rebinds);
            }
        }
    }

    /// <summary>
    /// Brings the view at least to <paramref name="atLeast"/> (design 78 §3), and returns where it stands:
    /// a host that changed what the view reads, by a refresh (whose version it numbered, or found with
    /// <see cref="ChalkEngine.VersionOf"/>) or a rebind (whose stamp it was returned), waits here until
    /// the view shows the change. A version the engine has not published yet is waited for.
    /// </summary>
    /// <exception cref="ArgumentException">The stamp's generation is one the view has not been rebound to.</exception>
    public async ValueTask<ViewStamp> UpdateToAsync(ViewStamp atLeast, CancellationToken ct = default)
    {
        while (true)
        {
            var published = _engine.NextPublished;
            await UpdateAsync(ct).ConfigureAwait(false);
            var stamp = Stamp;
            if (stamp >= atLeast)
            {
                return stamp;
            }

            if (stamp.Version >= atLeast.Version)
            {
                throw new ArgumentException(
                    $"the view stands at generation {stamp.Generation}, and the stamp names generation "
                    + $"{atLeast.Generation}: it has not been rebound that far.",
                    nameof(atLeast));
            }

            if (_engine.Version < atLeast.Version)
            {
                await published.WaitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>An update, with <paramref name="rebind"/>'s values bound first when it is a rebind.</summary>
    private async ValueTask<long> RunUpdateAsync(IReadOnlyDictionary<string, object?>? rebind, CancellationToken ct)
    {
        Usable();
        await _updating.WaitAsync(ct).ConfigureAwait(false);
        var tracker = _options.Tracker;
        var started = tracker is null ? 0L : Stopwatch.GetTimestamp();
        var allocated = tracker is null ? 0L : GC.GetTotalAllocatedBytes();
        var taken = _updates;
        IReadOnlyDictionary<string, object?>? before = null;
        var rebound = false;
        try
        {
            if (rebind is not null)
            {
                before = _binding.Rebind(rebind);
                rebound = true;
                _rebound = true;
            }

            var version = await CatchUpAsync(ct).ConfigureAwait(false);
            if (rebound)
            {
                lock (_gate)
                {
                    _rebinds++;
                }
            }
            if (tracker is not null && _updates != taken)
            {
                var update = new ViewUpdate(
                    version,
                    Stopwatch.GetElapsedTime(started),
                    GC.GetTotalAllocatedBytes() - allocated,
                    _lastWhole,
                    _lastChanges,
                    _maintained?.PlanDigest ?? Volatile.Read(ref _prepared).PlanDigest);
                tracker.Updated(in update);
            }

            BuildBeside();
            return version;
        }
        catch
        {
            // A failed update keeps nothing new: the view keeps the epoch, the reads and the values it held.
            _reads?.Abandon();
            if (rebound)
            {
                _binding.Restore(before);
            }

            throw;
        }
        finally
        {
            _rebound = false;
            _updating.Release();
        }
    }

    /// <summary>How many times an update catches up at once with refreshes that committed while it ran.</summary>
    private const int Settles = 3;

    /// <summary>
    /// The body of <see cref="UpdateAsync"/>, under its lock: the view brought to the latest epoch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What an update reads is named by its pin (design 78 §1): the count of commits read in the same hold
    /// of the gate as its tables. Where it is the count the stamp noted before staging recorded, the update
    /// read that epoch's tables whatever has committed since, and the view takes it at that stamp: no
    /// refresh is held off, and nothing staged is thrown away. A refresh that committed while it ran is then
    /// caught up at once, a few times at most, rather than at the next update, which a quiet feed may never
    /// send. Only a refresh that committed between noting the stamp and pinning makes it read again.
    /// </para>
    /// <para>
    /// The count is this engine's: another engine's commit to a source they share changes the rows without
    /// moving it, as it moves no epoch here. An update that pinned nothing, which a plan of no table may,
    /// is taken only when no refresh ran through it, and after two attempts it is staged with refreshes
    /// held off.
    /// </para>
    /// </remarks>
    private async ValueTask<long> CatchUpAsync(CancellationToken ct)
    {
        Usable();
        var settled = 0;
        var named = true;
        for (var attempt = 0; ; attempt++)
        {
            var changes = _engine.Changes;
            if ((changes & 1) == 1)
            {
                await _engine.WhenQuietAsync(ct).ConfigureAwait(false);
                continue;
            }

            var at = _engine.Stamp;
            if (_retainer is { } retainer)
            {
                if (retainer.Overrun)
                {
                    Overrun(retainer);
                }

                // Asked after the stamp is read: every refresh held up to it is stamped by then (design 78 §4).
                if (retainer.Next(_commit) is { } held)
                {
                    if (await TakeHeldAsync(retainer, held, ct).ConfigureAwait(false))
                    {
                        return _version;
                    }

                    continue;
                }
            }

            if (at.Epoch == _epoch && !_rebound)
            {
                _reads?.Abandon();
                return _version;
            }

            if (!_rebound && Relabel(at))
            {
                return _version;
            }

            if (!named && attempt >= 2)
            {
                return await CatchUpBetweenRefreshesAsync(ct).ConfigureAwait(false);
            }

            var staged = await StageAsync(at, ct).ConfigureAwait(false);
            named = staged.Commit >= 0;
            var read = named
                ? staged.Commit == at.Commit
                : _engine.Changes == changes;
            if (!read)
            {
                // What was read is not the stamp noted: read again, once the refresh has published.
                Keep(staged);
                continue;
            }

            Take(staged, at);
            _rebound = false;
            if (named && _engine.Stamp.Epoch != _epoch && ++settled < Settles && _options.Versions == ViewVersions.Latest)
            {
                continue;
            }

            return _version;
        }
    }

    /// <summary>
    /// Publishes a refresh held for the view (design 78 §4, §5), at its stamp: by a relabel where nothing it
    /// wrote is read, or else by staging the view at the states it committed. True where the update stops
    /// there — at a barrier, or at a change of a view that publishes every version — so that a reader that
    /// drains after the update is sent it.
    /// </summary>
    /// <remarks>
    /// A statement prepared anew may read a table the refresh's states were not held for: it is pinned as it
    /// stands, which names another refresh, and the held one is let go unpublished. A failed update keeps it
    /// held for the next.
    /// </remarks>
    private async ValueTask<bool> TakeHeldAsync(EpochRetainer retainer, HeldEpoch held, CancellationToken ct)
    {
        var stamp = held.Stamp!;
        if (!_rebound && Relabel(stamp))
        {
            retainer.Taken(held);
            return held.Barrier;
        }

        var staged = await StageAsync(stamp, ct, held.Tables).ConfigureAwait(false);
        if (staged.Commit != stamp.Commit)
        {
            Keep(staged);
            retainer.Taken(held);
            return false;
        }

        Take(staged, stamp);
        _rebound = false;
        retainer.Taken(held);
        return true;
    }

    /// <summary>Faults a view that publishes every version and fell further behind than its MaxBacklog.</summary>
    private void Overrun(EpochRetainer retainer)
    {
        var fault = new StandingViewException(
            ChalkErrorCodes.MemoryBudgetExceeded,
            $"The view publishes every version, and the engine held {retainer.MaxBacklog} refreshes it had yet to "
            + $"publish, its MaxBacklog, when another came: it was updated more slowly than the engine refreshed. "
            + "What was held has been let go, and the view keeps this fault until it is disposed. Make it again, "
            + "update it more often, or raise ViewOptions.MaxBacklog.");
        _fault = fault;
        throw fault;
    }

    /// <summary>The tables of <paramref name="prepared"/> whose states the engine can hold for the view (design 78 §4).</summary>
    private static (string SourceId, string Table, ITrackedSource Source)[] Held(ChalkEngine engine, PreparedQuery prepared)
    {
        var held = new List<(string SourceId, string Table, ITrackedSource Source)>();
        foreach (var (sourceId, table) in prepared.TablesRead)
        {
            if (engine.SourceById(sourceId) is ITrackedSource tracked)
            {
                held.Add((sourceId, table, tracked));
            }
        }

        return [.. held];
    }

    /// <summary>
    /// Moves the view's version to the engine's when every refresh since its own wrote nothing it reads
    /// (design 78 §2): none of its tables, and not the clock where it reads the clock. Its rows are then
    /// the statement's rows at the new version too, so nothing is computed, and readers are sent a commit
    /// at it. Not for a view that reads a database, whose rows a refresh is a reason to read again, nor for
    /// one with a new plan to take or a statement to prepare again.
    /// </summary>
    private bool Relabel(EpochStamp stamp)
    {
        // A view whose statement has a plan it does not run yet takes it at an update, and a stale one is
        // prepared again there: neither is only a version.
        if (_reads is not null || _maintained is { IsCurrent: false } || _prepared.IsStale || _prepared.ChangedShape)
        {
            return false;
        }

        var prepared = _prepared;
        if (_readSet is not { } known || !ReferenceEquals(known.For, prepared))
        {
            // The question asked of each table a refresh wrote, made once per statement rather than at each update.
            known = (prepared, prepared.TablesRead.ToHashSet(TableNames.Instance).Contains);
            _readSet = known;
        }

        if (!_engine.Untouched(_epoch, stamp.Epoch, known.Reads, _readsTime || _namesClock))
        {
            return false;
        }

        lock (_gate)
        {
            _epoch = stamp.Epoch;
            _version = stamp.Version;
        }

        _commit = stamp.Commit;
        Relabels++;
        return true;
    }

    /// <summary>
    /// The update, staged with refreshes held off, after two attempts each overtaken by one. A method of
    /// its own, so that the closure it hands the engine is made only when it runs.
    /// </summary>
    private ValueTask<long> CatchUpBetweenRefreshesAsync(CancellationToken ct)
    {
        HeldRefreshes++;
        return CatchUpHoldingRefreshesAsync(ct);
    }

    /// <summary>How many updates have held refreshes off: for a test, which expects none where pins are named.</summary>
    internal int HeldRefreshes { get; private set; }

    private ValueTask<long> CatchUpHoldingRefreshesAsync(CancellationToken ct) =>
        _engine.WithoutRefreshAsync(
            async () =>
            {
                var stamp = _engine.Stamp;
                if (stamp.Epoch != _epoch || _rebound)
                {
                    Take(await StageAsync(stamp, ct).ConfigureAwait(false), stamp);
                }

                return _version;
            },
            ct);

    /// <summary>Applies <paramref name="staged"/>, counting the keys it changes for the tracker, and keeps its staging.</summary>
    private void Take(Staged staged, EpochStamp stamp)
    {
        var before = _table.Position;
        Apply(staged, stamp);
        _commit = stamp.Commit;
        _lastChanges = _table.Position - before;
        Keep(staged);
    }

    /// <summary>The staging for this update: the one kept from the last, emptied.</summary>
    private Staged Stage(bool delta)
    {
        var staged = _staging ?? new Staged();
        _staging = null;
        return staged.Begin(delta);
    }

    /// <summary>Keeps <paramref name="staged"/> for the next update, letting go of room a large one grew.</summary>
    private void Keep(Staged staged)
    {
        staged.Trim();
        _staging = staged;
    }

    /// <summary>
    /// Puts the host's say on the view's plans on <paramref name="prepared"/> (design 70 §1, §3), where a
    /// background run asks it; with a margin, asks the planner too to keep the plan the statement runs,
    /// so the first plan a background run makes can be priced against it. A statement keeps one view's:
    /// <c>MakeAsync</c> refuses a host's statement that carries another view's. Under a low-jitter swap,
    /// the view also listens for the statement's new plans, to build each beside its own as it swaps in.
    /// </summary>
    private async ValueTask AttachAsync(PreparedQuery prepared, CancellationToken ct)
    {
        if (_buildAtSwap is not null)
        {
            prepared.PlanSwapped += _buildAtSwap;
        }

        if (_choice is not null)
        {
            prepared.Choice = _choice;
            if (_choice.ImprovementMargin is not null)
            {
                await prepared.KeepPlanAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Stops listening to <paramref name="prepared"/>, and takes the host's say off it when it is the view's.</summary>
    private void Detach(PreparedQuery prepared)
    {
        if (_buildAtSwap is not null)
        {
            prepared.PlanSwapped -= _buildAtSwap;
        }

        if (_choice is not null && ReferenceEquals(prepared.Choice, _choice))
        {
            prepared.Choice = null;
        }
    }

    /// <summary>How many changes reset a reader: <paramref name="resetWhen"/> of the live rows, at least one.</summary>
    private static int Enough(double resetWhen, int live) =>
        double.IsPositiveInfinity(resetWhen)
            ? int.MaxValue
            : (int)Math.Min(int.MaxValue, Math.Max(1d, Math.Ceiling(resetWhen * live)));

    /// <summary>The view's rows, in bytes, without what it keeps of its databases.</summary>
    private long StateRowBytes()
    {
        lock (_gate)
        {
            return _table.Bytes;
        }
    }

    /// <summary>
    /// Completes once the view holds <paramref name="version"/> or a later one, waiting for the
    /// engine to publish it and updating the view if need be: the rendezvous for a request whose
    /// answer is "after version k".
    /// </summary>
    public async ValueTask ReachedAsync(long version, CancellationToken ct = default)
    {
        while (true)
        {
            Usable();
            if (Version >= version)
            {
                return;
            }

            await _engine.WhenVersionAsync(version, ct).ConfigureAwait(false);
            await UpdateAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends <paramref name="sink"/> every key changed since <paramref name="cursor"/>, with its
    /// latest row, in change order, and a deleted key as a delete; then commits the view's version
    /// and checksum, and moves the cursor on. At most <paramref name="budget"/> keys a call: out of
    /// budget, it returns <see cref="DrainOutcome.Partial"/> without committing, and the next call
    /// carries on.
    /// </summary>
    /// <remarks>
    /// A drain starts with <see cref="IViewSink{TKey, TRow}.Reset"/> and then sends every live row when
    /// the cursor is <see cref="ViewCursor.Start"/>, when the view has prepared its statement anew
    /// since, when deletes it needs have been dropped, or when what changed since it is no fewer rows
    /// than the view holds. A drain allocates nothing: the sink gets references into the view.
    /// </remarks>
    public DrainOutcome Drain(ref ViewCursor cursor, IViewSink<TKey, TRow> sink, int budget = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, 1);
        Usable();

        lock (_gate)
        {
            if (cursor.Owner is not null && !ReferenceEquals(cursor.Owner, this))
            {
                throw new ArgumentException(
                    "the cursor belongs to another view. A cursor is one reader's place in one view.",
                    nameof(cursor));
            }

            var reset = cursor.Owner is null
                || cursor.Generation != _generation
                || cursor.Position < _table.DroppedThrough;

            // What changed since the cursor, counted as far as the reset threshold: by default no
            // fewer changes than rows, where the whole result is no more to send than the changes
            // are (D341: ResetWhen, a fraction of the live rows).
            var enough = Enough(_options.ResetWhen, _table.Live);
            var first = _table.FirstAfter(reset ? 0 : cursor.Position, enough, out var changed);
            if (!reset && cursor.ResetFrom == 0 && changed >= enough)
            {
                reset = true;
            }

            if (reset)
            {
                sink.Reset();
                cursor = new ViewCursor
                {
                    Owner = this,
                    Generation = _generation,
                    Position = 0,
                    ResetFrom = Math.Max(1, _table.Position),
                    Committed = cursor.Committed,
                };
                first = _table.First;
            }

            var sent = 0;
            for (var slot = first; slot != -1; slot = _table.NextOf(slot))
            {
                ref readonly var entry = ref _table[slot];
                if (!entry.Live && cursor.ResetFrom > 0 && entry.Position <= cursor.ResetFrom)
                {
                    // Deleted before this reset began, so never sent in it.
                    cursor.Position = entry.Position;
                    continue;
                }

                if (sent == budget)
                {
                    return DrainOutcome.Partial;
                }

                if (entry.Live)
                {
                    sink.Upsert(in entry.Key, in entry.Row);
                }
                else
                {
                    sink.Delete(in entry.Key);
                }

                cursor.Position = entry.Position;
                sent++;
            }

            if (sent > 0 || reset || cursor.ResetFrom > 0 || cursor.Committed != _version || cursor.CommittedGeneration != _rebinds)
            {
                sink.Commit(new ViewStamp(_version, _rebinds), _table.Checksum);
                cursor.Committed = _version;
                cursor.CommittedGeneration = _rebinds;
            }

            cursor.ResetFrom = 0;
            cursor.Position = _table.Position;
            return DrainOutcome.Complete;
        }
    }

    /// <summary>Lets the view go. An update in flight finishes first.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _updating.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            Detach(_prepared);
            _reads?.Dispose();
            _retainer?.Dispose();
            if (_maintained is { } maintained)
            {
                _maintained = null;
                await maintained.DisposeAsync().ConfigureAwait(false);
            }

            if (_green is { } green)
            {
                LetGo(green);
            }

            await Task.WhenAll(_lettingGo).ConfigureAwait(false);
            _lettingGo.Clear();
        }
        finally
        {
            _updating.Release();
        }
    }

    private void Usable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fault is { } fault)
        {
            throw fault;
        }
    }

    /// <summary>
    /// Runs the statement at <paramref name="stamp"/>'s epoch and maps every row, refusing a key the
    /// result gives twice. The view's rows are untouched until <see cref="Apply"/>.
    /// </summary>
    private async ValueTask<Staged> StageAsync(EpochStamp stamp, CancellationToken ct, RetainedTables? at = null)
    {
        await PreparedCurrentAsync(ct).ConfigureAwait(false);
        var prepared = _prepared;

        if ((_readsTime || _namesClock) && stamp.Clock is null)
        {
            throw new StandingViewException(
                ChalkErrorCodes.ClockRequired,
                $"The view reads the current time, and the engine's clock, '{_engine.ClockName}', has not "
                + $"been set as of version {stamp.Version}. Advance it in a refresh with AdvanceClock "
                + "before the view is updated.");
        }

        var values = _binding.ValuesForUpdate(prepared, _engine.ClockName, stamp.Clock);
        if (_maintained is { } maintained)
        {
            // Until it is applied, this update leaves the view behind the statement (D343).
            var whole = _resync;
            _resync = true;
            MaintainedUpdate update;
            try
            {
                update = await maintained.UpdateAsync(values, stamp.Clock ?? default, stamp.Version, whole, ct, at).ConfigureAwait(false);
            }
            catch (MaintainedKeyFault duplicate)
            {
                var fault = new StandingViewException(
                    ChalkErrorCodes.DeclarationNotHeld,
                    $"The view's key ({string.Join(", ", _options.Key)}) = ({duplicate.Key}) is given by two rows "
                    + $"{duplicate.Where} at version {stamp.Version}. A view keeps one row per key: correct the "
                    + "statement's key, or the Map that builds the host's key from it. The view keeps this fault "
                    + "until it is disposed.");
                _fault = fault;
                throw fault;
            }

            // Whole for the tracker only when computed from nothing: a low-jitter swap catches up.
            _lastWhole = update.Restarted;
            var changed = Changes(update, stamp);
            changed.Commit = update.Commit;
            return changed;
        }

        _lastWhole = true;
        await using var execution = await prepared.ExecuteAsync(
            values, context: _options.Context, arena: null, ct, version: stamp.Version).ConfigureAwait(false);
        if (_readsTime)
        {
            _clock.Now = stamp.Clock!.Value;
            execution.UseClock(_clock);
        }

        if (at is not null)
        {
            execution.ReadAt(at);
        }

        if (_reads is not null)
        {
            // What the update keeps of its databases' rows shares the budget with the view's rows.
            _reads.Begin(stamp, _options.StateBudget - StateRowBytes());
            execution.UseReads(_reads);
        }

        var staged = Stage(delta: false);
        await foreach (var batch in execution.Batches.WithCancellation(ct).ConfigureAwait(false))
        {
            using (batch)
            {
                Map(batch, staged, stamp);
            }
        }

        staged.Commit = execution.PinnedCommit;
        return staged;
    }

    private void Map(RecordBatch batch, Staged staged, EpochStamp stamp)
    {
        var columns = staged.ColumnsOf(batch);
        try
        {
            MapRows(batch, columns, staged, stamp);
        }
        finally
        {
            staged.LetGoOfColumns();
        }
    }

    private void MapRows(RecordBatch batch, IArrowArray[] columns, Staged staged, EpochStamp stamp)
    {
        for (var i = 0; i < batch.Length; i++)
        {
            var row = new ViewRow(columns, batch.Schema, i);
            _options.Map(in row, out var key, out var value);
            var hash = _options.RowHash is { } rowHash
                ? rowHash(Bytes<TKey>.Of(in key), Bytes<TRow>.Of(in value))
                : 0u;

            if (!staged.Add(key, value, hash, out var earlier))
            {
                var fault = new StandingViewException(
                    ChalkErrorCodes.DeclarationNotHeld,
                    $"The view's key ({string.Join(", ", _options.Key)}) = "
                    + $"({string.Join(", ", _key.Select(k => ViewColumns.Render(columns[k], i)))}) is given by "
                    + $"two rows of the result at version {stamp.Version}, rows {earlier} and "
                    + $"{staged.Count}. A view keeps one row per key: correct the statement's key, or the "
                    + "Map that builds the host's key from it. The view keeps this fault until it is disposed.");
                _fault = fault;
                throw fault;
            }
        }
    }

    /// <summary>
    /// A maintained update's rows, mapped (D343): every row, after a restart or when the view asked for
    /// the whole result; otherwise the rows each changed key holds now, and the host keys of the rows
    /// they held before.
    /// </summary>
    private Staged Changes(MaintainedUpdate update, EpochStamp stamp)
    {
        var staged = Stage(delta: !update.Whole);
        Map(update.Now, staged, stamp);
        if (update.Whole)
        {
            return staged;
        }

        var before = update.Before;
        if (before.Length > 0)
        {
            var columns = staged.ColumnsOf(before);
            try
            {
                for (var i = 0; i < before.Length; i++)
                {
                    var row = new ViewRow(columns, before.Schema, i);
                    _options.Map(in row, out var key, out _);
                    staged.Before.Add(key);
                }
            }
            finally
            {
                staged.LetGoOfColumns();
            }
        }

        return staged;
    }

    /// <summary>Makes the staged rows the view's, whole, under the lock drains take.</summary>
    private void Apply(Staged staged, EpochStamp stamp)
    {
        if (staged.Delta)
        {
            ApplyChanges(staged, stamp);
            _resync = false;
            return;
        }

        var added = 0L;
        for (var i = 0; i < staged.Count; i++)
        {
            if (!_table.Contains(in staged.Keys[i]))
            {
                added++;
            }
        }

        var kept = (_reads?.BytesKept ?? 0) + (_maintained?.StateBytes ?? 0);
        var bytes = _table.BytesWith(added) + kept;
        if (bytes > _options.StateBudget)
        {
            throw new StandingViewException(
                ChalkErrorCodes.MemoryBudgetExceeded,
                $"The view would hold about {bytes} bytes at version {stamp.Version}"
                + (_maintained is not null
                    ? $", {kept} of them kept by its incremental maintenance,"
                    : kept > 0 ? $", {kept} of them kept from its databases," : string.Empty)
                + $" past its StateBudget of {_options.StateBudget}. It keeps the epoch it held; raise "
                + "ViewOptions.StateBudget, or narrow the statement's result.");
        }

        lock (_gate)
        {
            var seen = ++_updates;
            for (var i = 0; i < staged.Count; i++)
            {
                _table.Put(in staged.Keys[i], in staged.Rows[i], staged.Hashes[i], seen);
            }

            _table.DeleteUnseen(seen);
            _table.Compact();
            _reads?.Commit();
            _epoch = stamp.Epoch;
            _version = stamp.Version;
        }

        _resync = false;
    }

    /// <summary>
    /// Applies a maintained update's changes (D343): a key whose row is gone is deleted, and each row a
    /// changed key holds now is put. A host key another unchanged row holds faults the view, as two
    /// rows mapped to one key in a recompute do.
    /// </summary>
    private void ApplyChanges(Staged staged, EpochStamp stamp)
    {
        var freed = staged.Freed;
        for (var i = 0; i < staged.Before.Count; i++)
        {
            freed.Add(staged.Before[i]);
        }

        var added = 0L;
        for (var i = 0; i < staged.Count; i++)
        {
            ref readonly var key = ref staged.Keys[i];
            if (!_table.Contains(in key))
            {
                added++;
            }
            else if (_table.IsLive(in key) && !freed.Contains(key))
            {
                var fault = new StandingViewException(
                    ChalkErrorCodes.DeclarationNotHeld,
                    $"Two keys of the view's result, ({string.Join(", ", _options.Key)}), are mapped to one host "
                    + $"key at version {stamp.Version}. A view keeps one row per key: correct the Map that builds "
                    + "the host's key from the row. The view keeps this fault until it is disposed.");
                _fault = fault;
                throw fault;
            }
        }

        var bytes = _table.BytesWith(added) + (_maintained?.StateBytes ?? 0);
        if (bytes > _options.StateBudget)
        {
            throw new StandingViewException(
                ChalkErrorCodes.MemoryBudgetExceeded,
                $"The view would hold about {bytes} bytes at version {stamp.Version}, what its incremental "
                + $"maintenance keeps included, past its StateBudget of {_options.StateBudget}. It keeps the epoch "
                + "it held; raise ViewOptions.StateBudget, or narrow the statement's result.");
        }

        // A key put again is not deleted first: a row put equal to the one it holds is no change.
        for (var i = 0; i < staged.Count; i++)
        {
            freed.Remove(staged.Keys[i]);
        }

        lock (_gate)
        {
            var seen = ++_updates;
            foreach (var key in freed)
            {
                _table.Delete(in key, seen);
            }

            for (var i = 0; i < staged.Count; i++)
            {
                _table.Put(in staged.Keys[i], in staged.Rows[i], staged.Hashes[i], seen);
            }

            _table.Compact();
            _epoch = stamp.Epoch;
            _version = stamp.Version;
        }
    }

    /// <summary>
    /// Keeps the prepared statement by incremental maintenance (D343), or refuses the view naming what
    /// in it cannot be kept so.
    /// </summary>
    private async ValueTask<MaintainedQuery> MaintainAsync(PreparedQuery prepared, int[] key, CancellationToken ct)
    {
        try
        {
            return await prepared.MaintainAsync(
                _binding.Values(prepared, _engine.ClockName, time: null),
                ViewBinding.ClockIndex(prepared, _engine.ClockName),
                key,
                ct,
                _options.Context,
                _binding.RebindableIndexes(prepared)).ConfigureAwait(false);
        }
        catch (NotMaintainableException refusal)
        {
            throw new StandingViewException(
                ChalkErrorCodes.UnsupportedInView,
                $"The view asks for incremental maintenance, and its statement cannot be maintained: "
                + $"{refusal.Message} The operator is at {refusal.Path} in the plan. Make the view with "
                + "ViewMaintenance.Recompute, or write the statement without it.");
        }
    }

    /// <summary>
    /// Prepares the statement anew when a table it reads changed shape, which its plan no longer fits.
    /// A statement a tenfold drift has made stale (D342) is planned again in the background instead,
    /// in place (design 70 §2): the view keeps running it meanwhile, and moves to a new plan as it moves
    /// to any. Only one the background cannot plan again is prepared anew for drift, and only when the
    /// host's <see cref="ViewOptions{TKey, TRow}.RefreshQueryPlan"/> allows it. The view's readers reset
    /// only when the result's columns or key changed; otherwise they keep their place. A statement that
    /// no longer prepares faults the view.
    /// </summary>
    private async ValueTask PreparedCurrentAsync(CancellationToken ct)
    {
        if (!_prepared.IsStale)
        {
            await MaintainedCurrentAsync(ct).ConfigureAwait(false);
            return;
        }

        // A statement stale for drift alone runs correctly on the plan it has. It is planned again in
        // place, off this thread, as one whose numbers moved twofold is: the run asks the host, the
        // planner applies the margin, and a plan that comes back the same is kept with nothing rebuilt.
        if (!_prepared.ChangedShape)
        {
            if (_prepared.MayPlanAgain)
            {
                _engine.PlanAgainInBackground(_prepared);
                await MaintainedCurrentAsync(ct).ConfigureAwait(false);
                return;
            }

            if (!_prepared.MayRefreshPlan())
            {
                await MaintainedCurrentAsync(ct).ConfigureAwait(false);
                return;
            }
        }

        try
        {
            var reason = _prepared.ChangedShape ? PrepareReason.ChangedShape : PrepareReason.Drift;
            var prepared = await _prepared.PrepareAgainAsync(_sql, _prepare, reason, ct).ConfigureAwait(false);
            var key = KeyOf(prepared, _options.Key);
            ViewChecks.Check(_engine, prepared, key, _options);
            if (_maintained is { } kept)
            {
                // The plan is a new one: it is maintained from the start, and its first update is whole.
                // An instance built beside the old one is of a statement that is no more.
                if (_green is { } green)
                {
                    LetGo(green);
                }

                _maintained = null;
                await kept.DisposeAsync().ConfigureAwait(false);
                _maintained = await MaintainAsync(prepared, key, ct).ConfigureAwait(false);
                _resync = true;
            }

            lock (_gate)
            {
                // A result with the columns and key it had keeps its readers where they are: the next
                // update diffs the new plan's rows against the view's, as any update does (D342). Only
                // a result that changed resets them.
                if (!SameResult(_prepared.OutputSchema, prepared.OutputSchema) || !_key.AsSpan().SequenceEqual(key))
                {
                    _generation++;
                }

                Detach(_prepared);
                Volatile.Write(ref _prepared, prepared);
                _retainer?.Reads(Held(_engine, prepared));
                _key = key;
                _readsTime = ViewPlan.ReadsCurrentTime(prepared.Plan);
                _namesClock = ViewBinding.NamesClock(prepared, _engine.ClockName);
            }

            // The plan is a new one, and its reads are in new places: read them all again.
            _reads?.Clear();
            await AttachAsync(prepared, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is ChalkException or ArgumentException)
        {
            _fault = failure;
            throw;
        }
    }

    /// <summary>
    /// Takes the plan the background made for the statement, once it has swapped in (design 69 §4):
    /// the view is maintained from it from the start, and its next update is whole, as after the
    /// statement is prepared again; readers keep their place, as the result's columns and key are the
    /// same. A new plan that cannot be maintained is declined: the plan the view keeps answers the same
    /// rows. Under <see cref="ViewPlanSwap.LowJitter"/>, the new plan is built beside the one the view
    /// keeps instead (design 70 §2).
    /// </summary>
    private async ValueTask MaintainedCurrentAsync(CancellationToken ct)
    {
        if (_maintained is not { IsCurrent: false } kept)
        {
            // An instance built for a plan the view no longer moves to waits for nothing.
            if (_green is { } stale)
            {
                LetGo(stale);
            }

            return;
        }

        if (_options.PlanSwap == ViewPlanSwap.LowJitter)
        {
            await SwapBesideAsync(kept).ConfigureAwait(false);
            return;
        }

        MaintainedQuery next;
        try
        {
            next = await MaintainAsync(_prepared, _key, ct).ConfigureAwait(false);
        }
        catch (StandingViewException refusal) when (refusal.Code == ChalkErrorCodes.UnsupportedInView)
        {
            kept.PlannedWith = _prepared.Plans;
            kept.KeepSparse = true;
            return;
        }

        _maintained = next;
        await kept.DisposeAsync().ConfigureAwait(false);
        _resync = true;
    }

    /// <summary>
    /// Moves the view to the plan its statement swapped in, without computing it from nothing at an
    /// update (design 70 §2). An instance on the new plan is built in the background, from the moment
    /// the statement swaps the plan in, while the view keeps updating <paramref name="blue"/>
    /// (<see cref="BuildBeside"/>). The first update after the build swaps the new instance in: it
    /// catches up to that update's epoch and gives its whole result, which the view compares with its
    /// rows, so readers keep their place. A newer plan meanwhile abandons the build, and its swap starts
    /// one on that plan; a plan that cannot be maintained, or a build that fails, keeps
    /// <paramref name="blue"/> on its plan, as a plan the view cannot maintain is declined.
    /// </summary>
    private async ValueTask SwapBesideAsync(MaintainedQuery blue)
    {
        if (_green is not { } green)
        {
            return;
        }

        if (!ReferenceEquals(green.PlannedFor, _prepared.Plans))
        {
            // A newer plan swapped in while this one was built.
            LetGo(green);
            return;
        }

        if (!green.Build.IsCompleted)
        {
            return;
        }

        if (green.Build.IsCompletedSuccessfully)
        {
            // Built: it takes blue's place at this update, caught up and compared whole.
            _green = null;
            green.Cancel.Dispose();
            _maintained = green.Build.Result;
            await blue.DisposeAsync().ConfigureAwait(false);
            _resync = true;
            return;
        }

        if (green.Build.Exception?.GetBaseException() is not StandingViewException { Code: var code }
            || code != ChalkErrorCodes.UnsupportedInView)
        {
            _engine.Log.LogWarning(
                green.Build.Exception?.GetBaseException(),
                "a view's build of a new plan beside plan {Kept:x16} failed; it keeps that plan",
                blue.PlanDigest);
        }

        LetGo(green);
        blue.PlannedWith = green.PlannedFor;
        blue.KeepSparse = true;
    }

    /// <summary>
    /// Starts building the plan its statement just swapped in beside the one the view runs (design 70
    /// §2), rather than at the end of the view's next update, so that the update after a swap can take
    /// it. The statement raises this under the engine's refresh lock, which an update may wait for while
    /// it holds the view's own: the build is started off this thread, under the update lock.
    /// </summary>
    private void BuildAtSwap() => Volatile.Write(ref _buildingAtSwap, Task.Run(BuildAtSwapAsync));

    private Task _buildingAtSwap = Task.CompletedTask;

    /// <summary>For tests: the start of the last build at a swap, done once the build has begun or was found not needed.</summary>
    internal Task BuildingAtSwap => Volatile.Read(ref _buildingAtSwap);

    private async Task BuildAtSwapAsync()
    {
        await _updating.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _fault is not null)
            {
                return;
            }

            // An instance building for a plan swapped out since would be let go at the next update.
            if (_green is { } stale && !ReferenceEquals(stale.PlannedFor, _prepared.Plans))
            {
                LetGo(stale);
            }

            BuildBeside();
        }
        finally
        {
            _updating.Release();
        }
    }

    /// <summary>
    /// Starts building the plan the statement runs beside the one the view runs (design 70 §2), when
    /// the view builds new plans beside its own, runs another plan, and builds nothing yet. Called as
    /// the statement swaps a plan in, and at the end of an update, so that the build runs in the time
    /// before the next: the instance is made and computed from nothing off the update's thread, over the
    /// epoch current when the build begins.
    /// </summary>
    private void BuildBeside()
    {
        if (_options.PlanSwap != ViewPlanSwap.LowJitter
            || _green is not null
            || _maintained is not { IsCurrent: false })
        {
            return;
        }

        _green = StartGreen();
    }

    /// <summary>
    /// Starts <see cref="BuildBeside"/>'s build. A method of its own, so that the closure the build runs
    /// in is made only when there is one to start, and not at the end of every update.
    /// </summary>
    private Green StartGreen()
    {
        var prepared = _prepared;
        var key = _key;
        var clocked = _readsTime || ViewBinding.NamesClock(prepared, _engine.ClockName);
        var plannedFor = prepared.Plans;
        var hold = HoldGreenBuild;
        var cancel = new CancellationTokenSource();
        var stop = cancel.Token;
        var build = Task.Run(
            async () =>
            {
                if (hold is not null)
                {
                    await hold(stop).ConfigureAwait(false);
                }

                var next = await MaintainAsync(prepared, key, stop).ConfigureAwait(false);
                try
                {
                    var stamp = _engine.Stamp;
                    if (clocked && stamp.Clock is null)
                    {
                        throw new InvalidOperationException(
                            "the engine's clock has not been set, and the statement reads it.");
                    }

                    // Its rows are not read: the update that swaps it in gives them whole.
                    var values = _binding.Values(prepared, _engine.ClockName, stamp.Clock);
                    await next.UpdateAsync(values, stamp.Clock ?? default, stamp.Version, whole: false, stop).ConfigureAwait(false);
                    return next;
                }
                catch
                {
                    await next.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            },
            CancellationToken.None);
        return new Green(plannedFor, build, cancel);
    }

    /// <summary>
    /// Lets an instance built beside go: its build is stopped, and what it built disposed once the build
    /// has stopped, off the update's thread.
    /// </summary>
    private void LetGo(Green green)
    {
        if (ReferenceEquals(_green, green))
        {
            _green = null;
        }

        green.Cancel.Cancel();
        _lettingGo.RemoveAll(static release => release.IsCompleted);
        _lettingGo.Add(ReleaseAsync(green));
    }

    private static async Task ReleaseAsync(Green green)
    {
        // Stopped, failed or done, the build is over: what it threw is the view's to log, not to throw.
        // A build that failed disposed what it made itself.
        await ((Task)green.Build).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _ = green.Build.Exception;
        if (green.Build.IsCompletedSuccessfully)
        {
            await green.Build.Result.DisposeAsync().ConfigureAwait(false);
        }

        green.Cancel.Dispose();
    }

    /// <summary>The build of an instance beside, for tests that wait for it; null when none is building or built.</summary>
    internal Task? GreenBuild => _green?.Build;

    /// <summary>For tests: awaited by a build beside before it begins, with the token that stops it.</summary>
    internal Func<CancellationToken, Task>? HoldGreenBuild { get; set; }

    /// <summary>
    /// An instance of the view on a new plan, built in the background (design 70 §2): made, and its
    /// first update, which computes from nothing, run off the update's thread over the epoch current
    /// when the build began.
    /// </summary>
    /// <param name="PlannedFor">The statement's plans the instance is built on (<see cref="PreparedQuery.Plans"/>).</param>
    /// <param name="Build">The instance, once built.</param>
    /// <param name="Cancel">Stops the build when the view lets the instance go.</param>
    private sealed record Green(object PlannedFor, Task<MaintainedQuery> Build, CancellationTokenSource Cancel);

    /// <summary>For tests: the keys the kept staging has room for, or 0 while none is kept.</summary>
    internal int StagingRoom => _staging?.Keys.Length ?? 0;

    /// <summary>Whether the view is maintained from the plans its statement runs now; true for a view that recomputes.</summary>
    internal bool MaintainsCurrentPlan => _maintained?.IsCurrent ?? true;

    /// <summary>Whether two results have the same columns: names, types and nullability, in order.</summary>
    private static bool SameResult(Schema before, Schema after)
    {
        if (before.FieldsList.Count != after.FieldsList.Count)
        {
            return false;
        }

        for (var i = 0; i < before.FieldsList.Count; i++)
        {
            if (!SameField(before.FieldsList[i], after.FieldsList[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameField(Field before, Field after) =>
        string.Equals(before.Name, after.Name, StringComparison.Ordinal)
        && before.IsNullable == after.IsNullable
        && SameType(before.DataType, after.DataType);

    private static bool SameType(Apache.Arrow.Types.IArrowType before, Apache.Arrow.Types.IArrowType after) =>
        (before, after) switch
        {
            (Apache.Arrow.Types.Decimal128Type a, Apache.Arrow.Types.Decimal128Type b) =>
                a.Precision == b.Precision && a.Scale == b.Scale,
            (Apache.Arrow.Types.TimestampType a, Apache.Arrow.Types.TimestampType b) =>
                a.Unit == b.Unit && string.Equals(a.Timezone, b.Timezone, StringComparison.Ordinal),
            (Apache.Arrow.Types.Time64Type a, Apache.Arrow.Types.Time64Type b) => a.Unit == b.Unit,
            (Apache.Arrow.Types.FixedSizeBinaryType a, Apache.Arrow.Types.FixedSizeBinaryType b) =>
                a.ByteWidth == b.ByteWidth,
            (Apache.Arrow.Types.NestedType a, Apache.Arrow.Types.NestedType b) =>
                a.TypeId == b.TypeId
                && a.Fields.Count == b.Fields.Count
                && a.Fields.Zip(b.Fields).All(pair => SameField(pair.First, pair.Second)),
            _ => before.TypeId == after.TypeId,
        };

    /// <summary>The key columns' positions in the result, refusing a name it does not have.</summary>
    private static int[] KeyOf(PreparedQuery prepared, IReadOnlyList<string> key)
    {
        var positions = new int[key.Count];
        for (var i = 0; i < key.Count; i++)
        {
            positions[i] = ViewColumns.Ordinal(prepared.OutputSchema, key[i]);
            if (positions[i] < 0)
            {
                throw new ArgumentException(
                    $"the view's key names '{key[i]}', which is not a column of the result. Its columns are: "
                    + string.Join(", ", prepared.OutputSchema.FieldsList.Select(f => f.Name)) + ".",
                    nameof(key));
            }
        }

        return positions;
    }

    /// <summary>
    /// One update's mapped rows, before they are the view's. The view keeps one between updates and
    /// empties it for each (F244): its maps and arrays keep their room, so an update no larger than the
    /// last stages its rows without allocating. Room an update much larger than the next grew is let go,
    /// so a whole result is not kept twice beside the view's own rows.
    /// </summary>
    private sealed class Staged
    {
        /// <summary>Room kept whatever the updates: what an update of this many keys stages into without growing.</summary>
        private const int Kept = 256;

        private const int Smallest = 16;

        private readonly Dictionary<TKey, int> _seen = new(Bytes<TKey>.Comparer);

        /// <summary>
        /// Whether the rows are a maintained update's changes (D343): the rows changed keys hold now,
        /// with <see cref="Before"/> the host keys of the rows they held. Otherwise the whole result.
        /// </summary>
        public bool Delta { get; private set; }

        public List<TKey> Before { get; } = [];

        /// <summary>The host keys a maintained update takes out, as <c>ApplyChanges</c> works them out.</summary>
        public HashSet<TKey> Freed { get; } = new(Bytes<TKey>.Comparer);

        public TKey[] Keys = new TKey[Smallest];
        public TRow[] Rows = new TRow[Smallest];
        public uint[] Hashes = new uint[Smallest];

        /// <summary>The columns of the batch being mapped; empty between batches.</summary>
        private IArrowArray[] _columns = [];

        public int Count { get; private set; }

        /// <summary>Empties the staging for an update, keeping its room.</summary>
        public Staged Begin(bool delta)
        {
            _seen.Clear();
            Before.Clear();
            Freed.Clear();
            Count = 0;
            Delta = delta;
            Commit = -1;
            return this;
        }

        /// <summary>How many refreshes the engine had committed when the staging pinned its tables (design 78 §1), or -1.</summary>
        public long Commit { get; set; } = -1;

        /// <summary>
        /// Lets go of room beyond four times what this update used, past <see cref="Kept"/>: a whole
        /// result, or a burst of changes, is not kept for the updates after it.
        /// </summary>
        public void Trim()
        {
            if (Keys.Length > Math.Max(Kept, 4 * Count))
            {
                var room = Math.Max(Smallest, 2 * Count);
                Keys = new TKey[room];
                Rows = new TRow[room];
                Hashes = new uint[room];
            }

            if (_seen.EnsureCapacity(0) > Math.Max(Kept, 4 * Count))
            {
                _seen.Clear();
                _seen.TrimExcess(Math.Max(Smallest, 2 * Count));
            }

            var before = Before.Count;
            if (Before.Capacity > Math.Max(Kept, 4 * before))
            {
                Before.Clear();
                Before.Capacity = Math.Max(Smallest, 2 * before);
            }

            var freed = Freed.Count;
            if (Freed.EnsureCapacity(0) > Math.Max(Kept, 4 * freed))
            {
                Freed.Clear();
                Freed.TrimExcess();
                Freed.EnsureCapacity(Math.Max(Smallest, 2 * freed));
            }
        }

        /// <summary>The columns of <paramref name="batch"/>, in an array kept between batches.</summary>
        public IArrowArray[] ColumnsOf(RecordBatch batch)
        {
            if (_columns.Length != batch.ColumnCount)
            {
                _columns = new IArrowArray[batch.ColumnCount];
            }

            for (var c = 0; c < _columns.Length; c++)
            {
                _columns[c] = batch.Column(c);
            }

            return _columns;
        }

        /// <summary>Lets go of the mapped batch's columns, so the staging holds none of its buffers.</summary>
        public void LetGoOfColumns() => System.Array.Clear(_columns);

        /// <summary>False, and the row the key came in, when the key is already staged.</summary>
        public bool Add(in TKey key, in TRow row, uint hash, out int earlier)
        {
            if (!_seen.TryAdd(key, Count))
            {
                earlier = _seen[key];
                return false;
            }

            if (Count == Keys.Length)
            {
                System.Array.Resize(ref Keys, Count * 2);
                System.Array.Resize(ref Rows, Count * 2);
                System.Array.Resize(ref Hashes, Count * 2);
            }

            Keys[Count] = key;
            Rows[Count] = row;
            Hashes[Count] = hash;
            Count++;
            earlier = -1;
            return true;
        }
    }
}

/// <summary>
/// A view's parameter values, bound when it is made and bound again by a rebind (design 77 §5), and the
/// clock it reads in place of one (D339).
/// </summary>
internal sealed class ViewBinding
{
    /// <summary>The named values: replaced whole by a rebind, so that a build beside reads one binding or the other.</summary>
    private volatile IReadOnlyDictionary<string, object?>? _named;

    private readonly IReadOnlyList<object?>? _positional;

    /// <summary>The parameters a rebind may move, by <see cref="Name"/>, each with its list's capacity or none.</summary>
    private Dictionary<string, Rebindable> _rebindable = new(StringComparer.Ordinal);

    /// <summary>A parameter a rebind may move: its name as the statement writes it, and what it takes.</summary>
    /// <param name="Shown">The parameter as the statement writes it, <c>@name</c>, for a refusal.</param>
    /// <param name="Capacity">The most elements a list bound to it holds, padded to this many; or null.</param>
    /// <param name="Length">For a list with no capacity, the length it was bound with, which a rebind keeps; or null.</param>
    private sealed record Rebindable(string Shown, int? Capacity, int? Length);

    /// <summary>The statement <see cref="_bound"/> holds the values of, for the view's updates.</summary>
    private PreparedQuery? _boundFor;

    private object?[] _bound = [];

    /// <summary>The slot of <see cref="_bound"/> the clock's time is written to, or -1.</summary>
    private int _clockAt = -1;

    private ViewBinding(IReadOnlyDictionary<string, object?>? named, IReadOnlyList<object?>? positional)
    {
        _named = named;
        _positional = positional;
    }

    /// <summary>
    /// The values a view binds: a dictionary or an object whose properties name <c>@name</c>
    /// parameters, a list for <c>?</c> or <c>$n</c> ones, or none.
    /// </summary>
    public static ViewBinding Of(object? parameters) => parameters switch
    {
        null => new(null, null),
        IReadOnlyDictionary<string, object?> named => new(named, null),
        System.Collections.IDictionary map => new(
            map.Keys.Cast<object>().ToDictionary(
                k => Convert.ToString(k, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                k => map[k],
                StringComparer.OrdinalIgnoreCase),
            null),
        IReadOnlyList<object?> positional => new(null, positional),
        var values => new(Properties(values), null),
    };

    /// <summary>An object's public readable properties, by name, as Dapper reads them.</summary>
    private static Dictionary<string, object?> Properties(object values) =>
        values.GetType()
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToDictionary(p => p.Name, p => p.GetValue(values), StringComparer.OrdinalIgnoreCase);

    /// <summary>The values given by name, or null for values given by position.</summary>
    public IReadOnlyDictionary<string, object?>? Named => _named;

    /// <summary>Whether any parameter may be bound again.</summary>
    public bool AnyRebindable => _rebindable.Count > 0;

    /// <summary>
    /// Declares the parameters a rebind may move (design 77 §5), refusing a declaration that cannot hold:
    /// a statement whose parameters have no names, a name it does not have or the clock's, a name given
    /// twice, a capacity on a parameter that takes no list or below the list given, or an empty list for
    /// a capacity.
    /// </summary>
    public void Declare(PreparedQuery prepared, IReadOnlyList<RebindableParameter>? declared, string? clock)
    {
        if (declared is not { Count: > 0 })
        {
            return;
        }

        if (prepared.ParameterStyle is not ParameterStyle.Named)
        {
            throw new ArgumentException(
                "ViewOptions.Rebindable names the parameters a rebind may move, and the statement's parameters "
                + (prepared.ParameterStyle is ParameterStyle.None ? "are none" : "have no names")
                + ": write them as @name.",
                "options");
        }

        var rebindable = new Dictionary<string, Rebindable>(StringComparer.Ordinal);
        foreach (var parameter in declared)
        {
            ArgumentNullException.ThrowIfNull(parameter, "options");
            var name = Name(parameter.Name ?? string.Empty);
            if (clock is not null && name == Name(clock))
            {
                throw new ArgumentException(
                    $"ViewOptions.Rebindable names @{clock}, and in a view @{clock} is the engine's clock: it moves "
                    + "with the clock, and takes no value.",
                    "options");
            }

            var found = prepared.Parameters.FirstOrDefault(p => p.Name is { } n && Name(n) == name);
            if (found is null)
            {
                throw new ArgumentException(
                    $"ViewOptions.Rebindable names @{parameter.Name?.TrimStart('@')}, and the statement has no such "
                    + $"parameter: it has {string.Join(", ", prepared.Parameters.Select(p => p.ToString()))}.",
                    "options");
            }

            if (rebindable.ContainsKey(name))
            {
                throw new ArgumentException($"ViewOptions.Rebindable names {found} twice.", "options");
            }

            var value = Value(name);
            int? length = ParameterBinder.IsList(value) ? Count(value) : null;
            if (parameter.Capacity is { } capacity)
            {
                if (!found.AcceptsList)
                {
                    throw new ArgumentException(
                        $"ViewOptions.Rebindable gives {found} a capacity, and it takes no list: only a parameter "
                        + "whose every occurrence follows IN or NOT IN does.",
                        "options");
                }

                if (capacity < 1)
                {
                    throw new ArgumentOutOfRangeException(
                        "options", capacity, $"the capacity of {found} is a number of elements, one or more.");
                }

                RefuseLength(found.ToString(), length ?? 1, capacity, "the view is made with");
                length = null;
            }

            rebindable[name] = new Rebindable(found.ToString(), parameter.Capacity, length);
        }

        _rebindable = rebindable;
        _boundFor = null;
    }

    /// <summary>
    /// Binds <paramref name="values"/> in place of the values their names held, refusing, before anything
    /// moves, a name that is not rebindable, a list where none was or none where one was, and a list of
    /// another length than the one a list with no capacity was bound with, or beyond its capacity. Returns
    /// the values held before, which <see cref="Restore"/> puts back.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Rebind(IReadOnlyDictionary<string, object?> values)
    {
        var next = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in _named ?? new Dictionary<string, object?>())
        {
            next[Name(name)] = value;
        }

        foreach (var (given, value) in values)
        {
            var name = Name(given);
            if (!_rebindable.TryGetValue(name, out var rebindable))
            {
                throw new ArgumentException(
                    _rebindable.Count == 0
                        ? "the view declares no rebindable parameters: name them in ViewOptions.Rebindable."
                        : $"@{given.TrimStart('@')} is not rebindable: the view may bind again "
                            + $"{string.Join(", ", _rebindable.Values.Select(r => r.Shown))}.",
                    "parameters");
            }

            var list = ParameterBinder.IsList(value);
            if (rebindable.Capacity is { } capacity)
            {
                RefuseLength(rebindable.Shown, list ? Count(value) : 1, capacity, "a rebind gives");
            }
            else if (rebindable.Length is { } length && (!list || Count(value) != length))
            {
                throw new ArgumentException(
                    $"{rebindable.Shown} was bound to a list of {length}, and a rebind keeps its length: give a list of "
                    + $"{length}, or declare the parameter with a capacity.",
                    "parameters");
            }
            else if (rebindable.Length is null && list)
            {
                throw new ArgumentException(
                    $"{rebindable.Shown} was bound to one value, and a rebind gives it a list: declare it with a capacity.",
                    "parameters");
            }

            next[name] = value;
        }

        var before = _named;
        _named = next;
        _boundFor = null;
        return before;
    }

    /// <summary>Puts back the values <see cref="Rebind"/> replaced, after an update with the new ones failed.</summary>
    public void Restore(IReadOnlyDictionary<string, object?>? before)
    {
        _named = before;
        _boundFor = null;
    }

    /// <summary>
    /// Refuses a list of <paramref name="length"/> for a capacity: one longer than the capacity, which
    /// another plan would be needed for, or an empty one. An empty list cannot be padded with one of its
    /// elements, and padding it with NULL would make <c>NOT IN</c> unknown for every row.
    /// </summary>
    private static void RefuseLength(string shown, int length, int capacity, string what)
    {
        if (length > capacity)
        {
            throw new ArgumentException(
                $"{what} {shown} a list of {length}, beyond its capacity of {capacity}: raise the capacity, or "
                + "hold the list as rows of a table and read it with IN (SELECT …).",
                "parameters");
        }

        if (length == 0)
        {
            throw new ArgumentException(
                $"{what} {shown} an empty list, and a list with a capacity holds one element or more: an empty one "
                + "cannot be padded to the capacity. To match nothing, hold the list as rows of a table and read "
                + "it with IN (SELECT …).",
                "parameters");
        }
    }

    private static int Count(object? list) => ((System.Collections.IEnumerable)list!).Cast<object?>().Count();

    /// <summary>The value bound under <paramref name="name"/>, or null.</summary>
    private object? Value(string name)
    {
        foreach (var (key, value) in _named ?? new Dictionary<string, object?>())
        {
            if (Name(key) == name)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>A list bound to a parameter with a capacity, padded to it by repeating its first element.</summary>
    private static object?[] Padded(object? value, int capacity)
    {
        object?[] elements = ParameterBinder.IsList(value)
            ? [.. ((System.Collections.IEnumerable)value!).Cast<object?>()]
            : [value];
        var padded = new object?[capacity];
        for (var i = 0; i < capacity; i++)
        {
            padded[i] = elements[Math.Min(i, elements.Length - 1)];
        }

        return padded;
    }

    /// <summary>The positions of the rebindable parameters in <paramref name="prepared"/>'s.</summary>
    public int[] RebindableIndexes(PreparedQuery prepared)
    {
        if (_rebindable.Count == 0)
        {
            return [];
        }

        var found = new List<int>();
        for (var i = 0; i < prepared.Parameters.Count; i++)
        {
            if (prepared.Parameters[i].Name is { } name && _rebindable.ContainsKey(Name(name)))
            {
                found.Add(i);
            }
        }

        return [.. found];
    }

    /// <summary>Refuses a value for the clock: in a view, it reads the engine's clock.</summary>
    public void Refuse(string? clock)
    {
        if (clock is not null && _named is not null && _named.Keys.Any(k => Name(k) == Name(clock)))
        {
            throw new ArgumentException(
                $"the view's parameters give @{clock} a value, and in a view @{clock} is the engine's "
                + "clock: it reads the time of the view's epoch. Leave it out of the parameters.",
                "parameters");
        }
    }

    /// <summary>The position of the parameter the statement names the engine's clock by, or -1.</summary>
    public static int ClockIndex(PreparedQuery prepared, string? clock)
    {
        if (clock is null)
        {
            return -1;
        }

        for (var i = 0; i < prepared.Parameters.Count; i++)
        {
            if (prepared.Parameters[i].Name is { } name && Name(name) == Name(clock))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether the statement names the engine's clock as a parameter.</summary>
    public static bool NamesClock(PreparedQuery prepared, string? clock) =>
        clock is not null && prepared.Parameters.Any(p => p.Name is { } name && Name(name) == Name(clock));

    /// <summary>
    /// The values an update executes with, in an array kept between updates (F244): the host's are bound
    /// once per statement, as they stand for the view's life, and only the clock's slot is written again.
    /// Only the view's update calls this, under its lock, and nothing it hands the array to keeps it; a
    /// build beside binds its own with <see cref="Values"/>.
    /// </summary>
    public object?[] ValuesForUpdate(PreparedQuery prepared, string? clock, DateTimeOffset? time)
    {
        if (!ReferenceEquals(_boundFor, prepared))
        {
            _bound = Values(prepared, clock, time);
            _clockAt = prepared.ParameterStyle is ParameterStyle.Named ? ClockIndex(prepared, clock) : -1;
            _boundFor = prepared;
            return _bound;
        }

        if (_clockAt >= 0)
        {
            _bound[_clockAt] = ClockValue(prepared.Parameters[_clockAt], time);
        }

        return _bound;
    }

    /// <summary>The values to execute with: the host's, and the clock's time for its parameter.</summary>
    public object?[] Values(PreparedQuery prepared, string? clock, DateTimeOffset? time)
    {
        var clocked = NamesClock(prepared, clock);

        if (prepared.ParameterStyle is ParameterStyle.None)
        {
            return [];
        }

        if (prepared.ParameterStyle is ParameterStyle.Named)
        {
            if (_positional is { Count: > 0 })
            {
                throw new ArgumentException(
                    "the statement names its parameters, so the view binds them by name, not by position.",
                    "parameters");
            }

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (_named is { } named)
            {
                foreach (var (name, value) in named)
                {
                    var key = Name(name);
                    values[key] = _rebindable.TryGetValue(key, out var rebindable) && rebindable.Capacity is { } capacity
                        ? Padded(value, capacity)
                        : value;
                }
            }

            if (clocked)
            {
                values[Name(clock!)] = ClockValue(prepared.Parameters[ClockIndex(prepared, clock)], time);
            }

            return ParameterBinder.ResolveNamed(prepared.Parameters, values);
        }

        if (_named is { Count: > 0 })
        {
            throw new ArgumentException(
                $"the statement uses {prepared.ParameterStyle} parameters, so the view binds them by "
                + "position, not by name.",
                "parameters");
        }

        return [.. _positional ?? []];
    }

    /// <summary>The clock's time, as the CLR value <paramref name="parameter"/>'s type takes.</summary>
    private static object? ClockValue(Chalk.Client.ParameterDescriptor parameter, DateTimeOffset? time)
    {
        if (time is not { } now)
        {
            return null;
        }

        // Boxed apart on purpose: as one conditional, the DateTime would widen to a DateTimeOffset.
        if (parameter.Type.Kind == Chalk.Ir.TypeKind.TimestampTz)
        {
            return now.ToUniversalTime();
        }

        return DateTime.SpecifyKind(now.UtcDateTime, DateTimeKind.Unspecified);
    }

    private static string Name(string name) => name.TrimStart('@').ToUpperInvariant();
}

/// <summary>Standing views over an engine (D339): <c>engine.MaterializeAsync(sql, options)</c>.</summary>
public static class StandingViews
{
    /// <summary>
    /// The value to pass as <see cref="ViewOptions{TKey, TRow}.LocalJoinMaxRows"/> without a better
    /// one (D340): a million rows, the planner's assumption for a table of unknown size, so one such
    /// table a join is admitted.
    /// </summary>
    public const long DefaultLocalJoinMaxRows = 1_000_000;

    /// <summary>
    /// Prepares <paramref name="sql"/> as a standing view, checks it, and computes it at the engine's
    /// current epoch. Its parameters bind once, from <see cref="ViewOptions{TKey, TRow}.Parameters"/>.
    /// </summary>
    public static ValueTask<StandingView<TKey, TRow>> MaterializeAsync<TKey, TRow>(
        this ChalkEngine engine, string sql, ViewOptions<TKey, TRow> options, CancellationToken ct = default)
        where TKey : unmanaged
        where TRow : unmanaged
    {
        ArgumentNullException.ThrowIfNull(options);
        return StandingView<TKey, TRow>.CreateAsync(engine, sql, ViewBinding.Of(options.Parameters), options, ct);
    }
}

/// <summary>
/// Standing views of statements the host has already prepared (D341, design 66 §1). Beside
/// <see cref="StandingViews"/> rather than in it, so that each <c>MaterializeAsync</c> can take its token
/// optionally: called the same way, <c>engine.MaterializeAsync(prepared, options)</c>.
/// </summary>
public static class PreparedViews
{
    /// <summary>
    /// Makes a standing view of a statement the host has prepared on this engine (D341): the view
    /// runs its plan, so a run of the statement at the view's epoch gives the view's rows bit for bit.
    /// <see cref="ViewOptions{TKey, TRow}.Prepare"/> must be unset. Once the statement is stale, after
    /// a shape change or a tenfold drift in a table's rows (D342), the view prepares its text again
    /// with the statement's own options. The host's statement is then stale too, and one it prepares
    /// in its place on the same catalog runs the view's new plan.
    /// </summary>
    public static ValueTask<StandingView<TKey, TRow>> MaterializeAsync<TKey, TRow>(
        this ChalkEngine engine, PreparedQuery prepared, ViewOptions<TKey, TRow> options, CancellationToken ct = default)
        where TKey : unmanaged
        where TRow : unmanaged
    {
        ArgumentNullException.ThrowIfNull(options);
        return StandingView<TKey, TRow>.CreateAsync(engine, prepared, ViewBinding.Of(options.Parameters), options, ct);
    }
}

/// <summary>
/// The time a view's statement reads as the current one: its epoch's clock, set before each update.
/// Elapsed time and timers are the system's, so an execution still measures how long it took.
/// </summary>
internal sealed class ViewClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;

    public override long GetTimestamp() => System.GetTimestamp();

    public override long TimestampFrequency => System.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
