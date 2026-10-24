using System.Globalization;
using System.Text;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sample.Blotter.Grid;

namespace Chalk.Sample.Blotter.Live;

/// <summary>
/// What one alert view watches: a condition, and either what arrives or how long a row waits for
/// the database. Rules that watch the same, in any browser, share one view.
/// </summary>
public sealed record AlertViewSpec(Condition Condition, TimeSpan? LagOver)
{
    /// <summary>What the host shares a view by.</summary>
    public string Name => Condition.Tab.Name + "\n" + Condition.Text + (LagOver is { } lag ? "\nlag " + lag.TotalSeconds.ToString(CultureInfo.InvariantCulture) : "");

    public static AlertViewSpec Of(AlertRule rule) => new(rule.Condition, rule.LagOver);

    /// <summary>
    /// The view's statement, over the host's tables only. On arrival, a fill is read from
    /// <c>live.arrived_fills</c>, an order from <c>live.latest_orders</c>, each order once at the newest
    /// version the streams sent. On the wait, a row is read from the tail, beside its wait in
    /// <c>live.waits</c>, and passes once it has waited longer than the rule allows by the engine's
    /// clock: for good, if the database held it only after that. A path joins
    /// <c>live.latest_orders</c>, so a fill whose order has not arrived yet enters the view when it does.
    /// </summary>
    public string Sql()
    {
        var tab = Condition.Tab;
        var table = (tab.Overrides, LagOver is null) switch
        {
            (true, true) => "live.latest_orders",
            (true, false) => "live.tail",
            (false, true) => "live.arrived_fills",
            (false, false) => "live.tail_fills",
        };

        var sql = new StringBuilder($"SELECT a.{tab.Key.Name} AS id");
        sql.Append(LagOver is null ? ", TIMESTAMP '2000-01-01 00:00:00' AS since" : ", w.since AS since");
        sql.Append($"\nFROM {table} AS a");
        if (LagOver is not null)
        {
            sql.Append($"\nJOIN live.waits AS w ON w.id = a.{tab.Key.Name}");
        }

        foreach (var path in Condition.Paths)
        {
            if (!path.Target.Overrides)
            {
                throw new NotSupportedException($"a path to {path.Target.Title} reads the orders the streams sent, which {path.Target.Title} are not");
            }

            sql.Append($"\nLEFT JOIN live.latest_orders AS p_{path.Name} ON p_{path.Name}.{path.Target.Key.Name} = a.{path.Via}");
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

/// <summary>A row in an alert view: its key, and, for a wait, when the wait began.</summary>
public struct AlertKey : IEquatable<AlertKey>
{
    public Guid Id;
    public DateTime Since;

    public readonly bool Equals(AlertKey other) => Id == other.Id && Since == other.Since;

    public override readonly bool Equals(object? obj) => obj is AlertKey other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Id, Since);
}

/// <summary>
/// One alert view: a ChalkQL standing view, maintained incrementally, of the rows that pass a
/// condition now. A row that enters it fires the rules that share it; one already in it, changing,
/// does not; one that leaves it may fire them again when it comes back. What it held when it was
/// made is taken as it stands: a rule starts from now.
/// </summary>
public sealed class AlertView : IAsyncDisposable, IViewSink<AlertKey, AlertKey>
{
    private readonly StandingView<AlertKey, AlertKey> _view;
    private readonly HashSet<AlertKey> _present = [];
    private readonly List<AlertKey> _entered = [];
    private ViewCursor _cursor = ViewCursor.Start;
    private bool _started;

    private AlertView(AlertViewSpec spec, StandingView<AlertKey, AlertKey> view)
    {
        Spec = spec;
        _view = view;
    }

    public AlertViewSpec Spec { get; }

    /// <summary>The view's state, in bytes, by its own count.</summary>
    public long Bytes => _view.StateBytes;

    internal static async Task<AlertView> CreateAsync(ChalkEngine engine, AlertViewSpec spec, long budget, CancellationToken ct)
    {
        var view = await engine.MaterializeAsync(spec.Sql(), new ViewOptions<AlertKey, AlertKey>
        {
            Key = ["id", "since"],
            LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
            Map = Map,
            StateBudget = budget,
            Maintenance = ViewMaintenance.Incremental,

            // Drained after every update: never far enough behind for a reset to be cheaper.
            ResetWhen = double.PositiveInfinity,
        }, ct).ConfigureAwait(false);
        var made = new AlertView(spec, view);
        await made.UpdateAsync(ct).ConfigureAwait(false);
        made._started = true;
        return made;
    }

    /// <summary>Brings the view to the engine's latest epoch, and says which rows entered it.</summary>
    internal async Task<IReadOnlyList<AlertKey>> UpdateAsync(CancellationToken ct)
    {
        _entered.Clear();
        await _view.UpdateAsync(ct).ConfigureAwait(false);
        while (_view.Drain(ref _cursor, this, 65536) == DrainOutcome.Partial)
        {
        }

        return [.. _entered];
    }

    public ValueTask DisposeAsync() => _view.DisposeAsync();

    void IViewSink<AlertKey, AlertKey>.Reset() => _present.Clear();

    void IViewSink<AlertKey, AlertKey>.Upsert(in AlertKey key, in AlertKey row)
    {
        if (_present.Add(key) && _started)
        {
            _entered.Add(key);
        }
    }

    void IViewSink<AlertKey, AlertKey>.Delete(in AlertKey key) => _present.Remove(key);

    void IViewSink<AlertKey, AlertKey>.Commit(long version, uint checksum)
    {
    }

    private static void Map(in ViewRow row, out AlertKey key, out AlertKey value)
    {
        key = new AlertKey { Id = (Guid)row.GetValue(0)!, Since = (DateTime)row.GetValue(1)! };
        value = key;
    }
}
