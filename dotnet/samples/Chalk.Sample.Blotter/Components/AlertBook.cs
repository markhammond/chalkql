using System.Collections.Concurrent;
using System.Collections.Immutable;
using Chalk.Sample.Blotter.Alerts;
using Chalk.Sample.Blotter.Live;

namespace Chalk.Sample.Blotter.Components;

/// <summary>
///     A page's alerts: its browser's rules and alerts, which the host keeps and evaluates; whether this
///     page may sound them, and notify; and how many have fired on each tab, for the bell.
/// </summary>
public sealed class AlertBook
{
    private readonly ConcurrentDictionary<string, long> _fired = new(StringComparer.Ordinal);

    public AlertSession? Session { get; set; }

    public ImmutableArray<AlertRule> Rules => Session?.Rules ?? [];

    /// <summary>Whether the page may make sound: someone has said so, by a click.</summary>
    public bool SoundOn { get; set; }

    /// <summary>Whether the splash that asks for sound has been answered, either way.</summary>
    public bool Answered { get; set; }

    /// <summary>Whether to notify through the system, when the page is in the background.</summary>
    public bool Notify { get; set; }

    /// <summary>Keeps the rules: with the host, and in the browser's local settings.</summary>
    public Func<IReadOnlyList<AlertRule>, Task> SaveAsync { get; set; } = _ => Task.CompletedTask;

    /// <summary>Whether an alert has fired since the alerts were last looked at, for the red dot on the bell.</summary>
    public bool Unseen { get; private set; }

    /// <summary>Whether the alerts panel is open, where an alert is seen as it fires.</summary>
    public bool Looking { get; set; }

    /// <summary>How many rows have fired alerts on <paramref name="tab" /> since the page opened.</summary>
    public long FiredOn(Tab tab)
    {
        return _fired.GetValueOrDefault(tab.Name);
    }

    public void Count(AlertEvent alert)
    {
        _fired.AddOrUpdate(alert.Tab, alert.Count, (_, n) => n + alert.Count);
        Unseen |= !Looking;
    }

    /// <summary>The alerts were looked at: the bell's dot goes.</summary>
    public void Seen()
    {
        Unseen = false;
    }
}