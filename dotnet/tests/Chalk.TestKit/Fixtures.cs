using Chalk.Sources.Poco;

namespace Chalk.TestKit;

/// <summary>A one-minute OHLCV bar — the client-zero shape (docs/design/05-testing.md §2).</summary>
public sealed record Bar(
    Utf8String Symbol,
    DateTime Ts,
    double Open,
    double High,
    double Low,
    double Close,
    long Volume,
    decimal? Vwap,
    int? TradeCount);

/// <summary>
/// The small dimension table. M1 uses it standalone; it exists so M5 need not invent it.
/// <c>Tags</c> is step 19's <c>LIST</c> column (D58): a <c>string[]</c> member, with one row whose
/// array is null and one whose array is empty, so the two edges of <c>UNNEST</c> are in the data.
/// </summary>
public sealed record SymbolRow(
    Utf8String Symbol, Utf8String Base, Utf8String Quote, decimal TickSize, string[]? Tags);

/// <summary>
/// An irregular trade series with deliberate gaps: the <c>SESSION</c> fixture (D55). Collated by
/// <c>(symbol, ts)</c>, which is the order a session window requires, and carrying a few NULL times —
/// the rows that belong to no session and sort last.
/// </summary>
public sealed record Trade(Utf8String Symbol, DateTime? Ts, double Price, long Size);

/// <summary>
/// TPC-H-shaped, the non-time-series half of the corpus (rev 3 §8). A <c>record struct</c> on
/// purpose: the extractors must work over struct rows, not only class rows.
/// </summary>
public record struct LineItem(
    long OrderKey,
    int LineNumber,
    long PartKey,
    long SuppKey,
    [property: ChalkColumn(Precision = 15, Scale = 2)] decimal Quantity,
    [property: ChalkColumn(Precision = 15, Scale = 2)] decimal ExtendedPrice,
    [property: ChalkColumn(Precision = 15, Scale = 2)] decimal Discount,
    [property: ChalkColumn(Precision = 15, Scale = 2)] decimal Tax,
    string ReturnFlag,
    string LineStatus,
    DateOnly ShipDate,
    DateOnly CommitDate,
    DateOnly ReceiptDate,
    string ShipInstruct,
    string ShipMode,
    string Comment);


/// <summary>
/// A per-symbol funding rate, published every eight hours. The ASOF fixture (D46): the rate in force
/// at a bar's timestamp is the most recent one at or before it.
/// </summary>
public sealed record Funding(
    Utf8String Symbol,
    DateTime? Ts,
    [property: ChalkColumn(Precision = 10, Scale = 8)] decimal Rate);

/// <summary>A window of time per symbol, for the range joins that have no equality to hash on.</summary>
public sealed record MarketEvent(
    int Id, Utf8String Symbol, DateTime StartTs, DateTime EndTs, string Kind);

/// <summary>TPC-H <c>customer</c>, at the SF 0.01 shape.</summary>
public sealed record Customer(
    long CustKey,
    string Name,
    string Address,
    long NationKey,
    string Phone,
    [property: ChalkColumn(Precision = 15, Scale = 2)] decimal AcctBal,
    string MktSegment,
    string Comment);

/// <summary>TPC-H <c>orders</c>, at the SF 0.01 shape.</summary>
public sealed record Order(
    long OrderKey,
    long CustKey,
    string OrderStatus,
    [property: ChalkColumn(Precision = 15, Scale = 2)] decimal TotalPrice,
    DateOnly OrderDate,
    string OrderPriority,
    string Clerk,
    int ShipPriority,
    string Comment);

/// <summary>TPC-H <c>nation</c> — the real 25.</summary>
public sealed record Nation(long NationKey, string Name, long RegionKey, string Comment);

/// <summary>TPC-H <c>region</c> — the real 5.</summary>
public sealed record Region(long RegionKey, string Name, string Comment);

/// <summary>TPC-H <c>supplier</c>, at the SF 0.01 shape.</summary>
public sealed record Supplier(
    long SuppKey,
    string Name,
    string Address,
    long NationKey,
    string Phone,
    [property: ChalkColumn(Precision = 15, Scale = 2)] decimal AcctBal,
    string Comment);

