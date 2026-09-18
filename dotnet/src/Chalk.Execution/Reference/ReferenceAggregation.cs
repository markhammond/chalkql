using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Reference;

/// <summary>
/// The reference aggregate: a dictionary from grouping tuple to state, in first-seen order (§8). It
/// re-derives the empty-group answers of <c>02-ir.md</c> §4 rather than sharing the engine's
/// accumulators, which is the point of a second implementation.
/// </summary>
internal static class ReferenceAggregation
{
    public static List<object?[]> Run(
        Aggregate aggregate, List<object?[]> input, ReferenceInterpreter interpreter)
    {
        var keys = aggregate.Groupings[0].Keys.Select(k => (int)k).ToArray();
        var measures = aggregate.Measures;
        var order = new List<ReferenceValues.RowKey>();
        var states = new Dictionary<ReferenceValues.RowKey, MeasureState[]>();

        if (keys.Length == 0)
        {
            // No grouping keys means exactly one output row, even for empty input (§4).
            var empty = new ReferenceValues.RowKey([]);
            order.Add(empty);
            states[empty] = NewStates(measures, interpreter);
        }

        foreach (var row in input)
        {
            var key = new ReferenceValues.RowKey([.. keys.Select(k => row[k])]);
            if (!states.TryGetValue(key, out var state))
            {
                state = NewStates(measures, interpreter);
                states[key] = state;
                order.Add(key);
            }

            for (var m = 0; m < measures.Count; m++)
            {
                var measure = measures[m];
                if (measure.Filter is not null && !interpreter.IsTrue(measure.Filter, row))
                {
                    continue;
                }

                // A percentile's value is its WITHIN GROUP expression; every other aggregate's is its
                // first argument (D57).
                var valueExpr = Holistic.IsPercentile(measure.Function)
                    ? measure.OrderBy[0].Expr
                    : measure.Args.Count > 0 ? measure.Args[0] : null;
                var value = valueExpr is null ? null : interpreter.Evaluate(valueExpr, row);
                var sortKey = Holistic.IsOrdered(measure) && !Holistic.IsPercentile(measure.Function)
                    ? interpreter.Evaluate(measure.OrderBy[0].Expr, row)
                    : value;
                state[m].Add(measure, value, sortKey);
            }
        }

        var results = new List<object?[]>(order.Count);
        foreach (var key in order)
        {
            var state = states[key];
            var values = new object?[keys.Length + measures.Count];
            for (var k = 0; k < keys.Length; k++)
            {
                values[k] = key.Values[k];
            }

            for (var m = 0; m < measures.Count; m++)
            {
                values[keys.Length + m] = state[m].Result(measures[m]);
            }

            results.Add(values);
        }

        return results;
    }

    private static MeasureState[] NewStates(
        IReadOnlyList<Measure> measures, ReferenceInterpreter interpreter)
    {
        var states = new MeasureState[measures.Count];
        for (var i = 0; i < measures.Count; i++)
        {
            states[i] = new MeasureState(measures[i], interpreter);
        }

        return states;
    }

    /// <summary>One measure's running state for one group.</summary>
    private sealed class MeasureState
    {
        private readonly List<object?>? _seen;
        private long _count;
        private object? _extreme;
        private long _integerSum;
        private double _doubleSum;
        private float _singleSum;
        private decimal _decimalSum;
        private bool _any;

        private readonly List<(object? Value, object? Key)> _holistic = [];

        /// <summary>The host's own state machine, for a client-bodied aggregate (D80).</summary>
        private readonly Chalk.Sources.IBoxedAggregate? _user;

        public MeasureState(Measure measure, ReferenceInterpreter interpreter)
        {
            _seen = measure.Distinct ? [] : null;
            _user = measure.UserFunction.Length > 0
                ? interpreter.UserAggregate(measure.UserFunction)
                : null;
        }

