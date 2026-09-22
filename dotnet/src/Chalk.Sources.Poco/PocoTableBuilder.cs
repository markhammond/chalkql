using System.Linq.Expressions;
using System.Numerics;
using System.Runtime.CompilerServices;
using Chalk.Catalog;
using Chalk.Entitlements;
using Array = System.Array;
using IndexKind = Chalk.Ir.IndexKind;
using SortDirection = Chalk.Ir.SortDirection;
using StatisticsLevel = Chalk.Ir.StatisticsLevel;

namespace Chalk.Sources.Poco;

/// <summary>
/// Configures one registered collection: which members become columns, what they are called, the
/// orderings the collection is already in, and the keys that are unique in it
/// (<c>docs/design/04-client.md</c> §5.1).
/// </summary>
/// <remarks>
/// Everything declared here is a claim the planner will act on — a collation deletes a Sort, a unique
/// key deletes an Aggregate — so <see cref="Verify(bool)"/> is on by default (D17).
/// </remarks>
public sealed class PocoTableBuilder<T>
{
    /// <summary>
    /// A host index declaration whose catalog descriptor is resolved after POCO column inference.
    /// The resolver is evaluated once at Build(); the factory is then retained and invoked once per
    /// snapshot by <see cref="PocoSnapshotFactory{T}"/>.
    /// </summary>
    private sealed record HostIndexDeclaration(
        Func<Dictionary<string, int>, IndexDescriptor> ResolveDescriptor,
        Func<
            IndexDescriptor,
            IReadOnlyCollection<T>,
            IPocoIndex<T>> Factory);
    
    private readonly string _table;
    private readonly Dictionary<string, ColumnOverride> _overrides = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
    private readonly List<ComputedColumn> _computed = [];
    private readonly List<List<KeyDeclaration>> _collations = [];
    private readonly List<string[]> _uniqueKeys = [];
    private readonly List<ForeignKeyDeclaration> _foreignKeys = [];
    private readonly List<IndexDeclaration> _indexes = [];
    private readonly List<HostIndexDeclaration> _hostIndexDeclarations = [];
    private readonly Dictionary<string, ColumnStatistics> _columnStatistics = new(StringComparer.Ordinal);
    private bool _verify = true;
    private StatisticsLevel _statisticsLevel = StatisticsLevel.Basic;
    private double _refreshWhenGrownBy;
    private int _histogramBuckets = 64;
    private Func<TableStatistics>? _statistics;
    
    private readonly PermutationIndexScratch _indexScratch =
        new();

    internal PocoTableBuilder(string table) => _table = table;

    /// <summary>Includes a member with an overridden name or logical type.</summary>
    /// <param name="member">A direct property or field access, e.g. <c>row =&gt; row.Symbol</c>.</param>
    /// <param name="name">The SQL column name. Null keeps the naming policy's answer.</param>
    /// <param name="type">
    /// A logical type that differs from the inferred one in nullability, precision or scale. It may
    /// not change the kind; use the projection overload for that.
    /// </param>
    public PocoTableBuilder<T> Column<TProp>(
        Expression<Func<T, TProp>> member, string? name = null, ChalkType? type = null)
    {
        ArgumentNullException.ThrowIfNull(member);
        var resolved = PocoMembers.Resolve(member, _table, "Column(member, …)");
        _overrides[resolved.Name] = new ColumnOverride(name, type);
        return this;
    }

    /// <summary>Adds a computed column, evaluated once per row inside the compiled chunk loop.</summary>
    public PocoTableBuilder<T> Column<TOut>(
        string name, Expression<Func<T, TOut>> projection, ChalkType? type = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(projection);
        _computed.Add(new ComputedColumn(name, projection, typeof(TOut), type));
        return this;
    }

    /// <summary>Leaves a member out of the table.</summary>
    public PocoTableBuilder<T> Ignore<TProp>(Expression<Func<T, TProp>> member)
    {
        ArgumentNullException.ThrowIfNull(member);
        _ignored.Add(PocoMembers.Resolve(member, _table, "Ignore(member)").Name);
        return this;
    }

    /// <summary>
    /// Declares that the collection is already in this order. Starts a new collation; chain
    /// <see cref="ThenBy{TKey}"/> for further keys.
    /// </summary>
    public PocoTableBuilder<T> OrderedBy<TKey>(
        Expression<Func<T, TKey>> key, SortDirection direction = SortDirection.AscNullsLast)
    {
        ArgumentNullException.ThrowIfNull(key);
        _collations.Add([Declare(key, direction, nameof(OrderedBy))]);
        return this;
    }

