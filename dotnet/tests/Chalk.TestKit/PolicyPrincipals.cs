using Chalk.Catalog;
using Chalk.Client;

namespace Chalk.TestKit;

/// <summary>
/// The battery's principals as bound <see cref="RequestContext"/>s
/// (<c>docs/design/16-entitlements.md</c> §2; <c>corpus/policy/README.md</c>).
/// </summary>
/// <remarks>
/// Every name any descriptor mentions is bound for every principal, empty where the principal holds
/// no grant of that kind: a name a descriptor reads must be bound, and an empty list is what makes a
/// membership test false. A list's column names are for the reader alone — a membership test
/// compares by position — and its width is the width the widest principal binds, because an empty
/// list still declares the arity the test compares against.
/// </remarks>
public static class PolicyPrincipals
{
    /// <summary>Every principal, by the name a case names it with.</summary>
    public static IReadOnlyDictionary<string, RequestContext> All { get; } =
        new Dictionary<string, RequestContext>(StringComparer.Ordinal)
    {
        ["u1"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 1,
                ["mask_key"] = "key-u1",
                ["global"] = false,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = List("manager_orgs", 1, [1]),
                ["agent_orgs"] = List("agent_orgs", 1, [2]),
                ["auditor_orgs"] = List("auditor_orgs", 1),
                ["subject_ids"] = List("subject_ids", 1),
                ["subject_pairs"] = List("subject_pairs", 2),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1, [1]),
                ["blocked_orgs"] = List("blocked_orgs", 1, [2]),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1,
                    [1], [2], [3], [4], [5], [6], [7], [10]),
            },
        },
        ["u10"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 95,
                ["mask_key"] = "key-u10",
                ["global"] = false,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = List("manager_orgs", 1),
                ["agent_orgs"] = List("agent_orgs", 1),
                ["auditor_orgs"] = List("auditor_orgs", 1),
                ["subject_ids"] = List("subject_ids", 1),
                ["subject_pairs"] = List("subject_pairs", 2),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1),
                ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1),
            },
        },
        ["u11"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 96,
                ["mask_key"] = "key-u11",
                ["global"] = false,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = List("manager_orgs", 1),
                ["agent_orgs"] = List("agent_orgs", 1),
                ["auditor_orgs"] = List("auditor_orgs", 1, [1], [3]),
                ["subject_ids"] = List("subject_ids", 1),
                ["subject_pairs"] = List("subject_pairs", 2),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1, [1], [3]),
                ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1,
                    [1], [2], [3], [4], [10], [8], [9]),
            },
        },
        ["u12"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 97,
                ["mask_key"] = "key-u12",
                ["global"] = false,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = List("manager_orgs", 1, [1]),
                ["agent_orgs"] = List("agent_orgs", 1),
                ["auditor_orgs"] = List("auditor_orgs", 1, [2]),
                ["subject_ids"] = List("subject_ids", 1),
                ["subject_pairs"] = List("subject_pairs", 2),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1, [1], [2]),
                ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1,
                    [1], [2], [3], [4], [10], [5], [6], [7]),
            },
        },
        ["u13"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 98,
                ["mask_key"] = "key-u13",
                ["global"] = false,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = Range("manager_orgs", 1, 70),
                ["agent_orgs"] = List("agent_orgs", 1),
                ["auditor_orgs"] = List("auditor_orgs", 1),
                ["subject_ids"] = List("subject_ids", 1),
                ["subject_pairs"] = List("subject_pairs", 2),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1, [1]),
                ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1, [1], [2], [3], [4], [10]),
            },
        },
        ["u14"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 99,
                ["mask_key"] = "key-u14",
                ["global"] = false,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = List("manager_orgs", 1, [1]),
                ["agent_orgs"] = List("agent_orgs", 1),
                ["auditor_orgs"] = List("auditor_orgs", 1),
                ["subject_ids"] = List("subject_ids", 1),
                ["subject_pairs"] = List("subject_pairs", 2),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1, [1]),
                ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1, [1], [2], [3], [4], [10]),
            },
        },
        ["u15"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 97,
                ["mask_key"] = "key-u15",
                ["global"] = false,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },

            // F50: seventy subject *pairs*, above the default fold ceiling of 64, and nothing else
            // granted — so the scope predicate folds down to the pair membership alone and the
            // list stays a relation. Three of the pairs say something about this fixture and the
            // rest say nothing; (4, 2) and (6, 1) are deliberately the wrong way round — member 4
            // is in organization 1 and member 6 in organization 2 — so a key set over each column
            // separately would return them and a key set over the tuple does not.
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = List("manager_orgs", 1),
                ["agent_orgs"] = List("agent_orgs", 1),
                ["auditor_orgs"] = List("auditor_orgs", 1),
                ["subject_ids"] = List("subject_ids", 1),
                ["subject_pairs"] = List(
                    "subject_pairs",
                    2,
                    [.. new int[][] { [8, 3], [4, 2], [6, 1] }
                        .Concat(Enumerable.Range(100, 67).Select(i => new[] { i, 9 }))]),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1),
                ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1, [8]),
            },
        },
        ["u2"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 2,
                ["mask_key"] = "key-u2",
                ["global"] = false,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = List("manager_orgs", 1),
                ["agent_orgs"] = List("agent_orgs", 1, [1]),
                ["auditor_orgs"] = List("auditor_orgs", 1),
                ["subject_ids"] = List("subject_ids", 1),
                ["subject_pairs"] = List("subject_pairs", 2),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1, [1]),
                ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1, [1], [2], [3], [4], [10]),
            },
        },
        ["u3"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 4,
                ["mask_key"] = "key-u3",
                ["global"] = false,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = List("manager_orgs", 1),
                ["agent_orgs"] = List("agent_orgs", 1),
                ["auditor_orgs"] = List("auditor_orgs", 1),
                ["subject_ids"] = List("subject_ids", 1),
                ["subject_pairs"] = List("subject_pairs", 2, [4, 1]),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1),
                ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1, [4]),
            },
        },
        ["u3b"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 4,
                ["mask_key"] = "key-u3b",
                ["global"] = false,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = List("manager_orgs", 1),
                ["agent_orgs"] = List("agent_orgs", 1),
                ["auditor_orgs"] = List("auditor_orgs", 1),
                ["subject_ids"] = List("subject_ids", 1),
                ["subject_pairs"] = List("subject_pairs", 2, [4, 2]),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1),
                ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1),
            },
        },
        ["u4"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 90,
                ["mask_key"] = "key-u4",
                ["global"] = true,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = List("manager_orgs", 1),
                ["agent_orgs"] = List("agent_orgs", 1),
                ["auditor_orgs"] = List("auditor_orgs", 1),
                ["subject_ids"] = List("subject_ids", 1),
                ["subject_pairs"] = List("subject_pairs", 2),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1, [1], [2], [3]),
                ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1),
            },
        },
        ["u5"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 91,
                ["mask_key"] = "key-u5",
                ["global"] = false,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = List("manager_orgs", 1),
                ["agent_orgs"] = List("agent_orgs", 1),
                ["auditor_orgs"] = List("auditor_orgs", 1),
                ["subject_ids"] = List("subject_ids", 1),
                ["subject_pairs"] = List("subject_pairs", 2),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1),
                ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1),
            },
        },
        ["u6"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 92,
                ["mask_key"] = "key-u6",
                ["global"] = false,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = List("manager_orgs", 1),
                ["agent_orgs"] = List("agent_orgs", 1),
                ["auditor_orgs"] = List("auditor_orgs", 1, [1]),
                ["subject_ids"] = List("subject_ids", 1),
                ["subject_pairs"] = List("subject_pairs", 2),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1, [1]),
                ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1, [1], [2], [3], [4], [10]),
            },
        },
        ["u7"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 93,
                ["mask_key"] = "key-u7",
                ["global"] = false,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = List("manager_orgs", 1),
                ["agent_orgs"] = List("agent_orgs", 1, [1]),
                ["auditor_orgs"] = List("auditor_orgs", 1, [1]),
                ["subject_ids"] = List("subject_ids", 1),
                ["subject_pairs"] = List("subject_pairs", 2),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1, [1]),
                ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1, [1], [2], [3], [4], [10]),
            },
        },
        ["u8"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 7,
                ["mask_key"] = "key-u8",
                ["global"] = false,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = List("manager_orgs", 1),
                ["agent_orgs"] = List("agent_orgs", 1),
                ["auditor_orgs"] = List("auditor_orgs", 1),
                ["subject_ids"] = List("subject_ids", 1, [7]),
                ["subject_pairs"] = List("subject_pairs", 2),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1),
                ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1, [7]),
            },
        },
        ["u9"] = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 94,
                ["mask_key"] = "key-u9",
                ["global"] = false,
            },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["user"] = ChalkType.Int32(),
                ["mask_key"] = ChalkType.String(),
                ["global"] = ChalkType.Bool(),
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = List("manager_orgs", 1),
                ["agent_orgs"] = List("agent_orgs", 1, [2]),
                ["auditor_orgs"] = List("auditor_orgs", 1),
                ["subject_ids"] = List("subject_ids", 1),
                ["subject_pairs"] = List("subject_pairs", 2),
                ["subject_org_desks"] = List("subject_org_desks", 3),
                ["allowed_orgs"] = List("allowed_orgs", 1, [2]),
                ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
                ["scope_member_ids"] = List("scope_member_ids", 1, [5], [6], [7]),
            },
        },
        // D266's three: one grant apiece, with the conjunction inside it. `u16` and `u17` differ
        // in the third column of the very same tuple, which is what tells a conjunction from a
        // union; `u18` holds two conjoined grants, because grants go on OR-ing with each other.
        ["u16"] = Confined([[1, 1, 1]]),
        ["u17"] = Confined([[1, 1, 3]]),
        ["u18"] = Confined([[1, 1, 1], [4, 1, 2]]),
        // D265 clause (h)'s two (group U): a vendor grant alone, and the same grant beside a
        // manager's in organisation 1, so that both perspectives meet on one order.
        ["u19"] = Vendor([1], managerOrgs: []),
        ["u20"] = Vendor([1], managerOrgs: [1]),
        // And one holding the organisation's grant alone, so that the vendor path's endpoint
        // predicate folds to FALSE and its chain is not built at all.
        ["u21"] = Vendor([], managerOrgs: [1]),
    };

    /// <summary>
    /// A principal holding a vendor grant, and optionally a manager's beside it (D265 §8): what it
    /// sees of an order comes through the lines of it that carry one of its own items, and what it
    /// sees of a line comes through that line's own item.
    /// </summary>
    private static RequestContext Vendor(int[] vendors, int[] managerOrgs) => new()
    {
        Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["user"] = 97,
            ["mask_key"] = "key-vendor",
            ["global"] = false,
        },
        ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
        {
            ["user"] = ChalkType.Int32(),
            ["mask_key"] = ChalkType.String(),
            ["global"] = ChalkType.Bool(),
        },
        Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
        {
            ["manager_orgs"] = List("manager_orgs", 1, [.. managerOrgs.Select(o => new int[] { o })]),
            ["agent_orgs"] = List("agent_orgs", 1),
            ["auditor_orgs"] = List("auditor_orgs", 1),
            ["subject_ids"] = List("subject_ids", 1),
            ["subject_pairs"] = List("subject_pairs", 2),
            ["subject_org_desks"] = List("subject_org_desks", 3),
            ["allowed_orgs"] = List("allowed_orgs", 1),
            ["blocked_orgs"] = List("blocked_orgs", 1),
            ["vendor_ids"] = List("vendor_ids", 1, [.. vendors.Select(v => new int[] { v })]),
            ["scope_member_ids"] = List("scope_member_ids", 1),
        },
    };

    /// <summary>
    /// A principal holding nothing but conjoined subject grants (D266): each is one row of
    /// <c>subject_org_desks</c>, and all three of its columns must match the same row at once.
    /// </summary>
    private static RequestContext Confined(int[][] grants) => new()
    {
        Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["user"] = 98,
            ["mask_key"] = "key-confined",
            ["global"] = false,
        },
        ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
        {
            ["user"] = ChalkType.Int32(),
            ["mask_key"] = ChalkType.String(),
            ["global"] = ChalkType.Bool(),
        },
        Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
        {
            ["manager_orgs"] = List("manager_orgs", 1),
            ["agent_orgs"] = List("agent_orgs", 1),
            ["auditor_orgs"] = List("auditor_orgs", 1),
            ["subject_ids"] = List("subject_ids", 1),
            ["subject_pairs"] = List("subject_pairs", 2),
            ["subject_org_desks"] = List("subject_org_desks", 3, grants),
            ["allowed_orgs"] = List("allowed_orgs", 1),
            ["blocked_orgs"] = List("blocked_orgs", 1),
                ["vendor_ids"] = List("vendor_ids", 1),
            ["scope_member_ids"] = List("scope_member_ids", 1),
        },
    };

    /// <summary>One bound list: its declared width, and the rows the principal holds.</summary>
    private static ContextRelation List(string name, int width, params int[][] rows) => new()
    {
        Columns = [.. Enumerable.Range(0, width).Select(i => $"{name}_{i}")],
        Rows = [.. rows.Select(r => (IReadOnlyList<object?>)[.. r.Select(v => (object?)v)])],
        ColumnTypes = [.. Enumerable.Range(0, width).Select(_ => ChalkType.Int32())],
    };

    /// <summary>A one-column list of every value from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static ContextRelation Range(string name, int from, int to) =>
        List(name, 1, [.. Enumerable.Range(from, to - from + 1).Select(v => new[] { v })]);
}
