using System.Globalization;
using Chalk.Client;
using Chalk.Sources.Poco;

namespace Chalk.Sample.Tutorial;

// The marketplace, in eleven tables. Two tenancies —
// customers and suppliers — are two perspectives over one relational graph, which is what the whole
// tutorial is over: a list, a database, a plan, a policy and a live pivot all read these rows.

/// <summary>A customer of the marketplace. Eight of them, and they never change.</summary>
public sealed record Customer(int CustomerId, string CompanyName, string ContactName, string Country);

/// <summary>
/// A supplier. The id is a short code because a grant is written in it —
/// <c>Grant.ForTenancy(supplier, "A", …)</c> — and the company name is UTF-8 bytes the host owns,
/// which is what chapter 4 is about.
/// </summary>
public sealed record Supplier(string SupplierId, Utf8String CompanyName, string Country);

/// <summary>A product, belonging to one supplier. <c>unit_price</c> is the supplier's list price.</summary>
public sealed record Product(int ProductId, string SupplierId, Utf8String ProductName, decimal UnitPrice);

/// <summary>A sales region. The third tenancy axis on <c>orders</c>, and nothing else's.</summary>
public sealed record Region(int RegionId, string Name);

/// <summary>
/// One of the merchant's own staff. <c>manager_id</c> is this tree's first self-referential foreign
/// key: it names another row of this table, and the run verifies it like any other.
/// </summary>
public sealed record Employee(int EmployeeId, int? ManagerId, string Name, string Title);

/// <summary>
/// An order. It holds its customer, the employee who handled it and the region it was sold into —
/// three tenancy axes on one row — and <c>order_id</c> is the ledger's sequence, strictly increasing.
/// <c>currency</c> is the invoicing currency, which is the customer's: an order's amounts are in the
/// customer's money, so a notional in USD is a conversion (chapter 6).
/// </summary>
public sealed record Order(
    int OrderId,
    int CustomerId,
    int EmployeeId,
    int RegionId,
    DateTime OrderDate,
    decimal Freight,
    string Currency);

/// <summary>
/// One day's rate for one currency: USD per one unit of it, already canonicalised, with USD itself
/// at 1.0. Public and append-only, so a notional in USD is a multiplication and nothing else.
/// </summary>
/// <remarks>
/// The rate is a <see cref="double"/> and not a decimal: a rate is an approximation of a market,
/// and money is exact. Chapter 6's function is where the two meet.
/// </remarks>
public sealed record UsdRate(string Currency, DateOnly Ts, double Rate);

/// <summary>
/// A line of an order. Append-only, keyed by <c>(order_id, product_id)</c>. <c>unit_price</c> is the
/// customer's negotiated price, which is not the supplier's list price on <c>products</c> — and the
/// difference is why a supplier may not read this column.
/// </summary>
public sealed record OrderDetail(
    int OrderId, int ProductId, decimal UnitPrice, int Quantity, decimal Discount);

/// <summary>A physical warehouse. A fourth tenancy kind, and chapters 15 to 17's second axis.</summary>
public sealed record Warehouse(string WarehouseId, string Name, string Country);

/// <summary>
/// How much of one product sits in one warehouse. The host's own materialisation of the ledger,
/// replaced whole by each refresh (chapter 15, chapter 16).
/// </summary>
public sealed record InventoryPosition(
    string WarehouseId, int ProductId, string SupplierId, int Quantity);

/// <summary>One stock movement. Append-only — the ledger chapters 16 and 17 extend.</summary>
public sealed record InventoryMovement(
    int MovementId, DateTime Ts, string WarehouseId, int ProductId, string SupplierId, int Delta);

/// <summary>
/// One price, on its own cadence. Public: a price carries no tenancy at all, which is what makes
/// chapter 17's valuation restricted by the movement it values rather than by the price.
/// </summary>
public sealed record MarketPrice(int PriceId, DateTime Ts, int ProductId, decimal Price);

/// <summary>
/// A movement with the side of the stream it came from beside it. Chapter 16's bootstrapping
/// subsection partitions the ledger on <c>stage</c>: a logical table is keyed by a column of its own
/// rows, so a ledger split between a warehouse and a live tail carries which it is.
/// </summary>
public sealed record LedgerEntry(
    int MovementId, DateTime Ts, string WarehouseId, int ProductId, int Delta, string Stage);

/// <summary>
/// The tutorial's data: small, deterministic and generated the same way on every machine, because
/// <c>docs/tutorial.md</c> quotes this program's output verbatim.
/// </summary>
public static class Data
{
    /// <summary>The day the marketplace's ledger starts.</summary>
    public static readonly DateTime Start = new(2026, 3, 2, 9, 0, 0, DateTimeKind.Unspecified);