    /// <summary>Adds a key to the collation the last <see cref="OrderedBy{TKey}"/> started.</summary>
    public PocoTableBuilder<T> ThenBy<TKey>(
        Expression<Func<T, TKey>> key, SortDirection direction = SortDirection.AscNullsLast)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_collations.Count == 0)
        {
            throw new InvalidOperationException(
                $"Table '{_table}': ThenBy must follow OrderedBy, which starts the collation it extends.");
        }

        _collations[^1].Add(Declare(key, direction, nameof(ThenBy)));
        return this;
    }

    /// <summary>Declares that these columns together identify a row.</summary>
    public PocoTableBuilder<T> UniqueKey(params Expression<Func<T, object?>>[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Length == 0)
        {
            throw new ArgumentException($"Table '{_table}': a unique key needs at least one column.", nameof(keys));
        }

        _uniqueKeys.Add(Array.ConvertAll(keys, k => PocoMembers.Resolve(k, _table, "UniqueKey(keys)").Name));
        return this;
    }

    /// <summary>
    /// Starts a referential constraint from this table's <paramref name="column"/>. Finish it with
    /// <see cref="PocoForeignKeyBuilder{T}.References{TParent}(Expression{Func{TParent, object?}}, bool)"/>:
    /// <c>.ForeignKey(x =&gt; x.OrgId).References&lt;Org&gt;(o =&gt; o.Id, verify: true)</c>.
    /// </summary>
    /// <remarks>
    /// The parent's type carries the type argument, so this half needs none — which is what makes
    /// the whole declaration read left to right. The planner reads the finished key as a Calcite
    /// referential constraint, as an upper bound on what a join of the two tables produces, and —
    /// since step 21 — as the fact `PROJECT_JOIN_REMOVE` and `AGGREGATE_JOIN_REMOVE` need before
    /// they will delete a join.
    /// </remarks>
    public PocoForeignKeyBuilder<T> ForeignKey(Expression<Func<T, object?>> column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return new PocoForeignKeyBuilder<T>(this, [column]);
    }

    /// <summary>The composite form: <c>.ForeignKey(x =&gt; x.A, x =&gt; x.B).References…</c>.</summary>
    public PocoForeignKeyBuilder<T> ForeignKey(params Expression<Func<T, object?>>[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Length == 0)
        {
            throw new ArgumentException(
                $"Table '{_table}': a foreign key needs at least one column.", nameof(columns));
        }

        return new PocoForeignKeyBuilder<T>(this, columns);
    }

    /// <summary>
    /// Declares that every non-NULL value of <paramref name="column"/> occurs in
    /// <paramref name="to"/>'s <paramref name="parentColumn"/> (F14). The shape is always checked at
    /// <c>Build()</c> — the columns exist, the parent is registered, the arities match and the kinds
    /// match. Whether the <em>claim</em> is checked against the data is
    /// <paramref name="verify"/>'s business, and it has no default because a host should have to say.
    /// </summary>
    /// <param name="column">A direct property or field access on this table's row.</param>
    /// <param name="to">The parent table's registered name, in the same source.</param>
    /// <param name="parentColumn">The parent's column or member name.</param>
    /// <param name="verify">
    /// <c>true</c> probes every non-NULL child key against the parent's key set at <c>Build()</c>
    /// and raises <c>CatalogVerificationException</c> naming the first orphan. <c>false</c> performs
    /// no integrity check at all: Chalk takes the host's word, and a foreign key that does not hold
    /// becomes a silent wrong answer, because the planner deletes joins on the strength of it.
    /// </param>
    public PocoTableBuilder<T> ForeignKey(
        Expression<Func<T, object?>> column, string to, string parentColumn, bool verify) =>
        ForeignKey([column], to, [parentColumn], verify);

    /// <summary>
    /// The typed spelling: <c>.ForeignKey&lt;Org&gt;(x =&gt; x.OrgId, to: "orgs",
    /// x =&gt; x.Id, verify: true)</c>. The parent's type is named once so its member lambda
    /// compiles; the rest is
    /// <see cref="ForeignKey(Expression{Func{T, object?}}, string, string, bool)"/>.
    /// </summary>
    public PocoTableBuilder<T> ForeignKey<TParent>(
        Expression<Func<T, object?>> column,
        string to,
        Expression<Func<TParent, object?>> parentColumn,
        bool verify)
    {
        ArgumentNullException.ThrowIfNull(parentColumn);
        return ForeignKey(
            [column],
            to,
            [PocoMembers.Resolve(parentColumn, _table, "ForeignKey(…, parent)").Name],
            verify);
    }

    /// <summary>The composite form: the two key column lists are paired position by position.</summary>
    public PocoTableBuilder<T> ForeignKey(
        IReadOnlyList<Expression<Func<T, object?>>> columns,
        string to,
        IReadOnlyList<string> parentColumns,
        bool verify)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        return Declare(columns, to, parentType: null, parentColumns, verify);
    }

    /// <summary>The one place a foreign-key declaration is recorded, whichever spelling made it.</summary>
    internal PocoTableBuilder<T> Declare(
        IReadOnlyList<Expression<Func<T, object?>>> columns,
        string? parentTable,
        Type? parentType,
        IReadOnlyList<string> parentColumns,
        bool verify)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(parentColumns);
        if (columns.Count == 0)
        {
            throw new ArgumentException(
                $"Table '{_table}': a foreign key needs at least one column.", nameof(columns));
        }

        if (columns.Count != parentColumns.Count)
        {
            throw new ArgumentException(
                $"Table '{_table}': the foreign key names {columns.Count} column(s) and "
                + $"{parentColumns.Count} parent column(s); they are paired one for one.",
                nameof(parentColumns));
        }

        _foreignKeys.Add(new ForeignKeyDeclaration(
            [.. columns.Select(c => PocoMembers.Resolve(c, _table, "ForeignKey(columns, …)").Name)],
            parentTable,
            parentType,
            [.. parentColumns],
            verify));
        return this;
    }

    /// <summary>Turns the D17 check at <c>Build()</c> off. Only for hosts that guarantee the invariants.</summary>
    public PocoTableBuilder<T> Verify(bool verify)
    {
        _verify = verify;
        return this;
    }

    /// <summary>
    /// Declares an ordered index over these columns, ascending with NULLs last (D15), named
    /// <c>ix_&lt;table&gt;_&lt;columns&gt;</c>. Chalk builds it at <c>Build()</c>: one <c>int</c> per
    /// row, or none at all when a declared collation already puts the rows in this order (D35).
    /// </summary>
    public PocoTableBuilder<T> Index(params Expression<Func<T, object?>>[] keys) =>
        Index(name: null, IndexKind.Ordered, unique: false, keys);

    /// <summary>As <see cref="Index(Expression{Func{T, object?}}[])"/>, and the key identifies a row.</summary>
    /// <remarks>Verified while sorting: adjacent equal keys name the two rows that collide.</remarks>
    public PocoTableBuilder<T> UniqueIndex(params Expression<Func<T, object?>>[] keys) =>
        Index(name: null, IndexKind.Ordered, unique: true, keys);

    /// <summary>An index with an explicit name, kind and uniqueness.</summary>
    /// <param name="name">Null takes the <c>ix_&lt;table&gt;_&lt;columns&gt;</c> default.</param>
    public PocoTableBuilder<T> Index(
        string? name, IndexKind kind, bool unique, params Expression<Func<T, object?>>[] keys) =>
        Index(name, kind, unique, directions: [], keys);

    /// <summary>
    /// The full form: an index whose keys are not all ascending-nulls-last. Pass one direction per
    /// key, or none at all for the default.
    /// </summary>
    public PocoTableBuilder<T> Index(
        string? name,
        IndexKind kind,
        bool unique,
        IReadOnlyList<SortDirection> directions,
        params Expression<Func<T, object?>>[] keys)
    {
        ArgumentNullException.ThrowIfNull(directions);
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Length == 0)
        {
            throw new ArgumentException($"Table '{_table}': an index needs at least one column.", nameof(keys));
        }

        if (kind == IndexKind.Unspecified)
        {
            throw new ArgumentException(
                $"Table '{_table}': an index needs a kind; ORDERED answers ranges, HASH only equality.",
                nameof(kind));
        }

        if (directions.Count != 0 && directions.Count != keys.Length)
        {
            throw new ArgumentException(
                $"Table '{_table}': {directions.Count} direction(s) were given for {keys.Length} key "
                + "column(s); give one per column or none at all.",
                nameof(directions));
        }

        foreach (var direction in directions)
        {
            if (direction == SortDirection.Unspecified)
            {
                throw new ArgumentException(
                    "An index key needs an explicit direction; leave the directions empty for the "
                    + "ascending-nulls-last default.",
                    nameof(directions));
            }
        }

        _indexes.Add(new IndexDeclaration(
            name,
            kind,
            unique,
            [.. directions],
            [.. keys.Select(k => PocoMembers.Resolve(k, _table, "Index(keys)").Name)],
            Covering: null));
        return this;
    }
    
    public PocoTableBuilder<T> Index(
        string name, IndexKind kind, bool unique, 
        Func<IndexDescriptor, IReadOnlyCollection<T>, IPocoIndex<T>> factory,
        params Expression<Func<T, object?>>[] keys) =>
        Index(
            name,
            kind,
            unique,
            directions: [],
            factory,
            keys);

    /// <summary>
    /// Registers a host index whose key columns are named as POCO members but whose storage and
    /// lookup implementation belong to the host. Member names are resolved to catalog column
    /// ordinals at <c>Build()</c>; <paramref name="factory"/> is then called once per snapshot.
    /// </summary>
    public PocoTableBuilder<T> Index(
        string name, IndexKind kind, bool unique,
        IReadOnlyList<SortDirection> directions,
        Func<IndexDescriptor, IReadOnlyCollection<T>, IPocoIndex<T>> factory,
        params Expression<Func<T, object?>>[] keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(directions);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Length == 0)
        {
            throw new ArgumentException(
                $"Table '{_table}': an index needs at least one column.",
                nameof(keys));
        }

        if (kind == IndexKind.Unspecified)
        {
            throw new ArgumentException(
                $"Table '{_table}': an index needs a kind; ORDERED answers ranges, HASH only equality.",
                nameof(kind));
        }

        if (directions.Count != 0 && directions.Count != keys.Length)
        {
            throw new ArgumentException(
                $"Table '{_table}': {directions.Count} direction(s) were given for {keys.Length} key "
                + "column(s); give one per column or none at all.",
                nameof(directions));
        }

        foreach (var direction in directions)
        {
            if (direction == SortDirection.Unspecified)
            {
                throw new ArgumentException(
                    "An index key needs an explicit direction; leave the directions empty for the "
                    + "ascending-nulls-last default.",
                    nameof(directions));
            }
        }

        var members = keys
            .Select(k => PocoMembers.Resolve(k, _table, "Index(keys)").Name)
            .ToArray();
        var declaredDirections = directions.ToArray();

        _hostIndexDeclarations.Add(new HostIndexDeclaration(
            byMember => new IndexDescriptor
            {
                Name = name,
                Kind = kind,
                Columns = Array.ConvertAll(members, member => Index(byMember, member)),
                Unique = unique,
                Directions = declaredDirections,
            },
            factory));

        return this;
    }

    /// <summary>
    /// Declares a clustered index over these columns (D257,
    /// <c>docs/design/34-clustered-indexes.md</c>): an ordered index that also holds a second copy of
    /// the table's columns, materialised in this key order at <c>Build()</c>.
    /// </summary>
    /// <remarks>
    /// A lookup whose projection lies inside the copy is served as slices of it rather than as a
    /// gather through the permutation, and the planner prices it as the sequential scan it is. Follow
    /// this with <see cref="Covering"/> to name the columns the copy carries; without it the copy
    /// carries every column.
    /// </remarks>
    public PocoTableBuilder<T> ClusteredIndex(params Expression<Func<T, object?>>[] keys) =>
        Index(name: null, IndexKind.Clustered, unique: false, keys);

    /// <summary>As <see cref="ClusteredIndex(Expression{Func{T, object?}}[])"/>, and the key identifies a row.</summary>
    public PocoTableBuilder<T> UniqueClusteredIndex(params Expression<Func<T, object?>>[] keys) =>
        Index(name: null, IndexKind.Clustered, unique: true, keys);

    /// <summary>A clustered index with an explicit name and uniqueness.</summary>
    /// <param name="name">Null takes the <c>ix_&lt;table&gt;_&lt;columns&gt;</c> default.</param>
    public PocoTableBuilder<T> ClusteredIndex(
        string? name, bool unique, params Expression<Func<T, object?>>[] keys) =>
        Index(name, IndexKind.Clustered, unique, directions: [], keys);

    /// <summary>
    /// The full form: a clustered index whose keys are not all ascending-nulls-last. Pass one
    /// direction per key, or none at all for the default.
    /// </summary>
    public PocoTableBuilder<T> ClusteredIndex(
        string? name,
        bool unique,
        IReadOnlyList<SortDirection> directions,
        params Expression<Func<T, object?>>[] keys) =>
        Index(name, IndexKind.Clustered, unique, directions, keys);

    /// <summary>
    /// The columns the clustered index declared last carries in its copy (D257, §1). The key columns
    /// are always carried — a range seek binary-searches them there — so naming them again is
    /// harmless; declaring no covering set at all means every column.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The last index declared on this table is not clustered, or none has been declared. Covering
    /// columns describe a copy, and only a clustered index has one.
    /// </exception>
    public PocoTableBuilder<T> Covering(params Expression<Func<T, object?>>[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Length == 0)
        {
            throw new ArgumentException(
                $"Table '{_table}': Covering(…) needs at least one column. Leave it off altogether "
                + "for a copy of every column.",
                nameof(columns));
        }

        if (_indexes.Count == 0 || _indexes[^1].Kind != IndexKind.Clustered)
        {
            throw new InvalidOperationException(
                $"Table '{_table}': Covering(…) applies to the clustered index declared last, and "
                + (_indexes.Count == 0
                    ? "no index has been declared on this table yet. "
                    : $"the last index declared is {_indexes[^1].Kind}. ")
                + "Only a clustered index holds a copy of its columns; declare it with "
                + "ClusteredIndex(…) or UniqueClusteredIndex(…) first.");
        }

        var members = columns
            .Select(c => PocoMembers.Resolve(c, _table, "Covering(columns)").Name)
            .ToArray();

        _indexes[^1] = _indexes[^1] with
        {
            Covering = [.. (_indexes[^1].Covering ?? []), .. members],
        };
        return this;
    }

    /// <summary>
    /// Registers a structure the host owns as an index (D35). Its descriptor is trusted as declared,
    /// which is why <c>Chalk.TestKit.PocoIndexConformance.Verify</c> exists: run it once against the
    /// rows and the adapter is proven, rather than inspected. The instance serves every snapshot of
    /// the table, so the structure behind it must not change while any snapshot built over it may
    /// still be read; a structure that follows the rows through a refresh is registered with
    /// <see cref="Index(IndexDescriptor, Func{IReadOnlyCollection{T}, IPocoIndex{T}})"/> instead.
    /// </summary>
    public PocoTableBuilder<T> Index(IPocoIndex<T> index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var descriptor = index.Descriptor;
        _hostIndexDeclarations.Add(new HostIndexDeclaration(
            _ => descriptor,
            (_, _) => index));
        return this;
    }

    /// <summary>
    /// Registers a host index made once per snapshot (F110): <paramref name="index"/> is called with
    /// the collection a snapshot is built from — the one the registration reported, or the rows an
    /// append produced — off the execution path, and its product belongs to that snapshot alone, so
    /// an execution keeps the index it started with while a refresh lands and the next execution
    /// gets the replacement. <paramref name="descriptor"/> is what the catalog publishes; every
    /// product must carry the same declaration, or the snapshot is refused.
    /// </summary>
    public PocoTableBuilder<T> Index(
        IndexDescriptor descriptor, Func<IReadOnlyCollection<T>, IPocoIndex<T>> index)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(index);
        _hostIndexDeclarations.Add(new HostIndexDeclaration(
            _ => descriptor,
            (_, rows) => index(rows)));
        return this;
    }

    /// <summary>
    /// Which of this table's rows and columns a caller may see (step 26, D141,
    /// <c>docs/design/16-entitlements.md</c> §1).
    /// </summary>
    /// <remarks>
    /// The descriptor is the whole vocabulary the core knows: a row predicate and per-column
    /// disclosure rules, both SQL in Chalk's own dialect. A policy model compiles down to it; nothing
    /// of any model's own vocabulary reaches here or the wire (D143). Declaring none — which is every
    /// table until a host says otherwise — costs nothing at all.
    /// </remarks>
    public PocoTableBuilder<T> Entitlement(TableEntitlementDescriptor entitlement)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        _entitlement = entitlement;
        return this;
    }

    private TableEntitlementDescriptor? _entitlement;

    /// <summary>
    /// How much this table tells the planner about its values (D36). <see cref="StatisticsLevel.Basic"/>
    /// is the default and is computed for free in the pass <c>Build()</c> already makes;
    /// <see cref="StatisticsLevel.Histogram"/> adds equi-height histograms and a most-common-value
    /// list per comparable column; <see cref="StatisticsLevel.Unknown"/> turns it all off.
    /// </summary>
    public PocoTableBuilder<T> Statistics(StatisticsLevel level) => Statistics(level, buckets: 64);

    /// <summary>
    /// The same, with the freshness policy this table's column statistics follow (D271 (g),
    /// <c>docs/design/44-catalog-registration.md</c> §7): recompute them only once the table has
    /// grown by <paramref name="refreshWhenGrownBy"/> of its size when they were last taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Statistics(StatisticsLevel.Histogram, refreshWhenGrownBy: 0.1)</c> reads "a histogram, kept
    /// within ten per cent". A table appended a micro-batch at a time otherwise recomputes histograms
    /// over the whole table on every batch, which is the cost this turns down.
    /// </para>
    /// <para>
    /// The row count is untouched by this and stays exact and O(1), so a plan's cardinality is always
    /// right; what ages is the distribution of values. Zero — the default — recomputes every time,
    /// which is what every table did before D271.
    /// </para>
    /// </remarks>
    /// <param name="refreshWhenGrownBy">
    /// A fraction of the row count the statistics were last computed over: <c>0.1</c> is "when the
    /// table is a tenth larger". Zero recomputes always; the value must not be negative.
    /// </param>
    /// <param name="level"></param>
    public PocoTableBuilder<T> Statistics(StatisticsLevel level, double refreshWhenGrownBy) =>
        Statistics(level, buckets: 64, refreshWhenGrownBy);

    /// <summary>
    /// As <see cref="Statistics(StatisticsLevel)"/>, with the histogram's bucket count.
    /// </summary>
    /// <param name="buckets">Histogram buckets per column. Ignored below <c>Histogram</c>.</param>
    public PocoTableBuilder<T> Statistics(StatisticsLevel level, int buckets) =>
        Statistics(level, buckets, refreshWhenGrownBy: 0);

    /// <inheritdoc cref="Statistics(StatisticsLevel, double)"/>
    /// <param name="buckets">Histogram buckets per column. Ignored below <c>Histogram</c>.</param>
    /// <param name="level"></param>
    /// <param name="refreshWhenGrownBy"></param>
    public PocoTableBuilder<T> Statistics(
        StatisticsLevel level, int buckets, double refreshWhenGrownBy)
    {
        if (buckets is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(buckets), buckets, "buckets must be 1..1024.");
        }

        if (refreshWhenGrownBy < 0 || double.IsNaN(refreshWhenGrownBy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(refreshWhenGrownBy),
                refreshWhenGrownBy,
                "refreshWhenGrownBy is a fraction of the row count the statistics were last taken "
                + "over and cannot be negative; 0 recomputes every time.");
        }

        _statisticsLevel = level;
        _histogramBuckets = buckets;
        _refreshWhenGrownBy = refreshWhenGrownBy;
        return this;
    }

    /// <summary>Overrides one column's statistics with numbers the host already has.</summary>
    public PocoTableBuilder<T> Statistics<TProp>(
        Expression<Func<T, TProp>> column, ColumnStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(statistics);
        _columnStatistics[PocoMembers.Resolve(column, _table, "Statistics(column, …)").Name] = statistics;
        return this;
    }

    /// <summary>
    /// Supplies the whole table's statistics instead of computing them. Called at every
    /// <c>DescribeSchema()</c> — at engine creation and on each catalog epoch bump — which is what
    /// "lazy" means for a push-based catalog: deferred to registration, never a callback from the
    /// planner (§2).
    /// </summary>
    public PocoTableBuilder<T> Statistics(Func<TableStatistics> statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        _statistics = statistics;
        return this;
    }

    private KeyDeclaration Declare<TKey>(
        Expression<Func<T, TKey>> key, SortDirection direction, string what)
    {
        if (direction == SortDirection.Unspecified)
        {
            throw new ArgumentException(
                "A collation key needs an explicit direction: Calcite compares collations including the "
                + "null direction, so an unspecified one never satisfies an ORDER BY (V3).",
                nameof(direction));
        }

        return new KeyDeclaration(PocoMembers.Resolve(key, _table, $"{what}(key)").Name, direction);
    }

    internal PocoTableRuntime<T> Build(
        string sourceId, PocoNamingPolicy policy, int defaultDecimalScale, PocoRows<T> rows)
    {
        var columns = new List<PocoColumn<T>>();
        var byMember = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var member in PocoMembers.Discover(typeof(T)))
        {
            if (member.Ignored || _ignored.Contains(member.Name))
            {
                continue;
            }

            _overrides.TryGetValue(member.Name, out var over);
            var name = over?.Name ?? member.Attribute?.Name ?? PocoNaming.Apply(member.Name, policy);
            var what = $"table '{_table}' member '{member.Name}'";

            var plan = PocoTypeMapping.Plan(
                member.ClrType, member.Nullable, member.Attribute, over?.Type, defaultDecimalScale, what);

            var row = Expression.Parameter(typeof(T), "row");
            var binding = new PocoValueBinding
            {
                Row = row,
                Value = Expression.MakeMemberAccess(row, member.Member),
                CanBeNull = member.Nullable,
                ToStorage = plan.ToStorage,
            };

            byMember[member.Name] = columns.Count;
            columns.Add(CreateColumn(name, plan, binding, sourceId, _table));
        }

        foreach (var name in _overrides.Keys)
        {
            if (!byMember.ContainsKey(name) && !_ignored.Contains(name))
            {
                throw new CatalogValidationException(
                    $"table '{_table}'",
                    $"Column(…) names '{name}', which is not a public instance property or field of {typeof(T).Name} "
                    + "(or is excluded by [ChalkIgnore]).");
            }
        }

        foreach (var computed in _computed)
        {
            var canBeNull = Nullable.GetUnderlyingType(computed.ResultType) is not null
                || !computed.ResultType.IsValueType;
            var what = $"table '{_table}' column '{computed.Name}'";
            var plan = PocoTypeMapping.Plan(
                computed.ResultType, canBeNull, attribute: null, computed.Type, defaultDecimalScale, what);

            var binding = new PocoValueBinding
            {
                Row = computed.Projection.Parameters[0],
                Value = computed.Projection.Body,
                CanBeNull = canBeNull,
                ToStorage = plan.ToStorage,
            };

            columns.Add(CreateColumn(computed.Name, plan, binding, sourceId, _table));
        }

        if (columns.Count == 0)
        {
            throw new CatalogValidationException(
                $"table '{_table}'",
                $"{typeof(T).Name} contributes no columns. A table needs at least one public instance "
                + "property or field, or a Column(name, projection).");
        }

        var collations = _collations
            .Select(keys => new CollationDescriptor
            {
                Keys = keys.Select(k => new KeyOrder(Index(byMember, k.Member), k.Direction)).ToArray(),
            })
            .ToArray();

        var uniqueKeys = _uniqueKeys
            .Select(key => new UniqueKeyDescriptor { Columns = Array.ConvertAll(key, m => Index(byMember, m)) })
            .ToArray();

        if (!rows.RandomAccess && collations.Length > 0)
        {
            throw new CatalogValidationException(
                $"table '{_table}'",
                "the collection was registered as an IReadOnlyCollection<T>, which only enumerates, so "
                + "its position order is not addressable and it cannot declare a collation (§1). "
                + "Register an IReadOnlyList<T>, or drop the OrderedBy declaration.");
        }

        var statistics = new PocoStatisticsPlan
        {
            Level = _statisticsLevel,
            HistogramBuckets = _histogramBuckets,
            Overrides = _columnStatistics.ToDictionary(
                pair => Index(byMember, pair.Key), pair => pair.Value),
            Supplier = _statistics,
            RefreshWhenGrownBy = _refreshWhenGrownBy,
        };

        var foreignKeys = _foreignKeys
            .Select(k => new PocoForeignKey(
                [.. k.Members.Select(m => Index(byMember, m))],
                k.ParentTable,
                k.ParentType,
                k.ParentColumns,
                k.Verify))
            .ToArray();

        var built = new List<PocoColumn<T>>(columns).ToArray();

        // The declarations are resolved once, here, because they are shape; the indexes themselves
        // are built per snapshot, because they are data (D260 §2).
        var hostIndexes = ResolveHostIndexes(byMember);
        var plans = PlanIndexes(built, byMember, collations, rows.RandomAccess, hostIndexes);
        var factory = new PocoSnapshotFactory<T>(
            _table, rows, built, collations, uniqueKeys, plans, hostIndexes, _indexScratch, _verify);

        return new PocoTableRuntime<T>(
            _table, factory, factory.FromRegistration(), built, uniqueKeys, collations, statistics,
            foreignKeys, byMember, _entitlement);
    }

    /// <summary>
    /// Resolves host index declarations after POCO member discovery, while the member-to-column map
    /// is available. The resulting descriptor is shape and is shared by every per-snapshot product.
    /// </summary>
    private PocoHostIndex<T>[] ResolveHostIndexes(Dictionary<string, int> byMember)
    {
        if (_hostIndexDeclarations.Count == 0)
        {
            return [];
        }

        var resolved = new PocoHostIndex<T>[_hostIndexDeclarations.Count];
        for (var i = 0; i < resolved.Length; i++)
        {
            var declaration = _hostIndexDeclarations[i];
            var descriptor = declaration.ResolveDescriptor(byMember);

            resolved[i] = new PocoHostIndex<T>(
                descriptor,
                rows => declaration.Factory(descriptor, rows));
        }

        return resolved;
    }

    /// <summary>
    /// Declared indexes, in declaration order: the built-in permutation indexes first, then whatever
    /// the host registered. A built-in index whose key is a prefix of a declared collation with the
    /// same directions is collation-backed and allocates nothing (§1).
    /// </summary>
    /// <remarks>
    /// Only the <em>plan</em> is made here — the descriptor, the compiled key accessors, the backing
    /// decision — because none of that depends on the rows. Building the index over a collection is
    /// <see cref="PocoSnapshotFactory{T}"/>'s, and it happens again on every refresh (D260).
    /// </remarks>
    private PocoIndexPlan<T>[] PlanIndexes(
        PocoColumn<T>[] columns,
        Dictionary<string, int> byMember,
        IReadOnlyList<CollationDescriptor> collations,
        bool randomAccess,
        IReadOnlyList<PocoHostIndex<T>> hostIndexes)
    {
        var indexCount = _indexes.Count;
        var plans = new List<PocoIndexPlan<T>>(indexCount);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        for (var declarationIndex = 0; declarationIndex < indexCount; declarationIndex++)
        {
            var declaration = _indexes[declarationIndex];

            if (!randomAccess)
            {
                throw new CatalogValidationException(
                    $"table '{_table}'",
                    "a built-in index is a permutation of row positions, so it needs an "
                    + "IReadOnlyList<T>. Register the collection as a list, or supply your own "
                    + "structure with .Index(IPocoIndex<T>).");
            }

            var keyCount = declaration.Members.Length;

            var keyColumns = new int[keyCount];
            var accessors = new Func<T, object?>[keyCount];
            var keyColumnNames = new string[keyCount];

            for (var k = 0; k < keyCount; k++)
            {
                var column = Index(byMember, declaration.Members[k]);

                keyColumns[k] = column;
                accessors[k] = columns[column].CompileLogicalAccessor();
                keyColumnNames[k] = columns[column].Name;
            }

            var name = declaration.Name;

            if (name is null)
            {
                name = "ix_" + _table + "_";

                // This is build-time only, so StringBuilder isn't worth complicating
                // the code for; use the same resulting naming convention.
                name += string.Join("_", keyColumnNames);
            }

            var descriptor = new IndexDescriptor
            {
                Name = name,
                Kind = declaration.Kind,
                Columns = keyColumns,
                Unique = declaration.Unique,
                Directions = declaration.Directions,
                Covering = CoveringColumns(declaration, byMember, keyColumns),
            };

            // The backing decision remains exactly the existing semantic test:
            // column AND direction must match the declared collation.
            plans.Add(new PocoIndexPlan<T>
            {
                Descriptor = descriptor,
                Accessors = accessors,
                CollationBacked = IsCollationBacked(descriptor, collations),
                KeyColumnNames = keyColumnNames,
            });
        }

        var descriptors = plans.Select(p => p.Descriptor)
            .Concat(hostIndexes.Select(i => i.Descriptor));

        foreach (var descriptor in descriptors)
        {
            if (!names.Add(descriptor.Name))
            {
                throw new CatalogValidationException(
                    $"table '{_table}'",
                    $"index name '{descriptor.Name}' is declared twice");
            }

            foreach (var column in descriptor.Columns)
            {
                if (column < 0 || column >= columns.Length)
                {
                    throw new CatalogValidationException(
                        $"table '{_table}' index '{descriptor.Name}'",
                        $"key column {column} is out of range for a table of {columns.Length} columns. "
                        + "An index's key columns are indexes into the table's columns, in key order.");
                }
            }
        }

        return [.. plans];
    }

    /// <summary>
    /// A clustered index's covering set as column indexes — the declared columns and the key columns,
    /// ascending and without duplicates, which is the shape the descriptor and the wire want (D257).
    /// Empty means every column, which is both what an index that declared no covering set carries
    /// and what every other kind declares.
    /// </summary>
    private int[] CoveringColumns(
        IndexDeclaration declaration, Dictionary<string, int> byMember, int[] keyColumns)
    {
        if (declaration.Covering is not { Length: > 0 } members)
        {
            return [];
        }

        var covering = new SortedSet<int>(keyColumns);
        foreach (var member in members)
        {
            covering.Add(Index(byMember, member));
        }

        return [.. covering];
    }

    /// <summary>
    /// True when the rows are already in this index's key order because a declared collation says
    /// so: the index's key is a prefix of the collation, direction for direction.
    /// </summary>
    private static bool IsCollationBacked(
        IndexDescriptor index, IReadOnlyList<CollationDescriptor> collations)
    {
        foreach (var collation in collations)
        {
            if (collation.Keys.Count < index.Columns.Count)
            {
                continue;
            }

            var matches = true;
            for (var i = 0; i < index.Columns.Count && matches; i++)
            {
                matches = collation.Keys[i].Column == index.Columns[i]
                    && collation.Keys[i].Direction == index.DirectionAt(i);
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private int Index(Dictionary<string, int> byMember, string member) =>
        byMember.TryGetValue(member, out var index)
            ? index
            : throw new CatalogValidationException(
                $"table '{_table}'",
                $"'{member}' is named by a collation, a unique key or an index but is not one of the "
                + "table's columns. "
                + "Keys must name a member that becomes a column; remove the Ignore, or drop the declaration.");

    private static PocoColumn<T> CreateColumn(
        string name, PocoColumnPlan plan, PocoValueBinding binding, string sourceId, string table) =>
        PocoColumnFactory.Create<T>(name, plan, binding, sourceId, table);

    /// <param name="Covering">
    /// The members a clustered index's copy carries, or null for "every column" (D257). Never set on
    /// any other kind: <see cref="Covering(Expression{Func{T, object?}}[])"/> refuses one.
    /// </param>
    private sealed record IndexDeclaration(
        string? Name,
        IndexKind Kind,
        bool Unique,
        SortDirection[] Directions,
        string[] Members,
        string[]? Covering);

    /// <summary>
    /// A foreign key as declared: member names on this side, and column-or-member names on the
    /// parent's, because the parent may not be registered yet. <c>PocoSourceBuilder.Build</c>
    /// resolves both to column indexes once every table exists (F14).
    /// </summary>
    /// <remarks>
    /// The parent is named either by table name or — with the <c>.References&lt;TParent&gt;</c>
    /// spelling — by row type, which is resolved against the one table registered over that type.
    /// Exactly one of the two is set.
    /// </remarks>
    internal sealed record ForeignKeyDeclaration(
        string[] Members, string? ParentTable, Type? ParentType, string[] ParentColumns, bool Verify);

    private sealed record ColumnOverride(string? Name, ChalkType? Type);

    private sealed record ComputedColumn(string Name, LambdaExpression Projection, Type ResultType, ChalkType? Type);

    private readonly record struct KeyDeclaration(string Member, SortDirection Direction);
}

/// <summary>
/// The second half of <c>.ForeignKey(x =&gt; x.OrgId).References&lt;Org&gt;(o =&gt; o.Id,
/// verify: true)</c> (F16). Splitting the declaration in two is what lets the child's columns be
/// written without a type argument and the parent's with one.
/// </summary>
/// <remarks>
/// A builder that is never finished with <c>References</c> declares nothing: the constraint is
/// recorded on the table builder only when the parent is known.
/// </remarks>
public sealed class PocoForeignKeyBuilder<T>
{
    private readonly PocoTableBuilder<T> _table;
    private readonly IReadOnlyList<Expression<Func<T, object?>>> _columns;

    internal PocoForeignKeyBuilder(
        PocoTableBuilder<T> table, IReadOnlyList<Expression<Func<T, object?>>> columns)
    {
        _table = table;
        _columns = columns;
    }

    /// <summary>
    /// Names the parent by row type: the table registered over <typeparamref name="TParent"/> on
    /// this source. Fails at <c>Build()</c> if no table or more than one is registered over it —
    /// name the table instead when two tables share a row type.
    /// </summary>
    /// <param name="parentColumn">A direct property or field access on the parent's row.</param>
    /// <param name="verify">
    /// <c>true</c> probes every non-NULL child key against the parent's key set at <c>Build()</c>
    /// and raises <c>CatalogVerificationException</c> naming the first orphan. <c>false</c> performs
    /// no integrity check at all.
    /// </param>
    public PocoTableBuilder<T> References<TParent>(
        Expression<Func<TParent, object?>> parentColumn, bool verify) =>
        References(table: null, [parentColumn], verify);

    /// <summary>The same, with the parent table named explicitly.</summary>
    public PocoTableBuilder<T> References<TParent>(
        string table, Expression<Func<TParent, object?>> parentColumn, bool verify) =>
        References(table, [parentColumn], verify);

    /// <summary>The composite form, paired with this key's columns position by position.</summary>
    public PocoTableBuilder<T> References<TParent>(
        string? table, IReadOnlyList<Expression<Func<TParent, object?>>> parentColumns, bool verify)
    {
        ArgumentNullException.ThrowIfNull(parentColumns);
        return _table.Declare(
            _columns,
            table,
            typeof(TParent),
            [.. parentColumns.Select(c => PocoMembers.Resolve(c, "parent", "References(parent)").Name)],
            verify);
    }

    /// <summary>The named-column form, for a parent whose row type is not in scope here.</summary>
    public PocoTableBuilder<T> References(string table, string parentColumn, bool verify)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentColumn);
        return References(table, [parentColumn], verify);
    }

    /// <summary>The composite named-column form.</summary>
    public PocoTableBuilder<T> References(string table, IReadOnlyList<string> parentColumns, bool verify)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        return _table.Declare(_columns, table, parentType: null, parentColumns, verify);
    }
}

