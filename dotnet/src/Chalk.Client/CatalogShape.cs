using Google.Protobuf;
using CatalogContext = Chalk.Catalog.CatalogContext;
using RowCountKind = Chalk.Ir.RowCountKind;

namespace Chalk.Client;

/// <summary>
/// One table, as the catalog addresses it. The source id is a runtime's identifier and matched
/// exactly; the schema and table are SQL identifiers and matched case-insensitively, the way every
/// other lookup in the client matches them — a planner that spells a name in another case is naming
/// the same table.
/// </summary>
internal readonly struct TableKey(string sourceId, string schema, string table)
    : IEquatable<TableKey>
{
    public string SourceId { get; } = sourceId;

    public string Schema { get; } = schema;

    public string Table { get; } = table;

    public bool Equals(TableKey other) =>
        string.Equals(SourceId, other.SourceId, StringComparison.Ordinal)
        && string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Table, other.Table, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is TableKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(SourceId),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Schema),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Table));

    public static bool operator ==(TableKey left, TableKey right) => left.Equals(right);

    public static bool operator !=(TableKey left, TableKey right) => !left.Equals(right);

    public override string ToString() => $"{Schema}.{Table}";
}

/// <summary>
/// How the engine tells a schema change from a data change (D260 §2), <b>per table</b> since D271
/// (d): a catalog's <em>shape</em> is the catalog the planner would be sent with everything that is
/// data rather than shape struck out, and a table's shape is that table's own share of it.
/// </summary>
/// <remarks>
/// <para>
/// It is computed from the wire catalog rather than from a hand-written list of fields, so a field
/// added to the contract is part of the shape by default. That is the conservative direction: the
/// worst a missed exclusion can do is re-prepare a statement that need not have been, where a
/// missed <em>inclusion</em> would run a plan against a table whose shape had moved.
/// </para>
/// <para>
/// Struck out, and only these: the epoch, which counts refreshes rather than describing anything;
/// every table's row count and its kind; every column's statistics; and the cross-source join
/// policy, which a host may compute from the row counts it can see (D104) and which decides how the
/// <em>next</em> plan is built rather than whether this one is still runnable.
/// </para>
/// <para>
/// Comparing per table is what lets a refresh say which tables changed shape, which changed only in
/// their numbers and which did not change at all — so a shape change registers a delta naming the
/// changed tables (§4), a data-only change publishes statistics alone (§3), and only the prepared
/// queries that read a changed table go stale.
/// </para>
/// </remarks>
internal sealed class CatalogShape
{
    private CatalogShape(
        byte[] catalog,
        IReadOnlyDictionary<TableKey, byte[]> tables,
        IReadOnlyDictionary<TableKey, byte[]> statistics)
    {
        Catalog = catalog;
        Tables = tables;
        Statistics = statistics;
    }

    /// <summary>The bytes two catalogs are shape-equal when they share, as D260 §2 defined them.</summary>
    public byte[] Catalog { get; }

    /// <summary>Every table's own shape, keyed by the triple that addresses it.</summary>
    public IReadOnlyDictionary<TableKey, byte[]> Tables { get; }

    /// <summary>
    /// Every table's <em>numbers</em> — the row count, its kind and the columns' statistics — as the
    /// bytes that say whether a refresh moved them. What the shape strikes out, this keeps.
    /// </summary>
    public IReadOnlyDictionary<TableKey, byte[]> Statistics { get; }

    public static CatalogShape Of(CatalogContext catalog) =>
        Of(Chalk.Catalog.CatalogSerialization.ToProto(catalog));

    /// <summary>The same from the wire message, which is what the engine already has in hand.</summary>
    public static CatalogShape Of(Chalk.Ir.CatalogContext wire)
    {
        // A clone, because striking fields out must not touch the message the caller is about to
        // register.
        var message = wire.Clone();
        message.Epoch = 0;
        message.JoinPolicy = null;

        var tables = new Dictionary<TableKey, byte[]>();
        var statistics = new Dictionary<TableKey, byte[]>();
        foreach (var schema in message.Schemas)
        {
            foreach (var table in schema.Tables)
            {
                var key = new TableKey(schema.SourceId, schema.Name, table.Name);
                statistics[key] = StatisticsOf(table);
                table.RowCount = 0;
                table.RowCountKind = RowCountKind.Unspecified;
                foreach (var column in table.Columns)
                {
                    column.Statistics = null;
                }

                tables[key] = table.ToByteArray();
            }
        }

        return new CatalogShape(message.ToByteArray(), tables, statistics);
    }

    /// <summary>
    /// The bytes a table's numbers are equal when they share: the row count, its kind, and each
    /// column's statistics in column order. A table whose statistics are all absent hashes to the
    /// same bytes as another such table, which is what "nothing moved" has to mean.
    /// </summary>
    private static byte[] StatisticsOf(Chalk.Ir.Table table)
    {
        var numbers = new Chalk.Ir.Table
        {
            RowCount = table.RowCount,
            RowCountKind = table.RowCountKind,
        };

        foreach (var column in table.Columns)
        {
            numbers.Columns.Add(new Chalk.Ir.Column
            {
                Name = column.Name,
                Statistics = column.Statistics?.Clone(),
            });
        }

        return numbers.ToByteArray();
    }

    /// <summary>
    /// Every table whose shape differs between two catalogs, and every table one has that the other
    /// does not. Order is the later catalog's own, so a registration's delta lists its tables in the
    /// order the catalog declares them.
    /// </summary>
    public IReadOnlyList<TableKey> ShapeChangesSince(CatalogShape previous)
    {
        var changed = new List<TableKey>();
        foreach (var (key, shape) in Tables)
        {
            if (!previous.Tables.TryGetValue(key, out var before)
                || !shape.AsSpan().SequenceEqual(before))
            {
                changed.Add(key);
            }
        }

        return changed;
    }

    /// <summary>Every table <paramref name="previous"/> had that this one does not.</summary>
    public IReadOnlyList<TableKey> RemovedSince(CatalogShape previous)
    {
        var removed = new List<TableKey>();
        foreach (var key in previous.Tables.Keys)
        {
            if (!Tables.ContainsKey(key))
            {
                removed.Add(key);
            }
        }

        return removed;
    }

    /// <summary>
    /// Every table whose numbers moved. A table whose shape changed is in here too when its numbers
    /// moved with it, which is the ordinary case: the shape travels in the delta and the numbers in
    /// the statistics message, and the planner joins them.
    /// </summary>
    public IReadOnlyList<TableKey> StatisticsChangesSince(CatalogShape previous)
    {
        var moved = new List<TableKey>();
        foreach (var (key, numbers) in Statistics)
        {
            if (!previous.Statistics.TryGetValue(key, out var before)
                || !numbers.AsSpan().SequenceEqual(before))
            {
                moved.Add(key);
            }
        }

        return moved;
    }
}
