using Apache.Arrow;
using Apache.Arrow.Memory;
using Chalk.Catalog;
using Chalk.Ir;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Sources;

public enum SourceSharing
{
    /// <summary>
    /// Whether this source instance may be attached to multiple live engines.
    /// Shared is the safe default; exclusive sources avoid cross-engine refresh coordination.
    /// </summary>
    Shared = 0,
    Exclusive = 1,
}

/// <summary>
/// The public extension point (rev 3 §3). A source describes its tables to the catalog and produces
/// Arrow batches when the engine scans them.
/// </summary>
/// <remarks>
/// Later milestones add members here — <c>IndexLookupAsync</c> (M2) and <c>ExecuteQueryAsync</c>
/// (M4/M5) — as new interface members with default implementations that throw, so an existing
/// adapter keeps compiling. That is why the request types are classes with <c>init</c> properties
/// rather than positional records.
/// </remarks>
public interface ISourceRuntime
{
    bool TryClaimEngine(object identity, out SourceSharing mode)
    {
        mode = SourceSharing.Shared;
        return true;
    }

    void ReleaseEngine(object identity)
    {
    }

    /// <summary>
    /// Identifies this source in the catalog and in every <c>TableRef</c> that names it.
    /// </summary>
    string SourceId { get; }

    /// <summary>
    /// The schema this source contributes. Stable for the life of a catalog epoch.
    /// </summary>
    SchemaDescriptor DescribeSchema();

    /// <summary>
    /// Which Arrow layout this source's STRING columns are physically in by the time the engine sees
    /// them (D244). Read at prepare time, and only to decide what a prepared query <em>declares</em>
    /// under <see cref="StringLayouts.Any"/>: whatever a source actually produces, the output
    /// materialiser emits the declared layout, so a source that gets this wrong costs a conversion
    /// and never a wrong answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is <see cref="StringLayouts.Utf8View"/>, the executor's own representation, which
    /// is right for every in-box source that fills a columnar slot. A source whose rows arrive as
    /// classic Arrow <c>StringArray</c>s — an ADO.NET reader, a driver's own Arrow export — says
    /// <see cref="StringLayouts.Utf8"/> and spares the engine a pass.
    /// </para>
    /// <para>
    /// This says nothing about the schema a source is <em>handed</em>: <see cref="ScanRequest.OutputSchema"/>
    /// and <see cref="RemoteQueryRequest.OutputSchema"/> declare <c>utf8</c> as they always have, and
    /// a batch must still match the schema it was asked for.
    /// </para>
    /// </remarks>
    StringLayouts NativeStringLayout => StringLayouts.Utf8View;

