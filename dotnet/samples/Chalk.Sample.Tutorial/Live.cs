using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// What chapters 15, 16 and 17 share: one analytical question, four principals, and the pivot
/// clause that does not change while everything underneath it does.
/// </summary>
public static class Live
{
    /// <summary>
    /// The pivot, written once. Three chapters use this exact text: what changes between them is
    /// what <c>amount</c> is a sum <em>of</em>, and where that number came from.
    /// </summary>
    public const string Pivot =
        "PIVOT (SUM(amount) AS qty FOR warehouse_id IN ('AMS' AS ams, 'SFO' AS sfo, 'SIN' AS sin))";

    /// <summary>
    /// "What is inventory by supplier and warehouse right now?" — chapters 15 and 16 both ask
    /// exactly this, and the answer moves because the positions do.
    /// </summary>
    public const string Positions = """
        SELECT *
        FROM (SELECT p.supplier_id, ip.warehouse_id, ip.quantity AS amount
              FROM inventory_positions ip
              JOIN products p ON p.product_id = ip.product_id)
        PIVOT (SUM(amount) AS qty FOR warehouse_id IN ('AMS' AS ams, 'SFO' AS sfo, 'SIN' AS sin))
        ORDER BY supplier_id
        """;

    /// <summary>
    /// Chapter 17's: the same pivot over the same two axes, valuing the ledger instead of counting
    /// the positions. Each movement is priced at the price that was in force when it happened.
    /// </summary>
    public const string Valuation = """
        SELECT *
        FROM (SELECT p.supplier_id, m.warehouse_id, m.delta * mp.price AS amount
              FROM inventory_movements m
              ASOF JOIN market_prices mp
                MATCH_CONDITION mp.ts <= m.ts
                ON mp.product_id = m.product_id
              JOIN products p ON p.product_id = m.product_id)
        PIVOT (SUM(amount) AS qty FOR warehouse_id IN ('AMS' AS ams, 'SFO' AS sfo, 'SIN' AS sin))
        ORDER BY supplier_id
        """;

    /// <summary>
    /// The four principals of the marketplace, held constant across the three chapters so that access control is
    /// an invariant of the progression rather than something each chapter reintroduces.
    /// </summary>
    public static IReadOnlyList<(string Who, TenancyPrincipal Principal)> Principals(
        Perspectives perspectives)
    {
        ArgumentNullException.ThrowIfNull(perspectives);
        return
        [
            ("warehouse operator, Singapore", perspectives.WarehouseOperator("SIN")),
            ("supplier A's representative", perspectives.SupplierRepresentative("A")),
            ("supplier A's reviewer in Singapore", perspectives.SupplierWarehouseReviewer("A", "SIN")),
            ("Finance", perspectives.FinanceDesk()),
        ];
    }

    /// <summary>Runs one statement as each of the four, printing each principal's slice.</summary>
    public static async Task EachPrincipalAsync(
        ChalkEngine engine,
        EntitledEngine entitled,
        TenancyEntitlements entitlements,
        Perspectives perspectives,
        string sql)
    {
        ArgumentNullException.ThrowIfNull(entitled);
        ArgumentNullException.ThrowIfNull(entitlements);

        foreach (var (who, principal) in Principals(perspectives))
        {
            Console.WriteLine();
            Console.WriteLine("    " + who + ":");
            var prepared = await entitled.PrepareAsync(sql, entitlements.Bind(principal));
            await Chapter10WhosAsking.PrintAsync(engine, prepared, maxRows: 8);
        }
    }

    /// <summary>The catalog a live chapter's policy is declared over.</summary>
    public static CatalogContext Catalog(string contextId, Chalk.Sources.Poco.PocoSource live) =>
        new()
        {
            ContextId = contextId,
            Epoch = 1,
            Schemas = [live is null ? throw new ArgumentNullException(nameof(live)) : live.DescribeSchema()],
        };
}
