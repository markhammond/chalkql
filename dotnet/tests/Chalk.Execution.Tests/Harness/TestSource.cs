using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Reference;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;
using CatalogContext = Chalk.Catalog.CatalogContext;
using ColumnStatistics = Chalk.Catalog.ColumnStatistics;
using Field = Chalk.Ir.Field;
using SourceKind = Chalk.Ir.SourceKind;

namespace Chalk.Execution.Tests.Harness;

/// <summary>
/// A table of boxed rows, in the reference executor's storage representation: <c>bool</c>,
/// <c>long</c> for every exact and temporal kind, <c>float</c>/<c>double</c>, <c>string</c>,
/// <c>byte[]</c>, <c>decimal</c>. Written this way so a test can spell a NULL, a NaN or a
/// sub-microsecond timestamp directly.
/// </summary>
internal sealed class TestTable
{
    public required string Name { get; init; }

    public required IReadOnlyList<(string Name, ChalkType Type)> Columns { get; init; }

    public required IReadOnlyList<object?[]> Rows { get; init; }

    public IReadOnlyList<CollationDescriptor> Collations { get; init; } = [];

    /// <summary>
    /// What the catalog declares about a column's values, by column index (D36). Empty — the default
    /// — says nothing at all, which is what most tables want; the aggregate's perfect hash (D255)
    /// is the reader that cares.
    /// </summary>
    public IReadOnlyDictionary<int, ColumnStatistics> Statistics { get; init; } =
        new Dictionary<int, ColumnStatistics>();

    public RowType RowType()
    {
        var row = new RowType();
        foreach (var (name, type) in Columns)
        {
            row.Fields.Add(new Field { Name = name, Type = type.ToProto() });
        }

        return row;
    }
}

/// <summary>An <see cref="ISourceRuntime"/> over <see cref="TestTable"/>s, so operator tests can
/// choose exactly what rows arrive and in what batches.</summary>
internal sealed class TestSource : ISourceRuntime
{
    private readonly TestTable[] _tables;

    public TestSource(string sourceId, string schemaName, params TestTable[] tables)
    {
        SourceId = sourceId;
        SchemaName = schemaName;
        _tables = tables;
    }

    public string SourceId { get; }

    public string SchemaName { get; }

    /// <summary>
    /// What the schema declares about the source's SQL. Local sources declare nothing, which is the
    /// default; a test sets it to say what a source <em>claims</em> — a non-binary
    /// <c>StringCollation</c>, say — and then check that the claim changes what is pushed and not
    /// what a kernel computes (D89, §5 F).
    /// </summary>
    public DialectProfileDescriptor Profile { get; init; } = DialectProfileDescriptor.None;

    /// <summary>How many times a scan was started — the counter the operator tests assert on.</summary>
    public int Scans { get; private set; }

    /// <summary>
    /// What this source claims its STRING columns are physically in (D244). Views by default, as the
    /// interface's own default is; a test sets it to <c>Utf8</c> to stand for a source whose rows
    /// arrive as classic Arrow strings.
    /// </summary>
    public StringLayouts NativeStringLayout { get; init; } = StringLayouts.Utf8View;

    public SchemaDescriptor DescribeSchema() => new()
    {
        SourceId = SourceId,
        Name = SchemaName,
        Kind = SourceKind.Local,
        DialectProfile = Profile,
        Tables = [.. _tables.Select(t => new TableDescriptor
        {
            Name = t.Name,
            Columns = [.. t.Columns.Select((c, i) => new ColumnDescriptor
            {
                Name = c.Name,
                Type = c.Type,
                Statistics = t.Statistics.TryGetValue(i, out var statistics)
                    ? statistics
                    : ColumnStatistics.Unknown,
            })],
            RowCount = t.Rows.Count,
            Collations = t.Collations,
        })],
    };

    public CatalogContext Catalog(string contextId = "test", long epoch = 1) => new()
    {
        ContextId = contextId,
        Epoch = epoch,
        Schemas = [DescribeSchema()],
    };

    public IAsyncEnumerable<RecordBatch> ScanAsync(
        ScanRequest request, ScanContext context, CancellationToken ct)
    {
        var table = _tables.FirstOrDefault(
                t => string.Equals(t.Name, request.Table, StringComparison.OrdinalIgnoreCase))
            ?? throw new SourceContractException(SourceId, request.Table, "no such table");
        Scans++;

        var types = request.Projection.Select(i => table.Columns[i].Type).ToArray();
        var projected = table.Rows
            .Select(row => request.Projection.Select(i => row[i]).ToArray())
            .ToArray();

        return Batches(projected, request.OutputSchema, types, request.BatchSize, context, ct);
    }

    private static async IAsyncEnumerable<RecordBatch> Batches(
        IReadOnlyList<object?[]> rows,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        int batchSize,
        ScanContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var batch in ReferenceValues.ToBatches(rows, schema, types, batchSize, context.Allocator))
        {
            // A batch is built before it can be handed over, and ownership only transfers when it is
            // yielded (ISourceRuntime.ScanAsync). Throwing between the two would leave its buffers —
            // this scan's arena rentals — with nobody to return them, so the batch goes back first.
            if (ct.IsCancellationRequested)
            {
                batch.Dispose();
                ct.ThrowIfCancellationRequested();
            }

            context.Stats.AddRowsScanned(batch.Length);
            yield return batch;
        }
    }
}
