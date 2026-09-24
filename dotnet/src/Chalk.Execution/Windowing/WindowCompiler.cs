using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Execution.Windowing;

/// <summary>
/// Binds one IR <c>Window</c> to the operator (§4). Everything that can be refused is refused here,
/// at plan compilation, so an unsupported frame or call surfaces from <c>PrepareAsync</c> rather than
/// mid-stream (§6.8).
/// </summary>
internal static class WindowCompiler
{
    /// <summary>The data-independent half: partitioning, ordering and the frame.</summary>
    public static WindowSpec Spec(Window window, ChalkType[] inputTypes, string path)
    {
        if (window.Frame.Mode == FrameMode.Groups)
        {
            throw new UnsupportedFeatureException(
                "a GROUPS frame",
                "Calcite 1.42's parser does not accept GROUPS, so no Chalk planner emits one "
                + "(docs/design/13-window-functions.md §9, ADR 0017 V16).");
        }

        // D297: the partition and order keys are bound here, for the buffered and the streaming
        // operator alike, so the executor's own refusal of a key it cannot compare is here too.
        foreach (var key in window.PartitionKeys)
        {
            ColumnKinds.RequireComparable(inputTypes[(int)key], "a window partition key");
        }

        var order = new WindowOrderKey[window.Order.Count];
        for (var i = 0; i < order.Length; i++)
        {
            var field = window.Order[i];
            ColumnKinds.RequireComparable(inputTypes[(int)field.Expr.FieldRef.Index], "a window order key");
            order[i] = new WindowOrderKey(
                (int)field.Expr.FieldRef.Index,
                field.Direction is SortDirection.DescNullsFirst or SortDirection.DescNullsLast,
                field.Direction is SortDirection.AscNullsFirst or SortDirection.DescNullsFirst);
        }

        return new WindowSpec
        {
            PartitionKeys = [.. window.PartitionKeys.Select(k => (int)k)],
            Order = order,
            Mode = window.Frame.Mode,
            Lower = Bound(window.Frame.Lower, $"{path}.frame.lower"),
            Upper = Bound(window.Frame.Upper, $"{path}.frame.upper"),
            Exclusion = window.Frame.Exclusion,
            InputTypes = inputTypes,
        };
    }

    /// <summary>
    /// One evaluator per call. Called once at compilation to surface everything unsupported, and
    /// again for each execution, because an evaluator owns mutable state.
    /// </summary>
    public static WindowCallEvaluator[] Evaluators(
        Window window,
        ChalkType[] inputTypes,
        string path,
        CatalogContext? catalog = null,
        HostFunctionSet? functions = null)
    {
        var evaluators = new WindowCallEvaluator[window.Calls.Count];
        for (var i = 0; i < evaluators.Length; i++)
        {
            evaluators[i] = Evaluator(
                window.Calls[i],
                inputTypes,
                $"{path}.calls[{i}]",
                catalog,
                functions ?? HostFunctionSet.Empty);
        }

        return evaluators;
    }

    /// <summary>
    /// The streaming calls for this window, or <see langword="null"/> when it stays on the buffered
    /// path (D258.4, <c>13-window-functions.md</c> §4.1). The decision is the frame's shape and then
    /// every call over it: one call that cannot stream buffers the whole window, because the operator
    /// is one or the other.
    /// </summary>
    /// <remarks>
    /// Nothing here refuses a query. Everything this returns null for is something the buffered
    /// operator answers, so an unsupported frame or call still raises from where it always did —
    /// which is why the range-key check asks <see cref="WindowFrames.HasRangeLane"/> rather than
    /// calling <c>RangeLane</c> and catching what it throws.
    /// </remarks>
    public static StreamingWindowPlan? Streaming(
        Window window, WindowSpec spec, ChalkType[] inputTypes, string path)
    {
        if (spec.Exclusion != FrameExclusion.NoOthers
            || !spec.EndsAtOrBeforeCurrentRow
            || spec.Lower.Kind is FrameBoundKind.Following or FrameBoundKind.UnboundedFollowing
            || spec.Mode == FrameMode.Groups)
        {
            return null;
        }

        if (spec.Mode == FrameMode.Range)
        {
            // With no ordering every row is every other row's peer, so a RANGE frame is the whole
            // partition and there is nothing for streaming to save.
            if (spec.Order.Length == 0)
            {
                return null;
            }

            if (spec.HasOffsets
                && (spec.Order.Length != 1
                    || !WindowFrames.HasRangeLane(spec.InputTypes[spec.RangeKey.Column])))
            {
                return null;
            }
        }

        var calls = new StreamingWindowCall[window.Calls.Count];
        for (var i = 0; i < calls.Length; i++)
        {
            var call = StreamingCall(window.Calls[i], spec, inputTypes, $"{path}.calls[{i}]");

            // A call that reads the frame's own rows needs them held, and an UNBOUNDED PRECEDING
            // frame's own rows are the partition's: that is buffering under another name.
            if (call is null || (call.LookBack.Frame && !spec.StartsWithinBoundedDistance))
            {
                return null;
            }

            calls[i] = call;
        }

        return new StreamingWindowPlan { Calls = calls, Arguments = Arguments(calls) };
    }

