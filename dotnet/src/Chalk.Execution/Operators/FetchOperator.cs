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
    private readonly RowBound _offset;
    private readonly RowBound? _count;
    private readonly ColumnarBatch _output;
    private int[] _window = [];

    public FetchOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        IBatchOperator input,
        RowBound offset,
        RowBound? count)
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
        // The bounds this execution runs under, read before any row moves: a parameterised one is
        // read from its slot here, and a negative or NULL value is refused by name (D285).
        var offset = _offset.Resolve(Context.Parameters, "OFFSET");
        var count = _count?.Resolve(Context.Parameters, "LIMIT");
        if (count == 0)
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
                var skip = (int)Math.Min(length, Math.Max(0, offset - skipped));
                skipped += skip;
                var available = length - skip;
                var take = count is null ? available : (int)Math.Min(available, count.Value - emitted);

                if (take <= 0)
                {
                    if (count is not null && emitted >= count.Value)
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

                if (count is not null && emitted >= count.Value)
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
