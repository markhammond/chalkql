using Chalk.Ir;

using Chalk.Entitlements;

namespace Chalk.Catalog;

/// <summary>
/// The named registry a host assembles and pushes to the planner (D9: "catalog", not "data lake").
/// Immutable for a given <see cref="Epoch"/>.
/// </summary>
public sealed class CatalogContext
{
    public required string ContextId { get; init; }
    
    /// <summary>The first schema is the default one; the rest are addressed <c>schema.table</c> (A4).</summary>
    public required IReadOnlyList<SchemaDescriptor> Schemas { get; init; }

    /// <summary>Bumped whenever the shape of any schema changes. Executors refuse plans from another epoch.</summary>
    public long Epoch { get; init; } = 0;

    /// <summary>
    /// How a join whose two sides live in different sources is planned (D104, M5). Produced by the
    /// host's <see cref="ICrossSourceJoinPolicy"/> at engine creation and on every catalog refresh,
    /// so changing it moves the epoch — which is right: it changes what every plan looks like. A
    /// request may still merge its own over it.
    /// </summary>
    public CrossSourceJoinPolicy JoinPolicy { get; init; } = CrossSourceJoinPolicy.Default;

    /// <summary>
    /// Associations between columns of two tables that no foreign key can state, because the two
    /// tables are in two schemas (D270 (c), <c>docs/design/45-typed-tenancy-surface.md</c> §3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A foreign key is one source's claim about a table of <em>its own</em> schema, so it lives on
    /// the table that holds it and is validated within the schema. An association's two ends are in
    /// two schemas and neither source owns it, which is why it hangs off the catalog — the registry
    /// the host assembles across its sources — rather than off either schema.
    /// </para>
    /// <para>
    /// <b>Declared, not verified.</b> It is the host's assertion, exactly as a declared foreign key
    /// is the source's own (ADR 0025's "verified is read as declared"): nothing reads either source
    /// to check it, and across two sources there is no transaction in which it could be checked.
    /// Empty — the default, and every catalog until a host declares one — adds no bytes.
    /// </para>
    /// </remarks>
    public IReadOnlyList<AssociationDescriptor> Associations { get; init; } = [];

    /// <summary>Finds a schema by name, case-insensitively (D15).</summary>
    public SchemaDescriptor? FindSchema(string name) =>
        Schemas.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves an unqualified table name in the default schema, or a qualified one anywhere.</summary>
    public (SchemaDescriptor Schema, TableDescriptor Table)? FindTable(string? schema, string table)
    {
        var candidates = schema is null ? Schemas.Take(1) : Schemas.Where(
            s => string.Equals(s.Name, schema, StringComparison.OrdinalIgnoreCase));
        foreach (var s in candidates)
        {
            var t = s.FindTable(table);
            if (t is not null)
            {
                return (s, t);
            }
        }

        return null;
    }
}

/// <summary>One source's tables, as that source describes them.</summary>
public sealed class SchemaDescriptor
{
    /// <summary>Identifies the <c>ISourceRuntime</c> that serves this schema.</summary>
    public required string SourceId { get; init; }

    /// <summary>The SQL schema name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the client scans this source or hands it native queries.</summary>
    public required SourceKind Kind { get; init; }

    /// <summary>SQL dialect for <c>RemoteQuery</c> generation. Null or empty for local sources.</summary>
    public string? Dialect { get; init; }

    /// <summary>What the source can execute itself (D82). The safe default is nothing.</summary>
    public SourceCapabilities Capabilities { get; init; } = SourceCapabilities.None;

    /// <summary>
    /// How this source spells and evaluates SQL (D82). Start from a <see cref="DialectProfiles"/>
    /// preset; the default claims nothing, which is what a source that pushes nothing needs.
    /// </summary>
    public DialectProfileDescriptor DialectProfile { get; init; } = DialectProfileDescriptor.None;

    /// <summary>
    /// What the planner charges for work against this schema's tables (D38). A table's own profile
    /// wins over this, and this wins over the planner's defaults; <see cref="CostProfile.Inherit"/>
    /// — every field zero — says nothing and inherits everything.
    /// </summary>
    public CostProfile CostProfile { get; init; } = CostProfile.Inherit;

    public required IReadOnlyList<TableDescriptor> Tables { get; init; }

    /// <summary>
    /// Functions this schema declares (D77). They are addressed exactly as tables are —
    /// <c>schema.name</c>, or bare in the default schema — and travel with the catalog, so declaring
    /// one moves the epoch.
    /// </summary>
    public IReadOnlyList<FunctionDescriptor> Functions { get; init; } = [];

    /// <summary>
    /// This source enforces its own row-level security and its rows are taken as already filtered, so
    /// no entitlement predicate is injected over its tables (D156). False — every source enforced —
    /// is the default. Column disclosures are unaffected: masking and the query-field rule are
    /// Chalk's, whatever the source filters.
    /// </summary>
    public bool TrustSourceRowSecurity { get; init; }