    /// <summary>
    /// One frame state per distinct argument across the sliding aggregates over it (D258.4). An
    /// <c>AVG(x)</c> reaches the executor as <c>COUNT(x)</c> and <c>SUM(x)</c>, and both read the same
    /// column through the same two frame edges.
    /// </summary>
    private static StreamingWindowArgument[] Arguments(StreamingWindowCall[] calls) =>
    [
        .. calls
            .OfType<StreamingWindowAggregate>()
            .GroupBy(a => a.ValueColumn)
            .OrderBy(g => g.Key)
            .Select(g => new StreamingWindowArgument(g.Key, [.. g])),
    ];

    private static StreamingWindowCall? StreamingCall(
        WindowCall call, WindowSpec spec, ChalkType[] inputTypes, string path)
    {
        // D80: a client-bodied aggregate has no removal and no incremental form.
        if (call.UserFunction.Length > 0)
        {
            return null;
        }

        var result = ChalkType.FromProto(call.Type);
        return call.FunctionCase == WindowCall.FunctionOneofCase.WindowFunction
            ? StreamingWindowFunction(call, result, spec, inputTypes, path)
            : StreamingAggregate(call, result, spec, inputTypes, path);
    }

    private static StreamingWindowCall? StreamingWindowFunction(
        WindowCall call, ChalkType result, WindowSpec spec, ChalkType[] inputTypes, string path)
    {
        switch (call.WindowFunction)
        {
            case WindowFunctionId.RowNumber:
            case WindowFunctionId.Rank:
            case WindowFunctionId.DenseRank:
                return new StreamingWindowRanking(result, call.WindowFunction);

            // NTILE, PERCENT_RANK and CUME_DIST all divide by the partition's total row count, which
            // is not known until the partition has ended.
            case WindowFunctionId.Ntile:
            case WindowFunctionId.PercentRank:
            case WindowFunctionId.CumeDist:
                return null;

            case WindowFunctionId.Lag:
            {
                // IGNORE NULLS counts non-NULL rows, so the row it lands on is any distance back;
                // and an offset that is still a parameter might yet be negative, which is a LEAD.
                if (call.IgnoreNulls)
                {
                    return null;
                }

                long offset = 1;
                var nullOffset = false;
                if (call.Args.Count > 1)
                {
                    if (Constant(call, 1, path).Literal is not { } literal)
                    {
                        return null;
                    }

                    nullOffset = literal.IsNull;
                    offset = nullOffset ? 0 : literal.Integer;
                    if (offset < 0)
                    {
                        return null;
                    }
                }

                return new StreamingWindowLag(
                    result,
                    Field(call, 0, inputTypes, path),
                    offset,
                    nullOffset,
                    call.Args.Count > 2 ? Constant(call, 2, path) : null);
            }

            case WindowFunctionId.FirstValue:
                return FrameValue(call, result, spec, inputTypes, path, last: false, call.IgnoreNulls);

            case WindowFunctionId.LastValue:
                return FrameValue(call, result, spec, inputTypes, path, last: true, call.IgnoreNulls);

            // LEAD reads forwards; NTH_VALUE counts from the frame's first row under IGNORE NULLS,
            // which the buffered pass answers in one scan and streaming would not improve.
            default:
                return null;
        }
    }