    /// <summary>
    /// Re-reads whatever the descriptor was derived from, so the next
    /// <see cref="DescribeSchema"/> reflects it (D86). Called by
    /// <c>ChalkEngine.RefreshCatalogAsync</c>, once per source, before the new epoch is assembled.
    /// </summary>
    /// <remarks>
    /// The default does nothing, which is right for a source whose schema comes from code — a POCO
    /// source's tables are its types, and those do not change while the process runs. A source that
    /// introspects a live database overrides it and introspects again; a table that gained a column
    /// then appears in the next epoch, and every plan from the epoch before it is refused rather
    /// than run against a shape it was not compiled for.
    /// </remarks>
    ValueTask RefreshAsync(CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>
    /// Re-reads whatever <em>one</em> table's descriptor was derived from (D271 (f),
    /// <c>docs/design/44-catalog-registration.md</c> §6). Called by a scoped refresh —
    /// <c>RefreshAsync(r =&gt; r.Refresh(orders))</c> — once per table named, before the new epoch is
    /// assembled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A default interface member rather than a new <see cref="SourceRefreshKind"/>, because the two
    /// are different things: a <see cref="SourceRefreshEntry"/> carries <em>rows the host is
    /// supplying</em> and is validated, prepared and committed as a transaction, while this asks the
    /// source to go and look again and carries nothing. Adding a kind would have given every
    /// implementation of <see cref="IRefreshableSource"/> a case to handle that has no rows in it,
    /// and would have left a source that is <em>not</em> refreshable — an ADO source, which is
    /// exactly the one a re-introspection is for — with no way to be named at all.
    /// </para>
    /// <para>
    /// The default re-reads the whole source, which is always correct and never wrong: a source that
    /// cannot single one table out does more work than it was asked to and reports the same
    /// descriptor. A source that can — an ADO source re-describing one table, a POCO source re-reading
    /// one registration — overrides it.
    /// </para>
    /// </remarks>
    ValueTask RefreshTableAsync(string table, CancellationToken ct) => RefreshAsync(ct);

    /// <summary>
    /// Produces the requested rows as Arrow batches. Every batch must match
    /// <see cref="ScanRequest.OutputSchema"/> exactly; ownership transfers to the consumer, which
    /// disposes it. A source must not reuse the buffers of a batch it has yielded, and must observe
    /// cancellation promptly.
    /// </summary>
    IAsyncEnumerable<RecordBatch> ScanAsync(
        ScanRequest request,
        ScanContext context,
        CancellationToken ct);

    /// <summary>
    /// Produces the rows a declared index matches, as Arrow batches (M2, D37). The same batch
    /// contract as <see cref="ScanAsync"/> applies, plus two more: rows come out in the index's key
    /// order, and <c>RowsScanned</c> counts only the rows the lookup actually read.
    /// </summary>
    /// <remarks>
    /// The default throws: a source that declares no index is never asked, and one that declares an
    /// index but does not implement this is a contract error worth hearing about rather than a
    /// silent full scan. A source declaring <see cref="Chalk.Ir.IndexKind.Hash"/> may reject a
    /// non-equality range with <see cref="SourceContractException"/>.
    /// </remarks>
    IAsyncEnumerable<RecordBatch> IndexLookupAsync(
        IndexLookupRequest request,
        ScanContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        throw new UnsupportedFeatureException(
            $"IndexLookupAsync on source '{SourceId}'",
            $"The plan asks for a lookup on index '{request.Index}' of table '{request.Table}', but "
            + "this source does not implement IndexLookupAsync. A source that declares an index in "
            + "its schema must serve lookups against it.");
    }

    /// <summary>
    /// Runs a whole pushed subtree and produces its rows as Arrow batches (M4, D84). The same batch
    /// contract as <see cref="ScanAsync"/> applies: every batch matches
    /// <see cref="RemoteQueryRequest.OutputSchema"/> exactly, ownership transfers to the consumer,
    /// and cancellation is observed promptly — for a driver that means reaching its own cancel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default throws, for the same reason <see cref="IndexLookupAsync"/>'s does: a source that
    /// declares <see cref="Chalk.Ir.QueryLanguage.None"/> is never asked, and one that declares SQL
    /// or IR and does not implement this is a contract error worth hearing about rather than a
    /// silent full scan.
    /// </para>
    /// <para>
    /// A source must still implement <see cref="ScanAsync"/>: that is what
    /// <c>PushdownLevel.None</c> reads, and it is the reference (I4) configuration every result is
    /// compared against — as well as what the conformance kit runs a capability's answers against.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<RecordBatch> ExecuteQueryAsync(
        RemoteQueryRequest request,
        ScanContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        throw new UnsupportedFeatureException(
            $"ExecuteQueryAsync on source '{SourceId}'",
            "The plan pushed a query into this source, but it does not implement "
            + "ExecuteQueryAsync. A source whose SourceCapabilities.QueryLanguage is Sql or Ir must "
            + "serve queries; one that declares None is only ever scanned.");
    }
}

/// <summary>
/// What the engine wants looked up. The index-driven twin of <see cref="ScanRequest"/>.
/// </summary>
public sealed class IndexLookupRequest
{
    public required string Table { get; init; }

    /// <summary>
    /// The declared index's name, as it appears in <c>TableDescriptor.Indexes</c>.
    /// </summary>
    public required string Index { get; init; }

    /// <summary>
    /// The ranges to union, already bound. Empty means the lookup matches nothing — which happens
    /// when every range collapsed, for instance because a parameter was bound to NULL.
    /// </summary>
    public required IReadOnlyList<IndexKeyRange> Ranges { get; init; }

    /// <summary>
    /// Table column indexes, in output order.
    /// </summary>
    public required IReadOnlyList<int> Projection { get; init; }

    /// <summary>
    /// What every produced batch must match exactly — names, types, nullability, order.
    /// </summary>
    public required ArrowSchema OutputSchema { get; init; }

    /// <summary>
    /// Upper bound on rows per batch. A source may produce smaller batches.
    /// </summary>
    public required int BatchSize { get; init; }

