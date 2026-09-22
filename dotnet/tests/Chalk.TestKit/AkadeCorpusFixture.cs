using Akade.IndexedSet;
using Chalk.Catalog;
using Chalk.Sample.AkadeIndexedSet;
using Chalk.Sources;
using Chalk.Sources.Poco;
using CatalogContext = Chalk.Catalog.CatalogContext;
using IndexKind = Chalk.Ir.IndexKind;

namespace Chalk.TestKit;

/// <summary>
/// The <c>bars-akade</c> configuration (D35, D40): the same corpus data, with <c>bars</c>'s two
/// indexes supplied by <c>samples/Chalk.Sample.AkadeIndexedSet</c> and its rows registered as an
/// <see cref="IReadOnlyCollection{T}"/> — an <c>IndexedSet&lt;Bar&gt;</c> implements no collection
/// interface at all, which is the case that overload exists for.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes D35's extension point <em>proven</em> rather than asserted: the whole corpus
/// runs a second time with an index Chalk did not build, against a source it cannot index into, and
/// every answer still has to match the reference executor.
/// </para>
/// <para>
/// The plans differ from the built-in configuration's, and are meant to. Without an
/// <c>IReadOnlyList&lt;T&gt;</c> there is no position order to declare a collation over, so
/// <c>ORDER BY ts</c> costs a sort here where it costs nothing there. Results are the contract;
/// plans are not.
/// </para>
/// </remarks>
public sealed class AkadeCorpusFixture
{
    private AkadeCorpusFixture(
        PocoSource source,
        IndexedSet<Bar> bars,
        IReadOnlyList<Bar> rows,
        IReadOnlyList<SymbolRow> symbols,
        IReadOnlyList<Trade> sparseTrades,
        IReadOnlyList<LineItem> lineItems,
        IReadOnlyList<SearchRow> searchRows)
    {
        Source = source;
        SearchRows = searchRows;
        Bars = bars;
        BarRows = rows;
        SymbolRows = symbols;
        SparseTrades = sparseTrades;
        LineItems = lineItems;
        Catalog = new CatalogContext
        {
            ContextId = CorpusFixture.ContextId,
            Epoch = CorpusFixture.Epoch,
            Schemas = [source.DescribeSchema()],
        };
        CatalogValidator.Validate(Catalog);
    }

    /// <summary>
    /// Built once per process, like <see cref="CorpusFixture.Shared"/>. Lazy rather than a static
    /// initialiser: <see cref="Create"/> reads the static key accessors below, and field
    /// initialisers run in declaration order.
    /// </summary>
    public static AkadeCorpusFixture Shared => SharedLazy.Value;