    private static StreamingWindowCall? StreamingAggregate(
        WindowCall call, ChalkType result, WindowSpec spec, ChalkType[] inputTypes, string path)
    {
        // D56's counted multiset is state over the frame's distinct values, not a sliding sum.
        if (call.Distinct
            && call.Aggregate is AggregateFunctionId.Count or AggregateFunctionId.Sum
                or AggregateFunctionId.Sum0 or AggregateFunctionId.Avg)
        {
            return null;
        }

        switch (call.Aggregate)
        {
            case AggregateFunctionId.Count:
                return new StreamingWindowAggregate(
                    result,
                    AggregateFunctionId.Count,
                    call.Args.Count == 0 ? -1 : Field(call, 0, inputTypes, path),
                    Accumulating(spec, floating: false));

            case AggregateFunctionId.Sum:
            case AggregateFunctionId.Sum0:
            case AggregateFunctionId.Avg:
            {
                // A floating-point sum is rebuilt from the frame every 4 096 rows so the drift stays
                // bounded, and the rebuild reads every row of the frame.
                var floating = ColumnKinds.Of(result) is ColumnKind.Float or ColumnKind.Double;
                return new StreamingWindowAggregate(
                    result,
                    call.Aggregate,
                    Field(call, 0, inputTypes, path),
                    Accumulating(spec, floating));
            }

            case AggregateFunctionId.Min:
            case AggregateFunctionId.BoolAnd:
                return new StreamingWindowExtremum(
                    result, Field(call, 0, inputTypes, path), max: false);

            case AggregateFunctionId.Max:
            case AggregateFunctionId.BoolOr:
                return new StreamingWindowExtremum(
                    result, Field(call, 0, inputTypes, path), max: true);

            case AggregateFunctionId.AnyValue:
                return FrameValue(call, result, spec, inputTypes, path, last: false, ignoreNulls: true);

            // MODE, LISTAGG and ARRAY_AGG are D57's holistic family: they hold the frame's values.
            default:
                return null;
        }
    }

    /// <summary>
    /// What a sliding accumulator reads behind the cursor: nothing at all when the frame only ever
    /// grows to the current row and the arithmetic is exact, and the frame's own rows otherwise.
    /// </summary>
    private static WindowLookBack Accumulating(WindowSpec spec, bool floating) =>
        spec.RunningToCurrentRow && !floating ? WindowLookBack.None : WindowLookBack.WholeFrame;

    private static StreamingWindowCall? FrameValue(
        WindowCall call,
        ChalkType result,
        WindowSpec spec,
        ChalkType[] inputTypes,
        string path,
        bool last,
        bool ignoreNulls)
    {
        var column = Field(call, 0, inputTypes, path);

        // LAST_VALUE respecting NULLs under a CURRENT ROW upper bound is the current row, or the last
        // of its peer group: at or ahead of the cursor either way, so nothing is held for it.
        if (last && !ignoreNulls && spec.Upper.Kind == FrameBoundKind.CurrentRow)
        {
            return new StreamingWindowFrameValue(
                result, inputTypes[column], column, last: true, ignoreNulls: false,
                pinned: false, WindowLookBack.None);
        }

        if (spec.StartsWithinBoundedDistance)
        {
            return new StreamingWindowFrameValue(
                result, inputTypes[column], column, last, ignoreNulls,
                pinned: false, WindowLookBack.WholeFrame);
        }

        // An UNBOUNDED PRECEDING frame that grows to the current row has one first row for the whole
        // partition, so it is found once and pinned — a row copied out of the hold, which is what
        // lets the hold move on beneath it.
        if (!last && spec.RunningToCurrentRow)
        {
            return new StreamingWindowFrameValue(
                result, inputTypes[column], column, last: false, ignoreNulls,
                pinned: true, WindowLookBack.None);
        }

        return null;
    }

    private static WindowBound Bound(FrameBound bound, string path) => new()
    {
        Kind = bound.Kind,
        Offset = bound.Offset is null ? null : WindowConstant.Of(bound.Offset, path),
    };

