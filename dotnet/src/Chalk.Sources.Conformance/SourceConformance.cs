using System.Globalization;
using System.Text;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Ir;
using SourceCapabilities = Chalk.Catalog.SourceCapabilities;

namespace Chalk.Sources.Conformance;

/// <summary>
/// The conformance kit (D88): the difference between an extension point and a liability.
/// </summary>
/// <remarks>
/// <para>
/// A source's descriptor is a set of claims the planner acts on — this predicate is one I evaluate
/// with your semantics, these strings compare by code point, NULLs sort here — and an honest-looking
/// claim that is not true produces a <em>wrong answer</em>, not an error. The kit is how an adapter
/// author finds out before a host does.
/// </para>
/// <para>
/// It works by asking the source the same question twice and comparing. Every check runs a query
/// through <see cref="ISourceRuntime.ExecuteQueryAsync"/> — the pushed path — and computes the same
/// answer itself from <see cref="ISourceRuntime.ScanAsync"/> — the path that pushes nothing. The
/// scan is the oracle, exactly as it is for the I4 differential tests, and it is why every remote
/// source must implement one whatever else it can do.
/// </para>
/// <para>
/// The kit needs no planner and no sidecar: it writes its own SQL from the dialect profile, so it
/// runs anywhere the adapter does.
/// </para>
/// </remarks>
public static class SourceConformance
{
    /// <summary>Runs the kit and returns what it found. Never throws for a failed check.</summary>
    public static async Task<ConformanceReport> RunAsync(
        ISourceRuntime source, ConformanceOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= ConformanceOptions.Default;

        var schema = source.DescribeSchema();
        var findings = new List<ConformanceFinding>();

        if (options.Seed is { } seed)
        {
            using var batch = ConformanceDataset.ToBatch();
            await seed(
                new ConformanceSeed
                {
                    Source = source,
                    Table = options.Table,
                    Batch = batch,
                    CreateTable = ConformanceDataset.CreateTableStatement(
                        schema.DialectProfile, options.Table),
                    Inserts = ConformanceDataset.InsertStatements(schema.DialectProfile, options.Table),
                },
                ct).ConfigureAwait(false);
        }

        var context = await ConformanceContext.OpenAsync(source, schema, options, ct).ConfigureAwait(false);
        if (context.Unavailable is { } why)
        {
            findings.Add(new ConformanceFinding
            {
                Subject = "the kit's dataset",
                Declared = $"table '{options.Table}' with the kit's schema",
                Observed = why,
                Outcome = ConformanceOutcome.Skipped,
                Advice = "Supply ConformanceOptions.Seed, or point Table at one that matches "
                    + "ConformanceDataset.Columns.",
            });
            return new ConformanceReport { SourceId = source.SourceId, Server = options.Server, Findings = findings };
        }

        // D263 (ADR 0046 §3): reporting, not a check — it never fails, so it runs whatever
        // ConformanceChecks asked for, the same as Server does.
        findings.AddRange(
            await ProviderTraitProbes.RunAsync(source, options.Table, ct).ConfigureAwait(false));

        if (options.Checks.HasFlag(ConformanceChecks.Capabilities))
        {
            findings.AddRange(await CapabilityProbes.RunAsync(context, ct).ConfigureAwait(false));
        }

        if (options.Checks.HasFlag(ConformanceChecks.Dialect))
        {
            findings.AddRange(await DialectProbes.RunAsync(context, ct).ConfigureAwait(false));
        }

        if (options.Checks.HasFlag(ConformanceChecks.Uniqueness))
        {
            findings.AddRange(await UniquenessProbe.RunAsync(context, ct).ConfigureAwait(false));
        }

        return new ConformanceReport { SourceId = source.SourceId, Server = options.Server, Findings = findings };
    }

    /// <summary>
    /// The same, throwing <see cref="ConformanceException"/> when anything failed — the shape an
    /// adapter author's test wants, so the findings land in the failure message.
    /// </summary>
    public static async Task<ConformanceReport> VerifyAsync(
        ISourceRuntime source, ConformanceOptions? options = null, CancellationToken ct = default)
    {
        var report = await RunAsync(source, options, ct).ConfigureAwait(false);
        return report.Passed ? report : throw new ConformanceException(report);
    }
}