/// <summary>
/// The adversarial six (D164, <c>docs/design/25-coverage-graft.md</c> §1). Two regions, a duplicate
/// <c>Amount</c>, one NULL <c>Amount</c>, no declared collation and no unique key: ties, nulls and
/// partitions in six rows.
/// </summary>
/// <remarks>
/// Grafted from <c>ikvmnet/calcite-dotnet</c> (Apache-2.0), <c>ClrEnumerableDifferentialTests.cs</c>
/// — the rows and their shape, never an expected answer (D163). Hand-written rather than generated,
/// which is an explicit exception to <c>05-testing.md</c> §2: the value of this table is the exact
/// ties and NULLs it holds, and a generator would only hide them.
/// </remarks>
public sealed record Sale(int Id, string Region, int? Amount, string Label);

/// <summary>
/// Four rows declared ordered by <c>K</c> and with <b>no</b> unique key (D164): a duplicate key and
/// a gap, so a merge join and a merge union stay reachable over a key that repeats.
/// </summary>
/// <remarks>
/// Grafted from <c>ikvmnet/calcite-dotnet</c> (Apache-2.0), <c>ClrEnumerableDifferentialTests.cs</c>.
/// </remarks>
public sealed record SortedRow(int K, string V);

/// <summary>
/// Deterministic generators. Every one takes a seed and produces identical data on every platform:
/// seeded <see cref="Random"/> uses .NET's stable legacy algorithm, and no control flow depends on a
/// floating-point comparison. <b>Changing a generator changes every recorded plan digest's data, and
/// therefore every differential expectation.</b>
/// </summary>
public static class Fixtures
{
    /// <summary>The seed the corpus is generated with.</summary>
    public const int DefaultSeed = 20260907;

    /// <summary>The five symbols, in the order they sort. Bars are emitted in this order within a minute.</summary>
    public static readonly Utf8String[] Symbols = [Utf8String.FromString("ADAUSDT") , Utf8String.FromString("BTCUSDT"), Utf8String.FromString("ETHUSDT"), Utf8String.FromString("SOLUSDT"), Utf8String.FromString("XRPUSDT")];

    /// <summary>14 days of one-minute bars: 20 160 minutes × 5 symbols.</summary>
    public const int BarMinutes = 20_160;

    /// <summary>
    /// Minutes per symbol in <c>bars_small</c> (D53): 5 symbols × 1 000 minutes = 5 000 rows, small
    /// enough for the reference executor to materialise every window frame.
    /// </summary>
    public const int BarsSmallMinutes = 1_000;

    /// <summary>60 000 rows, about TPC-H scale factor 0.01.</summary>
    public const int LineItemRows = 60_000;

    private static readonly DateTime BarsStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>
    /// Bars sorted by <c>(ts, symbol)</c>, which is what makes <c>ORDER BY ts</c> lose its sort.
    /// <c>Vwap</c> is NULL in 2% of rows and <c>TradeCount</c> in 5%, so the null paths are exercised
    /// by every query that touches them.
    /// </summary>
    public static IReadOnlyList<Bar> Bars(int seed = DefaultSeed, int minutes = BarMinutes)
    {
        var random = new Random(seed);
        var price = new double[Symbols.Length];
        for (var s = 0; s < Symbols.Length; s++)
        {
            price[s] = 10 * Math.Pow(10, s);
        }

        var bars = new List<Bar>(minutes * Symbols.Length);
        for (var minute = 0; minute < minutes; minute++)
        {
            var ts = BarsStart.AddMinutes(minute);
            for (var s = 0; s < Symbols.Length; s++)
            {
                var open = price[s];
                var drift = (random.NextDouble() - 0.5) * 0.004 * open;
                var close = open + drift;
                var spread = random.NextDouble() * 0.002 * open;
                var high = Math.Max(open, close) + spread;
                var low = Math.Min(open, close) - spread;
                var volume = 1_000L + random.Next(0, 9_000);

                decimal? vwap = random.Next(100) < 2
                    ? null
                    : Math.Round((decimal)((high + low + close) / 3), 10);
                int? tradeCount = random.Next(100) < 5 ? null : random.Next(1, 500);

                bars.Add(new Bar(Symbols[s], ts, open, high, low, close, volume, vwap, tradeCount));
                price[s] = close;
            }
        }

        return bars;
    }

