using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// The other leaf: a range lookup through the source's <c>IndexLookupAsync</c> (§5, D37). The
/// residual a lookup cannot enforce is an ordinary <see cref="FilterOperator"/> above it — nothing
/// is fused in M2 — so all this operator does is bind the bounds and hand the ranges over.
/// </summary>
/// <remarks>
/// A bound is a literal or a parameter, so binding happens once per execution rather than per row.
/// A parameter bound to NULL collapses its range to nothing, because <c>x = NULL</c> matches nothing
/// in SQL; when every range collapses the lookup produces no rows without touching the source.
/// </remarks>
internal sealed class IndexLookupOperator : OperatorBase
{
    private readonly ISourceRuntime _source;
    private readonly string _table;
    private readonly string _index;
    private readonly IReadOnlyList<RangePlan> _ranges;
    private readonly IReadOnlyList<int> _projection;
    private readonly int _keyColumns;
    private readonly long? _rowGoal;
    private readonly ColumnarBatch _output;
    private readonly ColumnView[]?[] _children;

    public IndexLookupOperator(
        OperatorContext context,
        string path,
        ISourceRuntime source,
        string table,
        string index,
        IReadOnlyList<RangePlan> ranges,
        IReadOnlyList<int> projection,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        long? rowGoal = null)
        : base(context, schema, columnTypes, path)
    {
        _source = source;
        _table = table;
        _index = index;
        _ranges = ranges;
        _projection = projection;
        _rowGoal = rowGoal;
        _keyColumns = ranges.Count == 0 ? 0 : ranges.Max(r => Math.Max(r.Lower.Count, r.Upper.Count));
        _output = NewOutput();
        _children = ArrowBatchViews.ChildHolders(columnTypes);
    }

    protected override ValueTask DisposeCoreAsync() => default;

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var bound = new List<IndexKeyRange>(_ranges.Count);
        foreach (var range in _ranges)
        {
            var value = range.Bind(Context.Parameters);
            if (value is not null)
            {
                bound.Add(value);
            }
        }

        if (bound.Count == 0)
        {
            // Every range collapsed — a NULL-bound parameter, or a planner that emitted none.
            yield break;
        }

        var request = new IndexLookupRequest
        {
            Table = _table,
            Index = _index,
            Ranges = bound,
            Projection = _projection,
            OutputSchema = Schema,
            BatchSize = Context.Settings.BatchSize,
            // `IndexLookup.row_goal` is a hint the source may size its first batch to (D276).
            RowGoal = _rowGoal,
        };

        var scanContext = ScanContextFor(Context);

        if (_source is IColumnarBatchSource columnar
            && columnar.ColumnarLookup(request, scanContext) is { } direct)
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

    /// <summary>How many leading key columns any of this lookup's ranges constrains.</summary>
    public int KeyColumns => _keyColumns;

    /// <summary>
    /// One range, with its bounds already reduced to "a literal" or "parameter n". Binding is then a
    /// lookup rather than an evaluation, which is what keeps a lookup off the expression path.
    /// </summary>
    internal sealed class RangePlan
    {
        public required IReadOnlyList<BoundPlan> Lower { get; init; }

        public required bool LowerInclusive { get; init; }

        public required IReadOnlyList<BoundPlan> Upper { get; init; }

        public required bool UpperInclusive { get; init; }

        /// <summary>The bound range, or null when a NULL parameter emptied it.</summary>
        public IndexKeyRange? Bind(IReadOnlyList<ScalarValue> parameters)
        {
            var lower = new object?[Lower.Count];
            for (var i = 0; i < lower.Length; i++)
            {
                if (!Lower[i].TryBind(parameters, out lower[i]))
                {
                    return null;
                }
            }

            var upper = new object?[Upper.Count];
            for (var i = 0; i < upper.Length; i++)
            {
                if (!Upper[i].TryBind(parameters, out upper[i]))
                {
                    return null;
                }
            }

            return new IndexKeyRange
            {
                Lower = lower,
                LowerInclusive = LowerInclusive,
                Upper = upper,
                UpperInclusive = UpperInclusive,
            };
        }
    }

    /// <summary>One bound: a constant the planner wrote down, or a parameter to read at execution.</summary>
    internal sealed class BoundPlan
    {
        private readonly object? _constant;
        private readonly int _parameter;

        private BoundPlan(object? constant, int parameter)
        {
            _constant = constant;
            _parameter = parameter;
        }

        public static BoundPlan Constant(object? value) => new(value, -1);

        public static BoundPlan Parameter(int index) => new(null, index);

        /// <summary>False when the bound is NULL, which makes the whole range empty.</summary>
        public bool TryBind(IReadOnlyList<ScalarValue> parameters, out object? value)
        {
            if (_parameter < 0)
            {
                value = _constant;
                return value is not null;
            }

            if (_parameter >= parameters.Count)
            {
                throw new InvalidPlanException(
                    "I-IR-5",
                    "IndexLookup.ranges",
                    $"a range bound reads parameter {_parameter} but only {parameters.Count} were bound");
            }

            var scalar = parameters[_parameter];
            value = scalar.ToClr();
            return value is not null;
        }
    }
}
