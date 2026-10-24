using System.Collections.Immutable;
using Chalk.Sample.Blotter.Grid;

namespace Chalk.Sample.Blotter.Live;

/// <summary>An alert fired: which rule, how many rows since it last sounded, and the latest of them.</summary>
public sealed record AlertEvent(long Seq, string RuleId, string RuleName, string Sound, bool Muted, string Tab, int Count, string Latest, DateTime At);

/// <summary>
/// One browser's alerts: its rules, what each has fired and not yet sounded, and the alerts no page of
/// it has sounded yet. A rule that fires within its throttle of sounding gathers its rows, and sounds
/// once the throttle allows, saying how many.
/// </summary>
public sealed class AlertSession(string browser)
{
    // Kept for a page to sound; past this many, the oldest go.
    private const int MostWaiting = 1000;

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
    /// Sets the rules. A rule whose condition is unchanged keeps what it has gathered; a new one, or a
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
                if (!_states.TryGetValue(rule.Id, out var state) || state.View != AlertWindowSpec.NameOf(rule))
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

    /// <summary>Rows have entered an alert view: each rule that watches it gathers them.</summary>
    public void Enter(string view, IReadOnlyList<(IRow Row, TimeSpan? Lag)> rows, DateTime now)
    {
        List<AlertEvent> fired = [];
        lock (_gate)
        {
            foreach (var state in _states.Values.Where(s => s.View == view))
            {
                foreach (var (row, lag) in rows)
                {
                    state.Hit(row, lag);
                }

                if (state.Due(now))
                {
                    fired.Add(Sound(state, now));
                }
            }
        }

        Raise(fired);
    }

    /// <summary>A count view's row has entered it: each rule that counts with it fires, saying how many matches the window holds.</summary>
    public void Count(string view, long matches, DateTime now)
    {
        List<AlertEvent> fired = [];
        lock (_gate)
        {
            foreach (var state in _states.Values.Where(s => s.View == view))
            {
                state.Counted(matches);
                fired.Add(Sound(state, now));
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
        var latest = rule.Count is { } count
            ? count.Kind == CountKind.AtLeast
                ? $"{state.LatestMatches:N0} in the last {AlertCount.Span(count.Window)}"
                : $"none in the last {AlertCount.Span(count.Window)}"
            : rule.Tab.Describe(state.Latest!) + (state.LatestLag is { } lag ? $" · {Column.Seconds(lag)} waiting for the database" : "");
        var alert = new AlertEvent(++_seq, rule.Id, rule.Name, rule.Sound, rule.Muted, rule.Tab.Name, state.Gathered, latest, now);
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

        /// <summary>The alert view or count view it watches, by name.</summary>
        public string View { get; } = AlertWindowSpec.NameOf(rule);

        public int Gathered { get; set; }

        public IRow? Latest { get; private set; }

        /// <summary>How long <see cref="Latest"/> had waited for the database, for a rule that watches the wait.</summary>
        public TimeSpan? LatestLag { get; private set; }

        public DateTime SoundedAt { get; set; } = DateTime.MinValue;

        /// <summary>For a rule that counts, the matches in the window when it last fired.</summary>
        public long LatestMatches { get; private set; }

        public void Counted(long matches)
        {
            Gathered++;
            LatestMatches = matches;
        }

        public void Hit(IRow row, TimeSpan? lag = null)
        {
            Gathered++;
            Latest = row;
            LatestLag = lag;
        }

        public bool Due(DateTime now) => Gathered > 0 && now - SoundedAt >= Rule.Throttle;
    }
}
