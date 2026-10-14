using Google.Protobuf;
using CatalogChange = Chalk.Catalog.CatalogChange;
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
/// <b>A table's shape carries its schema's structural properties</b> (F145): the source kind, the
/// dialect and its profile, the capabilities, the row-level-security trust and the zone. Each decides
/// whether a plan over the table is still right — a plan pushing a shape the source no longer
/// declares, or skipping a predicate the source is no longer trusted to enforce, is wrong rather than
/// merely stale — so a change to any of them moves every table of the schema and strands the plans
/// that read them. The schema's cost profile is a preference like the join policy, and its functions
/// are checked on their own at registration, so neither is carried.
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
    /// <summary>What <see cref="Policies"/> is the bytes of, kept to build the next when only the join policy moves.</summary>
    private readonly Chalk.Ir.CatalogContext _policies;

    /// <summary>Each schema's structural properties, in the catalog's order, as the bytes a table's shape starts with.</summary>
    private readonly byte[][] _structurals;

    /// <summary>The catalog this shape was taken of, when it was taken of one rather than of a wire message.</summary>
    private readonly CatalogContext? _described;

    private CatalogShape(
        IReadOnlyDictionary<TableKey, byte[]> tables,
        IReadOnlyDictionary<TableKey, TableNumbers> statistics,
        Chalk.Ir.CatalogContext policies,
        byte[] policyBytes,
        byte[][] structurals,
        CatalogContext? described)
    {
        Tables = tables;
        Statistics = statistics;
        _policies = policies;
        Policies = policyBytes;
        _structurals = structurals;
        _described = described;
    }

    /// <summary>Every table's own shape, keyed by the triple that addresses it.</summary>
    public IReadOnlyDictionary<TableKey, byte[]> Tables { get; }

    /// <summary>
    /// Every table's <em>numbers</em> — the row count, its kind and the columns' statistics — as what
    /// says whether a refresh moved them. What the shape strikes out, this keeps.
    /// </summary>
    public IReadOnlyDictionary<TableKey, TableNumbers> Statistics { get; }

    /// <summary>
    /// The bytes of what decides how the <em>next</em> plan is built and nothing about whether this
    /// one is still right (F145): the cross-source join policy and each schema's cost profile. Struck
    /// out of every shape above, and compared on their own, so a refresh that moves only these is
    /// registered with the planner — which plans under them — without stranding a single plan.
    /// </summary>
    public byte[] Policies { get; }

    public static CatalogShape Of(CatalogContext catalog) =>
        Of(Chalk.Catalog.CatalogSerialization.ToProto(catalog), owned: true, catalog);

    /// <summary>The same from the wire message, which is what the engine already has in hand.</summary>
    public static CatalogShape Of(Chalk.Ir.CatalogContext wire) => Of(wire, owned: false, described: null);

    /// <summary>
    /// The shape of <paramref name="wire"/>. Numbers are struck out of its tables in place, so a
    /// message the caller holds — one it may be about to register — is cloned first, and one converted
    /// here for the purpose (<paramref name="owned"/>) is struck as it is.
    /// </summary>
    private static CatalogShape Of(Chalk.Ir.CatalogContext wire, bool owned, CatalogContext? described)
    {
        var message = owned ? wire : wire.Clone();
        var policies = PoliciesOf(message);
        var structurals = new byte[message.Schemas.Count][];
        var tables = new Dictionary<TableKey, byte[]>();
        var statistics = new Dictionary<TableKey, TableNumbers>();
        for (var s = 0; s < message.Schemas.Count; s++)
        {
            var schema = message.Schemas[s];
            var structural = StructuralOf(schema).ToByteArray();
            structurals[s] = structural;
            foreach (var table in schema.Tables)
            {
                var key = new TableKey(schema.SourceId, schema.Name, table.Name);
                statistics[key] = NumbersOf(table);
                tables[key] = ShapeOf(structural, Struck(table));
            }
        }

        return new CatalogShape(tables, statistics, policies, policies.ToByteArray(), structurals, described);
    }

    /// <summary>
    /// The shape of <paramref name="next"/>, taken from this one where next shares the parts of
    /// <paramref name="previous"/>, the catalog this shape was taken of (F205): a table that is the
    /// descriptor it was keeps its shape and numbers, one whose row count alone moved keeps its shape
    /// and the rest of its numbers, and one whose columns moved is converted alone. Null — the shape
    /// then to be taken whole — when this shape was not taken of <paramref name="previous"/>, when
    /// more than tables moved, or when a table converted alone turns out to have changed its shape.
    /// </summary>
    public CatalogShape? Next(CatalogContext previous, CatalogContext next) =>
        Next(previous, next, CatalogChange.Between(previous, next));

    /// <summary>The same, with what moved already read.</summary>
    public CatalogShape? Next(CatalogContext previous, CatalogContext next, CatalogChange change)
    {
        if (change.Structural || _described is null || !ReferenceEquals(_described, previous))
        {
            return null;
        }

        Dictionary<TableKey, TableNumbers>? statistics = null;
        foreach (var table in change.Tables)
        {
            var schema = next.Schemas[table.Schema];
            var key = new TableKey(schema.SourceId, schema.Name, table.After.Name);
            if (!Statistics.TryGetValue(key, out var held) || !Tables.TryGetValue(key, out var shape))
            {
                return null;
            }

            TableNumbers numbers;
            if (table.CountOnly)
            {
                numbers = held.WithRowCount(table.After.RowCount);
            }
            else
            {
                var wire = Chalk.Catalog.CatalogProtoMapping.ToProto(table.After);
                numbers = NumbersOf(wire);
                if (!ShapeOf(_structurals[table.Schema], Struck(wire)).AsSpan().SequenceEqual(shape))
                {
                    return null;
                }
            }

            (statistics ??= new Dictionary<TableKey, TableNumbers>(Statistics))[key] = numbers;
        }

        var policies = _policies;
        var policyBytes = Policies;
        if (change.JoinPolicyMoved)
        {
            policies = _policies.Clone();
            policies.JoinPolicy = next.JoinPolicy is { } policy
                ? Chalk.Catalog.CatalogProtoMapping.ToProto(policy)
                : null;
            policyBytes = policies.ToByteArray();
        }

        return new CatalogShape(Tables, statistics ?? Statistics, policies, policyBytes, _structurals, next);
    }

    /// <summary>
    /// The join policy and each schema's cost profile: what <see cref="Policies"/> holds. Shares the
    /// message's parts, which this shape owns from here on.
    /// </summary>
    private static Chalk.Ir.CatalogContext PoliciesOf(Chalk.Ir.CatalogContext message)
    {
        var policies = new Chalk.Ir.CatalogContext();
        if (message.JoinPolicy is not null)
        {
            policies.JoinPolicy = message.JoinPolicy;
        }

        foreach (var schema in message.Schemas)
        {
            if (schema.CostProfile is not null)
            {
                policies.Schemas.Add(new Chalk.Ir.Schema
                {
                    SourceId = schema.SourceId,
                    Name = schema.Name,
                    CostProfile = schema.CostProfile,
                });
            }
        }

        return policies;
    }

    /// <summary>
    /// The schema's structural properties, and nothing else of it: what a table's shape carries beside
    /// its own bytes (F145). No tables, no functions, no cost profile.
    /// </summary>
    private static Chalk.Ir.Schema StructuralOf(Chalk.Ir.Schema schema)
    {
        var structural = new Chalk.Ir.Schema
        {
            SourceId = schema.SourceId,
            Name = schema.Name,
            Kind = schema.Kind,
            Dialect = schema.Dialect,
            TrustSourceRowLevelSecurity = schema.TrustSourceRowLevelSecurity,
            Zone = schema.Zone,
        };
        if (schema.Capabilities is not null)
        {
            structural.Capabilities = schema.Capabilities;
        }

        if (schema.DialectProfile is not null)
        {
            structural.DialectProfile = schema.DialectProfile;
        }

        return structural;
    }

    /// <summary>
    /// A table's numbers: its row count, and the bytes the rest are equal when they share — the
    /// count's kind and each column's statistics in column order. A table whose statistics are all
    /// absent hashes to the same bytes as another such table, which is what "nothing moved" has to
    /// mean. Read before the table is struck, and shares its statistics rather than copying them.
    /// </summary>
    private static TableNumbers NumbersOf(Chalk.Ir.Table table)
    {
        var numbers = new Chalk.Ir.Table
        {
            RowCountKind = table.RowCountKind,
        };

        foreach (var column in table.Columns)
        {
            numbers.Columns.Add(new Chalk.Ir.Column
            {
                Name = column.Name,
                Statistics = column.Statistics,
            });
        }

        return new TableNumbers(table.RowCount, numbers.ToByteArray());
    }

    /// <summary>The table with its numbers struck out, in place: what is left is its shape.</summary>
    private static Chalk.Ir.Table Struck(Chalk.Ir.Table table)
    {
        table.RowCount = 0;
        table.RowCountKind = RowCountKind.Unspecified;
        foreach (var column in table.Columns)
        {
            column.Statistics = null;
        }

        return table;
    }

    /// <summary>
    /// The bytes of a schema holding this table alone: the schema's structural properties, then the
    /// table as the schema's <c>tables</c> field. That is the order the message would not write its
    /// fields in, and it parses to the same schema; these bytes are compared only with each other.
    /// </summary>
    private static byte[] ShapeOf(byte[] structural, Chalk.Ir.Table table)
    {
        var holder = new Chalk.Ir.Schema();
        holder.Tables.Add(table);
        var bytes = new byte[structural.Length + holder.CalculateSize()];
        structural.CopyTo(bytes, 0);
        holder.WriteTo(bytes.AsSpan(structural.Length));
        return bytes;
    }

    /// <summary>
    /// Every table whose shape differs between two catalogs, and every table one has that the other
    /// does not. Order is the later catalog's own, so a registration's delta lists its tables in the
    /// order the catalog declares them.
    /// </summary>
    public IReadOnlyList<TableKey> ShapeChangesSince(CatalogShape previous)
    {
        if (ReferenceEquals(Tables, previous.Tables))
        {
            return [];
        }

        var changed = new List<TableKey>();
        foreach (var (key, shape) in Tables)
        {
            if (!previous.Tables.TryGetValue(key, out var before)
                || (!ReferenceEquals(shape, before) && !shape.AsSpan().SequenceEqual(before)))
            {
                changed.Add(key);
            }
        }

        return changed;
    }

    /// <summary>Every table <paramref name="previous"/> had that this one does not.</summary>
    /// <summary>Whether the join policy or a cost profile differs from <paramref name="previous"/>'s.</summary>
    public bool PoliciesChangedSince(CatalogShape previous) =>
        !ReferenceEquals(Policies, previous.Policies) && !Policies.AsSpan().SequenceEqual(previous.Policies);

    public IReadOnlyList<TableKey> RemovedSince(CatalogShape previous)
    {
        if (ReferenceEquals(Tables, previous.Tables))
        {
            return [];
        }

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
    /// Every table whose numbers moved (<see cref="TableNumbers.MovedFrom"/>). A table whose shape
    /// changed is in here too when its numbers moved with it, which is the ordinary case: the shape
    /// travels in the delta and the numbers in the statistics message, and the planner joins them.
    /// </summary>
    public IReadOnlyList<TableKey> StatisticsChangesSince(CatalogShape previous) =>
        StatisticsChangesSince(previous.Statistics);

    /// <summary>
    /// The same against numbers kept on their own: the ones the planner holds, which a refresh that
    /// deferred its planning has not sent (design 69 §4).
    /// </summary>
    public IReadOnlyList<TableKey> StatisticsChangesSince(IReadOnlyDictionary<TableKey, TableNumbers> previous)
    {
        var moved = new List<TableKey>();
        foreach (var (key, numbers) in Statistics)
        {
            if (!previous.TryGetValue(key, out var before) || numbers.MovedFrom(before))
            {
                moved.Add(key);
            }
        }

        return moved;
    }

    /// <summary>
    /// <paramref name="held"/> with the numbers of <paramref name="tables"/> taken from this shape:
    /// what the planner holds once those tables' numbers have been sent to it.
    /// </summary>
    public IReadOnlyDictionary<TableKey, TableNumbers> NumbersSent(
        IReadOnlyDictionary<TableKey, TableNumbers> held, IReadOnlyCollection<TableKey> tables)
    {
        var merged = new Dictionary<TableKey, TableNumbers>(held);
        foreach (var table in tables)
        {
            if (Statistics.TryGetValue(table, out var numbers))
            {
                merged[table] = numbers;
            }
        }

        return merged;
    }
}

