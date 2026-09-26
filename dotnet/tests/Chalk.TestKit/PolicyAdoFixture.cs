using System.Data.Common;
using System.Globalization;
using Chalk.Catalog;
using Chalk.Entitlements;
using Chalk.Sources;
using Chalk.Sources.Ado;
using PredicateShape = Chalk.Ir.PredicateShape;
using DuckDB.NET.Data;

namespace Chalk.TestKit;

/// <summary>
/// The battery's fixture in a real database, reached through the in-box ADO.NET source
/// (<c>corpus/policy/README.md</c> §3 step 1; F41).
/// </summary>
/// <remarks>
/// <para>
/// The same six tables, the same rows and the <em>same descriptors</em> as
/// <see cref="PolicyFixture"/> — they are read from <see cref="PolicyEntitlements"/> rather than
/// repeated — so a difference between the POCO run and this one is a difference the remote path
/// introduced and nothing else. That is what makes the fifteen cases naming an ADO source worth
/// running at all: they are the ones <c>row_predicate_pushed</c>, the remote query's text and
/// <c>PUSHDOWN_REQUIRED</c> are actually about, and a POCO table has no source to push into.
/// </para>
/// <para>
/// Every table is remote, so a statement joining two of them pushes the join; the schema is the
/// only one, so the statements name their tables unqualified exactly as they do in process.
/// </para>
/// </remarks>
public sealed class PolicyAdoFixture : IDisposable
{
    /// <summary>The remote source's id, and its schema name.</summary>
    public const string SourceId = "warehouse";

    private readonly List<DbConnection> _connections = [];
    private readonly List<DuckDbFile> _files = [];
    private bool _disposed;

    /// <summary>The three tables group U adds, which only the in-process fixture holds.</summary>
    private static readonly HashSet<string> Marketplace =
        new(StringComparer.Ordinal) { "vendors", "items", "order_items" };

    private PolicyAdoFixture(AdoSource remote, string dialect)
    {
        Remote = remote;
        Dialect = dialect;
    }

    /// <summary>Every table of the fixture, in a real database.</summary>
    public AdoSource Remote { get; }

    /// <summary>Which dialect the generated SQL is in — <c>duckdb</c> or <c>postgresql</c>.</summary>
    public string Dialect { get; }

    /// <summary>The one source the engine is built over.</summary>
    public IReadOnlyList<ISourceRuntime> Sources => [Remote];

    /// <summary>A DuckDB copy under one of the corpus's three DuckDB profiles.</summary>
    public static PolicyAdoFixture CreateDuckDb(PolicyCatalog catalog, PolicySourceProfile profile)
    {
        var file = DuckDbFile.Create("policy");
        var fixture = Create(
            DialectProfiles.DuckDb,
            file.ConnectionString,
            connection => new DuckDBConnection(connection),
            catalog,
            profile);
        fixture._files.Add(file);
        return fixture;
    }

    /// <summary>
    /// The same in PostgreSQL. The provider is injected because <c>Chalk.TestKit</c> does not
    /// reference Npgsql — it is the integration project's, exactly as it is a host's.
    /// </summary>
    public static PolicyAdoFixture CreatePostgres(
        string connectionString,
        Func<string, DbConnection> connect,
        PolicyCatalog catalog,
        PolicySourceProfile profile) =>
        Create(DialectProfiles.PostgreSql, connectionString, connect, catalog, profile);

    private static PolicyAdoFixture Create(
        DialectProfileDescriptor profile,
        string connectionString,
        Func<string, DbConnection> connect,
        PolicyCatalog catalog,
        PolicySourceProfile source)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        // One connection held open for the fixture's life, as the corpus fixture does: an in-memory
        // or file database that nothing is connected to is a database that may not be there.
        var keepAlive = connect(connectionString);
        keepAlive.Open();
        Load(keepAlive, profile);