        public void Add(Measure measure, object? value, object? key)
        {
            if (_user is not null)
            {
                _user.Add(value);
                return;
            }

            if (Holistic.Applies(measure.Function))
            {
                _holistic.Add((value, key));
                return;
            }

            if (_seen is not null && value is not null)
            {
                foreach (var previous in _seen)
                {
                    if (ReferenceValues.GroupEquals(previous, value))
                    {
                        return;
                    }
                }

                _seen.Add(value);
            }

            switch (measure.Function)
            {
                case AggregateFunctionId.Count:
                    if (measure.Args.Count == 0 || value is not null)
                    {
                        _count++;
                    }

                    return;

                case AggregateFunctionId.Min:
                case AggregateFunctionId.Max:
                    if (value is null)
                    {
                        return;
                    }

                    if (!_any)
                    {
                        _any = true;
                        _extreme = value;
                        return;
                    }

                    var comparison = ReferenceValues.Compare(value, _extreme);
                    if (measure.Function == AggregateFunctionId.Max ? comparison > 0 : comparison < 0)
                    {
                        _extreme = value;
                    }

                    return;

                default:
                    if (value is null)
                    {
                        return;
                    }

                    _any = true;
                    switch (measure.Type.Kind)
                    {
                        case TypeKind.Fp32:
                            _singleSum += ToSingle(value);
                            break;
                        case TypeKind.Fp64:
                            _doubleSum += ToDouble(value);
                            break;
                        case TypeKind.Decimal:
                            _decimalSum += ToDecimal(value);
                            break;
                        default:
                            _integerSum = checked(_integerSum + ToInteger(value));
                            break;
                    }

                    return;
            }
        }

        public object? Result(Measure measure)
        {
            var type = ChalkType.FromProto(measure.Type);
            if (_user is not null)
            {
                return _user.Finish();
            }

            if (Holistic.Applies(measure.Function))
            {
                return Holistic.Result(measure, type, _holistic);
            }

            switch (measure.Function)
            {
                case AggregateFunctionId.Count:
                    return _count;

                case AggregateFunctionId.Min:
                case AggregateFunctionId.Max:
                    return _any ? _extreme : null;

                case AggregateFunctionId.Sum when !_any:
                    return null;

                case AggregateFunctionId.Sum:
                case AggregateFunctionId.Sum0:
                    return type.Kind switch
                    {
                        TypeKind.Fp32 => _singleSum,
                        TypeKind.Fp64 => _doubleSum,
                        TypeKind.Decimal => ReferenceInterpreter.Conversions.Rescale(_decimalSum, type),
                        _ => ReferenceInterpreter.Conversions.CheckRange(_integerSum, type),
                    };

                default:
                    throw new UnsupportedFeatureException(
                        measure.Function.ToString(),
                        "The reference executor implements COUNT, SUM, SUM0, MIN and MAX (A12).");
            }
        }

        private static long ToInteger(object value) => value switch
        {
            long integer => integer,
            decimal number => checked((long)decimal.Truncate(number)),
            double real => checked((long)Math.Truncate(real)),
            float single => checked((long)MathF.Truncate(single)),
            _ => throw new InvalidCastException($"SUM cannot read a {value.GetType().Name}"),
        };

        private static double ToDouble(object value) => value switch
        {
            double real => real,
            float single => single,
            long integer => integer,
            decimal number => (double)number,
            _ => throw new InvalidCastException($"SUM cannot read a {value.GetType().Name}"),
        };

        private static float ToSingle(object value) => value switch
        {
            float single => single,
            double real => (float)real,
            long integer => integer,
            decimal number => (float)number,
            _ => throw new InvalidCastException($"SUM cannot read a {value.GetType().Name}"),
        };

        private static decimal ToDecimal(object value) => value switch
        {
            decimal number => number,
            long integer => integer,
            double real => (decimal)real,
            float single => (decimal)single,
            _ => throw new InvalidCastException($"SUM cannot read a {value.GetType().Name}"),
        };
    }
}
