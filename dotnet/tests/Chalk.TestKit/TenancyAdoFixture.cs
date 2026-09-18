using System.Data.Common;
using System.Globalization;
using Chalk.Catalog;
using Chalk.Entitlements;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using DuckDB.NET.Data;

namespace Chalk.TestKit;

/// <summary>
/// Where §3.13's two tables live, which is what decides whether the join a <c>through</c> compiles
/// into can go to one source (D229) or has to reach across a boundary (F52).
/// </summary>
public enum ThroughPlacement
{
    /// <summary>Both in the database: the join, the parent's predicate and the key condition push as one remote query.</summary>
    OneSource,

    /// <summary><c>threads</c> in process and <c>messages</c> in the database: the parent's visible keys travel as a key set.</summary>
    ParentInProcess,

    /// <summary>The reverse — <c>threads</c> in the database and <c>messages</c> in process, where there is no source to ship a key set to.</summary>
    ChildInProcess,
}

/// <summary>
/// Where the three tables of a <c>Related</c> path live (design 38 §5, §8; D265 clause (h)).
/// </summary>
public enum MarketplacePlacement
{
    /// <summary>
    /// All three in the database beside the target, where the whole key set — the bridge, the join
    /// down to the endpoint and the endpoint predicate — pushes as one remote query.
    /// </summary>
    OneSource,

    /// <summary>
    /// §8's own two-source case, which is the tutorial's layout (design 39 §4): the <b>target and
    /// the bridge</b> co-located in the database and <c>items</c> and <c>vendors</c> in process. The
    /// endpoint's keys that satisfy its predicate travel to the bridge's source as a key set, and
    /// the semi-join back to the target pushes with it (§5).
    /// </summary>
    EndpointInProcess,
}

/// <summary>
/// §8's query 16: the <c>tenancy</c> fixture with <c>members</c> and <c>orders</c> in a real
/// database, reached through the in-box ADO.NET source.
/// </summary>
/// <remarks>
/// <para>
/// The same rows and the <em>same descriptors</em> as <see cref="TenancyFixture"/> — they are read
/// from it rather than repeated — so a difference between the two fixtures' answers is a difference
/// the remote path introduced and nothing else. That is what makes this a differential rather than
/// a second set of expectations to keep in step.
/// </para>
/// <para>
/// What only this fixture can show: the folded tenancy predicate leaving the process as an IN list
/// in the source's own dialect, the mask staying behind, and <c>row_predicate_pushed</c> being true
/// about something. A POCO table has no source to push into, so run 2 could only report the flag
/// honestly false.
/// </para>
/// <para>
/// DuckDB always; PostgreSQL when <see cref="PostgresFixture"/> has a server, which
/// <c>CHALK_TEST_POSTGRES_REQUIRED</c> makes compulsory. The two are the same fixture over two real
/// dialects, and the point of the second is that the SQL is generated for a dialect nobody wrote by
/// hand.
/// </para>
/// </remarks>
public sealed class TenancyAdoFixture : IDisposable
{
    /// <summary>The remote source's id, and its schema name.</summary>
    public const string SourceId = "warehouse";

    /// <summary>The in-process schema's name, which is where a cross-source parent lives.</summary>
    public const string LocalSchema = "main";

    private readonly List<DbConnection> _connections = [];
    private readonly List<DuckDbFile> _files = [];
    private bool _disposed;

    private readonly ThroughPlacement _placement;

    private TenancyAdoFixture(
        PocoSource local, AdoSource remote, string dialect, ThroughPlacement placement)
    {
        Local = local;
        Remote = remote;
        Dialect = dialect;
        _placement = placement;
    }

    /// <summary>Reference data the statement joins to, in process: it carries no entitlement.</summary>
    public PocoSource Local { get; }

    /// <summary>The entitled tables, in a real database.</summary>
    public AdoSource Remote { get; }

    /// <summary>Which dialect the generated SQL is in — <c>duckdb</c> or <c>postgresql</c>.</summary>
    public string Dialect { get; }

    /// <summary>
    /// The remote source first, so its schema is the default one (A4) and a statement reads
    /// <c>members</c> by the same unqualified name it uses against the in-process fixture. That is
    /// what makes the two comparable statement for statement rather than fixture for fixture — and
    /// where the <em>child</em> is the table in process, it is that source's schema the statement
    /// has to name unqualified, so the order follows it.
    /// </summary>
    public IReadOnlyList<ISourceRuntime> Sources =>
        _placement == ThroughPlacement.ChildInProcess ? [Local, Remote] : [Remote, Local];