    /// <summary>How many orders a chapter with a cost model to exercise asks the generator for.</summary>
    public const int WarehouseRows = 20_000;

    /// <summary>The canonical size of the marketplace's own ledger.</summary>
    public const int Orders = 60;

    /// <summary>
    /// Eight customers in eight G10 countries, so that both quoting conventions are in the data:
    /// four invoice in a currency quoted as USD per unit, three in one quoted as units per USD, and
    /// one invoices in USD and needs no conversion at all.
    /// </summary>
    public static IReadOnlyList<Customer> Customers { get; } =
    [
        new(1, "Northwind Trading", "Ada Kessler", "SE"),
        new(2, "Cedar Capital", "Bo Nilsen", "NO"),
        new(3, "Harbour Partners", "Cai Fontaine", "GB"),
        new(4, "Lantern Markets", "Dara Osei", "US"),
        new(5, "Vasa Import", "Emil Braun", "DE"),
        new(6, "Kanto Supply", "Fen Lindqvist", "JP"),
        new(7, "Rivermouth Foods", "Gita Raman", "AU"),
        new(8, "Solvang Retail", "Hana Vogel", "CH"),
    ];

    /// <summary>
    /// The G10 currencies and what one unit of each is worth in USD. The market quotes some of
    /// these the other way up; this table is the canonicalised form, which is what a rate feed a
    /// host owns usually is, and it is why converting an amount is a multiplication whatever the
    /// currency.
    /// </summary>
    public static IReadOnlyList<(string Currency, double Base)> Currencies { get; } =
    [
        ("AUD", 0.66d),
        ("CAD", 0.735d),
        ("CHF", 1.124d),
        ("EUR", 1.085d),
        ("GBP", 1.27d),
        ("JPY", 0.00661d),
        ("NOK", 0.0926d),
        ("NZD", 0.61d),
        ("SEK", 0.0957d),
        ("USD", 1d),
    ];

    /// <summary>The currency a country invoices in.</summary>
    public static string Currency(string country) => country switch
    {
        "AU" => "AUD",
        "CA" => "CAD",
        "CH" => "CHF",
        "DE" => "EUR",
        "GB" => "GBP",
        "JP" => "JPY",
        "NO" => "NOK",
        "NZ" => "NZD",
        "SE" => "SEK",
        _ => "USD",
    };

    /// <summary>
    /// Six suppliers, keyed by a letter. The company names are not ASCII, which is the point of
    /// them: chapter 4 reads these bytes without decoding any of them.
    /// </summary>
    public static IReadOnlyList<Supplier> Suppliers { get; } =
    [
        new("A", Text("Åkerlund Bryggeri"), "SE"),
        new("B", Text("Épicerie Mourain"), "FR"),
        new("C", Text("Kräuterhof Süd"), "DE"),
        new("D", Text("南陽茶業"), "TW"),
        new("E", Text("Trattoria Piñón"), "IT"),
        new("F", Text("Øresund Fiskeri"), "DK"),
    ];

    public static IReadOnlyList<Region> Regions { get; } =
    [
        new(1, "Nordics"),
        new(2, "Benelux"),
        new(3, "Iberia"),
        new(4, "Levant"),
        new(5, "Maghreb"),
        new(6, "Pacific"),
        new(7, "Andes"),
        new(8, "Baltics"),
    ];

    /// <summary>
    /// Six employees. Employee 1 manages nobody's manager; everyone else names one, so the
    /// self-referential foreign key has something to verify.
    /// </summary>
    public static IReadOnlyList<Employee> Employees { get; } =
    [
        new(1, null, "Ines Halvorsen", "Managing Director"),
        new(2, 1, "Jae Sorensen", "Sales Manager"),
        new(3, 1, "Kirra Boone", "Operations Manager"),
        new(4, 2, "Luis Ferreira", "Sales Representative"),
        new(5, 2, "Mira Okonkwo", "Sales Representative"),
        new(6, 3, "Noa Bergström", "Logistics Clerk"),
    ];

    /// <summary>Twenty products over the six suppliers, in supplier order.</summary>
    public static IReadOnlyList<Product> Products { get; } = BuildProducts();

    /// <summary>Three warehouses. Singapore is the one chapter 15's operator holds.</summary>
    public static IReadOnlyList<Warehouse> Warehouses { get; } =
    [
        new("AMS", "Amsterdam", "NL"),
        new("SFO", "San Francisco", "US"),
        new("SIN", "Singapore", "SG"),
    ];

