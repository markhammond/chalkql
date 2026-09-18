using System.Data.Common;
using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Sources.Ado;
using DuckDB.NET.Data;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Sources.DuckDb;

/// <summary>
/// The second native DuckDB reader (D148a, <c>docs/design/25-coverage-graft.md</c> §0a): DuckDB's
/// own chunk-to-Arrow export, imported through Arrow's C Data Interface by the provider and handed
/// straight to the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Where <see cref="DuckDbFetch"/> copies each vector into arena staging, this one copies nothing:
/// <c>DuckDBCommand.ExecuteArrowBatchesAsync</c> exports a data chunk as an Arrow array and the
/// batch <em>is</em> DuckDB's memory. A batch is therefore not the arena's, and the pipeline's own
/// <c>batch.Dispose()</c> — which every consumer of <see cref="IRemoteFetch"/> already does — is
/// what releases it. The prefetch depth (D107) bounds how many are live at once.
/// </para>
/// <para>
/// DuckDB's chunk is 2 048 rows and that is the batch, so this path may hand over more rows than
/// <see cref="RemoteFetchRequest.BatchSize"/> asked for. That is legal — the contract calls the
/// batch size an upper bound an implementation may miss, and the operators grow their scratch for a
/// source that ignores it — and slicing instead would be wrong: a slice shares the imported buffers
/// without sharing their lifetime, so disposing one slice frees the rows of every other (V47).
/// </para>
/// <para>
/// The export's schema is DuckDB's, not the plan's: every field comes back nullable and a type is
/// whatever DuckDB exports rather than what the catalog declared. Each batch is re-wrapped in
/// <see cref="RemoteFetchRequest.OutputSchema"/> — no buffer moves — and a column whose exported
/// type is not the declared one sends the whole query to <see cref="DbDataReaderFetch"/>, decided on
/// the first batch. <c>ExecutionStats.SourcePaths</c> then names both paths.
/// </para>
/// <para>
/// Parameters are bound through the same prepared <see cref="DbCommand"/> the
/// <c>DbDataReader</c> path uses, so a query cannot mean one thing on one path and another on the
/// other, and cancellation is <see cref="DbCommand.Cancel"/> on the token exactly as there (D86).
/// </para>
/// </remarks>
public sealed class DuckDbArrowFetch : IRemoteFetch
{
    /// <summary>The one instance; it holds no state.</summary>
    public static DuckDbArrowFetch Instance { get; } = new();

    private DuckDbArrowFetch()
    {
    }

    /// <inheritdoc />
    public string Name => "duckdb-arrow";

    /// <inheritdoc />
    public IAsyncEnumerable<RecordBatch> FetchAsync(RemoteFetchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A host that pointed an ADO source at DuckDB through some other connection type gets the
        // path every provider gets, rather than a failure.
        return request.Connection is DuckDBConnection duck
            ? ArrowAsync(request, duck, ct)
            : Fallback(request, ct);
    }

    private static IAsyncEnumerable<RecordBatch> Fallback(
        RemoteFetchRequest request, CancellationToken ct)
    {
        request.Stats.RecordSourcePath(request.SourceId, DbDataReaderFetch.Instance.Name);
        return DbDataReaderFetch.Instance.FetchAsync(request, ct);
    }

    private static async IAsyncEnumerable<RecordBatch> ArrowAsync(
        RemoteFetchRequest request,
        DuckDBConnection connection,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var command = connection.CreateCommand();
        command.CommandText = request.Sql;
        command.CommandTimeout = request.Timeout == Timeout.InfiniteTimeSpan
            ? 0
            : Math.Max(1, (int)Math.Ceiling(request.Timeout.TotalSeconds));
        AdoParameters.Bind(command, request);

        // The export runs inside the provider; DbCommand.Cancel is what reaches it (D86). A command
        // that has already finished may refuse, which is not a failure of the query.
        await using var cancelling = ct.Register(
            static state =>
            {
                try
                {
                    ((DbCommand)state!).Cancel();
                }
                catch (Exception)
                {
                    // The command is past cancelling. Nothing to do and nothing to report.
                }
            },
            command);

        var checkedSchema = false;
        await foreach (var exported in command
            .ExecuteArrowBatchesAsync(ct)
            .WithCancellation(ct)
            .ConfigureAwait(false))
        {
            if (!checkedSchema)
            {
                checkedSchema = true;
                if (!Matches(exported.Schema, request.OutputSchema))
                {
                    // Decided once, on the first batch, and then the query runs again on the path
                    // that converts. Re-running is the price of not knowing DuckDB's output types
                    // until it has produced some: the copier can ask the result before its first
                    // chunk, the export cannot.
                    exported.Dispose();
                    await foreach (var batch in Fallback(request, ct).ConfigureAwait(false))
                    {
                        yield return batch;
                    }

                    yield break;
                }
            }

            // The declared schema over the exported arrays: names, nullability and types are the
            // plan's, and not one buffer moves. The wrapper owns the imported memory from here, so
            // the consumer's Dispose releases it and the export's own wrapper is left alone.
            yield return new RecordBatch(
                request.OutputSchema, exported.Arrays, exported.Length);
        }
    }

    /// <summary>
    /// Whether every exported column is the declared one. Nullability is not compared: DuckDB
    /// exports every field as nullable whatever the catalog says, and a column the plan calls NOT
    /// NULL that arrives with no nulls in it is the same column.
    /// </summary>
    private static bool Matches(ArrowSchema exported, ArrowSchema declared)
    {
        if (exported.FieldsList.Count != declared.FieldsList.Count)
        {
            return false;
        }

        for (var i = 0; i < declared.FieldsList.Count; i++)
        {
            if (!Matches(exported.FieldsList[i].DataType, declared.FieldsList[i].DataType))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One type against one type, and a list's element against a list's element.
    /// <c>ArrowTypeMapping.AreEquivalent</c> compares two <c>ListType</c>s by name alone, which
    /// answers the question the engine asks it and not the one here: a LIST of INT32 exported where
    /// the plan declared a LIST of STRING would pass it and then be read as the wrong thing.
    /// </summary>
    private static bool Matches(IArrowType exported, IArrowType declared) =>
        (exported, declared) switch
        {
            (ListType a, ListType b) => Matches(a.ValueDataType, b.ValueDataType),
            _ => ArrowTypeMapping.AreEquivalent(exported, declared),
        };
}