    private static WindowCallEvaluator Evaluator(
        WindowCall call,
        ChalkType[] inputTypes,
        string path,
        CatalogContext? catalog,
        HostFunctionSet functions)
    {
        var result = ChalkType.FromProto(call.Type);
        if (call.UserFunction.Length > 0)
        {
            // A client-bodied aggregate over a frame (D80). Its declaration must say WINDOW, which
            // is what the planner has already told Calcite through `allowsFraming`.
            var (host, _) = Expressions.UserFunctionBinding.AggregateFor(
                catalog, functions, call.UserFunction);
            return host.Accept(
                new WindowUserAggregateFactory(result, Field(call, 0, inputTypes, path)));
        }

        return call.FunctionCase == WindowCall.FunctionOneofCase.WindowFunction
            ? WindowFunction(call, result, inputTypes, path)
            : Aggregate(call, result, inputTypes, path);
    }

    private static WindowCallEvaluator WindowFunction(
        WindowCall call, ChalkType result, ChalkType[] inputTypes, string path)
    {
        switch (call.WindowFunction)
        {
            case WindowFunctionId.RowNumber:
            case WindowFunctionId.Rank:
            case WindowFunctionId.DenseRank:
            case WindowFunctionId.PercentRank:
            case WindowFunctionId.CumeDist:
                return new WindowRankingEvaluator(result, call.WindowFunction, buckets: null);

            case WindowFunctionId.Ntile:
                return new WindowRankingEvaluator(
                    result, call.WindowFunction, Constant(call, 0, path));

            case WindowFunctionId.Lag:
            case WindowFunctionId.Lead:
                return new WindowLagLeadEvaluator(
                    result,
                    Field(call, 0, inputTypes, path),
                    call.WindowFunction == WindowFunctionId.Lead,
                    call.Args.Count > 1 ? Constant(call, 1, path) : null,
                    call.Args.Count > 2 ? Constant(call, 2, path) : null,
                    call.IgnoreNulls);

            case WindowFunctionId.FirstValue:
                return new WindowFrameValueEvaluator(
                    result,
                    Field(call, 0, inputTypes, path),
                    WindowFrameValueEvaluator.Position.First,
                    nth: null,
                    call.IgnoreNulls);

            case WindowFunctionId.LastValue:
                return new WindowFrameValueEvaluator(
                    result,
                    Field(call, 0, inputTypes, path),
                    WindowFrameValueEvaluator.Position.Last,
                    nth: null,
                    call.IgnoreNulls);

            case WindowFunctionId.NthValue:
                return new WindowFrameValueEvaluator(
                    result,
                    Field(call, 0, inputTypes, path),
                    WindowFrameValueEvaluator.Position.Nth,
                    Constant(call, 1, path),
                    call.IgnoreNulls);

            default:
                throw new UnsupportedFeatureException(
                    call.WindowFunction.ToString().ToUpperInvariant(),
                    "It is outside the window functions of docs/design/13-window-functions.md §1.");
        }
    }

