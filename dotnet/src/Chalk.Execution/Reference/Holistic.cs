using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Reference;

/// <summary>
/// The reference implementations of the holistic aggregates (D57, <c>14-windows-ii.md</c> §3),
/// written the obvious way: keep every value, sort at the end, and read the answer off the list.
/// Nothing here is shared with the engine's accumulators, which is the point of an oracle (I4).
/// </summary>
internal static class Holistic
{
    /// <summary>Whether this function is one of the holistic family.</summary>
    public static bool Applies(AggregateFunctionId function) => function switch
    {
        AggregateFunctionId.PercentileCont or AggregateFunctionId.PercentileDisc
            or AggregateFunctionId.Listagg or AggregateFunctionId.StringAgg
            or AggregateFunctionId.ArrayAgg or AggregateFunctionId.Mode => true,
        _ => false,
    };

    public static bool IsPercentile(AggregateFunctionId function) =>
        function is AggregateFunctionId.PercentileCont or AggregateFunctionId.PercentileDisc;

    /// <summary>Whether the measure carries an ordering the answer depends on.</summary>
    public static bool IsOrdered(Measure measure) => measure.OrderBy.Count > 0;

    /// <summary>The group's answer, from every (value, key) pair the group saw.</summary>
    public static object? Result(
        Measure measure, ChalkType type, List<(object? Value, object? Key)> entries)
    {
        var descending = measure.OrderBy.Count > 0
            && measure.OrderBy[0].Direction
                is SortDirection.DescNullsFirst or SortDirection.DescNullsLast;

        switch (measure.Function)
        {
            case AggregateFunctionId.PercentileCont:
            case AggregateFunctionId.PercentileDisc:
            {
                var values = entries
                    .Select(e => e.Value)
                    .Where(v => v is not null)
                    .OrderBy(v => v, Comparer<object?>.Create(ReferenceValues.Compare!))
                    .ToList();
                if (descending)
                {
                    values.Reverse();
                }

                if (values.Count == 0)
                {
                    return null;
                }

                var fraction = Fraction(measure);
                if (measure.Function == AggregateFunctionId.PercentileDisc)
                {
                    var index = (int)Math.Ceiling(values.Count * fraction) - 1;
                    return values[Math.Clamp(index, 0, values.Count - 1)];
                }

                var position = (values.Count - 1) * fraction;
                var below = (int)Math.Floor(position);
                var above = Math.Min(below + 1, values.Count - 1);
                var weight = position - below;
                return (Number(values[below]) * (1 - weight)) + (Number(values[above]) * weight);
            }

            case AggregateFunctionId.Listagg:
            case AggregateFunctionId.StringAgg:
            {
                var ordered = Ordered(measure, entries, descending)
                    .Where(v => v is not null)
                    .Cast<string>()
                    .ToList();
                if (ordered.Count == 0)
                {
                    return null;
                }

                return string.Join(Separator(measure), ordered);
            }

            case AggregateFunctionId.ArrayAgg:
            {
                var ordered = Ordered(measure, entries, descending).ToArray();
                return ordered.Length == 0 || ordered.All(v => v is null) ? null : ordered;
            }

            case AggregateFunctionId.Mode:
            {
                var counts = new List<(object Value, int Count)>();
                foreach (var value in entries.Select(e => e.Value).Where(v => v is not null))
                {
                    var index = counts.FindIndex(c => ReferenceValues.GroupEquals(c.Value, value));
                    if (index < 0)
                    {
                        counts.Add((value!, 1));
                    }
                    else
                    {
                        counts[index] = (counts[index].Value, counts[index].Count + 1);
                    }
                }

                if (counts.Count == 0)
                {
                    return null;
                }

                // Ties go to the smallest value (D57), which Calcite leaves unspecified.
                var best = counts[0];
                foreach (var candidate in counts)
                {
                    if (candidate.Count > best.Count
                        || (candidate.Count == best.Count
                            && ReferenceValues.Compare(candidate.Value, best.Value) < 0))
                    {
                        best = candidate;
                    }
                }

                return best.Value;
            }

            default:
                throw new UnsupportedFeatureException(
                    measure.Function.ToString(), "It is not a holistic aggregate.");
        }
    }

    /// <summary>
    /// The values in the order the aggregate asked for: by the key when there is one, NULL keys last,
    /// ties broken by the order the rows arrived in.
    /// </summary>
    private static IEnumerable<object?> Ordered(
        Measure measure, List<(object? Value, object? Key)> entries, bool descending)
    {
        if (!IsOrdered(measure))
        {
            return entries.Select(e => e.Value);
        }

        var indexed = entries.Select((e, i) => (e.Value, e.Key, Index: i)).ToList();
        indexed.Sort((a, b) =>
        {
            if (a.Key is null || b.Key is null)
            {
                if (a.Key is null && b.Key is null)
                {
                    return a.Index.CompareTo(b.Index);
                }

                return a.Key is null ? 1 : -1;
            }

            var order = ReferenceValues.Compare(a.Key, b.Key);
            if (descending)
            {
                order = -order;
            }

            return order != 0 ? order : a.Index.CompareTo(b.Index);
        });
        return indexed.Select(e => e.Value);
    }

    private static double Fraction(Measure measure)
    {
        var literal = measure.Args[0].Literal;
        return literal.ValueCase switch
        {
            Literal.ValueOneofCase.Fp32Value => literal.Fp32Value,
            Literal.ValueOneofCase.Fp64Value => literal.Fp64Value,
            Literal.ValueOneofCase.DecimalValue => (double)ReferenceValues.ReadDecimal(
                literal.DecimalValue.Unscaled.Span, (int)measure.Args[0].Type.Scale),
            Literal.ValueOneofCase.I32Value => literal.I32Value,
            Literal.ValueOneofCase.I64Value => literal.I64Value,
            _ => throw new UnsupportedFeatureException(
                "a percentile fraction of " + literal.ValueCase,
                "The fraction is a numeric literal between 0 and 1."),
        };
    }

    private static string Separator(Measure measure) =>
        measure.Args.Count > 1 ? measure.Args[1].Literal.StringValue : string.Empty;

    private static double Number(object? value) => value switch
    {
        long integer => integer,
        double real => real,
        float single => single,
        decimal number => (double)number,
        _ => throw new UnsupportedFeatureException(
            "PERCENTILE_CONT over " + (value?.GetType().Name ?? "NULL"),
            "It interpolates between two numbers."),
    };
}