    /// <summary>
    /// The declaration design 38 §8's two-source layout needs (F84,
    /// <c>docs/design/45-typed-tenancy-surface.md</c> §3, D270 (c)): the line names an item of the
    /// other source, and no foreign key of <c>order_items</c> can say so because a foreign key names
    /// a table of its own schema.
    /// </summary>
    /// <remarks>
    /// Handed to the engine beside the two sources, and not carried by either of them: neither
    /// source owns a claim about the other's table, so the catalog the host assembles is where it
    /// belongs (ADR 0056 §5). A run that leaves it out is
    /// <c>TenancyRemoteBindingTests.The_endpoint_in_another_source_is_refused_at_registration</c>,
    /// which is the refusal this is the answer to.
    /// </remarks>
    public static IReadOnlyList<AssociationDescriptor> MarketplaceAssociation { get; } =
    [
        new AssociationDescriptor
        {
            Name = "line_item",
            FromSchema = SourceId,
            FromTable = "order_items",
            FromColumn = "item_id",
            ToSchema = LocalSchema,
            ToTable = "items",
            ToColumn = "id",
        },
    ];

    /// <summary>The shared DuckDB fixture under the default enforcement, built once per run.</summary>
    public static TenancyAdoFixture Shared => LazyShared.Value;

    private static readonly Lazy<TenancyAdoFixture> LazyShared = new(() => CreateDuckDb());

    /// <summary>
    /// A DuckDB copy of the two entitled tables.
    /// </summary>
    /// <param name="enforcement">
    /// <c>Pushdown</c> (the default), <c>Local</c> — no predicate of the table reaches the source —
    /// or <c>PushdownRequired</c>.
    /// </param>
    /// <param name="pushMasks">The table's half of D153's mask opt-in.</param>
    /// <param name="trustSourceRowSecurity">D156: the host trusts this source's own row security.</param>
    /// <param name="capabilities">
    /// What the source declares. Null takes the dialect's own, which is what a host gets.
    /// </param>
    /// <param name="placement">
    /// §3.13's cross-source half: which of the two tables stays in process, so the child's join to
    /// its parent crosses a source boundary (D229, F52). The default puts both in the database,
    /// where the join pushes as one remote query.
    /// </param>
    public static TenancyAdoFixture CreateDuckDb(
        Enforcement enforcement = Enforcement.Pushdown,
        bool pushMasks = false,
        bool trustSourceRowSecurity = false,
        SourceCapabilities? capabilities = null,
        ThroughPlacement placement = ThroughPlacement.OneSource,
        MarketplacePlacement marketplace = MarketplacePlacement.OneSource)
    {
        var file = DuckDbFile.Create("tenancy");
        var fixture = Create(
            DialectProfiles.DuckDb,
            file.ConnectionString,
            connection => new DuckDBConnection(connection),
            enforcement,
            pushMasks,
            trustSourceRowSecurity,
            capabilities,
            placement,
            marketplace);
        fixture._files.Add(file);
        return fixture;
    }

    /// <summary>
    /// The same in PostgreSQL. The provider is injected because <c>Chalk.TestKit</c> does not
    /// reference Npgsql — it is the integration project's, exactly as it is a host's.
    /// </summary>
    public static TenancyAdoFixture CreatePostgres(
        string connectionString,
        Func<string, DbConnection> connect,
        Enforcement enforcement = Enforcement.Pushdown,
        bool pushMasks = false,
        bool trustSourceRowSecurity = false,
        SourceCapabilities? capabilities = null,
        ThroughPlacement placement = ThroughPlacement.OneSource,
        MarketplacePlacement marketplace = MarketplacePlacement.OneSource) =>
        Create(
            DialectProfiles.PostgreSql,
            connectionString,
            connect,
            enforcement,
            pushMasks,
            trustSourceRowSecurity,
            capabilities,
            placement,
            marketplace);

