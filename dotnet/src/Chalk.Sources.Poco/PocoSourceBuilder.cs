using Chalk.Catalog;

namespace Chalk.Sources.Poco;

/// <summary>
/// Registers <see cref="IReadOnlyList{T}"/> collections as SQL tables
/// (<c>docs/design/04-client.md</c> §5.1). <c>Build()</c> runs inference, compiles one chunk writer
/// per column, and verifies the declared collations and unique keys (D17).
/// </summary>
/// <example>
/// <code>
/// var bars = new PocoSourceBuilder("mem")
///     .AddTable("bars", barList, t => t
///         .OrderedBy(b => b.Ts).ThenBy(b => b.Symbol)
///         .UniqueKey(b => b.Ts, b => b.Symbol))
///     .Build();
/// </code>
/// </example>
public sealed class PocoSourceBuilder
{
    private SourceSharing _sharing = SourceSharing.Shared;
    private readonly List<TableRegistration> _tables = [];
    private PocoNamingPolicy _policy = PocoNamingPolicy.AsIs;
    private int _decimalScale = 10;

    /// <param name="sourceId">Identifies this source in the catalog and in every <c>TableRef</c>.</param>
    /// <param name="schemaName">The SQL schema the tables live in.</param>
    public PocoSourceBuilder(string sourceId, string schemaName = "main")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        SourceId = sourceId;
        SchemaName = schemaName;
    }

    internal string SourceId { get; }

    internal string SchemaName { get; }

    public PocoSourceBuilder Exclusive()
    {
        _sharing = SourceSharing.Exclusive;
        return this;
    }
    
    /// <summary>How member names become column names (D15). <see cref="PocoNamingPolicy.AsIs"/> by default.</summary>
    public PocoSourceBuilder NamingPolicy(PocoNamingPolicy policy)
    {
        _policy = policy;
        return this;
    }

    /// <summary>
    /// The scale <c>decimal</c> columns get when nothing says otherwise (D16). Ten fractional digits
    /// out of DECIMAL(28,10)'s twenty-eight leaves eighteen integral ones, which covers money and
    /// most measurements without truncating.
    /// </summary>
    public PocoSourceBuilder DefaultDecimalScale(int scale)
    {
        if (scale is < 0 or > 38)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "DECIMAL scale must be 0..38.");
        }

        _decimalScale = scale;
        return this;
    }

    /// <summary>Registers a collection as a table.</summary>
    public PocoSourceBuilder AddTable<T>(
        string name, IReadOnlyList<T> rows, Action<PocoTableBuilder<T>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return AddTable(name, () => rows, configure);
    }

    /// <summary>
    /// Registers a collection as a table, yielding the handle this table is named by from here on.
    /// The handle carries the source with the name and the row type with both, so that
    /// <c>Replace(orders, rows)</c> can neither reach the wrong source nor take the wrong rows.
    /// </summary>
    /// <remarks>
    /// An <c>out</c> parameter rather than a different return type, so the fluent chain the rest of
    /// the builder is written in still runs through it:
    /// <code>
    /// var source = new PocoSourceBuilder("mem")
    ///     .AddTable("orders", orders, out var ordersTable)
    ///     .AddTable("notes", notes, out var notesTable)
    ///     .Build();
    /// </code>
    /// </remarks>
    public PocoSourceBuilder AddTable<T>(
        string name,
        IReadOnlyList<T> rows,
        out PocoTable<T> table,
        Action<PocoTableBuilder<T>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return AddTable(name, () => rows, out table, configure);
    }

    /// <inheritdoc cref="AddTable{T}(string, IReadOnlyList{T}, out PocoTable{T}, Action{PocoTableBuilder{T}})"/>
    public PocoSourceBuilder AddTable<T>(
        string name,
        Func<IReadOnlyList<T>> rows,
        out PocoTable<T> table,
        Action<PocoTableBuilder<T>>? configure = null)
    {
        AddTable(name, rows, configure);
        table = Handle<T>(name);
        return this;
    }

    /// <inheritdoc cref="AddTable{T}(string, IReadOnlyList{T}, out PocoTable{T}, Action{PocoTableBuilder{T}})"/>
    public PocoSourceBuilder AddTable<T>(
        string name,
        IReadOnlyCollection<T> rows,
        out PocoTable<T> table,
        Action<PocoTableBuilder<T>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return AddTable(name, () => rows, out table, configure);
    }

    /// <inheritdoc cref="AddTable{T}(string, IReadOnlyList{T}, out PocoTable{T}, Action{PocoTableBuilder{T}})"/>
    public PocoSourceBuilder AddTable<T>(
        string name,
        Func<IReadOnlyCollection<T>> rows,
        out PocoTable<T> table,
        Action<PocoTableBuilder<T>>? configure = null)
    {
        AddTable(name, rows, configure);
        table = Handle<T>(name);
        return this;
    }

    /// <summary>
    /// The binding a handle resolves through, made once per registered name and bound to the source
    /// at <see cref="Build"/>. Two handles for one name are one binding, so a host that takes the
    /// handle twice gets equal handles.
    /// </summary>
    private PocoTable<T> Handle<T>(string name)
    {
        if (!_handles.TryGetValue(name, out var binding))
        {
            binding = new PocoTableBinding
            {
                SourceId = SourceId,
                Schema = SchemaName,
                Table = name,
                RowType = typeof(T),
            };
            _handles[name] = binding;
        }

        if (binding.RowType != typeof(T))
        {
            throw new InvalidOperationException(
                $"table '{name}' was registered over {binding.RowType.Name} and a handle over "
                + $"{typeof(T).Name} was asked for.");
        }

        return new PocoTable<T>(binding);
    }

    private readonly Dictionary<string, PocoTableBinding> _handles =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a collection that is fetched when it is needed. The delegate is read at
    /// <c>Build()</c> and again at every refresh, so a host swaps the underlying list and refreshes
    /// rather than rebuilding the source (D260).
    /// </summary>
    /// <remarks>
    /// What the delegate returned becomes a snapshot — the rows, the indexes over them and the
    /// statistics of them, as one immutable object — and every scan reads a snapshot. So the host's
    /// one obligation is to hand over a <em>new</em> collection and never to mutate one it has
    /// already given: the zero-copy paths read the host's own arrays, and that rule is what keeps an
    /// execution already reading them intact.
    /// </remarks>
    public PocoSourceBuilder AddTable<T>(
        string name, Func<IReadOnlyList<T>> rows, Action<PocoTableBuilder<T>>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(rows);
        return Add(name, PocoRows<T>.OfList(rows), configure);
    }

    /// <summary>
    /// Registers a collection that only enumerates — an <c>IndexedSet&lt;T&gt;</c>, a
    /// <c>HashSet&lt;T&gt;</c>, anything without an indexer (§1). Scans stage a batch of rows at a
    /// time instead of reading them in place, and the table declares no collation and no built-in
    /// index: those need positions. Bring your own with <c>.Index(IPocoIndex&lt;T&gt;)</c>.
    /// </summary>
    public PocoSourceBuilder AddTable<T>(
        string name, IReadOnlyCollection<T> rows, Action<PocoTableBuilder<T>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return AddTable(name, () => rows, configure);
    }

    /// <summary>The enumerate-only twin of the <c>Func</c> registration.</summary>
    public PocoSourceBuilder AddTable<T>(
        string name, Func<IReadOnlyCollection<T>> rows, Action<PocoTableBuilder<T>>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(rows);
        return Add(name, PocoRows<T>.OfCollection(rows), configure);
    }

    private PocoSourceBuilder Add<T>(
        string name, PocoRows<T> rows, Action<PocoTableBuilder<T>>? configure)
    {
        var builder = new PocoTableBuilder<T>(name);
        configure?.Invoke(builder);
        _tables.Add(new TableRegistration<T>(builder, rows));
        return this;
    }

    /// <summary>
    /// Runs inference, compiles the extractors and verifies the declarations. Everything that can go
    /// wrong with a registration goes wrong here, not at the first query.
    /// </summary>
    /// <summary>
    /// Declares a function on this schema (D77, D81). The callback says what it is — its parameters,
    /// its result, its properties and where it is implemented — exactly as <c>AddTable</c>'s says
    /// what a table is.
    /// </summary>
    public PocoSourceBuilder AddFunction(string name, Action<FunctionBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new FunctionBuilder(name);
        configure(builder);
        _functions.Add(builder.Build());
        return this;
    }

    private readonly List<FunctionDescriptor> _functions = [];

    public PocoSource Build()
    {
        if (_tables.Count == 0)
        {
            throw new InvalidOperationException(
                $"Source '{SourceId}' has no tables. Call AddTable before Build.");
        }

        var tables = _tables.Select(t => t.Build(SourceId, _policy, _decimalScale)).ToArray();
        ResolveForeignKeys(tables);
        var source = new PocoSource(_sharing, SourceId, SchemaName, tables, [.. _functions], _handles);

        // The same rules the planner applies (03-planner.md §3.1). Running them here turns a duplicate
        // column name or an out-of-range key index into a registration error rather than a planner one.
        CatalogValidator.Validate(new CatalogContext
        {
            ContextId = SourceId,
            Epoch = 0,
            Schemas = [source.DescribeSchema()],
        });

        return source;
    }

    /// <summary>
    /// Turns each declared foreign key into a descriptor once every table is known: the parent is
    /// looked up by name or by row type and its columns by SQL name or member name (F14, F16). The
    /// rest of the shape checking — arity, kinds, ranges — is <c>CatalogValidator</c>'s, which runs
    /// immediately after; the ones declared <c>verify: true</c> are then probed against the data.
    /// </summary>
    private static void ResolveForeignKeys(PocoTableRuntime[] tables)
    {
        var verify = new List<(PocoTableRuntime Child, ForeignKeyDescriptor Key, PocoTableRuntime Parent)>();

        foreach (var table in tables)
        {
            if (table.DeclaredForeignKeys.Count == 0)
            {
                continue;
            }

            var resolved = new List<ForeignKeyDescriptor>(table.DeclaredForeignKeys.Count);
            foreach (var key in table.DeclaredForeignKeys)
            {
                var parent = FindParent(tables, table, key);

                var parentColumns = new int[key.ParentColumns.Length];
                for (var i = 0; i < parentColumns.Length; i++)
                {
                    parentColumns[i] = parent.ColumnIndex(key.ParentColumns[i]);
                    if (parentColumns[i] < 0)
                    {
                        throw new CatalogValidationException(
                            $"table '{table.Name}'",
                            $"ForeignKey(…) names '{key.ParentColumns[i]}' on parent table "
                            + $"'{parent.Name}', which has no such column or member.");
                    }
                }

                var descriptor = new ForeignKeyDescriptor
                {
                    Name = $"fk_{table.Name}_{parent.Name}_{resolved.Count}",
                    Columns = key.Columns,
                    ParentTable = parent.Name,
                    ParentColumns = parentColumns,
                };
                resolved.Add(descriptor);

                if (key.Verify)
                {
                    verify.Add((table, descriptor, parent));
                }
            }

            table.ResolveForeignKeys(resolved);
        }

        // After every table's keys are resolved, so a cycle of verified keys is still checked and
        // the parent's key set is built once per (parent, columns) pair rather than once per row.
        foreach (var (child, key, parent) in verify)
        {
            child.ProbeForeignKey(key, parent.KeySet(key.ParentColumns), parent);
        }
    }

    /// <summary>The parent a foreign key names, by table name or — the fluent spelling — by row type.</summary>
    private static PocoTableRuntime FindParent(PocoTableRuntime[] tables, PocoTableRuntime child, PocoForeignKey key)
    {
        if (key.ParentTable is { } named)
        {
            return System.Array.Find(
                tables, t => string.Equals(t.Name, named, StringComparison.OrdinalIgnoreCase))
                ?? throw new CatalogValidationException(
                    $"table '{child.Name}'",
                    $"ForeignKey(…) names the parent table '{named}', which is not registered on "
                    + "this source. Add it with AddTable before Build.");
        }

        var byType = System.Array.FindAll(tables, t => t.RowType == key.ParentType);
        return byType.Length switch
        {
            1 => byType[0],
            0 => throw new CatalogValidationException(
                $"table '{child.Name}'",
                $"References<{key.ParentType?.Name}>(…) has no table to point at: no table on this "
                + $"source is registered over {key.ParentType?.Name}. Add it with AddTable before "
                + "Build, or name the table with References(table, …)."),
            _ => throw new CatalogValidationException(
                $"table '{child.Name}'",
                $"References<{key.ParentType?.Name}>(…) is ambiguous: "
                + string.Join(", ", byType.Select(t => $"'{t.Name}'"))
                + $" are all registered over {key.ParentType?.Name}. Name the one you mean with "
                + "References(table, …)."),
        };
    }

    private abstract class TableRegistration
    {
        public abstract PocoTableRuntime Build(string sourceId, PocoNamingPolicy policy, int decimalScale);
    }

    private sealed class TableRegistration<T> : TableRegistration
    {
        private readonly PocoTableBuilder<T> _builder;
        private readonly PocoRows<T> _rows;

        public TableRegistration(PocoTableBuilder<T> builder, PocoRows<T> rows)
        {
            _builder = builder;
            _rows = rows;
        }

        public override PocoTableRuntime Build(string sourceId, PocoNamingPolicy policy, int decimalScale) =>
            _builder.Build(sourceId, policy, decimalScale, _rows);
    }
}
