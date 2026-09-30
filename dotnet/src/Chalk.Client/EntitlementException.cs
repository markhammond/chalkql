using Chalk.Client;
using Chalk.Client.Rpc;

namespace Chalk.Entitlements;

/// <summary>
/// Valid SQL the entitlements refuse (<c>docs/design/16-entitlements.md</c> §3.12, D143): a use of a
/// column no disclosure could permit for this principal, a star under a refusing
/// <c>StarPolicy</c>, a redacted column under <c>RedactedColumns.Refuse</c>, or a table this
/// principal holds no grant on under <c>RefuseWhenNoVisibleRows</c>.
/// </summary>
/// <remarks>
/// <para>
/// Its own type because a host handles it differently from a broken statement: the SQL is well
/// formed and the planner could express it perfectly well, so the answer is to write a different
/// statement or to hold a different grant, not to fix a typo. The message names the table, the
/// column and the use.
/// </para>
/// <para>
/// A sibling of <see cref="PlanningException"/> rather than a kind of it, because D26 keeps every
/// public class in this package sealed. A host that wants both catches
/// <see cref="Chalk.Ir.ChalkException"/>, which is the common base of everything Chalk throws.
/// </para>
/// <para>
/// It lives in the <c>Chalk.Client</c> assembly under this namespace (step 26c, D213): <c>POLICY</c>
/// is a core error kind that any extension's handler may raise, so the transport that turns a
/// planner status into an exception has to be able to throw it, whether or not the host references
/// <c>Chalk.Entitlements</c>. Every other entitlement type on the client side is in that assembly.
/// </para>
/// </remarks>
public sealed class EntitlementException : ChalkException
{
    public EntitlementException(string message, SqlPosition? position, Exception? innerException = null)
        : this(message, position, refusal: null, innerException)
    {
    }

    /// <summary>The same, carrying what was refused and the result the statement would have had.</summary>
    internal EntitlementException(
        string message, SqlPosition? position, EntitlementRefusal? refusal, Exception? innerException)
        : base(
            position is { Line: > 0 }
                ? $"Refused by the entitlements at {position}: {message}"
                : $"Refused by the entitlements: {message}",
            innerException)
    {
        Position = position;
        PlannerMessage = message;
        Refusal = refusal;
    }

    /// <summary>Always <see cref="PlanErrorKind.Policy"/>, so the two exceptions read alike.</summary>
    public PlanErrorKind Kind => PlanErrorKind.Policy;

    /// <summary>Where in the SQL, when the planner reported it.</summary>
    public SqlPosition? Position { get; }

    /// <summary>
    /// What was refused and the result the statement would have had (D329): the reason, the table,
    /// column and use, and the table model in the same form as a prepared statement's schema. Null
    /// where the planner said nothing more than the message, as one older than this client does.
    /// </summary>
    public EntitlementRefusal? Refusal { get; internal set; }

    /// <summary>The planner's own message, before this exception's prefix, for a refusal answered again.</summary>
    internal string PlannerMessage { get; }

    /// <summary>The refusal as the wire carried it, which the engine turns into <see cref="Refusal"/>.</summary>
    internal PolicyRefusal? WireRefusal { get; init; }
}
