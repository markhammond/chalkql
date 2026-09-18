using Chalk.Catalog;
using Chalk.Sources;
using Chalk.Sources.Poco;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.TestKit;

/// <summary>
/// The catalog the whole corpus is planned and executed against. It must match the planner's Java
/// twin (<c>chalk.planner.TestCatalogs</c>) column for column and statistic for statistic, because
/// the Java golden plans and the .NET recorded digests are compared to each other.
/// </summary>
public sealed class CorpusFixture
{
    /// <summary>The context id both sides use.</summary>
    public const string ContextId = "corpus";

    /// <summary>The catalog epoch both sides use.</summary>
    public const long Epoch = 1;

    private CorpusFixture(
        PocoSource source,
        IReadOnlyList<Bar> bars,
        IReadOnlyList<Bar> barsSmall,
        IReadOnlyList<SymbolRow> symbols,
        IReadOnlyList<Trade> sparseTrades,
        IReadOnlyList<LineItem> lineItems,
        IReadOnlyList<Funding> funding,
        IReadOnlyList<MarketEvent> events,
        IReadOnlyList<Customer> customers,
        IReadOnlyList<Order> orders,
        IReadOnlyList<Nation> nations,
        IReadOnlyList<Region> regions,
        IReadOnlyList<Supplier> suppliers,
        IReadOnlyList<Sale> sales,
        IReadOnlyList<SortedRow> sortedRows)
    {
        Source = source;
        Bars = bars;
        BarsSmall = barsSmall;
        SymbolRows = symbols;
        SparseTrades = sparseTrades;
        LineItems = lineItems;
        Funding = funding;
        Events = events;
        Customers = customers;
        Orders = orders;
        Nations = nations;
        Regions = regions;
        Suppliers = suppliers;
        Sales = sales;
        SortedRows = sortedRows;
        Catalog = new CatalogContext
        {
            ContextId = ContextId,
            Epoch = Epoch,
            Schemas = [source.DescribeSchema()],
        };
        CatalogValidator.Validate(Catalog);
    }

    /// <summary>
    /// The full-size fixture, built once per process. Generating 100 800 bars and 60 000 line items
    /// and verifying their declared collations takes long enough that every test class having its own
    /// is a waste; nothing mutates it.
    /// </summary>
    public static CorpusFixture Shared { get; } = Create();

