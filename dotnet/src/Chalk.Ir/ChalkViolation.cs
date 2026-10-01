namespace Chalk;

/// <summary>
/// One rule an error breaks: its <see cref="Code"/>, what kind of problem the planner says it is,
/// and the message that says what happened. A <see cref="ChalkException"/> carries every rule its
/// error breaks in <see cref="ChalkException.Violations"/>.
/// </summary>
/// <remarks>
/// ChalkQL stops at the first rule it finds broken, so an error carries one violation today. A host
/// that reads them as a list is ready for an error that reports more.
/// </remarks>
public sealed class ChalkViolation
{
    /// <summary>A violation of the rule <paramref name="code"/>, with no kind.</summary>
    public ChalkViolation(string code, string message)
        : this(code, message, kind: null)
    {
    }

    /// <summary>A violation the planner reported, under the kind it named.</summary>
    public ChalkViolation(string code, string message, string? kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentNullException.ThrowIfNull(message);
        Code = code;
        Message = message;
        Kind = kind;
    }

    /// <summary>
    /// The rule, as the glossary names it: one of <see cref="ChalkErrorCodes"/>, or a newer one this
    /// client has no constant for, kept as it was sent.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// What kind of problem the planner says it is, such as <c>Parse</c>, <c>Validation</c> or
    /// <c>Policy</c>; <c>PlanErrorKinds</c> in <c>Chalk.Client</c> lists them. Null for a violation
    /// the client found itself.
    /// </summary>
    public string? Kind { get; }

    /// <summary>What happened, where, and what to do, without the code the exception's message ends with.</summary>
    public string Message { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Message} [{Code}]";
}