    private static TenancyAdoFixture Create(
        DialectProfileDescriptor profile,
        string connectionString,
        Func<string, DbConnection> connect,
        Enforcement enforcement,
        bool pushMasks,
        bool trustSourceRowSecurity,
        SourceCapabilities? capabilities,
        ThroughPlacement placement,
        MarketplacePlacement marketplace)
    {
        // One connection held open for the fixture's life, as the corpus fixture does: an in-memory
        // or file database that nothing is connected to is a database that may not be there.
        var keepAlive = connect(connectionString);
        keepAlive.Open();

        Load(keepAlive, profile, placement, marketplace);

        var builder = new AdoSourceBuilder(SourceId, () => connect(connectionString), SourceId)
            .Dialect(profile)
            .Options(TestTimeouts.SourceOptions)
            .Capabilities(capabilities ?? AdoCapabilities.For(profile))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .DiscoverTables()
            .Entitlement("members", Entitled(TenancyFixture.MembersEntitlement(), enforcement, pushMasks))
            // §8's two-source layout names where `items` lives, so the path's last step and its
            // endpoint resolve in the process rather than in the database (§5, D229).
            .Entitlement(
                "orders",
                Entitled(
                    TenancyFixture.OrdersEntitlement(
                        marketplace == MarketplacePlacement.EndpointInProcess ? LocalSchema : ""),
                    enforcement,
                    pushMasks))
            // §8's query 16 puts a client-bodied predicate beside the tenancy conjunct. It is
            // declared on this schema because it is the default one — the remote source comes first
            // so a statement names `members` the way it does in process — and a client body can
            // never be pushed, so the mixed filter has to split or the whole table comes back.
            .AddFunction("is_vip", f => f.Scalar<int, bool>("id").Strict().Client())
            // The adversarial battery reaches `members` through a SQL body and hands a protected
            // column to a client body (D251 class 4); both are declared here so the same statement
            // resolves over the database as it does in process.
            .AddFunction("members_in", f => f
                .TableFunction()
                .Parameter<int>("org")
                .Column("id", ChalkType.Int32())
                .Column("first_name", ChalkType.String(nullable: true))
                .Sql("SELECT id, first_name FROM members WHERE org_id = org"))
            .AddFunction("echo", f => f.Scalar<string, string>("s").Strict().Client());

        // §3.13: the child and — unless the caller wants them apart — its parent, both here, so the
        // join, the parent's folded predicate and the markers push as one remote query.
        if (placement != ThroughPlacement.ChildInProcess)
        {
            builder.Entitlement(
                "messages",
                Entitled(
                    TenancyFixture.MessagesEntitlement(
                        placement == ThroughPlacement.ParentInProcess ? LocalSchema : ""),
                    enforcement,
                    pushMasks));
        }

        if (placement != ThroughPlacement.ParentInProcess)
        {
            // Declared rather than discovered, for its unique key: a `through` parent must have one
            // (D226) and only the SQLite route reads a primary key back out of a database.
            builder.AddTable(
                "threads",
                [
                    new ColumnDescriptor { Name = "id", Type = ChalkType.Int32() },
                    new ColumnDescriptor { Name = "org_id", Type = ChalkType.Int32() },
                    new ColumnDescriptor { Name = "member_id", Type = ChalkType.Int32() },
                ],
                configure: t => t.UniqueKey("id"));
            builder.Entitlement(
                "threads", Entitled(TenancyFixture.ThreadsEntitlement(), enforcement, pushMasks));
        }

        // D265 clause (h)'s three tables, declared rather than discovered for the same reason
        // `threads` is: a path's registration reads the *declared* unique keys and foreign keys of
        // every table on its route (§3), and only the SQLite route reads either back out of a
        // database. `orders` is redeclared for its own unique key, which the first step of a
        // `Related` path joins.
        builder.AddTable(
            "orders",
            [
                new ColumnDescriptor { Name = "id", Type = ChalkType.Int32() },
                new ColumnDescriptor { Name = "member_id", Type = ChalkType.Int32() },
                new ColumnDescriptor { Name = "org_id", Type = ChalkType.Int32() },
                new ColumnDescriptor { Name = "amount", Type = ChalkType.Int64() },
                new ColumnDescriptor { Name = "note", Type = ChalkType.String(nullable: true) },
                new ColumnDescriptor { Name = "created_by", Type = ChalkType.Int32() },
                new ColumnDescriptor { Name = "region_id", Type = ChalkType.Int32() },
            ],
            configure: t => t.UniqueKey("id"));
        builder.AddTable(
            "order_items",
            [
                new ColumnDescriptor { Name = "id", Type = ChalkType.Int32() },
                new ColumnDescriptor { Name = "order_id", Type = ChalkType.Int32() },
                new ColumnDescriptor { Name = "item_id", Type = ChalkType.Int32() },
                new ColumnDescriptor { Name = "quantity", Type = ChalkType.Int32() },
                new ColumnDescriptor { Name = "unit_price", Type = ChalkType.Int64() },
            ],
            configure: t =>
            {
                t.UniqueKey("id").ForeignKey("line_order", ["order_id"], "orders", ["id"]);
                // The step to the endpoint is declared only where the endpoint is here: a foreign
                // key may not name a table of another schema, which is what makes the two-source
                // case a key-set exchange rather than one remote query (§5, D229).
                if (marketplace != MarketplacePlacement.EndpointInProcess)
                {
                    t.ForeignKey("line_item", ["item_id"], "items", ["id"]);
                }
            });
        if (marketplace != MarketplacePlacement.EndpointInProcess)
        {
            builder.AddTable(
                "vendors",
                [
                    new ColumnDescriptor { Name = "id", Type = ChalkType.Int32() },
                    new ColumnDescriptor { Name = "name", Type = ChalkType.String() },
                ],
                configure: t => t.UniqueKey("id"));
            builder.AddTable(
                "items",
                [
                    new ColumnDescriptor { Name = "id", Type = ChalkType.Int32() },
                    new ColumnDescriptor { Name = "vendor_id", Type = ChalkType.Int32() },
                    new ColumnDescriptor { Name = "name", Type = ChalkType.String() },
                ],
                configure: t =>
                    t.UniqueKey("id").ForeignKey("item_vendor", ["vendor_id"], "vendors", ["id"]));
        }

        // D265 clause (h). The bridge is entitled here whichever placement this is; the endpoint
        // and its own endpoint are entitled here only when they are here. A path's steps are
        // resolved by the registration check against the catalog as a whole, so the schema the
        // endpoint lives in is what tells the two placements apart (§3, §5).
        builder.Entitlement(
            "order_items",
            Entitled(
                TenancyFixture.OrderItemsEntitlement(
                    marketplace == MarketplacePlacement.EndpointInProcess ? LocalSchema : ""),
                enforcement,
                pushMasks));
        if (marketplace != MarketplacePlacement.EndpointInProcess)
        {
            builder.Entitlement(
                "vendors", Entitled(TenancyFixture.VendorsEntitlement(), enforcement, pushMasks));
            builder.Entitlement(
                "items", Entitled(TenancyFixture.ItemsEntitlement(), enforcement, pushMasks));
        }

        if (trustSourceRowSecurity)
        {
            builder.TrustSourceRowSecurity();
        }

        var fixture = new TenancyAdoFixture(
            LocalSource(placement, enforcement, marketplace),
            builder.Build(),
            profile.Dialect,
            placement);
        fixture._connections.Add(keepAlive);
        return fixture;
    }

