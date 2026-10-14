using Chalk.Sources;
using Microsoft.Extensions.Logging;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Client;

/// <summary>
/// Statistics and plans left to the background by a refresh that defers its planning (design 69 §4),
/// and the numbers the planner holds, which such a refresh leaves behind the catalog's.
/// </summary>
public sealed partial class ChalkEngine
{
    /// <summary>
    /// Guards <see cref="_plannedNumbers"/>, <see cref="_plannerCatalog"/>, the statistics version,
    /// <see cref="_judgedStatisticsVersion"/> and <see cref="_planningOwed"/> together. Held only to
    /// read or replace them, never across a call to the planner.
    /// </summary>
    private readonly Lock _plannedGate = new();

    /// <summary>
    /// Every table's numbers as the planner holds them: what the last registration of each sent. A
    /// refresh that defers its planning sends nothing, so the catalog's numbers can run ahead of these
    /// until a background run, or a refresh that does not defer, sends the difference. A row count
    /// that has drifted by less than a tenth is not a difference (D351).
    /// </summary>
    private IReadOnlyDictionary<TableKey, TableNumbers> _plannedNumbers;

    /// <summary>
    /// A catalog whose numbers are the planner's, row counts within the tenth they may drift by
    /// (D351): what a plan is made on, and so what a prepared statement judges drift against (D342).
    /// The published catalog itself whenever planning is not deferred.
    /// </summary>
    private CatalogContext _plannerCatalog;

    /// <summary>
    /// The statistics version the last background run judged the held statements against (D351):
    /// numbers sent since — by a refresh that did not defer — are a reason for a run of their own,
    /// because a statement may now plan differently on them.
    /// </summary>
    private string _judgedStatisticsVersion;

    /// <summary>
    /// The last background run left some drifted statement on its plan — the planner failed, an
    /// extension refused what it brought, or its view's host vetoed a new plan for now (design 70 §1)
    /// — so the next refresh that defers its planning asks for another, as every such refresh did
    /// before D351, until one plans it.
    /// </summary>
    private bool _planningOwed;

    /// <summary>The prepared statements this engine made, held weakly: what a background run plans again.</summary>
    private readonly Lock _statementsGate = new();
    private readonly List<WeakReference<PreparedQuery>> _statements = [];
    private int _statementsKept = 64;

    /// <summary>One background run at a time, and whether another has been asked for meanwhile.</summary>
    private readonly Lock _planningGate = new();
    private Task? _planning;
    private bool _planningWanted;
    private readonly CancellationTokenSource _planningStop = new();
    private int _planningRuns;

    /// <summary>What finished runs produced, waiting for the next refresh to take it in.</summary>
    private BackgroundPlanning? _planned;

    /// <summary>Keeps <paramref name="statement"/> where a background run can find it, while the host holds it.</summary>
    internal void Track(PreparedQuery statement)
    {
        lock (_statementsGate)
        {
            // Pruned when the list has doubled since the last pruning, so that a host preparing a
            // statement per request does not grow it without bound.
            if (_statements.Count >= _statementsKept)
            {
                _statements.RemoveAll(reference => !reference.TryGetTarget(out _));
                _statementsKept = Math.Max(64, _statements.Count * 2);
            }

            _statements.Add(new WeakReference<PreparedQuery>(statement));
        }
    }

    private List<PreparedQuery> HeldStatements()
    {
        lock (_statementsGate)
        {
            var held = new List<PreparedQuery>(_statements.Count);
            foreach (var reference in _statements)
            {
                if (reference.TryGetTarget(out var statement))
                {
                    held.Add(statement);
                }
            }

            return held;
        }
    }

