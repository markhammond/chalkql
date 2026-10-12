using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Client;

namespace Chalk.Incremental;

/// <summary>
/// A statement kept current as one row per key (D339, <c>docs/design/64-incremental-views.md</c>
/// §4–§6), read from a cursor as the latest row of each key changed since.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UpdateAsync"/> brings the view to the engine's latest epoch, and is the only thing
/// that computes: as of writing it runs the statement in full and compares the result with the rows
/// the view holds, so only the delivery is incremental. <see cref="Drain"/> copies what changed into
/// a sink, and never computes. A view computes only when asked, so epochs nobody reads cost nothing.
/// </para>
/// <para>
/// An update reads one epoch whole: should a refresh land while it runs, it runs again, and holds
/// refreshes off on its third attempt. Updates are taken one at a time; drains may run beside them
/// and see each update whole or not at all.
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
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _updating = new(1, 1);
    private readonly KeyedTable<TKey, TRow> _table = new();
    private readonly ViewClock _clock = new();

    /// <summary>What the view keeps of its reads of databases (D340); null without the opt-in.</summary>
    private readonly ViewReads? _reads;

    private PreparedQuery _prepared;
    private int[] _key;
    private bool _readsTime;
    private long _epoch;
    private long _version;
    private long _generation = 1;
    private long _updates;
    private Exception? _fault;
    private bool _disposed;

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
        _reads = options.DatabaseReads == ViewDatabaseReads.WhenRefreshed ? new ViewReads(engine) : null;
    }

    /// <summary>The result's key columns.</summary>
    public IReadOnlyList<string> Key => _options.Key;

    /// <summary>The result's columns, as a <see cref="ViewRow"/> reads them.</summary>
    public Schema Columns => Volatile.Read(ref _prepared).OutputSchema;

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
    /// rows, and what it keeps of its reads of databases (D340).
    /// </summary>
    public long StateBytes
    {
        get
        {
            lock (_gate)
            {
                return _table.Bytes + (_reads?.Bytes ?? 0);
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
        var view = new StandingView<TKey, TRow>(engine, sql, prepare, options, binding, prepared, key);
        try
        {
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
    public async ValueTask<long> UpdateAsync(CancellationToken ct = default)
    {
        Usable();
        await _updating.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Usable();
            for (var attempt = 0; ; attempt++)
            {
                var changes = _engine.Changes;
                if ((changes & 1) == 1)
                {
                    await _engine.WhenQuietAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (_engine.Stamp.Epoch == _epoch)
                {
                    _reads?.Abandon();
                    return _version;
                }

                if (attempt >= 2)
                {
                    return await _engine.WithoutRefreshAsync(
                        async () =>
                        {
                            var stamp = _engine.Stamp;
                            if (stamp.Epoch != _epoch)
                            {
                                Apply(await StageAsync(stamp, ct).ConfigureAwait(false), stamp);
                            }

                            return _version;
                        },
                        ct).ConfigureAwait(false);
                }

                var at = _engine.Stamp;
                var staged = await StageAsync(at, ct).ConfigureAwait(false);
                if (_engine.Changes != changes)
                {
                    continue;
                }

                Apply(staged, at);
                return _version;
            }
        }
        catch
        {
            // A failed update keeps nothing new: the view keeps the epoch, and the reads, it held.
            _reads?.Abandon();
            throw;
        }
        finally
        {
            _updating.Release();
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

            if (sent > 0 || reset || cursor.ResetFrom > 0 || cursor.Committed != _version)
            {
                sink.Commit(_version, _table.Checksum);
                cursor.Committed = _version;
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
            _reads?.Dispose();
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
    private async ValueTask<Staged> StageAsync(EpochStamp stamp, CancellationToken ct)
    {
        await PreparedCurrentAsync(ct).ConfigureAwait(false);
        var prepared = _prepared;

        if ((_readsTime || ViewBinding.NamesClock(prepared, _engine.ClockName)) && stamp.Clock is null)
        {
            throw new StandingViewException(
                ChalkErrorCodes.ClockRequired,
                $"The view reads the current time, and the engine's clock, '{_engine.ClockName}', has not "
                + $"been set as of version {stamp.Version}. Advance it in a refresh with AdvanceClock "
                + "before the view is updated.");
        }

        var values = _binding.Values(prepared, _engine.ClockName, stamp.Clock);
        await using var execution = await prepared.ExecuteAsync(values, context: null, arena: null, ct).ConfigureAwait(false);
        if (_readsTime)
        {
            _clock.Now = stamp.Clock!.Value;
            execution.UseClock(_clock);
        }

        if (_reads is not null)
        {
            // What the update keeps of its databases' rows shares the budget with the view's rows.
            _reads.Begin(stamp, _options.StateBudget - StateRowBytes());
            execution.UseReads(_reads);
        }

        var staged = new Staged();
        await foreach (var batch in execution.Batches.WithCancellation(ct).ConfigureAwait(false))
        {
            using (batch)
            {
                Map(batch, staged, stamp);
            }
        }

        return staged;
    }

    private void Map(RecordBatch batch, Staged staged, EpochStamp stamp)
    {
        var columns = new IArrowArray[batch.ColumnCount];
        for (var c = 0; c < columns.Length; c++)
        {
            columns[c] = batch.Column(c);
        }

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

    /// <summary>Makes the staged rows the view's, whole, under the lock drains take.</summary>
    private void Apply(Staged staged, EpochStamp stamp)
    {
        var added = 0L;
        for (var i = 0; i < staged.Count; i++)
        {
            if (!_table.Contains(in staged.Keys[i]))
            {
                added++;
            }
        }

        var kept = _reads?.BytesKept ?? 0;
        var bytes = _table.BytesWith(added) + kept;
        if (bytes > _options.StateBudget)
        {
            throw new StandingViewException(
                ChalkErrorCodes.MemoryBudgetExceeded,
                $"The view would hold about {bytes} bytes at version {stamp.Version}"
                + (kept > 0 ? $", {kept} of them kept from its databases," : string.Empty)
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
    }

    /// <summary>
    /// Prepares the statement anew when the catalog's shape has changed since it was prepared: the
    /// view's readers then reset. A statement that no longer prepares faults the view.
    /// </summary>
    private async ValueTask PreparedCurrentAsync(CancellationToken ct)
    {
        if (!_prepared.IsStale)
        {
            return;
        }

        try
        {
            var prepared = await _engine.PrepareAsync(_sql, _prepare, ct).ConfigureAwait(false);
            var key = KeyOf(prepared, _options.Key);
            ViewChecks.Check(_engine, prepared, key, _options);
            lock (_gate)
            {
                Volatile.Write(ref _prepared, prepared);
                _key = key;
                _readsTime = ViewPlan.ReadsCurrentTime(prepared.Plan);
                _generation++;
            }

            // The plan is a new one, and its reads are in new places: read them all again.
            _reads?.Clear();
        }
        catch (Exception failure) when (failure is ChalkException or ArgumentException)
        {
            _fault = failure;
            throw;
        }
    }

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

    /// <summary>One update's mapped rows, before they are the view's.</summary>
    private sealed class Staged
    {
        private readonly Dictionary<TKey, int> _seen = new(Bytes<TKey>.Comparer);

        public TKey[] Keys = new TKey[16];
        public TRow[] Rows = new TRow[16];
        public uint[] Hashes = new uint[16];

        public int Count { get; private set; }

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

/// <summary>A view's parameter values, bound once, and the clock it reads in place of one (D339).</summary>
internal sealed class ViewBinding
{
    private readonly IReadOnlyDictionary<string, object?>? _named;
    private readonly IReadOnlyList<object?>? _positional;

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

    /// <summary>Whether the statement names the engine's clock as a parameter.</summary>
    public static bool NamesClock(PreparedQuery prepared, string? clock) =>
        clock is not null && prepared.Parameters.Any(p => p.Name is { } name && Name(name) == Name(clock));

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
            if (_named is not null)
            {
                foreach (var (name, value) in _named)
                {
                    values[Name(name)] = value;
                }
            }

            if (clocked)
            {
                values[Name(clock!)] = ClockValue(prepared, clock!, time);
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

    /// <summary>The clock's time, as the CLR value the parameter's type takes.</summary>
    private static object? ClockValue(PreparedQuery prepared, string clock, DateTimeOffset? time)
    {
        if (time is not { } now)
        {
            return null;
        }

        var parameter = prepared.Parameters.First(p => p.Name is { } name && Name(name) == Name(clock));
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
    /// <see cref="ViewOptions{TKey, TRow}.Prepare"/> must be unset; after a shape change the view
    /// prepares the statement's text again with the statement's own options.
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
