using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Reference;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;
using CatalogContext = Chalk.Catalog.CatalogContext;
using SourceCapabilities = Chalk.Catalog.SourceCapabilities;
using SourceKind = Chalk.Ir.SourceKind;

namespace Chalk.Execution.Tests.Harness;

/// <summary>
/// A source that answers a pushed query from a table it holds, and records everything the federation
/// operators are supposed to do to it (M5, §7): how many calls, with which keys, how many at once.
/// </summary>
/// <remarks>
/// <para>
/// It also lies on request: a delay before the first batch, a fault on the n-th call, a stream that
/// never ends. Those are the three shapes cancellation, failure attribution and backpressure need,
/// and every one of them is a line of configuration here rather than a race in a real database.
/// </para>
/// <para>
/// The "query" it is handed is the key set, not SQL: a fake source has no dialect, so it filters its
/// table by the bound parameters on the key column. What that tests is the operator's side of the
/// boundary — which keys it sent, how many at a time, and what it did with what came back.
/// </para>
/// </remarks>
internal sealed class FakeRemoteSource : ISourceRuntime
{
    private readonly TestTable _table;
    private readonly int _keyColumn;
    private int _inFlight;

    public FakeRemoteSource(string sourceId, TestTable table, int keyColumn = 0)
    {
        SourceId = sourceId;
        _table = table;
        _keyColumn = keyColumn;
    }

    public string SourceId { get; }

    /// <summary>Every call's bound key values, in call order.</summary>
    public List<object?[]> Calls { get; } = [];

    /// <summary>The query text of every call, in call order — the text as it was actually sent.</summary>
    public List<string> Queries { get; } = [];

    /// <summary>The most calls that were ever in flight at once.</summary>
    public int PeakConcurrency { get; private set; }

    /// <summary>Rows this source handed back, over every call.</summary>
    public long RowsReturned { get; private set; }

    /// <summary>How long to wait before the first batch of every call.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Fail the call with this ordinal (1-based). Zero never fails.</summary>
    public int FailOnCall { get; set; }

    /// <summary>Never end: the shape a cancellation test needs.</summary>
    public bool Hangs { get; set; }

    /// <summary>Set when a call observed its cancellation token.</summary>
    public bool ObservedCancellation { get; private set; }

    /// <summary>
    /// Completes when a hanging call is actually waiting on its token. Without it a cancellation
    /// test is a race it usually wins: cancelling before any fetch task has reached the source means
    /// the token is already cancelled when the prefetch task's first <c>MoveNextAsync</c> runs, the
    /// enumerator faults exactly as it should, and no call was ever in flight to observe anything.
    /// </summary>
    public Task Hanging => _hanging.Task;

    private readonly TaskCompletionSource _hanging =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SchemaDescriptor DescribeSchema() => new()
    {
        SourceId = SourceId,
        Name = SourceId,
        Kind = SourceKind.Remote,
        Dialect = "fake",
        Capabilities = new SourceCapabilities
        {
            QueryLanguage = QueryLanguage.Sql,
            MaxInList = 1000,
            SupportsProject = true,
        },
        Tables =
        [
            new TableDescriptor
            {
                Name = _table.Name,
                Columns = [.. _table.Columns.Select(c => new ColumnDescriptor { Name = c.Name, Type = c.Type })],
                RowCount = _table.Rows.Count,
            },
        ],
    };

    public IAsyncEnumerable<RecordBatch> ScanAsync(
        ScanRequest request, ScanContext context, CancellationToken ct) =>
        Rows(_table.Rows, request.OutputSchema, [.. _table.Columns.Select(c => c.Type)],
            request.BatchSize, context, ct);

    public IAsyncEnumerable<RecordBatch> ExecuteQueryAsync(
        RemoteQueryRequest request, ScanContext context, CancellationToken ct)
    {
        var keys = request.Parameters.ToArray();
        int ordinal;
        lock (Calls)
        {
            Calls.Add(keys);
            Queries.Add(request.QueryText);
            ordinal = Calls.Count;
            _inFlight++;
            PeakConcurrency = Math.Max(PeakConcurrency, _inFlight);
        }

        // No bound parameters means no key set: the query is a plain read of the table, which is
        // what a LOCAL strategy and a fan-out branch both send.
        var matched = keys.Length == 0
            ? [.. _table.Rows]
            : _table.Rows.Where(row => keys.Any(key => Equals(row[_keyColumn], key))).ToArray();

        return Answer(
            ordinal,
            matched,
            request.OutputSchema,
            [.. _table.Columns.Select(c => c.Type)],
            request.BatchSize,
            context,
            ct);
    }

    private async IAsyncEnumerable<RecordBatch> Answer(
        int ordinal,
        IReadOnlyList<object?[]> rows,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        int batchSize,
        ScanContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            if (Delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(Delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ObservedCancellation = true;
                    throw;
                }
            }

            if (ordinal == FailOnCall)
            {
                throw new SourceExecutionException(
                    SourceId, "the fake query", new InvalidOperationException("the source is down"));
            }

            if (Hangs)
            {
                try
                {
                    _hanging.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ObservedCancellation = true;
                    throw;
                }
            }

            foreach (var batch in ReferenceValues.ToBatches(
                rows, schema, types, batchSize, context.Allocator))
            {
                if (ct.IsCancellationRequested)
                {
                    ObservedCancellation = true;
                    batch.Dispose();
                    ct.ThrowIfCancellationRequested();
                }

                RowsReturned += batch.Length;
                context.Stats.AddRowsScanned(batch.Length);
                yield return batch;
            }
        }
        finally
        {
            lock (Calls)
            {
                _inFlight--;
            }
        }
    }

    private static async IAsyncEnumerable<RecordBatch> Rows(
        IReadOnlyList<object?[]> rows,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        int batchSize,
        ScanContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var batch in ReferenceValues.ToBatches(rows, schema, types, batchSize, context.Allocator))
        {
            // As in ScanAsync above: a batch this iterator built but has not yielded is still its
            // own, and its buffers are the arena's.
            if (ct.IsCancellationRequested)
            {
                batch.Dispose();
                ct.ThrowIfCancellationRequested();
            }

            context.Stats.AddRowsScanned(batch.Length);
            yield return batch;
        }
    }

    /// <summary>A catalog holding a local source and any number of these.</summary>
    public static CatalogContext Catalog(TestSource local, params FakeRemoteSource[] remotes) => new()
    {
        ContextId = "test",
        Epoch = 1,
        Schemas = [local.DescribeSchema(), .. remotes.Select(r => r.DescribeSchema())],
    };
}
