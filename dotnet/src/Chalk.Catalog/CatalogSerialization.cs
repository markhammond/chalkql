namespace Chalk.Catalog;

/// <summary>
/// The wire form of a catalog. The mapping itself is internal — the proto messages are a transport
/// detail — but the corpus tooling has to write the exact bytes the planner receives, so this is the
/// one place the mapping is exposed.
/// </summary>
public static class CatalogSerialization
{
    /// <summary>The message a <c>RegisterCatalog</c> call carries.</summary>
    public static Ir.CatalogContext ToProto(CatalogContext catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return CatalogProtoMapping.ToProto(catalog);
    }

    /// <summary>Reads a catalog back, for tooling that inspects a recorded one.</summary>
    /// <summary>The cross-source join policy on the wire (D104), for a per-request override.</summary>
    public static Ir.CrossSourceJoinPolicy ToProto(CrossSourceJoinPolicy policy) =>
        CatalogProtoMapping.ToProto(policy);

    public static CatalogContext FromProto(Ir.CatalogContext message) =>
        CatalogProtoMapping.FromProto(message);

    /// <summary>
    /// One column's statistics on the wire, or null when the column declares none (D271 (c),
    /// <c>docs/design/44-catalog-registration.md</c> §3). Exposed beside the whole-catalog mapping
    /// because statistics travel on their own version now, in their own message, and the bytes must
    /// be the ones the table descriptor would have carried.
    /// </summary>
    public static Ir.ColumnStatistics? ToProto(ColumnStatistics statistics, ChalkType type)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        return CatalogProtoMapping.ToProto(statistics, type, "column statistics");
    }
}
