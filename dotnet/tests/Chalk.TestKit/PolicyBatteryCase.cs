using Chalk.Entitlements;
using Chalk.Sources.Conformance;

namespace Chalk.TestKit;

/// <summary>
/// One case of the entitlement conformance battery of <c>corpus/policy</c>, as code
/// (<c>docs/design/16-entitlements.md</c> §7; <c>corpus/policy/README.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// The battery was written as YAML and is now written here, because test configuration belongs in
/// code: a catalog variant, a principal, an option, a column name and a disclosure are all names
/// something else declares, and a rename that misses one of them should stop the build rather than
/// produce a case that quietly tests nothing. The statements themselves stay in
/// <c>corpus/policy/cases/*.sql</c> and are named by <see cref="File"/>, as the corpus wrote them.
/// </para>
/// <para>
/// A case is turned into the conformance kit's own <see cref="BatteryCase"/> to be run
/// (<see cref="ToBatteryCase"/>): the kit is a shipped package an adapter author references, so it
/// keeps the string vocabulary an adapter's own battery would use, and the typing is on this side,
/// where the names belong to this repository.
/// </para>
/// </remarks>
public sealed record PolicyBatteryCase
{
    /// <summary>The case number — three digits, or <c>c</c> and two for the Calcite guards.</summary>
    public required string Id { get; init; }

    /// <summary>The coverage group of <c>corpus/policy/README.md</c> §1.</summary>
    public required string Group { get; init; }

    /// <summary>What the case is for: the design section, decision or CALCITE key it guards.</summary>
    public string Covers { get; init; } = "";

    /// <summary>The statement, relative to <c>corpus/policy</c>.</summary>
    public required string File { get; init; }

    /// <summary>The principal, by the name <see cref="PolicyPrincipals"/> declares.</summary>
    public required string Principal { get; init; }

    /// <summary>Prepare-time binding, or execute-time (D209).</summary>
    public PolicyBinding Binding { get; init; } = PolicyBinding.Prepare;

    /// <summary>Which source the fixture is loaded into for this case.</summary>
    public PolicySourceProfile Source { get; init; } = PolicySourceProfile.Poco;

    /// <summary>True for a case whose expectation is that registration itself fails.</summary>
    public bool AtRegistration { get; init; }

    /// <summary>The entitlement each table carries for this case.</summary>
    public PolicyCatalog Catalog { get; init; } = PolicyCatalog.Default;

    /// <summary>The options this case prepares under.</summary>
    public PolicyCaseOptions Options { get; init; } = PolicyCaseOptions.Default;

    /// <summary>Whether the statement asks for a total order.</summary>
    public PolicyCompare Compare { get; init; } = PolicyCompare.Multiset;

    /// <summary>What the case expects.</summary>
    public required PolicyExpectation Expect { get; init; }

    /// <summary>The case's own note: what it is for, and where its hand computation was uncertain.</summary>
    public string Notes { get; init; } = "";

    /// <summary>
    /// Set when the engine and this case disagree and the disagreement has been judged
    /// (ADR 0025). A case carrying one must disagree; a case without one must agree.
    /// </summary>
    public PolicyAdjudication? Disputed { get; init; }

    /// <summary>Whether this run executes the case at all — a layer-B check has no layer-A answer.</summary>
    public bool LayerBOnly { get; init; }

    public override string ToString() => $"{Id} {Principal}";

    /// <summary>This case as the conformance kit's own vocabulary, with the statement read.</summary>
    public BatteryCase ToBatteryCase(string sql)
    {
        var asserts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        Expect.Claims.Declare(asserts);

        return new BatteryCase
        {
            Id = Id,
            Group = Group,
            File = File,
            Sql = sql,
            Principal = Principal,
            Binding = Binding == PolicyBinding.Execute ? "execute" : "prepare",
            Source = Source.Name(),
            Phase = AtRegistration ? "registration" : "",
            Catalog = Catalog.ByTable(),
            Options = new Dictionary<string, string>(StringComparer.Ordinal),
            Compare = Compare == PolicyCompare.Ordered ? "ordered" : "multiset",
            Notes = Notes,
            Expect = new BatteryExpectation
            {
                Rows = Expect.Rows,
                Columns = Expect.Labels.ToDictionary(
                    e => e.Key, e => e.Value.ToString(), StringComparer.Ordinal),
                Report = Expect.Report.ToDictionary(
                    e => e.Key, e => e.Value.Claims(), StringComparer.Ordinal),
                Error = Expect.Refusal?.ToBatteryError(),
                Asserts = asserts,
            },
        };
    }
}

