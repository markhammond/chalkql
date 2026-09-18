using System.Data.Common;
using Apache.Arrow;
using Chalk.Catalog;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Sources.Ado;

/// <summary>
/// How an ADO source turns one executed query into Arrow batches (D148,
/// <c>docs/design/24-zero-gc.md</c> §6).
/// </summary>
/// <remarks>
/// <para>
/// The default, <see cref="DbDataReaderFetch"/>, runs a <see cref="DbCommand"/> and reads its
/// <see cref="DbDataReader"/>, which is all <c>System.Data.Common</c> offers and is what keeps this
/// package's only dependency what it is (D85). A driver that can hand over whole columns — DuckDB's
/// data chunks, and one day ADBC or Flight SQL — implements this instead and skips the per-cell
/// interface entirely; <c>Chalk.Sources.DuckDb</c> is the first.
/// </para>
/// <para>
/// The connection arrives <b>open</b> and belongs to the source: an implementation runs its query on
/// it and disposes nothing. The timeout and the caller's cancellation are already folded into the
/// token; an implementation that has a second way to reach the server — <c>DbCommand.Cancel</c>,
/// <c>duckdb_interrupt</c> — registers it on that token itself. Exceptions travel out raw: the
/// source attributes them, so a failure names the source and the query whichever path produced it.
/// </para>
/// <para>
/// An implementation <b>may block the calling thread</b>: an in-process driver's fetch is a
/// synchronous native call, and pretending otherwise would cost a task per chunk. The engine pulls a
/// remote source on a prefetch task for exactly that reason (D107), and a caller that does not
/// should expect to wait.
/// </para>
/// </remarks>
public interface IRemoteFetch
{
    /// <summary>
    /// What <c>ExecutionStats.SourcePaths</c> records for a query this fetch ran. Short and stable —
    /// it is read by people and by tests, not parsed.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Runs <paramref name="request"/> and yields batches of at most its batch size, each matching
    /// its output schema exactly. The batches own their buffers and the caller disposes them.
    /// </summary>
    IAsyncEnumerable<RecordBatch> FetchAsync(RemoteFetchRequest request, CancellationToken ct);

    /// <summary>
    /// The Arrow layout this fetch's STRING columns are physically in (D244), which is what the
    /// source it serves declares as its own. Classic by default, because that is what a
    /// <see cref="DbDataReader"/> is copied into and what a driver's Arrow export produces unless it
    /// has been told otherwise; a reader that builds views says so and spares the engine a pass.
    /// </summary>
    StringLayouts NativeStringLayout => StringLayouts.Utf8;
}

/// <summary>One query to run on one open connection (D148).</summary>
/// <remarks>
/// Every field is what the source already had to hand: nothing is computed for the fetch, so a
/// second implementation cannot see a different query from the one the default would have run.
/// </remarks>
public sealed class RemoteFetchRequest
{
    /// <summary>The source's id, for the messages an implementation raises.</summary>
    public required string SourceId { get; init; }

    /// <summary>What the query is about — a table name, or the pushed query's own text.</summary>
    public required string Subject { get; init; }

    /// <summary>An open connection, owned by the source. An implementation disposes nothing.</summary>
    public required DbConnection Connection { get; init; }

    /// <summary>The SQL as this dialect spells it, placeholders already rewritten (M4 §3).</summary>
    public required string Sql { get; init; }

    /// <summary>
    /// The placeholder names in ordinal order, or empty for a positional dialect. An implementation
    /// that binds by ordinal ignores these; one that binds by name needs them.
    /// </summary>
    public required IReadOnlyList<string> ParameterNames { get; init; }

    /// <summary>The bound values, in the same order, in the CLR shapes a driver expects (§7.2).</summary>
    public required IReadOnlyList<object?> Parameters { get; init; }

    /// <summary>The declared type of each parameter, in the same order.</summary>
    public required IReadOnlyList<ChalkType> ParameterTypes { get; init; }

    /// <summary>What every batch must match exactly — names, types, nullability, order.</summary>
    public required ArrowSchema OutputSchema { get; init; }

    /// <summary>The Chalk type of each output column, in the same order as the schema.</summary>
    public required IReadOnlyList<ChalkType> ColumnTypes { get; init; }

    /// <summary>The execution's arena. Every buffer a batch carries is rented from it.</summary>
    public required ExecutionArena Arena { get; init; }

    /// <summary>
    /// The execution's counters. A fetch does not count rows — the source does — but one that can
    /// take more than one path records which it took, through
    /// <c>ExecutionStats.RecordSourcePath</c>, so a fallback is visible.
    /// </summary>
    public required ExecutionStats Stats { get; init; }

    /// <summary>Upper bound on rows per batch. An implementation may produce smaller batches.</summary>
    public required int BatchSize { get; init; }

    /// <summary>The per-source timeout, already folded into the token; here to set on a command.</summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>How this source spells and evaluates SQL, for a path that needs to know.</summary>
    public required DialectProfileDescriptor Profile { get; init; }

    /// <summary>
    /// What the host or a vendor package declared about this source's provider (D263, ADR 0046), or
    /// null when nothing was. <see cref="AdoProviderTraits.Text"/> is used without a probe;
    /// everything left undeclared is measured once per process per column type, as before.
    /// </summary>
    public AdoProviderTraits? Traits { get; init; }
}
