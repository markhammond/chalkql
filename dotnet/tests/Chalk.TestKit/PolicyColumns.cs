namespace Chalk.TestKit;

/// <summary>
/// Where each column of the battery's fixture sits in its table, which is what an entitlement's
/// ordinal means (<c>docs/design/16-entitlements.md</c> §1).
/// </summary>
/// <remarks>
/// A descriptor addresses a column by its position, and a position written as a number is a fact
/// nothing checks. These are the positions, named, and <see cref="PolicyFixture"/> derives its own
/// layout from them — so a column that moves moves in one place and every entitlement that names it
/// follows, and a column that goes away stops the build.
/// </remarks>
public static class PolicyColumns
{
    /// <summary>The three organisations.</summary>
    public static class Orgs
    {
        public const int Id = 0;
        public const int Name = 1;

        /// <summary>The table's columns in order, which is what the ordinals index.</summary>
        public static IReadOnlyList<string> Layout { get; } = ["id", "name"];
    }

    /// <summary>The ten members: the pii and restricted realms of design §8.</summary>
    public static class Members
    {
        public const int Id = 0;
        public const int OrgId = 1;
        public const int FirstName = 2;
        public const int LastName = 3;
        public const int Dob = 4;
        public const int NationalId = 5;
        public const int Postcode = 6;

        /// <summary>The table's columns in order, which is what the ordinals index.</summary>
        public static IReadOnlyList<string> Layout { get; } =
            ["id", "org_id", "first_name", "last_name", "dob", "national_id", "postcode"];
    }

    /// <summary>The sixteen orders: the population-only <c>amount</c> and the created-by column.</summary>
    public static class Orders
    {
        public const int Id = 0;
        public const int MemberId = 1;
        public const int OrgId = 2;
        public const int PlacedAt = 3;
        public const int Note = 4;
        public const int Amount = 5;
        public const int Status = 6;
        public const int CreatedBy = 7;

        /// <summary>The table's columns in order, which is what the ordinals index.</summary>
        public static IReadOnlyList<string> Layout { get; } =
            ["id", "member_id", "org_id", "placed_at", "note", "amount", "status", "created_by"];
    }

    /// <summary>The six notes, which carry the resource-owner fail-safe (D206).</summary>
    public static class Notes
    {
        public const int Id = 0;
        public const int OrgId = 1;
        public const int CreatedBy = 2;
        public const int Body = 3;

        /// <summary>The table's columns in order, which is what the ordinals index.</summary>
        public static IReadOnlyList<string> Layout { get; } = ["id", "org_id", "created_by", "body"];
    }

    /// <summary>The four invites: a row predicate and no column rules at all.</summary>
    public static class Invites
    {
        public const int Id = 0;
        public const int OrgId = 1;
        public const int Target = 2;

        /// <summary>The table's columns in order, which is what the ordinals index.</summary>
        public static IReadOnlyList<string> Layout { get; } = ["id", "org_id", "target"];
    }

    /// <summary>The four symbols: reference data carrying no entitlement (design §8's query 15).</summary>
    public static class Symbols
    {
        public const int Code = 0;
        public const int Label = 1;
        public const int SortOrder = 2;

        /// <summary>The table's columns in order, which is what the ordinals index.</summary>
        public static IReadOnlyList<string> Layout { get; } = ["code", "label", "sort_order"];
    }


    /// <summary>The two vendors: the second tenancy kind, held on the row (D265 §8).</summary>
    public static class Vendors
    {
        public const int Id = 0;
        public const int Name = 1;

        /// <summary>The table's columns in order, which is what the ordinals index.</summary>
        public static IReadOnlyList<string> Layout { get; } = ["id", "name"];
    }

    /// <summary>The three catalogue items: the <b>endpoint</b>, where the vendor key lives.</summary>
    public static class Items
    {
        public const int Id = 0;
        public const int VendorId = 1;
        public const int Name = 2;

        /// <summary>The table's columns in order, which is what the ordinals index.</summary>
        public static IReadOnlyList<string> Layout { get; } = ["id", "vendor_id", "name"];
    }

    /// <summary>The five order lines: the <b>bridge</b>, entitled by kind (D265 §8).</summary>
    public static class OrderItems
    {
        public const int Id = 0;
        public const int OrderId = 1;
        public const int ItemId = 2;
        public const int Quantity = 3;
        public const int UnitPrice = 4;

        /// <summary>The table's columns in order, which is what the ordinals index.</summary>
        public static IReadOnlyList<string> Layout { get; } =
            ["id", "order_id", "item_id", "quantity", "unit_price"];
    }

    /// <summary>Every table's layout, by the name the statements use.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Layouts { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["orgs"] = Orgs.Layout,
            ["members"] = Members.Layout,
            ["orders"] = Orders.Layout,
            ["notes"] = Notes.Layout,
            ["invites"] = Invites.Layout,
            ["symbols"] = Symbols.Layout,
            ["vendors"] = Vendors.Layout,
            ["items"] = Items.Layout,
            ["order_items"] = OrderItems.Layout,
        };
}