/// <summary>When the principal's values are bound (D209).</summary>
public enum PolicyBinding
{
    /// <summary>At prepare, which folds them into the plan.</summary>
    Prepare,

    /// <summary>At execution, over a plan prepared from the shape alone.</summary>
    Execute,
}

/// <summary>Which source profile of <c>corpus/policy/README.md</c> §3 the fixture is loaded into.</summary>
public enum PolicySourceProfile
{
    /// <summary>In-process collections: no source boundary, so nothing is pushed.</summary>
    Poco,

    /// <summary>DuckDB through the in-box ADO.NET source, declaring the usual predicate shapes.</summary>
    AdoDuckDb,

    /// <summary>DuckDB declaring no <c>IN</c>, for <c>PUSHDOWN_REQUIRED</c> (design corpus 21).</summary>
    AdoDuckDbNoIn,

    /// <summary>DuckDB the host trusts the row security of (D156), so <c>Filter_R</c> is skipped.</summary>
    AdoDuckDbTrusted,

    /// <summary>PostgreSQL, so the generated SQL is a dialect nobody wrote by hand.</summary>
    AdoPostgres,
}

/// <summary>How the rows compare (<c>docs/design/05-testing.md</c> §5).</summary>
public enum PolicyCompare
{
    /// <summary>Order is not asserted.</summary>
    Multiset,

    /// <summary>The statement asks for a total order and the rows are compared in it.</summary>
    Ordered,
}

/// <summary>The kind a refusal must carry.</summary>
public enum PolicyErrorKind
{
    /// <summary>Refused by the entitlements: <c>PlanErrorKind.POLICY</c>.</summary>
    Policy,

    /// <summary>Refused by the planner's own validation.</summary>
    Validation,

    /// <summary>Refused when the catalog was registered.</summary>
    InvalidCatalog,

    /// <summary>Failed while the plan was running.</summary>
    Execution,
}

/// <summary>
/// What a raw or redacted value was used for, in the battery's own vocabulary
/// (<c>corpus/policy/README.md</c> §4). The design names the uses in prose rather than as an enum,
/// so this is the corpus's reading of them and a refusal is matched loosely against it.
/// </summary>
public enum PolicyUse
{
    /// <summary>The case does not say.</summary>
    None,

    /// <summary>A projection to the result.</summary>
    Projection,

    /// <summary>Any expression over the value.</summary>
    Expression,

    /// <summary>A <c>WHERE</c> or <c>HAVING</c> condition.</summary>
    Filter,

    /// <summary>A join condition.</summary>
    JoinCondition,

    /// <summary>A <c>GROUP BY</c> key.</summary>
    GroupingKey,

    /// <summary>An <c>ORDER BY</c> key.</summary>
    SortKey,

    /// <summary>A window function's argument or key.</summary>
    WindowAggregate,

    /// <summary>An aggregate's <c>FILTER (WHERE …)</c>.</summary>
    AggregateFilter,

    /// <summary>An aggregate outside the column's allow-list.</summary>
    AggregateNotAllowListed,

    /// <summary>An aggregate that is not a population aggregate (D190).</summary>
    AggregateNotPopulation,

    /// <summary>A <c>CAST</c> to something other than a numeric type.</summary>
    CastNotNumeric,

    /// <summary>A star, under a refusing <see cref="StarPolicy"/>.</summary>
    Star,

    /// <summary>The statement's shape, under <c>statistical</c> (D203).</summary>
    Shape,

    /// <summary>No visible row, under <c>RefuseWhenNoVisibleRows</c>.</summary>
    NoVisibleRows,

    /// <summary>The statement's tenancy conjunct is disjoint from the principal's scope.</summary>
    Contradiction,

    /// <summary>Pinning an individual by a declared unique key (D203).</summary>
    PinUniqueKey,

    /// <summary>A row-level output in an aggregate-only statement (D203).</summary>
    RowLevelOutput,

    /// <summary>A window in an aggregate-only statement (D203).</summary>
    WindowInStatistical,

    /// <summary>A sibling column's name the statement already produces (D207).</summary>
    DisclosureSuffixCollision,

    /// <summary>A global grant where the package does not allow one (D205).</summary>
    GlobalGrantNotAllowed,

    /// <summary>A redacted column the statement named, under <c>Redaction.NamedColumns.Refuse</c>.</summary>
    NamedRedacted,
}

