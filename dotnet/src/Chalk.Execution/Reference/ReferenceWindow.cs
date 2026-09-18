using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Reference;

/// <summary>
/// The window functions, read literally (D51, <c>13-window-functions.md</c> §4). For every row it
/// materialises the frame by scanning the partition and evaluates each call over the rows it holds —
/// no sliding accumulator, no deque, no two pointers, no peer array. That is O(n × frame) and it is
/// the entire point: the engine's fast paths have nothing in common with it, so a disagreement is
/// evidence.
/// </summary>
/// <remarks>
/// It runs on <c>bars_small</c>, where quadratic is instant (§6); the full tables are checked against
/// DuckDB instead. Rows are grouped into partitions through a dictionary rather than by assuming the
/// input arrives with each partition contiguous, which is the one thing the operator does assume.
/// </remarks>
internal static class ReferenceWindow
{
    public static List<object?[]> Run(
        Window window, List<object?[]> input, ReferenceInterpreter interpreter)
    {
        var width = window.Input.RowType.Fields.Count;
        var results = new object?[window.Calls.Count][];
        for (var c = 0; c < results.Length; c++)
        {
            results[c] = new object?[input.Count];
        }

        foreach (var partition in Partitions(window, input))
        {
            var peers = Peers(window, input, partition);
            for (var p = 0; p < partition.Count; p++)
            {
                var frame = Frame(window, input, partition, peers, p, interpreter);
                for (var c = 0; c < window.Calls.Count; c++)
                {
                    results[c][partition[p]] =
                        Evaluate(window.Calls[c], window, input, partition, peers, frame, p, interpreter);
                }
            }
        }

        var rows = new List<object?[]>(input.Count);
        for (var r = 0; r < input.Count; r++)
        {
            var row = new object?[width + window.Calls.Count];
            Array.Copy(input[r], row, width);
            for (var c = 0; c < window.Calls.Count; c++)
            {
                row[width + c] = results[c][r];
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Row indexes grouped by partition key, each group in the input's own order.</summary>
    private static List<List<int>> Partitions(Window window, List<object?[]> input)
    {
        if (window.PartitionKeys.Count == 0)
        {
            var all = new List<int>(input.Count);
            for (var r = 0; r < input.Count; r++)
            {
                all.Add(r);
            }

            return [all];
        }

        var order = new List<List<int>>();
        var groups = new Dictionary<ReferenceValues.RowKey, List<int>>();
        for (var r = 0; r < input.Count; r++)
        {
            var key = new ReferenceValues.RowKey(
                [.. window.PartitionKeys.Select(k => input[r][(int)k])]);
            if (!groups.TryGetValue(key, out var rows))
            {
                rows = [];
                groups[key] = rows;
                order.Add(rows);
            }

            rows.Add(r);
        }

        return order;
    }

    /// <summary>For each position in the partition, the half-open range of its peer group.</summary>
    private static (int Start, int End)[] Peers(
        Window window, List<object?[]> input, List<int> partition)
    {
        var peers = new (int Start, int End)[partition.Count];
        var start = 0;
        for (var p = 0; p <= partition.Count; p++)
        {
            if (p == partition.Count || (p > start && !SamePeer(window, input, partition, p - 1, p)))
            {
                for (var q = start; q < p; q++)
                {
                    peers[q] = (start, p);
                }

                start = p;
            }
        }

        return peers;
    }

    private static bool SamePeer(
        Window window, List<object?[]> input, List<int> partition, int left, int right)
    {
        foreach (var field in window.Order)
        {
            var column = (int)field.Expr.FieldRef.Index;
            if (!ReferenceValues.GroupEquals(input[partition[left]][column], input[partition[right]][column]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The frame of one row, as an inclusive pair of positions in the partition.</summary>
    private static (int Lo, int Hi) Frame(
        Window window,
        List<object?[]> input,
        List<int> partition,
        (int Start, int End)[] peers,
        int position,
        ReferenceInterpreter interpreter)
    {
        var frame = window.Frame;
        var size = partition.Count;
        var row = input[partition[position]];

        if (frame.Mode == FrameMode.Rows)
        {
            var lo = Bound(frame.Lower, position, size, interpreter, row, lower: true);
            var hi = Bound(frame.Upper, position, size, interpreter, row, lower: false);
            return ((int)Math.Clamp(lo, 0, size), (int)Math.Clamp(hi, -1, size - 1));
        }

        var peer = peers[position];
        var low = frame.Lower.Kind switch
        {
            FrameBoundKind.UnboundedPreceding => 0,
            FrameBoundKind.CurrentRow => peer.Start,
            _ => RangeSearch(window, input, partition, peers, position, frame.Lower, interpreter, lower: true),
        };
        var high = frame.Upper.Kind switch
        {
            FrameBoundKind.UnboundedFollowing => size - 1,
            FrameBoundKind.CurrentRow => peer.End - 1,
            _ => RangeSearch(window, input, partition, peers, position, frame.Upper, interpreter, lower: false),
        };
        return (low, high);
    }

    private static long Bound(
        FrameBound bound,
        int position,
        int size,
        ReferenceInterpreter interpreter,
        object?[] row,
        bool lower)
    {
        switch (bound.Kind)
        {
            case FrameBoundKind.UnboundedPreceding:
                return 0;
            case FrameBoundKind.UnboundedFollowing:
                return size - 1;
            case FrameBoundKind.CurrentRow:
                return position;
            default:
            {
                var offset = interpreter.Evaluate(bound.Offset, row)
                    ?? throw new InvalidOperationException("a window frame offset must not be NULL.");
                var n = Convert.ToInt64(offset, System.Globalization.CultureInfo.InvariantCulture);
                _ = lower;
                return bound.Kind == FrameBoundKind.Preceding ? position - n : position + n;
            }
        }
    }

    /// <summary>
    /// A RANGE bound with an offset, found by scanning the partition — the literal reading of "the
    /// rows whose key is within the offset of this row's". A NULL key is a peer of the other NULLs
    /// and of nothing else, so its frame is its own peer group.
    /// </summary>
    private static int RangeSearch(
        Window window,
        List<object?[]> input,
        List<int> partition,
        (int Start, int End)[] peers,
        int position,
        FrameBound bound,
        ReferenceInterpreter interpreter,
        bool lower)
    {
        var key = (int)window.Order[0].Expr.FieldRef.Index;
        var descending = window.Order[0].Direction
            is SortDirection.DescNullsFirst or SortDirection.DescNullsLast;
        var current = input[partition[position]][key];
        if (current is null)
        {
            return lower ? peers[position].Start : peers[position].End - 1;
        }

        var offsetValue = interpreter.Evaluate(bound.Offset, input[partition[position]])
            ?? throw new InvalidOperationException("a window frame offset must not be NULL.");
        var target = Shift(
            current,
            offsetValue,
            ChalkType.FromProto(window.Input.RowType.Fields[key].Type),
            ChalkType.FromProto(bound.Offset.Type),
            back: (bound.Kind == FrameBoundKind.Preceding) != descending);

        if (lower)
        {
            for (var p = 0; p < partition.Count; p++)
            {
                var value = input[partition[p]][key];
                if (value is null)
                {
                    continue;
                }

                var comparison = ReferenceValues.Compare(value, target);
                if (descending ? comparison <= 0 : comparison >= 0)
                {
                    return p;
                }
            }

            return partition.Count;
        }

        for (var p = partition.Count - 1; p >= 0; p--)
        {
            var value = input[partition[p]][key];
            if (value is null)
            {
                continue;
            }

            var comparison = ReferenceValues.Compare(value, target);
            if (descending ? comparison >= 0 : comparison <= 0)
            {
                return p;
            }
        }

        return -1;
    }

    /// <summary>The key value the bound compares against: the current key moved by the offset.</summary>
    private static object Shift(
        object current, object offset, ChalkType keyType, ChalkType offsetType, bool back)
    {
        if (offsetType.Kind == TypeKind.IntervalDay)
        {
            var microseconds = Convert.ToInt64(offset, System.Globalization.CultureInfo.InvariantCulture);
            var units = keyType.Kind == TypeKind.Date
                ? microseconds / 86_400_000_000L
                : Rescale(microseconds, (long)IrTypes.TimestampUnitsPerSecond((uint)keyType.Precision));
            var raw = Convert.ToInt64(current, System.Globalization.CultureInfo.InvariantCulture);
            return back ? raw - units : raw + units;
        }

        return current switch
        {
            double value => back
                ? value - Convert.ToDouble(offset, System.Globalization.CultureInfo.InvariantCulture)
                : value + Convert.ToDouble(offset, System.Globalization.CultureInfo.InvariantCulture),
            float value => back
                ? value - Convert.ToSingle(offset, System.Globalization.CultureInfo.InvariantCulture)
                : value + Convert.ToSingle(offset, System.Globalization.CultureInfo.InvariantCulture),
            decimal value => back
                ? value - Convert.ToDecimal(offset, System.Globalization.CultureInfo.InvariantCulture)
                : value + Convert.ToDecimal(offset, System.Globalization.CultureInfo.InvariantCulture),
            _ => back
                ? Convert.ToInt64(current, System.Globalization.CultureInfo.InvariantCulture)
                    - Convert.ToInt64(offset, System.Globalization.CultureInfo.InvariantCulture)
                : Convert.ToInt64(current, System.Globalization.CultureInfo.InvariantCulture)
                    + Convert.ToInt64(offset, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static long Rescale(long microseconds, long unitsPerSecond) => unitsPerSecond >= 1_000_000L
        ? microseconds * (unitsPerSecond / 1_000_000L)
        : microseconds / (1_000_000L / unitsPerSecond);

    private static bool Excluded(
        Window window, (int Start, int End)[] peers, int position, int candidate) =>
        window.Frame.Exclusion switch
        {
            FrameExclusion.CurrentRow => candidate == position,
            FrameExclusion.Group =>
                candidate >= peers[position].Start && candidate < peers[position].End,
            FrameExclusion.Ties =>
                candidate != position
                && candidate >= peers[position].Start
                && candidate < peers[position].End,
            _ => false,
        };

    private static object? Evaluate(
        WindowCall call,
        Window window,
        List<object?[]> input,
        List<int> partition,
        (int Start, int End)[] peers,
        (int Lo, int Hi) frame,
        int position,
        ReferenceInterpreter interpreter)
    {
        var type = ChalkType.FromProto(call.Type);
        var row = input[partition[position]];

        if (call.FunctionCase == WindowCall.FunctionOneofCase.WindowFunction)
        {
            return Navigate(call, window, input, partition, peers, frame, position, type, interpreter);
        }

        // The frame's rows, materialised. That is the whole implementation strategy.
        var values = new List<object?>();
        var rows = 0;
        for (var p = frame.Lo; p <= frame.Hi; p++)
        {
            if (Excluded(window, peers, position, p))
            {
                continue;
            }

            rows++;
            if (call.Args.Count > 0)
            {
                values.Add(interpreter.Evaluate(call.Args[0], input[partition[p]]));
            }
        }

        var present = values.Where(v => v is not null).ToList();

        // D56: DISTINCT inside a window aggregate. The oracle does the obvious thing — drop the
        // duplicates from the materialised frame — and NULLs are not values, so they are already out.
        if (call.Distinct)
        {
            var distinct = new List<object?>();
            foreach (var value in present)
            {
                if (!distinct.Any(seen => ReferenceValues.GroupEquals(seen, value)))
                {
                    distinct.Add(value);
                }
            }

            present = distinct;
            rows = present.Count;
        }

        if (call.UserFunction.Length > 0)
        {
            // D80: the host's state machine over the frame's own rows. Whether the vectorised engine
            // slid or recomputed, this is the answer both must agree on.
            var accumulator = interpreter.UserAggregate(call.UserFunction);
            foreach (var value in present)
            {
                accumulator.Add(value);
            }

            return accumulator.Finish();
        }

        switch (call.Aggregate)
        {
            case AggregateFunctionId.Count:
                return call.Args.Count == 0 ? (long)rows : (long)present.Count;

            case AggregateFunctionId.Sum when present.Count == 0:
            case AggregateFunctionId.Avg when present.Count == 0:
                return null;

            case AggregateFunctionId.Sum:
            case AggregateFunctionId.Sum0:
                return Total(present, type, average: false);

            case AggregateFunctionId.Avg:
                return Total(present, type, average: true);

            case AggregateFunctionId.Min:
            case AggregateFunctionId.Max:
            case AggregateFunctionId.BoolAnd:
            case AggregateFunctionId.BoolOr:
            {
                var max = call.Aggregate is AggregateFunctionId.Max or AggregateFunctionId.BoolOr;
                object? best = null;
                foreach (var value in present)
                {
                    if (best is null
                        || (max
                            ? ReferenceValues.Compare(value!, best) > 0
                            : ReferenceValues.Compare(value!, best) < 0))
                    {
                        best = value;
                    }
                }

                return best;
            }

            case AggregateFunctionId.AnyValue:
                return present.Count == 0 ? null : present[0];

            // D57's holistic family over a frame. The oracle reuses the grouped implementation over
            // the materialised frame, which is exactly what §3 says the window form computes; a
            // window carries no WITHIN GROUP ordering (V22), so the frame's row order is the order.
            case AggregateFunctionId.Mode:
            case AggregateFunctionId.Listagg:
            case AggregateFunctionId.StringAgg:
            case AggregateFunctionId.ArrayAgg:
            {
                var measure = new Measure { Function = call.Aggregate, Type = call.Type };
                measure.Args.AddRange(call.Args);
                return Holistic.Result(
                    measure, type, [.. values.Select(v => (v, (object?)null))]);
            }

            default:
                _ = row;
                throw new UnsupportedFeatureException(
                    call.Aggregate.ToString(),
                    "The reference executor implements COUNT, SUM, SUM0, AVG, MIN, MAX, BOOL_AND, "
                    + "BOOL_OR and ANY_VALUE over a window.");
        }
    }

    /// <summary>SUM and AVG over the frame's values, at the result type's own arithmetic.</summary>
    private static object Total(List<object?> values, ChalkType type, bool average)
    {
        switch (type.Kind)
        {
            case TypeKind.Fp32:
            {
                var sum = 0f;
                foreach (var value in values)
                {
                    sum += Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
                }

                return average ? sum / values.Count : sum;
            }

            case TypeKind.Fp64:
            {
                var sum = 0d;
                foreach (var value in values)
                {
                    sum += Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                }

                return average ? sum / values.Count : sum;
            }

            case TypeKind.Decimal:
            {
                var sum = 0m;
                foreach (var value in values)
                {
                    sum += Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
                }

                return ReferenceInterpreter.Conversions.Rescale(
                    average ? sum / values.Count : sum, type);
            }

            default:
            {
                var sum = 0L;
                foreach (var value in values)
                {
                    sum = checked(sum + Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                }

                return ReferenceInterpreter.Conversions.CheckRange(
                    average ? sum / values.Count : sum, type);
            }
        }
    }

    private static object? Navigate(
        WindowCall call,
        Window window,
        List<object?[]> input,
        List<int> partition,
        (int Start, int End)[] peers,
        (int Lo, int Hi) frame,
        int position,
        ChalkType type,
        ReferenceInterpreter interpreter)
    {
        var size = partition.Count;
        var row = input[partition[position]];

        switch (call.WindowFunction)
        {
            case WindowFunctionId.RowNumber:
                return (long)position + 1;
            case WindowFunctionId.Rank:
                return (long)peers[position].Start + 1;
            case WindowFunctionId.DenseRank:
            {
                var rank = 0L;
                var seen = -1;
                for (var p = 0; p <= position; p++)
                {
                    if (peers[p].Start != seen)
                    {
                        seen = peers[p].Start;
                        rank++;
                    }
                }

                return rank;
            }

            case WindowFunctionId.PercentRank:
                return size <= 1 ? 0d : (double)peers[position].Start / (size - 1);
            case WindowFunctionId.CumeDist:
                return (double)peers[position].End / size;
            case WindowFunctionId.Ntile:
            {
                var buckets = Convert.ToInt64(
                    interpreter.Evaluate(call.Args[0], row), System.Globalization.CultureInfo.InvariantCulture);
                if (buckets <= 0)
                {
                    throw new InvalidOperationException($"NTILE({buckets}) needs a positive bucket count.");
                }

                var quotient = size / buckets;
                var remainder = size % buckets;
                var large = remainder * (quotient + 1);
                return position < large
                    ? (position / (quotient + 1)) + 1
                    : remainder + ((position - large) / quotient) + 1;
            }

            case WindowFunctionId.Lag:
            case WindowFunctionId.Lead:
            {
                var lead = call.WindowFunction == WindowFunctionId.Lead;
                var offset = call.Args.Count > 1
                    ? interpreter.Evaluate(call.Args[1], row)
                    : 1L;
                if (offset is null)
                {
                    return null;
                }

                var n = Convert.ToInt64(offset, System.Globalization.CultureInfo.InvariantCulture);
                var column = (int)call.Args[0].FieldRef.Index;
                int? target;
                if (call.IgnoreNulls)
                {
                    var present = new List<int>();
                    for (var p = 0; p < size; p++)
                    {
                        if (input[partition[p]][column] is not null)
                        {
                            present.Add(p);
                        }
                    }

                    var seen = lead
                        ? present.Count(p => p <= position)
                        : present.Count(p => p < position);
                    var index = lead ? seen + n - 1 : seen - n;
                    target = index >= 0 && index < present.Count ? present[(int)index] : null;
                }
                else
                {
                    var index = lead ? position + n : position - n;
                    target = index >= 0 && index < size ? (int)index : null;
                }

                if (target is { } found)
                {
                    return input[partition[found]][column];
                }

                return call.Args.Count > 2 ? interpreter.Evaluate(call.Args[2], row) : null;
            }

            default:
            {
                var column = (int)call.Args[0].FieldRef.Index;
                var candidates = new List<object?>();
                for (var p = frame.Lo; p <= frame.Hi; p++)
                {
                    if (Excluded(window, peers, position, p))
                    {
                        continue;
                    }

                    var value = input[partition[p]][column];
                    if (call.IgnoreNulls && value is null)
                    {
                        continue;
                    }

                    candidates.Add(value);
                }

                _ = type;
                return call.WindowFunction switch
                {
                    WindowFunctionId.FirstValue => candidates.Count == 0 ? null : candidates[0],
                    WindowFunctionId.LastValue => candidates.Count == 0 ? null : candidates[^1],
                    WindowFunctionId.NthValue => Nth(call, candidates, row, interpreter),
                    _ => throw new UnsupportedFeatureException(
                        call.WindowFunction.ToString(),
                        "The reference executor implements the window functions of "
                        + "docs/design/13-window-functions.md §1."),
                };
            }
        }
    }

    private static object? Nth(
        WindowCall call, List<object?> candidates, object?[] row, ReferenceInterpreter interpreter)
    {
        var value = interpreter.Evaluate(call.Args[1], row);
        if (value is null)
        {
            return null;
        }

        var n = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        if (n < 1)
        {
            throw new InvalidOperationException($"NTH_VALUE({n}) counts from one.");
        }

        return n <= candidates.Count ? candidates[(int)n - 1] : null;
    }
}
