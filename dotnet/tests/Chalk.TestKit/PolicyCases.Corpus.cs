using Chalk.Entitlements;

using ColumnLabels = System.Collections.Generic.Dictionary<string, Chalk.Entitlements.ReportedDisclosure>;
using TableReport = System.Collections.Generic.Dictionary<string, Chalk.TestKit.PolicyTableExpectation>;

namespace Chalk.TestKit;

public static partial class PolicyCases
{
    /// <summary>
    /// The battery's own cases: the design's corpus run as every principal it makes sense for, then
    /// every disclosure in every position, the population-only trace, the group-size guard, stars and
    /// placeholders, statement shapes, folding, visibility, the tenancy grants, pushdown, the
    /// statistical opt-in, the sibling columns, the fingerprint join and the registration negatives
    /// (<c>corpus/policy/README.md</c> §1, groups A through P).
    /// </summary>
    public static IReadOnlyList<PolicyBatteryCase> Corpus =>
    [
        new PolicyBatteryCase
        {
            Id = "001",
            Group = "A",
            Covers = "§8 corpus 01; §3.1 D195, §3.2 D196, §3.12 D202",
            File = "cases/001-members-star-u1.sql",
            Principal = "u1",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "Tomas", "Kalny", new DateOnly(1980, 3, 14), "NID-1001", "2000"],
                    [2, 1, "Rina", "Osei", new DateOnly(1991, 7, 2), "NID-1002", "2000"],
                    [3, 1, "Petra", "Vance", new DateOnly(1975, 11, 30), "NID-1003", "3050"],
                    [4, 1, "Tao", "Lindqvist", new DateOnly(1988, 1, 9), "NID-1004", "2000"],
                    [10, 1, "Rina", "Sandoval", new DateOnly(1990, 6, 6), "NID-1010", "3050"],
                    [5, 2, "T", "M", new DateOnly(1993, 1, 1), null, "4101"],
                    [6, 2, "U", "B", new DateOnly(1969, 1, 1), null, "4101"],
                    [7, 2, "N", "H", new DateOnly(1985, 1, 1), null, "5000"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.PerRow,
                    ["last_name"] = ReportedDisclosure.PerRow,
                    ["dob"] = ReportedDisclosure.PerRow,
                    ["national_id"] = ReportedDisclosure.PerRow,
                    ["postcode"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The design's headline case: raw rows and masked rows in one result, so "
                + "the reported disclosure of four columns is PerRow. `dob`'s agent mask "
                + "generalises within the type (the year), `national_id`'s is the per-type "
                + "default NULL.",
        },
        new PolicyBatteryCase
        {
            Id = "002",
            Group = "A",
            Covers = "§8 corpus 01; §3.3 — simplification under Filter_R's conjuncts",
            File = "cases/002-members-star-u2.sql",
            Principal = "u2",
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
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "An agent in the one org it can see gets a constant MASKED rather than a "
                + "CASE, which is what §3.3 says the simplification under the leaf's own "
                + "conjuncts is for.",
        },
        new PolicyBatteryCase
        {
            Id = "003",
            Group = "A",
            Covers = "§8 corpus 01; D204 subject dimension, confined `within`",
            File = "cases/003-members-star-u3.sql",
            Principal = "u3",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [4, 1, "Tao", "Lindqvist", new DateOnly(1988, 1, 9), null, "2000"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Full,
                    ["last_name"] = ReportedDisclosure.Full,
                    ["dob"] = ReportedDisclosure.Full,
                    ["national_id"] = ReportedDisclosure.Masked,
                    ["postcode"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The subject rule grants FULL on the pii realm only; `national_id` is "
                + "`restricted` and stays masked even on the subject's own row. That split "
                + "is this fixture's choice, not the design's — §8 does not say what a "
                + "subject sees on a restricted column.",
        },
        new PolicyBatteryCase
        {
            Id = "004",
            Group = "A",
            Covers = "§8 corpus 14; §2 the scalar wildcard; §3.12 visibility = ALL",
            File = "cases/004-members-star-u4.sql",
            Principal = "u4",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "Tomas", "Kalny", new DateOnly(1980, 3, 14), "NID-1001", "2000"],
                    [2, 1, "Rina", "Osei", new DateOnly(1991, 7, 2), "NID-1002", "2000"],
                    [3, 1, "Petra", "Vance", new DateOnly(1975, 11, 30), "NID-1003", "3050"],
                    [4, 1, "Tao", "Lindqvist", new DateOnly(1988, 1, 9), "NID-1004", "2000"],
                    [5, 2, "Tessa", "Moreau", new DateOnly(1993, 5, 21), "NID-1005", "4101"],
                    [6, 2, "Ulf", "Berg", new DateOnly(1969, 9, 17), "NID-1006", "4101"],
                    [7, 2, "Nadia", "Haddad", new DateOnly(1985, 2, 28), "NID-1007", "5000"],
                    [8, 3, "Tao", "Elmi", new DateOnly(1979, 12, 5), "NID-1008", "6060"],
                    [9, 3, "Mira", "Kovac", new DateOnly(1997, 4, 11), "NID-1009", "6060"],
                    [10, 1, "Rina", "Sandoval", new DateOnly(1990, 6, 6), "NID-1010", "3050"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Full,
                    ["last_name"] = ReportedDisclosure.Full,
                    ["dob"] = ReportedDisclosure.Full,
                    ["national_id"] = ReportedDisclosure.Full,
                    ["postcode"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.All },
                },
            },
            Notes = "The global scalar folds the row predicate to TRUE, so the filter "
                + "disappears entirely and every disclosure folds to FULL. Audited like any "
                + "other grant (D157, D205).",
        },
        new PolicyBatteryCase
        {
            Id = "005",
            Group = "A",
            Covers = "§8 corpus 01; §3.12 visibility = NONE; D161 a constant NONE is a "
                + "placeholder",
            File = "cases/005-members-star-u5.sql",
            Principal = "u5",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Redacted,
                    ["last_name"] = ReportedDisclosure.Redacted,
                    ["dob"] = ReportedDisclosure.Redacted,
                    ["national_id"] = ReportedDisclosure.Redacted,
                    ["postcode"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.None },
                },
            },
            Notes = "`members` carries no created-by fail-safe, so every disjunct folds to "
                + "FALSE and the visibility is genuinely NONE. The columns are still "
                + "reported, from the folded rules alone — the report never counts hidden "
                + "rows (D207).",
        },
        new PolicyBatteryCase
        {
            Id = "006",
            Group = "A",
            Covers = "§8 corpus 01; §1 the per-rule mask; §4 FINGERPRINT",
            File = "cases/006-members-star-u6.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "!fp:Tomas", "!fp:Kalny", null, null, "2000"],
                    [2, 1, "!fp:Rina", "!fp:Osei", null, null, "2000"],
                    [3, 1, "!fp:Petra", "!fp:Vance", null, null, "3050"],
                    [4, 1, "!fp:Tao", "!fp:Lindqvist", null, null, "2000"],
                    [10, 1, "!fp:Rina", "!fp:Sandoval", null, null, "3050"],
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
            },
            Notes = "The auditor's token mask is a STRING, so it applies to the two name "
                + "columns and not to `dob`, which takes the per-type default NULL instead. "
                + "Rows 2 and 10 share a first name and therefore share a token — that is "
                + "what case 177 joins on.",
        },
        new PolicyBatteryCase
        {
            Id = "007",
            Group = "A",
            Covers = "§8 corpus 01; D196 first match wins",
            File = "cases/007-members-star-u7.sql",
            Principal = "u7",
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
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "UNCERTAIN (README §5 U3). Two rules match every visible row — the "
                + "agent's and the auditor's — and both say MASKED with different masks. "
                + "The earlier rule wins, so this principal sees initials. The design fixes "
                + "the order of the DISCLOSURE levels, not the order of two rules at the "
                + "same level, so the fixture's declaration order is what decides it here.",
        },
        new PolicyBatteryCase
        {
            Id = "008",
            Group = "A",
            Covers = "§8 corpus 01; D204 `Tenancy.Anywhere`",
            File = "cases/008-members-star-u8.sql",
            Principal = "u8",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [7, 2, "Nadia", "Haddad", new DateOnly(1985, 2, 28), null, "5000"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Full,
                    ["last_name"] = ReportedDisclosure.Full,
                    ["dob"] = ReportedDisclosure.Full,
                    ["national_id"] = ReportedDisclosure.Masked,
                    ["postcode"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "An unconfined subject grant binds the single-column list `subject_ids`, "
                + "which folds to a SEARCH rather than to the OR-of-ANDs a confined grant "
                + "produces.",
        },
        new PolicyBatteryCase
        {
            Id = "009",
            Group = "A",
            Covers = "§8 corpus 01; §3.12 PerRow over two roles in two tenancies",
            File = "cases/009-members-star-u12.sql",
            Principal = "u12",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "Tomas", "Kalny", new DateOnly(1980, 3, 14), "NID-1001", "2000"],
                    [2, 1, "Rina", "Osei", new DateOnly(1991, 7, 2), "NID-1002", "2000"],
                    [3, 1, "Petra", "Vance", new DateOnly(1975, 11, 30), "NID-1003", "3050"],
                    [4, 1, "Tao", "Lindqvist", new DateOnly(1988, 1, 9), "NID-1004", "2000"],
                    [10, 1, "Rina", "Sandoval", new DateOnly(1990, 6, 6), "NID-1010", "3050"],
                    [5, 2, "!fp:Tessa", "!fp:Moreau", null, null, "4101"],
                    [6, 2, "!fp:Ulf", "!fp:Berg", null, null, "4101"],
                    [7, 2, "!fp:Nadia", "!fp:Haddad", null, null, "5000"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.PerRow,
                    ["last_name"] = ReportedDisclosure.PerRow,
                    ["dob"] = ReportedDisclosure.PerRow,
                    ["national_id"] = ReportedDisclosure.PerRow,
                    ["postcode"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The same principal whose `orders.amount` is refused in case 076: on "
                + "`members` nothing is population-only, so a mixed principal is answered "
                + "per row.",
        },
        new PolicyBatteryCase
        {
            Id = "010",
            Group = "A",
            Covers = "§8 corpus 02; §3.6 — no guard where the column is FULL",
            File = "cases/010-orders-group-org-sum-u1.sql",
            Principal = "u1",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 8, 1400.00m],
                    [2, 6, 1800.00m],
                    [3, 1, 700.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["org_id"] = ReportedDisclosure.Full,
                    ["n"] = ReportedDisclosure.Full,
                    ["s"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The third group is the single order u1 created in O3, which the "
                + "created-by fail-safe makes visible; it is one row and is not suppressed, "
                + "because `amount` is FULL for this principal and no guard is emitted at "
                + "all.",
            Disputed = new PolicyAdjudication(
                "s's disclosure PerRow",
                "s's disclosure Full",
                "The engine is right: the table's row predicate carries the "
                    + "resource-owner fail-safe or a subject grant, so two rules stay "
                    + "reachable at the leaf and the disclosure is decided per row. The "
                    + "corpus computed what the rows turned out to be, which is a different "
                    + "question and one D207 forbids an acknowledgement from depending on."),
        },
        new PolicyBatteryCase
        {
            Id = "011",
            Group = "A",
            Covers = "§8 corpus 02; §3.4 the permitted consumer; §3.6 the guard",
            File = "cases/011-orders-group-org-sum-u6.sql",
            Principal = "u6",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 8, 1400.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["org_id"] = ReportedDisclosure.Full,
                    ["n"] = ReportedDisclosure.Full,
                    ["s"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "One visible tenancy, eight rows, above the floor. `COUNT(*)` is not over "
                + "the column and is reported Full; `SUM(amount)` is guarded and reported "
                + "Aggregate, whose NULL below the floor a grid must not read as `no rows`.",
        },
        new PolicyBatteryCase
        {
            Id = "012",
            Group = "A",
            Covers = "§8 corpus 02 and 13; §3.6",
            File = "cases/012-orders-group-org-sum-u11.sql",
            Principal = "u11",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 8, 1400.00m],
                    [3, 2, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["org_id"] = ReportedDisclosure.Full,
                    ["n"] = ReportedDisclosure.Full,
                    ["s"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The whole point of the fixture's org sizes: one group of 8 and one of 2 "
                + "in one result. The group below the floor keeps its `COUNT(*)`, which the "
                + "design accepts as the stated limit of query-set-size control.",
        },
        new PolicyBatteryCase
        {
            Id = "013",
            Group = "A",
            Covers = "§8 corpus 02; §3.4 the mixed principal under a permitted consumer; §3.5",
            File = "cases/013-orders-group-org-sum-u12.sql",
            Principal = "u12",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 8, 1400.00m],
                    [2, 6, 1800.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["org_id"] = ReportedDisclosure.Full,
                    ["n"] = ReportedDisclosure.Full,
                    ["s"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "UNCERTAIN (README §5 U4). AGGREGATE_ONLY is reachable for u12, and every "
                + "consumer is an allow-listed aggregate, so §3.5 emits the column tainted "
                + "and §3.6 guards every call over it — including the manager's own "
                + "tenancy, where the same principal may see the values one at a time (case "
                + "077). Taint is per column at a leaf, not per row, so the guard is "
                + "all-or-nothing; the design does not say this in so many words.",
        },
        new PolicyBatteryCase
        {
            Id = "014",
            Group = "A",
            Covers = "§8 corpus 03; D204 the subject dimension on a second table",
            File = "cases/014-orders-star-u3.sql",
            Principal = "u3",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [107, 4, 1, new DateOnly(2026, 1, 21), "urgent", 100.00m, "OPEN", 2],
                    [108, 4, 1, new DateOnly(2026, 1, 27), "return", 250.00m, "DONE", 2],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["member_id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["placed_at"] = ReportedDisclosure.Full,
                    ["note"] = ReportedDisclosure.Full,
                    ["amount"] = ReportedDisclosure.Full,
                    ["status"] = ReportedDisclosure.Full,
                    ["created_by"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The member sees their own orders and nothing else. Both rows were "
                + "created by u2, so the created-by rule plays no part here — the subject "
                + "rule is what discloses them.",
            Disputed = new PolicyAdjudication(
                "note's disclosure PerRow; amount's disclosure PerRow",
                "note's disclosure Full; amount's disclosure Full",
                "The engine is right: the table's row predicate carries the "
                    + "resource-owner fail-safe or a subject grant, so two rules stay "
                    + "reachable at the leaf and the disclosure is decided per row. The "
                    + "corpus computed what the rows turned out to be, which is a different "
                    + "question and one D207 forbids an acknowledgement from depending on."),
        },
        new PolicyBatteryCase
        {
            Id = "015",
            Group = "A",
            Covers = "§8 corpus 03 second half; D206 CreatorSees = Full",
            File = "cases/015-orders-star-u1.sql",
            Principal = "u1",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [101, 1, 1, new DateOnly(2026, 1, 5), "first order", 100.00m, "DONE", 1],
                    [102, 1, 1, new DateOnly(2026, 1, 7), "repeat", 200.00m, "DONE", 1],
                    [103, 2, 1, new DateOnly(2026, 1, 9), "gift", 150.00m, "OPEN", 2],
                    [104, 2, 1, new DateOnly(2026, 1, 12), "bulk", 250.00m, "OPEN", 1],
                    [105, 1, 1, new DateOnly(2026, 1, 15), "sample", 50.00m, "HOLD", 3],
                    [106, 3, 1, new DateOnly(2026, 1, 19), "renewal", 300.00m, "NEW", 95],
                    [107, 4, 1, new DateOnly(2026, 1, 21), "urgent", 100.00m, "OPEN", 2],
                    [108, 4, 1, new DateOnly(2026, 1, 27), "return", 250.00m, "DONE", 2],
                    [109, 5, 2, new DateOnly(2026, 2, 2), "********", 450.00m, "DONE", 5],
                    [110, 5, 2, new DateOnly(2026, 2, 5), "********", 150.00m, "OPEN", 5],
                    [111, 6, 2, new DateOnly(2026, 2, 8), "********", 200.00m, "NEW", 6],
                    [112, 6, 2, new DateOnly(2026, 2, 11), "********", 100.00m, "HOLD", 95],
                    [113, 7, 2, new DateOnly(2026, 2, 14), "********", 500.00m, "OPEN", 6],
                    [114, 7, 2, new DateOnly(2026, 2, 18), "********", 400.00m, "DONE", 5],
                    [115, 8, 3, new DateOnly(2026, 3, 3), "first order", 700.00m, "OPEN", 1],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["member_id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["placed_at"] = ReportedDisclosure.Full,
                    ["note"] = ReportedDisclosure.PerRow,
                    ["amount"] = ReportedDisclosure.Full,
                    ["status"] = ReportedDisclosure.Full,
                    ["created_by"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Order 115 is in O3, where u1 holds no grant: the created-by disjunct "
                + "makes the row visible and the created-by rule discloses it in full. "
                + "Order 111's raw note is NULL and the agent's constant mask replaces it, "
                + "so masking hides NULL-ness too (case 061). `amount` is Full for u1 even "
                + "in the agent's org, because `amount` is in no realm and the agent's "
                + "grant leaves the table default.",
            Disputed = new PolicyAdjudication(
                "amount's disclosure PerRow",
                "amount's disclosure Full",
                "The engine is right: the table's row predicate carries the "
                    + "resource-owner fail-safe or a subject grant, so two rules stay "
                    + "reachable at the leaf and the disclosure is decided per row. The "
                    + "corpus computed what the rows turned out to be, which is a different "
                    + "question and one D207 forbids an acknowledgement from depending on."),
        },
        new PolicyBatteryCase
        {
            Id = "016",
            Group = "A",
            Covers = "§8 corpus 07 second half; §3.4, §3.5, D160 — a star cannot widen past a "
                + "rule",
            File = "cases/016-orders-star-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.Projection },
            },
            Notes = "The star expands before the rewrite sees the tree, so `amount` is an "
                + "ordinary projection use of a population-only column and the statement is "
                + "refused. This is why an auditor's `SELECT *` is an error while their "
                + "`SELECT SUM(amount)` is not.",
        },
        new PolicyBatteryCase
        {
            Id = "017",
            Group = "A",
            Covers = "§8 corpus 25 on `orders`; D206",
            File = "cases/017-orders-star-u10.sql",
            Principal = "u10",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [106, 3, 1, new DateOnly(2026, 1, 19), "renewal", 300.00m, "NEW", 95],
                    [112, 6, 2, new DateOnly(2026, 2, 11), "renewal", 100.00m, "HOLD", 95],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["member_id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["placed_at"] = ReportedDisclosure.Full,
                    ["note"] = ReportedDisclosure.Full,
                    ["amount"] = ReportedDisclosure.Full,
                    ["status"] = ReportedDisclosure.Full,
                    ["created_by"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Two rows in two tenancies, neither of which this principal holds a grant "
                + "in. The row predicate is `created_by = 95` after folding, which is not "
                + "FALSE, so visibility is SOME rather than NONE.",
        },
        new PolicyBatteryCase
        {
            Id = "018",
            Group = "A",
            Covers = "§8 corpus 04; §3.1 leaf sanitisation replaces the query-field rule",
            File = "cases/018-members-like-t-u1.sql",
            Principal = "u1",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "Tomas", "Kalny", new DateOnly(1980, 3, 14), "NID-1001", "2000"],
                    [4, 1, "Tao", "Lindqvist", new DateOnly(1988, 1, 9), "NID-1004", "2000"],
                    [5, 2, "T", "M", new DateOnly(1993, 1, 1), null, "4101"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.PerRow,
                    ["last_name"] = ReportedDisclosure.PerRow,
                    ["dob"] = ReportedDisclosure.PerRow,
                    ["national_id"] = ReportedDisclosure.PerRow,
                    ["postcode"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Exactly the design's stated expectation: O1 rows whose name starts with "
                + "T, and the O2 row whose INITIAL is T. Member 8 is also called Tao but is "
                + "in O3 and invisible.",
        },
        new PolicyBatteryCase
        {
            Id = "019",
            Group = "A",
            Covers = "§8 corpus 04, as u2",
            File = "cases/019-members-like-t-u2.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
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
            },
            Notes = "Every visible value is one character, so `LIKE 'T%'` is an equality on "
                + "the initial.",
        },
        new PolicyBatteryCase
        {
            Id = "020",
            Group = "A",
            Covers = "§8 corpus 05; §3.1",
            File = "cases/020-members-national-id-null-u2.sql",
            Principal = "u2",
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
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Every visible row matches, because the disclosed value is the NULL mask. "
                + "The raw column is NOT NULL for every row in the fixture, so a "
                + "query-field rule would have returned nothing here and leaf sanitisation "
                + "returns everything.",
        },
        new PolicyBatteryCase
        {
            Id = "021",
            Group = "A",
            Covers = "§8 corpus 05, as a mixed principal",
            File = "cases/021-members-national-id-null-u1.sql",
            Principal = "u1",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [5, 2, "T", "M", new DateOnly(1993, 1, 1), null, "4101"],
                    [6, 2, "U", "B", new DateOnly(1969, 1, 1), null, "4101"],
                    [7, 2, "N", "H", new DateOnly(1985, 1, 1), null, "5000"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.PerRow,
                    ["last_name"] = ReportedDisclosure.PerRow,
                    ["dob"] = ReportedDisclosure.PerRow,
                    ["national_id"] = ReportedDisclosure.PerRow,
                    ["postcode"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The predicate selects exactly the rows whose disclosure is MASKED — the "
                + "agent's org — which is the sharpest statement of what a predicate on a "
                + "masked column means.",
        },
        new PolicyBatteryCase
        {
            Id = "022",
            Group = "A",
            Covers = "§8 corpus 06",
            File = "cases/022-members-national-id-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [null],
                    [null],
                    [null],
                    [null],
                    [null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["national_id"] = ReportedDisclosure.Masked,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Five rows, all NULL, reported Masked and not Undisclosed: the rows are "
                + "visible and it is the value that is withheld by a mask, which is a "
                + "different acknowledgement from a column the principal may not see at "
                + "all.",
        },
        new PolicyBatteryCase
        {
            Id = "023",
            Group = "A",
            Covers = "§8 corpus 07; §3.4, §3.6, §3.10 clause 1",
            File = "cases/023-orders-avg-amount-u6.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [175.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["a"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "1400 over 8 rows. UNCERTAIN (README §5 U13): the value is exact, the "
                + "SCALE of an AVG over DECIMAL(10,2) is Calcite's division rule and this "
                + "corpus does not pin it — the kit should compare numerically. The "
                + "allow-list is checked before the Hep pass, because "
                + "AGGREGATE_REDUCE_FUNCTIONS later rewrites AVG into SUM0 and COUNT.",
        },
        new PolicyBatteryCase
        {
            Id = "024",
            Group = "A",
            Covers = "§8 corpus 07 second half; §3.4 a value use",
            File = "cases/024-orders-amount-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.Projection },
            },
            Notes = "The counterpart to 023 over the same table, principal and column: what "
                + "is refused is the use, not the column.",
        },
        new PolicyBatteryCase
        {
            Id = "025",
            Group = "A",
            Covers = "§8 corpus 08; §3.1 two leaves, two sanitisers",
            File = "cases/025-join-members-orders-u1.sql",
            Principal = "u1",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["Kalny", "first order"],
                    ["Kalny", "repeat"],
                    ["Osei", "gift"],
                    ["Osei", "bulk"],
                    ["Kalny", "sample"],
                    ["Vance", "renewal"],
                    ["Lindqvist", "urgent"],
                    ["Lindqvist", "return"],
                    ["M", "********"],
                    ["M", "********"],
                    ["B", "********"],
                    ["B", "********"],
                    ["H", "********"],
                    ["H", "********"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["last_name"] = ReportedDisclosure.PerRow,
                    ["note"] = ReportedDisclosure.PerRow,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Fourteen rows: order 115 drops out because its member is in O3 and "
                + "invisible, so the join composes the two row predicates without either "
                + "one knowing about the other. Member 10 has no orders and does not "
                + "appear.",
        },
        new PolicyBatteryCase
        {
            Id = "026",
            Group = "A",
            Covers = "§8 corpus 08, as u2; D206 inside a join",
            File = "cases/026-join-members-orders-u2.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["K", "********"],
                    ["K", "********"],
                    ["O", "gift"],
                    ["O", "********"],
                    ["K", "********"],
                    ["V", "********"],
                    ["L", "urgent"],
                    ["L", "return"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["last_name"] = ReportedDisclosure.Masked,
                    ["note"] = ReportedDisclosure.PerRow,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Orders 103, 107 and 108 were created by u2, so the creator rule "
                + "discloses their notes while the agent's mask covers the rest — one "
                + "column, two disclosures, one join.",
        },
        new PolicyBatteryCase
        {
            Id = "027",
            Group = "A",
            Covers = "§8 corpus 09; §3 the pass recurses into Correlate",
            File = "cases/027-lateral-order-count-u1.sql",
            Principal = "u1",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 3],
                    [2, 2],
                    [3, 1],
                    [4, 2],
                    [5, 2],
                    [6, 2],
                    [7, 2],
                    [10, 0],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The lateral's own scan is entitled, so the counts are of VISIBLE orders. "
                + "Member 8's order 115 is visible to u1 but member 8 is not, so it "
                + "contributes to no row here.",
        },
        new PolicyBatteryCase
        {
            Id = "028",
            Group = "A",
            Covers = "§8 corpus 09, as u2",
            File = "cases/028-lateral-order-count-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 3],
                    [2, 2],
                    [3, 1],
                    [4, 2],
                    [10, 0],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Member 10 has no orders at all, so the decorrelated join must keep the "
                + "row with a count of zero rather than dropping it.",
        },
        new PolicyBatteryCase
        {
            Id = "029",
            Group = "A",
            Covers = "§8 corpus 10; §3.12 visibility; D206",
            File = "cases/029-notes-body-u5.sql",
            Principal = "u5",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["body"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["notes"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "FINDING (README §5 F1). §8's table says `visibility = NONE` here. It "
                + "cannot be: the created-by fail-safe puts `created_by = @ctx.user` in the "
                + "row predicate of every table that carries the option, and a comparison "
                + "against a bound scalar folds to neither TRUE nor FALSE, so the predicate "
                + "is SOME for every principal. Zero rows and no error is the part of §8's "
                + "expectation that holds; case 031 is where NONE really happens.",
            Disputed = new PolicyAdjudication(
                "body's disclosure Full",
                "body's disclosure Undisclosed",
                "The engine is right and the corpus contradicted its own descriptor: "
                    + "the resource-owner rule stays reachable for a principal with no "
                    + "grants, so the constant is Full - over zero rows, since they created "
                    + "nothing."),
        },
        new PolicyBatteryCase
        {
            Id = "030",
            Group = "A",
            Covers = "§8 corpus 10 second half; D207 RefuseWhenNoVisibleRows",
            File = "cases/030-notes-body-u5-refuse.sql",
            Principal = "u5",
            Options = PolicyCaseOptions.Default with { RefuseWhenNoVisibleRows = true },
            Expect = new PolicyExpectation
            {
                Rows =
                [
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["body"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["notes"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "FINDING (README §5 F1), the consequence: §8 expects POLICY here, and "
                + "with visibility SOME the option has nothing to fire on. Either the "
                + "option must also fire on a statement that returns no rows — which D207 "
                + "forbids, since that is a fact about hidden rows — or §8's expectation "
                + "for corpus 10 belongs on a table without the created-by fail-safe.",
            Disputed = new PolicyAdjudication(
                "body's disclosure Full",
                "body's disclosure Undisclosed",
                "The engine is right and the corpus contradicted its own descriptor: "
                    + "the resource-owner rule stays reachable for a principal with no "
                    + "grants, so the constant is Full - over zero rows, since they created "
                    + "nothing."),
        },
        new PolicyBatteryCase
        {
            Id = "031",
            Group = "A",
            Covers = "D207; §3.12 visibility = NONE",
            File = "cases/031-members-first-name-u5-refuse.sql",
            Principal = "u5",
            Options = PolicyCaseOptions.Default with { RefuseWhenNoVisibleRows = true },
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "members", Use = PolicyUse.NoVisibleRows },
            },
            Notes = "The refusal §8's corpus 10 asks for, on a table where the visibility "
                + "really does fold to NONE. The acknowledgement depends on the principal's "
                + "context alone (D207).",
        },
        new PolicyBatteryCase
        {
            Id = "032",
            Group = "A",
            Covers = "§8 corpus 11; §3.1 a sort key on a masked column",
            File = "cases/032-members-order-by-last-name-limit-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "T", "K", new DateOnly(1980, 1, 1), null, "2000"],
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
            },
            Notes = "The initials are K, O, V, L and S, so the first row by the DISCLOSED "
                + "value is member 1. By the raw surnames it would have been the same row "
                + "(Kalny), so the case also fixes the shape rather than only the value: "
                + "the sort is over the mask.",
        },
        new PolicyBatteryCase
        {
            Id = "033",
            Group = "A",
            Covers = "§8 corpus 13; §3.6",
            File = "cases/033-orders-avg-by-org-u11.sql",
            Principal = "u11",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 175.00m],
                    [3, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["org_id"] = ReportedDisclosure.Full,
                    ["a"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The two-order group's average would have been 800.00. UNCERTAIN as to "
                + "scale, as in 023.",
        },
        new PolicyBatteryCase
        {
            Id = "034",
            Group = "A",
            Covers = "§8 corpus 15; §0 zero cost when unused",
            File = "cases/034-symbols-star-u5.sql",
            Principal = "u5",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["NEW", "New", 1],
                    ["OPEN", "Open", 2],
                    ["HOLD", "On hold", 3],
                    ["DONE", "Done", 4],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["code"] = ReportedDisclosure.Full,
                    ["label"] = ReportedDisclosure.Full,
                    ["sort_order"] = ReportedDisclosure.Full,
                },
            },
            Notes = "A principal with no grants at all sees unrestricted reference data in "
                + "full, and the report has no row for the table because the table carries "
                + "no entitlement.",
        },
        new PolicyBatteryCase
        {
            Id = "035",
            Group = "A",
            Covers = "§8 corpus 15b; the Custom row predicate over a host-computed list",
            File = "cases/035-invites-star-u1.sql",
            Principal = "u1",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [301, 1, "ext-1@example.org"],
                    [302, 1, "ext-2@example.org"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["target"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["invites"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "`invites` has no dimension and no protected column: it exists to show "
                + "that a row predicate alone is a complete entitlement, and that the list "
                + "it folds is the host's.",
        },
        new PolicyBatteryCase
        {
            Id = "036",
            Group = "A",
            Covers = "§8 corpus 15b with an empty list; D197; §3.12",
            File = "cases/036-invites-star-u5.sql",
            Principal = "u5",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["target"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["invites"] = new() { Visibility = TableVisibility.None },
                },
            },
            Notes = "The second table on which a principal's visibility is genuinely NONE, "
                + "and the one that shows an empty list folding a whole predicate to FALSE.",
        },
        new PolicyBatteryCase
        {
            Id = "037",
            Group = "A",
            Covers = "§8 corpus 20; §3.12",
            File = "cases/037-members-org3-u1.sql",
            Principal = "u1",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.PerRow,
                    ["last_name"] = ReportedDisclosure.PerRow,
                    ["dob"] = ReportedDisclosure.PerRow,
                    ["national_id"] = ReportedDisclosure.PerRow,
                    ["postcode"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Zero rows with visibility SOME: the principal HAS a grant, and this "
                + "statement asked outside it. The contradiction that names `org_id` is "
                + "part 2b — case 136.",
            Disputed = new PolicyAdjudication(
                "first_name's disclosure Redacted; last_name's disclosure Redacted; "
                    + "dob's disclosure Redacted; national_id's disclosure Redacted",
                "first_name's disclosure PerRow; last_name's disclosure PerRow; dob's "
                    + "disclosure PerRow; national_id's disclosure PerRow",
                "The engine is right, and this is the remedy of section 3.4 seen from "
                    + "the report's side (V63): the leaf simplifies under the statement's "
                    + "own conjuncts and the report reads the leaf, so the statement's own "
                    + "filter narrows the label (README section 5 U17)."),
        },
        new PolicyBatteryCase
        {
            Id = "038",
            Group = "A",
            Covers = "§8 corpus 24; D204 `within`",
            File = "cases/038-orders-star-u3b.sql",
            Principal = "u3b",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["member_id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["placed_at"] = ReportedDisclosure.Full,
                    ["note"] = ReportedDisclosure.Redacted,
                    ["amount"] = ReportedDisclosure.Redacted,
                    ["status"] = ReportedDisclosure.Full,
                    ["created_by"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The same member id, confined to the wrong tenancy: `(member_id, org_id) "
                + "IN ((4, 2))` matches no row, and the fixture deliberately has member 4 "
                + "create nothing, so the created-by disjunct cannot rescue the case the "
                + "design wrote as zero rows.",
            Disputed = new PolicyAdjudication(
                "note's disclosure PerRow; amount's disclosure PerRow",
                "note's disclosure Undisclosed; amount's disclosure Undisclosed",
                "The engine is right: the table's row predicate carries the "
                    + "resource-owner fail-safe or a subject grant, so two rules stay "
                    + "reachable at the leaf and the disclosure is decided per row. The "
                    + "corpus computed what the rows turned out to be, which is a different "
                    + "question and one D207 forbids an acknowledgement from depending on."),
        },
        new PolicyBatteryCase
        {
            Id = "039",
            Group = "A",
            Covers = "§8 corpus 25; D206 CreatorSees = Full",
            File = "cases/039-notes-star-u10-creator-full.sql",
            Principal = "u10",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [203, 1, 95, "draft policy"],
                    [204, 1, 95, "follow-up"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["created_by"] = ReportedDisclosure.Full,
                    ["body"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["notes"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "DEVIATION from §8, which runs this case as u5. u5 must create nothing "
                + "for corpus 10 to return zero rows, so the fixture gives the created rows "
                + "to u10, a principal with the same (empty) grants.",
        },
        new PolicyBatteryCase
        {
            Id = "040",
            Group = "A",
            Covers = "§8 corpus 25 second half; D206 CreatorSees = ByRules",
            File = "cases/040-notes-star-u10-creator-by-rules.sql",
            Principal = "u10",
            Catalog = PolicyCatalog.Default with { Notes = PolicyEntitlements.NotesByrules },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [203, 1, 95, null],
                    [204, 1, 95, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["created_by"] = ReportedDisclosure.Full,
                    ["body"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["notes"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The row predicate keeps the fail-safe — the rows are visible — and only "
                + "the column rules change, which is exactly the distinction D206 draws.",
        },
        new PolicyBatteryCase
        {
            Id = "041",
            Group = "A",
            Covers = "§8 corpus 01, as u9; D197 an empty list beside a bound one",
            File = "cases/041-members-star-u9.sql",
            Principal = "u9",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [5, 2, "T", "M", new DateOnly(1993, 1, 1), null, "4101"],
                    [6, 2, "U", "B", new DateOnly(1969, 1, 1), null, "4101"],
                    [7, 2, "N", "H", new DateOnly(1985, 1, 1), null, "5000"],
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
            },
            Notes = "`manager_orgs` is bound and empty, so its disjunct folds to FALSE and "
                + "the manager rule is dead; the agent's list is what survives, in the "
                + "predicate and in every rule.",
        },
        new PolicyBatteryCase
        {
            Id = "042",
            Group = "A",
            Covers = "§8 corpus 01, as u11; one mask key over two tenancies",
            File = "cases/042-members-star-u11.sql",
            Principal = "u11",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "!fp:Tomas", "!fp:Kalny", null, null, "2000"],
                    [2, 1, "!fp:Rina", "!fp:Osei", null, null, "2000"],
                    [3, 1, "!fp:Petra", "!fp:Vance", null, null, "3050"],
                    [4, 1, "!fp:Tao", "!fp:Lindqvist", null, null, "2000"],
                    [10, 1, "!fp:Rina", "!fp:Sandoval", null, null, "3050"],
                    [8, 3, "!fp:Tao", "!fp:Elmi", null, null, "6060"],
                    [9, 3, "!fp:Mira", "!fp:Kovac", null, null, "6060"],
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
            },
            Notes = "Members 4 and 8 are both called Tao in different tenancies and therefore "
                + "carry the same token, which is what case 178 joins on. The mask key is "
                + "per principal, so the tokens of this case and of case 006 are different "
                + "strings for the same names.",
        },
        new PolicyBatteryCase
        {
            Id = "043",
            Group = "B",
            Covers = "§3.12 — Masked in a projection",
            File = "cases/043-members-first-name-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["T"],
                    ["R"],
                    ["P"],
                    ["T"],
                    ["R"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["first_name"] = ReportedDisclosure.Masked,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Ordered by id: 1, 2, 3, 4, 10.",
        },
        new PolicyBatteryCase
        {
            Id = "044",
            Group = "B",
            Covers = "§3.12 D202 — PerRow in a projection",
            File = "cases/044-members-first-name-u1.sql",
            Principal = "u1",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["Tomas"],
                    ["Rina"],
                    ["Petra"],
                    ["Tao"],
                    ["T"],
                    ["U"],
                    ["N"],
                    ["Rina"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["first_name"] = ReportedDisclosure.PerRow,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Ordered by id: 1, 2, 3, 4, 5, 6, 7, 10 — the masked rows are 5, 6 and 7.",
        },
        new PolicyBatteryCase
        {
            Id = "045",
            Group = "B",
            Covers = "§3.1 — Masked in a filter",
            File = "cases/045-members-filter-masked-eq-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [4],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Tomas and Tao both disclose as 'T'; the predicate can no longer tell "
                + "them apart, and that is the point of the mask.",
        },
        new PolicyBatteryCase
        {
            Id = "046",
            Group = "B",
            Covers = "§3.1 — a filter over a PerRow column",
            File = "cases/046-members-filter-masked-eq-u1.sql",
            Principal = "u1",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [5],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The one-character literal can only match where the initial mask applied, "
                + "so the same statement selects the agent's org for this principal and the "
                + "manager's for u2.",
        },
        new PolicyBatteryCase
        {
            Id = "047",
            Group = "B",
            Covers = "§3.1 — Masked as a join key",
            File = "cases/047-members-join-masked-key-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 4],
                    [2, 10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["a_id"] = ReportedDisclosure.Full,
                    ["b_id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Members 1 and 4 have different names and the same initial, so masking "
                + "created this pair. Members 2 and 10 really do share a name.",
        },
        new PolicyBatteryCase
        {
            Id = "048",
            Group = "B",
            Covers = "§3.1 — a join key over a PerRow column",
            File = "cases/048-members-join-masked-key-u1.sql",
            Principal = "u1",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [2, 10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["a_id"] = ReportedDisclosure.Full,
                    ["b_id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "u1 sees more rows than u2 and fewer pairs, because raw values and "
                + "initials do not match each other and the O2 initials T, U and N are "
                + "distinct.",
        },
        new PolicyBatteryCase
        {
            Id = "049",
            Group = "B",
            Covers = "§3.1 — Masked as a grouping key",
            File = "cases/049-members-group-masked-key-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["P", 1],
                    ["R", 2],
                    ["T", 2],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["first_name"] = ReportedDisclosure.Masked,
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "No group-size floor applies: the floor belongs to AGGREGATE_ONLY, and a "
                + "masked value is ordinary data. A host that wants k-anonymity on a masked "
                + "key opts into `statistical` (group L), which is a different guarantee.",
        },
        new PolicyBatteryCase
        {
            Id = "050",
            Group = "B",
            Covers = "§3.1 — Masked as a sort key",
            File = "cases/050-members-sort-masked-key-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, "K"],
                    [4, "L"],
                    [2, "O"],
                    [10, "S"],
                    [3, "V"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["last_name"] = ReportedDisclosure.Masked,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "By the raw surnames the order would have been Kalny, Lindqvist, Osei, "
                + "Sandoval, Vance — the same, by construction of the fixture's initials, "
                + "so this case pins the values and case 032 pins that it is the mask that "
                + "is compared.",
        },
        new PolicyBatteryCase
        {
            Id = "051",
            Group = "B",
            Covers = "§3.1 — Masked as a window key",
            File = "cases/051-members-window-masked-partition-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1],
                    [2, 1],
                    [3, 1],
                    [4, 2],
                    [10, 2],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["rn"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Partitions T = {1, 4}, R = {2, 10}, P = {3}: the window partitions on "
                + "the disclosed value. A window over a MASKED column is permitted; a "
                + "window over an AGGREGATE_ONLY column is not (case 072).",
        },
        new PolicyBatteryCase
        {
            Id = "052",
            Group = "B",
            Covers = "§3.1 — Masked as an aggregate argument",
            File = "cases/052-members-count-distinct-masked-u2.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [3],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Three distinct initials over five members. Reported Full, not Aggregate: "
                + "the count is derived from masked values, and the meet of §3.12 has no "
                + "rule that turns a count over a MASKED column into anything but the "
                + "count's own disclosure. UNCERTAIN (README §5 U2) — the design does not "
                + "say what a derived column over a masked origin reports, and 'Masked' is "
                + "the other reading.",
            Disputed = new PolicyAdjudication(
                "n's disclosure Masked",
                "n's disclosure Full",
                "The engine is right and its answer is the conservative one: §3.12's "
                    + "meet is over a column's origins, and a count's origin is a masked "
                    + "column (README §5 U2)."),
        },
        new PolicyBatteryCase
        {
            Id = "053",
            Group = "B",
            Covers = "§3.4, §3.6 — AGGREGATE_ONLY as an aggregate argument",
            File = "cases/053-orders-sum-amount-u6.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1400.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["s"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The one shape a population-only column may take.",
        },
        new PolicyBatteryCase
        {
            Id = "054",
            Group = "B",
            Covers = "D159, D161 — Undisclosed in a projection",
            File = "cases/054-members-undisclosed-projection-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersNone },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [null],
                    [null],
                    [null],
                    [null],
                    [null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["postcode"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Five visible rows, five placeholders.",
        },
        new PolicyBatteryCase
        {
            Id = "055",
            Group = "B",
            Covers = "§3.5 — Undisclosed in a filter",
            File = "cases/055-members-undisclosed-filter-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersNone },
            Expect = new PolicyExpectation
            {
                Rows =
                [
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The comparison is against the placeholder, which under "
                + "PlaceholdersAsNull is NULL, so every row evaluates to UNKNOWN. Under "
                + "PlaceholdersAsEmpty the same statement would compare against '' and also "
                + "return nothing — but a predicate such as `postcode = ''` WOULD then "
                + "return every row, which is the sharp edge D162 warns about and case 098 "
                + "shows.",
        },
        new PolicyBatteryCase
        {
            Id = "056",
            Group = "B",
            Covers = "§3.5 — Undisclosed as a sort key",
            File = "cases/056-members-undisclosed-sort-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersNone },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [2],
                    [3],
                    [4],
                    [10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Every placeholder is equal, so the tie-break on id decides the whole "
                + "order.",
        },
        new PolicyBatteryCase
        {
            Id = "057",
            Group = "C",
            Covers = "§3.1 D195",
            File = "cases/057-members-like-k-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Under the superseded query-field rule this statement returned nothing "
                + "for an agent.",
        },
        new PolicyBatteryCase
        {
            Id = "058",
            Group = "C",
            Covers = "§3.1",
            File = "cases/058-members-like-k-u1.sql",
            Principal = "u1",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The same answer by a different route: the raw surname matches in the "
                + "manager's org, and no initial in the agent's org is K.",
        },
        new PolicyBatteryCase
        {
            Id = "059",
            Group = "C",
            Covers = "§3.1 — any scalar function over a sanitised value",
            File = "cases/059-members-char-length-masked-u1.sql",
            Principal = "u1",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [5],
                    [6],
                    [7],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The statement can tell which rows are masked, which is not a disclosure "
                + "of the value — and is the same thing the per-column report and the "
                + "sibling columns say out loud.",
        },
        new PolicyBatteryCase
        {
            Id = "060",
            Group = "C",
            Covers = "§3.2 — a constant mask",
            File = "cases/060-orders-note-eq-mask-u9.sql",
            Principal = "u9",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [109],
                    [110],
                    [111],
                    [112],
                    [113],
                    [114],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "All six visible orders, order 111 included, whose raw note is NULL.",
        },
        new PolicyBatteryCase
        {
            Id = "061",
            Group = "C",
            Covers = "§3.2 — a mask hides NULL-ness",
            File = "cases/061-orders-note-is-null-u9.sql",
            Principal = "u9",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "A constant mask is not NULL-preserving. Whether a host WANTS that is a "
                + "policy question the descriptor can answer either way — `CASE WHEN note "
                + "IS NULL THEN NULL ELSE '********' END` is a legal mask of the column's "
                + "type — and the design does not prefer one, so the fixture takes the "
                + "simple mask and this case records the effect.",
        },
        new PolicyBatteryCase
        {
            Id = "062",
            Group = "C",
            Covers = "§3.2 — the same statement with no mask in the way",
            File = "cases/062-orders-note-is-null-u4.sql",
            Principal = "u4",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [111],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.All },
                },
            },
            Notes = "The row a masked principal cannot find.",
        },
        new PolicyBatteryCase
        {
            Id = "063",
            Group = "C",
            Covers = "§3.1 — an IN list over a masked column",
            File = "cases/063-members-in-list-masked-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [2],
                    [4],
                    [10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Four of the five visible rows; Petra's P is not in the list.",
        },
        new PolicyBatteryCase
        {
            Id = "064",
            Group = "C",
            Covers = "§3.1",
            File = "cases/064-members-in-list-masked-u1.sql",
            Principal = "u1",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [5],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Only the agent's org has one-character values, and only Tessa's initial "
                + "is in the list.",
        },
        new PolicyBatteryCase
        {
            Id = "065",
            Group = "D",
            Covers = "§8 corpus 12; §3.4 a value use",
            File = "cases/065-orders-amount-arith-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.Expression },
            },
            Notes = "`amount - amount` is constant zero and is still refused: the criterion "
                + "is the shape of the use, not what the expression happens to compute.",
        },
        new PolicyBatteryCase
        {
            Id = "066",
            Group = "D",
            Covers = "§8 corpus 12; §3.4 a query use",
            File = "cases/066-orders-amount-filter-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.Filter },
            },
            Notes = "A predicate on a raw value is an oracle.",
        },
        new PolicyBatteryCase
        {
            Id = "067",
            Group = "D",
            Covers = "§8 corpus 12; §3.4",
            File = "cases/067-orders-amount-sort-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.SortKey },
            },
            Notes = "Note the contrast with case 032: a sort on a MASKED column is fine and "
                + "sorts the masks; a sort on an AGGREGATE_ONLY column is refused.",
        },
        new PolicyBatteryCase
        {
            Id = "068",
            Group = "D",
            Covers = "§8 corpus 12; §3.4",
            File = "cases/068-orders-amount-group-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.GroupingKey },
            },
            Notes = "Without `statistical` a raw grouping key is refused; case 160 is the "
                + "same shape with the opt-in on.",
        },
        new PolicyBatteryCase
        {
            Id = "069",
            Group = "D",
            Covers = "§8 corpus 12; §3.4",
            File = "cases/069-orders-amount-join-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.JoinCondition },
            },
            Notes = "Both occurrences of the table are traced; either one alone would refuse.",
        },
        new PolicyBatteryCase
        {
            Id = "070",
            Group = "D",
            Covers = "§8 corpus 12; §3.4 the strict argument shape; F30",
            File = "cases/070-orders-sum-amount-times-two-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.Expression },
            },
            Notes = "An allow-listed aggregate is not enough: its argument must be a bare "
                + "reference. Products with an untainted column are registered as future "
                + "work F30.",
        },
        new PolicyBatteryCase
        {
            Id = "071",
            Group = "D",
            Covers = "§8 corpus 12; §3.4 the FILTER clause",
            File = "cases/071-orders-sum-amount-filter-clause-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.AggregateFilter },
            },
            Notes = "The design's own reasoning: two filtered sums differing by one row "
                + "reveal that row's value by subtraction, guard or no guard.",
        },
        new PolicyBatteryCase
        {
            Id = "072",
            Group = "D",
            Covers = "§8 corpus 12; §3.4 RexOver",
            File = "cases/072-orders-sum-amount-window-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.WindowAggregate },
            },
            Notes = "A window's partition can be one row, so no floor can be enforced on it.",
        },
        new PolicyBatteryCase
        {
            Id = "073",
            Group = "D",
            Covers = "§8 corpus 12 last clause; §3.4 the permitted CAST",
            File = "cases/073-orders-avg-cast-double-u6.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [175.0m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["a"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "FP64 rather than DECIMAL, so the value is exact and the scale question "
                + "of case 023 does not arise. Compare within 4 ULPs, per "
                + "docs/design/05-testing.md §5.",
        },
        new PolicyBatteryCase
        {
            Id = "074",
            Group = "D",
            Covers = "D190 — not a population aggregate at all",
            File = "cases/074-orders-min-amount-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.AggregateNotPopulation },
            },
            Notes = "MIN returns one row's value, which is why D190 keeps it, MAX, ANY_VALUE, "
                + "the positional and holistic aggregates and every string aggregate out of "
                + "the permitted set entirely.",
        },
        new PolicyBatteryCase
        {
            Id = "075",
            Group = "D",
            Covers = "§3.4 — a population aggregate outside THIS column's allow-list",
            File = "cases/075-orders-stddev-amount-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.AggregateNotAllowListed },
            },
            Notes = "STDDEV_POP is in D190's permitted set; the column's "
                + "`aggregate_only_functions` is the narrower list, and it is the one that "
                + "decides.",
        },
        new PolicyBatteryCase
        {
            Id = "076",
            Group = "D",
            Covers = "§3.4 — the mixed principal",
            File = "cases/076-orders-amount-u12-policy.sql",
            Principal = "u12",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.Projection },
            },
            Notes = "Half of u12's visible rows would disclose the value in full. The rule "
                + "holds whenever AGGREGATE_ONLY is POSSIBLE for a visible row, so the "
                + "statement is refused rather than half-answered — the design's own "
                + "example, with manager and auditor swapped.",
        },
        new PolicyBatteryCase
        {
            Id = "077",
            Group = "D",
            Covers = "§3.4 — the remedy",
            File = "cases/077-orders-amount-org1-u12.sql",
            Principal = "u12",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [101, 100.00m],
                    [102, 200.00m],
                    [103, 150.00m],
                    [104, 250.00m],
                    [105, 50.00m],
                    [106, 300.00m],
                    [107, 100.00m],
                    [108, 250.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["amount"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "UNCERTAIN (README §5 U5). The design says the remedy is to fix the "
                + "tenancy in the statement, under which the folded disclosure is constant "
                + "FULL. But §3 puts the pass BEFORE the Hep pre-pass, and it is the Hep "
                + "pre-pass that transposes the statement's own `WHERE` into Filter_R — so "
                + "at the moment the pass folds, `org_id = 1` is in a Filter above "
                + "Project_D and not among the leaf's conjuncts. Either the pass must "
                + "gather the conjuncts that hold over the leaf from above it, or this case "
                + "is a POLICY error and the design's remedy does not work as written. "
                + "Encoded as the design says.",
        },
        new PolicyBatteryCase
        {
            Id = "078",
            Group = "D",
            Covers = "D196 first match wins; §3.4 dom(d)",
            File = "cases/078-orders-amount-u7.sql",
            Principal = "u7",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [101, 100.00m],
                    [102, 200.00m],
                    [103, 150.00m],
                    [104, 250.00m],
                    [105, 50.00m],
                    [106, 300.00m],
                    [107, 100.00m],
                    [108, 250.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["amount"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "AGGREGATE_ONLY is not in dom(d) for this principal at all — the agent's "
                + "FULL rule matches first on every visible row — so the trace never runs "
                + "and no guard is emitted. Being an auditor as well as an agent takes "
                + "nothing away.",
            Disputed = new PolicyAdjudication(
                "the statement POLICY: Refused by the entitlements: "
                    + "main.orders.amount is population-only for this principal and a "
                    + "projection to the result is not one of the aggregates it permits.",
                "the statement rows",
                "The engine is right: §3.4's rule is about AGGREGATE_ONLY being "
                    + "possible for a visible row, so a principal who is an agent in one "
                    + "organization and an auditor in another is refused rather than handed "
                    + "values for one of them."),
        },
        new PolicyBatteryCase
        {
            Id = "079",
            Group = "D",
            Covers = "§3.4 — the CAST must be to a numeric type",
            File = "cases/079-orders-count-cast-varchar-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.CastNotNumeric },
            },
            Notes = "UNCERTAIN (README §5 U14). §3.4 permits `a bare reference, optionally "
                + "under a CAST to a numeric type`, and a cast to VARCHAR is outside that. "
                + "It is also harmless for COUNT, so an implementation might allow it; the "
                + "strict reading is encoded. The engine agreed from the F58 run onwards: "
                + "`amount` is nullable in the row type a statement is written over — a rule of "
                + "it can withhold the value — so the converter no longer erases the argument of "
                + "COUNT, the trace of §3.4 sees the cast, and the strict reading is what the "
                + "engine does.",
        },
        new PolicyBatteryCase
        {
            Id = "080",
            Group = "E",
            Covers = "§3.6 — COUNT(c) is guarded",
            File = "cases/080-orders-count-amount-u6.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [8],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Eight rows, above the floor, so the count answers — and it is still "
                + "reported Aggregate, because below the floor it would have been NULL. The "
                + "engine agreed from the F58 run onwards: F43's erasure of COUNT(x) needs x to "
                + "be NOT NULL, and a column a rule of the entitlement can withhold is nullable "
                + "in the row type a statement is written over, so the argument survives.",
        },
        new PolicyBatteryCase
        {
            Id = "081",
            Group = "E",
            Covers = "§3.6 — COUNT(*) is not over the column",
            File = "cases/081-orders-count-star-u6.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [8],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Same number, different acknowledgement: this one is a fact about rows "
                + "and not about the column, so it is never suppressed.",
        },
        new PolicyBatteryCase
        {
            Id = "082",
            Group = "E",
            Covers = "§3.6 — COUNT(DISTINCT c)",
            File = "cases/082-orders-count-distinct-amount-u6.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [6],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Six distinct amounts over eight orders in O1 (100 and 250 each occur "
                + "twice). The guard is COUNT(amount) = 8, not the distinct count.",
        },
        new PolicyBatteryCase
        {
            Id = "083",
            Group = "E",
            Covers = "§3.6 — one group over two tenancies",
            File = "cases/083-orders-sum-amount-u11.sql",
            Principal = "u11",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [3000.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["s"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "1400 + 1600 over ten rows. The tenancy below the floor is not suppressed "
                + "here, because the GROUP the statement asks for is above it — which is "
                + "exactly what query-set-size control does and does not promise.",
        },
        new PolicyBatteryCase
        {
            Id = "084",
            Group = "E",
            Covers = "§3.6 — every group below the floor",
            File = "cases/084-orders-group-member-u6.sql",
            Principal = "u6",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 3, null],
                    [2, 2, null],
                    [3, 1, null],
                    [4, 2, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["member_id"] = ReportedDisclosure.Full,
                    ["n"] = ReportedDisclosure.Full,
                    ["s"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The counts are 3, 2, 1, 2 — all below 5 — so every sum is NULL while "
                + "COUNT(*) tells the principal how many rows each member has.",
        },
        new PolicyBatteryCase
        {
            Id = "085",
            Group = "E",
            Covers = "§3.6, §6 — a vacuous floor",
            File = "cases/085-orders-avg-by-org-u11-floor1.sql",
            Principal = "u11",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersFloor1 },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 175.00m],
                    [3, 800.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["org_id"] = ReportedDisclosure.Full,
                    ["a"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "SETTLED by D211 (owner 2026-09-10): a column's explicit `1` disables the "
                + "guard for that column whatever the host's default says, so no COUNT and "
                + "no guard projection are emitted. The reported disclosure is still "
                + "Aggregate — the column is population-only whether or not a floor guards "
                + "it. (The residual case the earlier note raised — a group in which every "
                + "value of c is NULL has COUNT(c) = 0 and would be suppressed under a "
                + "literal k = 1 — is closed by the same decision rather than left to the "
                + "fixture.)",
        },
        new PolicyBatteryCase
        {
            Id = "086",
            Group = "E",
            Covers = "§6 — k = 0 disables the guard",
            File = "cases/086-orders-avg-by-org-u11-floor-off.sql",
            Principal = "u11",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersFloorOff },
            Options = PolicyCaseOptions.Default with { DefaultMinGroupSize = 0 },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 175.00m],
                    [3, 800.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["org_id"] = ReportedDisclosure.Full,
                    ["a"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "SETTLED by D211 (owner 2026-09-10): a column's `0` inherits the default "
                + "the host gives at planning, which is itself `0` when the host says "
                + "nothing, and an effective floor of one or less emits no guard at all. "
                + "The variant leaves both unset, so there is nothing to inherit. The "
                + "column is still AGGREGATE_ONLY, so the allow-list still applies and the "
                + "report still says Aggregate — only the suppression is off.",
        },
        new PolicyBatteryCase
        {
            Id = "087",
            Group = "E",
            Covers = "§3.6 — HAVING over a guarded aggregate",
            File = "cases/087-orders-having-guarded-u11.sql",
            Principal = "u11",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1400.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["org_id"] = ReportedDisclosure.Full,
                    ["s"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "UNCERTAIN (README §5 U7). O3's raw sum is 1600, which satisfies `< "
                + "2000`; guarded it is NULL, and NULL < 2000 is UNKNOWN, so the group "
                + "drops. That is the answer if the guard projection sits between the "
                + "Aggregate and the HAVING filter, which is how `above the aggregate` "
                + "reads. If an implementation puts the HAVING below the guard, the answer "
                + "is two rows, the second with a NULL sum — and a suppressed value would "
                + "have decided a filter, which is the worse of the two.",
        },
        new PolicyBatteryCase
        {
            Id = "088",
            Group = "E",
            Covers = "§3.6 — ORDER BY over a guarded aggregate",
            File = "cases/088-orders-order-by-guarded-u11.sql",
            Principal = "u11",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1400.00m],
                    [3, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["org_id"] = ReportedDisclosure.Full,
                    ["s"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "`NULLS LAST` is written out so that the case does not depend on the "
                + "engine's default null placement for DESC.",
        },
        new PolicyBatteryCase
        {
            Id = "089",
            Group = "E",
            Covers = "§3.6 — every allow-listed aggregate, one group",
            File = "cases/089-orders-all-allow-listed-u6.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [8, 6, 1400.00m, 175.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["c"] = ReportedDisclosure.Aggregate,
                    ["cd"] = ReportedDisclosure.Aggregate,
                    ["s"] = ReportedDisclosure.Aggregate,
                    ["a"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "SUM0 is not written here because no statement produces it directly; it "
                + "appears when AGGREGATE_REDUCE_FUNCTIONS rewrites the AVG, which is why "
                + "the allow-list names it (ADR 0025) and why the taint check's clause 1 "
                + "runs before the Hep pass. The engine agreed on `c` from the F58 run onwards: "
                + "F43's erasure of COUNT(x) needs x to be NOT NULL, and a column a rule of the "
                + "entitlement can withhold is nullable in the row type a statement is written "
                + "over, so the argument survives.",
        },
        new PolicyBatteryCase
        {
            Id = "090",
            Group = "E",
            Covers = "§3.6 — every allow-listed aggregate, one group below the floor",
            File = "cases/090-orders-all-allow-listed-by-org-u11.sql",
            Principal = "u11",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 8, 6, 1400.00m, 175.00m],
                    [3, null, null, null, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["org_id"] = ReportedDisclosure.Full,
                    ["c"] = ReportedDisclosure.Aggregate,
                    ["cd"] = ReportedDisclosure.Aggregate,
                    ["s"] = ReportedDisclosure.Aggregate,
                    ["a"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Every guarded output of the suppressed group is NULL, the two counts "
                + "included: the guard is `CASE WHEN count >= k THEN agg ELSE NULL END` per "
                + "output, and COUNT(c) is an output over c like any other. The engine agreed on "
                + "`c` — and so on the suppressed group's count — from the F58 run onwards: F43's "
                + "erasure of COUNT(x) needs x to be NOT NULL, and a column a rule of the "
                + "entitlement can withhold is nullable in the row type a statement is written "
                + "over, so the argument survives and the guard covers it.",
        },
        new PolicyBatteryCase
        {
            Id = "091",
            Group = "E",
            Covers = "§3.6 — the two acknowledgements side by side",
            File = "cases/091-orders-count-star-below-floor-u11.sql",
            Principal = "u11",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [2, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Full,
                    ["s"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The principal learns that the tenancy has two orders and learns nothing "
                + "about their amounts. This is the design's stated limit, stated as a row.",
        },
        new PolicyBatteryCase
        {
            Id = "092",
            Group = "E",
            Covers = "§3.6 — no guard where the column is not population-only",
            File = "cases/092-orders-group-org-sum-u9-unguarded.sql",
            Principal = "u9",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [2, 1800.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["org_id"] = ReportedDisclosure.Full,
                    ["s"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Same statement, same table, same six-row group as an auditor would see "
                + "in O2 — and no guard and no Aggregate, because the guard follows the "
                + "disclosure and not the statement.",
            Disputed = new PolicyAdjudication(
                "s's disclosure PerRow",
                "s's disclosure Full",
                "The engine is right: the table's row predicate carries the "
                    + "resource-owner fail-safe or a subject grant, so two rules stay "
                    + "reachable at the leaf and the disclosure is decided per row. The "
                    + "corpus computed what the rows turned out to be, which is a different "
                    + "question and one D207 forbids an acknowledgement from depending on."),
        },
        new PolicyBatteryCase
        {
            Id = "093",
            Group = "E",
            Covers = "§3.6 — the boundary, at k",
            File = "cases/093-orders-guard-at-exactly-k-u6.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [5, 750.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["c"] = ReportedDisclosure.Aggregate,
                    ["s"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Orders 101-105: five rows, 100 + 200 + 150 + 250 + 50. The engine agreed on "
                + "`c` from the F58 run onwards: F43's erasure of COUNT(x) needs x to be NOT "
                + "NULL, and a column a rule of the entitlement can withhold is nullable in the "
                + "row type a statement is written over, so the argument survives.",
        },
        new PolicyBatteryCase
        {
            Id = "094",
            Group = "E",
            Covers = "§3.6 — the boundary, one below k",
            File = "cases/094-orders-guard-below-k-u6.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [null, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["c"] = ReportedDisclosure.Aggregate,
                    ["s"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Orders 101-104. The count is NULL rather than 4: COUNT(amount) is "
                + "guarded by itself, so a below-floor group cannot even report its size "
                + "through the guarded call. The unguarded size is available through "
                + "COUNT(*), as case 091 shows. The engine agreed from the F58 run onwards: F43's "
                + "erasure of COUNT(x) needs x to be NOT NULL, and a column a rule of the "
                + "entitlement can withhold is nullable in the row type a statement is written "
                + "over, so the argument survives and the guard covers the count as well.",
        },
        new PolicyBatteryCase
        {
            Id = "095",
            Group = "F",
            Covers = "§8 corpus 17; D159, D161 Placeholder",
            File = "cases/095-members-star-none-placeholder-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersNone },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "T", "K", new DateOnly(1980, 1, 1), null, null],
                    [2, 1, "R", "O", new DateOnly(1991, 1, 1), null, null],
                    [3, 1, "P", "V", new DateOnly(1975, 1, 1), null, null],
                    [4, 1, "T", "L", new DateOnly(1988, 1, 1), null, null],
                    [10, 1, "R", "S", new DateOnly(1990, 1, 1), null, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Masked,
                    ["last_name"] = ReportedDisclosure.Masked,
                    ["dob"] = ReportedDisclosure.Masked,
                    ["national_id"] = ReportedDisclosure.Masked,
                    ["postcode"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "`postcode` and `national_id` both come back NULL and are reported "
                + "differently — Undisclosed for the column nobody declared, Masked for the "
                + "column whose rule says NULL. The output type of `postcode` is widened to "
                + "nullable even though the base column is NOT NULL.",
        },
        new PolicyBatteryCase
        {
            Id = "096",
            Group = "F",
            Covers = "§8 corpus 17; D161 Omit",
            File = "cases/096-members-star-none-omit-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersNone },
            Options = PolicyCaseOptions.Default with { Redaction = new() { StarExpansion = StarExpansion.Omit } },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "T", "K", new DateOnly(1980, 1, 1), null],
                    [2, 1, "R", "O", new DateOnly(1991, 1, 1), null],
                    [3, 1, "P", "V", new DateOnly(1975, 1, 1), null],
                    [4, 1, "T", "L", new DateOnly(1988, 1, 1), null],
                    [10, 1, "R", "S", new DateOnly(1990, 1, 1), null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Masked,
                    ["last_name"] = ReportedDisclosure.Masked,
                    ["dob"] = ReportedDisclosure.Masked,
                    ["national_id"] = ReportedDisclosure.Masked,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Six columns instead of seven; the row shape now differs between "
                + "principals, which is what D161 says Omit costs and Placeholder buys.",
        },
        new PolicyBatteryCase
        {
            Id = "097",
            Group = "F",
            Covers = "§8 corpus 17; D161 Refuse",
            File = "cases/097-members-star-none-refuse-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersNone },
            Options = PolicyCaseOptions.Default with { Redaction = new() { StarExpansion = StarExpansion.Refuse } },
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "members", Column = "postcode", Use = PolicyUse.Star },
            },
            Notes = "For a client that would rather be told than handed a NULL.",
            Disputed = new PolicyAdjudication(
                "the refusal names the output column 'postcode' and not the table",
                "the refusal names the table members",
                "The engine is right: a redacted output column may have several origins, "
                    + "and what the caller asked for is the column, so that is what the "
                    + "message names. The same reading as case 174's suffix collision — the "
                    + "clash is between output names, not tables."),
        },
        new PolicyBatteryCase
        {
            Id = "098",
            Group = "F",
            Covers = "§8 corpus 18; D162 PlaceholdersAsEmpty",
            File = "cases/098-members-star-none-empty-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersNone },
            Options = PolicyCaseOptions.Default with { PlaceholderPolicy = PlaceholderPolicy.PlaceholdersAsEmpty },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "T", "K", new DateOnly(1980, 1, 1), null, ""],
                    [2, 1, "R", "O", new DateOnly(1991, 1, 1), null, ""],
                    [3, 1, "P", "V", new DateOnly(1975, 1, 1), null, ""],
                    [4, 1, "T", "L", new DateOnly(1988, 1, 1), null, ""],
                    [10, 1, "R", "S", new DateOnly(1990, 1, 1), null, ""],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Masked,
                    ["last_name"] = ReportedDisclosure.Masked,
                    ["dob"] = ReportedDisclosure.Masked,
                    ["national_id"] = ReportedDisclosure.Masked,
                    ["postcode"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "`postcode` keeps its declared NOT NULL and holds the type's zero value. "
                + "`national_id` is still NULL: the policy governs PLACEHOLDERS, and a mask "
                + "keeps the model's own defaults (D162). This is the pair of columns that "
                + "shows the difference.",
        },
        new PolicyBatteryCase
        {
            Id = "099",
            Group = "F",
            Covers = "§3.11 — a named undisclosed column",
            File = "cases/099-members-postcode-none-placeholder-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersNone },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [null],
                    [null],
                    [null],
                    [null],
                    [null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["postcode"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Star provenance is what tells the two apart: this column was named, so "
                + "it is treated as it always was.",
        },
        new PolicyBatteryCase
        {
            Id = "100",
            Group = "F",
            Covers = "§3.11 — Omit never removes a named column",
            File = "cases/100-members-postcode-none-omit-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersNone },
            Options = PolicyCaseOptions.Default with { Redaction = new() { StarExpansion = StarExpansion.Omit } },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [null],
                    [null],
                    [null],
                    [null],
                    [null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["postcode"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "A statement that names a column and gets a result with no such column "
                + "would be a silent failure; the design says so explicitly.",
        },
        new PolicyBatteryCase
        {
            Id = "101",
            Group = "F",
            Covers = "§3.11 — Refuse against a named column",
            File = "cases/101-members-postcode-none-refuse-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersNone },
            Options = PolicyCaseOptions.Default with { Redaction = new() { StarExpansion = StarExpansion.Refuse } },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [null],
                    [null],
                    [null],
                    [null],
                    [null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["postcode"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "UNCERTAIN (README §5 U8). `UndisclosedColumns` is defined as what "
                + "happens to `undisclosed columns a star surfaces`, and §3.11 says a named "
                + "column is a POLICY error only `when no disclosure could ever permit it` "
                + "— which is not the case here, since a manager sees `postcode`. Encoded "
                + "as a placeholder. The other reading is that Refuse refuses anything "
                + "undisclosed, named or not.",
        },
        new PolicyBatteryCase
        {
            Id = "102",
            Group = "F",
            Covers = "§8 corpus 18; D162 — the epoch for a NOT NULL date",
            File = "cases/102-orders-star-none-empty-u9.sql",
            Principal = "u9",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersNone },
            Options = PolicyCaseOptions.Default with { PlaceholderPolicy = PlaceholderPolicy.PlaceholdersAsEmpty },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [109, 5, 2, new DateOnly(1970, 1, 1), "********", 450.00m, "DONE", 5],
                    [110, 5, 2, new DateOnly(1970, 1, 1), "********", 150.00m, "OPEN", 5],
                    [111, 6, 2, new DateOnly(1970, 1, 1), "********", 200.00m, "NEW", 6],
                    [112, 6, 2, new DateOnly(1970, 1, 1), "********", 100.00m, "HOLD", 95],
                    [113, 7, 2, new DateOnly(1970, 1, 1), "********", 500.00m, "OPEN", 6],
                    [114, 7, 2, new DateOnly(1970, 1, 1), "********", 400.00m, "DONE", 5],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["member_id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["placed_at"] = ReportedDisclosure.Redacted,
                    ["note"] = ReportedDisclosure.Masked,
                    ["amount"] = ReportedDisclosure.Full,
                    ["status"] = ReportedDisclosure.Full,
                    ["created_by"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Calcite's zero literal for DATE is the epoch. An agent's `amount` is "
                + "FULL, so this case also shows that `default_disclosure = NONE` hides "
                + "only what nobody declared.",
            Disputed = new PolicyAdjudication(
                "note's disclosure PerRow; amount's disclosure PerRow",
                "note's disclosure Masked; amount's disclosure Full",
                "The engine is right: the table's row predicate carries the "
                    + "resource-owner fail-safe or a subject grant, so two rules stay "
                    + "reachable at the leaf and the disclosure is decided per row. The "
                    + "corpus computed what the rows turned out to be, which is a different "
                    + "question and one D207 forbids an acknowledgement from depending on."),
        },
        new PolicyBatteryCase
        {
            Id = "103",
            Group = "F",
            Covers = "§8 corpus 18; D162 PlaceholdersAsNull",
            File = "cases/103-orders-star-none-null-u9.sql",
            Principal = "u9",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersNone },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [109, 5, 2, null, "********", 450.00m, "DONE", 5],
                    [110, 5, 2, null, "********", 150.00m, "OPEN", 5],
                    [111, 6, 2, null, "********", 200.00m, "NEW", 6],
                    [112, 6, 2, null, "********", 100.00m, "HOLD", 95],
                    [113, 7, 2, null, "********", 500.00m, "OPEN", 6],
                    [114, 7, 2, null, "********", 400.00m, "DONE", 5],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["member_id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["placed_at"] = ReportedDisclosure.Redacted,
                    ["note"] = ReportedDisclosure.Masked,
                    ["amount"] = ReportedDisclosure.Full,
                    ["status"] = ReportedDisclosure.Full,
                    ["created_by"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The same statement under the default policy, so the two are one "
                + "comparison.",
            Disputed = new PolicyAdjudication(
                "note's disclosure PerRow; amount's disclosure PerRow",
                "note's disclosure Masked; amount's disclosure Full",
                "The engine is right: the table's row predicate carries the "
                    + "resource-owner fail-safe or a subject grant, so two rules stay "
                    + "reachable at the leaf and the disclosure is decided per row. The "
                    + "corpus computed what the rows turned out to be, which is a different "
                    + "question and one D207 forbids an acknowledgement from depending on."),
        },
        new PolicyBatteryCase
        {
            Id = "104",
            Group = "F",
            Covers = "§8 corpus 18; D162 a per-column placeholder",
            File = "cases/104-orders-none-column-placeholder-u10.sql",
            Principal = "u10",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersNone },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [106, null, -1.00m, null],
                    [112, null, -1.00m, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["note"] = ReportedDisclosure.Redacted,
                    ["amount"] = ReportedDisclosure.Redacted,
                    ["placed_at"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The host names a stand-in only where an empty value would mislead: an "
                + "`amount` of 0 says more than it should, so -1 is declared and wins over "
                + "the policy. `note` has no per-column placeholder and takes the policy's "
                + "NULL. The rows are visible through the created-by disjunct while no "
                + "column rule matches them — which is why this variant declares "
                + "CreatorSees = ByRules, and is the only way a per-column placeholder on a "
                + "table whose every visibility disjunct has a matching column rule is "
                + "reachable at all.",
        },
        new PolicyBatteryCase
        {
            Id = "105",
            Group = "F",
            Covers = "§8 corpus 18; D162 — the per-column placeholder wins over either policy",
            File = "cases/105-orders-none-column-placeholder-empty-u10.sql",
            Principal = "u10",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersNone },
            Options = PolicyCaseOptions.Default with { PlaceholderPolicy = PlaceholderPolicy.PlaceholdersAsEmpty },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [106, "", -1.00m, new DateOnly(1970, 1, 1)],
                    [112, "", -1.00m, new DateOnly(1970, 1, 1)],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["note"] = ReportedDisclosure.Redacted,
                    ["amount"] = ReportedDisclosure.Redacted,
                    ["placed_at"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "`amount` is -1.00 under either policy, which is the clause of D162 this "
                + "pair exists to check.",
        },
        new PolicyBatteryCase
        {
            Id = "106",
            Group = "F",
            Covers = "D160 StarPolicy.RefuseOverEntitled",
            File = "cases/106-members-star-refuse-over-entitled-u1.sql",
            Principal = "u1",
            Options = PolicyCaseOptions.Default with { StarPolicy = StarPolicy.RefuseOverEntitled },
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "members", Use = PolicyUse.Star },
            },
            Notes = "Checked on the parse tree against the FROM scope before validation, so "
                + "the error arrives before any expansion. It changes nothing about what "
                + "the rewrite guarantees.",
        },
        new PolicyBatteryCase
        {
            Id = "107",
            Group = "F",
            Covers = "D160 StarPolicy.RefuseWhenEntitled",
            File = "cases/107-symbols-star-refuse-when-entitled-u1.sql",
            Principal = "u1",
            Options = PolicyCaseOptions.Default with { StarPolicy = StarPolicy.RefuseWhenEntitled },
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "symbols", Use = PolicyUse.Star },
            },
            Notes = "UNCERTAIN (README §5 U15). The setting is catalog-wide — `any star in "
                + "any query is refused while the catalog carries entitlements` — so the "
                + "refusal is not really about `symbols`. What the error should name is "
                + "unstated; the case expects the table the star stands over.",
            Disputed = new PolicyAdjudication(
                "the refusal names the table Refused by the entitlements: this "
                    + "statement uses SELECT *, and StarPolicy.RefuseWhenEntitled refuses "
                    + "any star while the catalog carries entitlements.",
                "the refusal names the table symbols",
                "The engine is right: StarPolicy.RefuseWhenEntitled is catalog-wide, "
                    + "so the refusal names the table the star stood over, which is the "
                    + "only useful thing to name (README §5 U15)."),
        },
        new PolicyBatteryCase
        {
            Id = "108",
            Group = "F",
            Covers = "D160 — the narrower setting lets an unentitled star stand",
            File = "cases/108-symbols-star-refuse-over-entitled-u1.sql",
            Principal = "u1",
            Options = PolicyCaseOptions.Default with { StarPolicy = StarPolicy.RefuseOverEntitled },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["NEW", "New", 1],
                    ["OPEN", "Open", 2],
                    ["HOLD", "On hold", 3],
                    ["DONE", "Done", 4],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["code"] = ReportedDisclosure.Full,
                    ["label"] = ReportedDisclosure.Full,
                    ["sort_order"] = ReportedDisclosure.Full,
                },
            },
            Notes = "The pair 107/108 is what tells the two settings apart.",
        },
        new PolicyBatteryCase
        {
            Id = "109",
            Group = "G",
            Covers = "§3 — RexSubQuery inside a Filter condition",
            File = "cases/109-exists-orders-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [2],
                    [3],
                    [4],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Member 10 has no orders. Both leaves are entitled and both row "
                + "predicates hold.",
        },
        new PolicyBatteryCase
        {
            Id = "110",
            Group = "G",
            Covers = "§3 — an entitled sub-query under an empty outer scan",
            File = "cases/110-exists-orders-u10.sql",
            Principal = "u10",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.None },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "u10 sees no members at all, and the two orders it created belong to "
                + "members it cannot see — so the answer is empty for a reason that has "
                + "nothing to do with the sub-query.",
        },
        new PolicyBatteryCase
        {
            Id = "111",
            Group = "G",
            Covers = "§3 — an IN sub-query over a column the principal sees in full",
            File = "cases/111-in-subquery-amount-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [2],
                    [3],
                    [4],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Orders above 100 in O1 are 102, 103, 104, 106 and 108, whose members are "
                + "1, 2, 3 and 4.",
        },
        new PolicyBatteryCase
        {
            Id = "112",
            Group = "G",
            Covers = "§3.4 — the trace crosses a sub-query boundary",
            File = "cases/112-in-subquery-amount-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.Filter },
            },
            Notes = "The same statement as 111 for a principal whose `amount` is "
                + "population-only. The trace follows a RexFieldAccess over a "
                + "RexCorrelVariable to the column of the node that defines it, so a "
                + "sub-query is no hiding place.",
        },
        new PolicyBatteryCase
        {
            Id = "113",
            Group = "G",
            Covers = "§3 — NOT EXISTS over an entitled table",
            File = "cases/113-not-exists-orders-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "`No visible order` is what the principal is entitled to be told; the "
                + "answer would be different for a principal who can see more orders, and "
                + "that is correct.",
        },
        new PolicyBatteryCase
        {
            Id = "114",
            Group = "G",
            Covers = "§3 — two independent entitled leaves in the select list",
            File = "cases/114-scalar-subqueries-u2.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [8, 5],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Full,
                    ["m"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Eight visible orders and five visible members.",
        },
        new PolicyBatteryCase
        {
            Id = "115",
            Group = "G",
            Covers = "§3.4, §3.6 inside a Correlate",
            File = "cases/115-lateral-sum-amount-u6.sql",
            Principal = "u6",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, null],
                    [2, null],
                    [3, null],
                    [4, null],
                    [10, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["s"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "UNCERTAIN (README §5 U16). No member has five visible orders, so every "
                + "per-member sum is suppressed; member 10 has none at all and its sum "
                + "would be NULL anyway. The case assumes the guard applies to the "
                + "decorrelated aggregate per correlation group, which is what §3.6 says "
                + "for an Aggregate and what the shape becomes after decorrelation.",
            Disputed = new PolicyAdjudication(
                "the statement VALIDATION: Unsupported query: correlated subquery "
                    + "could not be decorrelated: a LATERAL or correlated sub-query whose "
                    + "decorrelated form Calcite cannot re-type (Cannot add expression of "
                    + "different type to set:) is not supported by this planner.",
                "the statement rows",
                "Neither: the statement is refused by name as the LATERAL shape "
                    + "Calcite's decorrelator gets wrong (ADR 0026), and the case predates "
                    + "the refusal."),
        },
        new PolicyBatteryCase
        {
            Id = "116",
            Group = "G",
            Covers = "§3.4 — a set operation maps output i to input i of each branch",
            File = "cases/116-union-members-orders-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [2],
                    [3],
                    [4],
                    [10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Members 1-4 and 10 union the order members 1-4.",
        },
        new PolicyBatteryCase
        {
            Id = "117",
            Group = "G",
            Covers = "§3.4 — INTERSECT",
            File = "cases/117-intersect-members-orders-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [2],
                    [3],
                    [4],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
        },
        new PolicyBatteryCase
        {
            Id = "118",
            Group = "G",
            Covers = "§3.4 — EXCEPT",
            File = "cases/118-except-members-orders-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
        },
        new PolicyBatteryCase
        {
            Id = "119",
            Group = "G",
            Covers = "§3.12 D202 — the meet over two origins of one output column",
            File = "cases/119-union-all-two-disclosures-u1.sql",
            Principal = "u1",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["N"],
                    ["Petra"],
                    ["Rina"],
                    ["Rina"],
                    ["T"],
                    ["Tao"],
                    ["Tomas"],
                    ["U"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["first_name"] = ReportedDisclosure.PerRow,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Two occurrences of one table, one output column, two disclosure maps. "
                + "Ordering is by code point, so the one-character masks sort among the "
                + "names rather than before them. The table appears once in the report and "
                + "twice in the plan.",
        },
        new PolicyBatteryCase
        {
            Id = "120",
            Group = "G",
            Covers = "§3.4 — bare pass-throughs through a CTE",
            File = "cases/120-cte-sum-amount-u6.sql",
            Principal = "u6",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1400.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["org_id"] = ReportedDisclosure.Full,
                    ["s"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The CTE's projection carries `amount` as a bare reference, so the only "
                + "consumer is the outer SUM.",
        },
        new PolicyBatteryCase
        {
            Id = "121",
            Group = "G",
            Covers = "§3.4 — the same CTE with a value use above it",
            File = "cases/121-cte-amount-to-root-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.Projection },
            },
            Notes = "The pair 120/121 is the same CTE and the same column; only the consumer "
                + "differs.",
        },
        new PolicyBatteryCase
        {
            Id = "122",
            Group = "G",
            Covers = "§3.1 — each occurrence separately",
            File = "cases/122-orders-twice-different-uses-u6.sql",
            Principal = "u6",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [101, 1400.00m],
                    [102, 1400.00m],
                    [103, 1400.00m],
                    [104, 1400.00m],
                    [105, 1400.00m],
                    [106, 1400.00m],
                    [107, 1400.00m],
                    [108, 1400.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["total"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Two leaves, two Filter_R, two disclosure maps. The inner aggregate is "
                + "over the whole visible population and is guarded; the outer occurrence "
                + "never reads `amount`.",
        },
        new PolicyBatteryCase
        {
            Id = "123",
            Group = "G",
            Covers = "§3.1, §3.4 — one bad occurrence refuses the statement",
            File = "cases/123-orders-twice-value-use-u6-policy.sql",
            Principal = "u6",
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "orders", Column = "amount", Use = PolicyUse.Projection },
            },
        },
        new PolicyBatteryCase
        {
            Id = "124",
            Group = "G",
            Covers = "§3.1 — a self-join on an unentitled key",
            File = "cases/124-orders-self-join-u2.sql",
            Principal = "u2",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [101, 102],
                    [101, 105],
                    [102, 105],
                    [103, 104],
                    [107, 108],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["a_id"] = ReportedDisclosure.Full,
                    ["b_id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Member 1 has three visible orders and contributes three pairs; member 3 "
                + "has one and contributes none.",
        },
        new PolicyBatteryCase
        {
            Id = "125",
            Group = "G",
            Covers = "§3.12 — an unentitled table in an entitled statement",
            File = "cases/125-join-entitled-unentitled-u9.sql",
            Principal = "u9",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [109, "Done"],
                    [110, "Open"],
                    [111, "New"],
                    [112, "On hold"],
                    [113, "Open"],
                    [114, "Done"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["label"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "`symbols` has no report row and every column of it is Full, which is the "
                + "zero-cost property stated per column.",
        },
        new PolicyBatteryCase
        {
            Id = "126",
            Group = "H",
            Covers = "§2, D197 — an empty list",
            File = "cases/126-members-count-empty-list-u9.sql",
            Principal = "u9",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [3],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "`manager_orgs` is bound and empty. Its membership test folds to FALSE, "
                + "the disjunct disappears, and what is left is the agent's list — so the "
                + "plan for u9 has no trace of a role u9 does not hold.",
        },
        new PolicyBatteryCase
        {
            Id = "127",
            Group = "H",
            Covers = "§2, D197 — NOT IN over an empty list is TRUE",
            File = "cases/127-members-blocked-not-in-empty-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersBlocked },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [2],
                    [3],
                    [4],
                    [10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Three-valued-correct for an empty set, and the reason the design says so "
                + "explicitly: a NOT IN that folded to UNKNOWN would have silently emptied "
                + "the result.",
        },
        new PolicyBatteryCase
        {
            Id = "128",
            Group = "H",
            Covers = "§2, D197 — NOT IN over a bound list",
            File = "cases/128-members-blocked-not-in-non-empty-u1.sql",
            Principal = "u1",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersBlocked },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [2],
                    [3],
                    [4],
                    [10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "u1 is an agent in O2 and O2 is blocked, so the exclusion removes exactly "
                + "the rows case 001 shows masked. The pair 127/128 differs only in the "
                + "bound list.",
        },
        new PolicyBatteryCase
        {
            Id = "129",
            Group = "H",
            Covers = "§2 item 1, ADR 0025 V60 — a composite list",
            File = "cases/129-members-composite-list-u3.sql",
            Principal = "u3",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [4],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "`(id, org_id) IN (ROW(4, 1))` reduces to `id = 4 AND org_id = 1` "
                + "natively, which is what the pushdown gate then sees as two conjuncts of "
                + "a shape every profile declares.",
        },
        new PolicyBatteryCase
        {
            Id = "130",
            Group = "H",
            Covers = "§2 item 1 — a single-column list becomes a SEARCH",
            File = "cases/130-members-single-column-list-u8.sql",
            Principal = "u8",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [7],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
        },
        new PolicyBatteryCase
        {
            Id = "131",
            Group = "H",
            Covers = "§2 item 2 — a list above `fold_max_rows`",
            File = "cases/131-members-above-fold-ceiling-u13.sql",
            Principal = "u13",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [2],
                    [3],
                    [4],
                    [10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Seventy rows against a ceiling of 64, so the list is not made literal: "
                + "it becomes a sub-query over the ContextTable and decorrelates into a "
                + "semi-join. Only O1 exists, so the answer is the same as a manager of O1 "
                + "alone would get — which is the point: the fold changes the plan and not "
                + "the rows. The rows of the list are not in the plan.",
            Disputed = new PolicyAdjudication(
                "the row count 10",
                "the row count 5",
                "The engine is right and the corpus is wrong about its own fixture: "
                    + "u13 binds 1..70 to exercise the fold ceiling and the fixture has "
                    + "three organisations, so ten rows is right."),
        },
        new PolicyBatteryCase
        {
            Id = "132",
            Group = "H",
            Covers = "§2 — a scalar wildcard; §3.12 visibility = ALL",
            File = "cases/132-members-scalar-wildcard-u4.sql",
            Principal = "u4",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.All },
                },
            },
            Notes = "The predicate folds to TRUE and the filter disappears, so the plan of a "
                + "global grant over an entitled table is the plan of an unentitled one — "
                + "with the disclosure map still recorded and the report still emitted.",
        },
        new PolicyBatteryCase
        {
            Id = "133",
            Group = "I",
            Covers = "§3.12 — visibility = NONE",
            File = "cases/133-members-visibility-none-u5.sql",
            Principal = "u5",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [0],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.None },
                },
            },
            Notes = "A global COUNT over no visible rows is one row holding zero, not zero "
                + "rows.",
        },
        new PolicyBatteryCase
        {
            Id = "134",
            Group = "I",
            Covers = "§3.12 — visibility = SOME",
            File = "cases/134-members-visibility-some-u2.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [5],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
        },
        new PolicyBatteryCase
        {
            Id = "135",
            Group = "I",
            Covers = "D207 — the option fires on NONE and on nothing else",
            File = "cases/135-orders-refuse-when-no-visible-rows-u3b.sql",
            Principal = "u3b",
            Options = PolicyCaseOptions.Default with { RefuseWhenNoVisibleRows = true },
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [0],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Zero rows and no refusal: the created-by disjunct keeps the visibility "
                + "at SOME. A host that reads this as `refuse when the answer is empty` "
                + "would be reporting a fact about hidden rows, which D207 forbids.",
        },
        new PolicyBatteryCase
        {
            Id = "136",
            Group = "I",
            Covers = "§3.12 — contradiction",
            File = "cases/136-members-contradiction-u1.sql",
            Principal = "u1",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.PerRow,
                    ["last_name"] = ReportedDisclosure.PerRow,
                    ["dob"] = ReportedDisclosure.PerRow,
                    ["national_id"] = ReportedDisclosure.PerRow,
                    ["postcode"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some, Contradiction = true, ContradictionColumn = "org_id" },
                },
            },
            Notes = "Detected after the Hep pass, when the leaf has been pruned to empty and "
                + "the statement's own filter is satisfiable without the policy's "
                + "conjuncts. Safe to report because it is a fact about the principal's "
                + "scope and the statement, not about rows.",
            Disputed = new PolicyAdjudication(
                "first_name's disclosure Redacted; last_name's disclosure Redacted; "
                    + "dob's disclosure Redacted; national_id's disclosure Redacted",
                "first_name's disclosure PerRow; last_name's disclosure PerRow; dob's "
                    + "disclosure PerRow; national_id's disclosure PerRow",
                "The engine is right, and this is the remedy of section 3.4 seen from "
                    + "the report's side (V63): the leaf simplifies under the statement's "
                    + "own conjuncts and the report reads the leaf, so the statement's own "
                    + "filter narrows the label (README section 5 U17)."),
        },
        new PolicyBatteryCase
        {
            Id = "137",
            Group = "I",
            Covers = "D207 — a contradiction under RefuseWhenNoVisibleRows",
            File = "cases/137-members-contradiction-refuse-u1.sql",
            Principal = "u1",
            Options = PolicyCaseOptions.Default with { RefuseWhenNoVisibleRows = true },
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "members", Column = "org_id", Use = PolicyUse.Contradiction },
            },
            Notes = "The second thing the option turns into an exception, from part 2b.",
        },
        new PolicyBatteryCase
        {
            Id = "138",
            Group = "I",
            Covers = "§3.12 — the same shape inside the scope",
            File = "cases/138-members-org2-not-a-contradiction-u1.sql",
            Principal = "u1",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [5, 2, "T", "M", new DateOnly(1993, 1, 1), null, "4101"],
                    [6, 2, "U", "B", new DateOnly(1969, 1, 1), null, "4101"],
                    [7, 2, "N", "H", new DateOnly(1985, 1, 1), null, "5000"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.PerRow,
                    ["last_name"] = ReportedDisclosure.PerRow,
                    ["dob"] = ReportedDisclosure.PerRow,
                    ["national_id"] = ReportedDisclosure.PerRow,
                    ["postcode"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "UNCERTAIN (README §5 U17). Every returned row is masked, so the folded "
                + "disclosure over the rows this statement can return is constant MASKED; "
                + "whether the report should simplify to Masked under the statement's own "
                + "filter, or stay PerRow because the leaf's map is what it reads, the "
                + "design does not say. PerRow is encoded, since §3.12 reads the disclosure "
                + "map and §3.3 simplifies under Filter_R's conjuncts only.",
            Disputed = new PolicyAdjudication(
                "first_name's disclosure Masked; last_name's disclosure Masked; dob's "
                    + "disclosure Masked; national_id's disclosure Masked",
                "first_name's disclosure PerRow; last_name's disclosure PerRow; dob's "
                    + "disclosure PerRow; national_id's disclosure PerRow",
                "The engine is right, and this is the remedy of section 3.4 seen from "
                    + "the report's side (V63): the leaf simplifies under the statement's "
                    + "own conjuncts and the report reads the leaf, so the statement's own "
                    + "filter narrows the label (README section 5 U17)."),
        },
        new PolicyBatteryCase
        {
            Id = "139",
            Group = "I",
            Covers = "§3.12 — one report row per entitled table",
            File = "cases/139-report-two-tables-u1.sql",
            Principal = "u1",
            Compare = PolicyCompare.Ordered,
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
                    [5, 109],
                    [5, 110],
                    [6, 111],
                    [6, 112],
                    [7, 113],
                    [7, 114],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["order_id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Each row of the report carries its own descriptor hash, and both hashes "
                + "are in the plan digest, so a change to either policy is a different plan "
                + "by construction.",
        },
        new PolicyBatteryCase
        {
            Id = "140",
            Group = "I",
            Covers = "§0, §3.12 — no report row for an unentitled table",
            File = "cases/140-report-unentitled-table-u1.sql",
            Principal = "u1",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["DONE", "Done"],
                    ["HOLD", "On hold"],
                    ["NEW", "New"],
                    ["OPEN", "Open"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["code"] = ReportedDisclosure.Full,
                    ["label"] = ReportedDisclosure.Full,
                },
            },
            Notes = "Ordered by code: DONE, HOLD, NEW, OPEN. The statement touches no "
                + "entitlement, so the plan and digest must be byte-identical to the same "
                + "statement in an entitlement-free catalog — which is the zero-cost class "
                + "of §0 measured on one query.",
        },
        new PolicyBatteryCase
        {
            Id = "141",
            Group = "J",
            Covers = "D206 CreatorSees = Full",
            File = "cases/141-orders-creator-sees-full-u10.sql",
            Principal = "u10",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [106, "renewal", 300.00m],
                    [112, "renewal", 100.00m],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["note"] = ReportedDisclosure.Full,
                    ["amount"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Two rows in two tenancies u10 holds no grant in, disclosed in full "
                + "because u10 wrote them.",
        },
        new PolicyBatteryCase
        {
            Id = "142",
            Group = "J",
            Covers = "D206 CreatorSees = ByRules",
            File = "cases/142-orders-creator-sees-by-rules-u10.sql",
            Principal = "u10",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersByrules },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [106, null, null],
                    [112, null, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["note"] = ReportedDisclosure.Redacted,
                    ["amount"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The row predicate is unchanged, so the rows are still visible; the "
                + "column rules no longer name the creator, so nothing about them is. "
                + "`amount` is reported Undisclosed rather than refused: with no rule "
                + "matching, AGGREGATE_ONLY is not in dom(d) either, and §3.5's second row "
                + "applies — the placeholder.",
        },
        new PolicyBatteryCase
        {
            Id = "143",
            Group = "J",
            Covers = "D206 — the fail-safe reaches across tenancies",
            File = "cases/143-orders-creator-other-org-u1.sql",
            Principal = "u1",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [115, 3, "first order"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["note"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "O3 is outside every list u1 holds. `WHERE org_id = 3` is therefore NOT a "
                + "contradiction for this table, although the same predicate on `members` "
                + "is (case 136) — the created-by disjunct is what makes the difference, "
                + "and it is per table.",
            Disputed = new PolicyAdjudication(
                "note's disclosure PerRow",
                "note's disclosure Full",
                "The engine is right: the table's row predicate carries the "
                    + "resource-owner fail-safe or a subject grant, so two rules stay "
                    + "reachable at the leaf and the disclosure is decided per row. The "
                    + "corpus computed what the rows turned out to be, which is a different "
                    + "question and one D207 forbids an acknowledgement from depending on."),
        },
        new PolicyBatteryCase
        {
            Id = "144",
            Group = "J",
            Covers = "D206 ByRules across tenancies",
            File = "cases/144-orders-creator-other-org-by-rules-u1.sql",
            Principal = "u1",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersByrules },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [115, 3, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["note"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "`On an ungranted tenancy the creator sees that the row exists and "
                + "nothing else`, in one row.",
        },
        new PolicyBatteryCase
        {
            Id = "145",
            Group = "J",
            Covers = "D204 — a confined subject grant",
            File = "cases/145-orders-subject-within-u3.sql",
            Principal = "u3",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [107],
                    [108],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
        },
        new PolicyBatteryCase
        {
            Id = "146",
            Group = "J",
            Covers = "D204 — `Tenancy.Anywhere`",
            File = "cases/146-orders-subject-anywhere-u8.sql",
            Principal = "u8",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [113],
                    [114],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Member 7's orders are in O2 and the grant names no tenancy, so it "
                + "reaches them. Both were created by other members, so the created-by "
                + "disjunct plays no part.",
        },
        new PolicyBatteryCase
        {
            Id = "147",
            Group = "J",
            Covers = "D205 — AllowGlobalGrants off",
            File = "cases/147-members-global-grant-refused-u4.sql",
            Principal = "u4",
            LayerBOnly = true,
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Use = PolicyUse.GlobalGrantNotAllowed },
            },
            Notes = "LAYER B ONLY. The check belongs to the tenancy package, which refuses to "
                + "compile a global grant unless the option is on; in the layer-A run the "
                + "`global` scalar is an ordinary context value and this case is skipped. "
                + "Recorded here because §8 runs the corpus both ways and the negative "
                + "belongs to the package's half.",
        },
        new PolicyBatteryCase
        {
            Id = "148",
            Group = "J",
            Covers = "D206 on a second table",
            File = "cases/148-notes-creator-u10.sql",
            Principal = "u10",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [203],
                    [204],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["notes"] = new() { Visibility = TableVisibility.Some },
                },
            },
        },
        new PolicyBatteryCase
        {
            Id = "149",
            Group = "K",
            Covers = "§3.7 D199 — the row predicate reaches the source",
            File = "cases/149-members-pushed-duckdb-u2.sql",
            Principal = "u2",
            Source = PolicySourceProfile.AdoDuckDb,
            Compare = PolicyCompare.Ordered,
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
                    RemoteQueryContains = ["org_id"],
                },
            },
            Notes = "The masks are computed locally and must not appear in the remote text; "
                + "the tenancy predicate must. A single-org list may be rendered as `org_id "
                + "= 1` or `org_id IN (1)` after simplification, so the assertion names the "
                + "column and not the spelling.",
        },
        new PolicyBatteryCase
        {
            Id = "150",
            Group = "K",
            Covers = "§7 — the two ADO servers must agree",
            File = "cases/150-members-pushed-postgres-u2.sql",
            Principal = "u2",
            Source = PolicySourceProfile.AdoPostgres,
            Compare = PolicyCompare.Ordered,
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
                    RemoteQueryContains = ["org_id"],
                },
            },
        },
        new PolicyBatteryCase
        {
            Id = "151",
            Group = "K",
            Covers = "§3.8 D200 — a predicate over a withheld column stays local",
            File = "cases/151-members-mask-stays-local-u2.sql",
            Principal = "u2",
            Source = PolicySourceProfile.AdoDuckDb,
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [4],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some, RowPredicatePushed = true },
                },
                Claims = new PolicyClaims
                {
                    RemoteQueryNotContains = ["LIKE", "SUBSTRING"],
                },
            },
            Notes = "The tenancy conjunct is pushed and the LIKE is the residual, evaluated "
                + "over the rows the source returned. The filter-residual rule of §3.7 item "
                + "3 is what lets the two travel separately.",
        },
        new PolicyBatteryCase
        {
            Id = "152",
            Group = "K",
            Covers = "§3.8 — an expression over a column the principal sees in full is pushed",
            File = "cases/152-members-full-column-pushes-u14.sql",
            Principal = "u14",
            Source = PolicySourceProfile.AdoDuckDb,
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some, RowPredicatePushed = true },
                },
                Claims = new PolicyClaims
                {
                    RemoteQueryContains = ["UPPER"],
                },
            },
            Notes = "u14 is a manager in O1 and nothing else, so `first_name` folds to a "
                + "constant FULL and the gate has nothing to hold back, and the list is "
                + "small enough to fold literally so that the pushed predicate is an "
                + "ordinary IN list beside the pushed expression.",
        },
        new PolicyBatteryCase
        {
            Id = "153",
            Group = "K",
            Covers = "§3.8 — the same statement over a masked column",
            File = "cases/153-members-masked-column-local-u2.sql",
            Principal = "u2",
            Source = PolicySourceProfile.AdoDuckDb,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some, RowPredicatePushed = true },
                },
                Claims = new PolicyClaims
                {
                    RemoteQueryNotContains = ["UPPER"],
                },
            },
            Notes = "`UPPER(SUBSTRING(first_name FROM 1 FOR 1)) = 'TOMAS'` can never hold, so "
                + "the answer is empty — and the point of the case is that the expression "
                + "is computed here and not there. The pair 152/153 is the design's own "
                + "locality example.",
        },
        new PolicyBatteryCase
        {
            Id = "154",
            Group = "K",
            Covers = "D156 — Enforcement.LOCAL",
            File = "cases/154-members-local-enforcement-u2.sql",
            Principal = "u2",
            Source = PolicySourceProfile.AdoDuckDb,
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersLocal },
            Compare = PolicyCompare.Ordered,
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
                    ["members"] = new() { Visibility = TableVisibility.Some, RowPredicatePushed = false },
                },
                Claims = new PolicyClaims
                {
                    RemoteQueryNotContains = ["org_id ="],
                },
            },
            Notes = "Same rows as 149 and a different plan: the source receives a projected "
                + "scan and the tenant set never appears in remote query text. The flag is "
                + "false, honestly.",
        },
        new PolicyBatteryCase
        {
            Id = "155",
            Group = "K",
            Covers = "§8 corpus 21; D199 PUSHDOWN_REQUIRED",
            File = "cases/155-members-pushdown-required-no-in-u2.sql",
            Principal = "u11",
            Source = PolicySourceProfile.AdoDuckDbNoIn,
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersPushdownRequired },
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "members", Use = PolicyUse.Shape, Detail = "PREDICATE_SHAPE_IN" },
            },
            Notes = "README §5 U18, answered: a one-org list simplifies to `org_id = 1`, an EQ "
                + "shape the profile does declare, so the plan pushed after all and the "
                + "refusal did not fire. The case was written for u2 for that very reason "
                + "and its own note asked a later run to move it to a two-org principal; "
                + "u11 is an auditor in two organisations, so the folded predicate is an IN "
                + "list the profile does not declare and `PUSHDOWN_REQUIRED` refuses rather "
                + "than fetching a source that holds every tenancy's rows. The message names "
                + "the shape in the host's own vocabulary from F46 — the source 'does not "
                + "declare PREDICATE_SHAPE_IN' — where it used to quote Calcite's "
                + "`SEARCH($1, Sarg[1, 3])`, a word no host writes wrapped around this "
                + "principal's own tenancy identifiers.",
        },
        new PolicyBatteryCase
        {
            Id = "156",
            Group = "K",
            Covers = "D199 — the constraint satisfied",
            File = "cases/156-members-pushdown-required-ok-u2.sql",
            Principal = "u2",
            Source = PolicySourceProfile.AdoDuckDb,
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersPushdownRequired },
            Compare = PolicyCompare.Ordered,
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
            },
            Notes = "The same descriptor over a source that declares the shape: "
                + "PUSHDOWN_REQUIRED is a constraint on the plan, not a change to the "
                + "semantics.",
        },
        new PolicyBatteryCase
        {
            Id = "157",
            Group = "K",
            Covers = "D156 — trust_source_row_security",
            File = "cases/157-members-trust-source-row-security-u2.sql",
            Principal = "u2",
            Source = PolicySourceProfile.AdoDuckDbTrusted,
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "T", "K", new DateOnly(1980, 1, 1), null, "2000"],
                    [2, 1, "R", "O", new DateOnly(1991, 1, 1), null, "2000"],
                    [3, 1, "P", "V", new DateOnly(1975, 1, 1), null, "3050"],
                    [4, 1, "T", "L", new DateOnly(1988, 1, 1), null, "2000"],
                    [5, 2, null, null, null, null, "4101"],
                    [6, 2, null, null, null, null, "4101"],
                    [7, 2, null, null, null, null, "5000"],
                    [8, 3, null, null, null, null, "6060"],
                    [9, 3, null, null, null, null, "6060"],
                    [10, 1, "R", "S", new DateOnly(1990, 1, 1), null, "3050"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.PerRow,
                    ["last_name"] = ReportedDisclosure.PerRow,
                    ["dob"] = ReportedDisclosure.PerRow,
                    ["national_id"] = ReportedDisclosure.PerRow,
                    ["postcode"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some, RowPredicatePushed = false },
                },
            },
            Notes = "README §5 U10, answered, and F44 with it. The setting skips Filter_R and "
                + "nothing else, so a test source with no row security of its own hands back "
                + "every row and Chalk's disclosures still apply: inside the principal's "
                + "scope the agent's rule matches and the value is masked, outside it no rule "
                + "matches and every protected column is its placeholder. One origin that is "
                + "both, which §3.12's meet resolves to PerRow. Before F44 the pass assumed "
                + "the scope it had not enforced, folded the agent's rule to a constant and "
                + "handed every returned row the mask — so an out-of-scope row's first_name "
                + "came back as an initial of a name the principal holds no grant for. "
                + "`visibility` is SOME because that is what this principal's grants come to; "
                + "D156 skips the filter and nothing else.",
        },
        new PolicyBatteryCase
        {
            Id = "158",
            Group = "K",
            Covers = "§8 corpus 16; §3.7 item 3, the filter-residual rule",
            File = "cases/158-members-mixed-filter-residual-u1.sql",
            Principal = "u1",
            Source = PolicySourceProfile.AdoDuckDb,
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some, RowPredicatePushed = true },
                },
                Claims = new PolicyClaims
                {
                    RemoteQueryContains = ["postcode"],
                    RemoteQueryNotContains = ["is_vip"],
                },
            },
            Notes = "`is_vip` is client-bodied and never travels; the tenancy conjunct and "
                + "the postcode equality do. Postcode '2000' holds for members 1, 2 and 4 "
                + "and `is_vip` for member 1 of those, so one row survives. This is the "
                + "case ADR 0025's V56 could not find in the existing corpus.",
        },
        new PolicyBatteryCase
        {
            Id = "159",
            Group = "K",
            Covers = "§2 item 2, §3.7 — a list above the ceiling pushed as a key set",
            File = "cases/159-members-key-set-pushdown-u13.sql",
            Principal = "u13",
            Source = PolicySourceProfile.AdoDuckDb,
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [2],
                    [3],
                    [4],
                    [10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some, RowPredicatePushed = true },
                },
            },
            Notes = "README §5 U9, answered, and F45 with it. §3.7 says a list above "
                + "`fold_max_rows` `is pushed as M5's key set`, and it now is: the bound list "
                + "drives a lookup join into the entitled scan, whose remote predicate is "
                + "`\"org_id\" IN (?)` with the keys bound into that one placeholder in calls of "
                + "at most `max_in_list`. `row_predicate_pushed` is true, as this case declared. "
                + "The 70 keys go in one call here, because the DuckDB profile's ceiling is "
                + "1000.",
            Disputed = new PolicyAdjudication(
                "ten rows",
                "five rows",
                "One answer left, and it is the fixture's own error, kept as case 131's "
                + "note keeps it: u13 binds 1..70 to exercise the fold ceiling and the "
                + "fixture has three organisations, so every row is visible and ten come "
                + "back. The other half of this case is settled — the key set reaches the "
                + "source and the flag is true (F45, ADR 0025 part 2g)."),
        },
        new PolicyBatteryCase
        {
            Id = "160",
            Group = "L",
            Covers = "§8 corpus 22; D203 — a k-anonymous raw group key",
            File = "cases/160-members-statistical-group-key-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersStatistical },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["Rina", 2],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["first_name"] = ReportedDisclosure.Aggregate,
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "UNCERTAIN (README §5 U11) on the reported disclosure. The five visible "
                + "members are Tomas, Rina, Petra, Tao and Rina, so with k = 2 only the "
                + "`Rina` group survives and the other three are DROPPED, not NULLed — "
                + "group suppression is what makes a raw key k-anonymous. The value in the "
                + "key column is raw, which no other case in this corpus returns for u2; "
                + "§3.12's five names do not include one for `raw under a k-anonymity "
                + "guarantee`, and `Aggregate` is the closest — it is the name for a value "
                + "the guard stands behind. `Masked` and `Full` are the other readings.",
        },
        new PolicyBatteryCase
        {
            Id = "161",
            Group = "L",
            Covers = "§8 corpus 22 — the filtered count",
            File = "cases/161-members-statistical-filtered-count-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersStatistical },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["Rina", 2, 2],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["first_name"] = ReportedDisclosure.Aggregate,
                    ["n"] = ReportedDisclosure.Full,
                    ["r"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Both members of the surviving group match `R%`, so the filtered count "
                + "equals the count. An aggregate's FILTER over the raw value is permitted "
                + "here and refused without the opt-in (case 071 on the other table). `r` is "
                + "reported `Aggregate` and `n` `Full` since D261: a call's FILTER is a value "
                + "the measure is derived from as much as its arguments are, so `r` is a "
                + "count *of* the raw column and `n` is a count of rows. The label was `Full` "
                + "before, which said of a number derived from a withheld value that it was "
                + "the table's own; this is the stricter reading and the true one.",
        },
        new PolicyBatteryCase
        {
            Id = "162",
            Group = "L",
            Covers = "§8 corpus 22; D203 — no pinning of an individual",
            File = "cases/162-members-statistical-pin-individual-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersStatistical },
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "members", Column = "id", Use = PolicyUse.PinUniqueKey },
            },
            Notes = "`id` is a declared unique key of the entitled table, so the predicate "
                + "names one individual and the statement is refused whatever the group "
                + "sizes are. Its negation (`id <> ?`, `id NOT IN (...)`) is refused for "
                + "the same reason — excluding one individual and differencing is the same "
                + "attack.",
        },
        new PolicyBatteryCase
        {
            Id = "163",
            Group = "L",
            Covers = "§8 corpus 22; D203 — no row-level output",
            File = "cases/163-members-statistical-row-level-column-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersStatistical },
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "members", Column = "id", Use = PolicyUse.RowLevelOutput },
            },
            Notes = "Grouping by a unique key makes every group one row, which is a per-row "
                + "membership test with no k at all. The design's own second half of corpus "
                + "22.",
        },
        new PolicyBatteryCase
        {
            Id = "164",
            Group = "L",
            Covers = "D203 — no window in a statistical statement",
            File = "cases/164-members-statistical-window-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersStatistical },
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "members", Column = "first_name", Use = PolicyUse.WindowInStatistical },
            },
            Notes = "A window's frame can be one row, so no suppression can be enforced on "
                + "it. Note the contrast with case 051, where a window over the MASKED "
                + "value of the same column is perfectly ordinary — the difference is which "
                + "value the window sees.",
        },
        new PolicyBatteryCase
        {
            Id = "165",
            Group = "L",
            Covers = "D203 — the raw predicate the opt-in exists for",
            File = "cases/165-members-statistical-raw-predicate-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersStatistical },
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [2],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Every output column is an aggregate, so the relaxation applies and the "
                + "predicate sees the raw name. Two members are called Rina and k is 2, so "
                + "the group survives.",
        },
        new PolicyBatteryCase
        {
            Id = "166",
            Group = "L",
            Covers = "D203 — group suppression on a global aggregate",
            File = "cases/166-members-statistical-suppressed-group-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersStatistical },
            Expect = new PolicyExpectation
            {
                Rows =
                [
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "UNCERTAIN (README §5 U12). One member is called Petra, below k, and D203 "
                + "says the group is DROPPED rather than NULLed — so a global aggregate "
                + "returns NO ROW AT ALL, which is a shape a SQL consumer does not expect "
                + "from `SELECT COUNT(*)`. That follows from the text and is worth "
                + "confirming: the alternative reading is that the injected `HAVING "
                + "COUNT(*) >= k` applies only where the statement itself groups.",
        },
        new PolicyBatteryCase
        {
            Id = "167",
            Group = "L",
            Covers = "§3.1 — the same statement without the opt-in",
            File = "cases/167-members-no-statistical-raw-predicate-u2.sql",
            Principal = "u2",
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [0],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Not an error and not a raw predicate: the comparison is against the "
                + "initial mask, no row matches, and the count is zero — one row holding "
                + "zero, where case 166 returns no row. The pair is the sharpest statement "
                + "of what `statistical` changes.",
        },
        new PolicyBatteryCase
        {
            Id = "186",
            Group = "L",
            Covers = "D203 — the opt-in under AGGREGATE_ONLY rather than under MASKED",
            File = "cases/186-orders-statistical-raw-predicate-u6.sql",
            Principal = "u6",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersStatistical },
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [7],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Seven of u6's eight visible orders are at or above 100 (only order 105, "
                + "at 50, is not), so the group is above the floor of 5 and survives "
                + "suppression. Without the opt-in this is case 066's refusal — the same "
                + "predicate on the same column.",
        },
        new PolicyBatteryCase
        {
            Id = "168",
            Group = "L",
            Covers = "§2, D209 — execute-time binding",
            File = "cases/168-members-star-execute-binding-u2.sql",
            Principal = "u2",
            Binding = PolicyBinding.Execute,
            Compare = PolicyCompare.Ordered,
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
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "UNCERTAIN (README §5 U19) on the report. The rows must equal case 002 "
                + "exactly. But nothing is folded at prepare, so the disclosure cannot be a "
                + "constant MASKED — it is row-dependent by construction, and `PerRow` may "
                + "be the honest answer under this binding for every entitled column. §3.12 "
                + "does not say. The rows are the assertion that matters; the report is "
                + "flagged.",
            Disputed = new PolicyAdjudication(
                "first_name's disclosure PerRow; last_name's disclosure PerRow; dob's "
                    + "disclosure PerRow; national_id's disclosure PerRow",
                "first_name's disclosure Masked; last_name's disclosure Masked; dob's "
                    + "disclosure Masked; national_id's disclosure Masked",
                "The engine is right and U19 predicted it: nothing is folded under a "
                    + "shape, so no column can be a constant. The rows equal the "
                    + "prepare-time case exactly, which is what the mode had to prove."),
        },
        new PolicyBatteryCase
        {
            Id = "169",
            Group = "L",
            Covers = "§3.11 — Omit degrades to Placeholder under execute-time binding",
            File = "cases/169-members-omit-degrades-execute-u2.sql",
            Principal = "u2",
            Binding = PolicyBinding.Execute,
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersNone },
            Options = PolicyCaseOptions.Default with { Redaction = new() { StarExpansion = StarExpansion.Omit } },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "T", "K", new DateOnly(1980, 1, 1), null, null],
                    [2, 1, "R", "O", new DateOnly(1991, 1, 1), null, null],
                    [3, 1, "P", "V", new DateOnly(1975, 1, 1), null, null],
                    [4, 1, "T", "L", new DateOnly(1988, 1, 1), null, null],
                    [10, 1, "R", "S", new DateOnly(1990, 1, 1), null, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Masked,
                    ["last_name"] = ReportedDisclosure.Masked,
                    ["dob"] = ReportedDisclosure.Masked,
                    ["national_id"] = ReportedDisclosure.Masked,
                    ["postcode"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Seven columns rather than case 096's six, and the prepared query says "
                + "the setting degraded — `the prepared query says so` is the design's "
                + "phrase and this is the case that reads it.",
            Disputed = new PolicyAdjudication(
                "first_name's disclosure PerRow; last_name's disclosure PerRow; dob's "
                    + "disclosure PerRow; national_id's disclosure PerRow",
                "first_name's disclosure Masked; last_name's disclosure Masked; dob's "
                    + "disclosure Masked; national_id's disclosure Masked",
                "The engine is right and U19 predicted it: nothing is folded under a "
                    + "shape, so no column can be a constant. The rows equal the "
                    + "prepare-time case exactly, which is what the mode had to prove."),
        },
        new PolicyBatteryCase
        {
            Id = "170",
            Group = "L",
            Covers = "§2 — a mixed-rights principal under execute-time binding",
            File = "cases/170-members-star-execute-binding-u1.sql",
            Principal = "u1",
            Binding = PolicyBinding.Execute,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 1, "Tomas", "Kalny", new DateOnly(1980, 3, 14), "NID-1001", "2000"],
                    [2, 1, "Rina", "Osei", new DateOnly(1991, 7, 2), "NID-1002", "2000"],
                    [3, 1, "Petra", "Vance", new DateOnly(1975, 11, 30), "NID-1003", "3050"],
                    [4, 1, "Tao", "Lindqvist", new DateOnly(1988, 1, 9), "NID-1004", "2000"],
                    [10, 1, "Rina", "Sandoval", new DateOnly(1990, 6, 6), "NID-1010", "3050"],
                    [5, 2, "T", "M", new DateOnly(1993, 1, 1), null, "4101"],
                    [6, 2, "U", "B", new DateOnly(1969, 1, 1), null, "4101"],
                    [7, 2, "N", "H", new DateOnly(1985, 1, 1), null, "5000"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.PerRow,
                    ["last_name"] = ReportedDisclosure.PerRow,
                    ["dob"] = ReportedDisclosure.PerRow,
                    ["national_id"] = ReportedDisclosure.PerRow,
                    ["postcode"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Identical to case 001 row for row, which is what §8's binding-time class "
                + "asks for. Under this binding each distinct rule condition is evaluated "
                + "once per leaf, so the plan holds two projections that PROJECT_MERGE "
                + "folds back together.",
        },
        new PolicyBatteryCase
        {
            Id = "171",
            Group = "M",
            Covers = "§3.12 D207 — the sibling column",
            File = "cases/171-disclosure-sibling-column-u1.sql",
            Principal = "u1",
            Options = PolicyCaseOptions.Default with { IncludeDisclosureColumns = true },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["Tomas", "FULL"],
                    ["Rina", "FULL"],
                    ["Petra", "FULL"],
                    ["Tao", "FULL"],
                    ["T", "MASKED"],
                    ["U", "MASKED"],
                    ["N", "MASKED"],
                    ["Rina", "FULL"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["first_name"] = ReportedDisclosure.PerRow,
                    ["first_name__disclosure"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Ordered by id: 1, 2, 3, 4, 5, 6, 7, 10. The per-column report says "
                + "PerRow and the sibling says which row is which — `PerRow` never appears "
                + "as a per-row VALUE, since per row the answer is one of the four "
                + "constants. UNCERTAIN (README §5 U20): the sibling's own reported "
                + "disclosure is not stated by the design; Full is encoded, on the grounds "
                + "that the name is not the data.",
        },
        new PolicyBatteryCase
        {
            Id = "172",
            Group = "M",
            Covers = "§3.12 D202, D207 — the per-row meet on a derived column",
            File = "cases/172-disclosure-sibling-derived-u1.sql",
            Principal = "u1",
            Options = PolicyCaseOptions.Default with { IncludeDisclosureColumns = true },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["Tomas/2000", "FULL"],
                    ["Rina/2000", "FULL"],
                    ["Petra/3050", "FULL"],
                    ["Tao/2000", "FULL"],
                    ["T/4101", "MASKED"],
                    ["U/4101", "MASKED"],
                    ["N/5000", "MASKED"],
                    ["Rina/3050", "FULL"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["tag"] = ReportedDisclosure.PerRow,
                    ["tag__disclosure"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "`postcode` is Full for every row, so the meet is decided by `first_name` "
                + "alone — and the meet is conservative by rule, so a Masked origin makes "
                + "the derived value Masked.",
        },
        new PolicyBatteryCase
        {
            Id = "173",
            Group = "M",
            Covers = "§3.12 D207 — a column with no entitled origin has no sibling",
            File = "cases/173-disclosure-sibling-unentitled-u2.sql",
            Principal = "u2",
            Options = PolicyCaseOptions.Default with { IncludeDisclosureColumns = true },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["DONE", 101, "FULL"],
                    ["DONE", 102, "FULL"],
                    ["OPEN", 103, "FULL"],
                    ["OPEN", 104, "FULL"],
                    ["HOLD", 105, "FULL"],
                    ["NEW", 106, "FULL"],
                    ["OPEN", 107, "FULL"],
                    ["DONE", 108, "FULL"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["code"] = ReportedDisclosure.Full,
                    ["id"] = ReportedDisclosure.Full,
                    ["id__disclosure"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "`code` comes from `symbols`, which carries no entitlement, so it gets no "
                + "sibling; `o.id` comes from an entitled table and gets one even though it "
                + "is always Full, because the design emits the sibling even where the "
                + "disclosure is constant, so that a consumer has one code path.",
        },
        new PolicyBatteryCase
        {
            Id = "174",
            Group = "M",
            Covers = "§3.12 D207 — a suffix collision",
            File = "cases/174-disclosure-sibling-collision-u1.sql",
            Principal = "u1",
            Options = PolicyCaseOptions.Default with { IncludeDisclosureColumns = true },
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Table = "members", Column = "first_name", Use = PolicyUse.DisclosureSuffixCollision, Detail = "__disclosure" },
            },
            Notes = "UNCERTAIN (README §5 U21). The design says `refused at prepare naming "
                + "the column and the suffix` without naming the kind. POLICY is encoded "
                + "because the refusal exists for the policy layer's sake; VALIDATION is "
                + "defensible, since the clash is between two output names.",
            Disputed = new PolicyAdjudication(
                "the refusal names the table Refused by the entitlements: this "
                    + "statement already produces a column called 'first_name__disclosure', "
                    + "and PrepareOptions.IncludeDisclosureColumns would name the sibling "
                    + "of 'first_name' the same.",
                "the refusal names the table members",
                "The engine is right: the clash is between two output names, so the "
                    + "refusal names the column and the suffix and not a table (README §5 "
                    + "U21)."),
        },
        new PolicyBatteryCase
        {
            Id = "175",
            Group = "M",
            Covers = "§3.12 D207 — DisclosureColumnSuffix",
            File = "cases/175-disclosure-sibling-suffix-option-u2.sql",
            Principal = "u2",
            Options = PolicyCaseOptions.Default with { IncludeDisclosureColumns = true, DisclosureColumnSuffix = "__d" },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["T", "MASKED"],
                    ["R", "MASKED"],
                    ["P", "MASKED"],
                    ["T", "MASKED"],
                    ["R", "MASKED"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["first_name"] = ReportedDisclosure.Masked,
                    ["first_name__d"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The sibling is emitted even where the disclosure is a constant.",
        },
        new PolicyBatteryCase
        {
            Id = "176",
            Group = "M",
            Covers = "§3.12 D207 — the sibling of an undisclosed column",
            File = "cases/176-disclosure-sibling-undisclosed-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersNone },
            Options = PolicyCaseOptions.Default with { IncludeDisclosureColumns = true },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [null, "REDACTED"],
                    [null, "REDACTED"],
                    [null, "REDACTED"],
                    [null, "REDACTED"],
                    [null, "REDACTED"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["postcode"] = ReportedDisclosure.Redacted,
                    ["postcode__disclosure"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "This is what keeps an empty value from passing as data under "
                + "PlaceholdersAsEmpty: the row says the column was not disclosed, in the "
                + "row itself.",
        },
        new PolicyBatteryCase
        {
            Id = "177",
            Group = "N",
            Covers = "§8 corpus 19; §3.1 — an equality join on tokens",
            File = "cases/177-fingerprint-self-join-u6.sql",
            Principal = "u6",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [2],
                    [2],
                    [3],
                    [4],
                    [10],
                    [10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Five visible members; members 2 and 10 are both Rina and therefore carry "
                + "one token, so they pair with each other as well as with themselves — "
                + "seven rows. No name is disclosed anywhere in the result.",
        },
        new PolicyBatteryCase
        {
            Id = "178",
            Group = "N",
            Covers = "§8 corpus 19 across two tenancies",
            File = "cases/178-fingerprint-join-across-tenancies-u11.sql",
            Principal = "u11",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [2],
                    [2],
                    [3],
                    [4],
                    [4],
                    [8],
                    [8],
                    [9],
                    [10],
                    [10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Seven visible members over O1 and O3. Members 4 and 8 are both Tao in "
                + "different tenancies and pair across the boundary; the token is what "
                + "carries the equality, and one mask key per principal is what makes the "
                + "two tokens equal. Eleven rows.",
        },
        new PolicyBatteryCase
        {
            Id = "179",
            Group = "N",
            Covers = "§8 corpus 19 for a principal with two kinds of disclosure",
            File = "cases/179-fingerprint-join-mixed-u1.sql",
            Principal = "u1",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1],
                    [2],
                    [2],
                    [3],
                    [4],
                    [5],
                    [6],
                    [7],
                    [10],
                    [10],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Raw names in O1 and initials in O2. `Tomas` never equals `T`, so the two "
                + "tenancies do not pair — masking with two different methods is what a "
                + "fingerprint mask avoids, and this case is the contrast that shows why "
                + "178 works.",
        },
        new PolicyBatteryCase
        {
            Id = "180",
            Group = "O",
            Covers = "§8 corpus 23; §1, D208",
            File = "cases/180-registration-otherwise-full.sql",
            Principal = "u2",
            AtRegistration = true,
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.BadOtherwiseFull },
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.InvalidCatalog, Table = "members", Column = "postcode", Detail = "otherwise must be NONE on a table with a row predicate" },
            },
            Notes = "The check that closes §3.9: a column with `otherwise: FULL` would hand a "
                + "failing function the raw value of an EXCLUDED row, and the error text "
                + "would be the channel.",
            Disputed = new PolicyAdjudication(
                "the refusal names the detail Invalid catalog at schemas[0] "
                    + "(main).tables[1] (members).entitlement.columns[0] "
                    + "(postcode).otherwise: otherwise is Full on a table that has a row "
                    + "predicate.",
                "the refusal names the detail otherwise must be NONE on a table with "
                    + "a row predicate",
                "Both are right about the same refusal: the otherwise-is-FULL check "
                    + "fires at registration as the corpus asks, with different words than "
                    + "the corpus quoted."),
        },
        new PolicyBatteryCase
        {
            Id = "181",
            Group = "O",
            Covers = "§8 corpus 23; §1, D190",
            File = "cases/181-registration-aggregate-max.sql",
            Principal = "u6",
            AtRegistration = true,
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.BadAggregateMax },
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.InvalidCatalog, Table = "orders", Column = "amount", Detail = "not a population aggregate" },
            },
        },
        new PolicyBatteryCase
        {
            Id = "182",
            Group = "O",
            Covers = "§8 corpus 23; §1 — every `when` is boolean",
            File = "cases/182-registration-when-not-boolean.sql",
            Principal = "u2",
            AtRegistration = true,
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.BadWhenNotBoolean },
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.InvalidCatalog, Table = "members", Column = "first_name", Detail = "rule condition is not boolean" },
            },
            Notes = "Type-checked by the sidecar at registration, over a throwaway cluster, "
                + "by the same conversion the pass uses per request (§3.2).",
        },
        new PolicyBatteryCase
        {
            Id = "183",
            Group = "O",
            Covers = "§8 corpus 23; §1 — a mask has the column's type",
            File = "cases/183-registration-rule-mask-type.sql",
            Principal = "u2",
            AtRegistration = true,
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.BadRuleMaskType },
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.InvalidCatalog, Table = "members", Column = "dob", Detail = "mask type STRING does not match column type DATE" },
            },
            Notes = "The same reason the auditor's token mask cannot be used on `dob` in the "
                + "real descriptor (case 006): FINGERPRINT answers in STRING, and a mask "
                + "stays in the type.",
        },
        new PolicyBatteryCase
        {
            Id = "184",
            Group = "P",
            Covers = "D162 — a per-column placeholder on a STRING column",
            File = "cases/184-members-postcode-column-placeholder-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersNonePlaceholder },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    ["0000"],
                    ["0000"],
                    ["0000"],
                    ["0000"],
                    ["0000"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["postcode"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The counterpart of case 104 on a table where the column is undisclosed "
                + "for every principal but a manager: the declared stand-in wins over "
                + "PlaceholdersAsNull and the column is still reported Undisclosed, which "
                + "is what keeps '0000' from passing as a postcode.",
        },
        new PolicyBatteryCase
        {
            Id = "185",
            Group = "P",
            Covers = "§2, §5, README §5 A1 — the path dimension as a context list",
            File = "cases/185-orders-context-list-path-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersCtxPath },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [101],
                    [102],
                    [103],
                    [104],
                    [105],
                    [106],
                    [107],
                    [108],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "`member_id IN (@ctx.scope_member_ids)` reaches exactly the rows `org_id "
                + "IN (@ctx.agent_orgs)` reaches, because the fixture keeps the "
                + "denormalised column consistent with the path. This is the encoding a "
                + "compiler must emit if layer A is never allowed to mention a second "
                + "catalog table (§10), and the case exists so that a later run can compare "
                + "the two plans — the rows cannot tell them apart.",
        },
        new PolicyBatteryCase
        {
            Id = "187",
            Group = "F",
            Covers = "§3.11 D217 — the second knob, over a named column",
            File = "cases/187-members-postcode-none-named-refuse-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersNone },
            Options = PolicyCaseOptions.Default with
            {
                Redaction = new() { NamedColumns = NamedColumns.Refuse },
            },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Refusal = new PolicyRefusal { Kind = PolicyErrorKind.Policy, Column = "postcode", Use = PolicyUse.NamedRedacted },
            },
            Notes = "Case 101's statement under the policy's other member. `StarExpansion.Refuse` "
                + "is about the columns a star's expansion surfaces and leaves this one a "
                + "placeholder; this is the host that asked to be told about a column it named "
                + "itself (D217).",
        },
        new PolicyBatteryCase
        {
            Id = "188",
            Group = "P",
            Covers = "§3.11 D224 — a rule's own placeholder, on a STRING and on a DATE column",
            File = "cases/188-members-rule-placeholder-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersRulePlaceholder },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, "withheld from agents", new DateOnly(1900, 1, 1)],
                    [2, "withheld from agents", new DateOnly(1900, 1, 1)],
                    [3, "withheld from agents", new DateOnly(1900, 1, 1)],
                    [4, "withheld from agents", new DateOnly(1900, 1, 1)],
                    [10, "withheld from agents", new DateOnly(1900, 1, 1)],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["first_name"] = ReportedDisclosure.Redacted,
                    ["dob"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The rule that withheld the value is the one that says what stands in its "
                + "place, and it wins over the column's own stand-in and over "
                + "PlaceholdersAsNull. The DATE column is the sharper half: a date has no "
                + "spelling an empty value could take, so a host that needs a non-null one "
                + "names it. Both columns are still reported REDACTED, which is what keeps "
                + "either from passing as data.",
        },
        new PolicyBatteryCase
        {
            Id = "189",
            Group = "P",
            Covers = "§3.11 D224 — a rule placeholder is never a query use",
            File = "cases/189-members-rule-placeholder-predicate-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersRulePlaceholder },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows = [],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The same descriptor, and the predicate the placeholder does not make true. "
                + "Under leaf sanitisation (§3.1, D195) a statement's predicate is evaluated "
                + "over the sanitiser rather than refused, so what this agent compares is the "
                + "rule's stand-in and not the name: no row matches, where a manager reading "
                + "the same statement finds member 1. That is what \"never a query use\" comes "
                + "to for a placeholder — the withheld value never reaches the comparison — "
                + "and it is what tells a NONE rule with a placeholder from a MASKED rule with "
                + "a constant mask, whose value the statement genuinely may compare.",
        },
        new PolicyBatteryCase
        {
            Id = "190",
            Group = "K",
            Covers = "§2 item 2, §3.7 — a composite list above the ceiling as a key set (F50)",
            File = "cases/190-members-composite-key-set-pushdown-u15.sql",
            Principal = "u15",
            Source = PolicySourceProfile.AdoDuckDb,
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [8],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some, RowPredicatePushed = true },
                },
            },
            Notes = "Case 159 with a list of pairs rather than of organisations, which is what "
                + "the tenancy package writes for a confined subject grant. The seventy pairs "
                + "are above the fold ceiling, so none of them is in the plan; the remote "
                + "predicate is `(\"id\", \"org_id\") IN (?)` and the executor expands that one "
                + "placeholder into `(?, ?), (?, ?), …` per call. Two of the pairs are "
                + "deliberately the wrong way round — (4, 2) and (6, 1), where member 4 is in "
                + "organization 1 and member 6 in organization 2 — so a key set over each "
                + "column separately would return those two members as well, and the tuple "
                + "returns only member 8.",
        },

        // ---- D261: the test verdict, written directly as a DisclosureRule ----

        // D261's six layer-A cases are group **T**, not the **M** the code carried: M is the
        // disclosure-sibling family of `corpus/policy/README.md` §1 (171-176), and the README had
        // already given these their own row under T. The letter is a non-asserted string the run
        // report prints, so nothing about any expectation, adjudication or count moves with it.
        new PolicyBatteryCase
        {
            Id = "191",
            Group = "T",
            Covers = "D261 §2 — the select-list form of a TEST rule",
            File = "cases/191-members-tested-select-list-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersTested },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, false],
                    [2, true],
                    [3, false],
                    [4, false],
                    [10, false],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["confirmed"] = ReportedDisclosure.Tested,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "One boolean per visible row and never the value: the comparison is computed "
                + "in the leaf over the raw `national_id`, and `Tested` is what the caller is "
                + "told this column is.",
        },
        new PolicyBatteryCase
        {
            Id = "192",
            Group = "T",
            Covers = "D261 §2 — the predicate form",
            File = "cases/192-members-tested-predicate-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersTested },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows = [[2]],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The same substitution in a WHERE. Under the same rule written NONE the "
                + "predicate would compare the stand-in and select nothing, which is case 194.",
        },
        new PolicyBatteryCase
        {
            Id = "193",
            Group = "T",
            Covers = "D261 §2 — `IN (list)`, one shape and one lifted comparison",
            File = "cases/193-members-tested-in-list-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersTested },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows = [[2], [4]],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "A list of candidate values is still one probe. A comparison with a *column* "
                + "is refused precisely so that a VALUES list cannot turn it into a thousand, "
                + "which is the adversarial family's 43 and 48.",
        },
        new PolicyBatteryCase
        {
            Id = "194",
            Group = "T",
            Covers = "D261 §3 — a shape the rule does not name",
            File = "cases/194-members-tested-like-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersTested },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows = [],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "`LIKE` is not one of the three, so what the pattern is matched against is the "
                + "placeholder — and every `NID-…` the fixture holds is behind it.",
        },
        new PolicyBatteryCase
        {
            Id = "195",
            Group = "T",
            Covers = "D261 §2 — the value itself is never disclosed",
            File = "cases/195-members-tested-value-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersTested },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, null],
                    [2, null],
                    [3, null],
                    [4, null],
                    [10, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["national_id"] = ReportedDisclosure.Tested,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The placeholder for every row, and the label says which kind of withholding "
                + "it is: `Tested` rather than `Redacted`, because a comparison of it is "
                + "available and a consumer is entitled to know that one bit exists.",
        },
        new PolicyBatteryCase
        {
            Id = "196",
            Group = "T",
            Covers = "D261 §2 — the aggregate form under the group-size guard",
            File = "cases/196-members-counted-filtered-u2.sql",
            Principal = "u2",
            Catalog = PolicyCatalog.Default with { Members = PolicyEntitlements.MembersCounted },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows = [[1L]],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["n"] = ReportedDisclosure.Aggregate,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["members"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The one place a population-only column may be compared: the FILTER of an "
                + "aggregate its own allow-list names, with the shape its own rule names. Five "
                + "visible rows clear the floor of three, so the count is disclosed; `Aggregate` "
                + "is the label because below the floor it would be a withheld NULL (D202).",
        },
        new PolicyBatteryCase
        {
            Id = "197",
            Group = "Q",
            Covers = "D266 §4 — one membership over a tuple of arity three",
            File = "cases/197-orders-confined-u16.sql",
            Principal = "u16",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersConfined },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [101, 1, 1, 1],
                    [102, 1, 1, 1],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["member_id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["created_by"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The grant is (member 1, organisation 1, desk 1). Order 105 is member 1's in "
                + "organisation 1 and was written at desk 3, so it is not reached: two of the "
                + "three columns matching is not a match, which is what the conjunction means.",
        },
        new PolicyBatteryCase
        {
            Id = "198",
            Group = "Q",
            Covers = "D266 §1 — a conjunction and not a union",
            File = "cases/198-orders-confined-other-desk-u17.sql",
            Principal = "u17",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersConfined },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows = [[105, 1, 1, 3]],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["member_id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["created_by"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The very same tuple with its third column changed, and the answer is the "
                + "complement of 197's within member 1's organisation-1 orders. Two memberships "
                + "OR-ed would have given every one of them to both principals.",
        },
        new PolicyBatteryCase
        {
            Id = "199",
            Group = "Q",
            Covers = "D266 §0 — grants still OR; the conjunction is inside one grant",
            File = "cases/199-orders-two-confined-grants-u18.sql",
            Principal = "u18",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersConfined },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [101, 1, 1, 1],
                    [102, 1, 1, 1],
                    [107, 4, 1, 2],
                    [108, 4, 1, 2],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["member_id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.Full,
                    ["created_by"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Two conjoined grants, two rows of the tuple list, and the union of what each "
                + "reaches. Nothing about how grants combine changed: what D266 added is the "
                + "conjunction *inside* one of them.",
        },
        new PolicyBatteryCase
        {
            Id = "200",
            Group = "Q",
            Covers = "D266 §4 — a role condition takes the same conjoined term",
            File = "cases/200-orders-confined-note-u16.sql",
            Principal = "u16",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersConfined },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [101, "first order"],
                    [102, "repeat"],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["note"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "The column rule's condition is the membership the row predicate admitted the "
                + "row by, so the verdict is decided on the same row and folds to a constant — "
                + "`Full` rather than `PerRow` — for a principal whose only grant is the "
                + "conjoined one.",
        },
        new PolicyBatteryCase
        {
            Id = "201",
            Group = "U",
            Covers = "D265 §0, §4 — a row visible through the rows that belong to it",
            File = "cases/201-orders-related-vendor-u19.sql",
            Principal = "u19",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersRelated },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [101, null],
                    [109, null],
                    [115, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.PerRow,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "V1 supplies items 1 and 2, which appear on the lines of orders 101, 109 and "
                + "115 — and 109 is in organisation 2 and 115 in organisation 3, which this "
                + "principal holds no grant in at all. That is what makes related visibility an "
                + "assertion rather than a coincidence. Every other order carries no line of V1's, "
                + "so no path reaches it. The organisation is the placeholder: the rule that "
                + "grants it is the table's own reach, which this principal does not have, and the "
                + "vendor's rule is second and grants nothing.",
        },
        new PolicyBatteryCase
        {
            Id = "202",
            Group = "U",
            Covers = "D265 §8, D222 — the rule order where both perspectives meet",
            File = "cases/202-orders-related-vendor-and-org-u20.sql",
            Principal = "u20",
            Catalog = PolicyCatalog.Default with { Orders = PolicyEntitlements.OrdersRelated },
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [101, 1],
                    [102, 1],
                    [103, 1],
                    [104, 1],
                    [105, 1],
                    [106, 1],
                    [107, 1],
                    [108, 1],
                    [109, null],
                    [115, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["org_id"] = ReportedDisclosure.PerRow,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["orders"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Restrictions OR, so this principal sees organisation 1's eight orders and the "
                + "two more the path reaches. The rule order is what decides the column: the "
                + "organisation's own reach is written first and stops on match, so 101 — which "
                + "both routes reach — reads the value, and 109 and 115, which only the path "
                + "reaches, read the placeholder.",
        },
        new PolicyBatteryCase
        {
            Id = "203",
            Group = "U",
            Covers = "D265 §8 — the bridge entitled by kind",
            File = "cases/203-order-items-by-kind-vendor-u19.sql",
            Principal = "u19",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 101, 2, null],
                    [4, 109, 3, null],
                    [5, 115, 1, null],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["order_id"] = ReportedDisclosure.Full,
                    ["quantity"] = ReportedDisclosure.Full,
                    ["unit_price"] = ReportedDisclosure.Redacted,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["order_items"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "Line 2 is on order 101 and carries V2's item, so this principal does not see "
                + "it even though it sees the order and the line beside it: a line inherits its "
                + "vendor from its own item, which is the whole of the difference from §3.13's "
                + "kind-less `Through`. The price is the placeholder for every line it does see, "
                + "so the verdict is a constant and the column is reported `Redacted` rather than "
                + "`PerRow`.",
        },
        new PolicyBatteryCase
        {
            Id = "204",
            Group = "U",
            Covers = "D265 §4 — an endpoint predicate that folds to FALSE drops the chain",
            File = "cases/204-order-items-by-kind-manager-u21.sql",
            Principal = "u21",
            Compare = PolicyCompare.Ordered,
            Expect = new PolicyExpectation
            {
                Rows =
                [
                    [1, 101, 2, 50L],
                    [2, 101, 4, 30L],
                    [3, 103, 1, 200L],
                ],
                Labels = new ColumnLabels(StringComparer.Ordinal)
                {
                    ["id"] = ReportedDisclosure.Full,
                    ["order_id"] = ReportedDisclosure.Full,
                    ["quantity"] = ReportedDisclosure.Full,
                    ["unit_price"] = ReportedDisclosure.Full,
                },
                Report = new TableReport(StringComparer.Ordinal)
                {
                    ["order_items"] = new() { Visibility = TableVisibility.Some },
                },
            },
            Notes = "A manager in organisation 1 and nothing else. The vendor path grants nothing "
                + "and its chain is not built at all; the organisation's path reaches the lines of "
                + "orders 101 and 103, both of which are that organisation's. The price's first "
                + "rule is the organisation's reach at the endpoint, which is exactly the "
                + "endpoint's own predicate for this principal, so the verdict folds to a constant "
                + "and the column is full.",
        },
    ];
}
