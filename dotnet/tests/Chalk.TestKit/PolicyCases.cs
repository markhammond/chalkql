namespace Chalk.TestKit;

/// <summary>
/// The entitlement conformance battery of <c>corpus/policy</c>, as code
/// (<c>docs/design/16-entitlements.md</c> §7; <c>corpus/policy/README.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// Two hundred and twenty-five cases: a statement, a principal, a catalog, and the rows, labels,
/// acknowledgements and refusals it must produce. Every expected row was computed by hand from the
/// fixture under the entitlements and the principal's context, which is what makes the battery worth
/// running at all — a golden can only tell you the engine changed, and this can tell you it is
/// wrong.
/// </para>
/// <para>
/// Where a case and the engine disagree the case is <b>not</b> edited to match. The disagreement is
/// the finding: it is judged, and the judgement is written on the case as a
/// <see cref="PolicyAdjudication"/> with both answers. <c>PolicyBatteryTests</c> then holds the set
/// exactly — a case that starts disagreeing without one fails, and so does one that carries one and
/// now agrees.
/// </para>
/// <para>
/// <b>To add a case:</b> write the statement in <c>corpus/policy/cases/&lt;nnn&gt;-&lt;slug&gt;.sql</c>,
/// add a <see cref="PolicyBatteryCase"/> to <see cref="Corpus"/> (or to <see cref="Calcite"/> for a
/// case that guards a named Calcite defect) naming that file, and state what it must produce. State
/// only what differs from <see cref="PolicyCatalog.Default"/> and
/// <see cref="PolicyCaseOptions.Default"/>. If the engine disagrees, judge the disagreement and put
/// the judgement on the case — never on the expectation.
/// </para>
/// </remarks>
public static partial class PolicyCases
{
    /// <summary>Every case, the battery's own first and then the Calcite guards.</summary>
    public static IReadOnlyList<PolicyBatteryCase> All { get; } = [.. Corpus, .. Calcite];