    /// <summary>
    /// The orders. <paramref name="count"/> defaults to the marketplace's own sixty; the chapters
    /// that want a cost model to have an opinion draw more rows from the same generator, and the
    /// rows are identical as far as the shorter draw goes.
    /// </summary>
    public static IReadOnlyList<Order> OrderRows(int count = Orders)
    {
        var rows = new List<Order>(count);
        for (var i = 0; i < count; i++)
        {
            var customer = Customers[i % Customers.Count];
            rows.Add(new Order(
                OrderId: 1 + i,
                CustomerId: customer.CustomerId,
                EmployeeId: (i % Employees.Count) + 1,
                RegionId: (((i * 5) + (i / 8)) % Regions.Count) + 1,
                OrderDate: Start.AddMinutes(45 * i),
                Freight: 12m + (((i * 17) % 40) * 2.25m),
                Currency: Currency(customer.Country)));
        }

        return rows;
    }

    /// <summary>
    /// The lines. Two or three per order, drawn from the twenty products so that every supplier is
    /// reached and no order is all one supplier's.
    /// </summary>
    public static IReadOnlyList<OrderDetail> OrderDetails(IReadOnlyList<Order> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);
        var rows = new List<OrderDetail>(orders.Count * 3);
        var one = new List<OrderDetail>(3);
        foreach (var order in orders)
        {
            var lines = 2 + (order.OrderId % 2);
            one.Clear();
            for (var line = 0; line < lines; line++)
            {
                var product = Products[((order.OrderId * 7) + (line * 5) + (order.OrderId / 20)) % Products.Count];
                one.Add(new OrderDetail(
                    OrderId: order.OrderId,
                    ProductId: product.ProductId,
                    UnitPrice: decimal.Round(product.UnitPrice * 0.95m, 2),
                    Quantity: 5 + (((order.OrderId * 3) + line + (order.OrderId / 7)) % 20),
                    Discount: ((order.OrderId + line) % 4) * 0.05m));
            }

            // The table is declared ordered by (order_id, product_id) and the declaration is
            // checked, so the lines of one order are written in the order they are read in.
            rows.AddRange(one.OrderBy(d => d.ProductId));
        }

