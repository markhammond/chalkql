using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Joins;

/// <summary>
/// An ASOF join that seeks an index instead of reading the whole right table (D338, design 63 §4).
/// Maps to the IR's <c>AsOfLookupJoin</c>.
/// </summary>
/// <remarks>
/// <para>
/// The left input is read once, into <see cref="Materialisation"/>. Then an ordinary
/// <see cref="AsOfJoinOperator"/> runs over two inputs made from it: its right input is an
/// <see cref="AsOfSeekOperator"/>, which seeks the index once for each distinct key and time of the
/// buffered left rows and returns the first row of each range, and its left input replays the
/// buffer. Each seek finds the closest row for its own key and time, so among the rows found the
/// plain join's matching picks the same row the whole table would have given it; the matching, the
/// NULL rules and LEFT's padding are the plain join's own.
/// </para>
/// <para>
/// <b>Ties.</b> A seek returns one row, the first its read reaches: among rows with the same keys
/// and the closest time, the last in the index's order for <c>&gt;=</c> and <c>&gt;</c>, which read
/// backwards, and the first for <c>&lt;=</c> and <c>&lt;</c>. The plain join takes the first in the
/// table's order. The two agree for an index whose key ends with a unique column.
/// </para>
/// </remarks>
internal sealed class AsOfLookupJoinOperator : OperatorBase
{
    private readonly IBatchOperator _left;
    private readonly Materialisation _materialisation;
    private readonly IBatchOperator _join;
    private readonly ColumnarBatch _output;

    public AsOfLookupJoinOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        IBatchOperator left,
        Materialisation materialisation,
        IBatchOperator join)
        : base(context, schema, columnTypes, path)
    {
        _left = left;
        _materialisation = materialisation;
        _join = join;
        _output = NewOutput();
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        await _left.DisposeAsync().ConfigureAwait(false);
        await _join.DisposeAsync().ConfigureAwait(false);
    }

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var rows = _materialisation.Rows;
        try
        {
            rows.Begin();
            await foreach (var batch in _left.ExecuteAsync(ct).ConfigureAwait(false))
            {
                for (var row = 0; row < batch.Count; row++)
                {
                    rows.AppendRow(batch, row);
                }
            }

            rows.Finish();

            await foreach (var batch in _join.ExecuteAsync(ct).ConfigureAwait(false))
            {
                _output.Adopt(batch);
                yield return _output;
            }
        }
        finally
        {
            rows.Release();
        }
    }
}

/// <summary>
/// The right input of an <see cref="AsOfLookupJoinOperator"/>: for each distinct key and time of the
/// buffered left rows, one range of the index, and the first row of it (D338).
/// </summary>
/// <remarks>
/// A range holds each of the index's leading key columns to a left row's value or to a value fixed
/// for the execution, and bounds the next, the time column, by the left row's time. It is read
/// backwards for <c>&gt;=</c> and <c>&gt;</c> and forwards for <c>&lt;=</c> and <c>&lt;</c>, and the
/// source is asked for one row of each. A left row whose key or time is NULL seeks nothing, as it
/// matches nothing; a fixed value bound to NULL empties every range.
/// </remarks>
internal sealed class AsOfSeekOperator : OperatorBase
{
    private readonly ISourceRuntime _source;
    private readonly string _table;
    private readonly string _index;
    private readonly IReadOnlyList<int> _projection;
    private readonly Materialisation _left;
    private readonly IReadOnlyList<ChalkType> _leftTypes;
    private readonly IReadOnlyList<SeekKey> _keys;
    private readonly int _leftTime;
    private readonly bool _backwards;
    private readonly bool _inclusive;
    private readonly ColumnarBatch _output;
    private readonly ColumnView[]?[] _children;
    private readonly IReadOnlyDictionary<Chalk.Ir.Expr, Expressions.IVectorExpr> _computed;
    private readonly Expressions.EvalContext? _eval;

    /// <summary>One of the index's leading key columns: a left field, or a value for the execution.</summary>
    internal sealed record SeekKey(int LeftField, IndexLookupOperator.BoundPlan? Fixed);