    /// <summary>Finds a table by name, case-insensitively (D15).</summary>
    public TableDescriptor? FindTable(string name) =>
        Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a function by name, case-insensitively.</summary>
    public FunctionDescriptor? FindFunction(string name) =>
        Functions.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One function the catalog declares, with the PostgreSQL <c>CREATE FUNCTION</c> properties that
/// have a counterpart in a federated engine (D77,
/// <c>docs/design/17-user-defined-functions.md</c> §1).
/// </summary>
public sealed class FunctionDescriptor
{
    /// <summary>Unqualified. The schema that declares it qualifies it.</summary>
    public required string Name { get; init; }

    public required FunctionKind Kind { get; init; }

    /// <summary>Named, in call order. An optional parameter carries the default it stands for.</summary>
    public IReadOnlyList<ParameterDescriptor> Parameters { get; init; } = [];

    /// <summary>Scalar and aggregate functions. Null for a table function.</summary>
    public ChalkType? ReturnType { get; init; }

    /// <summary>A table function's <c>RETURNS TABLE</c>. Empty for the other kinds.</summary>
    public IReadOnlyList<ColumnDescriptor> ReturnsTable { get; init; } = [];

    /// <summary>Foldable, once per execution, or per lane. Immutable when nothing is said.</summary>
    public Volatility Volatility { get; init; } = Volatility.Immutable;

    /// <summary>
    /// <c>RETURNS NULL ON NULL INPUT</c>: the result is nullable when any argument is, and the
    /// executor skips the NULL lanes rather than calling the implementation on them.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>Reserved for the security step: safe over a protected column.</summary>
    public bool Leakproof { get; init; }

    /// <summary>Per parameter, parallel to <see cref="Parameters"/>, or empty for "none of them".</summary>
    public IReadOnlyList<Monotonicity> Monotonicity { get; init; } = [];

    /// <summary>Per-row evaluation cost in cost-profile units. Zero means the planner's default.</summary>
    public double Cost { get; init; }

    /// <summary>A table function's estimated output rows. Zero means the planner's default.</summary>
    public long Rows { get; init; }

    /// <summary>Aggregates: usable over a frame.</summary>
    public bool Window { get; init; }

    /// <summary>Aggregates: takes <c>WITHIN GROUP (ORDER BY …)</c>.</summary>
    public bool Ordered { get; init; }

    /// <summary>Aggregates: accepts <c>IGNORE NULLS</c> / <c>RESPECT NULLS</c>.</summary>
    public bool NullTreatment { get; init; }

    /// <summary>
    /// Aggregates: the host's promise that the result reports the group as a whole and never one
    /// row's value — every field of a composite result included. Only such an aggregate may be named
    /// in a column's <c>AggregateOnlyFunctions</c>, where the group-size floor then guards it as it
    /// guards <c>COUNT</c> (D295). Nothing can check the promise; like <see cref="Leakproof"/>, it is
    /// recorded and relied on.
    /// </summary>
    public bool Population { get; init; }

    /// <summary>Where the implementation lives, which is what decides where it may run.</summary>
    public required FunctionBody Body { get; init; }
}

/// <summary>A declared parameter: a name, a type, and possibly a default.</summary>
public sealed class ParameterDescriptor
{
    public required string Name { get; init; }

    public required ChalkType Type { get; init; }

    /// <summary>Whether a call may omit it. An optional parameter must carry a default.</summary>
    public bool Optional { get; init; }

    /// <summary>
    /// The constant an omitted argument stands for, as a plain CLR value of <see cref="Type"/> —
    /// the same shape a column's declared <c>Min</c> and <c>Max</c> take. Required when
    /// <see cref="Optional"/>, and meaningless otherwise.
    /// </summary>
    public object? Default { get; init; }
}

/// <summary>
/// One of the three implementation kinds (D78). Sealed by an internal constructor: the planner acts
/// on exactly these three and a fourth would have nowhere to run.
/// </summary>
public abstract class FunctionBody
{
    private protected FunctionBody()
    {
    }
}

/// <summary>Inlined by the planner, so the call disappears and is pushable wherever its parts are.</summary>
public sealed class SqlFunctionBody : FunctionBody
{
    /// <summary>
    /// A scalar body is an expression over the parameter names; an aggregate body an expression over
    /// built-in aggregates of them; a table body a <c>SELECT</c> over the schema.
    /// </summary>
    public required string Text { get; init; }
}

/// <summary>Implemented in the host process. Never pushed, and never folded.</summary>
public sealed class ClientFunctionBody : FunctionBody
{
    /// <summary>The key the implementation was registered under. Null means the function's name.</summary>
    public string? Registration { get; init; }
}

/// <summary>Implemented by the declaring schema's source. Pushed there, executable nowhere else.</summary>
public sealed class NativeFunctionBody : FunctionBody
{
    /// <summary>How the source spells it in generated SQL. Null means the function's name.</summary>
    public string? DialectName { get; init; }
}

/// <summary>
/// A table and the statistics the cost model plans against. Real row counts, unique keys and
/// collations from the outset — that is the whole point of Chalk (rev 3 §6 M1).
/// </summary>
public sealed class TableDescriptor
{
    public required string Name { get; init; }