/// <summary>
/// The entitlement each table of the fixture carries for one case. Five names, because the fixture
/// has five entitled tables and a case that names a sixth is a case naming something that is not
/// there.
/// </summary>
public sealed record PolicyCatalog
{
    /// <summary>What a case takes when it says nothing (<c>corpus/policy/README.md</c> §3 step 2).</summary>
    public static PolicyCatalog Default { get; } = new();

    public string Members { get; init; } = PolicyEntitlements.Members;

    public string Orders { get; init; } = PolicyEntitlements.Orders;

    public string Notes { get; init; } = PolicyEntitlements.Notes;

    public string Invites { get; init; } = PolicyEntitlements.Invites;

    public string Symbols { get; init; } = PolicyEntitlements.SymbolsUnentitled;

    /// <summary>D265 clause (h)'s endpoint, where the vendor kind lives directly (group U).</summary>
    public string Vendors { get; init; } = PolicyEntitlements.Vendors;

    /// <summary>The endpoint of every vendor path: the key is on the item's own row.</summary>
    public string Items { get; init; } = PolicyEntitlements.Items;

    /// <summary>The bridge, entitled <b>by kind</b>: its order for two kinds, its item for one.</summary>
    public string OrderItems { get; init; } = PolicyEntitlements.OrderItems;

    /// <summary>The same, keyed by the fixture's table names, which is what the loader wants.</summary>
    public IReadOnlyDictionary<string, string> ByTable() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["members"] = Members,
            ["orders"] = Orders,
            ["notes"] = Notes,
            ["invites"] = Invites,
            ["symbols"] = Symbols,
            ["vendors"] = Vendors,
            ["items"] = Items,
            ["order_items"] = OrderItems,
        };
}

/// <summary>
/// The options one case prepares under (<c>docs/design/16-entitlements.md</c> §6). The defaults are
/// the fixture's own, so a case states only what it changes.
/// </summary>
public sealed record PolicyCaseOptions
{
    /// <summary>The fixture's defaults: the corpus's <c>catalog_options</c> block, as code.</summary>
    public static PolicyCaseOptions Default { get; } = new();

    /// <summary>Whether a star may name an entitled table (D160).</summary>
    public StarPolicy StarPolicy { get; init; } = StarPolicy.Allow;

    /// <summary>
    /// What a column no disclosure permits becomes, in the two places the question arises — what a
    /// star's expansion surfaced, and what the statement named (D161, D217 as amended).
    /// </summary>
    public RedactionPolicy Redaction { get; init; } = new();

    /// <summary>What a placeholder holds (D162).</summary>
    public PlaceholderPolicy PlaceholderPolicy { get; init; } = PlaceholderPolicy.PlaceholdersAsNull;

    /// <summary>Turn "no visible row" into a refusal at prepare (D207).</summary>
    public bool RefuseWhenNoVisibleRows { get; init; }

    /// <summary>Emit a sibling column per entitled output column (D207).</summary>
    public bool IncludeDisclosureColumns { get; init; }

    /// <summary>What a sibling's name is its column's name plus.</summary>
    public string DisclosureColumnSuffix { get; init; } = "__disclosure";

    /// <summary>The group-size floor the host gives at planning (D211). The fixture's is five.</summary>
    public int DefaultMinGroupSize { get; init; } = 5;

    /// <summary>A dialect of its own, for the case that asks for one (D34).</summary>
    public bool Lenient { get; init; }
}

/// <summary>A refusal a case expects, in the battery's own vocabulary.</summary>
public sealed record PolicyRefusal
{
    /// <summary>Refused by the entitlements, naming a table, a column and what the value was for.</summary>
    public static PolicyRefusal Policy(string table, string column, PolicyUse use) =>
        new() { Kind = PolicyErrorKind.Policy, Table = table, Column = column, Use = use };

    public required PolicyErrorKind Kind { get; init; }

    /// <summary>The table the message must name, where the case says one.</summary>
    public string Table { get; init; } = "";

    /// <summary>The column the message must name, where the case says one.</summary>
    public string Column { get; init; } = "";

    /// <summary>What the value was used for; matched loosely, since it is the corpus's word.</summary>
    public PolicyUse Use { get; init; } = PolicyUse.None;

    /// <summary>A substring the message must carry, where the case names one.</summary>
    public string Detail { get; init; } = "";

