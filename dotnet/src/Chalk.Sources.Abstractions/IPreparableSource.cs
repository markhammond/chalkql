namespace Chalk.Sources;

/// <summary>
/// A source that can build a table's replacement before the refresh that commits it (design 69 §3).
/// The build runs on the caller's task and takes no refresh lock, so the table's appends keep landing
/// while it runs.
/// </summary>
internal interface IPreparableSource
{
    /// <summary>
    /// Builds a replacement for <paramref name="table"/> from <paramref name="rows"/>, the host's
    /// rewrite of the table's rows before row <paramref name="through"/>, and publishes nothing. A
    /// refresh commits it later with <see cref="SourceRefreshKind.Commit"/>.
    /// </summary>
    /// <param name="table">The table, as it is named in the source's schema.</param>
    /// <param name="rowType">The CLR row type the caller wrote, which the table must have been built over.</param>
    /// <param name="rows">
    /// The rows, as an <c>IReadOnlyList&lt;T&gt;</c> of <paramref name="rowType"/>; or, with
    /// <paramref name="owned"/>, an array of it the source takes over.
    /// </param>
    /// <param name="through">The cut: how many of the table's rows the rewrite covers.</param>
    /// <param name="owned">
    /// How many of the array's first rows are the rewrite, when the source takes the array over rather
    /// than copying the rows (F245); null for a list it copies.
    /// </param>
    /// <param name="ct">Checked before the build starts.</param>
    ValueTask<IPreparedReplacement> PrepareReplaceAsync(
        string table, Type rowType, object rows, int through, int? owned, CancellationToken ct);
}

/// <summary>
/// A replacement built and waiting for a refresh to commit it (design 69 §3). Disposing it before then
/// lets it go; once committed, it belongs to its table.
/// </summary>
internal interface IPreparedReplacement : IDisposable
{
    /// <summary>The table, as it is named in the source's schema.</summary>
    string Table { get; }

    /// <summary>The CLR row type the replacement was built over.</summary>
    Type RowType { get; }

    /// <summary>The cut: how many of the table's rows the replacement rewrote.</summary>
    int Through { get; }

    /// <summary>The rows the replacement holds.</summary>
    int RowCount { get; }

    /// <summary>Whether a refresh may still commit it: neither committed nor disposed.</summary>
    bool Pending { get; }
}