    /// <summary>The descriptor with this run's enforcement and mask opt-in, and nothing else moved.</summary>
    private static TableEntitlementDescriptor Entitled(
        TableEntitlementDescriptor descriptor, Enforcement enforcement, bool pushMasks) => new()
    {
        RowPredicate = descriptor.RowPredicate,
        Through = descriptor.Through,
        // D265 clause (h): a declared path travels with the descriptor like a `through` parent, or
        // the rules that read its endpoint would have nothing in scope to read.
        Inherited = descriptor.Inherited,
        Columns = descriptor.Columns,
        DefaultDisclosure = descriptor.DefaultDisclosure,
        Enforcement = enforcement,
        PushMasks = pushMasks,
    };

    /// <summary>
    /// The tables the statement joins to, unentitled and in process: the organisations a member
    /// belongs to, and the reference data §8's query 15 reads with no grants at all.
    /// </summary>
    private static PocoSource LocalSource(
        ThroughPlacement placement,
        Enforcement enforcement,
        MarketplacePlacement marketplace)
    {
        var builder = new PocoSourceBuilder("mem")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("orgs", TenancyFixture.Orgs, t => t.OrderedBy(o => o.Id).UniqueKey(o => o.Id))
            .AddTable(
                "symbols",
                TenancyFixture.Symbols,
                t => t.OrderedBy(s => s.Name).UniqueKey(s => s.Name))
            // §8's query 16 puts a client-bodied predicate beside the tenancy conjunct. It belongs
            // to no source and can never be pushed, so the filter has to split or the whole table
            // comes back — which is the case the residual rule exists for.
            .AddFunction("is_vip", f => f.Scalar<int, bool>("id").Strict().Client())
            .AddFunction("echo", f => f.Scalar<string, string>("s").Strict().Client());

        // The cross-source half of §3.13: the parent here and the child in the database, so the
        // planner has to reach the parent's visible keys through M5's strategies (D229, F52).
        if (placement == ThroughPlacement.ParentInProcess)
        {
            builder.AddTable("threads", TenancyFixture.Threads, t =>
            {
                t.OrderedBy(x => x.Id).UniqueKey(x => x.Id);
                t.Entitlement(TenancyFixture.ThreadsEntitlement());
            });
        }

        // And the reverse: the child here and the parent in the database, where there is no source
        // to ship a key set to and the parent's own entitled scan is the only remote query. No
        // foreign key, because it would name a table of another schema, which the catalog refuses.
        if (placement == ThroughPlacement.ChildInProcess)
        {
            builder.AddTable("messages", TenancyFixture.Messages, t =>
            {
                t.OrderedBy(m => m.Id).UniqueKey(m => m.Id);
                t.Entitlement(Entitled(
                    TenancyFixture.MessagesEntitlement(SourceId), enforcement, pushMasks: false));
            });
        }

        // §8's two-source case: the endpoint of `orders`' path and the vendor's own row in process,
        // so the endpoint's keys travel to the bridge's source as a key set and the semi-join back
        // to the target pushes with it (§5, design 39 §4).
        if (marketplace == MarketplacePlacement.EndpointInProcess)
        {
            builder.AddTable("vendors", TenancyFixture.Vendors, t =>
            {
                t.OrderedBy(v => v.Id).UniqueKey(v => v.Id);
                t.Entitlement(TenancyFixture.VendorsEntitlement());
            });
            builder.AddTable("items", TenancyFixture.Items, t =>
            {
                // No foreign key: it would name a table of another schema, which the catalog
                // refuses — the same reason `messages` declares none under ChildInProcess.
                t.OrderedBy(i => i.Id).UniqueKey(i => i.Id);
                t.Entitlement(TenancyFixture.ItemsEntitlement());
            });
        }

        return builder.Build();
    }