    /// <summary>
    /// The five dimension rows, sorted by symbol. <c>Tags</c> covers the three shapes a
    /// <c>LIST</c> column has: several elements, an empty list, and NULL.
    /// </summary>
    public static IReadOnlyList<SymbolRow> SymbolRows() =>
    [
        new(Utf8String.FromString("ADAUSDT"), Utf8String.FromString("ADA"), Utf8String.FromString("USDT"), 0.0001m, ["alt", "pos"]),
        new(Utf8String.FromString("BTCUSDT"), Utf8String.FromString("BTC"), Utf8String.FromString("USDT"), 0.01m, ["major", "pow", "store"]),
        new(Utf8String.FromString("ETHUSDT"), Utf8String.FromString("ETH"), Utf8String.FromString("USDT"), 0.01m, ["major", "smart"]),
        new(Utf8String.FromString("SOLUSDT"), Utf8String.FromString("SOL"), Utf8String.FromString("USDT"), 0.001m, []),
        new(Utf8String.FromString("XRPUSDT"), Utf8String.FromString("XRP"), Utf8String.FromString("USDT"), 0.0001m, null),
    ];

    /// <summary>Rows in <c>trades_sparse</c>: about 3 000 over the same fourteen days as the bars.</summary>
    public const int SparseTradesPerSymbol = 600;

    /// <summary>
    /// An irregular trade series per symbol, sorted by <c>(symbol, ts)</c>. Gaps are drawn from a
    /// mixture — mostly a minute or two, occasionally several hours — so a two-hour session window
    /// finds a handful of sessions per symbol rather than one or one per row. Every symbol's last
    /// two rows have a NULL timestamp, which is the row that belongs to no session and which sorts
    /// last under Chalk's NULLS LAST ascending collation.
    /// </summary>
    public static IReadOnlyList<Trade> SparseTrades(int seed = DefaultSeed)
    {
        var random = new Random(seed);
        var rows = new List<Trade>(Symbols.Length * (SparseTradesPerSymbol + 2));
        for (var s = 0; s < Symbols.Length; s++)
        {
            var price = 10 * Math.Pow(10, s);
            var ts = BarsStart.AddMinutes(s);
            for (var i = 0; i < SparseTradesPerSymbol; i++)
            {
                // Nine times out of ten a minute or two; otherwise a gap of three to nine hours,
                // which is what ends a session.
                var minutes = random.Next(10) < 9 ? 1 + random.Next(2) : 180 + random.Next(360);
                ts = ts.AddMinutes(minutes);
                price += (random.NextDouble() - 0.5) * 0.01 * price;
                rows.Add(new Trade(Symbols[s], ts, Math.Round(price, 6), 1 + random.Next(500)));
            }

            rows.Add(new Trade(Symbols[s], null, Math.Round(price, 6), 1 + random.Next(500)));
            rows.Add(new Trade(Symbols[s], null, Math.Round(price, 6), 1 + random.Next(500)));
        }

        return rows;
    }

