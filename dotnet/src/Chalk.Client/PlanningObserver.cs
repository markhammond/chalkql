namespace Chalk.Client;

/// <summary>
/// Told of the engine's planning as it happens: each drifted statement a background run judges, each
/// run, each refresh that takes a run's work in, and each prepare. Bound as
/// <see cref="ChalkEngineOptions.PlanningObserver"/>, so that a host decides what to log, count or show
/// of it, rather than read the engine's log.
/// </summary>
/// <remarks>
/// Called on the engine's threads: a background run's, a prepare's, and a refresh's once it has let go
/// of its lock. So each method must be quick, safe on any thread, and leave the engine alone. One that
/// throws is logged once, as a warning, and its throw is otherwise ignored: what the engine does is
/// the same either way. Each method does nothing unless the host overrides it.
/// </remarks>
public interface IPlanningObserver
{
    /// <summary>A statement whose numbers drifted, as a background run left it.</summary>
    void Judged(in StatementJudged judged)
    {
    }

    /// <summary>A background run, over: what it sent the planner, and what came of the drifted statements.</summary>
    void Ran(in PlanningRun run)
    {
    }

    /// <summary>A refresh that took a background run's work in, after it published.</summary>
    void TookIn(in PlansTakenIn takenIn)
    {
    }

    /// <summary>A statement prepared, by the host or by a view in its place, and why.</summary>
    void Prepared(in StatementPrepared prepared)
    {
    }
}

/// <summary>What a background run came to for a statement whose numbers drifted.</summary>
public enum PlanVerdict : byte
{
    /// <summary>Planned anew: a plan staged for the next refresh to swap in.</summary>
    PlannedAnew = 0,

    /// <summary>Planned to the plan it runs: judged from the new numbers from now on.</summary>
    Unchanged = 1,

    /// <summary>Planned to a plan that saves less than its view's margin, which the planner held back.</summary>
    HeldByMargin = 2,

    /// <summary>Not planned: its view's host said not now.</summary>
    Vetoed = 3,

    /// <summary>Planned to another statement, so neither swapped in nor planned again.</summary>
    NotTheSame = 4,

    /// <summary>Not planned: the planner failed, or an extension refused what it brought.</summary>
    Failed = 5,
}

/// <summary>Why a statement was prepared.</summary>
public enum PrepareReason : byte
{
    /// <summary>The host prepared it.</summary>
    Host = 0,

    /// <summary>A view prepared it again in place of its statement, a table of which changed shape.</summary>
    ChangedShape = 1,

    /// <summary>
    /// A view prepared it again in place of its statement, which a tenfold drift made stale and the
    /// background cannot plan again.
    /// </summary>
    Drift = 2,
}

/// <summary>A statement whose numbers drifted, as a background run left it.</summary>
public readonly struct StatementJudged
{
    internal StatementJudged(
        ulong logicalDigest,
        ulong runningPlan,
        ulong newPlan,
        PlanVerdict verdict,
        double? newCost,
        double? runningCost,
        bool stale)
    {
        LogicalDigest = logicalDigest;
        RunningPlan = runningPlan;
        NewPlan = newPlan;
        Verdict = verdict;
        NewCost = newCost;
        RunningCost = runningCost;
        Stale = stale;
    }

    /// <summary>The statement: the same for every plan of it, so a host's key for it (D342).</summary>
    public ulong LogicalDigest { get; }

    /// <summary>The digest of the plan it runs.</summary>
    public ulong RunningPlan { get; }

    /// <summary>
    /// The digest of the plan the run came to: <see cref="RunningPlan"/> when unchanged, and zero when
    /// no plan came back — held back by a margin, vetoed, or failed.
    /// </summary>
    public ulong NewPlan { get; }

    /// <summary>What the run came to.</summary>
    public PlanVerdict Verdict { get; }

    /// <summary>What the planner priced the new plan at, under its view's margin; null otherwise.</summary>
    public double? NewCost { get; }

    /// <summary>
    /// What the planner priced the plan it runs at on the same numbers, under its view's margin; null
    /// otherwise, or when the planner no longer kept that plan.
    /// </summary>
    public double? RunningCost { get; }

    /// <summary>Whether a tenfold drift had made it stale: the run its view asks for, rather than prepare it anew.</summary>
    public bool Stale { get; }
}

