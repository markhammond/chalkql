namespace Chalk;

/// <summary>
/// Base of every exception Chalk raises on purpose. Lives in <c>Chalk.Ir</c> because that is the
/// bottom of the package graph and every other package needs to derive from it (ADR 0005).
/// </summary>
/// <remarks>
/// Messages are self-contained: what happened, where (SQL position, operator path, table), the rule
/// it meets and what to do about it, naming nothing a host cannot see. See
/// <c>docs/design/04-client.md</c> §10 and design 61. Each ends with its <see cref="Code"/> in
/// brackets, so a log line carries the code a host can look up and branch on.
/// </remarks>
public abstract class ChalkException : Exception
{
    /// <summary>An error that breaks the rule <paramref name="code"/>, one of <see cref="ChalkErrorCodes"/>.</summary>
    protected ChalkException(string code, string message)
        : this(code, message, innerException: null)
    {
    }

    /// <summary>The same, wrapping what failed.</summary>
    protected ChalkException(string code, string message, Exception? innerException)
        : this([new ChalkViolation(code, message)], message, innerException)
    {
    }

    /// <summary>
    /// An error that breaks every rule in <paramref name="violations"/>, in the order they were
    /// found, worded by <paramref name="message"/>, which ends with the first one's code.
    /// </summary>
    protected ChalkException(IReadOnlyList<ChalkViolation> violations, string message, Exception? innerException)
        : base(WithCode(violations.Count > 0 ? violations[0].Code : null, message), innerException)
    {
        Violations = violations;
    }

    /// <summary>The same, breaking no rule a code names.</summary>
    protected ChalkException(string message)
        : this([], message, innerException: null)
    {
    }

    /// <summary>The same, breaking no rule a code names, wrapping what failed.</summary>
    protected ChalkException(string message, Exception? innerException)
        : this([], message, innerException)
    {
    }

    /// <summary>
    /// Every rule this error breaks, in the order they were found. ChalkQL stops at the first today,
    /// so there is one, or none for an error raised without a code.
    /// </summary>
    public IReadOnlyList<ChalkViolation> Violations { get; }

    /// <summary>
    /// The first rule this error breaks, as the glossary names it: one of <see cref="ChalkErrorCodes"/>,
    /// or null when it names none. A host can branch on it, or log it without the names the message
    /// carries.
    /// </summary>
    public string? Code => Violations.Count > 0 ? Violations[0].Code : null;

    /// <summary>
    /// A message ending with its code in brackets, or as it is when there is none, or when it already
    /// ends with it: an exception that passes on the message of one it wraps, under the same code.
    /// </summary>
    private static string WithCode(string? code, string message) =>
        code is null || message.EndsWith($"[{code}]", StringComparison.Ordinal)
            ? message
            : $"{message} [{code}]";
}