        var builder = new AdoSourceBuilder(SourceId, () => connect(connectionString), SourceId)
            .Dialect(profile)
            .Options(TestTimeouts.SourceOptions)
            .Capabilities(Capabilities(profile, source))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .DiscoverTables()
            // Case 158 puts a client-bodied predicate beside the tenancy conjunct. It can never be
            // pushed, so the mixed filter has to split or the whole table comes back — which is the
            // case the residual rule of §3.7 exists for.
            .AddFunction("is_vip", f => f.Scalar<int, bool>("id").Strict().Client());

        foreach (var (table, entitlement) in catalog.ByTable())
        {
            // D265 clause (h)'s three tables are the in-process fixture's alone: these profiles
            // hold the battery's original six, and no case that names a source profile reads a
            // vendor, an item or a line. Attaching an entitlement to a table the source does not
            // hold is refused by name, which is the right refusal for a real mistake and the wrong
            // one here.
            if (Marketplace.Contains(table))
            {
                continue;
            }

            if (PolicyEntitlements.Find(entitlement) is { } declared)
            {
                builder.Entitlement(table, declared.Descriptor);
            }
        }

        if (source == PolicySourceProfile.AdoDuckDbTrusted)
        {
            builder.TrustSourceRowLevelSecurity(
                RowLevelSecurityPreconditions.ConnectionIdentifiesPrincipal
                | RowLevelSecurityPreconditions.PoliciesEnabledAndForced
                | RowLevelSecurityPreconditions.PoliciesMatchEntitlements);
        }