    /// <summary>
    /// About how many rows of this lookup's output the consumer expects to pull before it stops,
    /// or null when the plan said nothing. A hint and never a bound, on exactly the terms
    /// <see cref="ScanRequest.RowGoal"/> sets out.
    /// </summary>
    public long? RowGoal { get; init; }

    /// <summary>
    /// Read the matched rows from the last to the first (D283): the same rows, in the reverse of the
    /// index's key order. Unlike <see cref="RowGoal"/> this <em>is</em> a requirement — the plan has
    /// no sort above it — and it is set only where the index declared it could serve one.
    /// </summary>
    public bool Reverse { get; init; }
}

/// <summary>
/// Bounds over a prefix of an index's key columns, with the values already converted to the CLR
/// shapes the source's rows hold (<c>04-client.md</c> §7.2). An empty list on a side means
/// unbounded there; equal inclusive bounds are an equality lookup.
/// </summary>
/// <remarks>
/// All but the last bound column are equalities, so <see cref="Lower"/> and <see cref="Upper"/>
/// agree except possibly in their last element, and at most one of them may be shorter than the
/// other by one. A source may rely on that (D37).
/// </remarks>
public sealed class IndexKeyRange
{
    /// <summary>
    /// An empty prefix on both sides: every row of the index, in key order.
    /// </summary>
    public static IndexKeyRange All { get; } =
        new() { Lower = [], Upper = [] };

    public required IReadOnlyList<object?> Lower { get; init; }

    public bool LowerInclusive { get; init; } = true;

    public required IReadOnlyList<object?> Upper { get; init; }

    public bool UpperInclusive { get; init; } = true;

    /// <summary>
    /// "The last bound column starts with this text", for an index whose kind is
    /// <see cref="Chalk.Ir.IndexKind.Prefix"/> (D282). Null for every other range, which is every
    /// range any other kind of index is ever sent.
    /// </summary>
    /// <remarks>
    /// <see cref="Lower"/> then holds the equality prefix and this text as its last element, and
    /// <see cref="Upper"/> holds the equality prefix alone, so the bounded columns — and with them
    /// the rule that a NULL in a bounded column never matches — read exactly as they do for any
    /// other range. An ORDERED index is never sent one: the client turns a prefix into the plain
    /// half-open range it is before the source sees it.
    /// </remarks>
    public string? Prefix { get; init; }

    /// <summary>
    /// An equality lookup on the given key prefix.
    /// </summary>
    public static IndexKeyRange Equality(params object?[] key) =>
        new()
        {
            Lower = key,
            Upper = key,
            LowerInclusive = true,
            UpperInclusive = true
        };

    /// <summary>
    /// How many leading key columns this range constrains at all.
    /// </summary>
    public int BoundedColumns =>
        Math.Max(Lower.Count, Upper.Count);

    /// <summary>
    /// Whether <paramref name="key"/> — one row's key values, in key order — falls inside this
    /// range.
    /// </summary>
    /// <remarks>
    /// A NULL in a column the range bounds never matches, whichever way the bound points: the range
    /// stands for a SQL comparison, and a comparison with NULL is unknown, so the row is dropped.
    /// A range that bounds nothing (<see cref="All"/>) matches every row, NULLs included.
    /// </remarks>
    public bool Contains(
        IReadOnlyList<object?> key,
        IReadOnlyList<SortDirection> directions)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(directions);

        var bounded = Math.Min(BoundedColumns, key.Count);

        for (var i = 0; i < bounded; i++)
        {
            if (key[i] is null)
                return false;
        }

        if (Prefix is not null)
        {
            // The columns before the last are equalities; the last starts with the prefix.
            for (var i = 0; i + 1 < Lower.Count && i < key.Count; i++)
            {
                if (SourceValueOrder.Compare(
                        key[i],
                        Lower[i],
                        i < directions.Count ? directions[i] : SortDirection.AscNullsLast) != 0)
                {
                    return false;
                }
            }

            var last = Lower.Count - 1;
            return last < key.Count
                && IndexPrefix.AsText(key[last]) is { } text
                && text.StartsWith(Prefix, StringComparison.Ordinal);
        }

        if (Lower.Count > 0)
        {
            var comparison = ComparePrefix(key, Lower, directions);

            if (comparison < 0 ||
                (comparison == 0 && !LowerInclusive))
            {
                return false;
            }
        }

