namespace Chalk.Client;

/// <summary>
/// What may end a statement's optimiser search before it has run to completion (D234,
/// <c>docs/design/30-planning-options.md</c>).
/// </summary>
/// <remarks>
/// Every field defaults to today's behaviour — the search runs to completion — so an engine that
/// sets none of them plans exactly as it did before this existed, with no listener installed and no
/// cost sampled. Because these options can change which plan comes back, they belong in a
/// plan-cache key beside <see cref="PrepareOptions.Pushdown"/>; <see cref="RecordedPlanner.KeyFor"/>
/// takes them for that reason.
/// </remarks>
public sealed class PlanningOptions
{
    /// <summary>The options every prepare gets when nobody sets any: a search that runs to completion.</summary>
    public static readonly PlanningOptions None = new();

    /// <summary>
    /// How long the optimiser may search. Null — the default — is no budget at all.
    /// </summary>
    /// <remarks>
    /// The one non-deterministic control here, and opt-in for that reason: two runs under a budget
    /// may stop at different points and produce different plans. Convergence does not have that
    /// property, because its interval is a count of rule evaluations and never a duration.
    /// A budget spent before the optimiser has any complete plan is a
    /// <see cref="PlanningException"/> of kind <c>PlanningAborted</c>, not a plan.
    /// </remarks>
    public TimeSpan? TimeBudget { get; init; }

    /// <summary>
    /// How many consecutive samples must show no improvement before the search is called converged.
    /// Zero — the default — never stops on convergence.
    /// </summary>
    public int ConvergencePatience { get; init; }

    /// <summary>
    /// How close to 1.0 a sample's improvement ratio must be to count as no improvement. The ratio
    /// is Calcite's own <c>divideBy</c>: the geometric mean over the non-zero finite components of
    /// rows, cpu and io.
    /// </summary>
    public double ConvergenceRangeThreshold { get; init; } = 0.001;

    /// <summary>
    /// How many rule evaluations there are between samples. An evaluation is one rule match fired.
    /// Null — the default — is not "no sampling": it means <see cref="ConvergenceSamplePeriod"/>
    /// governs instead (D239).
    /// </summary>
    /// <remarks>
    /// A count and never a duration, which is what makes a convergence-terminated plan the same plan
    /// on every machine (D235). <see cref="PlanningState.EvaluationCount"/> is how a host chooses
    /// one: plan the statement once and see how many evaluations it takes. Setting this is the
    /// reproducible mode, and it takes over from <see cref="ConvergenceSamplePeriod"/> whenever it is
    /// set, whatever that period is.
    /// </remarks>
    public int? ConvergenceEvaluationInterval { get; init; }

    /// <summary>
    /// The wall-clock cadence a look happens on, when <see cref="ConvergenceEvaluationInterval"/> is
    /// not set (D239). Null — the default — is the sidecar's own default of 20 ms.
    /// </summary>
    /// <remarks>
    /// This is the interactive default: a look costs the sidecar one volatile read per rule
    /// evaluation and lands within one evaluation of the cadence, with nothing to measure or tune.
    /// It is what a slice is time-boxed by once a bounded worker pool is time-slicing plannings
    /// (D240), which is also why every planning takes looks whether or not it asked for convergence
    /// or a budget: the pool needs a look to know where one planning's turn ends and the next
    /// begins. <see cref="ConvergenceEvaluationInterval"/> is the reproducible alternative, and
    /// governs instead whenever it is set — a host that needs a plan to be the same on every machine
    /// asks for it explicitly rather than relying on this one.
    /// </remarks>
    public TimeSpan? ConvergenceSamplePeriod { get; init; }

    /// <summary>
    /// Cancel this to say "give me the best complete plan you have" (D236). The prepare returns a
    /// plan: if the optimiser has no complete plan yet it returns the first one it finds.
    /// </summary>
    /// <remarks>
    /// This is not the call's own cancellation. Cancelling the token passed to <c>PrepareAsync</c>
    /// abandons the prepare and throws <see cref="OperationCanceledException"/> with no plan; this
    /// one asks for the plan so far and is the interactive "that will do" control. The two compose:
    /// a relaxed <see cref="TimeBudget"/> with a stop wired to a button is the shape the option was
    /// asked for in.
    /// </remarks>
    public CancellationToken StopToken { get; init; }

