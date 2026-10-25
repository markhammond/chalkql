using System.Globalization;
using System.Text;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sample.Blotter.Alerts;

namespace Chalk.Sample.Blotter.Live;

/// <summary>
///     What one alert view watches: a condition, and either what arrives or how long a row waits for
///     the database. Rules that watch the same, in any browser, share one view.
/// </summary>
public sealed record AlertViewSpec(Condition Condition, TimeSpan? LagOver)
{
    /// <summary>What the host shares a view by.</summary>
    public string Name => Condition.Tab.Name + "\n" + Condition.Text +
                          (LagOver is { } lag
                              ? "\nlag " + lag.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                              : "");

    public static AlertViewSpec Of(AlertRule rule)
    {
        return new AlertViewSpec(rule.Condition, rule.LagOver);
    }

    /// <summary>
    ///     The view's statement, over the host's tables only. On arrival, a fill is read from
    ///     <c>live.arrived_fills</c>, an order from <c>live.latest_orders</c>, each order once at the newest
    ///     version the streams sent. On the wait, a row is read from the tail, beside its wait in
    ///     <c>live.waits</c>, and passes once it has waited longer than the rule allows by the engine's
    ///     clock: for good, if the database held it only after that. A path joins
    ///     <c>live.latest_orders</c>, so a fill whose order has not arrived yet enters the view when it does.
    /// </summary>
    public string Sql()
    {
        var tab = Condition.Tab;
        var table = (tab.Overrides, LagOver is null) switch
        {
            (true, true) => "live.latest_orders",
            (true, false) => "live.tail",
            (false, true) => "live.arrived_fills",
            (false, false) => "live.tail_fills"
        };

        var sql = new StringBuilder($"SELECT a.{tab.Key.Name} AS id");
        sql.Append(LagOver is null ? ", TIMESTAMP '2000-01-01 00:00:00' AS since" : ", w.since AS since");

        // An order's version, so that each newer version that passes is a match a count sees.
        sql.Append(tab.Overrides && LagOver is null ? ", a.version AS version" : ", CAST(0 AS BIGINT) AS version");
        sql.Append($"\nFROM {table} AS a");
        if (LagOver is not null) sql.Append($"\nJOIN live.waits AS w ON w.id = a.{tab.Key.Name}");

        foreach (var path in Condition.Paths)
        {
            if (!path.Target.Overrides)
                throw new NotSupportedException(
                    $"a path to {path.Target.Title} reads the orders the streams sent, which {path.Target.Title} are not");

            sql.Append(
                $"\nLEFT JOIN live.latest_orders AS p_{path.Name} ON p_{path.Name}.{path.Target.Key.Name} = a.{path.Via}");
        }

        sql.Append("\nWHERE (").Append(Condition.Sql(c => "a." + c.Name, (p, c) => $"p_{p.Name}.{c.Name}")).Append(')');
        if (LagOver is { } over)
        {
            // Waited longer than allowed: by the clock, or, once the database held it, by when it did.
            var interval = string.Create(CultureInfo.InvariantCulture, $"INTERVAL '{over.TotalSeconds:0}' SECOND");
            sql.Append($"\n  AND w.since < @now - {interval} AND w.since < w.until - {interval}");
        }

        return sql.ToString();
    }
}

/// <summary>
///     A row in an alert view: its key, and, for a wait, when the wait began; and an order's version, which is no
///     part of the key.
/// </summary>
public struct AlertKey : IEquatable<AlertKey>
{
    public Guid Id;
    public DateTime Since;
    public long Version;

    public readonly bool Equals(AlertKey other)
    {
        return Id == other.Id && Since == other.Since;
    }

    public readonly override bool Equals(object? obj)
    {
        return obj is AlertKey other && Equals(other);
    }

    public readonly override int GetHashCode()
    {
        return HashCode.Combine(Id, Since);
    }
}

/// <summary>
///     What an update of an alert view found: the rows that entered it, and how many matches, entries and newer
///     versions alike.
/// </summary>
public readonly record struct AlertViewUpdate(IReadOnlyList<AlertKey> Entered, int Matches);