    /// <summary>
    /// Sends the planner the numbers of <paramref name="next"/> that it does not hold, as a new
    /// statistics version, and returns how many tables that was. The publish of a refresh that does
    /// not defer its planning.
    /// </summary>
    private async ValueTask<int> SendNumbersAsync(CatalogContext next, CatalogShape shape, CancellationToken ct)
    {
        await _registering.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<TableKey> moved;
            lock (_plannedGate)
            {
                moved = shape.StatisticsChangesSince(_plannedNumbers);
                if (moved.Count == 0)
                {
                    _plannerCatalog = next;
                    return 0;
                }
            }

            await RegisterNumbersAsync(next, shape, moved, ct).ConfigureAwait(false);
            return moved.Count;
        }
        finally
        {
            _registering.Release();
        }
    }

    /// <summary>
    /// The publish of a refresh that defers its planning: nothing is sent, and the planner's catalog
    /// moves only when the planner holds this catalog's numbers already. Returns whether some table's
    /// numbers are now ahead of the planner's.
    /// </summary>
    private bool NoteNumbers(CatalogContext next, CatalogShape shape)
    {
        lock (_plannedGate)
        {
            if (shape.StatisticsChangesSince(_plannedNumbers).Count > 0)
            {
                return true;
            }

            _plannerCatalog = next;
            return false;
        }
    }

    /// <summary>
    /// Sends <paramref name="tables"/>' numbers from <paramref name="catalog"/> — every table's when it
    /// is null — under a version minted now, and records them as the planner's. The caller holds
    /// <see cref="_registering"/>, so versions are minted, and arrive, in the order they are sent.
    /// </summary>
    private async ValueTask RegisterNumbersAsync(
        CatalogContext catalog, CatalogShape shape, IReadOnlyCollection<TableKey>? tables, CancellationToken ct)
    {
        var version = CatalogVersions.Mint();
        await _options.Planner.RegisterStatisticsAsync(
            StatisticsFor(catalog, _instanceId, version, tables), ct).ConfigureAwait(false);
        lock (_plannedGate)
        {
            _plannedNumbers = tables is null ? shape.Statistics : shape.NumbersSent(_plannedNumbers, tables);
            _plannerCatalog = catalog;
            _statisticsVersion = version;
            _registeredStatisticsVersion = version;
        }
    }

    /// <summary>
    /// Whether a refresh that deferred its planning left a background run anything to do (D351): some
    /// table's numbers ahead of the planner's (<paramref name="behind"/>), numbers sent since the
    /// last run that no run has judged the statements against, a source's deferred statistics past
    /// its own policy, or a statement the last run left on its plan. A refresh that moved none of
    /// them asks for no run, where each used to ask for one; a statement is planned again only on
    /// numbers the planner did not have, so a run without any would plan nothing new.
    /// </summary>
    private bool BackgroundPlanningDue(bool behind)
    {
        if (behind)
        {
            return true;
        }

        lock (_plannedGate)
        {
            if (_planningOwed
                || !string.Equals(_judgedStatisticsVersion, _statisticsVersion, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var registration in _sourceRegistrations)
        {
            if (registration.Source is IDeferredStatisticsSource { StatisticsDue: true })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Asks for a background run that plans <paramref name="statement"/> again (design 70 §2): what a
    /// view does with a statement a drift has made stale, rather than prepare it anew on its update's
    /// thread. The run judges it as it judges any statement whose numbers drifted, its view's host
    /// asked and its margin applied, and a refresh swaps a new plan in. Nothing is asked while a run is
    /// in flight, or a plan for it waits to be swapped in.
    /// </summary>
    internal void PlanAgainInBackground(PreparedQuery statement)
    {
        lock (_planningGate)
        {
            // A run in flight judges it, or a refresh that deferred meanwhile has asked for the next.
            if (_planning is not null || _planned?.Plans.ContainsKey(statement) == true)
            {
                return;
            }
        }

        RequestBackgroundPlanning();
    }

    /// <summary>
    /// Asks for a background run after a refresh that deferred its planning: one starts now when none
    /// is running, and otherwise the one running is followed by one more, over whatever is published
    /// when it starts.
    /// </summary>
    private void RequestBackgroundPlanning()
    {
        lock (_planningGate)
        {
            _planningWanted = true;
            if (_planning is null && !_planningStop.IsCancellationRequested)
            {
                _planning = Task.Run(PlanInBackgroundLoopAsync);
            }
        }
    }

    private async Task PlanInBackgroundLoopAsync()
    {
        while (true)
        {
            lock (_planningGate)
            {
                if (!_planningWanted || _planningStop.IsCancellationRequested)
                {
                    _planning = null;
                    return;
                }

                _planningWanted = false;
            }

            try
            {
                await PlanInBackgroundAsync(_planningStop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_planningStop.IsCancellationRequested)
            {
                // The engine is being disposed.
            }
            catch (Exception failure)
            {
                // The plans stay as they were; the next refresh that defers its planning tries again.
                _log.LogWarning(failure, "planning in the background failed; the plans stay as they were");
            }
        }
    }

    /// <summary>
    /// One background run (design 69 §4): the statistics deferred refreshes left, computed over the
    /// rows published now and sent to the planner; then every held statement whose tables have moved
    /// twofold since it was planned, planned again. Nothing of it takes effect until the next refresh.
    /// </summary>
    private async Task PlanInBackgroundAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _planningRuns);

        var computed = new List<(IDeferredStatisticsSource Source, string SourceId, IReadOnlyList<DeferredStatistics> Tables)>();
        foreach (var registration in _sourceRegistrations)
        {
            if (registration.Source is IDeferredStatisticsSource source)
            {
                var tables = source.ComputeDeferredStatistics(ct);
                if (tables.Count > 0)
                {
                    computed.Add((source, registration.Source.SourceId, tables));
                }
            }
        }

        // The shape taken from the published one, reading only the tables whose statistics were
        // computed (F205); whole, when a refresh published another catalog meanwhile.
        var published = Catalog;
        var catalog = WithStatistics(published, computed);
        var shape = Volatile.Read(ref _shape).Next(published, catalog) ?? CatalogShape.Of(catalog);
        var sent = 0;
        await _registering.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<TableKey> moved;
            lock (_plannedGate)
            {
                moved = shape.StatisticsChangesSince(_plannedNumbers);
            }

            if (moved.Count > 0)
            {
                await RegisterNumbersAsync(catalog, shape, moved, ct).ConfigureAwait(false);
                sent = moved.Count;
            }
        }
        finally
        {
            _registering.Release();
        }

        string judged;
        lock (_plannedGate)
        {
            judged = _statisticsVersion;
        }

        var plans = new List<(PreparedQuery Statement, PreparedQuery.PlanState Plan)>();
        var owed = false;
        int drifted = 0, unchanged = 0, held = 0, vetoed = 0, notTheSame = 0, failed = 0;
        foreach (var statement in HeldStatements())
        {
            ct.ThrowIfCancellationRequested();

            // A statement whose plan from an earlier run waits for a refresh to swap it in is judged
            // from that plan's numbers: planning it again on numbers that have not moved twofold since
            // would only plan the waiting plan a second time.
            PreparedQuery.PlanState? waiting;
            lock (_planningGate)
            {
                waiting = _planned?.Plans.GetValueOrDefault(statement);
            }

            if (!statement.MayPlanAgain
                || !(waiting is null ? statement.DriftedFrom(catalog) : PreparedQuery.DriftedFrom(waiting, catalog)))
            {
                continue;
            }

            drifted++;
            if (!statement.MayRefreshPlan())
            {
                // Its view's host vetoed a new plan for now (design 70 §1): nothing is requested, and
                // the next refresh that defers asks for a run that asks the host again.
                vetoed++;
                owed = true;
                continue;
            }

            try
            {
                var (plan, outcome) = await statement.PlanAgainAsync(ct).ConfigureAwait(false);
                switch (outcome)
                {
                    case PreparedQuery.PlanAgainOutcome.New:
                        plans.Add((statement, plan!));
                        break;
                    case PreparedQuery.PlanAgainOutcome.Unchanged:
                        unchanged++;
                        break;
                    case PreparedQuery.PlanAgainOutcome.Held:
                        held++;
                        break;
                    case PreparedQuery.PlanAgainOutcome.NotTheSame:
                        notTheSame++;
                        break;
                }
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // A statement that cannot be planned now keeps the plan it has, and the next refresh
                // that defers asks for a run that tries again.
                _log.LogDebug(failure, "planning {Digest:x16} again in the background failed", statement.LogicalDigest);
                failed++;
                owed = true;
            }
        }

        Stage(computed.Select(c => (c.Source, c.Tables)), plans);
        lock (_plannedGate)
        {
            _judgedStatisticsVersion = judged;
            _planningOwed = owed;
        }

        // What became of every statement whose numbers drifted, so that none passes unseen: a run
        // that planned none anew still says whether any drifted.
        var counts = new BackgroundPlans(drifted, plans.Count, unchanged, held, vetoed, notTheSame, failed);
        Volatile.Write(ref _lastBackgroundPlans, counts);
        _log.LogDebug(
            "planned in the background: statistics of {Tables} tables sent; {Drifted} statements drifted: "
            + "{New} planned anew, {Unchanged} unchanged, {Held} held back by a margin, {Vetoed} vetoed by "
            + "a host, {NotTheSame} not the same statement, {Failed} failed",
            sent,
            counts.Drifted,
            counts.New,
            counts.Unchanged,
            counts.Held,
            counts.Vetoed,
            counts.NotTheSame,
            counts.Failed);
    }

    /// <summary>What became of the statements a background run found drifted (design 70): for its log, and tests.</summary>
    /// <param name="Drifted">Statements whose numbers drifted twofold since their plans were made.</param>
    /// <param name="New">Planned anew, and staged for the next refresh to swap in.</param>
    /// <param name="Unchanged">Planned to the plans they run.</param>
    /// <param name="Held">Planned to plans that save less than their views' margins, which the planner held back.</param>
    /// <param name="Vetoed">Not planned: their views' hosts said not now.</param>
    /// <param name="NotTheSame">Planned to another statement, so neither swapped nor planned again.</param>
    /// <param name="Failed">Not planned: the planner failed, or an extension refused what it brought.</param>
    internal sealed record BackgroundPlans(
        int Drifted, int New, int Unchanged, int Held, int Vetoed, int NotTheSame, int Failed);

    private BackgroundPlans? _lastBackgroundPlans;

    /// <summary>What the last background run did with the statements it found drifted, if one ran: for tests.</summary>
    internal BackgroundPlans? LastBackgroundPlans => Volatile.Read(ref _lastBackgroundPlans);

    /// <summary>
    /// <paramref name="catalog"/> with the tables background statistics were computed for described
    /// with them: the numbers a background run sends, and the ones its plans are judged against.
    /// </summary>
    private static CatalogContext WithStatistics(
        CatalogContext catalog,
        IReadOnlyList<(IDeferredStatisticsSource Source, string SourceId, IReadOnlyList<DeferredStatistics> Tables)> computed)
    {
        if (computed.Count == 0)
        {
            return catalog;
        }

        var schemas = new List<Chalk.Catalog.SchemaDescriptor>(catalog.Schemas.Count);
        foreach (var schema in catalog.Schemas)
        {
            var mine = computed.FirstOrDefault(c => string.Equals(c.SourceId, schema.SourceId, StringComparison.Ordinal));
            if (mine.Tables is null)
            {
                schemas.Add(schema);
                continue;
            }

            var tables = new List<Chalk.Catalog.TableDescriptor>(schema.Tables.Count);
            foreach (var table in schema.Tables)
            {
                var fresh = mine.Tables.FirstOrDefault(
                    t => string.Equals(t.Table, table.Name, StringComparison.OrdinalIgnoreCase));
                tables.Add(fresh?.Descriptor ?? table);
            }

            schemas.Add(new Chalk.Catalog.SchemaDescriptor
            {
                SourceId = schema.SourceId,
                Name = schema.Name,
                Kind = schema.Kind,
                Dialect = schema.Dialect,
                Capabilities = schema.Capabilities,
                DialectProfile = schema.DialectProfile,
                CostProfile = schema.CostProfile,
                Tables = tables,
                Functions = schema.Functions,
                TrustSourceRowLevelSecurity = schema.TrustSourceRowLevelSecurity,
                Zone = schema.Zone,
            });
        }

        return new CatalogContext
        {
            ContextId = catalog.ContextId,
            Epoch = catalog.Epoch,
            Schemas = schemas,
            JoinPolicy = catalog.JoinPolicy,
            Associations = catalog.Associations,
        };
    }

    /// <summary>
    /// Keeps what a run produced for the next refresh, over whatever an earlier run left that no
    /// refresh has taken in yet: per table and per statement, the later run's word stands.
    /// </summary>
    private void Stage(
        IEnumerable<(IDeferredStatisticsSource Source, IReadOnlyList<DeferredStatistics> Tables)> statistics,
        List<(PreparedQuery Statement, PreparedQuery.PlanState Plan)> plans)
    {
        lock (_planningGate)
        {
            var staged = _planned ?? new BackgroundPlanning();
            foreach (var (source, tables) in statistics)
            {
                foreach (var table in tables)
                {
                    staged.Statistics[(source, table.Table.ToUpperInvariant())] = table;
                }
            }

            foreach (var (statement, plan) in plans)
            {
                staged.Plans[statement] = plan;
            }

            _planned = staged.Statistics.Count == 0 && staged.Plans.Count == 0 ? null : staged;
        }
    }

    /// <summary>
    /// Takes in what background runs produced (design 69 §4): the statistics enter their tables and
    /// the plans swap in. Called under the refresh lock, as a refresh begins, deferred or not.
    /// </summary>
    private void AdmitBackgroundPlanning()
    {
        BackgroundPlanning? staged;
        lock (_planningGate)
        {
            staged = _planned;
            _planned = null;
        }

        if (staged is null)
        {
            return;
        }

        foreach (var group in staged.Statistics.GroupBy(entry => entry.Key.Source, entry => entry.Value))
        {
            group.Key.AdmitStatistics([.. group]);
        }

        var swapped = 0;
        foreach (var (statement, plan) in staged.Plans)
        {
            if (statement.Adopt(plan))
            {
                swapped++;
            }
        }

        _log.LogDebug(
            "background planning taken in: statistics of {Tables} tables, {Statements} plans swapped in",
            staged.Statistics.Count,
            swapped);
    }

    /// <summary>Stops background planning, for disposal: a run in flight is cancelled and awaited.</summary>
    private async ValueTask StopBackgroundPlanningAsync()
    {
        Task? planning;
        lock (_planningGate)
        {
            _planningStop.Cancel();
            planning = _planning;
        }

        if (planning is not null)
        {
            await planning.ConfigureAwait(false);
        }

        _planningStop.Dispose();
    }

    /// <summary>How many background runs have started: for a test of their coalescing.</summary>
    internal int BackgroundPlanningRuns => Volatile.Read(ref _planningRuns);

    /// <summary>The shape of the published catalog, as publishes took it: for a test that it is the whole one's.</summary>
    internal CatalogShape PublishedShape => Volatile.Read(ref _shape);

    /// <summary>Whether a background run's work waits for the next refresh to take it in.</summary>
    internal bool BackgroundPlanningStaged
    {
        get
        {
            lock (_planningGate)
            {
                return _planned is not null;
            }
        }
    }

    /// <summary>Completes once no background run is running or asked for: for a test to wait on.</summary>
    internal async Task BackgroundPlanningSettledAsync()
    {
        while (true)
        {
            Task? planning;
            lock (_planningGate)
            {
                planning = _planning;
            }

            if (planning is null)
            {
                return;
            }

            await planning.ConfigureAwait(false);
        }
    }

    /// <summary>What background runs produced and no refresh has taken in yet.</summary>
    private sealed class BackgroundPlanning
    {
        public Dictionary<(IDeferredStatisticsSource Source, string Table), DeferredStatistics> Statistics { get; } = [];

        public Dictionary<PreparedQuery, PreparedQuery.PlanState> Plans { get; } =
            new(ReferenceEqualityComparer.Instance);
    }
}