    public required IReadOnlyList<ColumnDescriptor> Columns { get; init; }

    /// <summary>Exact or estimated. Negative means unknown.</summary>
    public required long RowCount { get; init; }

    /// <summary>
    /// How much <see cref="RowCount"/> is worth (D36). <see cref="RowCountKind.Unspecified"/> is read
    /// as <see cref="RowCountKind.Estimate"/> when the count is non-negative, and as
    /// <see cref="RowCountKind.Unknown"/> when it is not.
    /// </summary>
    public RowCountKind RowCountKind { get; init; } = RowCountKind.Unspecified;

    /// <summary>Costs for this table, overriding its schema's and the planner's (D38).</summary>
    public CostProfile CostProfile { get; init; } = CostProfile.Inherit;

    public IReadOnlyList<UniqueKeyDescriptor> UniqueKeys { get; init; } = [];

    /// <summary>Orderings the table's scan order satisfies. This is what deletes sorts.</summary>
    public IReadOnlyList<CollationDescriptor> Collations { get; init; } = [];

    /// <summary>Declared indexes. Consulted from M2.</summary>
    public IReadOnlyList<IndexDescriptor> Indexes { get; init; } = [];

    /// <summary>
    /// Referential constraints to other tables in the same schema (F14). A foreign key bounds a
    /// join's output at the child's row count, so its shape is validated here; the claim itself is
    /// the source's word, as a declared statistic is.
    /// </summary>
    public IReadOnlyList<ForeignKeyDescriptor> ForeignKeys { get; init; } = [];

    /// <summary>
    /// Where this table's rows actually live, when they live in more than one place. A
    /// scan of a partitioned table becomes the union of its partitions, each in its own source,
    /// after the partitions a predicate on the partition column cannot match are dropped. Null means
    /// one table in one source, which is every table until a host says otherwise.
    /// </summary>
    public PartitioningDescriptor? Partitioning { get; init; }

    /// <summary>
    /// Which of this table's rows and columns the caller may see (D141, step 26). Null — the default,
    /// and every table until a host says otherwise — means every row and every column, adds no bytes
    /// to the catalog and installs no planner pass.
    /// </summary>
    public TableEntitlementDescriptor? Entitlement { get; init; }

    /// <summary>
    /// The host has said this table is deliberately unrestricted (D154). Only
    /// <see cref="CatalogOptions.RequireEntitlements"/> reads it — it is what lets that setting tell
    /// "declared public" from "nobody got round to it" — and it never reaches the sidecar.
    /// </summary>
    public bool IsPublic { get; init; }

    /// <summary>The index of a column by name, case-insensitively, or -1.</summary>
    public int IndexOfColumn(string name)
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}

public sealed class ColumnDescriptor
{
    public required string Name { get; init; }

    public required ChalkType Type { get; init; }

    /// <summary>
    /// What the source knows about this column's values (D36). Every field may be unknown, and
    /// <see cref="ColumnStatistics.Unknown"/> — the default — says nothing at all, which is a
    /// legitimate answer rather than an error.
    /// </summary>
    public ColumnStatistics Statistics { get; init; } = ColumnStatistics.Unknown;
}

/// <summary>
/// Per-column statistics the cost model plans against (D36, part of D5). The counts use -1 for
/// "unknown" and the values use null, so an adapter fills in what it knows and leaves the rest.
/// </summary>
public sealed class ColumnStatistics
{
    /// <summary>Nothing is known about this column.</summary>
    public static ColumnStatistics Unknown { get; } = new();

    /// <summary>How much of the rest is populated.</summary>
    public StatisticsLevel Level { get; init; } = StatisticsLevel.Unknown;

    /// <summary>Distinct non-null values, or -1.</summary>
    public long DistinctCount { get; init; } = -1;

    /// <summary>Rows whose value is NULL, or -1.</summary>
    public long NullCount { get; init; } = -1;

    /// <summary>The smallest non-null value, as the CLR value the column produces. Null = unknown.</summary>
    public object? Min { get; init; }

    /// <summary>The largest non-null value. Null = unknown.</summary>
    public object? Max { get; init; }

    /// <summary>Equi-height buckets, in ascending order of <see cref="HistogramBucket.Upper"/>.</summary>
    public IReadOnlyList<HistogramBucket> Histogram { get; init; } = [];

    /// <summary>Most common values, most frequent first. May be empty.</summary>
    public IReadOnlyList<FrequentValue> FrequentValues { get; init; } = [];
}

/// <summary>Rows whose value is &lt;= <see cref="Upper"/> and greater than the previous bucket's.</summary>
public sealed class HistogramBucket
{
    public required object? Upper { get; init; }

