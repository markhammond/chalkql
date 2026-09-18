using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// A source that holds no rows: it declares a partitioned table whose partitions live in other
/// sources. The planner expands a scan of one into its partitions before anything asks who owns it,
/// so a descriptor is all this has to be — and scanning it is a contract error, which it says.
/// </summary>
/// <remarks>
/// A real host would put the declaration on whatever already owns the metadata: a catalog service,
/// a warehouse's information schema. This is the smallest object that can carry it honestly.
/// </remarks>
public sealed class PartitionCatalog : ISourceRuntime
{
    private readonly SchemaDescriptor _schema;

    public PartitionCatalog(string schemaName, IReadOnlyList<TableDescriptor> tables)
    {
        SourceId = schemaName;
        _schema = new SchemaDescriptor
        {
            SourceId = schemaName,
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
            "this source declares a partitioned table and holds no rows: a scan of one means the "
            + "planner did not expand it into its partitions.");

    /// <summary>
    /// One logical table over per-value partitions: <paramref name="placement"/> maps a schema name
    /// to the values it holds, and <paramref name="physical"/> names the table each value lives in.
    /// </summary>
    public static TableDescriptor Table(
        string name,
        IReadOnlyList<ColumnDescriptor> columns,
        int partitionColumn,
        IReadOnlyList<(string Schema, string Value)> placement,
        Func<string, string> physical,
        long rowsPerPartition) =>
        new()
        {
            Name = name,
            Columns = columns,
            RowCount = rowsPerPartition * placement.Count,
            RowCountKind = RowCountKind.Estimate,
            Partitioning = new PartitioningDescriptor
            {
                PartitionColumn = partitionColumn,
                Partitions =
                [
                    .. placement.Select(p => new PartitionDescriptor
                    {
                        Schema = p.Schema,
                        Table = physical(p.Value),
                        Value = p.Value,
                        HasValue = true,
                        RowCount = rowsPerPartition,
                    }),
                ],
            },
        };
}
