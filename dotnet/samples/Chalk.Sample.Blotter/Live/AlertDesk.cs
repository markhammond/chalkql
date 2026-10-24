using System.Collections.Concurrent;
using System.Collections.Immutable;
using Chalk.Sample.Blotter.Grid;
using Chalk.Sample.Blotter.Domain;

namespace Chalk.Sample.Blotter.Live;

/// <summary>
/// The alerts of every browser, evaluated by ChalkQL from the streams, not by the page nor by the
/// host's own code: each condition a rule has is an incremental standing view over the host's tables,
/// shared by every rule, in any browser, that watches the same, and a row entering it fires them,
/// however far behind a page's grid has fallen and whether or not a page is open. A browser's alerts
/// wait, in order, until a page of it has sounded them; a browser no page has opened for
/// <see cref="KeepFor"/> is let go, alerts and all.
/// </summary>
public sealed class AlertDesk : IDisposable
{
    /// <summary>How long a browser's alerts are kept, and evaluated, with no page of it open.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    private readonly BlotterHost _host;
    private readonly ConcurrentDictionary<string, AlertSession> _sessions = new(StringComparer.Ordinal);
    private readonly Timer _timer;

    public AlertDesk(BlotterHost host)
    {
        _host = host;
        host.AlertsEntered += OnEntered;
        _timer = new Timer(_ => Tick(DateTime.UtcNow), null, 250, 250);
    }

    /// <summary>The alerts of the browser <paramref name="browser"/>, its own id: kept since its last page, or new.</summary>
    public AlertSession Open(string browser) => _sessions.GetOrAdd(browser, b => new AlertSession(b));

    /// <summary>
    /// What is wrong with a condition, before a browser keeps it: a name the host does not know, or
    /// what ChalkQL says as it prepares the view's statement; null if nothing.
    /// </summary>
    public async Task<string?> CheckAsync(Condition condition, TimeSpan? lagOver, CancellationToken ct)
    {
        if (!condition.Errors.IsEmpty)
        {
            return condition.Errors[0].Message;
        }

        var refused = await _host.Queries.CheckAsync(new AlertViewSpec(condition, lagOver).Sql(), ct).ConfigureAwait(false);

        // Without the planner's correlation id: what a reader needs.
        var at = refused?.IndexOf(" [correlation id", StringComparison.Ordinal) ?? -1;
        return at < 0 ? refused : refused![..at];
    }

    /// <summary>What ChalkQL said when it would not make the rule's view; null while it runs.</summary>
    public string? Refusal(AlertRule rule) => _host.AlertsRefused.GetValueOrDefault(AlertViewSpec.Of(rule).Name);

    public void Dispose()
    {
        _host.AlertsEntered -= OnEntered;
        _timer.Dispose();
    }

    private void OnEntered(AlertViewSpec view, IReadOnlyList<(IRow Row, TimeSpan? Lag)> rows)
    {
        var now = DateTime.UtcNow;
        foreach (var session in _sessions.Values)
        {
            session.Enter(view.Name, rows, now);
        }
    }

    private void Tick(DateTime now)
    {
        _host.WatchAlerts([.. _sessions.Values.SelectMany(s => s.Rules).Select(AlertViewSpec.Of)]);
        foreach (var (browser, session) in _sessions)
        {
            session.Flush(now);
            if (session.Unattended(now) > KeepFor)
            {
                _sessions.TryRemove(browser, out _);
            }
        }
    }
}
