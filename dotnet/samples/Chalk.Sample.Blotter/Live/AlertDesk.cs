using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Chalk.Sample.Blotter.Grid;
using Chalk.Sample.Blotter.Domain;

namespace Chalk.Sample.Blotter.Live;

/// <summary>An alert fired: which rule, how many rows since it last sounded, and the latest of them.</summary>
public sealed record AlertEvent(long Seq, string RuleId, string RuleName, string Sound, bool Muted, string Tab, int Count, string Latest, DateTime At);

/// <summary>
/// The alerts of every browser, evaluated by the host from the streams, not by the page, and by
/// ChalkQL, not by the host's own code: each condition is the <c>WHERE</c> of a statement over what
/// the streams just sent, run once for every browser that has it, however far behind a page's grid
/// has fallen and whether or not a page is open. Four times a second, what the tail is waiting for
/// the database to hold is weighed against the rules that watch the wait, each row once for each of
/// its versions. A browser's alerts wait, in order, until a page of it has sounded them; a browser no
/// page has opened for <see cref="KeepFor"/> is let go, alerts and all.
/// </summary>
/// <remarks>
/// The statements run one after another on the desk's own loop, never on the host's ingest loop: a
/// slow one delays alerts, not the grid. What the streams sent stays in the arrival tables for
/// <see cref="BlotterHost.ArrivedFor"/>, long enough for the desk to read it.
/// </remarks>
public sealed class AlertDesk : IAsyncDisposable
{
    /// <summary>How long a browser's alerts are kept, and evaluated, with no page of it open.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    private readonly BlotterHost _host;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, AlertSession> _sessions = new(StringComparer.Ordinal);
    private readonly Channel<(IReadOnlyList<Order> Versions, IReadOnlyList<Fill> Fills)?> _work =
        Channel.CreateUnbounded<(IReadOnlyList<Order>, IReadOnlyList<Fill>)?>(new UnboundedChannelOptions { SingleReader = true });

    // Each condition a rule that watches the wait has, by its text: whether each row's version passes.
    private readonly Dictionary<string, Dictionary<(Guid Id, int Version), bool>> _passed = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Timer _timer;
    private readonly Task _loop;
    private int _ticking;

    public AlertDesk(BlotterHost host, ILogger<AlertDesk> log)
    {
        _host = host;
        _log = log;
        host.ArrivalsKept += OnArrivalsKept;
        _loop = Task.Run(() => RunAsync(_stop.Token));
        _timer = new Timer(_ => Tick(), null, 250, 250);
    }

    /// <summary>The alerts of the browser <paramref name="browser"/>, its own id: kept since its last page, or new.</summary>
    public AlertSession Open(string browser) => _sessions.GetOrAdd(browser, b => new AlertSession(b));

    /// <summary>
    /// What ChalkQL says is wrong with a condition, as the desk would run it, before a browser keeps
    /// it; null if nothing.
    /// </summary>
    public async Task<string?> CheckAsync(Condition condition, CancellationToken ct)
    {
        if (!condition.Errors.IsEmpty)
        {
            return condition.Errors[0].Message;
        }

        var refused = await _host.Queries.CheckAsync(ConditionStatements.Sql(condition, ConditionRows.Arrived), ct).ConfigureAwait(false)
            ?? await _host.Queries.CheckAsync(ConditionStatements.Sql(condition, ConditionRows.Tail), ct).ConfigureAwait(false);

        // Without the planner's correlation id: what a reader needs.
        var at = refused?.IndexOf(" [correlation id", StringComparison.Ordinal) ?? -1;
        return at < 0 ? refused : refused![..at];
    }