    /// <summary>
    /// This planning's priority once its first slice is over, under the sidecar's bounded worker
    /// pool (D241, D240). Normal — the default — is right for almost everything; every planning is
    /// served from the high queue for its own first slice regardless of this value, so a simple
    /// query never waits behind a complex one that has already had one.
    /// </summary>
    public PlanningPriority Priority { get; init; } = PlanningPriority.Normal;

    /// <summary>
    /// A named partition of the sidecar's workers, declared inline (D245). Null — the default —
    /// runs uncapped and outside every session. The name is the identity, not this gRPC channel:
    /// two channels naming the same session share one cap, and a host may open any number of named
    /// sessions. Advisory quality-of-service for a host's own cooperating callers (D247), never
    /// isolation between callers that do not cooperate — that would need authentication, which this
    /// is not.
    /// </summary>
    public PlanningSession? Session { get; init; }

    /// <summary>True when nothing here can end a search early, which is the default.</summary>
    internal bool RunsToCompletion =>
        TimeBudget is not { Ticks: > 0 } && ConvergencePatience <= 0 && !StopToken.CanBeCanceled;

    /// <summary>The part of these options that belongs in a plan-cache key (D234).</summary>
    /// <remarks>
    /// The stop token is not in it: a stop is an event during one search, not a property of the
    /// statement, and two prepares that differ only in whether somebody pressed a button are not two
    /// statements. The budget is, because it can change the plan. Priority and Session are not in it
    /// either, for the same reason as the stop token: both are scheduling — when and alongside what
    /// a planning runs — and neither can change which plan the optimiser returns.
    /// </remarks>
    internal string CacheKeyPart =>
        TimeBudget is not { Ticks: > 0 } && ConvergencePatience <= 0
            ? string.Empty
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{TimeBudget?.TotalMilliseconds ?? 0:0.###}/{ConvergencePatience}/{ConvergenceRangeThreshold:R}/"
                + $"{ConvergenceEvaluationInterval ?? 0}/{ConvergenceSamplePeriod?.TotalMilliseconds ?? 0:0.###}");
}

/// <summary>
/// A named partition of the sidecar's planning workers, with a concurrency cap (D245, D246).
/// </summary>
/// <remarks>
/// There is no lifecycle call: the sidecar keeps a registry keyed by <see cref="Name"/>, the first
/// request naming a session registers it with the <see cref="MaxConcurrency"/> it carries — including
/// zero, which is how a session nobody ever caps runs uncapped — and a later request naming the same
/// session with a different, non-zero cap replaces it; a cap of zero on a later request leaves the
/// registered cap as it is, which is what lets a host join an existing session without having to know
/// or repeat its cap. A cap at or above the sidecar's worker count is no cap at all.
/// </remarks>
public sealed class PlanningSession
{
    public required string Name { get; init; }

    public required int MaxConcurrency { get; init; }
}

/// <summary>How a planning ended (D237). One value per <c>chalk.v1.PlanningTerminationReason</c>.</summary>
/// <remarks>
/// Hand-written rather than the generated wire enum, for the reason <see cref="PushdownLevel"/> is:
/// a host should not have to reference <c>Chalk.Client.Rpc</c> to read a prepared query.
/// </remarks>
public enum PlanningTerminationReason
{
    /// <summary>
    /// The search finished, or it stopped improving. Both mean "the optimiser had nothing more worth
    /// doing"; <see cref="PlanningState.EvaluationCount"/> and the costs say which.
    /// </summary>
    Converged = 1,

    /// <summary>The time budget was spent, and the best plan so far was returned.</summary>
    BudgetExhausted = 2,

    /// <summary>The host asked for the plan so far through <see cref="PlanningOptions.StopToken"/>.</summary>
    StoppedByHost = 3,

