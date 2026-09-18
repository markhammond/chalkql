namespace Chalk.Sources;

/// <summary>What a refresh transaction asks of one table (D260, <c>docs/design/35-poco-refresh.md</c> §2).</summary>
public enum SourceRefreshKind
{
    /// <summary>The table's rows become exactly the rows given.</summary>
    Replace = 0,

    /// <summary>The rows given are added after the ones the table already holds (§3).</summary>
    Append = 1,
}

/// <summary>
/// When a refresh recomputes a table's column statistics (D271 (g),
/// <c>docs/design/44-catalog-registration.md</c> §7).
/// </summary>
/// <remarks>
/// The <em>row count</em> is not governed by this and never is: it stays exact and costs one field
/// read whatever the policy says. What this governs is the pass over the values — distinct counts,
/// minima and maxima, histograms, most-common-value lists — which a table appended a micro-batch at
/// a time would otherwise recompute over the whole table on every batch.
/// </remarks>
public enum StatisticsRefresh
{
    /// <summary>
    /// Recompute now, at the level the table declares. The default, and what every refresh did
    /// before D271.
    /// </summary>
    Now = 0,

    /// <summary>
    /// Carry the previous snapshot's column statistics forward, and leave them for the next plain
    /// refresh to recompute. The row count still moves, so a planner's cardinality is right and only
    /// its value distributions are as of the last pass.
    /// </summary>
    Defer = 1,
}

/// <summary>
/// One entry of a refresh transaction: which table, what to do with it, and the rows to do it with.
/// </summary>
/// <remarks>
/// The row type travels beside the rows because it is what the source checks against the type its
/// table was built over — and an empty list of the wrong type would otherwise be indistinguishable
/// from an empty list of the right one.
/// </remarks>
public sealed class SourceRefreshEntry
{
    /// <summary>The table, as it is named in the source's schema.</summary>
    public required string Table { get; init; }

    /// <summary>Replace the rows, or add to them.</summary>
    public required SourceRefreshKind Kind { get; init; }

    /// <summary>The CLR row type the caller wrote, which the table must have been built over.</summary>
    public required Type RowType { get; init; }

    /// <summary>The rows, as an <c>IReadOnlyList&lt;T&gt;</c> of <see cref="RowType"/>.</summary>
    public required object Rows { get; init; }

    /// <summary>
    /// Whether this operation recomputes the table's column statistics (D271 (g)).
    /// <see cref="StatisticsRefresh.Now"/> — the default — is what every refresh did before, and
    /// what a table's own policy is then free to turn down.
    /// </summary>
    public StatisticsRefresh Statistics { get; init; } = StatisticsRefresh.Now;
}

/// <summary>
/// A source whose data an engine refresh transaction can replace — the contract behind
/// <c>ChalkEngine.RefreshAsync(refresh =&gt; …)</c> (D260 §2).
/// </summary>
/// <remarks>
/// The two halves are deliberate. Everything that can be refused is refused by
/// <see cref="ValidateRefresh"/>, before any source has built anything, so a transaction with one
/// bad entry in it changes nothing anywhere. What remains is built by
/// <see cref="PrepareRefreshAsync"/> off the execution path — executions meanwhile read the state
/// this one will replace — and applied by <see cref="ISourceRefreshCommit.Commit"/>, which the
/// engine calls for every source at once and which may not fail.
/// </remarks>
public interface IRefreshableSource
{
    /// <summary>
    /// Refuses everything this source cannot do: a table it does not have, a row type its table was
    /// not built over, a kind it does not support. Throws, and changes nothing.
    /// </summary>
    void ValidateRefresh(IReadOnlyList<SourceRefreshEntry> entries);

    /// <summary>
    /// Builds the new state for every entry without publishing any of it, and hands back the commit
    /// that will. Called after <see cref="ValidateRefresh"/> has passed for every source in the
    /// transaction.
    /// </summary>
    ValueTask<ISourceRefreshCommit> PrepareRefreshAsync(
        IReadOnlyList<SourceRefreshEntry> entries, CancellationToken ct);
}

/// <summary>
/// One source's share of a refresh transaction, built and ready. <see cref="Commit"/> publishes it
/// and must not fail: by the time the engine calls it, every other source in the transaction is
/// about to be published too.
/// </summary>
public interface ISourceRefreshCommit
{
    /// <summary>Publishes what was built. Executions started after this see it; earlier ones do not.</summary>
    void Commit();
}