/// <summary>
/// A table's numbers as the planner is sent them (D351): the row count on its own, and the rest —
/// the count's kind and each column's statistics — as the bytes they are equal when they share.
/// </summary>
/// <remarks>
/// The row count is apart because it moves with every append and the rest do not: a source that keeps
/// its column statistics until a tenth of a table's rows have moved, as a POCO table does unless told
/// otherwise, moves only the count in between. Sending the planner every such count would send a
/// statistics message, and plan again in the background, at every refresh, for a number a plan's
/// cost hardly turns on. So a count is sent once it has drifted by <see cref="RowCountDrift"/> from
/// the one the planner holds, and the catalog the host reads keeps it exact at every refresh.
/// </remarks>
internal sealed class TableNumbers(long rowCount, byte[] rest)
{
    /// <summary>
    /// How far a row count may drift, either way, from the one the planner holds before it is sent
    /// again: a tenth of the count held, the same tenth a POCO table's statistics are kept for.
    /// </summary>
    public const double RowCountDrift = 0.1;

    /// <summary>Rows in the table; -1 when unknown.</summary>
    public long RowCount { get; } = rowCount;

    /// <summary>The count's kind and the columns' statistics, as bytes.</summary>
    public byte[] Rest { get; } = rest;

    /// <summary>These numbers with another row count: a table whose columns are the ones they were.</summary>
    public TableNumbers WithRowCount(long rowCount) => rowCount == RowCount ? this : new TableNumbers(rowCount, Rest);

    /// <summary>
    /// Whether these numbers have moved from <paramref name="held"/>: any of the rest by any amount,
    /// or the row count by <see cref="RowCountDrift"/> of the count held or more. A count that is or
    /// was unknown, or was zero, has moved by any change.
    /// </summary>
    public bool MovedFrom(TableNumbers held)
    {
        if (!Rest.AsSpan().SequenceEqual(held.Rest))
        {
            return true;
        }

        if (RowCount == held.RowCount)
        {
            return false;
        }

        if (RowCount < 0 || held.RowCount <= 0)
        {
            return true;
        }

        return Math.Abs(RowCount - held.RowCount) >= held.RowCount * RowCountDrift;
    }
}
