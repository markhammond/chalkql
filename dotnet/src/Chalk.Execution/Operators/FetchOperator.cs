using Chalk.Catalog;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// Streaming <c>OFFSET</c> / <c>FETCH</c> (§6.3). Batches wholly inside the window are forwarded
/// untouched; only the two at the edges are narrowed, and narrowing a view is arithmetic on an
/// offset rather than a copy.
/// </summary>
internal sealed class FetchOperator : OperatorBase
{
    private readonly IBatchOperator _input;
    private readonly long _offset;
    private readonly long? _count;
    private readonly ColumnarBatch _output;
    private int[] _window = [];

    public FetchOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        IBatchOperator input,
        long offset,
        long? count)
        : base(context, schema, columnTypes, path)
    {
        _input = input;
        _offset = offset;
        _count = count;
        _output = NewOutput();
    }

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_count == 0)
        {
            yield break;
        }

        var arena = Context.Arena;
        _window = arena.Rent<int>(Context.Settings.BatchSize);
        try
        {
            var skipped = 0L;
            var emitted = 0L;

            await foreach (var batch in _input.ExecuteAsync(ct))
            {
                var length = batch.Count;
                var skip = (int)Math.Min(length, Math.Max(0, _offset - skipped));
                skipped += skip;
                var available = length - skip;
                var take = _count is null ? available : (int)Math.Min(available, _count.Value - emitted);

                if (take <= 0)
                {
                    if (_count is not null && emitted >= _count.Value)
                    {
                        yield break;
                    }

                    continue;
                }

                emitted += take;
                if (skip == 0 && take == length)
                {
                    _output.Adopt(batch);
                }
                else if (batch.HasSelection)
                {
                    Grow(take);
                    batch.Selection.Slice(skip, take).CopyTo(_window);
                    _output.Begin(batch.RowCount);
                    _output.SetAll(batch.Columns);
                    _output.Select(_window, take);
                }
                else
                {
                    _output.Begin(take);
                    for (var c = 0; c < batch.ColumnCount; c++)
                    {
                        _output.Set(c, batch.Column(c).Slice(skip, take));
                    }
                }

                yield return _output;

                if (_count is not null && emitted >= _count.Value)
                {
                    yield break;
                }
            }
        }
        finally
        {
            arena.Return(_window);
            _window = [];
        }
    }

    private void Grow(int rows)
    {
        if (_window.Length >= rows)
        {
            return;
        }

        var grown = Context.Arena.Rent<int>(rows);
        Context.Arena.Return(_window);
        _window = grown;
    }
}
