using System.Data.Common;
using Chalk.Catalog;
using Chalk.Sources.Ado;
using DuckDB.NET.Data;

namespace Chalk.Sources.DuckDb;

/// <summary>
/// A DuckDB source: the in-box ADO.NET source with the native data-chunk reader already set
/// (D148, <c>docs/design/24-zero-gc.md</c> §6).
/// </summary>
/// <remarks>
/// <para>
/// Everything else about the source is unchanged — discovery, capabilities, pushdown, the dialect
/// profile — because it <em>is</em> the ADO source. What this adds is the reader underneath: a
/// query's rows arrive as DuckDB vectors copied into arena buffers rather than one cell at a time
/// through a <see cref="DbDataReader"/>.
/// </para>
/// <para>
/// A host that would rather build the source itself calls <see cref="UseNativeReader"/> on its own
/// <see cref="AdoSourceBuilder"/>; a host that already registers DuckDB through
/// <see cref="AdoSourceBuilder"/> and does neither keeps the <c>DbDataReader</c> path it has today.
/// </para>
/// </remarks>
public static class DuckDbSources
{
    /// <summary>
    /// A builder for a DuckDB source at <paramref name="connectionString"/>, with the DuckDB dialect
    /// profile and the native reader. Call <c>DiscoverTables()</c> or <c>AddTable(...)</c> on it, then
    /// <c>Build()</c>.
    /// </summary>
    public static AdoSourceBuilder AddDuckDbSource(
        string sourceId, string connectionString, string schemaName = "main")
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        return AddDuckDbSource(sourceId, () => new DuckDBConnection(connectionString), schemaName);
    }

    /// <summary>
    /// The same over a connection factory, for a host that pools or configures its own connections.
    /// </summary>
    public static AdoSourceBuilder AddDuckDbSource(
        string sourceId, Func<DuckDBConnection> connect, string schemaName = "main")
    {
        ArgumentNullException.ThrowIfNull(connect);
        return new AdoSourceBuilder(sourceId, connect, schemaName)
            .Dialect(DialectProfiles.DuckDb)
            .UseNativeReader();
    }

    /// <summary>
    /// The reader <see cref="UseNativeReader"/> and <see cref="AddDuckDbSource(string, string, string)"/>
    /// set (D148a). Named here so a host — or a benchmark comparing the two — can ask what the
    /// default is rather than assume it.
    /// </summary>
    public static IRemoteFetch NativeReader => DuckDbFetch.Instance;

    /// <summary>
    /// The trait this package declares for its own provider (D263, ADR 0046 §2): DuckDB's driver
    /// hands a TEXT value's UTF-8 over through <c>GetBytes</c> for BLOB only and refuses it for
    /// VARCHAR, so an undeclared, measured answer always lands on <see cref="TextStrategy.String"/>
    /// anyway — declaring it only spares the one exception a first measurement would otherwise pay.
    /// It reaches the reader through <see cref="AdoSourceBuilder.Provider"/> like any host's own
    /// declaration would; nothing here is a provider name inside <c>Chalk.Sources.Ado</c> itself.
    /// </summary>
    private static readonly AdoProviderTraits Traits = new() { Text = TextStrategy.String };

    /// <summary>
    /// Sets the native DuckDB reader on a builder a host is configuring itself (D148, D148a) — the
    /// same reader <see cref="AddDuckDbSource(string, string, string)"/> sets. A connection that
    /// turns out not to be a <see cref="DuckDBConnection"/> falls back to the <c>DbDataReader</c>
    /// path rather than failing, and so does a column outside the native reader's own type table
    /// (D148a) — both reach <c>AdoBatchReader</c> over a real <c>DuckDBDataReader</c>, which is why
    /// <see cref="Traits"/> is declared here rather than left for that path to measure.
    /// </summary>
    public static AdoSourceBuilder UseNativeReader(this AdoSourceBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Fetch(NativeReader).Provider(Traits);
    }

    /// <summary>
    /// Sets DuckDB's own Arrow export as the reader instead (D148a): a data chunk exported through
    /// Arrow's C Data Interface and handed to the pipeline without a copy.
    /// </summary>
    /// <remarks>
    /// Both readers cost nothing per fetched row. This one is smaller and reads every type DuckDB
    /// can export; the copier produces batches of exactly the requested size out of buffers the
    /// arena owns. <c>ADR 0024</c> records the measurements and which is the default. It falls back
    /// to <c>AdoBatchReader</c> over the same real <c>DuckDBDataReader</c> the native reader's
    /// fallback does (a mismatched export schema, or a connection that is not a
    /// <see cref="DuckDBConnection"/>), so it declares <see cref="Traits"/> too.
    /// </remarks>
    public static AdoSourceBuilder UseArrowReader(this AdoSourceBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Fetch(DuckDbArrowFetch.Instance).Provider(Traits);
    }
}
