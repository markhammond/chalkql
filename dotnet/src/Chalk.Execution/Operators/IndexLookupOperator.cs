using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// The other leaf: a range lookup through the source's <c>IndexLookupAsync</c> (§5, D37). The
/// residual a lookup cannot enforce is an ordinary <see cref="FilterOperator"/> above it, so all this
/// operator does is bind the bounds and hand the ranges over.
/// </summary>
/// <remarks>
/// <para>
/// A bound is a literal or a parameter, so binding happens once per execution rather than per row.
/// A parameter bound to NULL collapses its range to nothing, because <c>x = NULL</c> matches nothing
/// in SQL; when every range collapses the lookup produces no rows without touching the source.
/// </para>
/// <para>
/// A lookup's own residual (D314) — the <c>LIKE</c> a prefix range was made from, when its pattern is
/// a parameter — is applied by a <see cref="FilterOperator"/> the compiler puts over this one. Whether
/// it needs to run is known here, at bind: when every prefix range's value turned out to be a bare
/// prefix, the ranges are the whole answer, and <see cref="ResidualGate"/> says so.
/// </para>
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
    private readonly bool _reverse;
    private readonly ResidualGate? _gate;
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
        long? rowGoal = null,
        bool reverse = false,
        ResidualGate? gate = null)
        : base(context, schema, columnTypes, path)
    {
        _source = source;
        _table = table;
        _index = index;
        _ranges = ranges;
        _projection = projection;
        _rowGoal = rowGoal;
        _reverse = reverse;
        _gate = gate;
        _keyColumns = ranges.Count == 0 ? 0 : ranges.Max(r => Math.Max(r.Lower.Count, r.Upper.Count));
        _output = NewOutput();
        _children = ArrowBatchViews.ChildHolders(columnTypes);
    }

    protected override ValueTask DisposeCoreAsync() => default;

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var bound = new List<IndexKeyRange>(_ranges.Count);
        var decided = true;
        foreach (var range in _ranges)
        {
            var value = range.Bind(Context.Parameters, out var whole);
            if (value is not null)
            {
                bound.Add(value);
                decided &= whole;
            }
        }

        if (!decided && _gate is null)
        {
            // A range that covers more than its pattern matches, and nothing to decide the rest
            // with. The planner never writes one — a literal prefix it uses is bare, and a parameter
            // brings its residual — so this is a plan from somewhere else, refused rather than
            // answered with the extra rows.
            throw new UnsupportedFeatureException(
                ChalkErrorCodes.UnsupportedOperator,
                $"a LIKE lookup on index '{_index}' that its ranges do not decide",
                $"The lookup on '{_table}' binds a pattern that is not a bare prefix, and the plan "
                + "gives it no residual to check the rest of the pattern with.");
        }

        _gate?.Bound(decided);

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
            // `IndexLookup.reverse` is not a hint: the plan claims the reverse of the index's key
            // order and has no sort above it to make good on that (D283).
            Reverse = _reverse,
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

    /// <summary>How many leading key columns any of this lookup's ranges constrains.</summary>
    public int KeyColumns => _keyColumns;

    /// <summary>
    /// What a lookup tells its own residual at bind (D314): whether the ranges already decide every
    /// row, so the residual can pass each batch through untouched. One per execution, shared by the
    /// lookup and the residual's condition, and written before the first batch is produced.
    /// </summary>
    internal sealed class ResidualGate
    {
        private readonly bool _skippable;

        /// <param name="skippable">
        /// Whether the residual is the prefix ranges' own LIKE and nothing else, which the compiler
        /// checks against the plan. Only then does "the ranges decide every row" make it redundant.
        /// </param>
        public ResidualGate(bool skippable) => _skippable = skippable;

        /// <summary>True once bound when the residual has nothing left to decide.</summary>
        public bool Redundant { get; private set; }

        public void Bound(bool decided) => Redundant = _skippable && decided;
    }

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

        /// <summary>The last lower bound is a <c>LIKE</c> pattern rather than a value (D282).</summary>
        public bool Prefix { get; init; }

        /// <summary>
        /// The index answers prefixes itself, so it is sent the prefix rather than the half-open
        /// range the prefix stands for.
        /// </summary>
        public bool PrefixIndex { get; init; }

        /// <summary>The prefix pattern's escape character, or null when its LIKE had none (D313).</summary>
        public string? Escape { get; init; }

        public string IndexName { get; init; } = string.Empty;

        public string Table { get; init; } = string.Empty;

        public string SourceId { get; init; } = string.Empty;

        /// <summary>The bound range, or null when a NULL parameter emptied it.</summary>
        public IndexKeyRange? Bind(IReadOnlyList<ScalarValue> parameters) => Bind(parameters, out _);

        /// <summary>
        /// The bound range, or null when a NULL parameter emptied it; <paramref name="decided"/> says
        /// whether the range is exactly the rows its predicate selects, which every range but a prefix
        /// one bound to a pattern that is not a bare prefix is (D314).
        /// </summary>
        public IndexKeyRange? Bind(IReadOnlyList<ScalarValue> parameters, out bool decided)
        {
            decided = true;
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

            if (Prefix)
            {
                return BindPrefix(lower, out decided);
            }

            return new IndexKeyRange
            {
                Lower = lower,
                LowerInclusive = LowerInclusive,
                Upper = upper,
                UpperInclusive = UpperInclusive,
            };
        }

        /// <summary>
        /// What a <c>LIKE</c> range becomes, resolved by the index's kind (D282) through the
        /// pattern's escape (D313).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The range is over the pattern's literal start <c>p</c>. A prefix index is sent <c>p</c>.
        /// Every other kind is sent the plain half-open range <c>[p, next(p))</c>, where
        /// <c>next(p)</c> is the smallest text above every text that starts with <c>p</c> — so an
        /// ordinary ordered string index serves a <c>LIKE</c> with no change at all.
        /// </para>
        /// <para>
        /// A pattern that is not a bare prefix — a parameter bound to <c>'%BLK'</c>, say, or to
        /// <c>'KB-%-BLK'</c> — gets the range over its literal start, which holds every row it
        /// matches and some it does not, and <paramref name="decided"/> is false: the lookup's
        /// residual decides the rest (D314). A malformed pattern is refused, as the client refuses
        /// it before the execution starts (D312).
        /// </para>
        /// </remarks>
        private IndexKeyRange BindPrefix(object?[] lower, out bool decided)
        {
            var pattern = IndexPrefix.AsText(lower[^1]) ?? string.Empty;
            LikePattern.Validate(pattern, Escape);
            var prefix = LikePattern.LiteralStart(pattern, Escape, out decided);

            if (PrefixIndex)
            {
                return new IndexKeyRange
                {
                    Lower = lower,
                    LowerInclusive = true,
                    Upper = lower[..^1],
                    UpperInclusive = false,
                    Prefix = prefix,
                };
            }

            // An empty prefix matches every text, so the range is open above — and still bounds the
            // column, which is what keeps a NULL out of it. The upper side is the equality columns
            // alone, inclusive: exclusive, a key equal to them would compare equal to the bound and
            // fall outside it, and the lookup would answer nothing (F153).
            if (prefix.Length == 0)
            {
                return new IndexKeyRange
                {
                    Lower = lower[..^1].Append((object?)string.Empty).ToArray(),
                    LowerInclusive = true,
                    Upper = lower[..^1],
                    UpperInclusive = true,
                };
            }

            if (!IndexPrefix.TryNext(prefix, out var next))
            {
                // No text sorts above every text that starts with this one — it ends at the largest
                // code point there is — so the range can only be open above, which covers rows the
                // pattern does not match. The residual decides them; without one, the lookup refuses.
                decided = false;
                return new IndexKeyRange
                {
                    Lower = lower[..^1].Append((object?)prefix).ToArray(),
                    LowerInclusive = true,
                    Upper = lower[..^1],
                    UpperInclusive = true,
                };
            }

            var start = lower.ToArray();
            start[^1] = prefix;

            var end = lower.ToArray();
            end[^1] = next;

            return new IndexKeyRange
            {
                Lower = start,
                LowerInclusive = true,
                Upper = end,
                UpperInclusive = false,
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

/// <summary>
/// A lookup's residual as a <see cref="FilterOperator"/> condition (D314): the residual itself, or —
/// when the lookup has found at bind that its ranges already decide every row — true, which the
/// filter forwards a batch on without looking at a row.
/// </summary>
internal sealed class ResidualGateExpr : Expressions.IVectorExpr
{
    private static readonly ScalarValue True = new() { Type = ChalkType.Bool(), Integer = 1 };

    private readonly Expressions.IVectorExpr _residual;
    private readonly IndexLookupOperator.ResidualGate _gate;

    public ResidualGateExpr(Expressions.IVectorExpr residual, IndexLookupOperator.ResidualGate gate)
    {
        _residual = residual;
        _gate = gate;
    }

    public ChalkType Type => _residual.Type;

    public Vector Evaluate(Expressions.EvalContext context) =>
        _gate.Redundant ? Vector.FromScalar(True, context.Length) : _residual.Evaluate(context);
}