        if (Upper.Count > 0)
        {
            var comparison = ComparePrefix(key, Upper, directions);

            if (comparison > 0 ||
                (comparison == 0 && !UpperInclusive))
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString()
    {
        if (Prefix is not null)
        {
            return "prefix(" + string.Join(", ", Lower.Select(Render)) + ")";
        }

        var lower = Lower.Count == 0
            ? "-inf"
            : "(" + string.Join(", ", Lower.Select(Render)) + ")";

        var upper = Upper.Count == 0
            ? "+inf"
            : "(" + string.Join(", ", Upper.Select(Render)) + ")";

        return $"{(LowerInclusive ? '[' : '(')}{lower}, {upper}{(UpperInclusive ? ']' : ')')}";
    }

    private static int ComparePrefix(
        IReadOnlyList<object?> key,
        IReadOnlyList<object?> bound,
        IReadOnlyList<SortDirection> directions)
    {
        var columns = Math.Min(bound.Count, key.Count);

        for (var i = 0; i < columns; i++)
        {
            var direction = i < directions.Count
                ? directions[i]
                : SortDirection.AscNullsLast;

            var comparison = SourceValueOrder.Compare(
                key[i],
                bound[i],
                direction);

            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private static string Render(object? value) => value switch
    {
        null => "NULL",
        string text => $"'{text}'",
        ReadOnlyMemory<byte> bytes => $"0x{Convert.ToHexString(bytes.Span)}",
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        _ => System.Convert.ToString(
                 value,
                 System.Globalization.CultureInfo.InvariantCulture)
             ?? string.Empty,
    };
}

/// <summary>
/// What the engine wants scanned.
/// </summary>
public sealed class ScanRequest
{
    public required string Table { get; init; }

    /// <summary>
    /// Table column indexes, in output order.
    /// </summary>
    public required IReadOnlyList<int> Projection { get; init; }

    /// <summary>
    /// What every produced batch must match exactly — names, types, nullability, order.
    /// </summary>
    public required ArrowSchema OutputSchema { get; init; }

    /// <summary>
    /// Upper bound on rows per batch. A source may produce smaller batches.
    /// </summary>
    public required int BatchSize { get; init; }

    /// <summary>
    /// A predicate the source applies itself, over the <em>table's</em> row. Null until M4; a source
    /// that receives one it did not declare MUST throw <see cref="SourceContractException"/>.
    /// </summary>
    public Expr? PushedFilter { get; init; }

    /// <summary>
    /// About how many rows of this scan's output the consumer expects to pull before it stops, or
    /// null when the plan said nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hint and never a bound. The scan must still hand back every row it is asked for, and
    /// whatever truncates the result is the operator above that says so. A source stops producing
    /// because its consumer stopped pulling, never because it counted to this number, so what a
    /// source does with the goal cannot change an answer — unlike <see cref="PushedFilter"/>, which
    /// a source that never declared it must refuse.
    /// </para>
    /// <para>
    /// What it is for is sizing the first batch: honour it where a row is cheap next to a batch, as
    /// it is for an in-memory structure, or where it sizes a fetch. Leave it unread where the
    /// backend computes the whole result whatever the consumer does — there the win is a
    /// <c>LIMIT</c> in the query the source is sent, not a smaller first batch.
    /// </para>
    /// </remarks>
    public long? RowGoal { get; init; }
}

/// <summary>
/// Ambient state for a scan: where to count, where to allocate, and the bound parameters.
/// </summary>
public sealed class ScanContext
{
    /// <summary>
    /// The source increments <c>RowsScanned</c>, and <c>BytesFetched</c> /
    /// <c>RemoteCalls</c> where they apply.
    /// </summary>
    public required ExecutionStats Stats { get; init; }

    /// <summary>
    /// Where this execution's memory comes from: Arrow buffers through <see cref="Allocator"/>,
    /// and staging through <c>Rent</c> / <c>Return</c>
    /// (<c>docs/design/08-execution-arena.md</c> §2). A source must return everything it rents
    /// before its scan's <c>DisposeAsync</c> completes.
    /// </summary>
    public required ExecutionArena Arena { get; init; }

    /// <summary>
    /// The Arrow allocator to build buffers with — the arena's, always.
    /// </summary>
    public MemoryAllocator Allocator => Arena.Allocator;

    /// <summary>
    /// Bound parameter values, by <c>DynamicParam.index</c>. Empty unless the scan needs them.
    /// </summary>
    public IReadOnlyList<object?> Parameters { get; init; } = [];
}