/// <summary>
///     One alert view: a ChalkQL standing view, maintained incrementally, of the rows that pass a
///     condition now. A row that enters it fires the rules that share it; one already in it, changing,
///     does not, though a newer version of an order in it is a match for the rules that count; one that
///     leaves it may fire them again when it comes back. What it held when it was made is taken as it
///     stands: a rule starts from now.
/// </summary>
public sealed class AlertView : IAsyncDisposable, IViewSink<AlertKey, AlertKey>
{
    private readonly List<AlertKey> _entered = [];
    private readonly Dictionary<AlertKey, long> _present = [];
    private readonly StandingView<AlertKey, AlertKey> _view;
    private ViewCursor _cursor = ViewCursor.Start;
    private int _matches;
    private bool _started;

    private AlertView(AlertViewSpec spec, StandingView<AlertKey, AlertKey> view)
    {
        Spec = spec;
        _view = view;
    }

    public AlertViewSpec Spec { get; }

    /// <summary>The view's state, in bytes, by its own count.</summary>
    public long Bytes => _view.StateBytes;

    public ValueTask DisposeAsync()
    {
        return _view.DisposeAsync();
    }

    void IViewSink<AlertKey, AlertKey>.Reset()
    {
        _present.Clear();
    }

    void IViewSink<AlertKey, AlertKey>.Upsert(in AlertKey key, in AlertKey row)
    {
        var entered = _present.TryAdd(key, row.Version);
        var newer = !entered && row.Version > _present[key];
        if (newer) _present[key] = row.Version;

        if (_started && entered) _entered.Add(key);

        if (_started && (entered || newer)) _matches++;
    }

    void IViewSink<AlertKey, AlertKey>.Delete(in AlertKey key)
    {
        _present.Remove(key);
    }

    void IViewSink<AlertKey, AlertKey>.Commit(long version, uint checksum)
    {
    }

    internal static async Task<AlertView> CreateAsync(ChalkEngine engine, AlertViewSpec spec, long budget,
        CancellationToken ct)
    {
        var view = await engine.MaterializeAsync(spec.Sql(), new ViewOptions<AlertKey, AlertKey>
        {
            Key = ["id", "since"],
            LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
            Map = Map,
            StateBudget = budget,
            Maintenance = ViewMaintenance.Incremental,

            // Drained after every update: never far enough behind for a reset to be cheaper.
            ResetWhen = double.PositiveInfinity
        }, ct).ConfigureAwait(false);
        var made = new AlertView(spec, view);
        await made.UpdateAsync(ct).ConfigureAwait(false);
        made._started = true;
        return made;
    }

    /// <summary>Brings the view to the engine's latest epoch, and says which rows entered it and how many matches it saw.</summary>
    internal async Task<AlertViewUpdate> UpdateAsync(CancellationToken ct)
    {
        _entered.Clear();
        _matches = 0;
        await _view.UpdateAsync(ct).ConfigureAwait(false);
        while (_view.Drain(ref _cursor, this, 65536) == DrainOutcome.Partial)
        {
        }

        return new AlertViewUpdate([.. _entered], _matches);
    }

    private static void Map(in ViewRow row, out AlertKey key, out AlertKey value)
    {
        key = new AlertKey { Id = (Guid)row.GetValue(0)!, Since = (DateTime)row.GetValue(1)! };
        value = key with { Version = Convert.ToInt64(row.GetValue(2), CultureInfo.InvariantCulture) };
    }
}

/// <summary>
///     How many matches an alert view saw in one slice of time, as <c>live.match_counts</c> holds it: by
///     the counter that counts them, one for each view and width of slice.
/// </summary>
public sealed record MatchCount(int Counter, DateTime SliceStart, long Matched);

/// <summary>
///     What one count view watches: the matches of an alert view in a sliding window, at least so many or
///     none. Rules that count the same, in any browser, share one view.
/// </summary>
public sealed record AlertWindowSpec(AlertViewSpec Matcher, AlertCount Count)
{
    /// <summary>What the host shares a view by.</summary>
    public string Name => Matcher.Name + "\n" + Count.Describe();

