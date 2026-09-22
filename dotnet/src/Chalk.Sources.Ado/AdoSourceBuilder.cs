using System.Data.Common;
using Chalk.Catalog;
using Chalk.Entitlements;
using Chalk.Ir;
using CostProfile = Chalk.Catalog.CostProfile;
using SourceCapabilities = Chalk.Catalog.SourceCapabilities;
using CatalogContext = Chalk.Catalog.CatalogContext;
using FunctionDescriptor = Chalk.Catalog.FunctionDescriptor;

namespace Chalk.Sources.Ado;

/// <summary>
/// Registers a database's tables as Chalk tables (D85).
/// </summary>
/// <example>
/// <code>
/// var crm = new AdoSourceBuilder("crm", SqliteFactory.Instance, connectionString)
///     .Dialect(DialectProfiles.Sqlite)
///     .Capabilities(AdoCapabilities.For(DialectProfiles.Sqlite))
///     .DiscoverTables()
///     .Build();
/// </code>
/// </example>
/// <remarks>
/// <para>
/// Nothing is pushed until <see cref="Capabilities"/> says so. That is deliberate: a source that has
/// only been pointed at is a source nobody has checked, and the safe reading of an undeclared
/// descriptor is "scan it". <see cref="AdoCapabilities.For"/> is the shortcut for a dialect whose
/// profile Chalk ships, and the conformance kit is how a host confirms the claim against its own
/// database.
/// </para>
/// <para>
/// No collation is ever declared. A declared collation is a promise that <em>scanning</em> the table
/// yields rows in that order, and a SQL <c>SELECT</c> without <c>ORDER BY</c> promises nothing —
/// so discovering one from a primary key would be inventing a fact.
/// </para>
/// </remarks>
public sealed class AdoSourceBuilder
{
    private readonly string _sourceId;
    private readonly string _schemaName;
    private readonly Func<DbConnection> _connect;

    // Tables whose complete shape was supplied by the host.
    private readonly List<TableRegistration> _tables = [];

    // Individual tables whose shape is obtained from the provider and may then be augmented by
    // the host. Kept as requests because RefreshAsync must perform exactly the same discovery.
    private readonly List<DiscoveredTableRegistration> _discoverTables = [];

    // Schemas whose tables are discovered wholesale.
    private readonly List<string> _discover = [];

    private readonly Dictionary<string, AdoTableBinding> _handles =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, TableEntitlementDescriptor> _entitlements =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<FunctionDescriptor> _functions = [];

    private DialectProfileDescriptor _profile = DialectProfiles.Ansi;
    private SourceCapabilities _capabilities = SourceCapabilities.None;
    private SourceOptions _options = SourceOptions.Default;
    private CostProfile _costProfile = CostProfile.Inherit;
    private RowCountMode _rowCounts = RowCountMode.Unknown;
    private IRemoteFetch _fetch = DbDataReaderFetch.Instance;
    private AdoProviderTraits? _traits;
    private bool _trustSourceRowSecurity;

    /// <param name="sourceId">Identifies this source in the catalog and in every <c>TableRef</c>.</param>
    /// <param name="factory">The provider's factory; the host references the provider package, Chalk does not.</param>
    /// <param name="connectionString">Passed to every connection this source opens.</param>
    /// <param name="schemaName">The SQL schema Chalk addresses these tables in.</param>
    public AdoSourceBuilder(
        string sourceId,
        DbProviderFactory factory,
        string connectionString,
        string schemaName = "main")
        : this(sourceId, ConnectionFrom(factory, connectionString), schemaName)
    {
    }

    /// <summary>
    /// The same, with the host supplying connections itself — for a provider with no
    /// <see cref="DbProviderFactory"/>, or one whose connections need configuring after
    /// construction. Chalk opens and disposes what the delegate returns; pooling is the provider's.
    /// </summary>
    public AdoSourceBuilder(
        string sourceId,
        Func<DbConnection> connect,
        string schemaName = "main")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ArgumentNullException.ThrowIfNull(connect);