    public required long Count { get; init; }

    /// <summary>Distinct values in this bucket, or -1.</summary>
    public long DistinctCount { get; init; } = -1;
}

/// <summary>One of a column's most common values, and how often it occurs.</summary>
public sealed class FrequentValue
{
    public required object? Value { get; init; }

    public required long Count { get; init; }
}

/// <summary>
/// What the planner charges for the work a source does (D38). Zero on a field means "inherit": a
/// table's profile falls back to its schema's, and a schema's to the planner's defaults, field by
/// field. The defaults are documented on each property and live in the planner, not here, so a
/// cost-model revision does not need every host to redeploy.
/// </summary>
public sealed class CostProfile
{
    /// <summary>Says nothing; every cost comes from the next level.</summary>
    public static CostProfile Inherit { get; } = new();

    /// <summary>Reading one row by scan. Planner default 1.0.</summary>
    public double ScanRowCost { get; init; }

    /// <summary>One index range seek. Planner default 17.0.</summary>
    public double LookupSeekCost { get; init; }

    /// <summary>One row fetched by index — the random-access penalty. Planner default 4.0.</summary>
    public double LookupRowCost { get; init; }

    /// <summary>One round trip to a remote source (M4/M5). Planner default 1000.0.</summary>
    public double RemoteCallCost { get; init; }

    /// <summary>One row fetched from a remote source (M4/M5). Planner default 2.0.</summary>
    public double RemoteRowCost { get; init; }

    /// <summary>True when nothing is set, which is the common case.</summary>
    public bool IsInherit =>
        ScanRowCost == 0 && LookupSeekCost == 0 && LookupRowCost == 0
        && RemoteCallCost == 0 && RemoteRowCost == 0;
}

/// <summary>A set of columns whose values are unique across the table. Verified at build (D17).</summary>
public sealed class UniqueKeyDescriptor
{
    public required IReadOnlyList<int> Columns { get; init; }
}

/// <summary>
/// A referential constraint: every non-NULL value of <see cref="Columns"/> occurs in
/// <see cref="ParentTable"/>'s <see cref="ParentColumns"/> (F14). The planner turns it into a Calcite
/// <c>RelReferentialConstraint</c> and reads it when it estimates a join's cardinality.
/// </summary>
public sealed class ForeignKeyDescriptor
{
    /// <summary>A name for diagnostics. May be empty.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Column indexes in the declaring table, in key order.</summary>
    public required IReadOnlyList<int> Columns { get; init; }

    /// <summary>The parent table's name, in the same schema. Matched case-insensitively.</summary>
    public required string ParentTable { get; init; }

    /// <summary>Column indexes in the parent, parallel to <see cref="Columns"/>.</summary>
    public required IReadOnlyList<int> ParentColumns { get; init; }
}

/// <summary>
/// A declared association between one column of one table and one column of another, where the two
/// tables need not be in the same schema (D270 (c),
/// <c>docs/design/45-typed-tenancy-surface.md</c> §3).
/// </summary>
/// <remarks>
/// <para>
/// It says what a <see cref="ForeignKeyDescriptor"/> says — every non-NULL value of the "from"
/// column names a row of the "to" table, keyed by the "to" column — for a pair a foreign key cannot
/// reach, because a foreign key names a table of its own schema. A tenancy path's step resolves
/// through one exactly as through a foreign key: the far column must be a declared unique key of its
/// table and the two columns' types must agree, and the direction is checked rather than inferred.
/// </para>
/// <para>
/// The name is the word: an association is <b>declared</b>, and registration does not verify it
/// against either source. That is the same standing a declared foreign key has, and across two
/// sources there is no transaction in which more could be promised.
/// </para>
/// </remarks>
public sealed class AssociationDescriptor
{
    /// <summary>A name for diagnostics. May be empty.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The schema of the table holding the referencing column.</summary>
    public required string FromSchema { get; init; }

    /// <summary>The table holding the referencing column.</summary>
    public required string FromTable { get; init; }

    /// <summary>The referencing column.</summary>
    public required string FromColumn { get; init; }

    /// <summary>The schema of the referenced table.</summary>
    public required string ToSchema { get; init; }

    /// <summary>The referenced table.</summary>
    public required string ToTable { get; init; }

    /// <summary>The referenced column, which must be a declared unique key of its table.</summary>
    public required string ToColumn { get; init; }