    private static WindowCallEvaluator Aggregate(
        WindowCall call, ChalkType result, ChalkType[] inputTypes, string path)
    {
        // D56: DISTINCT through a counted multiset. MIN and MAX are unaffected by duplicates, so
        // they take the ordinary path whether or not the query wrote DISTINCT.
        if (call.Distinct
            && call.Aggregate is AggregateFunctionId.Count or AggregateFunctionId.Sum
                or AggregateFunctionId.Sum0 or AggregateFunctionId.Avg)
        {
            if (call.Args.Count == 0)
            {
                throw new InvalidPlanException(
                    "I-IR-11", path, "COUNT(DISTINCT) needs an argument");
            }

            // D297: the counted multiset hashes and compares the argument.
            var distinct = Field(call, 0, inputTypes, path);
            ColumnKinds.RequireComparable(inputTypes[distinct], "a DISTINCT window aggregate's argument");
            return new WindowDistinctEvaluator(result, call.Aggregate, distinct);
        }

        switch (call.Aggregate)
        {
            case AggregateFunctionId.Count:
                return new WindowAggregateEvaluator(
                    result,
                    AggregateFunctionId.Count,
                    call.Args.Count == 0 ? -1 : Field(call, 0, inputTypes, path),
                    result);

            case AggregateFunctionId.Sum:
            case AggregateFunctionId.Sum0:
            case AggregateFunctionId.Avg:
            {
                var column = Field(call, 0, inputTypes, path);
                return new WindowAggregateEvaluator(
                    result, call.Aggregate, column, inputTypes[column]);
            }

            // EVERY and SOME are Calcite's MIN and MAX over a boolean, and the IR carries both
            // spellings; the extremum operator does not care which name arrived.
            case AggregateFunctionId.Min:
            case AggregateFunctionId.BoolAnd:
                return new WindowExtremumEvaluator(result, Field(call, 0, inputTypes, path), max: false);

            case AggregateFunctionId.Max:
            case AggregateFunctionId.BoolOr:
                return new WindowExtremumEvaluator(result, Field(call, 0, inputTypes, path), max: true);

            case AggregateFunctionId.AnyValue:
                return new WindowAnyValueEvaluator(result, Field(call, 0, inputTypes, path));

            // D57's holistic family, in its window form. The percentiles are absent because Calcite
            // 1.42 cannot express one — WITHIN GROUP is not an aggregate call as far as OVER is
            // concerned, so no plan carries them (V22, ADR 0018).
            case AggregateFunctionId.Mode:
            {
                var column = Field(call, 0, inputTypes, path);
                return new WindowModeEvaluator(result, column, inputTypes[column]);
            }

            case AggregateFunctionId.Listagg:
            case AggregateFunctionId.StringAgg:
                return new WindowCollectingEvaluator(
                    result,
                    AggregateFunctionId.Listagg,
                    Field(call, 0, inputTypes, path),
                    Separator(call, path));

            case AggregateFunctionId.ArrayAgg:
                return new WindowCollectingEvaluator(
                    result, AggregateFunctionId.ArrayAgg, Field(call, 0, inputTypes, path), string.Empty);

            default:
                throw new UnsupportedFeatureException(
                    $"{call.Aggregate.ToString().ToUpperInvariant()} over a window",
                    "The window operator implements COUNT, SUM, SUM0, AVG, MIN, MAX, BOOL_AND, "
                    + "BOOL_OR, ANY_VALUE, MODE, LISTAGG and ARRAY_AGG, with or without DISTINCT "
                    + "(docs/design/13-window-functions.md §1, docs/design/14-windows-ii.md §2 "
                    + "and §3).");
        }
    }

    /// <summary>
    /// <c>LISTAGG</c>'s separator, which the IR carries as the call's second argument and which is a
    /// constant — Calcite validates that it is a literal.
    /// </summary>
    private static string Separator(WindowCall call, string path)
    {
        if (call.Args.Count < 2)
        {
            return ",";
        }

        var argument = call.Args[1];
        if (argument.KindCase != Expr.KindOneofCase.Literal
            || argument.Literal.ValueCase != Literal.ValueOneofCase.StringValue)
        {
            throw new UnsupportedFeatureException(
                $"LISTAGG over a window with a {argument.KindCase} separator",
                "The separator is a string literal (docs/design/14-windows-ii.md §3).");
        }

        return argument.Literal.StringValue;
    }

    /// <summary>
    /// A call's value argument. Calcite always pushes an expression below the window into a project,
    /// so the argument that reaches here is a column reference; anything else would need the operator
    /// to evaluate expressions over its buffered input, which §4 does not ask for.
    /// </summary>
    private static int Field(WindowCall call, int index, ChalkType[] inputTypes, string path)
    {
        if (index >= call.Args.Count)
        {
            throw new InvalidPlanException(
                "I-IR-11", path, $"the call has {call.Args.Count} arguments; it needs at least {index + 1}");
        }

        var argument = call.Args[index];
        if (argument.KindCase != Expr.KindOneofCase.FieldRef)
        {
            throw new UnsupportedFeatureException(
                $"a window call whose value argument is a {argument.KindCase}",
                "The operator reads its value from a buffered column; the planner pushes any "
                + "expression into a Project below the window.");
        }

        var column = (int)argument.FieldRef.Index;
        if (column < 0 || column >= inputTypes.Length)
        {
            throw new InvalidPlanException(
                "I-IR-3", path, $"argument {index} reads column {column} of a {inputTypes.Length}-column row");
        }

        return column;
    }

    private static WindowConstant Constant(WindowCall call, int index, string path)
    {
        if (index >= call.Args.Count)
        {
            throw new InvalidPlanException(
                "I-IR-11", path, $"the call has {call.Args.Count} arguments; it needs at least {index + 1}");
        }

        return WindowConstant.Of(call.Args[index], $"{path}.args[{index}]");
    }
}