    /// <summary>Builds the corpus fixture. Verification (D17) runs, so a bad generator fails here.</summary>
    public static CorpusFixture Create(int seed = Fixtures.DefaultSeed, int barMinutes = Fixtures.BarMinutes,
        int lineItemRows = Fixtures.LineItemRows)
    {
        var bars = Fixtures.Bars(seed, barMinutes);
        var barsSmall = Fixtures.Bars(seed, Math.Min(barMinutes, Fixtures.BarsSmallMinutes));
        var symbols = Fixtures.SymbolRows();
        var sparseTrades = Fixtures.SparseTrades(seed);
        var lineItems = Fixtures.LineItems(seed, lineItemRows);
        var funding = Fixtures.FundingRates(seed);
        var events = Fixtures.Events();
        var customers = Fixtures.Customers(seed);
        var orders = Fixtures.Orders(seed);
        var nations = Fixtures.Nations();
        var regions = Fixtures.Regions();
        var suppliers = Fixtures.Suppliers(seed);
        
        var sales = Fixtures.Sales();
        var sortedRows = Fixtures.SortedRows();

        const bool skipIntegrityCheckForTestFixtures = true;
        const bool checkFk = !skipIntegrityCheckForTestFixtures;
        
        var source = CorpusFunctions.Declare(new PocoSourceBuilder("mem"))
            // bars and symbols use snake_case so the naming policy is exercised; lineitem names its
            // columns explicitly because TPC-H's l_ prefix is not a naming policy.
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("bars", bars, t => t
                .OrderedBy(b => b.Ts)
                .ThenBy(b => b.Symbol)
                .UniqueKey(b => b.Ts, b => b.Symbol)
                // M2 (D40). The (symbol, ts) index is a permutation — one int per row — and the
                // (ts, symbol) one is the declared collation, so it costs nothing at all (§1).
                .UniqueIndex(b => b.Symbol, b => b.Ts)
                .Index(b => b.Ts, b => b.Symbol)
                // F14: every bar names one of the five `symbols` rows. The fluent spelling of F16,
                // which finds `symbols` by row type.
                .ForeignKey(b => b.Symbol).References<SymbolRow>(s2 => s2.Symbol, verify: checkFk))
            .AddTable("bars_small", barsSmall, t => t
                .OrderedBy(b => b.Ts)
                .ThenBy(b => b.Symbol)
                .UniqueKey(b => b.Ts, b => b.Symbol)
                .UniqueIndex(b => b.Symbol, b => b.Ts)
                // The same declaration as `bars`, so a plan over either has the same shape (D53).
                .ForeignKey(b => b.Symbol).References<SymbolRow>(s2 => s2.Symbol, verify: checkFk))

            // D257: `bars_small` again, with the (symbol, ts) index clustered and covering exactly
            // what the moving average projects. The pair is the corpus's comparison — 02 takes the
            // permutation and gathers, 02b takes the copy and streams — so the two tables differ in
            // the index and in nothing else.
            .AddTable("bars_clustered", barsSmall, t => t
                .OrderedBy(b => b.Ts)
                .ThenBy(b => b.Symbol)
                .UniqueKey(b => b.Ts, b => b.Symbol)
                .UniqueClusteredIndex(b => b.Symbol, b => b.Ts)
                    .Covering(b => b.Close)
                .ForeignKey(b => b.Symbol).References<SymbolRow>(s2 => s2.Symbol, verify: checkFk))
            .AddTable("symbols", symbols, t => t
                .OrderedBy(s => s.Symbol)
                .UniqueKey(s => s.Symbol))
            // D55: (symbol, ts) is exactly the order a session window requires, so corpus query 02
            // plans with no Sort.
            .AddTable("trades_sparse", sparseTrades, t => t
                .OrderedBy(t2 => t2.Symbol)
                .ThenBy(t2 => t2.Ts))
            .AddTable("lineitem", lineItems, t => t
                .Column(i => i.OrderKey, "l_orderkey")
                .Column(i => i.LineNumber, "l_linenumber")
                .Column(i => i.PartKey, "l_partkey")
                .Column(i => i.SuppKey, "l_suppkey")
                .Column(i => i.Quantity, "l_quantity")
                .Column(i => i.ExtendedPrice, "l_extendedprice")
                .Column(i => i.Discount, "l_discount")
                .Column(i => i.Tax, "l_tax")
                .Column(i => i.ReturnFlag, "l_returnflag")
                .Column(i => i.LineStatus, "l_linestatus")
                .Column(i => i.ShipDate, "l_shipdate")
                .Column(i => i.CommitDate, "l_commitdate")
                .Column(i => i.ReceiptDate, "l_receiptdate")
                .Column(i => i.ShipInstruct, "l_shipinstruct")
                .Column(i => i.ShipMode, "l_shipmode")
                .Column(i => i.Comment, "l_comment")
                .OrderedBy(i => i.OrderKey)
                .ThenBy(i => i.LineNumber)
                .UniqueKey(i => i.OrderKey, i => i.LineNumber)
                // A non-unique index over struct rows, collation-backed; and one that is not.
                .Index(i => i.OrderKey)
                .Index(i => i.ShipDate)
                .ForeignKey(i => i.OrderKey).References<Order>(o => o.OrderKey, verify: checkFk))

            // The join fixtures (D46). `funding` is the ASOF right side: collated by (symbol, ts),
            // which is exactly the order the operator needs and therefore does not have to make.
            .AddTable("funding", funding, t => t
                .OrderedBy(f => f.Symbol)
                .ThenBy(f => f.Ts)
                .UniqueKey(f => f.Symbol, f => f.Ts)
                // Every funding row's symbol is one of the five `symbols` rows.
                .ForeignKey(f => f.Symbol).References<SymbolRow>(s2 => s2.Symbol, verify: checkFk))
            .AddTable("events", events, t => t
                .Column(e => e.Id, "id")
                .OrderedBy(e => e.Id)
                .UniqueKey(e => e.Id))
            .AddTable("customer", customers, t => t
                .Column(c => c.CustKey, "c_custkey")
                .Column(c => c.Name, "c_name")
                .Column(c => c.Address, "c_address")
                .Column(c => c.NationKey, "c_nationkey")
                .Column(c => c.Phone, "c_phone")
                .Column(c => c.AcctBal, "c_acctbal")
                .Column(c => c.MktSegment, "c_mktsegment")
                .Column(c => c.Comment, "c_comment")
                .OrderedBy(c => c.CustKey)
                .UniqueKey(c => c.CustKey)
                .Index(c => c.NationKey)
                // TPC-H referential constraint
                .ForeignKey(c => c.NationKey).References<Nation>(n => n.NationKey, verify: checkFk))
            .AddTable("orders", orders, t => t
                .Column(o => o.OrderKey, "o_orderkey")
                .Column(o => o.CustKey, "o_custkey")
                .Column(o => o.OrderStatus, "o_orderstatus")
                .Column(o => o.TotalPrice, "o_totalprice")
                .Column(o => o.OrderDate, "o_orderdate")
                .Column(o => o.OrderPriority, "o_orderpriority")
                .Column(o => o.Clerk, "o_clerk")
                .Column(o => o.ShipPriority, "o_shippriority")
                .Column(o => o.Comment, "o_comment")
                .OrderedBy(o => o.OrderKey)
                .UniqueKey(o => o.OrderKey)
                .Index(o => o.CustKey)
                // TPC-H referential constraint which bounds the join.
                .ForeignKey(o => o.CustKey).References<Customer>(c => c.CustKey, verify: checkFk))
            .AddTable("nation", nations, t => t
                .Column(n => n.NationKey, "n_nationkey")
                .Column(n => n.Name, "n_name")
                .Column(n => n.RegionKey, "n_regionkey")
                .Column(n => n.Comment, "n_comment")
                .OrderedBy(n => n.NationKey)
                .UniqueKey(n => n.NationKey)
                // Associate nation → region.
                .ForeignKey(n => n.RegionKey).References<Region>(r => r.RegionKey, verify: checkFk))
            .AddTable("region", regions, t => t
                .Column(r => r.RegionKey, "r_regionkey")
                .Column(r => r.Name, "r_name")
                .Column(r => r.Comment, "r_comment")
                .OrderedBy(r => r.RegionKey)
                .UniqueKey(r => r.RegionKey))
            .AddTable("supplier", suppliers, t => t
                .Column(s => s.SuppKey, "s_suppkey")
                .Column(s => s.Name, "s_name")
                .Column(s => s.Address, "s_address")
                .Column(s => s.NationKey, "s_nationkey")
                .Column(s => s.Phone, "s_phone")
                .Column(s => s.AcctBal, "s_acctbal")
                .Column(s => s.Comment, "s_comment")
                .OrderedBy(s => s.SuppKey)
                .UniqueKey(s => s.SuppKey)
                .Index(s => s.NationKey)
                // Associate supplier → nation.
                .ForeignKey(s => s.NationKey).References<Nation>(n => n.NationKey, verify: checkFk))

            // Two adversarial tables:
            // `sales` declares nothing at all and is intended for the case where there is no key to fall back on.
            .AddTable("sales", sales)
            // `sorted` is ordered without a unique key, inducing use of merge joins and merge unions.
            .AddTable("sorted", sortedRows, t => t.OrderedBy(r => r.K))
            .Build();

        return new CorpusFixture(
            source, bars, barsSmall, symbols, sparseTrades, lineItems, funding, events, customers,
            orders, nations, regions, suppliers, sales, sortedRows);
    }

