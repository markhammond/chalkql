using System.Data;
using System.Data.Common;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Ir;
using CostProfile = Chalk.Catalog.CostProfile;
using SourceCapabilities = Chalk.Catalog.SourceCapabilities;
using CatalogContext = Chalk.Catalog.CatalogContext;
using Array = System.Array;
using ArrowSchema = Apache.Arrow.Schema;
using FunctionDescriptor = Chalk.Catalog.FunctionDescriptor;

namespace Chalk.Sources.Ado;

/// <summary>
/// The in-box ADO.NET source (D85): any <see cref="DbProviderFactory"/>'s tables as Chalk tables,
/// with pushdown driven by the capabilities the builder declared.
/// </summary>
/// <remarks>
/// <para>
/// Both paths are here and both matter. <see cref="ScanAsync"/> is a plain
/// <c>SELECT columns FROM table</c>: it is what <c>PushdownLevel.None</c> reads, what the I4 oracle
/// compares against, and what the conformance kit runs a capability's answers against — so every
/// remote source must scan, whatever else it can do. <see cref="ExecuteQueryAsync"/> runs the query
/// the planner generated.
/// </para>
/// <para>
/// A connection is opened per query from the provider's own pool and disposed with the enumerator,
/// including when the consumer abandons the scan part-way or the query faults: the
/// <c>await using</c> chain in the iterator is what makes that true, and a test asserts it against
/// both a fake provider and the real pools.
/// </para>
/// </remarks>
public sealed class AdoSource : ISourceRuntime
{
    private readonly Func<DbConnection> _connect;
    private readonly SourceOptions _options;
    private readonly CostProfile _costProfile;
    private readonly Func<AdoTableDefinition[]>? _reintrospect;
    private readonly FunctionDescriptor[] _functions;
    private readonly IRemoteFetch _fetch;
    private readonly AdoProviderTraits? _traits;
    private SourceRuntimeMixin _runtime;

    /// <summary>
    /// The tables and the descriptor over them, swapped as one reference so a query that starts
    /// during a refresh sees either the old shape or the new one and never half of each.
    /// </summary>
    private Introspection _current;

    internal AdoSource(
        string sourceId,
        string schemaName,
        Func<DbConnection> connect,
        DialectProfileDescriptor profile,
        SourceCapabilities capabilities,
        SourceOptions options,
        AdoTableDefinition[] tables,
        CostProfile costProfile,
        Func<AdoTableDefinition[]>? reintrospect,
        FunctionDescriptor[] functions,
        IRemoteFetch? fetch = null,
        AdoProviderTraits? traits = null,
        bool trustSourceRowLevelSecurity = false)
    {
        _trustSourceRowLevelSecurity = trustSourceRowLevelSecurity;
        SourceId = sourceId;
        SchemaName = schemaName;
        _connect = connect;
        Profile = profile;
        Capabilities = capabilities;
        _options = options;
        _costProfile = costProfile;
        _reintrospect = reintrospect;
        _functions = functions;
        _fetch = fetch ?? DbDataReaderFetch.Instance;
        _traits = traits;
        _current = Describe(tables);
    }

    private sealed record Introspection(AdoTableDefinition[] Tables, SchemaDescriptor Schema);

    private Introspection Describe(AdoTableDefinition[] tables) => new(
        tables,
        new SchemaDescriptor
        {
            SourceId = SourceId,
            Name = SchemaName,
            Kind = SourceKind.Remote,
            Dialect = Profile.Dialect,
            Capabilities = Capabilities,
            DialectProfile = Profile,
            CostProfile = _costProfile,
            Tables = Array.ConvertAll(tables, t => t.Descriptor),
            Functions = _functions,
            TrustSourceRowLevelSecurity = _trustSourceRowLevelSecurity,
        });

    private readonly bool _trustSourceRowLevelSecurity;

    /// <summary>The SQL schema these tables live in, as Chalk addresses them.</summary>
    public string SchemaName { get; }

    /// <summary>How this source spells and evaluates SQL.</summary>
    public DialectProfileDescriptor Profile { get; }

    /// <summary>What this source claims it can do.</summary>
    public SourceCapabilities Capabilities { get; }

    /// <summary>Per-source timeouts and limits (D86).</summary>
    public SourceOptions Options => _options;

    /// <summary>
    /// How this source reads an executed query (D148). <see cref="DbDataReaderFetch"/> unless the
    /// builder was given another — <c>AddDuckDbSource</c> composes this source with the native
    /// DuckDB reader that way.
    /// </summary>
    public IRemoteFetch Fetch => _fetch;

