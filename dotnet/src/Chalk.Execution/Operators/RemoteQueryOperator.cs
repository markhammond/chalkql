using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// The other leaf: a whole subtree the source ran (D83, D84). Structurally a twin of
/// <see cref="ScanOperator"/> — the first batch's schema is checked, the Arrow graph lives for
/// exactly one batch and is this operator's to release — with three differences that matter.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>
/// The request carries both flavours of the pushed subtree, so the adapter reads whichever its
/// query language names and neither is lost (D84).
/// </item>
/// <item>
/// Parameters are bound <em>per execution</em>: the query text has positional placeholders and the
/// values come from the execution's bound parameters, so a prepared query is planned once and run
/// with different values.
/// </item>
/// <item>
/// Every failure is attributed (§4): a provider exception becomes
/// <see cref="SourceExecutionException"/> naming the source and the query, a timeout becomes
/// <see cref="SourceTimeoutException"/>, and nothing partial is ever surfaced — the enumerator
/// throws instead of yielding.
/// </item>
/// </list>
/// <para>
/// It counts the round trip itself (<c>RemoteCalls</c>), because that is the number a host reads to
/// see whether pushdown is working and the adapter should not have to remember to.
/// </para>
/// </remarks>
internal sealed class RemoteQueryOperator : OperatorBase
{
    private readonly ISourceRuntime _source;
    private readonly RemoteQuery _query;
    private readonly IReadOnlyList<int> _parameterIndexes;
    private readonly TimeSpan? _timeout;
    private readonly ColumnarBatch _output;
    private readonly ColumnView[]?[] _children;
    private readonly object?[] _bound;
    private readonly ChalkType[] _parameterTypes;

    /// <summary>
    /// This query's text with its literals as pseudonyms, when the engine's redaction is on (D262),
    /// and null otherwise. Computed at prepare and never here: a failure path must not make a call.
    /// </summary>
    private readonly string? _redactedQueryText;

    public RemoteQueryOperator(
        OperatorContext context,
        string path,
        ISourceRuntime source,
        RemoteQuery query,
        IReadOnlyList<int> parameterIndexes,
        TimeSpan? timeout,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        string? redactedQueryText = null)
        : base(context, schema, columnTypes, path)
    {
        _source = source;
        _query = query;
        _redactedQueryText = redactedQueryText;
        _parameterIndexes = parameterIndexes;
        _timeout = timeout;
        _output = NewOutput();
        _children = ArrowBatchViews.ChildHolders(columnTypes);
        _bound = new object?[parameterIndexes.Count];
        _parameterTypes = new ChalkType[query.Parameters.Count];
        for (var i = 0; i < _parameterTypes.Length; i++)
        {
            _parameterTypes[i] = ChalkType.FromProto(query.Parameters[i].Type);
        }
    }

    protected override ValueTask DisposeCoreAsync() => default;

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var request = new RemoteQueryRequest
        {
            QueryText = _query.QueryText,
            PushedPlan = _query.PushedPlan,
            Parameters = BindParameters(),
            ParameterTypes = _parameterTypes,
            OutputSchema = Schema,
            BatchSize = Context.Settings.BatchSize,
            Timeout = _timeout,
            Dialect = _query.Dialect,
        };

        Context.Stats.AddRemoteCalls(1);

        // One place in the queue for this stream, released when it ends however it ends (D107).
        using var lease = await Context.RemoteGate
            .EnterAsync(_source.SourceId, ct)
            .ConfigureAwait(false);

        var first = true;
        var rows = 0L;
        await foreach (var batch in RemoteFetch.RunAsync(
            _source,
            request,
            Context,
            Context.Settings.RemotePrefetchDepth,
            ct,
            _redactedQueryText))
        {
            try
            {
                if (first)
                {
                    first = false;
                    if (!ArrowTypeMapping.AreEquivalent(Schema, batch.Schema))
                    {
                        throw new SourceContractException(
                            _source.SourceId,
                            Subject,
                            $"the first batch has schema {ArrowTypeMapping.DescribeArrow(batch.Schema)}, "
                            + $"not the requested {ArrowTypeMapping.DescribeArrow(Schema)}.");
                    }
                }

                Context.Stats.AddRowsFetched(batch.Length);
                rows += batch.Length;
                ArrowBatchViews.Fill(_output, batch, ColumnTypes, _children);
                yield return _output;
            }
            finally
            {
                batch.Dispose();
            }
        }

        // D98, D101: no cross-source snapshot, so a host is told which moment each source was read
        // at rather than being left to assume they agree.
        Context.Stats.RecordFetch(_source.SourceId, rows, Context.Settings.TimeProvider.GetUtcNow());
    }

    /// <summary>
    /// This execution's values for the query's placeholders, in placeholder order. Reused between
    /// executions: the array is plan-shaped, and the values it holds belong to one run.
    /// </summary>
    private IReadOnlyList<object?> BindParameters()
    {
        for (var i = 0; i < _parameterIndexes.Count; i++)
        {
            var index = _parameterIndexes[i];
            _bound[i] = index >= 0 && index < Context.Parameters.Count
                ? Context.Parameters[index].ToHost()
                : null;
        }

        return _bound;
    }

    /// <summary>
    /// What a failure names: the query text where there is one, else the pushed plan's table — and
    /// the query text with its literals redacted when the engine asked for that (D262).
    /// </summary>
    private string Subject =>
        _redactedQueryText
        ?? (_query.QueryText.Length > 0
            ? _query.QueryText
            : $"the pushed plan for '{_source.SourceId}'");
}