    /// <summary>
    ///     How finely matches are counted: a sixtieth of the window, to the second at least. The window
    ///     slides a slice at a time, so a match is counted in it up to a slice longer than the window.
    /// </summary>
    public TimeSpan Slice => TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(Count.Window.TotalSeconds / 60)));

    public static AlertWindowSpec? Of(AlertRule rule)
    {
        return rule.Count is { } count ? new AlertWindowSpec(AlertViewSpec.Of(rule), count) : null;
    }

    /// <summary>The name of the view a rule's alerts come from: its count's, if it counts, or its condition's.</summary>
    public static string NameOf(AlertRule rule)
    {
        return Of(rule)?.Name ?? AlertViewSpec.Of(rule).Name;
    }

    /// <summary>
    ///     The view's statement: the matches the counter has counted in the slices that started within the
    ///     window, by the engine's clock, which it holds as a single row while they are enough, or none. No
    ///     count of none is judged before <paramref name="judgedFrom" />: one window after the counter began.
    /// </summary>
    public string Sql(int counter, DateTime judgedFrom)
    {
        var window = string.Create(CultureInfo.InvariantCulture, $"INTERVAL '{Count.Window.TotalSeconds:0}' SECOND");
        var sql = new StringBuilder("SELECT 1 AS id, COALESCE(SUM(c.matched), 0) AS matched");
        sql.Append("\nFROM live.match_counts AS c");
        sql.Append(CultureInfo.InvariantCulture, $"\nWHERE c.counter = {counter} AND c.slice_start > @now - {window}");
        sql.Append(Count.Kind == CountKind.AtLeast
            ? string.Create(CultureInfo.InvariantCulture, $"\nHAVING COALESCE(SUM(c.matched), 0) >= {Count.Matches}")
            : string.Create(CultureInfo.InvariantCulture,
                $"\nHAVING COALESCE(SUM(c.matched), 0) = 0 AND @now >= TIMESTAMP '{judgedFrom:yyyy-MM-dd HH:mm:ss.ffffff}'"));
        return sql.ToString();
    }
}

/// <summary>
///     One count view: a ChalkQL standing view, maintained incrementally, that holds its one row while the
///     matches in its window are enough, or none. Its row entering fires the rules that share it; it
///     fires them again only after it has left. As the clock moves, the window slides with it.
/// </summary>
public sealed class CountView : IAsyncDisposable, IViewSink<int, long>
{
    private readonly StandingView<int, long> _view;
    private ViewCursor _cursor = ViewCursor.Start;
    private long? _entered;
    private bool _present;

    private CountView(AlertWindowSpec spec, int counter, StandingView<int, long> view)
    {
        Spec = spec;
        Counter = counter;
        _view = view;
    }

    public AlertWindowSpec Spec { get; }

    /// <summary>The counter whose counts it reads.</summary>
    public int Counter { get; }

    /// <summary>The view's state, in bytes, by its own count.</summary>
    public long Bytes => _view.StateBytes;

    public ValueTask DisposeAsync()
    {
        return _view.DisposeAsync();
    }

    void IViewSink<int, long>.Reset()
    {
        _present = false;
    }

    void IViewSink<int, long>.Upsert(in int key, in long matches)
    {
        if (!_present)
        {
            _present = true;
            _entered = matches;
        }
    }

    void IViewSink<int, long>.Delete(in int key)
    {
        _present = false;
    }

    void IViewSink<int, long>.Commit(long version, uint checksum)
    {
    }

    internal static async Task<CountView> CreateAsync(
        ChalkEngine engine, AlertWindowSpec spec, int counter, DateTime judgedFrom, long budget, CancellationToken ct)
    {
        var view = await engine.MaterializeAsync(spec.Sql(counter, judgedFrom), new ViewOptions<int, long>
        {
            Key = ["id"],
            LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
            Map = Map,
            StateBudget = budget,
            Maintenance = ViewMaintenance.Incremental,
            ResetWhen = double.PositiveInfinity
        }, ct).ConfigureAwait(false);
        var made = new CountView(spec, counter, view);
        await made.UpdateAsync(ct).ConfigureAwait(false);
        return made;
    }

    /// <summary>Brings the view to the engine's latest epoch: the matches in the window, if its row entered it.</summary>
    internal async Task<long?> UpdateAsync(CancellationToken ct)
    {
        _entered = null;
        await _view.UpdateAsync(ct).ConfigureAwait(false);
        while (_view.Drain(ref _cursor, this, 1024) == DrainOutcome.Partial)
        {
        }

        return _entered;
    }

    private static void Map(in ViewRow row, out int key, out long matches)
    {
        key = Convert.ToInt32(row.GetValue(0), CultureInfo.InvariantCulture);
        matches = Convert.ToInt64(row.GetValue(1), CultureInfo.InvariantCulture);
    }
}