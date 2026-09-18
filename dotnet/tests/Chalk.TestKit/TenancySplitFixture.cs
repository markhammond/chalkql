using Chalk.Catalog;
using Chalk.Sources.Poco;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.TestKit;

/// <summary>
/// Design 38 §8's <b>two-source</b> layout (F84): the tenancy fixture with <c>vendors</c> and
/// <c>items</c> in a source of their own, so that the path an order holds its vendor along crosses a
/// source at its last step.
/// </summary>
/// <remarks>
/// <para>
/// The same descriptors as <see cref="TenancyFixture"/> — they are read from it rather than repeated
/// — and the same principals, so a difference between the two fixtures' answers is a difference the
/// layout introduced and nothing else. That is what makes the family built on it a differential
/// rather than a second set of expectations to keep in step, and it is why the oracle and D252's
/// detector can be the co-located family's own.
/// </para>
/// <para>
/// Two things follow from the layout and nothing else does. <c>order_items</c> declares no foreign
/// key to <c>items</c>, because a foreign key names a table of its own schema; what stands in its
/// place is the <b>association</b> the catalog carries, which is what a step across two sources
/// resolves through (<c>docs/design/45-typed-tenancy-surface.md</c> §3, D270 (c)). And the path's
/// last step and its endpoint name the other schema, which is the one thing a descriptor has to say
/// about where a table lives.
/// </para>
/// <para>
/// A statement naming a table of the second source qualifies it, because the first schema is the
/// default one and the rest are addressed <c>schema.table</c> (A4,
/// <c>docs/design/03-planner.md</c> §2). <see cref="Qualified"/> is that adaptation and nothing
/// else: it puts <c>catalogue.</c> in front of the two table names this layout moved, and leaves
/// every other word of the statement alone.
/// </para>
/// </remarks>
public sealed partial class TenancySplitFixture
{
    private TenancySplitFixture(PocoSource main, PocoSource marketplace)
    {
        Main = main;
        Marketplace = marketplace;
        Catalog = new CatalogContext
        {
            ContextId = TenancyFixture.ContextId,
            Epoch = TenancyFixture.Epoch,
            Schemas = [main.DescribeSchema(), marketplace.DescribeSchema()],
            Associations = Association,
        };
        CatalogValidator.Validate(Catalog);
    }

    /// <summary>The target, the bridge and everything else — the default schema a statement names.</summary>
    public PocoSource Main { get; }

    /// <summary>The endpoint and the table its kind is held on, one source away.</summary>
    public PocoSource Marketplace { get; }

    public IReadOnlyList<PocoSource> Sources => [Main, Marketplace];

    public CatalogContext Catalog { get; }

    /// <summary>
    /// The declaration the step from the line to the item resolves through, which the host puts into
    /// the catalog it registers (D270 (c)). Neither source owns it: an association's two ends are in
    /// two schemas and neither of them described the other's table.
    /// </summary>
    public static IReadOnlyList<AssociationDescriptor> Association { get; } =
    [
        new AssociationDescriptor
        {
            Name = "line_item",
            FromSchema = "main",
            FromTable = "order_items",
            FromColumn = "item_id",
            ToSchema = TenancyFixture.MarketplaceSchema,
            ToTable = "items",
            ToColumn = "id",
        },
    ];

    public static TenancySplitFixture Shared => LazyShared.Value;

    private static readonly Lazy<TenancySplitFixture> LazyShared = new(() => Create());

    public static TenancySplitFixture Create() =>
        new(TenancyFixture.SplitSource(), TenancyFixture.MarketplaceSource());

    /// <summary>
    /// The oracle's own two sources: exactly the rows and values this principal is entitled to,
    /// carrying no entitlement at all, in the same layout — so the statement a family runs over the
    /// fixture resolves over the oracle too.
    /// </summary>
    public static IReadOnlyList<PocoSource> Oracle(Chalk.Client.RequestContext context) =>
    [
        TenancyOracle.Disclose(context, split: true),
        TenancyOracle.DiscloseMarketplace(context),
    ];

    /// <summary>
    /// A statement written against the co-located layout, addressed to this one: the two tables that
    /// moved are qualified with their schema and nothing else changes (A4).
    /// </summary>
    /// <remarks>
    /// Word-boundary and never a substring: <c>order_items</c> holds <c>items</c> and must not be
    /// touched, and neither must <c>item_id</c>. The qualification is what a host would write over
    /// this layout, and it is the only difference between the statement this family runs and the one
    /// the co-located family runs.
    /// </remarks>
    public static string Qualified(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return Marketplaced().Replace(
            sql, m => TenancyFixture.MarketplaceSchema + "." + m.Value);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\w.])(?:items|vendors)\b")]
    private static partial System.Text.RegularExpressions.Regex Marketplaced();
}
