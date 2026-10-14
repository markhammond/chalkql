namespace Chalk.Catalog;

/// <summary>
/// How a catalog differs from one before it, read by reference (F205): the tables that are not the
/// very descriptors they were, whether each moved in nothing but its numbers, whether the join policy
/// moved, and whether anything else might have.
/// </summary>
/// <remarks>
/// <para>
/// A source that hands back the descriptor it handed before for a table that did not change, and the
/// parts of the one before for a table whose rows did, lets a refresh validate, convert and
/// fingerprint what it changed rather than the whole catalog. A source that builds every descriptor
/// afresh loses nothing: its tables read as moved, and the catalog is taken whole, as before.
/// </para>
/// <para>
/// Only references are compared, never contents, so a part that is the same object is the same part
/// and a part that is not is taken to have moved. That is the conservative direction: the worst a
/// source that builds equal parts afresh can do is cost a whole reading.
/// </para>
/// </remarks>
internal sealed class CatalogChange
{
    private static readonly CatalogChange Whole = new(structural: true, joinPolicyMoved: true, []);

    private CatalogChange(bool structural, bool joinPolicyMoved, IReadOnlyList<TableChange> tables)
    {
        Structural = structural;
        JoinPolicyMoved = joinPolicyMoved;
        Tables = tables;
        var countsOnly = !structural;
        foreach (var table in tables)
        {
            countsOnly &= table.CountOnly;
        }

        CountsOnly = countsOnly;
    }

    /// <summary>
    /// Something besides the tables' numbers and the join policy might have moved: the schemas, a
    /// schema's own parts, a table's shape, or the associations. The catalog is to be read whole.
    /// </summary>
    public bool Structural { get; }

    /// <summary>The join policy is not the object it was.</summary>
    public bool JoinPolicyMoved { get; }

    /// <summary>
    /// The tables that are not the very descriptors they were, in catalog order; empty when
    /// <see cref="Structural"/>, since then every table is to be read.
    /// </summary>
    public IReadOnlyList<TableChange> Tables { get; }

    /// <summary>
    /// Nothing moved but tables' row counts and the join policy: every other part of the catalog is
    /// the object it was.
    /// </summary>
    public bool CountsOnly { get; }

    /// <summary>
    /// The members of each descriptor this reads, by name: a test holds them to the descriptors' own,
    /// so a member added to a descriptor is read here or the test says it is not.
    /// </summary>
    internal static readonly IReadOnlyList<string> CatalogMembers =
    [
        nameof(CatalogContext.ContextId), nameof(CatalogContext.Schemas), nameof(CatalogContext.Epoch),
        nameof(CatalogContext.JoinPolicy), nameof(CatalogContext.Associations),
    ];

    /// <inheritdoc cref="CatalogMembers"/>
    internal static readonly IReadOnlyList<string> SchemaMembers =
    [
        nameof(SchemaDescriptor.SourceId), nameof(SchemaDescriptor.Name), nameof(SchemaDescriptor.Kind),
        nameof(SchemaDescriptor.Dialect), nameof(SchemaDescriptor.Capabilities),
        nameof(SchemaDescriptor.DialectProfile), nameof(SchemaDescriptor.CostProfile),
        nameof(SchemaDescriptor.Tables), nameof(SchemaDescriptor.Functions),
        nameof(SchemaDescriptor.TrustSourceRowLevelSecurity), nameof(SchemaDescriptor.Zone),
    ];

    /// <inheritdoc cref="CatalogMembers"/>
    internal static readonly IReadOnlyList<string> TableMembers =
    [
        nameof(TableDescriptor.Name), nameof(TableDescriptor.Columns), nameof(TableDescriptor.RowCount),
        nameof(TableDescriptor.RowCountKind), nameof(TableDescriptor.CostProfile),
        nameof(TableDescriptor.UniqueKeys), nameof(TableDescriptor.Collations), nameof(TableDescriptor.Indexes),
        nameof(TableDescriptor.ForeignKeys), nameof(TableDescriptor.Partitioning),
        nameof(TableDescriptor.Entitlement), nameof(TableDescriptor.IsPublic),
    ];