/// <summary>
/// Reusable construction storage for PermutationIndex.
///
/// One instance may be reused sequentially across batches/indexes,
/// but must not be used concurrently.
/// </summary>
internal sealed class PermutationIndexScratch
{
    //
    // ------------------------------------------------------------
    // Per-key materialised storage.
    //
    // These buffers must remain distinct by key ordinal because all
    // materialised keys remain live until sorting, distinct-count
    // calculation and uniqueness verification have completed.
    // ------------------------------------------------------------
    //

    private byte[]?[] _byteCodes = [];
    private ushort[]?[] _ushortCodes = [];
    private uint[]?[] _uintCodes = [];
    private ulong[]?[] _ulongCodes = [];

    private bool[]?[] _nulls = [];
    private int[]?[] _symbolIds = [];

    //
    // Only string and Utf8String currently enter
    // MaterialiseSymbols.
    //
    private string[]?[] _stringValues = [];
    private Utf8String[]?[] _utf8Values = [];

    //
    // ------------------------------------------------------------
    // Symbol-discovery scratch.
    //
    // MaterialiseSymbols completes one key before the next key is
    // started, so these do NOT need to be per-key.
    // ------------------------------------------------------------
    //

    private readonly Dictionary<string, int>
        _stringSymbols =
            new(StringComparer.Ordinal);