    public async ValueTask DisposeAsync()
    {
        _host.ArrivalsKept -= OnArrivalsKept;
        await _timer.DisposeAsync().ConfigureAwait(false);
        _work.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private void OnArrivalsKept(IReadOnlyList<Order> versions, IReadOnlyList<Fill> fills) => _work.Writer.TryWrite((versions, fills));

    // A tick is asked for only when the last has been taken, so that a slow statement does not pile them up.
    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 0)
        {
            _work.Writer.TryWrite(null);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await foreach (var work in _work.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                if (work is { } arrived)
                {
                    await NoteAsync(arrived.Versions, arrived.Fills, ct).ConfigureAwait(false);
                }
                else
                {
                    Interlocked.Exchange(ref _ticking, 0);
                    await TickAsync(DateTime.UtcNow, ct).ConfigureAwait(false);
                }
            }
            catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(failure, "alerts: a condition could not be weighed");
            }
        }
    }

    /// <summary>Weighs what the streams sent: each condition once, then each browser's rules by what it found.</summary>
    private async Task NoteAsync(IReadOnlyList<Order> versions, IReadOnlyList<Fill> fills, CancellationToken ct)
    {
        var rules = _sessions.Values.SelectMany(s => s.Rules).Where(r => r.LagOver is null).ToList();
        var passing = new Dictionary<string, HashSet<(Guid Id, int Version)>>(StringComparer.Ordinal);
        foreach (var condition in Distinct(rules))
        {
            IReadOnlyList<IRow> rows = condition.Tab.Overrides ? versions : fills;
            if (rows.Count > 0)
            {
                var sql = ConditionStatements.Sql(condition, ConditionRows.Arrived);
                passing[Name(condition)] = [.. (await _host.Queries.WhereAsync(condition, sql, rows, ct).ConfigureAwait(false)).Select(r => (r.Id, r.Version))];
            }
        }

        var now = DateTime.UtcNow;
        foreach (var session in _sessions.Values)
        {
            session.Note(versions, fills, (rule, row) => passing.TryGetValue(Name(rule.Condition), out var passed) && passed.Contains((row.Id, row.Version)), now);
        }
    }

    /// <summary>
    /// Weighs the tail's waits against the rules that watch them: a row past the shortest wait any of
    /// them allows is weighed against each condition once for each of its versions; then each browser
    /// fires its rules by what was found.
    /// </summary>
    private async Task TickAsync(DateTime now, CancellationToken ct)
    {
        _host.KeepArrivals = _sessions.Values.Any(s => s.Rules.Any(r => r.LagOver is null));
        var reconciling = _host.Reconciling;
        var watching = _sessions.Values.SelectMany(s => s.Rules).Where(r => r.LagOver is not null).ToList();
        foreach (var gone in _passed.Keys.Where(k => watching.All(r => Name(r.Condition) != k)).ToList())
        {
            _passed.Remove(gone);
        }

        foreach (var condition in Distinct(watching))
        {
            var name = Name(condition);
            var shortest = watching.Where(r => Name(r.Condition) == name).Min(r => r.LagOver!.Value);
            if (!_passed.TryGetValue(name, out var passed))
            {
                _passed[name] = passed = [];
            }

            // Forgotten once the tail has let the row go, or has a newer version of it.
            foreach (var key in passed.Keys.Where(k => !reconciling.TryGetValue(k.Id, out var r) || r.Row.Version != k.Version).ToList())
            {
                passed.Remove(key);
            }

            var late = reconciling.Values
                .Where(r => r.Row is Order == condition.Tab.Overrides && r.Lag(now) > shortest && !passed.ContainsKey((r.Row.Id, r.Row.Version)))
                .Select(r => r.Row)
                .ToList();
            if (late.Count == 0)
            {
                continue;
            }

            var sql = ConditionStatements.Sql(condition, ConditionRows.Tail);
            var found = (await _host.Queries.WhereAsync(condition, sql, late, ct).ConfigureAwait(false)).Select(r => (r.Id, r.Version)).ToHashSet();
            foreach (var row in late)
            {
                passed[(row.Id, row.Version)] = found.Contains((row.Id, row.Version));
            }
        }

        foreach (var (browser, session) in _sessions)
        {
            session.Weigh(reconciling, (rule, row) => _passed.TryGetValue(Name(rule.Condition), out var passed)
                && passed.GetValueOrDefault((row.Id, row.Version)), now);
            session.Flush(now);
            if (session.Unattended(now) > KeepFor)
            {
                _sessions.TryRemove(browser, out _);
            }
        }
    }

    private static IEnumerable<Condition> Distinct(IEnumerable<AlertRule> rules) =>
        rules.GroupBy(r => Name(r.Condition), StringComparer.Ordinal).Select(g => g.First().Condition);

    private static string Name(Condition condition) => condition.Tab.Name + "\n" + condition.Text;
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
                if (!_states.TryGetValue(rule.Id, out var state) || state.Rule.Condition.Text != rule.Condition.Text || state.Rule.Tab != rule.Tab
                    || state.Rule.LagOver != rule.LagOver)
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

    /// <summary>Weighs what the streams sent against each rule, by whether each row <paramref name="passes"/> its condition.</summary>
    public void Note(IReadOnlyList<Order> versions, IReadOnlyList<Fill> fills, Func<AlertRule, IRow, bool> passes, DateTime now)
    {
        List<AlertEvent> fired = [];
        lock (_gate)
        {
            foreach (var state in _states.Values)
            {
                var rule = state.Rule;
                if (rule.LagOver is not null)
                {
                    // Weighed by the wait, not by what arrives.
                    continue;
                }

                if (rule.Tab.Overrides)
                {
                    foreach (var version in versions)
                    {
                        // An order fires as it comes to pass the filter; then not again until it has left it.
                        if (!passes(rule, version))
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
                    foreach (var fill in fills.Where(f => passes(rule, f)))
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

    /// <summary>
    /// Weighs the tail's rows against the rules that watch the wait for the database: a row that passes
    /// the filter and has waited longer than the rule allows fires it, once for each wait. A row
    /// reconciled since the last look, but later than allowed, fires it too.
    /// </summary>
    public void Weigh(ImmutableDictionary<Guid, Reconciliation> reconciling, Func<AlertRule, IRow, bool> passes, DateTime now)
    {
        List<AlertEvent> fired = [];
        lock (_gate)
        {
            foreach (var state in _states.Values)
            {
                if (state.Rule.LagOver is not { } over)
                {
                    continue;
                }

                var rule = state.Rule;
                foreach (var (id, reconciliation) in reconciling)
                {
                    var lag = reconciliation.Lag(now);
                    if (lag > over && reconciliation.Row is Order == rule.Tab.Overrides && passes(rule, reconciliation.Row)
                        && state.Waits.Add((id, reconciliation.Since)))
                    {
                        state.Hit(reconciliation.Row, lag);
                    }
                }

                // A wait is remembered while the tail holds it, so the set is as big as the tail at most.
                state.Waits.RemoveWhere(w => !reconciling.TryGetValue(w.Id, out var r) || r.Since != w.Since);
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
            ++_seq, rule.Id, rule.Name, rule.Sound, rule.Muted, rule.Tab.Name, state.Gathered,
            rule.Tab.Describe(state.Latest!) + (state.LatestLag is { } lag ? $" · {Column.Seconds(lag)} waiting for the database" : ""), now);
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

        /// <summary>The waits that have fired the rule: each row's, by when it began.</summary>
        public HashSet<(Guid Id, DateTime Since)> Waits { get; } = [];

        public int Gathered { get; set; }

        public IRow? Latest { get; private set; }

        /// <summary>How long <see cref="Latest"/> had waited for the database, for a rule that watches the wait.</summary>
        public TimeSpan? LatestLag { get; private set; }

        public DateTime SoundedAt { get; set; } = DateTime.MinValue;

        public void Hit(IRow row, TimeSpan? lag = null)
        {
            Gathered++;
            Latest = row;
            LatestLag = lag;
        }

        public bool Due(DateTime now) => Gathered > 0 && now - SoundedAt >= Rule.Throttle;
    }
}
