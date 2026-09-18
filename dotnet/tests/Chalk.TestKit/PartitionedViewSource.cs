using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.TestKit;

/// <summary>
/// A source that holds no rows: it declares partitioned tables whose rows live in other sources
/// (D106, <c>docs/design/20-m5-federation.md</c> §6).
/// </summary>
/// <remarks>
/// <para>
/// A partitioned table has to be declared <em>somewhere</em>, and the somewhere is a schema like any
/// other — but the schema that declares it never serves it. The planner expands a scan of a
/// partitioned table into its partitions before anything asks who owns it, so this source is only
/// ever read as a descriptor. Scanning one is a contract error, and it says so.
/// </para>
/// <para>
/// A real host would put a partitioned table on the source that owns its metadata — a catalog
/// service, a warehouse's information schema. The fixture has no such thing, so it has this: the
/// smallest object that can carry the declaration honestly.
/// </para>
/// </remarks>
public sealed class PartitionedViewSource : ISourceRuntime
{
    private readonly SchemaDescriptor _schema;

    public PartitionedViewSource(string sourceId, string schemaName, IReadOnlyList<TableDescriptor> tables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        SourceId = sourceId;
        _schema = new SchemaDescriptor
        {
            SourceId = sourceId,
            Name = schemaName,
            Kind = SourceKind.Local,
            Tables = tables,
        };
    }

    public string SourceId { get; }

    public SchemaDescriptor DescribeSchema() => _schema;

    public IAsyncEnumerable<RecordBatch> ScanAsync(
        ScanRequest request, ScanContext context, CancellationToken ct) =>
        throw new SourceContractException(
            SourceId,
            request?.Table ?? string.Empty,
            "this source declares partitioned tables and holds no rows: a scan of one means the "
            + "planner did not expand it into its partitions, which is a planner bug rather than a "
            + "source that cannot answer.");
}
