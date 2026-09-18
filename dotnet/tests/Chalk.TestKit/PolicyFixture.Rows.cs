namespace Chalk.TestKit;

/// <summary>
/// The battery's fixture, row by row (<c>corpus/policy/README.md</c> §3 step 1): three
/// organisations, ten members, sixteen orders, six notes, four invites and four symbols.
/// </summary>
/// <remarks>
/// Written out rather than loaded, so a column that moves is a compiler error rather than a run of
/// quiet nonsense. It shares nothing with <see cref="TenancyFixture"/>: that is design §8's own
/// six-row fixture, this is the battery's.
/// </remarks>
public sealed partial class PolicyFixture
{
    /// <summary>The fixture's <c>orgs</c>.</summary>
    public static IReadOnlyList<Org> Orgs { get; } =
    [
        new(1, "Northwind"),
        new(2, "Eastgate"),
        new(3, "Southbank"),
    ];

    /// <summary>The fixture's <c>members</c>.</summary>
    public static IReadOnlyList<Member> Members { get; } =
    [
        new(1, 1, "Tomas", "Kalny", new DateOnly(1980, 3, 14), "NID-1001", "2000"),
        new(2, 1, "Rina", "Osei", new DateOnly(1991, 7, 2), "NID-1002", "2000"),
        new(3, 1, "Petra", "Vance", new DateOnly(1975, 11, 30), "NID-1003", "3050"),
        new(4, 1, "Tao", "Lindqvist", new DateOnly(1988, 1, 9), "NID-1004", "2000"),
        new(5, 2, "Tessa", "Moreau", new DateOnly(1993, 5, 21), "NID-1005", "4101"),
        new(6, 2, "Ulf", "Berg", new DateOnly(1969, 9, 17), "NID-1006", "4101"),
        new(7, 2, "Nadia", "Haddad", new DateOnly(1985, 2, 28), "NID-1007", "5000"),
        new(8, 3, "Tao", "Elmi", new DateOnly(1979, 12, 5), "NID-1008", "6060"),
        new(9, 3, "Mira", "Kovac", new DateOnly(1997, 4, 11), "NID-1009", "6060"),
        new(10, 1, "Rina", "Sandoval", new DateOnly(1990, 6, 6), "NID-1010", "3050"),
    ];

    /// <summary>The fixture's <c>orders</c>.</summary>
    public static IReadOnlyList<Order> Orders { get; } =
    [
        new(101, 1, 1, new DateOnly(2026, 1, 5), "first order", 100.00m, "DONE", 1),
        new(102, 1, 1, new DateOnly(2026, 1, 7), "repeat", 200.00m, "DONE", 1),
        new(103, 2, 1, new DateOnly(2026, 1, 9), "gift", 150.00m, "OPEN", 2),
        new(104, 2, 1, new DateOnly(2026, 1, 12), "bulk", 250.00m, "OPEN", 1),
        new(105, 1, 1, new DateOnly(2026, 1, 15), "sample", 50.00m, "HOLD", 3),
        new(106, 3, 1, new DateOnly(2026, 1, 19), "renewal", 300.00m, "NEW", 95),
        new(107, 4, 1, new DateOnly(2026, 1, 21), "urgent", 100.00m, "OPEN", 2),
        new(108, 4, 1, new DateOnly(2026, 1, 27), "return", 250.00m, "DONE", 2),
        new(109, 5, 2, new DateOnly(2026, 2, 2), "setup", 450.00m, "DONE", 5),
        new(110, 5, 2, new DateOnly(2026, 2, 5), "addon", 150.00m, "OPEN", 5),
        new(111, 6, 2, new DateOnly(2026, 2, 8), null, 200.00m, "NEW", 6),
        new(112, 6, 2, new DateOnly(2026, 2, 11), "renewal", 100.00m, "HOLD", 95),
        new(113, 7, 2, new DateOnly(2026, 2, 14), "bulk", 500.00m, "OPEN", 6),
        new(114, 7, 2, new DateOnly(2026, 2, 18), "urgent", 400.00m, "DONE", 5),
        new(115, 8, 3, new DateOnly(2026, 3, 3), "first order", 700.00m, "OPEN", 1),
        new(116, 9, 3, new DateOnly(2026, 3, 9), "audit fee", 900.00m, "DONE", 9),
    ];

    /// <summary>The fixture's <c>notes</c>.</summary>
    public static IReadOnlyList<Note> Notes { get; } =
    [
        new(201, 1, 1, "onboarding checklist"),
        new(202, 1, 2, "call summary"),
        new(203, 1, 95, "draft policy"),
        new(204, 1, 95, "follow-up"),
        new(205, 2, 5, "renewal terms"),
        new(206, 3, 9, "audit trail"),
    ];

    /// <summary>The fixture's <c>invites</c>.</summary>
    public static IReadOnlyList<Invite> Invites { get; } =
    [
        new(301, 1, "ext-1@example.org"),
        new(302, 1, "ext-2@example.org"),
        new(303, 2, "ext-3@example.org"),
        new(304, 3, "ext-4@example.org"),
    ];

    /// <summary>The fixture's <c>symbols</c>.</summary>
    public static IReadOnlyList<Symbol> Symbols { get; } =
    [
        new("NEW", "New", 1),
        new("OPEN", "Open", 2),
        new("HOLD", "On hold", 3),
        new("DONE", "Done", 4),
    ];


    /// <summary>
    /// The fixture's <c>vendors</c> (D265 clause (h), group U): the second tenancy kind, whose key
    /// is the row's own identifier.
    /// </summary>
    public static IReadOnlyList<Vendor> Vendors { get; } =
    [
        new(1, "Ashgrove"),
        new(2, "Brightsea"),
    ];

    /// <summary>The fixture's <c>items</c>: the endpoint, two of V1's and one of V2's.</summary>
    public static IReadOnlyList<Item> Items { get; } =
    [
        new(1, 1, "widget"),
        new(2, 1, "gadget"),
        new(3, 2, "sprocket"),
    ];

    /// <summary>
    /// The fixture's <c>order_items</c>: the bridge, chosen so that every claim group U makes is a
    /// row rather than a coincidence.
    /// </summary>
    /// <remarks>
    /// Order 101 carries a line of each vendor, so both reach it and a manager in organisation 1
    /// meets it from both sides. Order 109 is in organisation 2 and 115 in organisation 3, and V1
    /// reaches both — which is the assertion that a row becomes visible through the rows that
    /// belong to it and by no other route. Every other order carries no line at all, so a vendor
    /// reaches it by no path: a row related to zero tenants is invisible along that axis.
    /// </remarks>
    public static IReadOnlyList<OrderItem> OrderItems { get; } =
    [
        new(1, 101, 1, 2, 50),
        new(2, 101, 3, 4, 30),
        new(3, 103, 3, 1, 200),
        new(4, 109, 2, 3, 70),
        new(5, 115, 1, 1, 90),
    ];
}
