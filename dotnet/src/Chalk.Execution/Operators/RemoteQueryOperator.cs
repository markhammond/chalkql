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
/// with different values. A pushed <c>LIMIT</c> or <c>OFFSET</c> that is a parameter is the one
/// placeholder the provider never binds: its value is read from its slot when the execution starts,
/// refused by name if it is not a count, and written into the text as a whole number (D288). One
/// string per execution, and nothing per row.
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
    private readonly TimeSpan? _timeout;
    private readonly ColumnarBatch _output;
    private readonly ColumnView[]?[] _children;
    private readonly ChalkType[] _parameterTypes;

    /// <summary>The slot each placeholder the <em>provider</em> binds reads, in placeholder order.</summary>
    private readonly int[] _parameterIndexes;

    /// <summary>This execution's value for each of those, reused between executions.</summary>
    private readonly object?[] _bound;

    /// <summary>The placeholders this executor writes a number into, ascending (D288).</summary>
    private readonly int[] _renderedPositions;

    /// <summary>The bound behind each of those, read at execution start.</summary>
    private readonly RowBound[] _renderedBounds;

    /// <summary>Which clause each one is, for a refusal that names it: <c>LIMIT</c> or <c>OFFSET</c>.</summary>
    private readonly string[] _renderedClauses;

    /// <summary>This execution's number for each, reused: plan-shaped array, per-run values.</summary>
    private readonly long[] _rendered;

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
        IReadOnlyList<RenderedBound> renderedBounds,
        TimeSpan? timeout,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        string? redactedQueryText = null)
        : base(context, schema, columnTypes, path)
    {
        _source = source;
        _query = query;
        _redactedQueryText = redactedQueryText;
        _timeout = timeout;
        _output = NewOutput();
        _children = ArrowBatchViews.ChildHolders(columnTypes);

        _renderedPositions = new int[renderedBounds.Count];
        _renderedBounds = new RowBound[renderedBounds.Count];
        _renderedClauses = new string[renderedBounds.Count];
        _rendered = new long[renderedBounds.Count];
        for (var i = 0; i < renderedBounds.Count; i++)
        {
            _renderedPositions[i] = renderedBounds[i].Position;
            _renderedBounds[i] = renderedBounds[i].Bound;
            _renderedClauses[i] = renderedBounds[i].Clause;
        }

        // What is left after the bounds are taken out is what the provider binds, still in
        // placeholder order — which is what makes dropping one from the middle of the list safe.
        var bound = parameterIndexes.Count - _renderedPositions.Length;
        _parameterIndexes = new int[bound];
        _parameterTypes = new ChalkType[bound];
        _bound = new object?[bound];
        var next = 0;
        var rendered = 0;
        for (var i = 0; i < parameterIndexes.Count; i++)
        {
            if (rendered < _renderedPositions.Length && _renderedPositions[rendered] == i)
            {
                rendered++;
                continue;
            }

            _parameterIndexes[next] = parameterIndexes[i];
            _parameterTypes[next] = ChalkType.FromProto(query.Parameters[i].Type);
            next++;
        }
    }

    /// <summary>One pushed bound the executor renders: where it is, what it reads, what to call it.</summary>
    internal readonly record struct RenderedBound(int Position, RowBound Bound, string Clause);

    protected override ValueTask DisposeCoreAsync() => default;

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // The bounds this execution runs under, read before any row moves and before the round trip
        // is even made: a negative, a NULL and a fraction are refused by the names D285 gave them,
        // and the number is written into the text in the placeholder's place (D288).
        for (var i = 0; i < _renderedBounds.Length; i++)
        {
            _rendered[i] = _renderedBounds[i].Resolve(Context.Parameters, _renderedClauses[i]);
        }

        var request = new RemoteQueryRequest
        {
            QueryText = RenderedBounds.Substitute(_query.QueryText, _renderedPositions, _rendered),
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
    /// This execution's values for the placeholders the provider binds, in placeholder order —
    /// which is every placeholder the text still has a <c>?</c> for. Reused between executions: the
    /// array is plan-shaped, and the values it holds belong to one run.
    /// </summary>
    private IReadOnlyList<object?> BindParameters()
    {
        for (var i = 0; i < _parameterIndexes.Length; i++)
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
