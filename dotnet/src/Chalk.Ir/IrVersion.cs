namespace Chalk.Ir;

/// <summary>
/// The plan IR version this build speaks. Twin of <c>chalk.ir.IrVersion</c> in the planner
/// (<c>docs/design/02-ir.md</c> §2); the two constants must always agree.
/// </summary>
public static class IrVersion
{
    /// <summary>The version this client stamps on <c>PlanRequest.client_ir_version</c>.</summary>
    public const uint Current = 1;

    /// <summary>The oldest IR version this client can read.</summary>
    public const uint Minimum = 1;
}
