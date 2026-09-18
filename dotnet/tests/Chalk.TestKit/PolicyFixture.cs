using Chalk.Sources.Poco;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.TestKit;

/// <summary>
/// The battery's fixture — <c>corpus/policy/README.md</c> §3 step 1 — as POCO collections.
/// </summary>
/// <remarks>
/// <para>
/// Six tables with fixed columns, declared as records and filled from
/// <see cref="PolicyFixture.Members"/> and its siblings, which are the rows written out. That is
/// deliberate: the one thing this has to do is fail loudly when the battery and the code stop
/// agreeing, and a record with the wrong number of fields does, on the line that reads it.
/// </para>
/// <para>
/// It shares nothing with <see cref="TenancyFixture"/>: that one is design §8's own six-row fixture,
/// this one is the battery's ten-member fixture, and the two are different fixtures for different
/// runs.
/// </para>
/// </remarks>
public sealed partial class PolicyFixture
{
    public const string ContextId = "policy";

    public const long Epoch = 1;

    private PolicyFixture(PocoSource source)
    {
        Source = source;
        Catalog = new CatalogContext
        {
            ContextId = ContextId,
            Epoch = Epoch,
            Schemas = [source.DescribeSchema()],
        };
    }

    public PocoSource Source { get; }

    public CatalogContext Catalog { get; }

    // ---------------------------------------------------------------- the rows

    public sealed record Org(int Id, string Name);

    public sealed record Member(
        int Id,
        int OrgId,
        string FirstName,
        string LastName,
        DateOnly Dob,
        string NationalId,
        string Postcode);

    public sealed record Order(
        int Id,
        int MemberId,
        int OrgId,
        DateOnly PlacedAt,
        string? Note,
        decimal Amount,
        string Status,
        int CreatedBy);

    public sealed record Note(int Id, int OrgId, int CreatedBy, string Body);

    public sealed record Invite(int Id, int OrgId, string Target);

    public sealed record Symbol(string Code, string Label, int SortOrder);

    /// <summary>A vendor: the second tenancy kind, held on its own row (D265 §8, group U).</summary>
    public sealed record Vendor(int Id, string Name);

    /// <summary>A catalogue item: the <b>endpoint</b>, where the vendor's key lives.</summary>
    public sealed record Item(int Id, int VendorId, string Name);

    /// <summary>An order line: the <b>bridge</b>, which contributes existence and nothing else.</summary>
    public sealed record OrderItem(
        int Id, int OrderId, int ItemId, int Quantity, long UnitPrice);

    /// <summary>
    /// Where a column sits in its table, which is what an entitlement's ordinal means. The names are
    /// <see cref="PolicyColumns"/>'s, so this and the descriptors cannot drift apart.
    /// </summary>
    public static int ColumnIndex(string table, string column)
    {
        if (!PolicyColumns.Layouts.TryGetValue(table, out var columns))
        {
            throw new InvalidOperationException(
                $"the battery names the table '{table}', which this fixture does not declare.");
        }

        var at = columns.ToList().IndexOf(column);
        if (at < 0)
        {
            throw new InvalidOperationException(
                $"the battery names {table}.{column}, which this fixture does not declare. Its "
                + $"columns are {string.Join(", ", columns)}.");
        }

        return at;
    }

    // ---------------------------------------------------------------- the catalog

    /// <summary>
    /// The fixture with one entitlement per table, as a case's <see cref="PolicyCatalog"/> names
    /// them. A name this does not know is a name a case declares and nothing registers, which is a
    /// finding.
    /// </summary>
    public static PolicyFixture Create(PolicyCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var builder = new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase);

        builder.AddTable("orgs", Orgs, t => t.OrderedBy(o => o.Id).UniqueKey(o => o.Id));
        builder.AddTable("members", Members, t =>
        {
            t.OrderedBy(m => m.Id).UniqueKey(m => m.Id).ForeignKey(m => m.OrgId)
                .References<Org>(o => o.Id, verify: true);
            Entitle(t, catalog.Members);
        });
        builder.AddTable("orders", Orders, t =>
        {
            t.OrderedBy(o => o.Id).UniqueKey(o => o.Id).ForeignKey(o => o.MemberId)
                .References<Member>(m => m.Id, verify: true);
            Entitle(t, catalog.Orders);
        });
        builder.AddTable("notes", Notes, t =>
        {
            t.OrderedBy(n => n.Id).UniqueKey(n => n.Id);
            Entitle(t, catalog.Notes);
        });
        builder.AddTable("invites", Invites, t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id);
            Entitle(t, catalog.Invites);
        });
        builder.AddTable("symbols", Symbols, t =>
        {
            t.OrderedBy(s => s.SortOrder).UniqueKey(s => s.Code);
            Entitle(t, catalog.Symbols);
        });

        // D265 clause (h)'s three, which every catalog carries: a path's registration resolves the
        // whole route and refuses a step to a table the catalog does not hold (design 38 §3), so
        // the tables are here whether or not the case's `orders` declares the path. No case before
        // 201 reads one of them, and a table nothing reads costs nothing.
        builder.AddTable("vendors", Vendors, t =>
        {
            t.OrderedBy(v => v.Id).UniqueKey(v => v.Id);
            Entitle(t, catalog.Vendors);
        });
        builder.AddTable("items", Items, t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id).ForeignKey(i => i.VendorId)
                .References<Vendor>(v => v.Id, verify: true);
            Entitle(t, catalog.Items);
        });
        builder.AddTable("order_items", OrderItems, t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id).ForeignKey(i => i.OrderId)
                .References<Order>(o => o.Id, verify: true)
                .ForeignKey(i => i.ItemId).References<Item>(x => x.Id, verify: true);
            Entitle(t, catalog.OrderItems);
        });

        return new PolicyFixture(builder.Build());
    }

    private static void Entitle<T>(PocoTableBuilder<T> table, string entitlement)
        where T : class
    {
        if (PolicyEntitlements.Find(entitlement) is { } declared)
        {
            table.Entitlement(declared.Descriptor);
        }
    }

    /// <summary>The client-bodied function the fixture declares (README §3 step 1).</summary>
    public static void RegisterFunctions(Chalk.Client.IFunctionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddScalar<int, bool>("is_vip", static id => id is 1 or 5 or 8);
    }
}
