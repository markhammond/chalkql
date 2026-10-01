using Chalk.Client;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;
using Rpc = Chalk.Client.Rpc;

namespace Chalk.Entitlements;

/// <summary>
/// Why the entitlements refused a statement (D329): the refusal's code, as an enum a host can
/// switch over. Each reason is one code; <see cref="EntitlementRefusal.Code"/> carries the code
/// itself.
/// </summary>
public enum RefusalReason
{
    /// <summary>A code this client has no reason for: a planner newer than it refused for something else.</summary>
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
        string code,
        string? table,
        string? column,
        string? use,
        IReadOnlyList<string> permitted,
        ArrowSchema? outputSchema)
    {
        Code = code;
        Reason = ReasonOf(code);
        Table = table;
        Column = column;
        Use = use;
        Permitted = permitted;
        OutputSchema = outputSchema;
    }

    /// <summary>The rule refused, as the glossary names it: <see cref="ChalkErrorCodes.Star"/>, say.</summary>
    public string Code { get; }

    /// <summary>Why, as the reason <see cref="Code"/> is; <see cref="RefusalReason.Unspecified"/> for a code this client has no reason for.</summary>
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
    /// The refusals a planner's error describes, one for each rule it breaks, in its order. They
    /// share one table model, the error's. STRING columns declare the layout the engine accepts;
    /// where it accepts any, which the compiler would settle from a plan a refused statement does not
    /// have, they declare <see cref="StringLayouts.Utf8View"/>, the default.
    /// </summary>
    internal static IReadOnlyList<EntitlementRefusal> From(Rpc.PlanError wire, StringLayouts strings)
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

        return
        [
            .. wire.Violations
                .Where(violation => violation.Code.Length > 0)
                .Select(violation => new EntitlementRefusal(
                    violation.Code,
                    violation.Table.Length == 0 ? null : violation.Table,
                    violation.Column.Length == 0 ? null : violation.Column,
                    violation.Use.Length == 0 ? null : violation.Use,
                    [.. violation.Permitted],
                    schema)),
        ];
    }

    /// <summary>The reason a refusal's code is, the one each refusal code has.</summary>
    internal static RefusalReason ReasonOf(string code) => code switch
    {
        ChalkErrorCodes.PopulationOnly => RefusalReason.PopulationOnly,
        ChalkErrorCodes.Statistical => RefusalReason.Statistical,
        ChalkErrorCodes.Redacted => RefusalReason.Redacted,
        ChalkErrorCodes.Star => RefusalReason.Star,
        ChalkErrorCodes.NoVisibleRows => RefusalReason.NoVisibleRows,
        ChalkErrorCodes.PushdownRequired => RefusalReason.PushdownRequired,
        ChalkErrorCodes.ContextRequired => RefusalReason.NoContext,
        ChalkErrorCodes.EntitlementBinding => RefusalReason.Binding,
        ChalkErrorCodes.InvalidEntitlement => RefusalReason.InvalidEntitlement,
        ChalkErrorCodes.DisclosureNameCollision => RefusalReason.NameCollision,
        ChalkErrorCodes.Internal => RefusalReason.Internal,
        _ => RefusalReason.Unspecified,
    };
}
