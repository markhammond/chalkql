using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// A <c>VirtualTable</c>: literal rows materialised into batches (§6.3). Zero rows is legal and yields
/// no batches at all — that is how the planner spells <c>WHERE 1=0</c>.
/// </summary>
internal sealed class ValuesOperator : OperatorBase
{
    private readonly ScalarValue[][] _rows;
    private readonly ColumnCopier[] _copiers;
    private readonly ColumnarBatch _output;

    public ValuesOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        ScalarValue[][] rows)
        : base(context, schema, columnTypes, path)
    {
        _rows = rows;
        _copiers = [.. columnTypes.Select(t => new ColumnCopier(t))];
        _output = NewOutput();
    }

    protected override ValueTask DisposeCoreAsync() => default;

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var batchSize = Context.Settings.BatchSize;
        for (var start = 0; start < _rows.Length; start += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(batchSize, _rows.Length - start);
            _output.Begin(count);
            for (var c = 0; c < _copiers.Length; c++)
            {
                _copiers[c].Begin();
                for (var r = 0; r < count; r++)
                {
                    _copiers[c].AppendConstant(_rows[start + r][c], 1);
                }

                _output.Set(c, _copiers[c].FinishView());
            }

            yield return _output;
        }
    }
}
