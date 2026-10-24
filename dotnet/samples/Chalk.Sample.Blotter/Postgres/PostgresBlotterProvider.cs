using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sample.Blotter.Live;
using Npgsql;

namespace Chalk.Sample.Blotter.Postgres;

/// <summary>The blotter's database in PostgreSQL, at a connection string, read through plain ADO.NET.</summary>
/// <remarks>
/// Every read goes through a <see cref="DbDataSource"/>, ADO.NET's connection factory, whose
/// connections carry the time limit as PostgreSQL's own <c>statement_timeout</c>: ChalkQL's source
/// opens its connections from it, and so do the estimates. A read past the limit is stopped by the
/// server, freeing its workers too, and Npgsql gives up connecting or waiting at the same limit.
/// Npgsql is named only where a data source is built; the seed, the market's writers and the oracle
/// connect without the limit, because building an index takes longer.
/// </remarks>
public sealed class PostgresBlotterProvider : IBlotterProvider
{
    // PostgreSQL's code for a statement cancelled, at statement_timeout among other reasons; ADO.NET
    // carries it on every DbException as the SQL standard's SQLSTATE.
    private const string QueryCanceled = "57014";

    private readonly string _connectionString;
    private readonly ConcurrentDictionary<TimeSpan, NpgsqlDataSource> _limited = new();

    /// <param name="connectionString">The database, its tables found in the <c>blotter</c> schema.</param>
    public PostgresBlotterProvider(string connectionString)
    {
        _connectionString = connectionString;
    }

    public string Name => "PostgreSQL";

    public DatabaseReads Open(TableRows rows, TimeSpan timeLimit)
    {
        var reading = Limited(timeLimit);

        // The tables' text is collated "C", so the database sorts and compares strings byte by byte,
        // as the host does; told so, ChalkQL pushes sorts and ranges on text down.
        var profile = DialectProfiles.PostgreSql.With(stringCollation: StringCollation.Binary);
        var source = new AdoSourceBuilder("db", reading.CreateConnection, "blotter")
            .Dialect(profile)
            .Capabilities(AdoCapabilities.For(profile))

            // No statement may run longer than the host's time limit, in the database or in ChalkQL.
            .Options(new SourceOptions { QueryTimeout = timeLimit })

            // Declared rather than discovered, with the indexes and the sizes the planner prices by.
            // ChalkQL's schema is the database's, because a pushed query names a table by its
            // catalog name (F273).
            .AddTable("orders_current", OrderColumns, configure: t => t
                .UniqueKey("order_id")
                .Index("orders_current_newest", false, "order_time", "order_id")
                .RowCount(rows.Orders))
            .AddTable("order_versions", OrderColumns, configure: t => t
                .UniqueKey("order_id", "version")
                .Index("order_versions_recent", false, "event_time")
                .RowCount(rows.Versions))
            .AddTable("fills", FillColumns, configure: t => t
                .UniqueKey("fill_id")
                .Index("fills_by_order", false, "order_id")
                .Index("fills_newest", false, "event_time", "fill_id")
                .RowCount(rows.Fills))
            .Build();
        return new DatabaseReads(source, new PostgresExplain(reading, Limited));
    }

    public bool StoppedAtTimeLimit(Exception failure)
    {
        for (var at = failure; at is not null; at = at.InnerException)
        {
            if (at is DbException { SqlState: QueryCanceled })
            {
                return true;
            }
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _limited.Values)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }

        _limited.Clear();
    }

    /// <summary>A connection factory whose every statement the server stops at <paramref name="limit"/>.</summary>
    private NpgsqlDataSource Limited(TimeSpan limit) => _limited.GetOrAdd(limit, l =>
    {
        var seconds = (int)Math.Ceiling(l.TotalSeconds);
        return NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Timeout = seconds,
            CommandTimeout = seconds + 5,
            Options = string.Create(CultureInfo.InvariantCulture, $"-c statement_timeout={(long)l.TotalMilliseconds}"),
        }.ConnectionString);
    });

    /// <summary>An order version's columns as the database holds them: times to the microsecond.</summary>
    private static readonly ColumnDescriptor[] OrderColumns =
    [
        Column("order_id", ChalkType.Uuid()),
        Column("version", ChalkType.Int32()),
        Column("event_time", ChalkType.Timestamp(6)),
        Column("order_time", ChalkType.Timestamp(6)),
        Column("cl_ord_id", ChalkType.String()),
        Column("client", ChalkType.String()),
        Column("region", ChalkType.String()),
        Column("channel", ChalkType.String()),
        Column("instrument", ChalkType.String()),
        Column("side", ChalkType.String()),
        Column("qty", ChalkType.Float64()),
        Column("filled_qty", ChalkType.Float64()),
        Column("avg_price", ChalkType.Float64()),
        Column("markup_bps", ChalkType.Float64()),
        Column("status", ChalkType.String()),
    ];

    private static readonly ColumnDescriptor[] FillColumns =
    [
        Column("fill_id", ChalkType.Uuid()),
        Column("order_id", ChalkType.Uuid()),
        Column("event_time", ChalkType.Timestamp(6)),
        Column("cl_ord_id", ChalkType.String()),
        Column("client", ChalkType.String()),
        Column("region", ChalkType.String()),
        Column("channel", ChalkType.String()),
        Column("instrument", ChalkType.String()),
        Column("side", ChalkType.String()),
        Column("qty", ChalkType.Float64()),
        Column("price", ChalkType.Float64()),
        Column("mid", ChalkType.Float64()),
        Column("markup_bps", ChalkType.Float64()),
    ];

    private static ColumnDescriptor Column(string name, ChalkType type) => new() { Name = name, Type = type };
}
