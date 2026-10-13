using Chalk.Sources;

namespace Chalk.Client;

/// <summary>
/// A table's replacement, built before the refresh that commits it (design 69 §3): what
/// <see cref="ChalkEngine.PrepareReplaceAsync{T}(ISourceRuntime, string, IReadOnlyList{T}, int, CancellationToken)"/>
/// returns and <see cref="RefreshBuilder.Commit"/> takes.
/// </summary>
/// <remarks>
/// <para>
/// The commit appends the rows the table took since the cut to the replacement and publishes it, in
/// the refresh's epoch with whatever else the refresh holds. It is refused when the table was replaced
/// after this was prepared, and when the rows since the cut do not follow the replacement in the
/// table's declared order; the replacement then stays uncommitted.
/// </para>
/// <para>
/// A replacement is committed once. Disposing it before then lets it go; disposing it after does
/// nothing, since it then belongs to its table.
/// </para>
/// </remarks>
public sealed class PreparedReplacement : IDisposable
{
    internal PreparedReplacement(ISourceRuntime source, IPreparedReplacement prepared)
    {
        Source = source;
        Prepared = prepared;
    }

    /// <summary>The source whose table it replaces.</summary>
    public ISourceRuntime Source { get; }

    /// <summary>The table, as it is named in the source's schema.</summary>
    public string Table => Prepared.Table;

    /// <summary>The cut: how many of the table's rows the replacement rewrote.</summary>
    public int Through => Prepared.Through;

    /// <summary>The rows the replacement holds.</summary>
    public int RowCount => Prepared.RowCount;

    /// <summary>Whether a refresh may still commit it: neither committed nor disposed.</summary>
    public bool Pending => Prepared.Pending;

    internal IPreparedReplacement Prepared { get; }

    /// <inheritdoc />
    public void Dispose() => Prepared.Dispose();

    /// <inheritdoc />
    public override string ToString() => $"{Table}: {RowCount} rows through row {Through}";
}