    /// <summary>
    /// The bodies of the fixtures' client-bodied functions, for the engine that runs them — this
    /// one's <c>is_vip</c> and <see cref="TenancyFixture.Functions"/>'s <c>echo</c>, which every
    /// theory of the family passes because every fixture of the family declares them.
    /// </summary>
    /// <remarks>
    /// The plain <c>echo</c> is the identity. A run that wants to know what the host was actually
    /// handed registers a body of its own instead, which is what the adversarial battery's
    /// client-body case does.
    /// </remarks>
    public static void RegisterFunctions(Chalk.Client.IFunctionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddScalar<int, bool>("is_vip", static id => id % 2 == 1);
        registry.AddScalar<string, string>("echo", static s => s);
    }

    private static void Load(
        DbConnection connection,
        DialectProfileDescriptor profile,
        ThroughPlacement placement,
        MarketplacePlacement marketplace)
    {
        var text = profile.Dialect == "postgresql" ? "TEXT" : "VARCHAR";
        Execute(connection, "DROP TABLE IF EXISTS \"order_items\"");
        Execute(connection, "DROP TABLE IF EXISTS \"items\"");
        Execute(connection, "DROP TABLE IF EXISTS \"vendors\"");
        Execute(connection, "DROP TABLE IF EXISTS \"orders\"");
        Execute(connection, "DROP TABLE IF EXISTS \"members\"");
        Execute(connection, "DROP TABLE IF EXISTS \"messages\"");
        Execute(connection, "DROP TABLE IF EXISTS \"threads\"");
        Execute(
            connection,
            "CREATE TABLE \"members\" (\"id\" INTEGER NOT NULL, \"org_id\" INTEGER NOT NULL, "
            + $"\"first_name\" {text} NOT NULL, \"last_name\" {text} NOT NULL, "
            + $"\"national_id\" {text} NOT NULL, \"postcode\" {text})");
        Execute(
            connection,
            // PRIMARY KEY since D265 clause (h): the first step of a `Related` path joins the
            // target's own unique key, and over a real database that key is *discovered* (§3).
            "CREATE TABLE \"orders\" (\"id\" INTEGER NOT NULL PRIMARY KEY, \"member_id\" INTEGER NOT NULL, "
            + $"\"org_id\" INTEGER NOT NULL, \"amount\" BIGINT NOT NULL, \"note\" {text}, "
            // The region of D266: the column a confined grant's tuple reads beside the tenancy.
            + "\"created_by\" INTEGER NOT NULL, \"region_id\" INTEGER NOT NULL)");

        // D265 clause (h): the bridge is always here, beside the target, so `orders`' key set has
        // its up-step in the same source whichever placement this is (§5). The endpoint and its own
        // endpoint follow it unless the caller wants them apart, which is §8's two-source case.
        Execute(
            connection,
            "CREATE TABLE \"order_items\" (\"id\" INTEGER NOT NULL PRIMARY KEY, "
            + "\"order_id\" INTEGER NOT NULL, \"item_id\" INTEGER NOT NULL, "
            + "\"quantity\" INTEGER NOT NULL, \"unit_price\" BIGINT NOT NULL)");
        if (marketplace != MarketplacePlacement.EndpointInProcess)
        {
            Execute(
                connection,
                $"CREATE TABLE \"vendors\" (\"id\" INTEGER NOT NULL PRIMARY KEY, \"name\" {text} NOT NULL)");
            Execute(
                connection,
                "CREATE TABLE \"items\" (\"id\" INTEGER NOT NULL PRIMARY KEY, "
                + $"\"vendor_id\" INTEGER NOT NULL, \"name\" {text} NOT NULL)");
            foreach (var vendor in TenancyFixture.Vendors)
            {
                Execute(
                    connection,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"INSERT INTO \"vendors\" VALUES ({vendor.Id}, {Literal(vendor.Name)})"));
            }

            foreach (var item in TenancyFixture.Items)
            {
                Execute(
                    connection,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"INSERT INTO \"items\" VALUES ({item.Id}, {item.VendorId}, {Literal(item.Name)})"));
            }
        }

        foreach (var line in TenancyFixture.OrderItems)
        {
            Execute(
                connection,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO \"order_items\" VALUES ({line.Id}, {line.OrderId}, "
                    + $"{line.ItemId}, {line.Quantity}, {line.UnitPrice})"));
        }

        // The parent and the child of §3.13. A `through` parent must declare a unique key on the
        // column its child correlates against, and over a real database that key is *discovered*:
        // the PRIMARY KEY below is what the ADO source reads back as one.
        if (placement != ThroughPlacement.ParentInProcess)
        {
            Execute(
                connection,
                "CREATE TABLE \"threads\" (\"id\" INTEGER NOT NULL, "
                + "\"org_id\" INTEGER NOT NULL, \"member_id\" INTEGER NOT NULL)");
        }
        if (placement != ThroughPlacement.ChildInProcess)
        {
            Execute(
                connection,
                "CREATE TABLE \"messages\" (\"id\" INTEGER NOT NULL PRIMARY KEY, "
                + $"\"thread_id\" INTEGER NOT NULL, \"content\" {text} NOT NULL, "
                + "\"first_viewed_at\" TIMESTAMP)");
        }

        foreach (var thread in placement == ThroughPlacement.ParentInProcess ? [] : TenancyFixture.Threads)
        {
            Execute(
                connection,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO \"threads\" VALUES ({thread.Id}, {thread.OrgId}, "
                    + $"{thread.MemberId})"));
        }

        foreach (var message in placement == ThroughPlacement.ChildInProcess ? [] : TenancyFixture.Messages)
        {
            Execute(
                connection,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO \"messages\" VALUES ({message.Id}, {message.ThreadId}, "
                    + $"{Literal(message.Content)}, {Timestamp(message.FirstViewedAt)})"));
        }

        foreach (var member in TenancyFixture.Members)
        {
            Execute(
                connection,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO \"members\" VALUES ({member.Id}, {member.OrgId}, "
                    + $"{Literal(member.FirstName)}, {Literal(member.LastName)}, "
                    + $"{Literal(member.NationalId)}, {Literal(member.Postcode)})"));
        }

        foreach (var order in TenancyFixture.Orders)
        {
            Execute(
                connection,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO \"orders\" VALUES ({order.Id}, {order.MemberId}, {order.OrgId}, "
                    + $"{order.Amount}, {Literal(order.Note)}, {order.CreatedBy}, "
                    + $"{order.RegionId})"));
        }
    }

    private static string Timestamp(DateTime? value) =>
        value is null
            ? "NULL"
            : "TIMESTAMP '" + value.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "'";

    private static string Literal(string? value) =>
        value is null ? "NULL" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var connection in _connections)
        {
            connection.Dispose();
        }

        foreach (var file in _files)
        {
            file.Dispose();
        }
    }
}
