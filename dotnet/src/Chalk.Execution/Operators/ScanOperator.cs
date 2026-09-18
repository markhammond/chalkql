using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// The leaf: a full scan through the source (§6.3). The first batch's schema is checked against what
/// was asked for, because a source that quietly returns a different shape is a wrong-answer bug
/// rather than a crash.
/// </summary>
/// <remarks>
/// Two paths (§1, D61). An in-box source implements <see cref="IColumnarBatchSource"/> and fills this
/// operator's slot directly, so the common case creates no Arrow object at all. Everything else keeps
/// the public Arrow contract of <see cref="ISourceRuntime"/> and its batches are wrapped as views
/// without a copy, one Arrow graph per source batch — the source's cost, and its choice.
/// </remarks>
internal sealed class ScanOperator : OperatorBase
{
    private readonly ISourceRuntime _source;
    private readonly ScanRequest _request;
    private readonly ColumnarBatch _output;
    private readonly ColumnView[]?[] _children;

    public ScanOperator(
        OperatorContext context,
        string path,
        ISourceRuntime source,
        ScanRequest request,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes)
        : base(context, schema, columnTypes, path)
    {
        _source = source;
        _request = request;
        _output = NewOutput();
        _children = ArrowBatchViews.ChildHolders(columnTypes);
    }

    protected override ValueTask DisposeCoreAsync() => default;

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var scanContext = ScanContextFor(Context);

        if (_source is IColumnarBatchSource columnar
            && columnar.ColumnarScan(_request, scanContext) is { } direct)
        {
            await using (direct.ConfigureAwait(false))
            {
                while (true)
                {
                    // The in-box path answers synchronously and never awaits, so this is the only
                    // place a cancelled query can stop before a blocking operator above has drained
                    // the whole collection.
                    ct.ThrowIfCancellationRequested();
                    if (direct.TryNext(_output))
                    {
                        yield return _output;
                        continue;
                    }

                    if (!await direct.WaitAsync(_output, ct).ConfigureAwait(false))
                    {
                        yield break;
                    }

                    yield return _output;
                }
            }
        }

        var first = true;
        await foreach (var batch in _source.ScanAsync(_request, scanContext, ct).WithCancellation(ct))
        {
            // The Arrow graph lives for exactly one batch (§1), and it is this operator's to release
            // — including when the consumer throws or abandons the scan part-way.
            try
            {
                if (first)
                {
                    first = false;
                    if (!ArrowTypeMapping.AreEquivalent(Schema, batch.Schema))
                    {
                        throw new SourceContractException(
                            _source.SourceId,
                            _request.Table,
                            $"the first batch has schema {ArrowTypeMapping.DescribeArrow(batch.Schema)}, "
                            + $"not the requested {ArrowTypeMapping.DescribeArrow(Schema)}.");
                    }
                }

                ArrowBatchViews.Fill(_output, batch, ColumnTypes, _children);
                yield return _output;
            }
            finally
            {
                batch.Dispose();
            }
        }
    }
}

/// <summary>Wrapping a source's Arrow batch as this operator's views, with no copy (§1).</summary>
internal static class ArrowBatchViews
{
    /// <summary>One reusable element-view holder per LIST column, so wrapping one allocates nothing.</summary>
    public static ColumnView[]?[] ChildHolders(IReadOnlyList<ChalkType> types)
    {
        var holders = new ColumnView[]?[types.Count];
        for (var i = 0; i < types.Count; i++)
        {
            holders[i] = types[i].Kind == Ir.TypeKind.List ? new ColumnView[1] : null;
        }

        return holders;
    }

    /// <summary>Points <paramref name="slot"/> at <paramref name="batch"/>'s columns.</summary>
    public static void Fill(
        ColumnarBatch slot,
        RecordBatch batch,
        IReadOnlyList<ChalkType> types,
        ColumnView[]?[] children)
    {
        slot.Begin(batch.Length);
        for (var c = 0; c < types.Count; c++)
        {
            slot.Set(c, ColumnarBoundary.Wrap(batch.Column(c), types[c], children[c]));
        }
    }
}
