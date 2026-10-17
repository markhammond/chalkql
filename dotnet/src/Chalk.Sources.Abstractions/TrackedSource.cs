namespace Chalk.Sources;

/// <summary>
/// A source that can say what changed in a table between two of its pinned states, for a view kept by
/// incremental maintenance (D343, <c>docs/design/68-incremental-maintenance.md</c> §3, §15): the rows an
/// append added; the rows a replacement took out and appended, when it kept the rest in their order; or
/// that the table was rewritten some other way.
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

    /// <summary>
    /// When <paramref name="later"/> holds <paramref name="earlier"/>'s rows with some taken out, the
    /// rest in their order, and rows appended after them — as a host's compaction of a log makes it —
    /// the rows taken out and where the appended rows begin; and, for a source that knows them, the
    /// rows put in the places of rows taken out, as a keyed refresh's replacements are (D367). Null for
    /// any other rewrite, which is read whole. Asked only when <see cref="AppendedFrom"/> says -1.
    /// </summary>
    TableDiff? DiffFrom(string table, object earlier, object later);

    /// <summary>A scan of <paramref name="pinned"/>'s rows at <paramref name="positions"/>, ascending.</summary>
    IColumnarScan ScanAt(string table, object pinned, int[] positions, ScanRequest request, ScanContext context);

    /// <summary>
    /// Whether <paramref name="index"/> on <paramref name="table"/> hands over rows with equal keys in
    /// their order in the table, as an index the source builds itself does: what lets a view keep the
    /// ties of an as-of join over a lookup (design 68 §19). False for an index it cannot vouch for, such
    /// as one a host supplied.
    /// </summary>
    bool OrdersTiesByPlace(string table, string index);
}

/// <summary>
/// What a replacement changed in a table, when it kept the rest of its rows in their order (§15): the
/// positions of the rows it took out, in the earlier state, ascending; and the first of the rows it
/// appended, in the later state.
/// </summary>
internal sealed record TableDiff(int[] Removed, long AppendedFrom)
{
    /// <summary>
    /// The positions, in the later state and ascending, of rows put where rows it took out stood: a
    /// keyed refresh's replacements, which keep their rows' places (D367). Empty for a compaction.
    /// </summary>
    public int[] Added { get; init; } = [];
}