    /// <summary>The case with that id.</summary>
    public static PolicyBatteryCase Find(string id) =>
        All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"the battery declares no case '{id}'.");

    /// <summary>
    /// <c>corpus/policy/README.md</c> §5's uncertainties, the cases each one rides on, and what the
    /// run answered — recorded beside the cases rather than left as a question in a file nobody
    /// re-reads. <c>PolicyBatteryTests</c> recomputes each verdict from the run and holds it against
    /// what is written here, so an uncertainty that changes its answer is a failing test.
    /// </summary>
    public static IReadOnlyList<PolicyUncertainty> Uncertainties { get; } =
    [
        new("U1", ["005", "038", "110", "133"], PolicyVerdict.Mixed,
            "a placeholder, the column reported Redacted. Confirmed wherever no rule can match; 038 "
            + "differs for another reason — a subject rule stays reachable, so it is PerRow."),
        new("U2", ["052"], PolicyVerdict.Disputed,
            "Masked. A count over a masked column takes its origin's label: the meet of §3.12 is "
            + "over origins, and the engine's answer is the conservative one."),
        new("U3", ["007"], PolicyVerdict.Agreed,
            "declaration order decides, as first-match-wins says it must. The agent's rule precedes "
            + "the auditor's, so u7 sees initials rather than tokens."),
        new("U4", ["013"], PolicyVerdict.Agreed,
            "both groups guarded. Taint is a property of the column at a leaf and not of a row."),
        new("U5", ["077"], PolicyVerdict.Agreed,
            "the remedy works: the pass gathers the conjuncts of a filter standing over the leaf, so "
            + "WHERE org_id = 2 makes the folded disclosure constant (V63)."),
        new("U6", ["085", "086"], PolicyVerdict.Agreed,
            "settled by D211 and confirmed: an effective floor of one or less emits no guard. The "
            + "floor-off variant says so through the case's own DefaultMinGroupSize."),
        new("U7", ["087"], PolicyVerdict.Agreed,
            "the guard sits immediately above the aggregate, so HAVING tests the guarded value and a "
            + "suppressed group drops on UNKNOWN."),
        new("U8", ["101"], PolicyVerdict.Agreed,
            "the corpus is right and the option has two members: StarExpansion is about the columns "
            + "a star's expansion surfaces, so a named column keeps §3.11's placeholder, and the "
            + "host that would rather be told says Redaction.NamedColumns.Refuse (D217, case 187)."),
        new("U9", ["159"], PolicyVerdict.Disputed,
            "true, and §3.7 is right: a list above the fold ceiling now reaches the source as M5's "
            + "key set, so the flag the case declared is what the engine says (F45). The 70 keys go "
            + "in one call, because the DuckDB profile's `max_in_list` is 1000. What is still "
            + "disputed is the row count, which is the fixture's own error."),
        new("U10", ["157"], PolicyVerdict.Agreed,
            "PerRow, and F44 is fixed. Under trust_source_row_level_security one origin is both masked "
            + "(inside the scope) and a placeholder (outside it), and §3.12's meet resolves that to "
            + "PerRow. The pass had been simplifying the rules under a row predicate it does not "
            + "emit for a trusted source, which folded the agent's rule to a constant and masked "
            + "every row the source returned — including rows outside the scope. It now simplifies "
            + "over all the rows the source returns, which is what they are."),
        new("U11", ["160"], PolicyVerdict.Agreed,
            "Aggregate. A k-anonymous raw group key is a value the guard stands behind, which is what "
            + "the name is for."),
        new("U12", ["166"], PolicyVerdict.Agreed,
            "as written: group suppression on a global aggregate returns no row at all. Surprising, "
            + "and it is what D203's `dropped, not NULLed` means."),
        new("U13", ["023", "033", "089", "090"], PolicyVerdict.Agreed,
            "the scale is Calcite's and the battery compares numerically, which settles it. 089 and "
            + "090 used to disagree about COUNT's label and not about the scale — COUNT of a NOT "
            + "NULL column is COUNT(*) by the time the pass sees it (F43) — and they agree from the "
            + "F58 run onwards: a column a rule of the entitlement can withhold is nullable in the "
            + "row type a statement is written over, so COUNT's argument is not erased (ADR 0038)."),
        new("U14", ["079"], PolicyVerdict.Agreed,
            "the design's reading is the right one and the engine now does it: "
            + "COUNT(CAST(amount AS VARCHAR)) is refused, because `amount` is nullable where the "
            + "statement can see it and §3.4's trace therefore meets the cast rather than a COUNT "
            + "whose argument the converter had erased (ADR 0038)."),
        new("U15", ["107"], PolicyVerdict.Disputed,
            "the catalog-wide refusal names the table the star stood over, which is the case's own "
            + "guess and the only useful thing to name."),
        new("U16", ["115"], PolicyVerdict.Disputed,
            "unanswered: the statement is refused by name, as the LATERAL shape Calcite's "
            + "decorrelator gets wrong (ADR 0026)."),
        new("U17", ["138"], PolicyVerdict.Disputed,
            "the report does simplify under the statement's own filter: Masked, not PerRow. Same "
            + "mechanism as U5, seen from the report's side."),
        new("U18", ["155"], PolicyVerdict.Agreed,
            "as the case's own note predicted: a one-org list simplifies to an EQ the profile does "
            + "declare, so the refusal did not fire for u2. Moved to u11, two organisations, where "
            + "it does — and the message now names the shape the host declared and did not, "
            + "PREDICATE_SHAPE_IN, rather than Calcite's SEARCH over the principal's own tenancy "
            + "identifiers (F46)."),
        new("U19", ["168"], PolicyVerdict.Disputed,
            "PerRow for every entitled column, as the note suspected — nothing folded, so nothing can "
            + "be constant. The rows equal the prepare-time case exactly, which is the assertion that "
            + "mattered."),
        new("U20", ["171", "172", "173", "174", "175", "176"], PolicyVerdict.Mixed,
            "the sibling's own reported disclosure is Full, as encoded, and its values are the lower "
            + "snake `full`, `masked`, `redacted` the Arrow metadata already carries — one word "
            + "whichever side a consumer reads it from (D218). 174 disagrees about something else."),
        new("U21", ["174"], PolicyVerdict.Disputed,
            "POLICY, as encoded. The message names the column and the suffix and not a table, which "
            + "is right: the clash is between two output names."),
    ];
}