/// <summary>A background run, over: what it sent the planner, and what came of the drifted statements.</summary>
public readonly struct PlanningRun
{
    internal PlanningRun(
        int tablesSent,
        int drifted,
        int plannedAnew,
        int unchanged,
        int heldByMargin,
        int vetoed,
        int notTheSame,
        int failed,
        TimeSpan elapsed)
    {
        TablesSent = tablesSent;
        Drifted = drifted;
        PlannedAnew = plannedAnew;
        Unchanged = unchanged;
        HeldByMargin = heldByMargin;
        Vetoed = vetoed;
        NotTheSame = notTheSame;
        Failed = failed;
        Elapsed = elapsed;
    }

    /// <summary>The tables whose numbers it sent the planner.</summary>
    public int TablesSent { get; }

    /// <summary>The statements whose numbers had drifted twofold since their plans were made.</summary>
    public int Drifted { get; }

    /// <summary>Of those, the ones planned anew.</summary>
    public int PlannedAnew { get; }

    /// <summary>Of those, the ones planned to the plans they run.</summary>
    public int Unchanged { get; }

    /// <summary>Of those, the ones whose new plans saved less than their views' margins.</summary>
    public int HeldByMargin { get; }

    /// <summary>Of those, the ones whose views' hosts said not now.</summary>
    public int Vetoed { get; }

    /// <summary>Of those, the ones planned to another statement.</summary>
    public int NotTheSame { get; }

    /// <summary>Of those, the ones not planned because planning failed.</summary>
    public int Failed { get; }

    /// <summary>How long the run took.</summary>
    public TimeSpan Elapsed { get; }
}

/// <summary>A refresh that took a background run's work in.</summary>
public readonly struct PlansTakenIn
{
    internal PlansTakenIn(long epoch, long version, int plansSwappedIn, int plansUnchanged, int statisticsTables)
    {
        Epoch = epoch;
        Version = version;
        PlansSwappedIn = plansSwappedIn;
        PlansUnchanged = plansUnchanged;
        StatisticsTables = statisticsTables;
    }

    /// <summary>The epoch the refresh published: the number its <c>RefreshAsync</c> returned.</summary>
    public long Epoch { get; }

    /// <summary>
    /// The version the refresh published (<see cref="ChalkEngine.Version"/>): the host's own, when it
    /// numbered the refresh, and one past the last otherwise. A view numbers its updates by version,
    /// not epoch (<c>ViewUpdate.Version</c>), so this is the number to match them by. A plan swapped
    /// in here runs from the view's first update at this version or later; under a blue/green swap,
    /// that update starts building it beside the plan the view runs, and the first after the build
    /// swaps it in.
    /// </summary>
    public long Version { get; }

    /// <summary>The statements that run their new plans from this epoch.</summary>
    public int PlansSwappedIn { get; }

    /// <summary>
    /// The plans that came to the plan their statements already ran, or no longer fitted a table that
    /// changed shape meanwhile: nothing swapped in for them.
    /// </summary>
    public int PlansUnchanged { get; }

    /// <summary>The tables whose computed statistics entered with this refresh.</summary>
    public int StatisticsTables { get; }
}

/// <summary>A statement prepared, by the host or by a view in its place.</summary>
public readonly struct StatementPrepared
{
    internal StatementPrepared(ulong logicalDigest, ulong planDigest, PrepareReason reason, TimeSpan elapsed)
    {
        LogicalDigest = logicalDigest;
        PlanDigest = planDigest;
        Reason = reason;
        Elapsed = elapsed;
    }

    /// <summary>The statement (D342).</summary>
    public ulong LogicalDigest { get; }

    /// <summary>The digest of the plan it was prepared with.</summary>
    public ulong PlanDigest { get; }

    /// <summary>Why it was prepared.</summary>
    public PrepareReason Reason { get; }

    /// <summary>How long the prepare took: the planner's work and the compile.</summary>
    public TimeSpan Elapsed { get; }
}