/// <summary>
/// One run's state: the source, its descriptor, the kit's rows as the source actually holds them,
/// and the two ways of asking it a question.
/// </summary>
internal sealed class ConformanceContext
{
    private readonly ISourceRuntime _source;
    private readonly ConformanceOptions _options;

    private ConformanceContext(
        ISourceRuntime source,
        SchemaDescriptor schema,
        TableDescriptor table,
        ConformanceOptions options,
        IReadOnlyList<object?[]> rows)
    {
        _source = source;
        _options = options;
        Schema = schema;
        Table = table;
        Rows = rows;
    }

    public SchemaDescriptor Schema { get; }

    public TableDescriptor Table { get; }

    /// <summary>Every row of the kit's table, as the source's own scan produced it.</summary>
    public IReadOnlyList<object?[]> Rows { get; }

    public DialectProfileDescriptor Profile => Schema.DialectProfile;

    public SourceCapabilities Capabilities => Schema.Capabilities;

    /// <summary>Why the kit cannot run, or null.</summary>
    public string? Unavailable { get; private init; }

    public static async Task<ConformanceContext> OpenAsync(
        ISourceRuntime source,
        SchemaDescriptor schema,
        ConformanceOptions options,
        CancellationToken ct)
    {
        var table = schema.FindTable(options.Table);
        if (table is null)
        {
            return Missing(source, schema, options, $"there is no table named '{options.Table}'");
        }

        for (var c = 0; c < ConformanceDataset.Columns.Count; c++)
        {
            var wanted = ConformanceDataset.Columns[c];
            var found = table.Columns.FirstOrDefault(
                col => string.Equals(col.Name, wanted.Name, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                return Missing(
                    source, schema, options, $"table '{table.Name}' has no column '{wanted.Name}'");
            }
        }

        try
        {
            var rows = await ScanAsync(source, table, options, ct).ConfigureAwait(false);
            return new ConformanceContext(source, schema, table, options, rows);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return Missing(source, schema, options, $"scanning it failed: {failure.Message}");
        }
    }

    private static ConformanceContext Missing(
        ISourceRuntime source, SchemaDescriptor schema, ConformanceOptions options, string why) =>
        new(source, schema, EmptyTable(options.Table), options, []) { Unavailable = why };

    private static TableDescriptor EmptyTable(string name) => new()
    {
        Name = name,
        Columns = ConformanceDataset.Columns,
        RowCount = -1,
    };

    /// <summary>The oracle: every row of the table through the source's own scan path.</summary>
    private static async Task<IReadOnlyList<object?[]>> ScanAsync(
        ISourceRuntime source, TableDescriptor table, ConformanceOptions options, CancellationToken ct)
    {
        var projection = Enumerable.Range(0, table.Columns.Count).ToArray();
        var request = new ScanRequest
        {
            Table = table.Name,
            Projection = projection,
            OutputSchema = ArrowTypeMapping.ToArrowSchema(table.Columns),
            BatchSize = options.BatchSize,
        };

        using var arena = new ExecutionArena();
        var context = new ScanContext { Stats = new ExecutionStats(), Arena = arena };
        var rows = new List<object?[]>();
        await foreach (var batch in source.ScanAsync(request, context, ct).WithCancellation(ct))
        {
            using (batch)
            {
                ConformanceValues.Read(batch, table.Columns, rows);
            }
        }

        return rows;
    }

    /// <summary>The pushed path: one query, run by the source, read back as rows.</summary>
    public Task<IReadOnlyList<object?[]>> QueryAsync(
        string sql, IReadOnlyList<ColumnDescriptor> columns, CancellationToken ct) =>
        QueryAsync(sql, columns, [], [], ct);

    /// <summary>
    /// The same, with values bound to the query's <c>?</c> placeholders — which the source rewrites
    /// into whatever its driver takes (D89). The parameter round-trip probe is the only caller: a
    /// value that does not come back as itself is a claim about <c>supports_parameters</c>, and
    /// nothing else the kit asks needs one.
    /// </summary>
    public async Task<IReadOnlyList<object?[]>> QueryAsync(
        string sql,
        IReadOnlyList<ColumnDescriptor> columns,
        IReadOnlyList<object?> parameters,
        IReadOnlyList<ChalkType> parameterTypes,
        CancellationToken ct)
    {
        var schema = ArrowTypeMapping.ToArrowSchema(columns);
        var request = new RemoteQueryRequest
        {
            QueryText = sql,
            PushedPlan = new Rel(),
            Parameters = parameters,
            ParameterTypes = parameterTypes,
            OutputSchema = schema,
            BatchSize = _options.BatchSize,
            Dialect = Profile.Dialect,
        };

        using var arena = new ExecutionArena();
        var context = new ScanContext { Stats = new ExecutionStats(), Arena = arena };
        var rows = new List<object?[]>();
        await foreach (var batch in _source.ExecuteQueryAsync(request, context, ct).WithCancellation(ct))
        {
            using (batch)
            {
                ConformanceValues.Read(batch, columns, rows);
            }
        }

        return rows;
    }

    /// <summary>The index of one of the kit's columns in the source's own table.</summary>
    public int ColumnIndex(string name) => Table.IndexOfColumn(name);

    /// <summary>A <c>SELECT id FROM table WHERE …</c> in this source's dialect.</summary>
    public string SelectIdsWhere(string predicate)
    {
        var sql = new StringBuilder("SELECT ");
        ConformanceDataset.Identifier(sql, "id", Profile);
        sql.Append(" FROM ");
        ConformanceDataset.Identifier(sql, Table.Name, Profile);
        sql.Append(" WHERE ").Append(predicate);
        return sql.ToString();
    }

    /// <summary>A quoted column name, for a probe's hand-written SQL.</summary>
    public string Column(string name)
    {
        var sql = new StringBuilder();
        ConformanceDataset.Identifier(sql, name, Profile);
        return sql.ToString();
    }

    /// <summary>The quoted table name.</summary>
    public string TableName()
    {
        var sql = new StringBuilder();
        ConformanceDataset.Identifier(sql, Table.Name, Profile);
        return sql.ToString();
    }

    /// <summary>The one-column output shape most probes read: the key.</summary>
    public IReadOnlyList<ColumnDescriptor> IdOnly { get; } =
        [new() { Name = "id", Type = ChalkType.Int64() }];
}

/// <summary>Reading a batch into rows of host-shaped CLR values.</summary>
internal static class ConformanceValues
{
    public static void Read(
        RecordBatch batch, IReadOnlyList<ColumnDescriptor> columns, List<object?[]> into)
    {
        for (var row = 0; row < batch.Length; row++)
        {
            var values = new object?[columns.Count];
            for (var c = 0; c < columns.Count && c < batch.ColumnCount; c++)
            {
                values[c] = Value(batch.Column(c), row, columns[c].Type);
            }

            into.Add(values);
        }
    }