        _sourceId = sourceId;
        _schemaName = schemaName;
        _connect = connect;
    }

    private static Func<DbConnection> ConnectionFrom(
        DbProviderFactory factory,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(connectionString);

        return () =>
        {
            var connection = factory.CreateConnection()
                ?? throw new InvalidOperationException(
                    $"{factory.GetType().Name}.CreateConnection() returned null.");

            connection.ConnectionString = connectionString;
            return connection;
        };
    }

    /// <summary>
    /// How this source spells and evaluates SQL. One of <see cref="DialectProfiles"/>, adjusted.
    /// </summary>
    public AdoSourceBuilder Dialect(DialectProfileDescriptor profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        return this;
    }

    /// <summary>
    /// What this source may be asked to do. Nothing is pushed until this is called: the default is
    /// <see cref="SourceCapabilities.None"/>, and the safe reading of a source nobody has checked is
    /// "scan it".
    /// </summary>
    public AdoSourceBuilder Capabilities(SourceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _capabilities = capabilities;
        return this;
    }

    /// <summary>Per-source timeouts and limits (D86).</summary>
    public AdoSourceBuilder Options(SourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        return this;
    }

    /// <summary>What the planner charges for work against this source (D38).</summary>
    public AdoSourceBuilder Costs(CostProfile costProfile)
    {
        ArgumentNullException.ThrowIfNull(costProfile);
        _costProfile = costProfile;
        return this;
    }

    /// <summary>
    /// How row counts are established. <see cref="RowCountMode.Unknown"/> — the default — says
    /// nothing, which is a legitimate answer (D36) and costs nothing at registration;
    /// <see cref="RowCountMode.Exact"/> runs one <c>COUNT(*)</c> per table, which is opt-in because
    /// on a large table it is not free.
    /// </summary>
    public AdoSourceBuilder RowCounts(RowCountMode mode)
    {
        _rowCounts = mode;
        return this;
    }

    /// <summary>
    /// How the source reads an executed query (D148, <c>docs/design/24-zero-gc.md</c> §6). The
    /// default is <see cref="DbDataReaderFetch"/>, which is all <c>System.Data.Common</c> offers;
    /// <c>Chalk.Sources.DuckDb</c>'s <c>AddDuckDbSource</c> sets the native DuckDB reader here.
    /// </summary>
    public AdoSourceBuilder Fetch(IRemoteFetch fetch)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        _fetch = fetch;
        return this;
    }

    /// <summary>
    /// What this source's host or a vendor package knows about its ADO.NET provider (D263, ADR 0046):
    /// today, the TEXT strategy <see cref="AdoProviderTraits.Text"/> declares instead of measuring.
    /// <c>Chalk.Sources.DuckDb</c> calls this from its own builder extension so its package's
    /// knowledge reaches the reader without naming DuckDB inside <c>Chalk.Sources.Ado</c>. A trait
    /// left undeclared is measured once per process per column type, exactly as before.
    /// </summary>
    public AdoSourceBuilder Provider(AdoProviderTraits traits)
    {
        ArgumentNullException.ThrowIfNull(traits);

        if (traits.Text == TextStrategy.Undecided)
        {
            throw new ArgumentException(
                "AdoProviderTraits.Text cannot be TextStrategy.Undecided: that value means "
                + "\"nothing was declared\", which leaving Text null already says.",
                nameof(traits));
        }

        _traits = traits;
        return this;
    }

    /// <summary>
    /// Introspects every table the provider reports, in <paramref name="schema"/> when the provider
    /// has schemas. Called more than once, the schemas add up.
    /// </summary>
    public AdoSourceBuilder DiscoverTables(string? schema = null)
    {
        _discover.Add(schema ?? string.Empty);
        return this;
    }

    /// <summary>
    /// Registers one table whose physical shape is discovered from the provider, while allowing the
    /// host to declare metadata discovery cannot supply reliably: keys, indexes, statistics and
    /// costs.
    /// </summary>
    /// <param name="name">The name Chalk addresses the table by.</param>
    /// <param name="remoteName">
    /// The name the source knows it by. Defaults to <paramref name="name"/>.
    /// </param>
    /// <param name="configure">
    /// Metadata applied after the table has been discovered. Column names are resolved against the
    /// discovered shape, so a misspelling is still a registration error.
    /// </param>
    /// <remarks>
    /// This differs from <see cref="DiscoverTables(string?)"/> in two ways: it names one table, and
    /// it creates an explicit registration that may be configured. Its columns still come from the
    /// database.
    /// </remarks>
    public AdoSourceBuilder AddTable(
        string name,
        string? remoteName = null,
        Action<AdoTableBuilder>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _discoverTables.Add(new DiscoveredTableRegistration(
            name,
            remoteName ?? name,
            configure));

        return this;
    }

    /// <summary>
    /// The discovered-shape overload of <see cref="AddTable(string,string?,Action{AdoTableBuilder}?)"/>,
    /// also returning a typed handle for later policy declarations.
    /// </summary>
    public AdoSourceBuilder AddTable(
        string name,
        out AdoTable table,
        string? remoteName = null,
        Action<AdoTableBuilder>? configure = null)
    {
        AddTable(name, remoteName, configure);
        table = Handle(name);
        return this;
    }

    /// <summary>
    /// Registers one table explicitly, overriding whatever broad discovery would have said about it.
    /// The escape hatch for a provider whose schema collections are thin, or a column whose declared
    /// type Chalk cannot map.
    /// </summary>
    /// <param name="name">The name Chalk addresses the table by.</param>
    /// <param name="columns">Its columns, in the order a scan produces them.</param>
    /// <param name="remoteName">The name the source knows it by. Defaults to <paramref name="name"/>.</param>
    /// <param name="configure">Keys, indexes, statistics and costs.</param>
    public AdoSourceBuilder AddTable(
        string name,
        IReadOnlyList<ColumnDescriptor> columns,
        string? remoteName = null,
        Action<AdoTableBuilder>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
        {
            throw new ArgumentException(
                $"Table '{name}' needs at least one column.",
                nameof(columns));
        }

        var builder = new AdoTableBuilder(name, columns, remoteName ?? name);
        configure?.Invoke(builder);
        _tables.Add(builder.Build());

        return this;
    }

    /// <summary>
    /// Registers one explicitly-shaped table and returns a typed handle for later policy
    /// declarations.
    /// </summary>
    /// <remarks>
    /// An <c>out</c> parameter rather than a different return type, so the builder's fluent chain
    /// still runs through it. A table obtained only through <see cref="DiscoverTables(string?)"/>
    /// has no registration from which to take a handle; the named <see cref="AddTable(string,
    /// string?,Action{AdoTableBuilder}?)"/> overload does.
    /// </remarks>
    public AdoSourceBuilder AddTable(
        string name,
        IReadOnlyList<ColumnDescriptor> columns,
        out AdoTable table,
        string? remoteName = null,
        Action<AdoTableBuilder>? configure = null)
    {
        AddTable(name, columns, remoteName, configure);
        table = Handle(name);
        return this;
    }

    /// <summary>The binding a handle resolves through, made once per registered name.</summary>
    private AdoTable Handle(string name)
    {
        if (!_handles.TryGetValue(name, out var binding))
        {
            binding = new AdoTableBinding
            {
                SourceId = _sourceId,
                Schema = _schemaName,
                Table = name,
            };

            _handles[name] = binding;
        }

        return new AdoTable(binding);
    }

    /// <summary>
    /// Which of a table's rows and columns a caller may see (step 26, D141,
    /// <c>docs/design/16-entitlements.md</c> §1).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddTable(string,IReadOnlyList{ColumnDescriptor},string?,
    /// Action{AdoTableBuilder}?)"/> because a remote table is commonly discovered: the shape comes
    /// from the provider and the policy from the host, and asking a host to hand-write a discovered
    /// table's columns in order to attach an entitlement to it would be a way to get the shape wrong.
    /// Naming a table this source does not have is a registration error rather than a silently
    /// ignored line.
    /// </remarks>
    /// <param name="table">The name Chalk addresses the table by.</param>
    /// <param name="entitlement">The row predicate and the per-column disclosure rules.</param>
    public AdoSourceBuilder Entitlement(
        string table,
        TableEntitlementDescriptor entitlement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(entitlement);

        _entitlements[table] = entitlement;
        return this;
    }

    /// <summary>
    /// The host trusts this source to enforce row visibility itself (D156,
    /// <c>docs/design/16-entitlements.md</c> §3.7). The entitlement pass then emits no row predicate
    /// for this source's tables — <b>and nothing else changes</b>: the column disclosures are still
    /// Chalk's, the masks are still applied here, and the report says the row predicate was not
    /// pushed, because there was none.
    /// </summary>
    /// <remarks>
    /// Off by default, which is the safe default: a source nobody has said anything about is one
    /// Chalk filters itself. Turning it on is a statement about the database's own policy, and
    /// Chalk cannot check it.
    /// </remarks>
    public AdoSourceBuilder TrustSourceRowSecurity()
    {
        _trustSourceRowSecurity = true;
        return this;
    }

    /// <summary>
    /// Declares a function on this schema (D77, D81). A <c>Native</c> body is the one that matters
    /// here: the source evaluates it itself, and its name is added to the capability list so the two
    /// claims cannot disagree.
    /// </summary>
    public AdoSourceBuilder AddFunction(
        string name,
        Action<FunctionBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new FunctionBuilder(name);
        configure(builder);
        _functions.Add(builder.Build());

        return this;
    }

    /// <summary>
    /// Opens one connection, reads the schema, and produces the source. Everything that can go wrong
    /// with a registration goes wrong here rather than at the first query.
    /// </summary>
    /// <remarks>
    /// Synchronous on purpose. Registration is a startup activity, it happens once, and the
    /// alternative — an asynchronous builder with a blocking twin — would mean either a
    /// <c>GetAwaiter().GetResult()</c> in a library or two copies of the discovery code. ADO.NET's
    /// synchronous API is the one every provider implements.
    /// </remarks>
    public AdoSource Build()
    {
        // A native function the source runs itself has to be in `native_functions` for the pushdown
        // gate to allow it; declaring it twice would be a way to get that wrong, so the builder
        // derives the list rather than asking for it.
        var native = _functions
            .Where(f => f.Body is NativeFunctionBody)
            .Select(f => f.Name)
            .Where(n => !_capabilities.NativeFunctions.Contains(
                n,
                StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (native.Length > 0)
        {
            _capabilities = new SourceCapabilities
            {
                QueryLanguage = _capabilities.QueryLanguage,
                PushablePredicates = _capabilities.PushablePredicates,
                PushableFunctions = _capabilities.PushableFunctions,
                PushableAggregates = _capabilities.PushableAggregates,
                NativeFunctions = [.. _capabilities.NativeFunctions, .. native],
                UnsupportedFunctions = _capabilities.UnsupportedFunctions,
                SupportsProject = _capabilities.SupportsProject,
                SupportsSort = _capabilities.SupportsSort,
                SupportsLimit = _capabilities.SupportsLimit,
                SupportsOffset = _capabilities.SupportsOffset,
                SupportsDistinct = _capabilities.SupportsDistinct,
                SupportsGroupBy = _capabilities.SupportsGroupBy,
                SupportsHaving = _capabilities.SupportsHaving,
                SupportsInnerJoin = _capabilities.SupportsInnerJoin,
                SupportsOuterJoin = _capabilities.SupportsOuterJoin,
                SupportsSemiAntiJoin = _capabilities.SupportsSemiAntiJoin,
                MaxPushdownRows = _capabilities.MaxPushdownRows,
                MaxInList = _capabilities.MaxInList,
                SupportsParameters = _capabilities.SupportsParameters,
            };
        }

        // The same work RefreshAsync does, which is why it is a closure rather than a block here:
        // a re-introspection has to reach exactly the tables the first one did, and a second code
        // path that drifted from this one would be a catalog that disagrees with itself (D86).
        var introspect = Introspect;
        var tables = introspect();

        var refreshes =
            _discover.Count > 0
            || _discoverTables.Count > 0
            || _rowCounts != RowCountMode.Unknown;

        var source = new AdoSource(
            _sourceId,
            _schemaName,
            _connect,
            _profile,
            _capabilities,
            _options,
            tables,
            _costProfile,
            refreshes ? introspect : null,
            [.. _functions],
            _fetch,
            _traits,
            _trustSourceRowSecurity);

        // The handles taken at registration acquire the source they name now that there is one
        // (D271 (h)).
        foreach (var binding in _handles.Values)
        {
            binding.Bind(source);
        }

        // The same rules the planner applies, run here so a contradictory descriptor or an unmapped
        // column is a registration error rather than a planner one.
        CatalogValidator.Validate(new CatalogContext
        {
            ContextId = _sourceId,
            Epoch = 0,
            Schemas = [source.DescribeSchema()],
        });

        return source;
    }

    /// <summary>
    /// The tables as the database has them now: the hand-registered ones, the individually
    /// discovered-and-configured ones, then whatever broad discovery finds that neither already
    /// named, then exact row counts if they were asked for.
    /// </summary>
    private AdoTableDefinition[] Introspect()
    {
        var registrations = new List<TableRegistration>(_tables);

        var needsConnection =
            _discoverTables.Count > 0
            || _discover.Count > 0
            || _rowCounts != RowCountMode.Unknown;

        if (needsConnection)
        {
            using var connection = _connect();
            connection.Open();

            // Cache per-schema discovery for this introspection. Named discovery and broad
            // discovery commonly name the same schema; asking the provider for it twice buys
            // nothing and some providers make GetSchema surprisingly expensive.
            var discoveredBySchema =
                new Dictionary<string, TableRegistration[]>(
                    StringComparer.OrdinalIgnoreCase);

            TableRegistration[] Discover(string schema)
            {
                if (!discoveredBySchema.TryGetValue(schema, out var discovered))
                {
                    discovered =
                    [
                        .. AdoDiscovery.Tables(
                            connection,
                            schema,
                            _profile),
                    ];

                    discoveredBySchema[schema] = discovered;
                }

                return discovered;
            }

            // A named AddTable without supplied columns means "discover this shape, then apply the
            // host's additions". The AdoTableBuilder is deliberately not constructed until here:
            // its existing name-to-column validation therefore remains authoritative.
            if (_discoverTables.Count > 0)
            {
                var discovered = Discover(_schemaName);

                foreach (var request in _discoverTables)
                {
                    if (registrations.Any(r => string.Equals(
                        r.Descriptor.Name,
                        request.Name,
                        StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException(
                            $"Source '{_sourceId}' registers table '{request.Name}' more than once. "
                            + "Use either the explicit-column AddTable overload or the discovered "
                            + "AddTable overload for a given Chalk table name.");
                    }

                    TableRegistration? match = null;

                    foreach (var candidate in discovered)
                    {
                        if (!string.Equals(
                                candidate.RemoteName,
                                request.RemoteName,
                                StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(
                                candidate.Descriptor.Name,
                                request.RemoteName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (match is not null)
                        {
                            throw new InvalidOperationException(
                                $"Source '{_sourceId}' found more than one table named "
                                + $"'{request.RemoteName}' while registering '{request.Name}'. "
                                + "Specify a schema whose table name is unambiguous.");
                        }

                        match = candidate;
                    }

                    if (match is null)
                    {
                        throw new InvalidOperationException(
                            $"Source '{_sourceId}' has no table '{request.RemoteName}' "
                            + $"to register as '{request.Name}'.");
                    }

                    var builder = new AdoTableBuilder(
                        request.Name,
                        match,
                        request.RemoteName);

                    request.Configure?.Invoke(builder);
                    registrations.Add(builder.Build());
                }
            }

            // Broad discovery adds only tables not already explicitly or individually registered.
            foreach (var schema in _discover)
            {
                foreach (var discovered in Discover(schema))
                {
                    var alreadyRegistered = registrations.Any(t =>
                        string.Equals(
                            t.Descriptor.Name,
                            discovered.Descriptor.Name,
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            t.RemoteName,
                            discovered.RemoteName,
                            StringComparison.OrdinalIgnoreCase));

                    if (!alreadyRegistered)
                    {
                        registrations.Add(discovered);
                    }
                }
            }

            if (_rowCounts == RowCountMode.Exact)
            {
                for (var i = 0; i < registrations.Count; i++)
                {
                    var count = AdoDiscovery.Count(
                        connection,
                        registrations[i].RemoteName,
                        _profile);

                    registrations[i] = registrations[i] with
                    {
                        Descriptor = With(
                            registrations[i].Descriptor,
                            count,
                            RowCountKind.Exact),
                    };
                }
            }
        }

        if (registrations.Count == 0)
        {
            throw new InvalidOperationException(
                $"Source '{_sourceId}' has no tables. Call DiscoverTables or AddTable before Build.");
        }

        ResolveForeignKeys(registrations);
        Entitle(registrations);

        return
        [
            .. registrations.Select(r =>
                new AdoTableDefinition(
                    r.Descriptor,
                    r.RemoteName)),
        ];
    }

    /// <summary>
    /// Attaches the host's entitlements to the tables they name, once discovery has settled which
    /// tables there are. A name that matches none of them is a registration error.
    /// </summary>
    private void Entitle(List<TableRegistration> registrations)
    {
        if (_entitlements.Count == 0)
        {
            return;
        }

        var attached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < registrations.Count; i++)
        {
            var descriptor = registrations[i].Descriptor;

            if (!_entitlements.TryGetValue(
                    descriptor.Name,
                    out var entitlement))
            {
                continue;
            }

            attached.Add(descriptor.Name);

            registrations[i] = registrations[i] with
            {
                Descriptor = new TableDescriptor
                {
                    Name = descriptor.Name,
                    Columns = descriptor.Columns,
                    RowCount = descriptor.RowCount,
                    RowCountKind = descriptor.RowCountKind,
                    UniqueKeys = descriptor.UniqueKeys,
                    Indexes = descriptor.Indexes,
                    ForeignKeys = descriptor.ForeignKeys,
                    CostProfile = descriptor.CostProfile,
                    Partitioning = descriptor.Partitioning,
                    Collations = descriptor.Collations,
                    IsPublic = descriptor.IsPublic,
                    Entitlement = entitlement,
                },
            };
        }

        foreach (var name in _entitlements.Keys)
        {
            if (!attached.Contains(name))
            {
                throw new InvalidOperationException(
                    $"Source '{_sourceId}' has no table '{name}' to attach an entitlement to. "
                    + "Register or discover the table first; a policy on a table that is not there "
                    + "is a policy that enforces nothing.");
            }
        }
    }

    /// <summary>
    /// Turns each declared foreign key into a descriptor once every table is known: the parent is
    /// looked up by name and its columns by column name. The same job the POCO builder does, and the
    /// same reason — a child may be registered before its parent.
    /// </summary>
    private void ResolveForeignKeys(List<TableRegistration> registrations)
    {
        for (var t = 0; t < registrations.Count; t++)
        {
            var registration = registrations[t];

            if (registration.ForeignKeys.Count == 0)
            {
                continue;
            }

            var resolved =
                new List<ForeignKeyDescriptor>(
                    registration.ForeignKeys.Count);

            foreach (var key in registration.ForeignKeys)
            {
                var parent = registrations.Find(r =>
                    string.Equals(
                        r.Descriptor.Name,
                        key.ParentTable,
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        r.RemoteName,
                        key.ParentTable,
                        StringComparison.OrdinalIgnoreCase))
                    ?? throw new CatalogValidationException(
                        $"table '{registration.Descriptor.Name}'",
                        $"ForeignKey(…) names the parent table '{key.ParentTable}', which is not "
                        + $"registered on source '{_sourceId}'.");

                var parentColumns = new int[key.ParentColumns.Length];

                for (var i = 0; i < parentColumns.Length; i++)
                {
                    parentColumns[i] =
                        parent.Descriptor.IndexOfColumn(
                            key.ParentColumns[i]);

                    if (parentColumns[i] < 0)
                    {
                        throw new CatalogValidationException(
                            $"table '{registration.Descriptor.Name}'",
                            $"ForeignKey(…) names '{key.ParentColumns[i]}' on parent table "
                            + $"'{parent.Descriptor.Name}', which has no such column.");
                    }
                }

                resolved.Add(new ForeignKeyDescriptor
                {
                    Name = key.Name,
                    Columns = key.Columns,
                    ParentTable = parent.Descriptor.Name,
                    ParentColumns = parentColumns,
                });
            }

            registrations[t] = registration with
            {
                Descriptor = WithForeignKeys(
                    registration.Descriptor,
                    resolved),
            };
        }
    }

    private static TableDescriptor WithForeignKeys(
        TableDescriptor table,
        IReadOnlyList<ForeignKeyDescriptor> keys) => new()
    {
        Name = table.Name,
        Columns = table.Columns,
        RowCount = table.RowCount,
        RowCountKind = table.RowCountKind,
        CostProfile = table.CostProfile,
        UniqueKeys = table.UniqueKeys,
        Collations = table.Collations,
        Indexes = table.Indexes,
        ForeignKeys = keys,
        Partitioning = table.Partitioning,
        IsPublic = table.IsPublic,
        Entitlement = table.Entitlement,
    };

    private static TableDescriptor With(
        TableDescriptor table,
        long rowCount,
        RowCountKind kind) => new()
    {
        Name = table.Name,
        Columns = table.Columns,
        RowCount = rowCount,
        RowCountKind = kind,
        CostProfile = table.CostProfile,
        UniqueKeys = table.UniqueKeys,
        Collations = table.Collations,
        Indexes = table.Indexes,
        ForeignKeys = table.ForeignKeys,
        Partitioning = table.Partitioning,
        IsPublic = table.IsPublic,
        Entitlement = table.Entitlement,
    };

    /// <summary>How much a table's row count is worth, and what it costs to find out.</summary>
    public enum RowCountMode
    {
        /// <summary>Say nothing. The default: free, and a legitimate answer (D36).</summary>
        Unknown = 0,

        /// <summary>One <c>COUNT(*)</c> per table at registration. Exact, and not free.</summary>
        Exact = 1,
    }
}

/// <summary>
/// A table whose physical shape is obtained from the provider and augmented after discovery.
/// </summary>
internal sealed record DiscoveredTableRegistration(
    string Name,
    string RemoteName,
    Action<AdoTableBuilder>? Configure);

/// <summary>A discovered or declared table, before it becomes an <see cref="AdoTableDefinition"/>.</summary>
internal sealed record TableRegistration(
    TableDescriptor Descriptor,
    string RemoteName,
    IReadOnlyList<DeclaredForeignKey> ForeignKeys);

/// <summary>A foreign key whose parent columns are still names, because the parent may come later.</summary>
internal sealed record DeclaredForeignKey(
    string Name,
    int[] Columns,
    string ParentTable,
    string[] ParentColumns);

/// <summary>Keys, indexes and statistics for a table registered by hand or augmented after discovery.</summary>
public sealed class AdoTableBuilder
{
    private readonly string _name;
    private readonly IReadOnlyList<ColumnDescriptor> _columns;
    private readonly string _remoteName;

    private readonly List<UniqueKeyDescriptor> _uniqueKeys = [];
    private readonly List<IndexDescriptor> _indexes = [];
    private readonly List<DeclaredForeignKey> _foreignKeys = [];

    private long _rowCount = -1;
    private RowCountKind _rowCountKind = RowCountKind.Unknown;
    private CostProfile _costProfile = CostProfile.Inherit;

    /// <summary>
    /// The originally discovered descriptor, when this builder is augmenting an existing table.
    /// Kept so metadata the builder does not itself expose is not accidentally discarded.
    /// </summary>
    private readonly TableDescriptor? _discovered;

    internal AdoTableBuilder(
        string name,
        IReadOnlyList<ColumnDescriptor> columns,
        string remoteName)
    {
        _name = name;
        _columns = columns;
        _remoteName = remoteName;
    }

    /// <summary>
    /// Builds on a provider-discovered table. Discovery establishes the shape and whatever metadata
    /// the provider supplied; calls on this builder augment or override that metadata.
    /// </summary>
    internal AdoTableBuilder(
        string name,
        TableRegistration discovered,
        string remoteName)
    {
        ArgumentNullException.ThrowIfNull(discovered);

        _name = name;
        _columns = discovered.Descriptor.Columns;
        _remoteName = remoteName;
        _discovered = discovered.Descriptor;

        _uniqueKeys.AddRange(discovered.Descriptor.UniqueKeys);
        _indexes.AddRange(discovered.Descriptor.Indexes);
        _foreignKeys.AddRange(discovered.ForeignKeys);

        _rowCount = discovered.Descriptor.RowCount;
        _rowCountKind = discovered.Descriptor.RowCountKind;
        _costProfile = discovered.Descriptor.CostProfile;
    }

    /// <summary>Declares that these columns together identify a row.</summary>
    public AdoTableBuilder UniqueKey(params ReadOnlySpan<string> columns)
    {
        if (columns == ReadOnlySpan<string>.Empty) 
            throw new ArgumentNullException(nameof(columns));

        var resolved = Resolve(columns);

        // A provider may already have discovered the same primary/unique key. The host affirming it
        // should not turn one fact into two identical descriptors.
        _uniqueKeys.RemoveAll(
            key => key.Columns.SequenceEqual(resolved));

        _uniqueKeys.Add(new UniqueKeyDescriptor
        {
            Columns = resolved,
        });

        return this;
    }

    /// <summary>
    /// Declares an index. A remote index feeds selectivity and cost only: an index lookup on a
    /// remote table is a pushed filter, not an <c>IndexLookup</c> node (§2).
    /// </summary>
    public AdoTableBuilder Index(
        string name,
        bool unique,
        params ReadOnlySpan<string> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (columns == ReadOnlySpan<string>.Empty) 
            throw new ArgumentNullException(nameof(columns));
        
        var resolved = Resolve(columns);

        // Configuration is authoritative for a named index. This lets a host correct or enrich
        // incomplete provider metadata without creating two descriptors with the same name.
        _indexes.RemoveAll(index =>
            string.Equals(
                index.Name,
                name,
                StringComparison.OrdinalIgnoreCase));

        _indexes.Add(new IndexDescriptor
        {
            Name = name,
            Kind = IndexKind.Ordered,
            Unique = unique,
            Columns = resolved,
        });

        return this;
    }

    /// <summary>Declares a referential constraint to another table in this source.</summary>
    public AdoTableBuilder ForeignKey(
        string name,
        string[] columns,
        string parentTable,
        string[] parentColumns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentTable);
        ArgumentNullException.ThrowIfNull(parentColumns);

        // The parent's own column indexes cannot be resolved here — the parent may not be
        // registered yet — so the names are kept and resolved at Build, like the POCO source's.
        var resolved = Resolve(columns);

        _foreignKeys.RemoveAll(key =>
            string.Equals(
                key.Name,
                name,
                StringComparison.OrdinalIgnoreCase));

        _foreignKeys.Add(new DeclaredForeignKey(
            name,
            resolved,
            parentTable,
            parentColumns));

        return this;
    }

    /// <summary>How many rows the table holds, and how much that number is worth (D36).</summary>
    public AdoTableBuilder RowCount(
        long rows,
        RowCountKind kind = RowCountKind.Estimate)
    {
        _rowCount = rows;
        _rowCountKind = kind;
        return this;
    }

    /// <summary>What the planner charges for work against this table (D38).</summary>
    public AdoTableBuilder Costs(CostProfile costProfile)
    {
        _costProfile = costProfile;
        return this;
    }

    internal TableRegistration Build() => new(
        new TableDescriptor
        {
            Name = _name,
            Columns = _columns,
            RowCount = _rowCount,
            RowCountKind = _rowCountKind,
            UniqueKeys = _uniqueKeys,
            Indexes = _indexes,
            CostProfile = _costProfile,

            // These are not currently configurable through AdoTableBuilder, but named discovery
            // must not erase them if a provider supplied them.
            Collations = _discovered?.Collations ?? [],
            Partitioning = _discovered?.Partitioning,
            IsPublic = _discovered?.IsPublic ?? false,
            Entitlement = _discovered?.Entitlement,
        },
        _remoteName,
        _foreignKeys);

    private int[] Resolve(params ReadOnlySpan<string> columns)
    {
        var indexes = new int[columns.Length];

        for (var i = 0; i < columns.Length; i++)
        {
            var found = -1;

            for (var c = 0; c < _columns.Count; c++)
            {
                if (string.Equals(
                    _columns[c].Name,
                    columns[i],
                    StringComparison.OrdinalIgnoreCase))
                {
                    found = c;
                    break;
                }
            }

            indexes[i] = found >= 0
                ? found
                : throw new CatalogValidationException(
                    $"table '{_name}'",
                    $"there is no column named '{columns[i]}'. Columns: "
                    + string.Join(", ", _columns.Select(c => c.Name))
                    + ".");
        }

        return indexes;
    }
}