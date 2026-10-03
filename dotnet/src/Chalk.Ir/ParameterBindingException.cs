namespace Chalk;

/// <summary>
/// A value bound to a parameter, a context scalar or a context binding's column that the declared
/// type does not hold exactly, or a binding that does not match the parameters a plan declares
/// (D317, design 61). Refused at binding, before anything runs.
/// </summary>
/// <remarks>
/// The message names what was bound and the CLR type bound to it, never the value: a value is the
/// caller's data, and a refusal is a log line.
/// </remarks>
public sealed class ParameterBindingException : ChalkException
{
    public ParameterBindingException(string subject, string message)
        : this(subject, message, innerException: null)
    {
    }

    /// <summary>The same, wrapping what failed: a host's converter that threw, say.</summary>
    public ParameterBindingException(string subject, string message, Exception? innerException)
        : base(ChalkErrorCodes.ParameterBinding, message, innerException)
    {
        Subject = subject;
    }

    /// <summary>
    /// What was bound, as the message names it: <c>Parameter @amount</c>, <c>Context scalar 'tenant'</c>.
    /// </summary>
    public string Subject { get; }
}
