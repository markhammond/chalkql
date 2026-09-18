namespace Chalk.Ir;

/// <summary>
/// The plan was produced by a planner speaking a newer IR than this client understands. Clients
/// reject newer IR; they never guess (<c>docs/design/02-ir.md</c> §2, rev 3 §4).
/// </summary>
public sealed class IrVersionMismatchException : ChalkException
{
    public IrVersionMismatchException(uint planVersion, uint clientVersion, string detail)
        : base(
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
    public InvalidPlanException(string invariant, string path, string detail)
        : base($"Invalid plan at {path}: {detail} (violates {invariant}, docs/design/02-ir.md §8)")
    {
        Invariant = invariant;
        Path = path;
    }

    /// <summary>The invariant id, e.g. <c>I-IR-3</c>.</summary>
    public string Invariant { get; }

    /// <summary>Where in the plan, e.g. <c>root/Filter/condition/args[1]</c>.</summary>
    public string Path { get; }
}