    private readonly Dictionary<Utf8String, int>
        _utf8Symbols =
            new();

    private string[] _uniqueStrings = [];
    private Utf8String[] _uniqueUtf8 = [];

    private int[] _symbolOrder = [];
    private int[] _symbolRanks = [];

    //
    // ------------------------------------------------------------
    // Sort scratch.
    // ------------------------------------------------------------
    //

    private int[] _bucketRows = [];
    private int[] _bucketMeta = [];

    private ulong[] _packed64 = [];
    private UInt128[] _packed128 = [];

    //
    // ------------------------------------------------------------
    // Encoded key columns.
    // ------------------------------------------------------------
    //

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TCode[] GetCodes<TCode>(
        int key,
        int length)
        where TCode :
            unmanaged,
            IBinaryInteger<TCode>,
            IUnsignedNumber<TCode>
    {
        //
        // TCode is closed at every caller. RyuJIT can specialize
        // these typeof checks for each generic instantiation.
        //
        if (typeof(TCode) == typeof(byte))
        {
            return (TCode[])(object)
                GetPerKey(
                    ref _byteCodes,
                    key,
                    length);
        }

        if (typeof(TCode) == typeof(ushort))
        {
            return (TCode[])(object)
                GetPerKey(
                    ref _ushortCodes,
                    key,
                    length);
        }

        if (typeof(TCode) == typeof(uint))
        {
            return (TCode[])(object)
                GetPerKey(
                    ref _uintCodes,
                    key,
                    length);
        }

        if (typeof(TCode) == typeof(ulong))
        {
            return (TCode[])(object)
                GetPerKey(
                    ref _ulongCodes,
                    key,
                    length);
        }

        throw new NotSupportedException(
            $"Unsupported encoded-key type "
            + typeof(TCode).FullName);
    }