    /// <summary>Whether two associations name the same pair of columns, case-insensitively.</summary>
    public static bool Same(AssociationDescriptor left, AssociationDescriptor right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return string.Equals(left.FromSchema, right.FromSchema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.FromTable, right.FromTable, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.FromColumn, right.FromColumn, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ToSchema, right.ToSchema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ToTable, right.ToTable, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ToColumn, right.ToColumn, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() =>
        $"{FromSchema}.{FromTable}.{FromColumn} -> {ToSchema}.{ToTable}.{ToColumn}";
}

/// <summary>
/// An ordering the table's rows are in. Both the direction and the null direction are explicit:
/// Calcite compares collations with <c>RelFieldCollation.equals</c>, which includes null direction,
/// so an unspecified one never satisfies an <c>ORDER BY</c> (V3).
/// </summary>
public sealed class CollationDescriptor
{
    public required IReadOnlyList<KeyOrder> Keys { get; init; }
}

/// <param name="Column">Index into the table's columns.</param>
/// <param name="Direction">Direction and null placement, both explicit.</param>
public readonly record struct KeyOrder(int Column, SortDirection Direction);

/// <summary>A declared index. M1 carries it through the catalog; M2 plans lookups against it.</summary>
public sealed class IndexDescriptor
{
    public required string Name { get; init; }

    public required IndexKind Kind { get; init; }

    /// <summary>Table column indexes, in key order.</summary>
    public required IReadOnlyList<int> Columns { get; init; }

    public bool Unique { get; init; }

    /// <summary>
    /// Key directions, parallel to <see cref="Columns"/>. Empty means every key is ascending with
    /// NULLs last (D15). Meaningless for <see cref="IndexKind.Hash"/>, which answers equality only.
    /// </summary>
    public IReadOnlyList<SortDirection> Directions { get; init; } = [];

    /// <summary>
    /// The table columns this index holds its own copy of, in the index's key order (D257,
    /// <c>docs/design/34-clustered-indexes.md</c> §2). Ascending, without duplicates, and always a
    /// superset of <see cref="Columns"/>. Empty — the default, and the only legal value for a kind
    /// other than <see cref="IndexKind.Clustered"/> — means every column.
    /// </summary>
    public IReadOnlyList<int> Covering { get; init; } = [];

    /// <summary>
    /// Which ranges this index can also be read from its last row to its first (D283). The default —
    /// and every index that says nothing — is forwards only.
    /// </summary>
    /// <remarks>
    /// A question about the structure rather than about the query: the built-in permutation and
    /// clustered indexes walk their slice either way, while a sorted structure a source only
    /// enumerates forwards can still be read from its top down to a lower bound and no further. The
    /// POCO builder reads it from the index instance, so a host declares it by implementing
    /// <c>IReversiblePocoIndex&lt;T&gt;</c> rather than by saying so twice.
    /// </remarks>
    public IndexReversal Reversal { get; init; } = IndexReversal.Unspecified;

    /// <summary>Whether a range of this shape can be read backwards.</summary>
    public bool CanReverse(bool openAbove) => Reversal switch
    {
        IndexReversal.Any => true,
        IndexReversal.OpenAbove => openAbove,
        _ => false,
    };

    /// <summary>The direction of key <paramref name="position"/>, defaulted when none was declared.</summary>
    public SortDirection DirectionAt(int position) =>
        position < Directions.Count ? Directions[position] : SortDirection.AscNullsLast;

    /// <summary>The key as (column, direction) pairs, which is what a collation is made of.</summary>
    public IReadOnlyList<KeyOrder> Key() =>
        [.. Columns.Select((column, position) => new KeyOrder(column, DirectionAt(position)))];

    /// <summary>
    /// Whether every column of <paramref name="projection"/> is in the covering set, which is what
    /// decides both the price of a lookup and whether the source can serve it from the copy. An empty
    /// <see cref="Covering"/> covers everything.
    /// </summary>
    public bool Covers(IReadOnlyList<int> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (Covering.Count == 0)
        {
            return true;
        }

        for (var i = 0; i < projection.Count; i++)
        {
            if (!Covering.Contains(projection[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// What a source can execute itself (D82). All-false / empty means nothing is pushed, which is the
/// safe default and what every source gets until it declares otherwise.
/// </summary>
/// <remarks>
/// Everything here is a claim the planner acts on, so nothing undeclared is ever pushed — and some
/// things are refused even when they are declared, because pushing them could change the answer
/// (D89): a string comparison under a non-binary collation, a sort whose null placement the source
/// will not honour, a comparison the source's precision would truncate, anything stable or volatile.
/// The conformance kit (<c>Chalk.Sources.Conformance</c>) is how an adapter author proves the claims
/// true.
/// </remarks>
public sealed class SourceCapabilities
{
    /// <summary>Nothing is pushable. The default for every source that does not say otherwise.</summary>
    public static SourceCapabilities None { get; } = new();

    /// <summary>
    /// Whether this source takes a query at all, and in which language.
    /// <see cref="QueryLanguage.None"/> — the default — means it can only be scanned.
    /// </summary>
    public QueryLanguage QueryLanguage { get; init; } = QueryLanguage.None;

    public IReadOnlyList<PredicateShape> PushablePredicates { get; init; } = [];

    /// <summary>
    /// Scalar functions this source implements with Chalk's semantics. A function not named here is
    /// not pushed; <see cref="UnsupportedFunctions"/> subtracts from what a dialect library adds.
    /// </summary>
    public IReadOnlyList<FunctionId> PushableFunctions { get; init; } = [];

    public IReadOnlyList<AggregateFunctionId> PushableAggregates { get; init; } = [];

    /// <summary>Names of the host's own functions this source runs natively (step 22).</summary>
    public IReadOnlyList<string> NativeFunctions { get; init; } = [];

    public bool SupportsProject { get; init; }

    public bool SupportsSort { get; init; }

    public bool SupportsLimit { get; init; }

    public bool SupportsOffset { get; init; }

    public bool SupportsDistinct { get; init; }

    public bool SupportsGroupBy { get; init; }

    public bool SupportsHaving { get; init; }

    public bool SupportsInnerJoin { get; init; }

    public bool SupportsOuterJoin { get; init; }

    public bool SupportsSemiAntiJoin { get; init; }

    /// <summary>Do not push a subtree whose estimated output exceeds this. Zero means unlimited.</summary>
    public long MaxPushdownRows { get; init; }

    /// <summary>The longest <c>IN</c> list the source accepts. Zero means none is ever pushed.</summary>
    public int MaxInList { get; init; }

    /// <summary>
    /// Whether a pushed query may carry dynamic parameters. When false, the converter inlines the
    /// bound values as literals — a different query text per parameter value.
    /// </summary>
    public bool SupportsParameters { get; init; }

    /// <summary>
    /// Whether the source can join a pushed query against inline rows — a <c>VALUES</c> list in a
    /// SQL source (M5). What makes <see cref="JoinStrategy.Broadcast"/> possible: the small side's
    /// rows are shipped into the source's own query and it does the join, in one call rather than
    /// one per key batch.
    /// </summary>
    public bool SupportsValuesJoin { get; init; }

    /// <summary>
    /// Whether a mask expression may be evaluated by this source (D153). False everywhere until a
    /// host says otherwise, and even then a table must opt in through
    /// <see cref="TableEntitlementDescriptor.PushMasks"/>: a pushed mask puts the raw value and the
    /// mask key in query text, which is the thing masking exists to prevent.
    /// </summary>
    public bool SupportsMaskPushdown { get; init; }

    /// <summary>
    /// Whether the source evaluates a conditional — a SQL <c>CASE</c>, or the IR's <c>IfThen</c> in
    /// a pushed plan (D273, F104). <b>True by default</b>, and on the wire the field carries
    /// explicit presence so that a catalog written before it existed reads as true: a <c>CASE</c>
    /// whose operands all push is SQL-92 and every dialect spells it the same way, so this is an
    /// opt-out for a source that cannot take one rather than an opt-in. Set it false for a source
    /// that cannot render or interpret a conditional.
    /// </summary>
    /// <remarks>
    /// It admits the <em>shape</em> and nothing else: every operand is still judged in its own
    /// right, so a conditional over a function the source did not declare, or over a string under a
    /// collation that is not Chalk's, stays above the boundary as it did before. A mask is a second
    /// question again — <see cref="TableEntitlementDescriptor.PushMasks"/> and
    /// <see cref="SupportsMaskPushdown"/> decide whether a sanitiser over a column this principal
    /// does not see plainly may travel at all (<c>docs/design/16-entitlements.md</c> §3.8).
    /// </remarks>
    public bool SupportsCase { get; init; } = true;

    /// <summary>
    /// Whether the source accepts a row-constructor <c>IN</c> list —
    /// <c>(a, b) IN ((?, ?), (?, ?))</c> (F50). What lets a lookup join carry a key set over more
    /// than one column, so a bound list of tuples above the fold ceiling reaches the source as key
    /// rows rather than fetching the table whole. Measured on 2026-09-11: DuckDB 1.5.1 and
    /// PostgreSQL 16 both accept it; SQLite's profile does not declare it.
    /// <see cref="MaxInList"/> sizes a call of them, counted in key rows.
    /// </summary>
    public bool SupportsRowValueInList { get; init; }

    public IReadOnlyList<FunctionId> UnsupportedFunctions { get; init; } = [];
}

/// <summary>
/// How a logical table's rows are spread over physical tables, possibly in different sources.
/// </summary>
/// <remarks>
/// Every partition has the logical table's row type and holds the rows whose partition column
/// matches it. Nothing in the engine assumes the partitions are disjoint or complete: they are a
/// claim the host makes, and a wrong one shows up as duplicate or missing rows exactly as a wrong
/// collation would. What Chalk does with them is prune — by predicate at planning, and by a bound
/// key set at execution — and fan out over what is left, in no particular order.
/// </remarks>
public sealed class PartitioningDescriptor
{
    /// <summary>The partition column, as an index into the logical table's columns.</summary>
    public required int PartitionColumn { get; init; }

    /// <summary>The partitions, in the order a scan fans out over them. At least one.</summary>
    public required IReadOnlyList<PartitionDescriptor> Partitions { get; init; }
}

/// <summary>One physical table holding the rows whose partition column matches it (D106).</summary>
public sealed class PartitionDescriptor
{
    /// <summary>The schema the physical table lives in — a source's own schema name.</summary>
    public required string Schema { get; init; }

    /// <summary>The physical table's name in that schema.</summary>
    public required string Table { get; init; }

    /// <summary>
    /// The single partition-column value this partition holds. Exactly one of this and
    /// <see cref="LowerBound"/>/<see cref="UpperBound"/> is set; a value partition is the one that
    /// can be pruned by a key set at execution.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>Whether <see cref="Value"/> is meant, including when it is null.</summary>
    public bool HasValue { get; init; }

    /// <summary>Inclusive lower bound of a range partition, or null for unbounded.</summary>
    public object? LowerBound { get; init; }

    /// <summary>Exclusive upper bound of a range partition, or null for unbounded.</summary>
    public object? UpperBound { get; init; }

    /// <summary>Exact or estimated rows in this partition. Negative means unknown.</summary>
    public long RowCount { get; init; } = -1;
}

/// <summary>How a cross-source join is executed (D103, §1). Names follow rev 3.</summary>
public enum JoinStrategy
{
    /// <summary>Let the policy or the cost model decide.</summary>
    Unspecified = 0,

    /// <summary>Fetch both sides through their boundaries and join locally. Always available.</summary>
    Local = 1,

    /// <summary>Stream the driving side; ask the other source once per batch of distinct keys.</summary>
    Lookup = 2,

    /// <summary>Ship the small side's rows into the other source's query as a <c>VALUES</c> join.</summary>
    Broadcast = 3,

    /// <summary>Plan both; decide at execution from the small side's measured distinct keys.</summary>
    Adaptive = 4,
}

/// <summary>
/// The cross-source join policy as data the planner reads (D104,
/// <c>docs/design/20-m5-federation.md</c> §2).
/// </summary>
/// <remarks>
/// Rev 3's <see cref="ICrossSourceJoinPolicy"/> survives as the object a host implements; what it
/// produces is this. Every limit reads <b>zero as "the planner's default"</b>, exactly as
/// <see cref="CostProfile"/> does, which is what lets a request override one field of one pair rule
/// without restating the rest.
/// </remarks>
public sealed class CrossSourceJoinPolicy
{
    /// <summary>The shipped default: adaptive everywhere, every limit at its default.</summary>
    public static CrossSourceJoinPolicy Default { get; } = new();

    /// <summary>What a pair no rule names gets. Unspecified is read as Adaptive.</summary>
    public JoinStrategy DefaultStrategy { get; init; } = JoinStrategy.Unspecified;

    /// <summary>The most rows a broadcast may ship. Zero means the planner's default, 10 000.</summary>
    public long BroadcastMaxRows { get; init; }

    /// <summary>
    /// How many IN-list calls an adaptive join's lookup branch may take before it takes the local
    /// branch instead. Zero means the planner's default, 64.
    /// </summary>
    public int LookupMaxCalls { get; init; }

    /// <summary>
    /// A plan that would fetch more than this into a local join fails at planning, naming the join
    /// and the estimate. Zero is unlimited, which is the default.
    /// </summary>
    public long LocalJoinMaxRows { get; init; }

    /// <summary>
    /// What an unknown remote cardinality is taken to be (D99). Zero means the planner's default,
    /// one million.
    /// </summary>
    public long UnknownRowCountAssumption { get; init; }

    /// <summary>
    /// Per ordered pair of sources. The first rule whose pair matches wins; an empty source id on a
    /// side matches every source there.
    /// </summary>
    public IReadOnlyList<SourcePairRule> Pairs { get; init; } = [];
}

/// <summary>Which strategies one ordered pair of sources may use, and which it prefers (D104).</summary>
public sealed class SourcePairRule
{
    /// <summary>The driving side's source id. Empty matches any.</summary>
    public string LeftSource { get; init; } = string.Empty;

    /// <summary>The looked-up side's source id. Empty matches any.</summary>
    public string RightSource { get; init; } = string.Empty;

    /// <summary>
    /// The strategies allowed for this pair. Empty allows every one.
    /// <see cref="JoinStrategy.Local"/> is always allowed whatever this says: it is the fallback
    /// that always exists, and a policy that forbade everything would make a legal query
    /// unplannable.
    /// </summary>
    public IReadOnlyList<JoinStrategy> Allowed { get; init; } = [];

    /// <summary>The strategy to use outright. Unspecified leaves it to cost.</summary>
    public JoinStrategy Preferred { get; init; } = JoinStrategy.Unspecified;

    /// <summary>Overrides the policy's broadcast ceiling for this pair. Zero inherits.</summary>
    public long BroadcastMaxRows { get; init; }
}

/// <summary>
/// What a host implements to replace the shipped cross-source join policy without forking (D104,
/// rev 3's M5 exit criterion).
/// </summary>
/// <remarks>
/// Called once at engine creation and again on every catalog refresh, and given the catalog it is
/// about to describe — so a policy may read the sources' names, their row counts and their declared
/// capabilities, and answer differently for a catalog with a data warehouse in it than for one
/// without. What it returns is data: the planner never calls back into it.
/// </remarks>
public interface ICrossSourceJoinPolicy
{
    /// <summary>The policy for this catalog.</summary>
    CrossSourceJoinPolicy Build(CatalogContext catalog);
}

/// <summary>
/// Everything the planner must know to generate SQL a source will accept, and to decide what may be
/// pushed into it (D82). Calcite's per-connection adapter properties, made per source.
/// </summary>
/// <remarks>
/// Every unset field reads conservatively: no quoting, case-sensitive matching, no null-ordering
/// clause, no binary collation, no approximation permitted, no implicit coercion trusted. Start from
/// a preset in <see cref="DialectProfiles"/> and override what differs.
/// </remarks>
public sealed class DialectProfileDescriptor
{
    /// <summary>The conservative profile: everything a source has not claimed.</summary>
    public static DialectProfileDescriptor None { get; } = new();

    /// <summary>
    /// Preset name selecting the Calcite <c>SqlDialect</c> the converter generates with:
    /// <c>sqlite</c>, <c>duckdb</c>, <c>postgresql</c>, <c>ansi</c>. Empty means ANSI. Any other
    /// name that is one of Calcite's own <c>SqlDialect.DatabaseProduct</c> constants — <c>oracle</c>,
    /// <c>mssql</c>, <c>big_query</c>, and so on, matched case-insensitively with <c>-</c> and
    /// <c>_</c> both accepted — is accepted too, as that product's own stock dialect, untuned
    /// (D249). A running sidecar's full list is <c>Chalk.Client.PlannerInfo.Dialects</c>.
    /// </summary>
    public string Dialect { get; init; } = string.Empty;

    public IdentifierQuoting Quoting { get; init; } = IdentifierQuoting.Unspecified;

    public IdentifierCasing QuotedCasing { get; init; } = IdentifierCasing.Unspecified;

    public IdentifierCasing UnquotedCasing { get; init; } = IdentifierCasing.Unspecified;

    public bool CaseSensitiveIdentifiers { get; init; }

    /// <summary>What SQL the <em>source</em> accepts. Distinct from the request's level (D34).</summary>
    public SqlConformance Conformance { get; init; } = SqlConformance.Unspecified;

    /// <summary>Function libraries the source implements natively.</summary>
    public IReadOnlyList<SqlLibrary> Libraries { get; init; } = [];

    /// <summary>Zero means "as Chalk's" — DECIMAL(38).</summary>
    public uint MaxNumericPrecision { get; init; }

    /// <summary>Zero means "as Chalk's" — TIMESTAMP(9).</summary>
    public uint MaxTimestampPrecision { get; init; }

    public bool HasBoolean { get; init; }

    /// <summary>The zone the source assumes for zone-less timestamps. Empty means UTC.</summary>
    public string TimeZone { get; init; } = string.Empty;

    public NullCollation DefaultNullCollation { get; init; } = NullCollation.Unspecified;

    /// <summary>Whether <c>NULLS FIRST</c> / <c>NULLS LAST</c> is accepted in <c>ORDER BY</c>.</summary>
    public bool SupportsNullOrderingClause { get; init; }

    /// <summary>
    /// How the source compares strings. Only <see cref="StringCollation.Binary"/> lets a string
    /// predicate, a string <c>ORDER BY</c> or a <c>DISTINCT</c> over strings be pushed (D89).
    /// </summary>
    public StringCollation StringCollation { get; init; } = StringCollation.Unspecified;

    public bool ApproximateDecimal { get; init; }

    public bool ApproximateDistinctCount { get; init; }

    public bool ApproximateTopN { get; init; }

    /// <summary>
    /// Whether the source's implicit coercions match the ones Chalk applied at validation. False —
    /// the default — makes the converter emit an explicit cast wherever a type changes.
    /// </summary>
    public bool ImplicitCoercionMatches { get; init; }

    /// <summary>
    /// How this source's driver spells a bound parameter (§3). The planner always generates the
    /// positional <c>?</c> Calcite's writer emits; the adapter rewrites it into this style before
    /// binding.
    /// </summary>
    public ParameterPlaceholder ParameterPlaceholder { get; init; } = ParameterPlaceholder.Unspecified;

    /// <summary>
    /// Whether the engine types values per row rather than per column — SQLite's storage classes.
    /// The ADO.NET source reads such a provider through its boxed path, where every value is checked,
    /// rather than through the typed getters a per-column type makes safe; a typed getter on SQLite
    /// would coerce a stray text cell to a number silently, which is the one thing the contract
    /// check exists to refuse.
    /// </summary>
    public bool DynamicallyTyped { get; init; }
}
