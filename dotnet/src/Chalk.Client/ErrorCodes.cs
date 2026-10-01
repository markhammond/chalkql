using Chalk.Client.Rpc;

namespace Chalk.Client;

/// <summary>
/// Where a client's codes come from: the violations a planner's error carries, and the bracketed
/// form a message ends with.
/// </summary>
internal static class ErrorCodes
{
    /// <summary>
    /// The rules a planner's error breaks, in the order it met them, as the wire names them. A code
    /// or a kind this client has no constant for is kept as it was sent; a violation that names no
    /// rule is left out.
    /// </summary>
    public static IReadOnlyList<ChalkViolation> Violations(PlanError error) =>
    [
        .. error.Violations
            .Where(violation => violation.Code.Length > 0)
            .Select(violation => new ChalkViolation(
                violation.Code,
                violation.Message.Length > 0 ? violation.Message : error.Message,
                violation.Kind.Length > 0 ? violation.Kind : null)),
    ];

    /// <summary>
    /// A message ending with its code in brackets, as <see cref="ChalkException"/> writes it, for the
    /// two exceptions that derive from a framework type instead.
    /// </summary>
    public static string WithCode(string code, string message) =>
        message.EndsWith($"[{code}]", StringComparison.Ordinal) ? message : $"{message} [{code}]";
}