    /// <summary>What moved from <paramref name="before"/> to <paramref name="after"/>.</summary>
    public static CatalogChange Between(CatalogContext before, CatalogContext after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        // The epoch counts publishes and describes nothing, so it is not read.
        if (!string.Equals(before.ContextId, after.ContextId, StringComparison.Ordinal)
            || !ReferenceEquals(before.Associations, after.Associations)
            || before.Schemas.Count != after.Schemas.Count)
        {
            return Whole;
        }

        List<TableChange>? tables = null;
        for (var s = 0; s < after.Schemas.Count; s++)
        {
            var was = before.Schemas[s];
            var now = after.Schemas[s];
            if (ReferenceEquals(was, now))
            {
                continue;
            }

            if (!SameSchema(was, now))
            {
                return Whole;
            }

            for (var t = 0; t < now.Tables.Count; t++)
            {
                var wasTable = was.Tables[t];
                var nowTable = now.Tables[t];
                if (ReferenceEquals(wasTable, nowTable))
                {
                    continue;
                }

                if (!SameShape(wasTable, nowTable))
                {
                    return Whole;
                }

                (tables ??= []).Add(new TableChange(s, t, wasTable, nowTable));
            }
        }

        return new CatalogChange(
            structural: false, !ReferenceEquals(before.JoinPolicy, after.JoinPolicy), tables ?? []);
    }

    /// <summary>A schema's own parts, which are everything but its tables, and how many tables it has.</summary>
    private static bool SameSchema(SchemaDescriptor was, SchemaDescriptor now) =>
        string.Equals(was.SourceId, now.SourceId, StringComparison.Ordinal)
        && string.Equals(was.Name, now.Name, StringComparison.Ordinal)
        && was.Kind == now.Kind
        && string.Equals(was.Dialect, now.Dialect, StringComparison.Ordinal)
        && ReferenceEquals(was.Capabilities, now.Capabilities)
        && ReferenceEquals(was.DialectProfile, now.DialectProfile)
        && ReferenceEquals(was.CostProfile, now.CostProfile)
        && ReferenceEquals(was.Functions, now.Functions)
        && was.TrustSourceRowLevelSecurity == now.TrustSourceRowLevelSecurity
        && string.Equals(was.Zone, now.Zone, StringComparison.Ordinal)
        && was.Tables.Count == now.Tables.Count;

    /// <summary>A table's parts but its numbers: its row count, and its columns, which carry their statistics.</summary>
    private static bool SameShape(TableDescriptor was, TableDescriptor now) =>
        string.Equals(was.Name, now.Name, StringComparison.Ordinal)
        && was.RowCountKind == now.RowCountKind
        && ReferenceEquals(was.CostProfile, now.CostProfile)
        && ReferenceEquals(was.UniqueKeys, now.UniqueKeys)
        && ReferenceEquals(was.Collations, now.Collations)
        && ReferenceEquals(was.Indexes, now.Indexes)
        && ReferenceEquals(was.ForeignKeys, now.ForeignKeys)
        && ReferenceEquals(was.Partitioning, now.Partitioning)
        && ReferenceEquals(was.Entitlement, now.Entitlement)
        && was.IsPublic == now.IsPublic;
}

/// <summary>
/// A table that is not the descriptor it was, though every part of it but its row count and its
/// columns is: at <see cref="Schema"/> and <see cref="Table"/> in the catalog's lists.
/// </summary>
internal readonly record struct TableChange(int Schema, int Table, TableDescriptor Before, TableDescriptor After)
{
    /// <summary>
    /// Its columns are the very list they were, so nothing but its row count moved; otherwise its
    /// columns, and the statistics they carry, are to be read again.
    /// </summary>
    public bool CountOnly => ReferenceEquals(Before.Columns, After.Columns);
}