    internal BatteryError ToBatteryError() => new()
    {
        Kind = Kind switch
        {
            PolicyErrorKind.Validation => "VALIDATION",
            PolicyErrorKind.InvalidCatalog => "INVALID_CATALOG",
            PolicyErrorKind.Execution => "EXECUTION",
            _ => "POLICY",
        },
        Table = Table,
        Column = Column,
        Use = Use == PolicyUse.None ? "" : Use.ToString(),
        Detail = Detail,
    };
}

/// <summary>What the report must say about one entitled table (§3.12).</summary>
public sealed record PolicyTableExpectation
{
    /// <summary>NONE, SOME or ALL; null when the case does not say.</summary>
    public TableVisibility? Visibility { get; init; }

    /// <summary>Whether the row predicate reached the source (§3.7); null when the case does not say.</summary>
    public bool? RowPredicatePushed { get; init; }

    /// <summary>Whether the statement's tenancy conjunct is disjoint from the scope; null for silent.</summary>
    public bool? Contradiction { get; init; }

    /// <summary>The column the contradiction is about.</summary>
    public string ContradictionColumn { get; init; } = "";

    internal IReadOnlyDictionary<string, string> Claims()
    {
        var claims = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Visibility is { } visibility)
        {
            claims["visibility"] = visibility.ToString().ToUpperInvariant();
        }

        if (RowPredicatePushed is { } pushed)
        {
            claims["row_predicate_pushed"] = pushed ? "true" : "false";
        }

        if (Contradiction is { } contradiction)
        {
            claims["contradiction"] = contradiction ? "true" : "false";
        }

        if (ContradictionColumn.Length > 0)
        {
            claims["contradiction_column"] = ContradictionColumn;
        }

        return claims;
    }
}

/// <summary>
/// What the claims harness compares between the plan's claims and the execution, over and above the
/// rows (<c>corpus/policy/cases-calcite.yaml</c>'s <c>assert:</c> vocabulary).
/// </summary>
public sealed record PolicyClaims
{
    /// <summary>Nothing beyond the rows, the labels and the report.</summary>
    public static PolicyClaims None { get; } = new();

    /// <summary>The prepared query's output columns, in order — the order and the width both.</summary>
    public IReadOnlyList<string>? OutputColumnOrder { get; init; }

    /// <summary>Substrings the generated source SQL must carry.</summary>
    public IReadOnlyList<string> RemoteQueryContains { get; init; } = [];

    /// <summary>Substrings the generated source SQL must not carry.</summary>
    public IReadOnlyList<string> RemoteQueryNotContains { get; init; } = [];

    /// <summary>The IR <c>Read.projection</c> of a table's read, in the read's own output order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> ReadProjection { get; init; } =
        new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);

    /// <summary>The read's per-column outcomes (§3.10), one per column of the table.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ReadDisclosures { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>The labels equal the <c>chalk.disclosure</c> Arrow metadata, positionally.</summary>
    public bool LabelsEqualArrowMetadata { get; init; }

    /// <summary>The labels are consistent with the values under them.</summary>
    public bool LabelsAgreeWithValues { get; init; }

    /// <summary>No projection in the physical plan holds an aggregate call.</summary>
    public bool PlanHasNoAggregateInProject { get; init; }

    /// <summary>No two columns of the generated query's outermost select list share a name.</summary>
    public bool RemoteQueryNoDuplicateOutputNames { get; init; }

    /// <summary>A NULL in these columns is an outer join's, not a redacted column's placeholder.</summary>
    public IReadOnlyList<string> JoinNullsAreNotPlaceholders { get; init; } = [];

    /// <summary>The generated text names that table's columns in <see cref="ReadProjection"/>'s order.</summary>
    public string RemoteQueryProjectionIsCatalogOrder { get; init; } = "";

    /// <summary>These claims in the kit's own verb vocabulary, which is what its dispatcher reads.</summary>
    internal void Declare(IDictionary<string, IReadOnlyList<string>> into)
    {
        if (RemoteQueryContains.Count > 0)
        {
            into["remote_query_contains"] = RemoteQueryContains;
        }

        if (RemoteQueryNotContains.Count > 0)
        {
            into["remote_query_not_contains"] = RemoteQueryNotContains;
        }

        if (OutputColumnOrder is { } order)
        {
            into["output_column_order"] = order;
        }

        if (ReadProjection.Count > 0)
        {
            into["read_projection"] =
                [.. ReadProjection.Select(e => $"{e.Key}=[{string.Join(", ", e.Value)}]")
                    .OrderBy(e => e, StringComparer.Ordinal)];
        }

        if (ReadDisclosures.Count > 0)
        {
            into["read_disclosures"] =
                [.. ReadDisclosures.Select(e => $"{e.Key}=[{string.Join(", ", e.Value)}]")
                    .OrderBy(e => e, StringComparer.Ordinal)];
        }

        if (LabelsEqualArrowMetadata)
        {
            into["labels_equal_arrow_metadata"] = ["true"];
        }

        if (LabelsAgreeWithValues)
        {
            into["labels_agree_with_values"] = ["true"];
        }

        if (PlanHasNoAggregateInProject)
        {
            into["plan_has_no_aggregate_in_project"] = ["true"];
        }

        if (RemoteQueryNoDuplicateOutputNames)
        {
            into["remote_query_no_duplicate_output_names"] = ["true"];
        }

        if (JoinNullsAreNotPlaceholders.Count > 0)
        {
            into["join_nulls_are_not_placeholders"] = JoinNullsAreNotPlaceholders;
        }

        if (RemoteQueryProjectionIsCatalogOrder.Length > 0)
        {
            into["remote_query_projection_is_catalog_order"] =
                [RemoteQueryProjectionIsCatalogOrder];
        }
    }
}