    public SourceSharing Sharing => Options.Sharing;

    public bool TryClaimEngine(object identity, out SourceSharing Mode) => _runtime.TryClaimEngine(identity, out Mode);

    public void ReleaseEngine(object identity) => _runtime.ReleaseEngine(identity);

    public string SourceId { get; }

    /// <inheritdoc />
    public SchemaDescriptor DescribeSchema() => Volatile.Read(ref _current).Schema;

    /// <summary>
    /// What this source's reader produces (D244). Both in-box readers build classic Arrow strings —
    /// <see cref="DbDataReaderFetch"/> copies each cell into offsets and a data buffer, and a
    /// driver's Arrow export hands over <c>utf8</c> — so the answer is the fetch's own.
    /// </summary>
    public StringLayouts NativeStringLayout => _fetch.NativeStringLayout;

    /// <summary>
    /// Introspects the database again (D86). A source whose tables were all registered by hand has
    /// nothing to re-read and returns immediately; one built with <c>DiscoverTables</c> or exact row
    /// counts opens a connection and asks the provider what is there now.
    /// </summary>
    /// <remarks>
    /// The new descriptor is validated before it is published, by the same rules the planner
    /// applies, so a table that has grown a column Chalk cannot map is a refresh that fails rather
    /// than a catalog that is quietly wrong. The old descriptor stays in place when it does.
    /// </remarks>
    public ValueTask RefreshAsync(CancellationToken ct)
    {
        if (_reintrospect is null)
        {
            return ValueTask.CompletedTask;
        }

        ct.ThrowIfCancellationRequested();
        var next = Describe(_reintrospect());
        CatalogValidator.Validate(new CatalogContext
        {
            ContextId = SourceId,
            Epoch = 0,
            Schemas = [next.Schema],
        });

        Volatile.Write(ref _current, next);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Re-introspects, and keeps only the named table's new descriptor (D271 (f)): a table target
    /// re-describes that one table, statistics included, and discovers nothing — a table the
    /// re-introspection has newly found, or has lost, is not this target's business.
    /// </summary>
    /// <remarks>
    /// The introspection itself is the source's own and reads whatever it reads; what this bounds is
    /// the <em>effect</em>, which is what a host asking for one table means. A source built without a
    /// re-introspection does nothing, exactly as <see cref="RefreshAsync"/> does.
    /// </remarks>
    public ValueTask RefreshTableAsync(string table, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (_reintrospect is null)
        {
            return ValueTask.CompletedTask;
        }

        ct.ThrowIfCancellationRequested();
        var current = Volatile.Read(ref _current);
        var replacement = Array.Find(
            _reintrospect(), t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));
        var index = Array.FindIndex(
            current.Tables, t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));
        if (replacement is null || index < 0)
        {
            // The table is gone from the database, or it is one this source does not serve. Either
            // way nothing changes here: removing a table is a discovery, and a table target discovers
            // nothing. A plain refresh is what reconciles the set of tables.
            return ValueTask.CompletedTask;
        }

        var tables = (AdoTableDefinition[])current.Tables.Clone();
        tables[index] = replacement;
        var next = Describe(tables);
        CatalogValidator.Validate(new CatalogContext
        {
            ContextId = SourceId,
            Epoch = 0,
            Schemas = [next.Schema],
        });

