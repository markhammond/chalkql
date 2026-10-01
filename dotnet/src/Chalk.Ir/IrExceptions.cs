namespace Chalk.Ir;

/// <summary>
/// The plan was produced by a planner speaking a newer IR than this client understands. Clients
/// reject newer IR; they never guess (<c>docs/design/02-ir.md</c> §2, rev 3 §4).
/// </summary>
public sealed class IrVersionMismatchException : ChalkException
{
    public IrVersionMismatchException(uint planVersion, uint clientVersion, string detail)
        : base(
            ChalkErrorCodes.IrVersionMismatch,
            $"Plan IR version {planVersion} cannot be read by this client, which speaks version {clientVersion}. "
            + $"{detail} Upgrade the Chalk client packages, or pin the planner sidecar to a version that serves IR {clientVersion}.")
    {
        PlanIrVersion = planVersion;
        ClientIrVersion = clientVersion;
    }

    public uint PlanIrVersion { get; }

    public uint ClientIrVersion { get; }
}

/// <summary>
/// The plan violates a structural invariant of the IR (<c>docs/design/02-ir.md</c> §8). This is a
/// contract bug in whoever produced the plan, not a user error; it is distinct from
/// <c>UnsupportedFeatureException</c>, which is valid IR the executor cannot run yet.
/// </summary>
public sealed class InvalidPlanException : ChalkException
{
    /// <summary>A plan that breaks an invariant of the IR: <see cref="ChalkErrorCodes.Internal"/>.</summary>
    public InvalidPlanException(string invariant, string path, string detail)
        : this(ChalkErrorCodes.Internal, invariant, path, detail)
    {
    }

    /// <summary>The same, with the code of the rule it meets: a plan that breaks the entitlements, say.</summary>
    public InvalidPlanException(string code, string invariant, string path, string detail)
        : base(
            code,
            $"Invalid plan at {path}: {detail} (invariant {invariant}). The fault is in the planner that "
            + "produced the plan, not in the statement: report it with the statement.")
    {
        Invariant = invariant;
        Path = path;
    }

    /// <summary>The invariant id, e.g. <c>I-IR-3</c>.</summary>
    public string Invariant { get; }

    /// <summary>Where in the plan, e.g. <c>root/Filter/condition/args[1]</c>.</summary>
    public string Path { get; }
}
