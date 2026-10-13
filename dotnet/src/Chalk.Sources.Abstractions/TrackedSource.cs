namespace Chalk.Sources;

/// <summary>
/// A source that can say what changed in a table between two of its pinned states, for a view kept by
/// incremental maintenance (D343, <c>docs/design/68-incremental-maintenance.md</c> §3): the rows an
/// append added, or that the table was rewritten some other way.
/// </summary>
/// <remarks>
/// The states are the ones <see cref="IPinnableSource.Pin"/> hands out. A maintained view holds the state
/// it last read across updates, by <see cref="Retain"/>, and lets it go with
/// <see cref="IPinnableSource.Unpin"/> once it has read the next.
/// </remarks>
internal interface ITrackedSource : IPinnableSource
{
    /// <summary>
    /// Where <paramref name="later"/> continues <paramref name="earlier"/>: the first row an append
    /// added after it, when only appends separate the two, so every earlier row is still in its place;
    /// or -1 when <paramref name="later"/> rewrote the table, by a replacement, an upsert or a delete.
    /// Two references to one state give its row count.
    /// </summary>
    long AppendedFrom(string table, object earlier, object later);

    /// <summary>
    /// Another reference to <paramref name="pinned"/>, a state the caller already holds, so that it
    /// outlives the execution that pinned it. Let go with <see cref="IPinnableSource.Unpin"/>.
    /// </summary>
    object Retain(string table, object pinned);

    /// <summary>
    /// A scan of <paramref name="pinned"/>'s rows from position <paramref name="from"/> on, in their
    /// order, as <paramref name="request"/> asks: what changed since a state whose rows ended there.
    /// </summary>
    IColumnarScan ScanFrom(string table, object pinned, long from, ScanRequest request, ScanContext context);
}