    private static object? Value(IArrowArray array, int row, ChalkType type) => array switch
    {
        Int64Array a => a.GetValue(row),
        Int32Array a => a.GetValue(row) is { } v ? (long)v : null,
        Int16Array a => a.GetValue(row) is { } v ? (long)v : null,
        Int8Array a => a.GetValue(row) is { } v ? (long)v : null,
        DoubleArray a => a.GetValue(row),
        FloatArray a => a.GetValue(row),
        StringArray a => a.GetString(row),
        BooleanArray a => a.GetValue(row),
        Decimal128Array a => a.GetValue(row),
        Date32Array a => a.GetDateOnly(row),
        TimestampArray a => a.GetTimestamp(row)?.UtcDateTime,
        BinaryArray a => a.IsNull(row) ? null : a.GetBytes(row).ToArray(),
        _ => null,
    };

    /// <summary>Two values as the kit compares them: by their printed form, which is stable.</summary>
    public static bool Same(object? left, object? right) =>
        string.Equals(Text(left), Text(right), StringComparison.Ordinal);

    public static string Text(object? value) => value switch
    {
        null => "NULL",
        string s => s,
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>The ids in a result, in order, as text — what most probes compare.</summary>
    public static string Ids(IReadOnlyList<object?[]> rows) =>
        rows.Count == 0 ? "(none)" : string.Join(",", rows.Select(r => Text(r[0])));
}