/// <summary>What one case expects: rows, labels, acknowledgements, a refusal, and the claims.</summary>
public sealed record PolicyExpectation
{
    /// <summary>The rows, in the select list's order. Null when the case expects a refusal.</summary>
    public IReadOnlyList<IReadOnlyList<object?>>? Rows { get; init; }

    /// <summary>Output column name to what the report must say it discloses (§3.12).</summary>
    public IReadOnlyDictionary<string, ReportedDisclosure> Labels { get; init; } =
        new Dictionary<string, ReportedDisclosure>(StringComparer.Ordinal);

    /// <summary>Per entitled table the plan touches, what the report must say.</summary>
    public IReadOnlyDictionary<string, PolicyTableExpectation> Report { get; init; } =
        new Dictionary<string, PolicyTableExpectation>(StringComparer.Ordinal);

    /// <summary>The refusal, when the case expects one.</summary>
    public PolicyRefusal? Refusal { get; init; }

    /// <summary>What the claims harness compares beyond the rows.</summary>
    public PolicyClaims Claims { get; init; } = PolicyClaims.None;
}

/// <summary>
/// A judged disagreement between this case's hand computation and the engine (ADR 0025 part 2d,
/// part 2e). The battery is never edited to match the engine: the disagreement is the finding, so
/// each one is written down here with both answers and which side is right.
/// </summary>
/// <param name="Engine">What the engine does.</param>
/// <param name="Corpus">What the case expects.</param>
/// <param name="Judgement">Which side is right, and why.</param>
public sealed record PolicyAdjudication(string Engine, string Corpus, string Judgement);

/// <summary>
/// One of <c>corpus/policy/README.md</c> §5's uncertainties, the cases it rides on, and what the
/// run answered.
/// </summary>
/// <param name="Id">U1 through U21.</param>
/// <param name="Cases">The cases whose agreement or disagreement answers it.</param>
/// <param name="Verdict">What the run made of it, which the battery recomputes and holds.</param>
/// <param name="Answer">This run's judgement, in prose.</param>
public sealed record PolicyUncertainty(
    string Id,
    IReadOnlyList<string> Cases,
    PolicyVerdict Verdict,
    string Answer);

/// <summary>What a run made of one uncertainty.</summary>
public enum PolicyVerdict
{
    /// <summary>Every case it rides on agreed with the corpus.</summary>
    Agreed,

    /// <summary>Every one of them disagreed.</summary>
    Disputed,

    /// <summary>Some agreed and some did not.</summary>
    Mixed,

    /// <summary>None of its cases ran.</summary>
    NotRun,
}

/// <summary>The source profile's name, as the fixture and the corpus spell it.</summary>
public static class PolicySourceProfiles
{
    /// <summary>The corpus's own name for a profile.</summary>
    public static string Name(this PolicySourceProfile source) => source switch
    {
        PolicySourceProfile.AdoDuckDb => "ado_duckdb",
        PolicySourceProfile.AdoDuckDbNoIn => "ado_duckdb_no_in",
        PolicySourceProfile.AdoDuckDbTrusted => "ado_duckdb_trusted",
        PolicySourceProfile.AdoPostgres => "ado_postgres",
        _ => "poco",
    };

    /// <summary>Whether the profile is a real database rather than in-process collections.</summary>
    public static bool IsAdo(this PolicySourceProfile source) => source != PolicySourceProfile.Poco;
}