    //
    // ------------------------------------------------------------
    // Null maps.
    // ------------------------------------------------------------
    //

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool[] GetNulls(
        int key,
        int length)
    {
        var buffer =
            GetPerKey(
                ref _nulls,
                key,
                length);

        //
        // This replaces the zero-initialisation previously supplied
        // by new bool[].
        //
        buffer
            .AsSpan(0, length)
            .Clear();

        return buffer;
    }

    //
    // ------------------------------------------------------------
    // Symbol row IDs and fallback values.
    // ------------------------------------------------------------
    //

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int[] GetSymbolIds(
        int key,
        int length) =>
        GetPerKey(
            ref _symbolIds,
            key,
            length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSymbol[] GetSymbolValues<TSymbol>(
        int key,
        int length)
        where TSymbol : notnull
    {
        if (typeof(TSymbol) == typeof(string))
        {
            return (TSymbol[])(object)
                GetPerKey(
                    ref _stringValues,
                    key,
                    length);
        }

        if (typeof(TSymbol) ==
            typeof(Utf8String))
        {
            return (TSymbol[])(object)
                GetPerKey(
                    ref _utf8Values,
                    key,
                    length);
        }

        throw new NotSupportedException(
            $"Unsupported symbol type "
            + typeof(TSymbol).FullName);
    }

    //
    // ------------------------------------------------------------
    // Symbol discovery.
    // ------------------------------------------------------------
    //

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Dictionary<TSymbol, int>
        BeginSymbols<TSymbol>(
            int minimumCapacity)
        where TSymbol : notnull
    {
        if (typeof(TSymbol) == typeof(string))
        {
            var dictionary =
                _stringSymbols;

            dictionary.Clear();

            if (minimumCapacity != 0)
            {
                dictionary.EnsureCapacity(
                    minimumCapacity);
            }

            return
                (Dictionary<TSymbol, int>)
                (object)dictionary;
        }

        if (typeof(TSymbol) ==
            typeof(Utf8String))
        {
            var dictionary =
                _utf8Symbols;

            dictionary.Clear();

            if (minimumCapacity != 0)
            {
                dictionary.EnsureCapacity(
                    minimumCapacity);
            }

            return
                (Dictionary<TSymbol, int>)
                (object)dictionary;
        }

        throw new NotSupportedException(
            $"Unsupported symbol type "
            + typeof(TSymbol).FullName);
    }

    /// <summary>
    /// Dense insertion-id -> logical symbol table used only while
    /// one symbol key is being materialised.
    ///
    /// Growth preserves existing entries because this method may be
    /// called after symbols have already been inserted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TSymbol[] GetUniqueSymbols<TSymbol>(
        int length)
        where TSymbol : notnull
    {
        if (typeof(TSymbol) == typeof(string))
        {
            EnsurePreserving(
                ref _uniqueStrings,
                length);

            return
                (TSymbol[])(object)
                _uniqueStrings;
        }

        if (typeof(TSymbol) ==
            typeof(Utf8String))
        {
            EnsurePreserving(
                ref _uniqueUtf8,
                length);

            return
                (TSymbol[])(object)
                _uniqueUtf8;
        }

        throw new NotSupportedException(
            $"Unsupported symbol type "
            + typeof(TSymbol).FullName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<int> GetSymbolOrder(
        int length)
    {
        Ensure(
            ref _symbolOrder,
            length);

        return _symbolOrder
            .AsSpan(0, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<int> GetSymbolRanks(
        int length)
    {
        Ensure(
            ref _symbolRanks,
            length);

        return _symbolRanks
            .AsSpan(0, length);
    }

    //
    // ------------------------------------------------------------
    // Bucket-sort storage.
    // ------------------------------------------------------------
    //

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<int> GetBucketRows(
        int length)
    {
        Ensure(
            ref _bucketRows,
            length);

        return _bucketRows
            .AsSpan(0, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<int> GetBucketMeta(
        int length)
    {
        Ensure(
            ref _bucketMeta,
            length);

        return _bucketMeta
            .AsSpan(0, length);
    }

    //
    // ------------------------------------------------------------
    // Packed-sort storage.
    // ------------------------------------------------------------
    //

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<ulong> GetPacked64(
        int length)
    {
        Ensure(
            ref _packed64,
            length);

        return _packed64
            .AsSpan(0, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<UInt128> GetPacked128(
        int length)
    {
        Ensure(
            ref _packed128,
            length);

        return _packed128
            .AsSpan(0, length);
    }

    //
    // ------------------------------------------------------------
    // Internal allocation helpers.
    // ------------------------------------------------------------
    //

    private static T[] GetPerKey<T>(
        ref T[]?[] slots,
        int key,
        int length)
    {
        EnsureSlot(
            ref slots,
            key);

        var buffer =
            slots[key];

        if (buffer is null ||
            buffer.Length < length)
        {
            buffer =
                GC.AllocateUninitializedArray<T>(
                    GrowCapacity(
                        buffer?.Length ?? 0,
                        length));

            slots[key] =
                buffer;
        }

        return buffer;
    }

    private static void EnsureSlot<T>(
        ref T[]?[] slots,
        int key)
    {
        if ((uint)key <
            (uint)slots.Length)
        {
            return;
        }

        var required =
            key + 1;

        var capacity =
            slots.Length == 0
                ? 4
                : slots.Length * 2;

        if (capacity < required)
        {
            capacity =
                required;
        }

        Array.Resize(
            ref slots,
            capacity);
    }

    private static void Ensure<T>(
        ref T[] buffer,
        int length)
    {
        if (buffer.Length >= length)
            return;

        buffer =
            GC.AllocateUninitializedArray<T>(
                GrowCapacity(
                    buffer.Length,
                    length));
    }

    //
    // Unlike ordinary scratch buffers, the unique-symbol array may
    // grow while it already contains the current key's symbols.
    //
    private static void EnsurePreserving<T>(
        ref T[] buffer,
        int length)
    {
        if (buffer.Length >= length)
            return;

        var replacement =
            GC.AllocateUninitializedArray<T>(
                GrowCapacity(
                    buffer.Length,
                    length));

        buffer
            .AsSpan()
            .CopyTo(
                replacement);

        buffer =
            replacement;
    }

    private static int GrowCapacity(
        int current,
        int required)
    {
        if (required <= current)
            return current;

        if (current == 0)
            return required;

        //
        // Growth is cold; favour stable amortised capacity without
        // blindly doubling close to Array.MaxLength.
        //
        var doubled =
            current <= Array.MaxLength / 2
                ? current * 2
                : Array.MaxLength;

        return Math.Max(
            required,
            doubled);
    }
}