    /// <summary>The call was cancelled, or its session was lost. There is no plan.</summary>
    CancelledByHost = 4,

    /// <summary>The search ended before there was any complete plan. There is no plan.</summary>
    Aborted = 5,
}

/// <summary>
/// A planning's priority once its first slice is over, under a bounded worker pool (D241). One
/// value per <c>chalk.v1.PlanningPriority</c>; hand-written for the reason <see cref="PushdownLevel"/>
/// is.
/// </summary>
public enum PlanningPriority
{
    High = 1,
    Normal = 2,
    Low = 3,
}

/// <summary>Calcite's three cost components, as the optimiser carries them.</summary>
public sealed class PlanningCost
{
    public required double Rows { get; init; }

    public required double Cpu { get; init; }

    public required double Io { get; init; }

    /// <summary>
    /// <paramref name="cost"/> against <paramref name="other"/>, as Calcite's
    /// <c>VolcanoCost.divideBy</c> computes it: the geometric mean of the ratios of the components
    /// that are non-zero and finite on both sides, and 1.0 when there are none.
    /// </summary>
    /// <remarks>
    /// Transcribed rather than approximated, because it is the very function the sidecar's
    /// convergence test uses and a host comparing the two should get the same number.
    /// </remarks>
    public static double DivideBy(PlanningCost cost, PlanningCost other)
    {
        ArgumentNullException.ThrowIfNull(cost);
        ArgumentNullException.ThrowIfNull(other);

        var d = 1.0;
        var n = 0.0;
        if (cost.Rows != 0 && !double.IsInfinity(cost.Rows) && other.Rows != 0 && !double.IsInfinity(other.Rows))
        {
            d *= cost.Rows / other.Rows;
            n++;
        }

        if (cost.Cpu != 0 && !double.IsInfinity(cost.Cpu) && other.Cpu != 0 && !double.IsInfinity(other.Cpu))
        {
            d *= cost.Cpu / other.Cpu;
            n++;
        }

        if (cost.Io != 0 && !double.IsInfinity(cost.Io) && other.Io != 0 && !double.IsInfinity(other.Io))
        {
            d *= cost.Io / other.Io;
            n++;
        }

        return n == 0 ? 1.0 : Math.Pow(d, 1 / n);
    }