        return rows;
    }

    /// <summary>
    /// The opening positions: every warehouse holds some of the first twelve products. Supplier A's
    /// products are in all three warehouses, which is what makes the reviewer's intersection
    /// smaller than either grant it is made of.
    /// </summary>
    public static IReadOnlyList<InventoryPosition> Positions()
    {
        var rows = new List<InventoryPosition>();
        for (var w = 0; w < Warehouses.Count; w++)
        {
            var warehouse = Warehouses[w];
            for (var i = 0; i < 12; i++)
            {
                var product = Products[i];
                rows.Add(new InventoryPosition(
                    warehouse.WarehouseId,
                    product.ProductId,
                    product.SupplierId,
                    100 + (((product.ProductId * 13) + (w * 29)) % 90)));
            }
        }

        return [.. rows.OrderBy(p => p.WarehouseId, StringComparer.Ordinal).ThenBy(p => p.ProductId)];
    }

    /// <summary>
    /// The ledger as it stands when chapter 16 opens: twenty-four movements, in time order, which is
    /// what the bootstrapping subsection splits into a history and a tail.
    /// </summary>
    public static IReadOnlyList<InventoryMovement> Movements()
    {
        var rows = new List<InventoryMovement>(24);
        for (var i = 0; i < 24; i++)
        {
            var warehouse = Warehouses[i % Warehouses.Count];
            var product = Products[(i * 5) % 12];
            rows.Add(new InventoryMovement(
                MovementId: 1 + i,
                Ts: Start.AddMinutes(10 * i),
                WarehouseId: warehouse.WarehouseId,
                ProductId: product.ProductId,
                SupplierId: product.SupplierId,
                Delta: ((i % 3) == 2 ? -1 : 1) * (5 + ((i * 7) % 25))));
        }

        return rows;
    }

    /// <summary>The batch chapter 16 appends: four more movements, after the last one above.</summary>
    public static IReadOnlyList<InventoryMovement> MovementBatch()
    {
        var after = Movements()[^1];
        return
        [
            Moved(after.MovementId + 1, after.Ts.AddMinutes(10), "SIN", Products[0], 40),
            Moved(after.MovementId + 2, after.Ts.AddMinutes(11), "SIN", Products[4], -15),
            Moved(after.MovementId + 3, after.Ts.AddMinutes(12), "AMS", Products[1], 25),
            Moved(after.MovementId + 4, after.Ts.AddMinutes(13), "SFO", Products[0], -10),
        ];
    }

    private static InventoryMovement Moved(
        int id, DateTime ts, string warehouse, Product product, int delta) =>
        new(id, ts, warehouse, product.ProductId, product.SupplierId, delta);

    /// <summary>
    /// Prices, on a cadence of their own: one per product every forty minutes, which is neither the
    /// movements' cadence nor a multiple of it. That is what makes chapter 17's join an ASOF join.
    /// </summary>
    public static IReadOnlyList<MarketPrice> Prices()
    {
        var rows = new List<MarketPrice>();
        var id = 1;
        for (var i = 0; i < 12; i++)
        {
            var product = Products[i];
            for (var tick = 0; tick < 8; tick++)
            {
                rows.Add(new MarketPrice(
                    PriceId: id++,
                    Ts: Start.AddMinutes(40 * tick),
                    ProductId: product.ProductId,
                    Price: decimal.Round(product.UnitPrice + (((tick * 7) % 11) * 0.25m), 2)));
            }
        }

        return [.. rows.OrderBy(p => p.ProductId).ThenBy(p => p.Ts)];
    }

    /// <summary>
    /// One rate per currency per day, over every day the orders span. A rate is a fact about the
    /// market and belongs to nobody, which is why the policy leaves this table unrestricted. USD is
    /// in it at 1.0, so a statement never has to special-case the currency it converts to.
    /// </summary>
    public static IReadOnlyList<UsdRate> UsdRates(IReadOnlyList<Order> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);
        var first = DateOnly.FromDateTime(orders[0].OrderDate);
        var last = DateOnly.FromDateTime(orders[^1].OrderDate);
        // At least a month, whatever the orders span, so a moving average has something to average.
        var days = Math.Max(30, last.DayNumber - first.DayNumber + 1);

        var rows = new List<UsdRate>(days * Currencies.Count);
        foreach (var (currency, basis) in Currencies)
        {
            for (var day = 0; day < days; day++)
            {
                // A deterministic walk of a few tenths of a percent either way: enough for a moving
                // average to have something to average, and the same on every machine. The dollar
                // does not drift against itself.
                var drift = currency == "USD"
                    ? 1d
                    : 1d + ((((day * 7) + currency[0]) % 13) - 6) / 1000d;
                rows.Add(new UsdRate(currency, first.AddDays(day), Math.Round(basis * drift, 6)));
            }
        }

        return rows;
    }

    /// <summary>
    /// <paramref name="count"/> product rows, cycling <see cref="Products"/>, for chapter 4's
    /// measurement — which needs two result sizes rather than a realistic table.
    /// </summary>
    public static IReadOnlyList<Product> ProductRepeat(int count)
    {
        var rows = new List<Product>(count);
        for (var i = 0; i < count; i++)
        {
            var template = Products[i % Products.Count];
            rows.Add(template with { ProductId = i + 1 });
        }

        return rows;
    }

    /// <summary>The year a partitioned <c>orders</c> table is keyed by (chapter 7).</summary>
    public static int Year(Order order) =>
        order is null ? throw new ArgumentNullException(nameof(order)) : order.OrderDate.Year;

    private static IReadOnlyList<Product> BuildProducts()
    {
        string[] names =
        [
            "Björnkorv", "Lingonsylt", "Knäckebröd",
            "Crème Fraîche", "Pâté Mourain", "Cassoulet Épicé",
            "Kräutertee", "Süßmost", "Bergkäse",
            "凍頂烏龍", "東方美人", "桂花烏龍",
            "Olio d'Oliva", "Pesto Genovese", "Piñón Tostado",
            "Røget Sild", "Klipfisk", "Tørret Rejer",
            "Havssalt", "Rapsolja",
        ];

        var rows = new List<Product>(names.Length);
        for (var i = 0; i < names.Length; i++)
        {
            rows.Add(new Product(
                ProductId: i + 1,
                SupplierId: Suppliers[i % Suppliers.Count].SupplierId,
                ProductName: Text(names[i]),
                UnitPrice: decimal.Round(4.5m + (((i * 11) % 23) * 1.75m), 2)));
        }

        return rows;
    }

    /// <summary>UTF-8 bytes the host owns: constructed here, so they outlive every batch.</summary>
    private static Utf8String Text(string value) => Utf8String.FromString(value);

    /// <summary>A date, the way every chapter prints one.</summary>
    public static string Day(DateTime value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
