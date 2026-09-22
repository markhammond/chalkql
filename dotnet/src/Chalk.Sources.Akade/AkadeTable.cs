namespace Chalk.Sources.Akade;

/// <summary>
/// The sole table exposed by an Akade source. The runtime, schema and table name travel together,
/// so RefreshBuilder.Replace/Append cannot accidentally address the same-named table on another source.
/// </summary>
public sealed class AkadeTable<T> : ITableTarget<T>
{
    internal AkadeTable(ISourceRuntime runtime, string schema, string table)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        Runtime = runtime;
        SourceId = runtime.SourceId;
        Schema = schema;
        Table = table;
    }

    public ISourceRuntime Runtime { get; }

    public string SourceId { get; }

    public string Schema { get; }

    public string Table { get; }
}