    public AsOfSeekOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        ISourceRuntime source,
        string table,
        string index,
        IReadOnlyList<int> projection,
        Materialisation left,
        IReadOnlyList<ChalkType> leftTypes,
        IReadOnlyList<SeekKey> keys,
        int leftTime,
        Chalk.Ir.AsOfMatch match,
        IReadOnlyDictionary<Chalk.Ir.Expr, Expressions.IVectorExpr>? computed = null)
        : base(context, schema, columnTypes, path)
    {
        _source = source;
        _table = table;
        _index = index;
        _projection = projection;
        _left = left;
        _leftTypes = leftTypes;
        _keys = keys;
        _leftTime = leftTime;

        // The comparison reads `left time <op> right time`: GE and GT keep the right rows at or before
        // the left row's time, so the closest is the last of them.
        _backwards = match is Chalk.Ir.AsOfMatch.Ge or Chalk.Ir.AsOfMatch.Gt;
        _inclusive = match is Chalk.Ir.AsOfMatch.Ge or Chalk.Ir.AsOfMatch.Le;
        _output = NewOutput();
        _children = ArrowBatchViews.ChildHolders(columnTypes);
        _computed = computed ?? new Dictionary<Chalk.Ir.Expr, Expressions.IVectorExpr>();
        _eval = _computed.Count > 0 ? context.NewEvalContext() : null;
    }

    protected override ValueTask DisposeCoreAsync() => default;

    private ScalarValue Compute(Chalk.Ir.Expr bound)
    {
        var expr = _computed[bound];
        var answer = _eval!.EvaluateOnce(expr);
        return answer.IsScalar ? answer.Scalar : ScalarValue.FromLane(answer.View, 0, expr.Type);
    }

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // The fixed values, once for the execution. One bound to NULL matches no row.
        var held = _keys.Count;
        var fixedValues = new object?[held];
        for (var k = 0; k < held; k++)
        {
            if (_keys[k].Fixed is { } bound && !bound.TryBind(Context.Parameters, Compute, out fixedValues[k]))
            {
                yield break;
            }
        }

        var rows = _left.Rows;

        // An index the source seeks straight from the left rows' columns: no bound made for any row.
        int[]? sought = null;
        IColumnarScan? typed = null;
        try
        {
            if (_source is IColumnarSeekSource seeker)
            {
                (typed, sought) = SeekTyped(seeker, rows, fixedValues, held);
            }

            if (typed is not null)
            {
                await using (typed.ConfigureAwait(false))
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (typed.TryNext(_output))
                        {
                            yield return _output;
                            continue;
                        }

                        if (!await typed.WaitAsync(_output, ct).ConfigureAwait(false))
                        {
                            yield break;
                        }

                        yield return _output;
                    }
                }
            }
        }
        finally
        {
            if (sought is not null)
            {
                Context.Arena.Return(sought);
            }
        }

        var seen = new HashSet<object?[]>(SeekComparer.Instance);
        var ranges = new List<IndexKeyRange>(rows.Count);

        // Each key and the time, boxed once for a run of rows that repeat it, as a leg's rows repeat
        // the time they are priced at.
        var lastRow = new int[held + 1];
        var lastBoxed = new object?[held + 1];
        Array.Fill(lastRow, -1);
        object? Boxed(int slot, int field, int row)
        {
            var column = rows.Column(field);
            var type = _leftTypes[field];
            if (lastRow[slot] >= 0 && SameLane(column, lastRow[slot], row, type))
            {
                return lastBoxed[slot];
            }

            lastRow[slot] = row;
            return lastBoxed[slot] = ScalarValue.ClrAt(column, row, type);
        }

        for (var row = 0; row < rows.Count; row++)
        {
            var values = new object?[held + 1];
            var missing = false;
            for (var k = 0; k < held && !missing; k++)
            {
                var key = _keys[k];
                values[k] = key.Fixed is null ? Boxed(k, key.LeftField, row) : fixedValues[k];
                missing = values[k] is null;
            }

            values[held] = Boxed(held, _leftTime, row);
            if (missing || values[held] is null || !seen.Add(values))
            {
                continue;
            }

            var keys = values[..held];
            ranges.Add(_backwards
                ? new IndexKeyRange { Lower = keys, Upper = values, UpperInclusive = _inclusive }
                : new IndexKeyRange { Lower = values, LowerInclusive = _inclusive, Upper = keys });
        }

        if (ranges.Count == 0)
        {
            yield break;
        }

        var request = new IndexLookupRequest
        {
            Table = _table,
            Index = _index,
            Ranges = ranges,
            Projection = _projection,
            OutputSchema = Schema,
            BatchSize = Context.Settings.BatchSize,
            Reverse = _backwards,
            RowsPerRange = 1,
        };

        var scanContext = ScanContextFor(Context);
        if (_source is IColumnarBatchSource columnar
            && columnar.ColumnarLookup(request, scanContext) is { } direct)
        {
            await using (direct.ConfigureAwait(false))
            {
                while (true)
                {
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
        await foreach (var batch in _source.IndexLookupAsync(request, scanContext, ct).WithCancellation(ct))
        {
            try
            {
                if (first)
                {
                    first = false;
                    if (!ArrowTypeMapping.AreEquivalent(Schema, batch.Schema))
                    {
                        throw new SourceContractException(
                            ChalkErrorCodes.SourceContract,
                            _source.SourceId,
                            _table,
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

    /// <summary>
    /// The seeks asked of a source that reads them from the left rows' columns, and the rows they are
    /// for, rented from the arena until the scan ends; no scan when the source cannot seek this index so.
    /// A row whose key or time is NULL seeks nothing, as it matches nothing, and a row that repeats the
    /// one before it is not sought again.
    /// </summary>
    private (IColumnarScan? Scan, int[]? Rows) SeekTyped(
        IColumnarSeekSource seeker, JoinRows rows, object?[] fixedValues, int held)
    {
        var bounds = new SeekBound[held + 1];
        for (var k = 0; k < held; k++)
        {
            var key = _keys[k];
            bounds[k] = key.Fixed is null
                ? SeekBound.Of(rows.Column(key.LeftField), _leftTypes[key.LeftField])
                : SeekBound.Fixed(fixedValues[k]!);
        }

        bounds[held] = SeekBound.Of(rows.Column(_leftTime), _leftTypes[_leftTime]);
        var selected = Context.Arena.Rent<int>(Math.Max(1, rows.Count));
        var count = 0;
        for (var row = 0; row < rows.Count; row++)
        {
            if (!Bounded(bounds, row) || (count > 0 && Repeats(bounds, selected[count - 1], row)))
            {
                continue;
            }

            selected[count++] = row;
        }

        var scan = seeker.ColumnarSeek(
            new ColumnarSeekRequest
            {
                Table = _table,
                Index = _index,
                Projection = _projection,
                OutputSchema = Schema,
                BatchSize = Context.Settings.BatchSize,
                Bounds = bounds,
                Rows = selected,
                RowCount = count,
                Backwards = _backwards,
                Inclusive = _inclusive,
            },
            ScanContextFor(Context));
        if (scan is null)
        {
            Context.Arena.Return(selected);
            return (null, null);
        }

        return (scan, selected);
    }

    /// <summary>Whether every bound a left row reads holds a value: a NULL key or time matches nothing.</summary>
    private static bool Bounded(SeekBound[] bounds, int row)
    {
        foreach (var bound in bounds)
        {
            if (!bound.IsFixed && !bound.Column.IsValid(row))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a left row seeks what the row before it did.</summary>
    private static bool Repeats(SeekBound[] bounds, int previous, int row)
    {
        foreach (var bound in bounds)
        {
            if (!bound.IsFixed && !SameLane(bound.Column, previous, row, bound.Type))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether two rows of a column hold the same lane: both NULL, or the same bytes.</summary>
    private static bool SameLane(in ColumnView column, int a, int b, ChalkType type)
    {
        var valid = column.IsValid(a);
        if (valid != column.IsValid(b))
        {
            return false;
        }

        if (!valid)
        {
            return true;
        }

        var kind = ColumnKinds.Of(type);
        if (ColumnKinds.IsVariableLength(kind))
        {
            return column.VarValue(a).SequenceEqual(column.VarValue(b));
        }

        if (kind == ColumnKind.Boolean)
        {
            return column.BoolAt(a) == column.BoolAt(b);
        }

        var width = ColumnKinds.Width(kind);
        var lanes = column.RawLanes(width);
        return lanes.Slice(a * width, width).SequenceEqual(lanes.Slice(b * width, width));
    }

    /// <summary>A seek's key and time, compared value by value.</summary>
    private sealed class SeekComparer : IEqualityComparer<object?[]>
    {
        public static readonly SeekComparer Instance = new();

        public bool Equals(object?[]? x, object?[]? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null || x.Length != y.Length)
            {
                return false;
            }

            for (var i = 0; i < x.Length; i++)
            {
                if (!object.Equals(x[i], y[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(object?[] values)
        {
            var hash = default(HashCode);
            foreach (var value in values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