        Volatile.Write(ref _current, next);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The handle for one of this source's tables (D271 (h)), for a host that did not take it at
    /// registration — which is every discovered table. A table this source does not serve is refused
    /// here, naming the ones it does.
    /// </summary>
    public AdoTable Table(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var current = Volatile.Read(ref _current);
        var declared = Array.Find(
            current.Tables, t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new SourceContractException(
                SourceId,
                name,
                $"there is no such table in schema '{SchemaName}'. Known tables: "
                + string.Join(", ", current.Tables.Select(t => t.Name)) + ".");

        lock (_handles)
        {
            if (!_handles.TryGetValue(declared.Name, out var binding))
            {
                binding = new AdoTableBinding
                {
                    SourceId = SourceId,
                    Schema = SchemaName,
                    Table = declared.Name,
                };
                binding.Bind(this);
                _handles[declared.Name] = binding;
            }

            return new AdoTable(binding);
        }
    }

    /// <summary>The handles this source's tables are named by, made once per name (D271 (h)).</summary>
    private readonly Dictionary<string, AdoTableBinding> _handles =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IAsyncEnumerable<RecordBatch> ScanAsync(
        ScanRequest request, ScanContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var table = Require(request.Table);
        if (request.PushedFilter is not null)
        {
            // §4: a source must refuse a predicate it never declared rather than ignore it, which
            // would return too many rows. This source's predicates travel in a RemoteQuery, so a
            // filter on the *scan* path is always a planner error.
            throw new SourceContractException(
                SourceId,
                request.Table,
                "a filter was pushed into a scan. This source evaluates predicates through "
                + "ExecuteQueryAsync, so Read.filter is never one it declared.");
        }

        var sql = table.SelectSql(request.Projection, Profile);
        return RunAsync(
            sql,
            [],
            [],
            [],
            request.OutputSchema,
            table.ColumnTypes(request.Projection),
            request.BatchSize,
            context,
            request.Table,
            countAsRemoteRows: false,
            _options.QueryTimeout,
            ct);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<RecordBatch> ExecuteQueryAsync(
        RemoteQueryRequest request, ScanContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (request.QueryText.Length == 0)
        {
            throw new SourceContractException(
                SourceId,
                "a pushed query",
                "this source speaks SQL, but the request carries no query text. A source whose "
                + "QueryLanguage is Sql is never handed an IR-only request.");
        }

        var types = new ChalkType[request.OutputSchema.FieldsList.Count];
        for (var i = 0; i < types.Length; i++)
        {
            var field = request.OutputSchema.FieldsList[i];
            types[i] = ArrowTypeMapping.FromArrow(field.DataType, field.IsNullable);
        }

        var (sql, names) = AdoParameters.Rewrite(request.QueryText, Profile.ParameterPlaceholder);
        return RunAsync(
            sql,
            names,
            request.Parameters,
            request.ParameterTypes,
            request.OutputSchema,
            types,
            request.BatchSize,
            context,
            request.QueryText,
            countAsRemoteRows: true,
            request.Timeout ?? _options.QueryTimeout,
            ct);
    }

    /// <summary>
    /// The one path both entry points share: open, execute, read batches, dispose. Written as an
    /// iterator so the connection and the reader are released the moment the consumer stops, and
    /// the failure attribution of §4 is in one place rather than two.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> RunAsync(
        string sql,
        IReadOnlyList<string> parameterNames,
        IReadOnlyList<object?> parameters,
        IReadOnlyList<ChalkType> parameterTypes,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        int batchSize,
        ScanContext context,
        string subject,
        bool countAsRemoteRows,
        TimeSpan timeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var timer = timeout == Timeout.InfiniteTimeSpan
            ? null
            : new CancellationTokenSource(timeout);
        using var linked = timer is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        var token = linked.Token;

        await using var connection = _connect();
        await OpenAsync(connection, subject, token, ct, timeout).ConfigureAwait(false);

        var request = new RemoteFetchRequest
        {
            SourceId = SourceId,
            Subject = subject,
            Connection = connection,
            Sql = sql,
            ParameterNames = parameterNames,
            Parameters = parameters,
            ParameterTypes = parameterTypes,
            OutputSchema = schema,
            ColumnTypes = columnTypes,
            Arena = context.Arena,
            Stats = context.Stats,
            BatchSize = batchSize,
            Timeout = timeout,
            Profile = Profile,
            Traits = _traits,
        };

        // D148: which reader ran, for the host and for the tests. Recorded before the first batch,
        // so a query that fails part-way still says what it was using; a path that falls back
        // records the second name too, and Stats joins them.
        context.Stats.RecordSourcePath(SourceId, _fetch.Name);

        var batches = _fetch.FetchAsync(request, token).GetAsyncEnumerator(token);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await batches.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception failure) when (Attributable(failure, ct))
                {
                    throw Attribute(failure, subject, timeout, ct);
                }

                if (!moved)
                {
                    yield break;
                }

                var batch = batches.Current;
                context.Stats.AddRowsScanned(batch.Length);
                if (countAsRemoteRows)
                {
                    context.Stats.AddBytesFetched(EstimateBytes(batch));
                }

                yield return batch;
            }
        }
        finally
        {
            await batches.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task OpenAsync(
        DbConnection connection, string subject, CancellationToken token, CancellationToken ct, TimeSpan timeout)
    {
        try
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
        }
        catch (Exception failure) when (Attributable(failure, ct))
        {
            throw Attribute(failure, subject, timeout, ct);
        }
    }

    /// <summary>
    /// Whether this failure is one to attribute rather than pass through. A cancellation the
    /// <em>caller</em> asked for is the caller's, and a contract error already names everything.
    /// </summary>
    private static bool Attributable(Exception failure, CancellationToken ct) =>
        failure is not SourceContractException
        && !(failure is OperationCanceledException && ct.IsCancellationRequested);

    /// <summary>
    /// The failure, named (§4). A cancellation that the caller did <em>not</em> ask for is the
    /// timeout firing, which is a different thing from a provider error and gets its own exception.
    /// </summary>
    private Exception Attribute(Exception failure, string subject, TimeSpan timeout, CancellationToken ct)
    {
        if (!ct.IsCancellationRequested
            && (failure is OperationCanceledException || IsTimeout(failure)))
        {
            return new SourceTimeoutException(SourceId, subject, timeout, failure);
        }

        return new SourceExecutionException(SourceId, subject, failure);
    }

    /// <summary>
    /// Whether a provider exception is its own timeout rather than a cancellation. Providers vary:
    /// some cancel the command and surface their own exception type, and the only thing they agree
    /// on is that the message says so.
    /// </summary>
    private static bool IsTimeout(Exception failure) =>
        failure is TimeoutException
        || (failure is DbException && failure.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        || failure.InnerException is TimeoutException;

    /// <summary>What the batch's Arrow buffers hold, which is what <c>BytesFetched</c> reports.</summary>
    private static long EstimateBytes(RecordBatch batch)
    {
        long bytes = 0;
        foreach (var array in batch.Arrays)
        {
            foreach (var buffer in array.Data.Buffers)
            {
                bytes += buffer.Length;
            }
        }

        return bytes;
    }

    private AdoTableDefinition Require(string name)
    {
        var tables = Volatile.Read(ref _current).Tables;
        return Array.Find(tables, t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new SourceContractException(
                SourceId,
                name,
                $"there is no such table in schema '{SchemaName}'. Known tables: "
                + string.Join(", ", tables.Select(t => t.Name)) + ".");
    }

    /// <summary>
    /// The text strategy each STRING column of <paramref name="table"/> would use right now —
    /// declared through <see cref="AdoProviderTraits"/>, or measured — keyed by the driver's own
    /// data type name, for the conformance kit's report (D263, ADR 0046 §3). Opens one connection
    /// and reads rows until every STRING column has seen a non-NULL value, exactly the condition
    /// <c>AdoBatchReader</c> itself waits for; a table with no STRING column, or whose STRING
    /// columns are NULL throughout, reports nothing to declare.
    /// </summary>
    /// <remarks>
    /// This is <em>the</em> probe, not a second one: an undeclared column reaches
    /// <c>AdoBatchReader.ProbeTextForReport</c>, which shares the process-wide cache a live scan
    /// already fills, so a data type this source's own scans already measured costs nothing more
    /// here, and one nothing has measured yet is measured now rather than left unreported.
    /// </remarks>
    internal async Task<IReadOnlyList<AdoTextStrategyReport>> ProbeTextStrategiesAsync(
        string table, CancellationToken ct)
    {
        var descriptor = Require(table);
        var columns = descriptor.Descriptor.Columns;
        var stringColumns = new List<int>();
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Type.Kind == TypeKind.String)
            {
                stringColumns.Add(i);
            }
        }

        if (stringColumns.Count == 0)
        {
            return [];
        }

        var sql = descriptor.SelectSql(stringColumns, Profile);
        await using var connection = _connect();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct)
            .ConfigureAwait(false);

        var reports = new Dictionary<string, AdoTextStrategyReport>(StringComparer.Ordinal);
        var resolved = new bool[stringColumns.Count];
        var remaining = stringColumns.Count;
        while (remaining > 0 && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            for (var c = 0; c < stringColumns.Count; c++)
            {
                if (resolved[c] || await reader.IsDBNullAsync(c, ct).ConfigureAwait(false))
                {
                    continue;
                }

                var dataType = reader.GetDataTypeName(c);
                var declared = _traits?.Text;
                var strategy = declared ?? AdoBatchReader.ProbeTextForReport(reader, c);
                reports[dataType] = new AdoTextStrategyReport(dataType, strategy, declared is not null);
                resolved[c] = true;
                remaining--;
            }
        }

        return [.. reports.Values];
    }
}

/// <summary>
/// One data type's text strategy, as <see cref="AdoSource.ProbeTextStrategiesAsync"/> resolved it —
/// the conformance kit's own copy of the answer, so it names no ADO type wider than this (D263,
/// ADR 0046 §3).
/// </summary>
internal sealed record AdoTextStrategyReport(string DataType, TextStrategy Strategy, bool Declared);