    public PocoSource Source { get; }

    public CatalogContext Catalog { get; }

    public IReadOnlyList<Bar> Bars { get; }

    /// <summary>The window corpus's table (D53): the same generator at 1 000 minutes per symbol.</summary>
    public IReadOnlyList<Bar> BarsSmall { get; }

    public IReadOnlyList<SymbolRow> SymbolRows { get; }

    /// <summary>The irregular trade series the SESSION corpus runs on (D55).</summary>
    public IReadOnlyList<Trade> SparseTrades { get; }

    public IReadOnlyList<LineItem> LineItems { get; }

    public IReadOnlyList<Funding> Funding { get; }

    public IReadOnlyList<MarketEvent> Events { get; }

    public IReadOnlyList<Customer> Customers { get; }

    public IReadOnlyList<Order> Orders { get; }

    public IReadOnlyList<Nation> Nations { get; }

    public IReadOnlyList<Region> Regions { get; }

    public IReadOnlyList<Supplier> Suppliers { get; }

    /// <summary>The graft's six-row adversarial table (D164): ties, a NULL, two partitions.</summary>
    public IReadOnlyList<Sale> Sales { get; }

    /// <summary>The graft's four-row ordered table (D164): a duplicate key and a gap.</summary>
    public IReadOnlyList<SortedRow> SortedRows { get; }

    /// <summary>The one source, keyed the way the engine wants it.</summary>
    public IReadOnlyList<ISourceRuntime> Sources => [Source];

    /// <summary>
    /// The catalog as the wire message. The corpus tool writes this to <c>corpus/schemas/</c> so the
    /// planner's Java tests register exactly what the client pushes (05-testing.md §4).
    /// </summary>
    public Chalk.Ir.CatalogContext CatalogMessage() => CatalogSerialization.ToProto(Catalog);
}