    public static AkadeCorpusFixture Create(
        int seed = Fixtures.DefaultSeed,
        int barMinutes = Fixtures.BarMinutes,
        int lineItemRows = Fixtures.LineItemRows)
    {
        var rows = Fixtures.Bars(seed, barMinutes);
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
        var searchRows = Fixtures.SearchRows();
        var searchSet = searchRows.ToIndexedSet()
            .WithPrefixIndex(CorpusFixture.SearchName, indexName: CorpusFixture.SearchAccessor)
            .Build();

        var set = IndexedSetBuilder.Create((IEnumerable<Bar>)rows)
            .WithRangeIndex(SymbolTs, SymbolTsComparer)
            .WithRangeIndex(TsSymbol, TsSymbolComparer)
            .Build();

        var source = CorpusFunctions.Declare(
            new PocoSourceBuilder("mem")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable(
                "bars",
                new AkadeRows<Bar>(set),
                t => t
                    .UniqueKey(b => b.Ts, b => b.Symbol)
                    .Index(SymbolTsIndex(set).Descriptor, rows => SymbolTsIndex(SetOf(rows)))
                    .Index(TsSymbolIndex(set).Descriptor, rows => TsSymbolIndex(SetOf(rows))))
            // The window corpus's table, declared exactly as the built-in configuration declares it:
            // this configuration is about `bars`' indexes and every other table is a control (D53).
            .AddTable("bars_small", barsSmall, t => t
                .OrderedBy(b => b.Ts)
                .ThenBy(b => b.Symbol)
                .UniqueKey(b => b.Ts, b => b.Symbol)
                .UniqueIndex(b => b.Symbol, b => b.Ts))
            // D257's table, declared as the built-in configuration declares it, for the same reason
            // `bars_small` is: this configuration is about `bars`' indexes and every other table is
            // a control.
            .AddTable("bars_clustered", barsSmall, t => t
                .OrderedBy(b => b.Ts)
                .ThenBy(b => b.Symbol)
                .UniqueKey(b => b.Ts, b => b.Symbol)
                .UniqueClusteredIndex(b => b.Symbol, b => b.Ts)
                    .Covering(b => b.Close))
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
                .Index(i => i.OrderKey)
                .Index(i => i.ShipDate))

            // The join fixtures, declared exactly as the built-in configuration declares them: this
            // configuration is about `bars`' indexes, and every other table is a control.
            .AddTable("funding", funding, t => t
                .OrderedBy(f => f.Symbol)
                .ThenBy(f => f.Ts)
                .UniqueKey(f => f.Symbol, f => f.Ts))
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
                .Index(c => c.NationKey))
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
                .Index(o => o.CustKey))
            .AddTable("nation", nations, t => t
                .Column(n => n.NationKey, "n_nationkey")
                .Column(n => n.Name, "n_name")
                .Column(n => n.RegionKey, "n_regionkey")
                .Column(n => n.Comment, "n_comment")
                .OrderedBy(n => n.NationKey)
                .UniqueKey(n => n.NationKey))
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
                .Index(s => s.NationKey))

            // D282's table, declared exactly as the built-in configuration declares it: this
            // configuration is about `bars`' indexes and every other table is a control.
            .AddTable("terms", searchRows, t => t
                .Index(CorpusFixture.SearchIndex, _ => new AkadePrefixIndex<SearchRow>(
                    CorpusFixture.SearchIndex,
                    searchSet,
                    CorpusFixture.SearchName,
                    CorpusFixture.SearchAccessor)))
            )
            .Build();

        return new AkadeCorpusFixture(source, set, rows, symbols, sparseTrades, lineItems, searchRows);
    }

    public PocoSource Source { get; }

    public CatalogContext Catalog { get; }

    /// <summary>The Akade set behind <c>bars</c>, for the conformance check.</summary>
    public IndexedSet<Bar> Bars { get; }

    /// <summary>The same rows as a list, which is what a full-scan comparison needs.</summary>
    public IReadOnlyList<Bar> BarRows { get; }

    public IReadOnlyList<SymbolRow> SymbolRows { get; }

    /// <summary>The irregular trade series the SESSION corpus runs on (D55).</summary>
    public IReadOnlyList<Trade> SparseTrades { get; }

    public IReadOnlyList<LineItem> LineItems { get; }

    /// <summary>The prefix corpus's rows (D282).</summary>
    public IReadOnlyList<SearchRow> SearchRows { get; }

    public IReadOnlyList<ISourceRuntime> Sources => [Source];

    /// <summary>The two Akade-backed indexes, with the key accessors the conformance kit needs.</summary>
    public IEnumerable<(IPocoIndex<Bar> Index, Func<Bar, object?>[] Keys)> BarIndexes()
    {
        yield return (SymbolTsIndex(Bars), [b => b.Symbol, b => Nanoseconds(b.Ts)]);
        yield return (TsSymbolIndex(Bars), [b => Nanoseconds(b.Ts), b => b.Symbol]);
    }

    /// <summary>TIMESTAMP(9) storage — nanoseconds since the epoch, which is what a bound carries.</summary>
    private static long Nanoseconds(DateTime value) => (value.Ticks - DateTime.UnixEpoch.Ticks) * 100L;

    public static AkadeIndex<Bar, (Utf8String Symbol, long Ts)> SymbolTsIndex(IndexedSet<Bar> set) =>
        new(
            new IndexDescriptor
            {
                Name = "ix_bars_symbol_ts",
                Kind = IndexKind.Ordered,
                Columns = [0, 1],
                Unique = true,
            },
            set,
            SymbolTs,
            "SymbolTs",
            new AkadeKey<Bar, (Utf8String, long)>(
                (bounds, max) => (
                    bounds[0].ToUtf8String(),
                    bounds.Count > 1 ? (long)bounds[1]! : max ? long.MaxValue : long.MinValue)),
            SymbolTsComparer);

    private static readonly Utf8String LowerBound = Utf8String.Empty;
    private static readonly Utf8String UpperBound = "\uFFFF".ToUtf8String();
    
    /// <summary>
    /// The <c>(ts, symbol)</c> index — the same rows, the other key order.</summary>
    /// <remarks>
    /// Converts Chalk logical bound values into this index's native TKey
    /// representation. Bound CLR types are not required to match the CLR types
    /// used by the indexed source; for example SQL STRING may arrive as either
    /// string or Utf8String.
    /// </remarks>
    private static AkadeIndex<Bar, (long Ts, Utf8String Symbol)> TsSymbolIndex(
        IndexedSet<Bar> set) =>
        new(
            new IndexDescriptor
            {
                Name = "ix_bars_ts_symbol",
                Kind = IndexKind.Ordered,
                Columns = [1, 0],
                Unique = false,
            },
            set,
            TsSymbol,
            "TsSymbol",
            new AkadeKey<Bar, (long, Utf8String)>(
                (bounds, max) => (
                    (long)bounds[0]!,
                    bounds.Count > 1
                        ? bounds[1].ToUtf8String()
                        : max
                            ? UpperBound
                            : LowerBound)),
            TsSymbolComparer);


    // Static so Akade's CallerArgumentExpression records the same index name at registration and at
    // query time.
    private static readonly Func<Bar, (Utf8String Symbol, long Ts)> SymbolTs =
        bar => (bar.Symbol, Nanoseconds(bar.Ts));

    private static readonly Func<Bar, (long Ts, Utf8String Symbol)> TsSymbol =
        bar => (Nanoseconds(bar.Ts), bar.Symbol);

    private static readonly AkadeKeyComparer<(Utf8String Symbol, long Ts)> SymbolTsComparer =
        new(key => [key.Symbol, key.Ts]);

    private static readonly AkadeKeyComparer<(long Ts, Utf8String Symbol)> TsSymbolComparer =
        new(key => [key.Ts, key.Symbol]);

    private static readonly Lazy<AkadeCorpusFixture> SharedLazy = new(() => Create());

    /// <summary>
    /// The set a snapshot's Akade indexes are built over (F110): the registered one. The corpus
    /// never appends to <c>bars</c>, so rows of Chalk's own are a fixture error, not a case.
    /// </summary>
    private static IndexedSet<Bar> SetOf(IReadOnlyCollection<Bar> rows) =>
        rows is AkadeRows<Bar> registered
            ? registered.Set
            : throw new InvalidOperationException("the corpus fixture's bars are never appended to");

}
