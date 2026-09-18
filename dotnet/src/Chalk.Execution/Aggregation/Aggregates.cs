using Chalk.Ir;

namespace Chalk.Execution.Aggregation;

/// <summary>Small facts about the aggregate functions the IR carries, in one place.</summary>
internal static class Aggregates
{
    /// <summary>Whether this is one of the two percentiles, whose value is their ordering (D57).</summary>
    public static bool IsPercentile(AggregateFunctionId function) =>
        function is AggregateFunctionId.PercentileCont or AggregateFunctionId.PercentileDisc;

    /// <summary>How many arguments a measure of this function may carry.</summary>
    public static int MaximumArguments(AggregateFunctionId function) => function switch
    {
        // LISTAGG and STRING_AGG take the value and the separator.
        AggregateFunctionId.Listagg or AggregateFunctionId.StringAgg => 2,
        _ => 1,
    };

    /// <summary>Whether a sort direction runs the other way.</summary>
    public static bool IsDescending(SortDirection direction) =>
        direction is SortDirection.DescNullsFirst or SortDirection.DescNullsLast;
}
