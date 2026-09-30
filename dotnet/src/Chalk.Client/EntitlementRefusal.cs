using Chalk.Client;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;
using Rpc = Chalk.Client.Rpc;

namespace Chalk.Entitlements;

/// <summary>
/// Why the entitlements refused a statement (D329). A twin of the wire's
/// <c>PolicyRefusalReason</c>, value for value, as <c>StarPolicy</c> is of its own.
/// </summary>
public enum RefusalReason
{
    /// <summary>A reason this client does not know: a planner newer than it said something else.</summary>
    Unspecified = 0,

    /// <summary>
    /// A column the principal may only aggregate, used some other way — returned as a value,
    /// compared, grouped or sorted by. <see cref="EntitlementRefusal.Permitted"/> names the aggregates
    /// it allows.
    /// </summary>
    PopulationOnly = 1,

    /// <summary>
    /// A statistical column read under a window, or a declared unique key of its table compared or
    /// grouped by.
    /// </summary>
    Statistical = 2,

    /// <summary>A column that discloses nothing, under a redaction policy that refuses it.</summary>
    Redacted = 3,

    /// <summary>A star, or a <c>ROW</c> over a table's columns, under a star policy that refuses it.</summary>
    Star = 4,

    /// <summary>A statement that can return no row, under <c>RefuseWhenNoVisibleRows</c>.</summary>
    NoVisibleRows = 5,

    /// <summary>A row predicate declared <c>PUSHDOWN_REQUIRED</c> that the plan would evaluate here.</summary>
    PushdownRequired = 6,

    /// <summary>A prepare that bound no context against a catalog that carries an entitlement.</summary>
    NoContext = 7,

    /// <summary>An entitlement this request's binding time cannot apply.</summary>
    Binding = 8,

    /// <summary>An entitlement that does not parse, does not type-check, or does not fit its table.</summary>
    InvalidEntitlement = 9,

    /// <summary>A disclosure column whose name the statement already produces.</summary>
    NameCollision = 10,

    /// <summary>A guard the planner holds itself to. Reaching one is a bug, to be reported.</summary>
    Internal = 11,
}

/// <summary>
/// What the entitlements refused, and the result the statement would have had (D329): the table
/// model a host can still return beside the error, in the same form as
/// <see cref="PreparedQuery.OutputSchema"/>.
/// </summary>
/// <remarks>
/// <para>
/// Immutable, and the same instance for every refusal of the same request an engine answers from
/// memory (D330), so a host that keeps it keeps one copy.
/// </para>
/// <para>
/// <see cref="OutputSchema"/> describes the statement's own columns as the policy shapes them — a
/// withheld column is nullable — with <c>chalk.disclosure</c> on each field whenever any of them
/// discloses less than the value, exactly as a prepared statement's schema carries it. Disclosure
/// sibling columns and <c>Omit</c>'s trimming are for a result that runs, and are not applied. It is
/// null where the refusal came before the columns were decided: a star refused on the parse tree, an
/// entitlement that does not parse.
/// </para>
/// </remarks>
public sealed class EntitlementRefusal
{
    internal EntitlementRefusal(
        RefusalReason reason,
        string? table,
        string? column,
        string? use,
        IReadOnlyList<string> permitted,
        ArrowSchema? outputSchema)
    {
        Reason = reason;
        Table = table;
        Column = column;
        Use = use;
        Permitted = permitted;
        OutputSchema = outputSchema;
    }

    /// <summary>Why.</summary>
    public RefusalReason Reason { get; }

    /// <summary>The table the refusal is about, as <c>schema.table</c>, or null where it is about none.</summary>
    public string? Table { get; }

    /// <summary>The column the refusal is about, or null where it is about none.</summary>
    public string? Column { get; }

    /// <summary>
    /// What the statement did that was refused, in the message's own words — "a projection to the
    /// result", "SUM", "a window function" — or null where the reason says it all.
    /// </summary>
    public string? Use { get; }

    /// <summary>
    /// What the column does permit this principal: the aggregates a
    /// <see cref="RefusalReason.PopulationOnly"/> column allows. Empty for every other reason.
    /// </summary>
    public IReadOnlyList<string> Permitted { get; }

    /// <summary>
    /// The statement's result as the policy would shape it, or null where the refusal came before
    /// the columns were decided.
    /// </summary>
    public ArrowSchema? OutputSchema { get; }

    /// <summary>
    /// The public refusal the wire's describes. STRING columns declare the layout the engine
    /// accepts; where it accepts any, which the compiler would settle from a plan a refused statement
    /// does not have, they declare <see cref="StringLayouts.Utf8View"/>, the default.
    /// </summary>
    internal static EntitlementRefusal From(Rpc.PolicyRefusal wire, StringLayouts strings)
    {
        ArrowSchema? schema = null;
        if (wire.Output is { Fields.Count: > 0 } output)
        {
            var layout = strings == StringLayouts.Any ? StringLayouts.Utf8View : strings;
            var layouts = new StringLayouts[output.Fields.Count];
            Array.Fill(layouts, layout);
            schema = DisclosureLabels.Decorate(
                ArrowTypeMapping.ToArrowSchema(output, layouts), wire.Disclosures);
        }

        return new EntitlementRefusal(
            ReasonOf(wire.Reason),
            wire.Table.Length == 0 ? null : wire.Table,
            wire.Column.Length == 0 ? null : wire.Column,
            wire.Use.Length == 0 ? null : wire.Use,
            [.. wire.Permitted],
            schema);
    }

    private static RefusalReason ReasonOf(Rpc.PolicyRefusalReason reason) => reason switch
    {
        Rpc.PolicyRefusalReason.PopulationOnly => RefusalReason.PopulationOnly,
        Rpc.PolicyRefusalReason.Statistical => RefusalReason.Statistical,
        Rpc.PolicyRefusalReason.Redacted => RefusalReason.Redacted,
        Rpc.PolicyRefusalReason.Star => RefusalReason.Star,
        Rpc.PolicyRefusalReason.NoVisibleRows => RefusalReason.NoVisibleRows,
        Rpc.PolicyRefusalReason.PushdownRequired => RefusalReason.PushdownRequired,
        Rpc.PolicyRefusalReason.NoContext => RefusalReason.NoContext,
        Rpc.PolicyRefusalReason.Binding => RefusalReason.Binding,
        Rpc.PolicyRefusalReason.InvalidEntitlement => RefusalReason.InvalidEntitlement,
        Rpc.PolicyRefusalReason.NameCollision => RefusalReason.NameCollision,
        Rpc.PolicyRefusalReason.Internal => RefusalReason.Internal,
        _ => RefusalReason.Unspecified,
    };
}
