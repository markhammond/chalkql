using System.Collections.Concurrent;
using System.Collections.Immutable;
using Chalk.Sample.Blotter.Grid;
using Chalk.Sample.Blotter.Domain;

namespace Chalk.Sample.Blotter.Live;

/// <summary>An alert fired: which rule, how many rows since it last sounded, and the latest of them.</summary>
public sealed record AlertEvent(long Seq, string RuleId, string RuleName, string Sound, bool Muted, string Tab, int Count, string Latest, DateTime At);

/// <summary>
/// The alerts of every browser, evaluated by the host from the streams, not by the page: every order
/// version and fill the streams send is weighed against each rule as the host reads it, however far
/// behind a page's grid has fallen, and whether or not a page is open. A browser's alerts wait, in
/// order, until a page of it has sounded them; a browser no page has opened for
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
        host.Arrived += OnArrived;
        _timer = new Timer(_ => Tick(DateTime.UtcNow), null, 250, 250);
    }

    /// <summary>The alerts of the browser <paramref name="browser"/>, its own id: kept since its last page, or new.</summary>
    public AlertSession Open(string browser) => _sessions.GetOrAdd(browser, b => new AlertSession(b));

    public void Dispose()
    {
        _host.Arrived -= OnArrived;
        _timer.Dispose();
    }

    private void OnArrived(IReadOnlyList<Order> versions, IReadOnlyList<Fill> fills)
    {
        var now = DateTime.UtcNow;
        foreach (var session in _sessions.Values)
        {
            session.Note(versions, fills, now);
        }
    }

    private void Tick(DateTime now)
    {
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

/// <summary>
/// One browser's alerts: its rules, what each has fired and not yet sounded, and the alerts no page of
/// it has sounded yet. A rule that fires within its throttle of sounding gathers its rows, and sounds
/// once the throttle allows, saying how many.
/// </summary>
public sealed class AlertSession(string browser)
{
    // Kept for a page to sound; past this many, the oldest go.
    private const int MostWaiting = 1000;

    // Orders a rule has seen pass its filter; past this many, forgotten, so that some may fire again.
    private const int MostMatching = 200_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, RuleState> _states = new(StringComparer.Ordinal);
    private readonly List<AlertEvent> _waiting = [];
    private ImmutableArray<AlertRule> _rules = [];
    private long _seq;
    private int _pages;
    private DateTime _leftAt = DateTime.UtcNow;

    public string Browser => browser;

    public ImmutableArray<AlertRule> Rules => _rules;

    /// <summary>Raised as an alert fires, on whichever thread fires it: a page takes note, and sounds it in its own time.</summary>
    public event Action<AlertEvent>? Fired;

    /// <summary>
    /// Sets the rules. A rule whose filter is unchanged keeps what it has gathered; a new one, or a
    /// changed one, starts from now.
    /// </summary>
    public void SetRules(IEnumerable<AlertRule> rules)
    {
        lock (_gate)
        {
            _rules = [.. rules];
            foreach (var id in _states.Keys.Where(id => !_rules.Any(r => r.Id == id)).ToList())
            {
                _states.Remove(id);
            }

            foreach (var rule in _rules)
            {
                if (!_states.TryGetValue(rule.Id, out var state) || state.Rule.Filter.Name != rule.Filter.Name || state.Rule.Tab != rule.Tab)
                {
                    _states[rule.Id] = new RuleState(rule);
                }
                else
                {
                    state.Rule = rule;
                }
            }
        }
    }

    /// <summary>Weighs what the streams sent against each rule.</summary>
    public void Note(IReadOnlyList<Order> versions, IReadOnlyList<Fill> fills, DateTime now)
    {
        List<AlertEvent> fired = [];
        lock (_gate)
        {
            foreach (var state in _states.Values)
            {
                var rule = state.Rule;
                if (rule.Tab == Tabs.Orders)
                {
                    foreach (var version in versions)
                    {
                        // An order fires as it comes to pass the filter; then not again until it has left it.
                        if (!rule.Matches(version))
                        {
                            state.Matching.Remove(version.OrderId);
                        }
                        else if (state.Matching.Add(version.OrderId))
                        {
                            state.Hit(version);
                        }
                    }

                    if (state.Matching.Count > MostMatching)
                    {
                        state.Matching.Clear();
                    }
                }
                else
                {
                    foreach (var fill in fills.Where(rule.Matches))
                    {
                        state.Hit(fill);
                    }
                }

                if (state.Due(now))
                {
                    fired.Add(Sound(state, now));
                }
            }
        }

        Raise(fired);
    }

    /// <summary>Sounds what each rule has gathered, where its throttle now allows.</summary>
    public void Flush(DateTime now)
    {
        List<AlertEvent> fired = [];
        lock (_gate)
        {
            fired.AddRange(_states.Values.Where(s => s.Due(now)).Select(s => Sound(s, now)).ToList());
        }

        Raise(fired);
    }

    /// <summary>The alerts no page has sounded yet, oldest first.</summary>
    public IReadOnlyList<AlertEvent> Waiting()
    {
        lock (_gate)
        {
            return [.. _waiting];
        }
    }

    /// <summary>A page has sounded every alert up to <paramref name="seq"/>.</summary>
    public void Acknowledge(long seq)
    {
        lock (_gate)
        {
            _waiting.RemoveAll(e => e.Seq <= seq);
        }
    }

    /// <summary>A page of this browser has opened.</summary>
    public void Attach()
    {
        lock (_gate)
        {
            _pages++;
        }
    }

    /// <summary>A page of this browser has closed.</summary>
    public void Detach()
    {
        lock (_gate)
        {
            _pages = Math.Max(0, _pages - 1);
            _leftAt = DateTime.UtcNow;
        }
    }

    /// <summary>How long no page of this browser has been open; none while one is.</summary>
    public TimeSpan Unattended(DateTime now)
    {
        lock (_gate)
        {
            return _pages > 0 ? TimeSpan.Zero : now - _leftAt;
        }
    }

    private AlertEvent Sound(RuleState state, DateTime now)
    {
        var rule = state.Rule;
        var alert = new AlertEvent(
            ++_seq, rule.Id, rule.Name, rule.Sound, rule.Muted, rule.Tab.Name, state.Gathered, rule.Tab.Describe(state.Latest!), now);
        state.Gathered = 0;
        state.SoundedAt = now;
        _waiting.Add(alert);
        if (_waiting.Count > MostWaiting)
        {
            _waiting.RemoveAt(0);
        }

        return alert;
    }

    private void Raise(List<AlertEvent> fired)
    {
        foreach (var alert in fired)
        {
            Fired?.Invoke(alert);
        }
    }

    private sealed class RuleState(AlertRule rule)
    {
        public AlertRule Rule { get; set; } = rule;

        public HashSet<Guid> Matching { get; } = [];

        public int Gathered { get; set; }

        public IRow? Latest { get; private set; }

        public DateTime SoundedAt { get; set; } = DateTime.MinValue;

        public void Hit(IRow row)
        {
            Gathered++;
            Latest = row;
        }

        public bool Due(DateTime now) => Gathered > 0 && now - SoundedAt >= Rule.Throttle;
    }
}
