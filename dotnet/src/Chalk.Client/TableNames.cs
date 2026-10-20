namespace Chalk.Client;

/// <summary>A table by its source and name, its name compared without regard to case, as the catalog resolves it.</summary>
internal sealed class TableNames : IEqualityComparer<(string SourceId, string Table)>
{
    public static readonly TableNames Instance = new();

    public bool Equals((string SourceId, string Table) x, (string SourceId, string Table) y) =>
        string.Equals(x.SourceId, y.SourceId, StringComparison.Ordinal)
        && string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string SourceId, string Table) table) =>
        HashCode.Combine(table.SourceId, StringComparer.OrdinalIgnoreCase.GetHashCode(table.Table));
}
