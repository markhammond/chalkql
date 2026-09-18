namespace Chalk;

/// <summary>
/// Base of every exception Chalk raises on purpose. Lives in <c>Chalk.Ir</c> because that is the
/// bottom of the package graph and every other package needs to derive from it (ADR 0005).
/// </summary>
/// <remarks>
/// Messages are self-contained: what happened, where (SQL position, operator path, table) and what
/// to do about it. See <c>docs/design/04-client.md</c> §10.
/// </remarks>
public abstract class ChalkException : Exception
{
    protected ChalkException(string message)
        : base(message)
    {
    }

    protected ChalkException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