    /// <summary>
    /// TPC-H-like line items sorted by <c>(l_orderkey, l_linenumber)</c>: dates in 1992–1998,
    /// discounts 0.00–0.10, tax 0.00–0.08, return flags R/A/N and line statuses O/F. Not the official
    /// <c>dbgen</c> (A8), so results are compared across executors rather than to published answers.
    /// </summary>
    public static IReadOnlyList<LineItem> LineItems(int seed = DefaultSeed, int rows = LineItemRows)
    {
        var random = new Random(seed);
        string[] returnFlags = ["R", "A", "N"];
        string[] lineStatuses = ["O", "F"];
        string[] shipInstructs = ["DELIVER IN PERSON", "COLLECT COD", "NONE", "TAKE BACK RETURN"];
        string[] shipModes = ["AIR", "AIR REG", "RAIL", "SHIP", "TRUCK", "MAIL", "FOB"];
        var epoch = new DateOnly(1992, 1, 1);

        var items = new List<LineItem>(rows);
        long orderKey = 1;
        while (items.Count < rows)
        {
            var lines = 1 + random.Next(7);
            for (var line = 1; line <= lines && items.Count < rows; line++)
            {
                var shipDays = random.Next(0, 2557);        // 1992-01-01 .. 1998-12-31
                var shipDate = epoch.AddDays(shipDays);
                var quantity = decimal.Round(1m + random.Next(0, 4900) / 100m, 2);
                var extendedPrice = decimal.Round(quantity * (900m + random.Next(0, 100_000) / 100m), 2);
                var discount = decimal.Round(random.Next(0, 11) / 100m, 2);
                var tax = decimal.Round(random.Next(0, 9) / 100m, 2);

                items.Add(new LineItem(
                    orderKey,
                    line,
                    1 + random.Next(200_000),
                    1 + random.Next(10_000),
                    quantity,
                    extendedPrice,
                    discount,
                    tax,
                    returnFlags[random.Next(returnFlags.Length)],
                    lineStatuses[random.Next(lineStatuses.Length)],
                    shipDate,
                    shipDate.AddDays(random.Next(30, 90)),
                    shipDate.AddDays(random.Next(1, 30)),
                    shipInstructs[random.Next(shipInstructs.Length)],
                    shipModes[random.Next(shipModes.Length)],
                    "comment " + random.Next(1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            orderKey++;
        }

        return items;
    }

    /// <summary>Funding rows per symbol: one every eight hours over the same 14 days as the bars.</summary>
    public const int FundingSlots = 42;

    /// <summary>
    /// One funding rate per symbol every eight hours, sorted by <c>(symbol, ts)</c>. The last slot of
    /// the first and last symbols has a NULL <c>ts</c>, which is the row an ASOF join must never
    /// match (D41) and which sorts last because Chalk puts NULLs last in an ascending collation.
    /// </summary>
    public static IReadOnlyList<Funding> FundingRates(int seed = DefaultSeed)
    {
        var random = new Random(seed);
        var rows = new List<Funding>(Symbols.Length * FundingSlots);
        foreach (var symbol in Symbols)
        {
            for (var slot = 0; slot < FundingSlots; slot++)
            {
                var nullTime = slot == FundingSlots - 1
                    && (symbol == Symbols[0] || symbol == Symbols[^1]);
                var rate = decimal.Round((random.Next(-2000, 2001) / 100_000_000m), 8);
                rows.Add(new Funding(
                    symbol, nullTime ? null : BarsStart.AddHours(8 * slot), rate));
            }
        }

        return rows;
    }

    /// <summary>
    /// The six <c>sales</c> rows, exactly as §1 lists them. A constant, not a generator: the point of
    /// the table is which values tie and which is NULL (D164).
    /// </summary>
    public static IReadOnlyList<Sale> Sales() =>
    [
        new(1, "EAST", 10, "A"),
        new(2, "EAST", 20, "B"),
        new(3, "EAST", 20, "C"),
        new(4, "WEST", 30, "D"),
        new(5, "WEST", null, "E"),
        new(6, "WEST", 5, "F"),
    ];

    /// <summary>The four <c>sorted</c> rows: a duplicate key at 2 and a gap at 3 (D164).</summary>
    public static IReadOnlyList<SortedRow> SortedRows() =>
    [
        new(1, "A"),
        new(2, "B"),
        new(2, "C"),
        new(4, "D"),
    ];

    /// <summary>Twenty windows over the 14 days, four per symbol, of three kinds.</summary>
    public static IReadOnlyList<MarketEvent> Events()
    {
        string[] kinds = ["halt", "listing", "maintenance", "spike"];
        var rows = new List<MarketEvent>(20);
        var id = 1;
        for (var s = 0; s < Symbols.Length; s++)
        {
            for (var e = 0; e < 4; e++)
            {
                // Windows of 5 hours, 18 hours apart, staggered per symbol so some bars fall in more
                // than one and some in none.
                var start = BarsStart.AddHours((18 * e) + (3 * s));
                // Two of the four kinds per symbol, and which two depends on the symbol — so a
                // query asking for one kind finds it for some symbols and not others, which is what
                // makes the ANTI and FULL corpus queries have anything to say.
                rows.Add(new MarketEvent(
                    id, Symbols[s], start, start.AddHours(5), kinds[((id * 2) + s) % kinds.Length]));
                id++;
            }
        }

        return rows;
    }

    /// <summary>The five TPC-H regions, by key.</summary>
    public static IReadOnlyList<Region> Regions() =>
    [
        new(0, "AFRICA", "region 0"),
        new(1, "AMERICA", "region 1"),
        new(2, "ASIA", "region 2"),
        new(3, "EUROPE", "region 3"),
        new(4, "MIDDLE EAST", "region 4"),
    ];

    /// <summary>The 25 TPC-H nations, by key, each in its real region.</summary>
    public static IReadOnlyList<Nation> Nations()
    {
        (string Name, long Region)[] nations =
        [
            ("ALGERIA", 0), ("ARGENTINA", 1), ("BRAZIL", 1), ("CANADA", 1), ("EGYPT", 4),
            ("ETHIOPIA", 0), ("FRANCE", 3), ("GERMANY", 3), ("INDIA", 2), ("INDONESIA", 2),
            ("IRAN", 4), ("IRAQ", 4), ("JAPAN", 2), ("JORDAN", 4), ("KENYA", 0),
            ("MOROCCO", 0), ("MOZAMBIQUE", 0), ("PERU", 1), ("CHINA", 2), ("ROMANIA", 3),
            ("SAUDI ARABIA", 4), ("VIETNAM", 2), ("RUSSIA", 3), ("UNITED KINGDOM", 3),
            ("UNITED STATES", 1),
        ];
        return [.. nations.Select((n, i) => new Nation(i, n.Name, n.Region, $"nation {i}"))];
    }

    /// <summary>1 500 customers, keys 1..1500, sorted by key.</summary>
    public static IReadOnlyList<Customer> Customers(int seed = DefaultSeed, int rows = 1_500)
    {
        var random = new Random(seed);
        string[] segments = ["BUILDING", "AUTOMOBILE", "MACHINERY", "HOUSEHOLD", "FURNITURE"];
        var customers = new List<Customer>(rows);
        for (var i = 1; i <= rows; i++)
        {
            customers.Add(new Customer(
                i,
                "Customer#" + i.ToString("000000", System.Globalization.CultureInfo.InvariantCulture),
                "address " + random.Next(1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture),
                random.Next(25),
                "phone-" + random.Next(1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture),
                decimal.Round(random.Next(-99_999, 999_999) / 100m, 2),
                segments[random.Next(segments.Length)],
                "customer comment " + random.Next(1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return customers;
    }

    /// <summary>100 suppliers, keys 1..100, sorted by key.</summary>
    public static IReadOnlyList<Supplier> Suppliers(int seed = DefaultSeed, int rows = 100)
    {
        var random = new Random(seed);
        var suppliers = new List<Supplier>(rows);
        for (var i = 1; i <= rows; i++)
        {
            suppliers.Add(new Supplier(
                i,
                "Supplier#" + i.ToString("000000", System.Globalization.CultureInfo.InvariantCulture),
                "address " + random.Next(1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture),
                random.Next(25),
                "phone-" + random.Next(1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture),
                decimal.Round(random.Next(-99_999, 999_999) / 100m, 2),
                "supplier comment " + random.Next(1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return suppliers;
    }

    /// <summary>
    /// 15 000 orders, keys 1..15000, sorted by key — the same key space
    /// <see cref="LineItems"/> draws its <c>l_orderkey</c> from, so the two join.
    /// </summary>
    public static IReadOnlyList<Order> Orders(int seed = DefaultSeed, int rows = 15_000, int customers = 1_500)
    {
        var random = new Random(seed);
        string[] statuses = ["O", "F", "P"];
        string[] priorities = ["1-URGENT", "2-HIGH", "3-MEDIUM", "4-NOT SPECIFIED", "5-LOW"];
        var epoch = new DateOnly(1992, 1, 1);
        var orders = new List<Order>(rows);
        for (var i = 1; i <= rows; i++)
        {
            orders.Add(new Order(
                i,
                1 + random.Next(customers),
                statuses[random.Next(statuses.Length)],
                decimal.Round(random.Next(100, 500_000) / 100m * 100m, 2),
                epoch.AddDays(random.Next(0, 2557)),
                priorities[random.Next(priorities.Length)],
                "Clerk#" + random.Next(1000).ToString("0000", System.Globalization.CultureInfo.InvariantCulture),
                random.Next(0, 2),
                "order comment " + random.Next(1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return orders;
    }
}
