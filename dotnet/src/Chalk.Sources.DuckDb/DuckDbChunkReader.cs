using Apache.Arrow;
using Chalk.Sources.Ado;
using DuckDB.NET.Native;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Sources.DuckDb;

/// <summary>
/// DuckDB's data chunks accumulated into Chalk batches (D148,
/// <c>docs/design/24-zero-gc.md</c> §6).
/// </summary>
/// <remarks>
/// A chunk holds up to <c>duckdb_vector_size()</c> rows — 2 048 — and a batch holds as many as the
/// execution asked for, so the two are not the same shape: a chunk is appended in slices, a batch is
/// emitted when it is full, and the last one is whatever is left when the stream ends.
/// </remarks>
internal sealed class DuckDbChunkReader : IDisposable
{
    private readonly ArrowSchema _schema;
    private readonly DuckDbColumn[] _columns;
    private readonly int _capacity;
    private bool _disposed;

    private DuckDbChunkReader(ArrowSchema schema, DuckDbColumn[] columns, int capacity)
    {
        _schema = schema;
        _columns = columns;
        _capacity = capacity;
    }

    /// <summary>
    /// A reader for this result, or null when a column's logical type is outside §6's table — which
    /// is decided here, before the first chunk, so the fallback costs nothing but a rewind.
    /// </summary>
    public static DuckDbChunkReader? TryCreate(RemoteFetchRequest request, ref DuckDBResult result)
    {
        var fields = request.OutputSchema.FieldsList;
        var columns = (long)NativeMethods.Query.DuckDBColumnCount(ref result);
        if (columns != fields.Count)
        {
            // The query answered a different shape from the one the plan promised. The provider's
            // own reader raises that as the contract error it is, with the names in the message.
            return null;
        }

        var built = new List<DuckDbColumn>(fields.Count);
        for (var i = 0; i < fields.Count; i++)
        {
            var logical = NativeMethods.Query.DuckDBColumnLogicalType(ref result, i);
            DuckDbColumn? column;
            using (logical)
            {
                column = DuckDbColumn.TryCreate(
                    request.Arena,
                    request.BatchSize,
                    request.ColumnTypes[i],
                    fields[i].DataType,
                    fields[i].Name,
                    request.SourceId,
                    request.Subject,
                    logical);
            }

            if (column is null)
            {
                foreach (var created in built)
                {
                    created.Release();
                }

                return null;
            }

            built.Add(column);
        }

        return new DuckDbChunkReader(request.OutputSchema, [.. built], request.BatchSize);
    }

    /// <summary>Every batch this result produces, in order.</summary>
    public async IAsyncEnumerable<RecordBatch> ReadAsync(
        DuckDBResult result,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var rows = 0;
        foreach (var column in _columns)
        {
            column.BeginBatch();
        }

        while (true)
        {
            // Between chunks, because the interrupt only reaches a fetch already inside the native
            // call and a cancelled token must not start another.
            ct.ThrowIfCancellationRequested();

            var chunk = NativeMethods.StreamingResult.DuckDBStreamFetchChunk(result);
            if (chunk.IsInvalid)
            {
                chunk.Dispose();
                break;
            }

            using (chunk)
            {
                var chunkRows = (int)NativeMethods.DataChunks.DuckDBDataChunkGetSize(chunk);
                var from = 0;
                while (from < chunkRows)
                {
                    var take = Math.Min(chunkRows - from, _capacity - rows);
                    for (var c = 0; c < _columns.Length; c++)
                    {
                        var vector = NativeMethods.DataChunks.DuckDBDataChunkGetVector(chunk, c);
                        _columns[c].Append(vector, from, take, rows);
                    }

                    from += take;
                    rows += take;
                    if (rows == _capacity)
                    {
                        yield return Build(rows);
                        rows = 0;
                        foreach (var column in _columns)
                        {
                            column.BeginBatch();
                        }
                    }
                }
            }

            // Nothing here awaits, and saying so is better than a fake await: DuckDB is in-process
            // and its fetch is a synchronous call. The operator above already prefetches on a task
            // (D107), which is where the concurrency lives.
            await Task.CompletedTask.ConfigureAwait(false);
        }

        if (rows > 0)
        {
            yield return Build(rows);
        }
    }

    private RecordBatch Build(int rows)
    {
        var arrays = new IArrowArray[_columns.Length];
        for (var c = 0; c < _columns.Length; c++)
        {
            arrays[c] = _columns[c].Build(rows);
        }

        return new RecordBatch(_schema, arrays, rows);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var column in _columns)
        {
            column.Release();
        }
    }
}