/// <summary>
/// What a scoped refresh names (D271 (f), <c>docs/design/44-catalog-registration.md</c> §6): one
/// source, or one table of one source.
/// </summary>
/// <remarks>
/// <para>
/// A selection and not a value: refreshing is idempotent, so a scope decided before the engine's
/// refresh lock costs nothing if another refresh got there first. There is no coherency to protect
/// in <em>what</em> to refresh, only in the rows a <c>Replace</c> or an <c>Append</c> carries, and
/// those are composed inside the lock already.
/// </para>
/// <para>
/// It lives here, beside <see cref="IRefreshableSource"/> and <see cref="SourceRefreshEntry"/>,
/// rather than in the client package: the POCO and ADO table handles implement it (D271 (h)) and
/// those packages know nothing of the engine. A host without the tenancy package reaches it the same
/// way, through <see cref="RefreshTarget"/>.
/// </para>
/// </remarks>
public interface IRefreshTarget
{
    /// <summary>
    /// The runtime's identifier — what <see cref="ISourceRuntime.SourceId"/> returns — or empty when
    /// this target names its source by schema name instead.
    /// </summary>
    string SourceId { get; }

    /// <summary>
    /// The schema's federation-level name, or empty when this target names its source by id. A handle
    /// carries both, so it resolves without either lookup being a guess.
    /// </summary>
    string Schema { get; }

    /// <summary>Empty when the whole source is the target; otherwise the one table to re-describe.</summary>
    string Table { get; }
}

/// <summary>
/// The string spellings of <see cref="IRefreshTarget"/>, for a host that has no handle to hand
/// (D271 (h)). There are two of them and no more: a source by its runtime's id, and a table by its
/// schema <em>and</em> its name. <b>There is no schema-less table target</b> — two sources holding a
/// table of one name is exactly the confusion the handles exist to prevent, and a factory that let a
/// host write the name alone would reintroduce it.
/// </summary>
public static class RefreshTarget
{
    /// <summary>
    /// Every table of the source with this id, re-introspected — discovery included for a source over
    /// a database, every registration re-read for one over collections.
    /// </summary>
    /// <param name="sourceId">
    /// The runtime's own identifier, which is what the engine was built with and what every
    /// <c>TableRef</c> names; not the schema's SQL name, which <see cref="Table"/> takes.
    /// </param>
    public static IRefreshTarget Source(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        return new NamedTarget(sourceId, string.Empty, string.Empty);
    }

    /// <summary>
    /// One table, re-described with its statistics and nothing discovered, addressed the way SQL
    /// addresses it: <c>schema.table</c>, by the schema's federation-level name.
    /// </summary>
    public static IRefreshTarget Table(string schema, string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        return new NamedTarget(string.Empty, schema, table);
    }

    private sealed record NamedTarget(string SourceId, string Schema, string Table) : IRefreshTarget;
}

/// <summary>
/// A handle to one table of one source (D271 (h),
/// <c>docs/design/44-catalog-registration.md</c> §6 (h)): the source, the schema, the name, together
/// and obtained once.
/// </summary>
/// <remarks>
/// <para>
/// A refresh aimed at a table named it twice by hand before this — a source object and a string at
/// registration, then the same pair at every <c>Replace</c> or <c>Append</c> — and with two sources
/// holding a table of one name over one row type, a refresh that named the right table on the wrong
/// source succeeded silently: the only check that could have caught it compares row types, and they
/// agreed. A handle carries the source <em>with</em> the name, so the pair cannot come apart.
/// </para>
/// <para>
/// Declared here rather than in a source package because the engine's refresh builder takes it and
/// the engine knows no source package; the POCO and ADO builders are what produce one.
/// </para>
/// </remarks>
public interface ITableTarget : IRefreshTarget
{
    /// <summary>The runtime that serves this table, carried with the name rather than beside it.</summary>
    ISourceRuntime Runtime { get; }
}

/// <summary>
/// The same over the CLR row type the table was built over (D271 (h)), so
/// <c>Replace(orders, rows)</c> checks the rows against the table at compile time rather than at the
/// refresh's validation step. An ADO table has no row type and implements
/// <see cref="ITableTarget"/> alone.
/// </summary>
/// <typeparam name="T">The row type.</typeparam>
public interface ITableTarget<out T> : ITableTarget;