        var fixture = new PolicyAdoFixture(builder.Build(), profile.Dialect);
        fixture._connections.Add(keepAlive);
        return fixture;
    }

    /// <summary>
    /// What the profile declares (<c>corpus/policy/README.md</c>'s source list). The dialect's own,
    /// except for <c>ado_duckdb_no_in</c>, which deliberately claims no <c>IN</c> and a ceiling of
    /// zero so that a tenancy list has no shape to be pushed as — which is what design corpus 21
    /// and <c>PUSHDOWN_REQUIRED</c> are about.
    /// </summary>
    private static SourceCapabilities Capabilities(
        DialectProfileDescriptor profile, PolicySourceProfile source)
    {
        var full = AdoCapabilities.For(profile);
        if (source != PolicySourceProfile.AdoDuckDbNoIn)
        {
            return full;
        }

        return new SourceCapabilities
        {
            QueryLanguage = full.QueryLanguage,
            PushablePredicates = [.. full.PushablePredicates.Where(p => p != PredicateShape.In)],
            PushableFunctions = full.PushableFunctions,
            PushableAggregates = full.PushableAggregates,
            NativeFunctions = full.NativeFunctions,
            UnsupportedFunctions = full.UnsupportedFunctions,
            SupportsProject = full.SupportsProject,
            SupportsSort = full.SupportsSort,
            SupportsLimit = full.SupportsLimit,
            SupportsOffset = full.SupportsOffset,
            SupportsDistinct = full.SupportsDistinct,
            SupportsGroupBy = full.SupportsGroupBy,
            SupportsHaving = full.SupportsHaving,
            SupportsInnerJoin = full.SupportsInnerJoin,
            SupportsOuterJoin = full.SupportsOuterJoin,
            SupportsSemiAntiJoin = full.SupportsSemiAntiJoin,
            MaxPushdownRows = full.MaxPushdownRows,
            MaxInList = 0,
            SupportsParameters = full.SupportsParameters,
        };
    }

    /// <summary>The body of the function the fixture declares, for the engine that runs it.</summary>
    public static void RegisterFunctions(Chalk.Client.IFunctionRegistry registry) =>
        PolicyFixture.RegisterFunctions(registry);

    // ---------------------------------------------------------------- the rows

    private static void Load(DbConnection connection, DialectProfileDescriptor profile)
    {
        var postgres = profile.Dialect == "postgresql";
        var text = postgres ? "TEXT" : "VARCHAR";
        var money = postgres ? "NUMERIC(10, 2)" : "DECIMAL(10, 2)";

        foreach (var table in new[] { "orders", "notes", "invites", "symbols", "members", "orgs" })
        {
            Execute(connection, $"DROP TABLE IF EXISTS \"{table}\"");
        }

        Execute(
            connection,
            "CREATE TABLE \"orgs\" (\"id\" INTEGER NOT NULL, "
            + $"\"name\" {text} NOT NULL)");
        Execute(
            connection,
            "CREATE TABLE \"members\" (\"id\" INTEGER NOT NULL, \"org_id\" INTEGER NOT NULL, "
            + $"\"first_name\" {text} NOT NULL, \"last_name\" {text} NOT NULL, "
            + $"\"dob\" DATE NOT NULL, \"national_id\" {text} NOT NULL, "
            + $"\"postcode\" {text} NOT NULL)");
        Execute(
            connection,
            "CREATE TABLE \"orders\" (\"id\" INTEGER NOT NULL, \"member_id\" INTEGER NOT NULL, "
            + "\"org_id\" INTEGER NOT NULL, \"placed_at\" DATE NOT NULL, "
            + $"\"note\" {text}, \"amount\" {money} NOT NULL, \"status\" {text} NOT NULL, "
            + "\"created_by\" INTEGER NOT NULL)");
        Execute(
            connection,
            "CREATE TABLE \"notes\" (\"id\" INTEGER NOT NULL, \"org_id\" INTEGER NOT NULL, "
            + $"\"created_by\" INTEGER NOT NULL, \"body\" {text} NOT NULL)");
        Execute(
            connection,
            "CREATE TABLE \"invites\" (\"id\" INTEGER NOT NULL, \"org_id\" INTEGER NOT NULL, "
            + $"\"target\" {text} NOT NULL)");
        Execute(
            connection,
            $"CREATE TABLE \"symbols\" (\"code\" {text} NOT NULL, \"label\" {text} NOT NULL, "
            + "\"sort_order\" INTEGER NOT NULL)");

        foreach (var org in PolicyFixture.Orgs)
        {
            Execute(connection, $"INSERT INTO \"orgs\" VALUES ({org.Id}, {Literal(org.Name)})");
        }

        foreach (var member in PolicyFixture.Members)
        {
            Execute(
                connection,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO \"members\" VALUES ({member.Id}, {member.OrgId}, "
                    + $"{Literal(member.FirstName)}, {Literal(member.LastName)}, "
                    + $"{Date(member.Dob)}, {Literal(member.NationalId)}, "
                    + $"{Literal(member.Postcode)})"));
        }

        foreach (var order in PolicyFixture.Orders)
        {
            Execute(
                connection,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO \"orders\" VALUES ({order.Id}, {order.MemberId}, {order.OrgId}, "
                    + $"{Date(order.PlacedAt)}, {Literal(order.Note)}, {order.Amount}, "
                    + $"{Literal(order.Status)}, {order.CreatedBy})"));
        }

        foreach (var note in PolicyFixture.Notes)
        {
            Execute(
                connection,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO \"notes\" VALUES ({note.Id}, {note.OrgId}, {note.CreatedBy}, "
                    + $"{Literal(note.Body)})"));
        }

        foreach (var invite in PolicyFixture.Invites)
        {
            Execute(
                connection,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO \"invites\" VALUES ({invite.Id}, {invite.OrgId}, "
                    + $"{Literal(invite.Target)})"));
        }

        foreach (var symbol in PolicyFixture.Symbols)
        {
            Execute(
                connection,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO \"symbols\" VALUES ({Literal(symbol.Code)}, "
                    + $"{Literal(symbol.Label)}, {symbol.SortOrder})"));
        }
    }

    private static string Date(DateOnly value) =>
        "DATE '" + value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";

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