    /// <summary>The three numbers, for a plan text and for a log line.</summary>
    public override string ToString() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{{rows: {Rows:0.###}, cpu: {Cpu:0.###}, io: {Io:0.###}}}");
}

/// <summary>What the optimiser did for one statement, and how it stopped doing it (D237).</summary>
public sealed class PlanningState
{
    /// <summary>What a prepare reports when the sidecar said nothing — an older sidecar.</summary>
    internal static readonly PlanningState Unknown = new()
    {
        TerminationReason = PlanningTerminationReason.Converged,
    };

    public required PlanningTerminationReason TerminationReason { get; init; }

    /// <summary>The sidecar's whole planning of this request.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>The optimiser's share of it.</summary>
    public TimeSpan OptimiserElapsed { get; init; }

    /// <summary>
    /// Rule matches fired. Zero for a prepare whose options could not end a search early, because
    /// nothing was installed to count them — which is what makes the option free when unused.
    /// </summary>
    public long EvaluationCount { get; init; }

    /// <summary>
    /// Looks taken: a period-mode sample or a count-mode interval boundary, whichever governed
    /// (D239). Zero for a prepare no listener was installed for, exactly as <see cref="EvaluationCount"/>.
    /// </summary>
    public long Looks { get; init; }

    /// <summary>
    /// The evaluation interval in force, but only when <see cref="PlanningOptions.ConvergenceEvaluationInterval"/>
    /// governed the look cadence (D239). Null in the (default) period mode, where the interval is
    /// not a count and there is nothing to report here.
    /// </summary>
    public int? EvaluationInterval { get; init; }

    /// <summary>
    /// Time this planning actually spent holding a worker, summed across every slice (D242, D240).
    /// A time budget is charged against this, never against <see cref="Elapsed"/>: contention around
    /// a query is never why its own budget runs out.
    /// </summary>
    public TimeSpan RunningElapsed { get; init; }

    /// <summary>
    /// Time this planning spent waiting for a worker, summed across every wait (D242).
    /// <see cref="Elapsed"/> is <see cref="RunningElapsed"/> plus this, to the microsecond, modulo
    /// rounding.
    /// </summary>
    public TimeSpan QueuedElapsed { get; init; }

    /// <summary>
    /// Slices this planning ran in (D240, D242): one to begin with, one more for every look that did
    /// not end the search. One for a planning that never yielded, whether or not the sidecar's
    /// scheduler owned it.
    /// </summary>
    public long Slices { get; init; }

    /// <summary>
    /// The planning session this request named (D245, D247), or null for a request that named none.
    /// <see cref="QueuedElapsed"/> is what the design calls a session's own queued-for time: D242's
    /// accounting already separates queued from running time, so a session's share of it needs
    /// nothing beyond that field.
    /// </summary>
    public string? Session { get; init; }

    /// <summary>The root's best cost at the first sample that saw a complete plan.</summary>
    public PlanningCost? FirstCost { get; init; }

    /// <summary>The root's best cost at the last sample.</summary>
    public PlanningCost? BestCost { get; init; }

    /// <summary>
    /// <see cref="BestCost"/> against <see cref="FirstCost"/>, so a ratio below 1 is the improvement
    /// the search bought after its first complete answer. Null when nothing sampled a cost.
    /// </summary>
    public double? CostRatio =>
        FirstCost is { } first && BestCost is { } best ? PlanningCost.DivideBy(best, first) : null;

    /// <summary>The line a plan text and an explain carry (D237).</summary>
    /// <remarks>
    /// Deliberately without a duration: what a reader can rely on is the reason, the count and the
    /// costs, and those are the same on every machine for every termination but a budget's.
    /// </remarks>
    public override string ToString()
    {
        var ratio = CostRatio;
        var costs = ratio is null
            ? string.Empty
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $", first {FirstCost}, best {BestCost}, ratio {ratio:0.####}");
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{TerminationReason} after {EvaluationCount} evaluations{costs}");
    }
}

/// <summary>Wire <c>chalk.v1.PlanningState</c> to <see cref="PlanningState"/> and back.</summary>
internal static class PlanningStates
{
    internal static PlanningState FromProto(Rpc.PlanningState? state)
    {
        if (state is null)
        {
            return PlanningState.Unknown;
        }

        return new PlanningState
        {
            // UNSPECIFIED is a sidecar older than this option; reading it as Converged is what that
            // sidecar's behaviour actually was — every search ran to completion.
            TerminationReason =
                state.TerminationReason == Rpc.PlanningTerminationReason.Unspecified
                    ? PlanningTerminationReason.Converged
                    : (PlanningTerminationReason)state.TerminationReason,
            Elapsed = TimeSpan.FromTicks((long)state.ElapsedUs * TimeSpan.TicksPerMicrosecond),
            OptimiserElapsed =
                TimeSpan.FromTicks((long)state.OptimiserElapsedUs * TimeSpan.TicksPerMicrosecond),
            EvaluationCount = (long)state.EvaluationCount,
            FirstCost = Cost(state.FirstCost),
            BestCost = Cost(state.BestCost),
            Looks = (long)state.Looks,
            EvaluationInterval = state.EvaluationInterval == 0 ? null : (int)state.EvaluationInterval,
            RunningElapsed = TimeSpan.FromTicks((long)state.RunningElapsedUs * TimeSpan.TicksPerMicrosecond),
            QueuedElapsed = TimeSpan.FromTicks((long)state.QueuedElapsedUs * TimeSpan.TicksPerMicrosecond),
            Slices = (long)state.Slices,
            Session = state.Session.Length == 0 ? null : state.Session,
        };
    }

    private static PlanningCost? Cost(Rpc.PlanningCost? cost) =>
        cost is null ? null : new PlanningCost { Rows = cost.Rows, Cpu = cost.Cpu, Io = cost.Io };
}
