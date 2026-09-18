using Chalk.Entitlements;

using ColumnLabels = System.Collections.Generic.Dictionary<string, Chalk.Entitlements.ReportedDisclosure>;
using ReadColumns = System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<int>>;
using ReadOutcomes = System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<string>>;
using TableReport = System.Collections.Generic.Dictionary<string, Chalk.TestKit.PolicyTableExpectation>;

namespace Chalk.TestKit;

public static partial class PolicyCases
{
/// <summary>
/// The cases that guard against a named Apache Calcite defect
/// (<c>docs/design/calcite-open-issues-assessment.md</c>). Each exists because an open issue
/// could make the plan disagree with its execution in the one way this battery is built to
/// notice: a column moved, renumbered or renamed between what the plan claims and what the rows
/// hold. A failure here is a disagreement first and a Calcite bug second, and the key the case
/// names is where to look.
/// </summary>
    public static IReadOnlyList<PolicyBatteryCase> Calcite =>
    [
        new PolicyBatteryCase
        {
            Id = "c01",
            Group = "CAL-P",
            Covers = "§3.12 D202; PlannerPipeline.rootProject; RuleSets.hep PROJECT_REMOVE, "
                + "PROJECT_MERGE",
            File = "cases/c01-permuted-select-masked.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["2000", "K", 1, "T"],
                    ["2000", "O", 2, "R"],
                    ["3050", "V", 3, "P"],
                    ["2000", "L", 4, "T"],
                    ["3050", "S", 10, "R"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["postcode"] = ReportedDisclosure.Full,
                    ["last_name"] = ReportedDisclosure.Masked,
                    ["id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Masked,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["postcode", "last_name", "id", "first_name"],
                    ReadProjection = new ReadColumns(StringComparer.Ordinal)
                    {
                        ["members"] = [6, 3, 0, 2],
                    },
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["members"] = ["FULL", "FULL", "MASKED", "MASKED", "MASKED", "MASKED", "FULL"],
                    },
                },
            },
            Notes = "CALCITE-1584, CALCITE-3922, CALCITE-4803, CALCITE-6070. The select list "
                + "names the table's columns out of order and alternates Full with Masked, "
                + "so a projection that was renumbered by PROJECT_REMOVE or the trimmer "
                + "puts a mask where the report says Full. `read_projection` is stated "
                + "because ChalkProjectScanRule prunes the scan to exactly this "
                + "permutation.",
            Disputed = new PolicyAdjudication(
                "the read projects members [0, 1, 2, 3, 6], in catalog order",
                "the read projects members [6, 3, 0, 2], the select list's permutation",
                "The engine is right. The read is pruned but not permuted: its "
                + "projection is in the table's own column order and carries whatever the "
                + "row predicate and the sanitisers need, and the permutation the select "
                + "list asks for is done above it. A column no sanitiser reads — one "
                + "whose mask is a constant — is not projected at all."),
        },
        new PolicyBatteryCase
        {
            Id = "c02",
            Group = "CAL-P",
            Covers = "§3.12; rootProject's 'names the optimiser threw away' (ADR 0014, ADR "
                + "0001 §3)",
            File = "cases/c02-rename-swaps-names.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["2000", "T"],
                    ["2000", "R"],
                    ["3050", "P"],
                    ["2000", "T"],
                    ["3050", "R"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["first_name"] = ReportedDisclosure.Full,
                    ["postcode"] = ReportedDisclosure.Masked,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["first_name", "postcode"],
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["members"] = ["FULL", "FULL", "MASKED", "MASKED", "MASKED", "MASKED", "FULL"],
                    },
                },
            },
            Notes = "CALCITE-1584, CALCITE-3922, CALCITE-4803. The output column named "
                + "`first_name` holds postcodes and is Full; the one named `postcode` holds "
                + "the mask of first_name and is Masked. The deliberate inversion is the "
                + "point: a report or a harness that matched the label to the *name* rather "
                + "than to the position would agree with itself and be wrong. "
                + "ProjectRemoveRule compares indexes and types and not names, so this "
                + "projection is trivial to it and rootProject is the only thing that puts "
                + "the names back.",
        },
        new PolicyBatteryCase
        {
            Id = "c03",
            Group = "CAL-P",
            Covers = "rootProject's SqlValidatorUtil.uniquify (ADR 0016); §3.12",
            File = "cases/c03-join-duplicate-output-names.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 101],
                    [1, 102],
                    [1, 105],
                    [2, 103],
                    [2, 104],
                    [3, 106],
                    [4, 107],
                    [4, 108],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["id0"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["id", "id0"],
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["members"] = ["FULL", "FULL", "MASKED", "MASKED", "MASKED", "MASKED", "FULL"],
                        ["orders"] = ["FULL", "FULL", "FULL", "FULL", "PER_ROW", "FULL", "FULL", "FULL"],
                    },
                },
            },
            Notes = "CALCITE-5316 (a duplicated select-list name is refused by "
                + "OrderByScope.aliasCount), CALCITE-597 and CALCITE-5402 (the generated "
                + "column list for a join of two tables that share a column name), "
                + "CALCITE-7663, CALCITE-6157. The first output is members.id and the "
                + "second orders.id; whichever way the join is commuted, `id` must be the "
                + "member's.",
            Disputed = new PolicyAdjudication(
                "orders reads note and amount PER_ROW",
                "orders reads note PER_ROW and amount FULL",
                "The engine is right, for the same reason cases 010 and 014 disagree: "
                + "`orders` carries the resource-owner fail-safe, so two rules stay "
                + "reachable at the leaf and the read's own outcome for a column the "
                + "rules differ on is PER_ROW. The case computed what the rows turned out "
                + "to be."),
        },
        new PolicyBatteryCase
        {
            Id = "c23",
            Group = "CAL-P",
            Covers = "§3.1; RelFieldTrimmer; rootProject after the sort (ADR 0014)",
            File = "cases/c23-rename-over-dropped-sort-keys.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["K"],
                    ["O"],
                    ["L"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["surname"] = ReportedDisclosure.Masked,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["surname"],
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["members"] = ["FULL", "FULL", "MASKED", "MASKED", "MASKED", "MASKED", "FULL"],
                    },
                },
            },
            Notes = "CALCITE-4149 (the top project is missing after trimming, and with it the "
                + "alias), CALCITE-6070 (a rename-only projection is what the trimmer "
                + "removes), CALCITE-3922. Both sort keys are dropped from the output and "
                + "the only surviving column is a rename, so the trimmer has to keep "
                + "postcode and id below the sort and out of the row while the name "
                + "`surname` survives PROJECT_REMOVE. The three rows are the first three of "
                + "the (postcode, id) order: members 1, 2 and 4, whose surnames mask to K, "
                + "O and L.",
        },
        new PolicyBatteryCase
        {
            Id = "c04",
            Group = "CAL-O",
            Covers = "§3.11 D160 (the star is expanded before the rewrite); §3.1 (a sort key "
                + "on a mask)",
            File = "cases/c04-order-by-ordinal-star.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [3, 1, "P", "V", new DateOnly(1975, 1, 1), null, "3050"],
                    [2, 1, "R", "O", new DateOnly(1991, 1, 1), null, "2000"],
                    [10, 1, "R", "S", new DateOnly(1990, 1, 1), null, "3050"],
                    [1, 1, "T", "K", new DateOnly(1980, 1, 1), null, "2000"],
                    [4, 1, "T", "L", new DateOnly(1988, 1, 1), null, "2000"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Masked,
                    ["last_name"] = ReportedDisclosure.Masked,
                    ["dob"] = ReportedDisclosure.Masked,
                    ["national_id"] = ReportedDisclosure.Masked,
                    ["postcode"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["id", "org_id", "first_name", "last_name", "dob", "national_id", "postcode"],
                    ReadProjection = new ReadColumns(StringComparer.Ordinal)
                    {
                        ["members"] = [0, 1, 2, 3, 4, 5, 6],
                    },
                },
            },
            Notes = "CALCITE-5724, CALCITE-5808, CALCITE-2610. Ordinal 3 must resolve to the "
                + "third column of the *expanded* star, `first_name`, which this principal "
                + "sees masked — so the rows come out in the masks' order (P, R, R, T, T) "
                + "and not the raw names' (Petra, Rina, Rina, Tao, Tomas), which would put "
                + "member 4 before member 1. Ordinal 1 breaks the two ties so the case can "
                + "be compared in order.",
            Disputed = new PolicyAdjudication(
                "the read projects members [0, 1, 2, 3, 4, 6]",
                "the read projects every column of members",
                "The engine is right. The read is pruned but not permuted: its "
                + "projection is in the table's own column order and carries whatever the "
                + "row predicate and the sanitisers need, and the permutation the select "
                + "list asks for is done above it. A column no sanitiser reads — one "
                + "whose mask is a constant — is not projected at all."),
        },
        new PolicyBatteryCase
        {
            Id = "c05",
            Group = "CAL-O",
            Covers = "D34 (conformance is per request); §3.6 (no guard: the key is not "
                + "entitled)",
            File = "cases/c05-group-by-ordinal-lenient.sql",
            Principal = "u2",
            Options = PolicyCaseOptions.Default with { Lenient = true },
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 5],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["org_id"] = ReportedDisclosure.Full,
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["org_id", "n"],
                },
            },
            Notes = "CALCITE-4541 (a numeric literal in GROUP BY collides with the ordinal "
                + "reading), CALCITE-2610 (group-by ordinal in a select-star query). "
                + "LENIENT is the conformance SqlConfigs.conformance offers that enables "
                + "both group-by-alias and group-by-ordinal, which is the combination "
                + "CALCITE-4541 needs. The grouping key must be `org_id` and not the "
                + "literal 1; the row count is the assertion that says which.",
        },
        new PolicyBatteryCase
        {
            Id = "c21",
            Group = "CAL-O",
            Covers = "§3.12; RelToIr (an aggregate call may appear only in an Aggregate)",
            File = "cases/c21-order-by-agg-alias-shadow.sql",
            Principal = "u1",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [300.00m, 1],
                    [500.00m, 2],
                    [700.00m, 3],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["amount"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["amount", "org_id"],
                    PlanHasNoAggregateInProject = true,
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["orders"] = ["FULL", "FULL", "FULL", "FULL", "PER_ROW", "FULL", "FULL", "FULL"],
                    },
                },
            },
            Notes = "CALCITE-7744 (the select list's alias shadows the base column and the "
                + "ORDER BY names an aggregate over it, producing a Project holding an "
                + "aggregate call), CALCITE-4987 (the generated SQL turns ORDER BY <alias> "
                + "into ORDER BY <the aggregate>). The third group is the single O3 order "
                + "u1 created, which the created-by fail-safe makes visible. `amount` is "
                + "FULL for u1 through the creator, manager and agent rules alike, so no "
                + "guard is emitted and no group is suppressed.",
            Disputed = new PolicyAdjudication(
                "the statement VALIDATION: Unsupported query: function MAX (MAX) is "
                    + "not supported by this planner.",
                "the statement rows",
                "Neither: MAX is not a function this planner supports, so the "
                    + "statement is refused by name."),
        },
        new PolicyBatteryCase
        {
            Id = "c06",
            Group = "CAL-J",
            Covers = "ADR 0014 (project after optimisation); RuleSets.volcano "
                + "SORT_PROJECT_TRANSPOSE",
            File = "cases/c06-sort-keys-not-in-select.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["T", 1],
                    ["R", 2],
                    ["T", 4],
                    ["P", 3],
                    ["R", 10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["first_name"] = ReportedDisclosure.Masked,
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["first_name", "id"],
                },
            },
            Notes = "CALCITE-6611 (a rule that weakens a Sort's collation registers a rel the "
                + "parent RelSubset will not choose, under exactly the top-down Volcano "
                + "Chalk pins), CALCITE-3352. The order is by postcode then id — 2000: 1, "
                + "2, 4; 3050: 3, 10 — and postcode is not in the output, so the projection "
                + "has to be applied after the sort and therefore after optimisation.",
        },
        new PolicyBatteryCase
        {
            Id = "c07",
            Group = "CAL-J",
            Covers = "§3.5, §3.12; RuleSets.hep PROJECT_JOIN_TRANSPOSE; ADR 0025 V62 "
                + "(RelRetyper)",
            File = "cases/c07-cast-over-outer-join.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, "********", "T"],
                    [1, "********", "T"],
                    [2, null, "R"],
                    [3, null, "P"],
                    [4, "return", "T"],
                    [10, null, "R"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["note_text"] = ReportedDisclosure.PerRow,
                    ["first_name"] = ReportedDisclosure.Masked,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["id", "note_text", "first_name"],
                    JoinNullsAreNotPlaceholders = ["note_text"],
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["members"] = ["FULL", "FULL", "MASKED", "MASKED", "MASKED", "MASKED", "FULL"],
                        ["orders"] = ["FULL", "FULL", "FULL", "FULL", "PER_ROW", "FULL", "FULL", "FULL"],
                    },
                },
            },
            Notes = "CALCITE-7488 and CALCITE-7489 (ProjectJoinTransposeRule produces a "
                + "row-type mismatch when a nullability-narrowing CAST is pushed through an "
                + "outer join), CALCITE-4399. The three NULLs are the outer join's — "
                + "members 2, 3 and 10 have no DONE order this principal can see — and must "
                + "not be read as a withheld column's placeholder, which is why note_text "
                + "is PerRow and not Undisclosed. Order 108 was created by u2, so the "
                + "creator rule discloses its note in full while orders 101 and 102 take "
                + "the agent's constant mask; that is what makes note_text PerRow.",
            Disputed = new PolicyAdjudication(
                "orders reads note and amount PER_ROW",
                "orders reads note PER_ROW and amount FULL",
                "The engine is right, for the same reason cases 010 and 014 disagree: "
                + "`orders` carries the resource-owner fail-safe, so two rules stay "
                + "reachable at the leaf and the read's own outcome for a column the "
                + "rules differ on is PER_ROW. The case computed what the rows turned out "
                + "to be."),
        },
        new PolicyBatteryCase
        {
            Id = "c08",
            Group = "CAL-J",
            Covers = "RelFieldTrimmer; RuleSets.hep PROJECT_JOIN_TRANSPOSE",
            File = "cases/c08-zero-column-join-inputs.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [1],
                    [1],
                    [1],
                    [1],
                    [1],
                    [1],
                    [1],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["one"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["one"],
                },
            },
            Notes = "CALCITE-7487 (ArrayIndexOutOfBoundsException in PushProjector when a "
                + "join input has a zero-column row type), CALCITE-4047. Nothing is "
                + "projected from either side, so after trimming PushProjector reaches its "
                + "'arbitrarily project the first column' branch, which indexes an empty "
                + "row. Eight rows: the same join as c03. A plan that cannot be found is "
                + "the failure this case looks for; the rows are only there to say the join "
                + "is the one c03 counted.",
            Disputed = new PolicyAdjudication(
                "the statement VALIDATION: SQL parse error at line 4, column 13 to "
                    + "line 4, column 15: Encountered \"one\" at line 4, column 13. Was "
                    + "expecting one of: \"MEASURE\" ... <QUOTED_STRING> ... "
                    + "<BRACKET_QUOTED_IDENTIFIER> ... <QUOTED_IDENTIFIER> ... "
                    + "<BACK_QUOTED_IDENTIFIER> ... <BIG_QUERY_BACK_QUOTED_IDENTIFIER> ... "
                    + "<HYPHENATED_IDENTIFIER> ... <IDENTIFIER> ... "
                    + "<UNICODE_QUOTED_IDENTIFIER> ...",
                "the statement rows",
                "Neither: the corpus's own SQL meeting this dialect, where AS one "
                    + "does not parse."),
        },
        new PolicyBatteryCase
        {
            Id = "c24",
            Group = "CAL-J",
            Covers = "§3.12 (DisclosureFlow concatenates a join's inputs in input order); "
                + "JOIN_COMMUTE",
            File = "cases/c24-join-commute-offsets.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [101, "T"],
                    [102, "T"],
                    [105, "T"],
                    [103, "R"],
                    [104, "R"],
                    [106, "P"],
                    [107, "T"],
                    [108, "T"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Masked,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["id", "first_name"],
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["members"] = ["FULL", "FULL", "MASKED", "MASKED", "MASKED", "MASKED", "FULL"],
                        ["orders"] = ["FULL", "FULL", "FULL", "FULL", "PER_ROW", "FULL", "FULL", "FULL"],
                    },
                },
            },
            Notes = "CALCITE-4126 (StackOverflow applying JoinCommuteRule), CALCITE-5842 "
                + "(LogicalProject's deepHashCode omits the row type while deepEquals "
                + "includes it, and HepPlanner looks up an equivalent rel by hash). The "
                + "scan order is written orders-then-members so that JOIN_COMMUTE has "
                + "something to swap; the masked column belongs to members whichever side "
                + "wins, and DisclosureFlow's concatenation of a join's inputs must follow "
                + "the physical input order rather than the statement's.",
            Disputed = new PolicyAdjudication(
                "orders reads note and amount PER_ROW",
                "orders reads note PER_ROW and amount FULL",
                "The engine is right, for the same reason cases 010 and 014 disagree: "
                + "`orders` carries the resource-owner fail-safe, so two rules stay "
                + "reachable at the leaf and the read's own outcome for a column the "
                + "rules differ on is PER_ROW. The case computed what the rows turned out "
                + "to be."),
        },
        new PolicyBatteryCase
        {
            Id = "c25",
            Group = "CAL-J",
            Covers = "RuleSets.hep JOIN_PUSH_TRANSITIVE_PREDICATES (§3.7 item 1 depends on it)",
            File = "cases/c25-right-join-transitive-predicates.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 101],
                    [1, 102],
                    [1, 105],
                    [2, 103],
                    [2, 104],
                    [3, 106],
                    [4, 107],
                    [4, 108],
                    [10, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["id0"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["id", "id0"],
                    JoinNullsAreNotPlaceholders = ["id0"],
                },
            },
            Notes = "CALCITE-7766 (type mismatch on nullability in "
                + "JoinPushTransitivePredicatesRule for a RIGHT join — CALCITE-5387 fixed "
                + "INNER and LEFT only), CALCITE-2626. §3.7 item 1 needs this rule to carry "
                + "the tenancy conjunct across an equi-join on the tenancy column, so it "
                + "cannot simply be dropped. Member 10 has no order, so its row is the "
                + "outer join's NULL; `id0` must be reported Full, not Undisclosed.",
        },
        new PolicyBatteryCase
        {
            Id = "c10",
            Group = "CAL-A",
            Covers = "§3.6 (the guard), §3.10 clauses 2 and 4 (the origin walk), §3.12",
            File = "cases/c10-aggregate-group-key-last.sql",
            Principal = "u11",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [8, 1400.00m, 1],
                    [2, null, 3],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Full,
                    ["s"] = ReportedDisclosure.Aggregate,
                    ["org_id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["n", "s", "org_id"],
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["orders"] = ["FULL", "FULL", "FULL", "FULL", "MASKED", "AGGREGATE", "FULL", "FULL"],
                    },
                },
            },
            Notes = "CALCITE-4250 — RelMdColumnOrigins returns the wrong column index for an "
                + "Aggregate ('the correct column index is 7, but got 5'), and the taint "
                + "check's clauses 2 and 4 are origin walks. The group key is deliberately "
                + "last in the select list and the guarded measure is in the middle, so an "
                + "off-by-two origin names the wrong column: the check would then ask "
                + "whether `org_id` is population-only rather than `amount`, and a plan "
                + "that reads a raw population-only value would pass. Rows: this auditor "
                + "sees O1's eight orders (sum 1400) and O3's two (sum 1600), and 2 is "
                + "below the floor of 5, so the second group's SUM is NULL while its "
                + "COUNT(*) — not over the column — is not.",
            Disputed = new PolicyAdjudication(
                "orders reads note PER_ROW",
                "orders reads note MASKED",
                "The engine is right, for the same reason cases 010 and 014 disagree: "
                + "`orders` carries the resource-owner fail-safe, so two rules stay "
                + "reachable at the leaf and the read's own outcome for a column the "
                + "rules differ on is PER_ROW. The case computed what the rows turned out "
                + "to be."),
        },
        new PolicyBatteryCase
        {
            Id = "c11",
            Group = "CAL-A",
            Covers = "RuleSets.hep AGGREGATE_PROJECT_MERGE; §3.12",
            File = "cases/c11-permuted-grouping-keys.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["2000", 1, 3, "R"],
                    ["3050", 1, 2, "P"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["postcode"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["n"] = ReportedDisclosure.Full,
                    ["f"] = ReportedDisclosure.Masked,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["postcode", "org_id", "n", "f"],
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["members"] = ["FULL", "FULL", "MASKED", "MASKED", "MASKED", "MASKED", "FULL"],
                    },
                },
            },
            Notes = "CALCITE-4803 (an alias is lost after AGGREGATE_PROJECT_MERGE), "
                + "CALCITE-4250. The grouping key set is (org_id, postcode) and the select "
                + "list is (postcode, org_id, …), so the two orders have to be kept apart "
                + "when the projection below the aggregate is folded into its group set. "
                + "MIN over a masked column is the minimum of the *masks*: postcode 2000 "
                + "holds members 1, 2 and 4 whose initials are T, R, T, so f is R; 3050 "
                + "holds 3 and 10, P and R, so f is P. `f` is encoded Masked because its "
                + "only origin is a masked column and §3.12's meet is over origins; "
                + "README.md §5 U2 records that the same question is encoded the other way "
                + "for COUNT(DISTINCT) in case 052, and a disagreement between the two is "
                + "the finding.",
        },
        new PolicyBatteryCase
        {
            Id = "c12",
            Group = "CAL-S",
            Covers = "RuleSets.volcano UNION_PULL_UP_CONSTANTS, UNION_MERGE; D69",
            File = "cases/c12-union-pull-up-constants.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "T"],
                    [2, 1, "R"],
                    [3, 1, "P"],
                    [4, 1, "T"],
                    [10, 1, "R"],
                    [1, 1, "T"],
                    [2, 1, "R"],
                    [3, 1, "P"],
                    [4, 1, "T"],
                    [10, 1, "R"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Masked,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["id", "org_id", "first_name"],
                },
            },
            Notes = "CALCITE-5345. Both branches pin org_id to the same literal, so "
                + "UNION_PULL_UP_CONSTANTS removes that column from every branch and "
                + "re-adds it above the union — a width change under a set operation, which "
                + "is where a column lands one position out. The constant must come back in "
                + "org_id's position and not in first_name's; the mask in the third column "
                + "is what makes a one-position slip visible in the values as well as the "
                + "report.",
        },
        new PolicyBatteryCase
        {
            Id = "c13",
            Group = "CAL-S",
            Covers = "§3.12 (the SetOp meet in DisclosureFlow); D69",
            File = "cases/c13-union-mixed-disclosure.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, "T"],
                    [2, "R"],
                    [3, "P"],
                    [4, "T"],
                    [10, "R"],
                    [1, "2000"],
                    [2, "2000"],
                    [3, "3050"],
                    [4, "2000"],
                    [10, "3050"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Masked,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["id", "first_name"],
                },
            },
            Notes = "CALCITE-1268, CALCITE-2346. One branch's second column is masked and the "
                + "other's is a column this principal sees in full, so the output column's "
                + "reported disclosure is the meet — Masked — and half its rows are "
                + "postcodes. A set operation is read positionally by the client; a branch "
                + "alignment done by name rather than by position would be a wrong column "
                + "read with no error anywhere. The output name is the first branch's.",
            Disputed = new PolicyAdjudication(
                "first_name's disclosure PerRow",
                "first_name's disclosure Masked",
                "The engine is right by V66: a set operation's branches are "
                    + "alternative rows rather than two origins of one value, so a masked "
                    + "column unioned with a full one is PerRow."),
        },
        new PolicyBatteryCase
        {
            Id = "c14",
            Group = "CAL-C",
            Covers = "SqlConfigs expand=false + TOPDOWN_GENERAL_DECORRELATION_ENABLED; §3.12",
            File = "cases/c14-scalar-subquery-per-member.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, "T", 3],
                    [2, "R", 2],
                    [3, "P", 1],
                    [4, "T", 2],
                    [10, "R", 0],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Masked,
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["id", "first_name", "n"],
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["members"] = ["FULL", "FULL", "MASKED", "MASKED", "MASKED", "MASKED", "FULL"],
                        ["orders"] = ["FULL", "FULL", "FULL", "FULL", "PER_ROW", "FULL", "FULL", "FULL"],
                    },
                },
            },
            Notes = "CALCITE-7405 (SqlToRelConverter re-applies a projection's expressions "
                + "over a node projection fusion already removed — mis-aligned references, "
                + "in the expand=false plus correlated sub-query configuration Chalk pins), "
                + "CALCITE-7574, CALCITE-7430, CALCITE-3978, CALCITE-4318, CALCITE-4984. "
                + "The counts are over the orders this principal can see: member 1 has 101, "
                + "102 and 105; member 2 has 103 and 104; member 3 has 106; member 4 has "
                + "107 and 108; member 10 has none. Each count must belong to the member on "
                + "its own row, which is what a decorrelator that renumbered a field gets "
                + "wrong without failing.",
            Disputed = new PolicyAdjudication(
                "orders reads note and amount PER_ROW",
                "orders reads note PER_ROW and amount FULL",
                "The engine is right, for the same reason cases 010 and 014 disagree: "
                + "`orders` carries the resource-owner fail-safe, so two rules stay "
                + "reachable at the leaf and the read's own outcome for a column the "
                + "rules differ on is PER_ROW. The case computed what the rows turned out "
                + "to be."),
        },
        new PolicyBatteryCase
        {
            Id = "c15",
            Group = "CAL-C",
            Covers = "RuleSets.hep FILTER_SUB_QUERY_TO_CORRELATE; afterDecorrelation "
                + "MARK_TO_SEMI_OR_ANTI_JOIN_RULE then PROJECT_JOIN_TRANSPOSE",
            File = "cases/c15-exists-permuted-projection.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["2000", "T", 1],
                    ["2000", "R", 2],
                    ["3050", "P", 3],
                    ["2000", "T", 4],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["postcode"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Masked,
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["postcode", "first_name", "id"],
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["members"] = ["FULL", "FULL", "MASKED", "MASKED", "MASKED", "MASKED", "FULL"],
                    },
                },
            },
            Notes = "CALCITE-7622 (a transpose rule builds a scratch row type with "
                + "deriveJoinRowType(..., INNER, ...), which is wrong for SEMI, ANTI and "
                + "LEFT MARK joins — Chalk manufactures all three in afterDecorrelation and "
                + "runs PROJECT_JOIN_TRANSPOSE immediately after), CALCITE-6504, "
                + "CALCITE-5213, CALCITE-2136. Member 10 has no order, so four rows. The "
                + "projection is permuted so that a rule which assumed left+right columns "
                + "puts the mask in the wrong output position.",
        },
        new PolicyBatteryCase
        {
            Id = "c16",
            Group = "CAL-C",
            Covers = "RuleSets.hep JOIN_SUB_QUERY_TO_CORRELATE",
            File = "cases/c16-join-on-correlated-subquery.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 101],
                    [1, 102],
                    [1, 105],
                    [2, 101],
                    [2, 102],
                    [2, 105],
                    [3, 101],
                    [3, 102],
                    [3, 105],
                    [4, 101],
                    [4, 102],
                    [4, 105],
                    [10, 101],
                    [10, 102],
                    [10, 105],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["id0"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["id", "id0"],
                },
            },
            Notes = "CALCITE-6504 — JOIN_SUB_QUERY_TO_CORRELATE produces an incorrect tree "
                + "when a correlated sub-query is part of an equi-join's condition, which "
                + "is this statement. Every visible member is in O1, so the sub-query's MIN "
                + "over the *visible* members is 1 for every outer row and the join "
                + "condition selects orders 101, 102 and 105: five members times three "
                + "orders. UNCERTAIN: if the planner refuses this shape instead "
                + "(CorrelateSupport, or a decorrelation failure reported as UNSUPPORTED "
                + "under D67), the refusal is the case's answer and the rows above are the "
                + "finding — the fifteen-row result is what the statement means.",
        },
        new PolicyBatteryCase
        {
            Id = "c09",
            Group = "CAL-W",
            Covers = "RuleSets.hep PROJECT_TO_LOGICAL_PROJECT_AND_WINDOW, "
                + "PROJECT_WINDOW_TRANSPOSE; §3.1",
            File = "cases/c09-window-masked-partition.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, "T", 1],
                    [2, "R", 1],
                    [3, "P", 1],
                    [4, "T", 2],
                    [10, "R", 2],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Masked,
                    ["rn"] = ReportedDisclosure.Masked,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["id", "first_name", "rn"],
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["members"] = ["FULL", "FULL", "MASKED", "MASKED", "MASKED", "MASKED", "FULL"],
                    },
                },
            },
            Notes = "CALCITE-3352 (ProjectToWindowRule sets the Project's collation on the "
                + "generated Window, which a window does not change — under top-down "
                + "Volcano an overstated collation trait is how a required ORDER BY "
                + "disappears), CALCITE-7616, CALCITE-7644. The partition key is the "
                + "*mask*, so the groups are T = {1, 4}, R = {2, 10} and P = {3}, and the "
                + "row numbers are 1, 1, 1, 2, 2 — not the five distinct raw names' "
                + "all-ones. `rn` is Masked because DisclosureFlow meets over the "
                + "references a RexOver reads, its window's partition and order keys "
                + "included, and the pass runs before the Hep rule that turns the RexOver "
                + "into a LogicalWindow. UNCERTAIN: after that rule the flow would meet a "
                + "shape it does not know and report PerRow instead; the disagreement "
                + "between the two is the finding.",
            Disputed = new PolicyAdjudication(
                "rn's disclosure Full",
                "rn's disclosure Masked",
                "The engine is right by V66: a window function is judged on its "
                    + "arguments, so ROW_NUMBER() OVER (PARTITION BY first_name) is Full. "
                    + "The case predates that finding."),
        },
        new PolicyBatteryCase
        {
            Id = "c20",
            Group = "CAL-W",
            Covers = "§3.7 (the row predicate reaches the source); SourceSql / "
                + "RelToSqlConverter",
            File = "cases/c20-window-order-by-expression.sql",
            Principal = "u14",
            Source = PolicySourceProfile.AdoPostgres,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [103, 150.00m],
                    [104, 400.00m],
                    [105, 450.00m],
                    [106, 750.00m],
                    [107, 850.00m],
                    [101, 950.00m],
                    [102, 1150.00m],
                    [108, 1400.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["running_total"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some, RowPredicatePushed = true },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["id", "running_total"],
                    RemoteQueryNotContains = ["ORDER BY 1 ", "ORDER BY 2 ", "ORDER BY 1)", "ORDER BY 2)"],
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["orders"] = ["FULL", "FULL", "FULL", "FULL", "FULL", "FULL", "FULL", "FULL"],
                    },
                },
            },
            Notes = "CALCITE-7644 (a window's ORDER BY expression is unparsed as a positional "
                + "ordinal into the *enclosing* select list, which is a different sort "
                + "key), CALCITE-5828. This manager sees O1's eight orders. The window "
                + "orders by (status = 'DONE') then id, so the running total visits 103, "
                + "104, 105, 106, 107 (the non-DONE orders, by id) and then 101, 102, 108: "
                + "150, 400, 450, 750, 850, 950, 1150, 1400. Decimal scale is Calcite's; "
                + "compare numerically. The alias was `running` until the "
                + "package-refinement run, and RUNNING is a reserved word in the SQL "
                + "Calcite parses (a MATCH_RECOGNIZE keyword), so the case was a parse "
                + "error and never reached the exposure it guards; it is `running_total` "
                + "now and the statement runs. ANSWERED, this note's own uncertainty: the "
                + "window is *not* pushed to this profile — the remote text is a projected "
                + "scan of `orders` under `\"org_id\" = 1 OR \"created_by\" = 99`, the "
                + "row predicate and nothing else — so the four ORDER-BY-ordinal "
                + "assertions are vacuous and only the rows and the labels stand. The rows "
                + "are right; the labels are the disagreement below.",
            Disputed = new PolicyAdjudication(
                "running_total PerRow, and note and amount PER_ROW on the read",
                "running_total Full, and every column of orders FULL",
                "The engine is right and this case's hand computation was wrong, which "
                + "is what a case that never parsed could not find out. For u14 — a "
                + "manager in O1 and nothing else — note and amount each keep two live "
                + "rules after the fold, created_by = 99 and org_id IN (1), over an "
                + "`otherwise` of NONE, and nothing proves the row filter implies one of "
                + "the two disjuncts, so PER_ROW is the honest label; every other case "
                + "over orders records the same for note. running_total is SUM(amount) "
                + "OVER (…), judged on its argument by V66, so it takes amount's."),

        },
        new PolicyBatteryCase
        {
            Id = "c17",
            Group = "CAL-R",
            Covers = "§3.7 D199; §3.8 D200 (a mask stays local); SourceSql; §3.11 D160",
            File = "cases/c17-remote-star-catalog-order.sql",
            Principal = "u2",
            Source = PolicySourceProfile.AdoPostgres,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "T", "K", new DateOnly(1980, 1, 1), null, "2000"],
                    [2, 1, "R", "O", new DateOnly(1991, 1, 1), null, "2000"],
                    [3, 1, "P", "V", new DateOnly(1975, 1, 1), null, "3050"],
                    [4, 1, "T", "L", new DateOnly(1988, 1, 1), null, "2000"],
                    [10, 1, "R", "S", new DateOnly(1990, 1, 1), null, "3050"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Masked,
                    ["last_name"] = ReportedDisclosure.Masked,
                    ["dob"] = ReportedDisclosure.Masked,
                    ["national_id"] = ReportedDisclosure.Masked,
                    ["postcode"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some, RowPredicatePushed = true },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["id", "org_id", "first_name", "last_name", "dob", "national_id", "postcode"],
                    ReadProjection = new ReadColumns(StringComparer.Ordinal)
                    {
                        ["members"] = [0, 1, 2, 3, 4, 5, 6],
                    },
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["members"] = ["FULL", "FULL", "MASKED", "MASKED", "MASKED", "MASKED", "FULL"],
                    },
                    RemoteQueryProjectionIsCatalogOrder = "members",
                    RemoteQueryContains = ["org_id"],
                },
            },
            Notes = "CALCITE-7663 — expanding a SELECT * over a join with duplicate field "
                + "names produces un-aliased, ambiguous column references. The single-table "
                + "case is the other half of the same promise: SqlImplementor emits `SELECT "
                + "*` when nothing above the scan projects, Chalk reads the answer by "
                + "position, and nothing in the plan says the source's column order equals "
                + "the registered catalog's. The rows are case 002's, unchanged: masks are "
                + "local (push_masks false, supports_mask_pushdown false), so what crosses "
                + "the boundary is the raw row and the tenancy filter.",
            Disputed = new PolicyAdjudication(
                "the pushed read projects [0, 1, 2, 3, 4, 6]",
                "it projects every column of members",
                "The engine is right, and it is the same answer as case c04: a "
                + "column no sanitiser reads — national_id, whose mask is a constant "
                + "NULL — is not projected at all. The generated text names the six "
                + "it does read, in the catalog's own order, which is what the case's "
                + "other claim asserts."),
        },
        new PolicyBatteryCase
        {
            Id = "c18",
            Group = "CAL-R",
            Covers = "§3.10 (per-column outcomes by table ordinal); ProjectedRelOptTable; "
                + "SourceSql",
            File = "cases/c18-remote-pruned-permuted.sql",
            Principal = "u2",
            Source = PolicySourceProfile.AdoPostgres,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["2000", 1],
                    ["2000", 2],
                    ["3050", 3],
                    ["2000", 4],
                    ["3050", 10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["postcode"] = ReportedDisclosure.Full,
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some, RowPredicatePushed = true },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["postcode", "id"],
                    ReadProjection = new ReadColumns(StringComparer.Ordinal)
                    {
                        ["members"] = [6, 0],
                    },
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["members"] = ["FULL", "FULL", "MASKED", "MASKED", "MASKED", "MASKED", "FULL"],
                    },
                    RemoteQueryContains = ["postcode", "id", "org_id"],
                },
            },
            Notes = "CALCITE-3558 (a pushdown rule whose loop index does not advance, so "
                + "column reordering silently does not happen — the defect class, in an "
                + "adapter Chalk does not run, which is why Chalk's own PushdownRules "
                + "deserve this case), CALCITE-597. Two assertions that only this shape "
                + "makes: the read's projection is the *table* ordinals in the read's "
                + "output order, [6, 0] and not [0, 6]; and the read's per-column outcomes "
                + "are stated for all seven columns of the table, including the four masked "
                + "ones the statement never projects, because §3.10 asks for one entry per "
                + "column of the table.",
        },
        new PolicyBatteryCase
        {
            Id = "c19",
            Group = "CAL-R",
            Covers = "SourceSql; rootProject's uniquify (ADR 0016); §3.7",
            File = "cases/c19-remote-join-name-clash.sql",
            Principal = "u2",
            Source = PolicySourceProfile.AdoPostgres,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 101, "DONE"],
                    [1, 102, "DONE"],
                    [1, 105, "HOLD"],
                    [2, 103, "OPEN"],
                    [2, 104, "OPEN"],
                    [3, 106, "NEW"],
                    [4, 107, "OPEN"],
                    [4, 108, "DONE"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["id0"] = ReportedDisclosure.Full,
                    ["status"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some, RowPredicatePushed = true },
                    ["orders"] = new() { Visibility = TableVisibility.Some, RowPredicatePushed = true },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["id", "id0", "status"],
                    RemoteQueryNoDuplicateOutputNames = true,
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["members"] = ["FULL", "FULL", "MASKED", "MASKED", "MASKED", "MASKED", "FULL"],
                        ["orders"] = ["FULL", "FULL", "FULL", "FULL", "PER_ROW", "FULL", "FULL", "FULL"],
                    },
                },
            },
            Notes = "CALCITE-597 (the generated list names an ambiguous column and one that "
                + "does not exist), CALCITE-5402 (one side's field resolves to the "
                + "other's), CALCITE-7642 (two derived-table aliases that differ only by "
                + "case, which Lex.MYSQL_ANSI's case-insensitive matching makes reachable), "
                + "CALCITE-7663. Both tables carry an `id`; the first output must be the "
                + "member's.",
            Disputed = new PolicyAdjudication(
                "the pushed read of orders reports amount PER_ROW",
                "it reports amount FULL",
                "The engine is right, for the same reason cases 010 and 014 "
                + "disagree: orders carries the resource-owner fail-safe, so two "
                + "rules stay reachable at the leaf and the read's own outcome for a "
                + "column the rules differ on is PER_ROW."),
        },
        new PolicyBatteryCase
        {
            Id = "c22",
            Group = "CAL-U",
            Covers = "§3.11 D160–D161; ADR 0025 V65 (star provenance); UndisclosedColumns.omit",
            File = "cases/c22-omit-star-column-offsets.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersNone },
            Options = PolicyCaseOptions.Default with { Redaction = new() { StarExpansion = StarExpansion.Omit } },
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "T", "K", new DateOnly(1980, 1, 1), null, "DONE"],
                    [1, 1, "T", "K", new DateOnly(1980, 1, 1), null, "DONE"],
                    [1, 1, "T", "K", new DateOnly(1980, 1, 1), null, "HOLD"],
                    [2, 1, "R", "O", new DateOnly(1991, 1, 1), null, "OPEN"],
                    [2, 1, "R", "O", new DateOnly(1991, 1, 1), null, "OPEN"],
                    [3, 1, "P", "V", new DateOnly(1975, 1, 1), null, "NEW"],
                    [4, 1, "T", "L", new DateOnly(1988, 1, 1), null, "OPEN"],
                    [4, 1, "T", "L", new DateOnly(1988, 1, 1), null, "DONE"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Masked,
                    ["last_name"] = ReportedDisclosure.Masked,
                    ["dob"] = ReportedDisclosure.Masked,
                    ["national_id"] = ReportedDisclosure.Masked,
                    ["status"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
                Claims = new PolicyClaims
                {
                    OutputColumnOrder = ["id", "org_id", "first_name", "last_name", "dob", "national_id", "status"],
                    ReadDisclosures = new ReadOutcomes(StringComparer.Ordinal)
                    {
                        ["members"] = ["FULL", "FULL", "MASKED", "MASKED", "MASKED", "MASKED", "REDACTED"],
                        ["orders"] = ["FULL", "FULL", "FULL", "FULL", "PER_ROW", "FULL", "FULL", "FULL"],
                    },
                },
            },
            Notes = "CALCITE-7453 (star expansion and the names it records), CALCITE-4923, "
                + "ADR 0025 V65. `Omit` edits RelRoot.fields, which is the one index list "
                + "the per-column report and the root projection share — so this is the "
                + "case where a field-index defect anywhere upstream is amplified rather "
                + "than caught. Under members_none, postcode is undeclared and therefore "
                + "withheld; the star surfaced it, so Omit drops it and nothing else, and "
                + "the join's field offsets have to survive an edit made to the *root's* "
                + "list. `status` is named rather than starred, so it is untouched by Omit "
                + "and must stay last. Exactly one star, so provenance knows its width (two "
                + "or more would read as all-named).",
            Disputed = new PolicyAdjudication(
                "orders reads note and amount PER_ROW",
                "orders reads note PER_ROW and amount FULL",
                "The engine is right, for the same reason cases 010 and 014 disagree: "
                + "`orders` carries the resource-owner fail-safe, so two rules stay "
                + "reachable at the leaf and the read's own outcome for a column the "
                + "rules differ on is PER_ROW. The case computed what the rows turned out "
                + "to be."),
        },
    ];
}
