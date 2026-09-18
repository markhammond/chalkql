using Chalk.Ir;

namespace Chalk.Integration.Tests;

internal enum ColumnOriginKind
{
    /// <summary>A column of a base table, read unchanged.</summary>
    BaseColumn,

    /// <summary>The result of an aggregate measure, or of an expression over one.</summary>
    Measure,

    /// <summary>Anything computed from base columns.</summary>
    Computed,
}

/// <summary>
/// Where one output column of a plan came from. <see cref="Discriminator"/> distinguishes two
/// computed columns that would otherwise look alike, so a set of origins can be compared for
/// coverage the way a set of column indexes can.
/// </summary>
internal readonly record struct ColumnOrigin(ColumnOriginKind Kind, string Table, int Discriminator)
{
    public static ColumnOrigin Base(string table, int column) => new(ColumnOriginKind.BaseColumn, table, column);
}

/// <summary>
/// Traces each of a plan's output columns back to a base column, an aggregate measure, or a
/// computation. The differential comparison needs this for two decisions
/// (<c>docs/design/05-testing.md</c> §5): which floating-point columns were produced by aggregation
/// and so compare within a tolerance, and whether the ORDER BY covers a unique key and so gives the
/// result a total order.
/// </summary>
internal sealed class PlanOrigins
{
    private int _next;
    private IReadOnlyList<ColumnOrigin> _columns = [];
    private IReadOnlyList<ColumnOrigin>? _groupKeys;

    private PlanOrigins()
    {
    }

    /// <summary>Origins of the root's output columns, in output order.</summary>
    public IReadOnlyList<ColumnOrigin> Columns => _columns;

    /// <summary>
    /// Origins of the grouping keys of the aggregate nearest the root, or null when the plan has no
    /// aggregate. Those columns are unique in the aggregate's output, and every operator above an
    /// aggregate in an M1 plan is row-preserving or row-dropping, so they stay unique at the root.
    /// </summary>
    public IReadOnlyList<ColumnOrigin>? GroupKeys => _groupKeys;

    public static PlanOrigins Trace(Rel root)
    {
        var tracer = new PlanOrigins();
        tracer._columns = tracer.Of(root);
        return tracer;
    }

    private ColumnOrigin[] Of(Rel rel)
    {
        switch (rel.KindCase)
        {
            case Rel.KindOneofCase.Read:
                var table = $"{rel.Read.Table.Schema}.{rel.Read.Table.Table}";
                return [.. rel.Read.Projection.Select(c => ColumnOrigin.Base(table, (int)c))];
            case Rel.KindOneofCase.Filter:
                return Of(rel.Filter.Input);
            case Rel.KindOneofCase.Sort:
                return Of(rel.Sort.Input);
            case Rel.KindOneofCase.Fetch:
                return Of(rel.Fetch.Input);
            case Rel.KindOneofCase.TopN:
                return Of(rel.TopN.Input);
            case Rel.KindOneofCase.Project:
                var input = Of(rel.Project.Input);
                return [.. rel.Project.Exprs.Select(e => OfExpr(e, input))];
            case Rel.KindOneofCase.Aggregate:
                return OfAggregate(rel.Aggregate);
            case Rel.KindOneofCase.HashAggregate:
                return OfAggregate(rel.HashAggregate.Aggregate);
            case Rel.KindOneofCase.StreamAggregate:
                return OfAggregate(rel.StreamAggregate.Aggregate);
            case Rel.KindOneofCase.Window:
            {
                // A window's row is its input's, then one column per call. A call is an aggregation
                // over a frame, and a floating one carries the same rounding an aggregate's does —
                // more of it, in fact, since a sliding sum adds and subtracts (13-window-functions.md
                // §4). So its column compares within the ULP tolerance of 05-testing.md §5.
                var below = Of(rel.Window.Input);
                return
                [
                    .. below,
                    .. rel.Window.Calls.Select(_ => Fresh(ColumnOriginKind.Measure)),
                ];
            }
            default:
                // Values, and everything M2 adds. Opaque, but still distinguishable column by column.
                return [.. rel.RowType.Fields.Select(_ => Fresh(ColumnOriginKind.Computed))];
        }
    }

    private ColumnOrigin[] OfAggregate(Aggregate aggregate)
    {
        var input = Of(aggregate.Input);
        var keys = aggregate.Groupings.Count == 0 ? [] : aggregate.Groupings[0].Keys;
        var origins = new List<ColumnOrigin>(keys.Count + aggregate.Measures.Count);
        origins.AddRange(keys.Select(k => input[(int)k]));
        origins.AddRange(aggregate.Measures.Select(_ => Fresh(ColumnOriginKind.Measure)));

        // The aggregate nearest the root wins: an outer aggregate's own trace finishes after the
        // traces of everything below it.
        _groupKeys = [.. origins.Take(keys.Count)];
        return [.. origins];
    }

    private ColumnOrigin OfExpr(Expr expr, ColumnOrigin[] input)
    {
        if (expr.KindCase == Expr.KindOneofCase.FieldRef)
        {
            return input[(int)expr.FieldRef.Index];
        }

        var referenced = PlanWalker.Exprs(expr)
            .Where(e => e.KindCase == Expr.KindOneofCase.FieldRef)
            .Select(e => input[(int)e.FieldRef.Index])
            .ToList();

        // SUM(x) / 2 is still an aggregated value, and carries the aggregate's rounding with it.
        return referenced.Any(o => o.Kind == ColumnOriginKind.Measure)
            ? Fresh(ColumnOriginKind.Measure)
            : Fresh(ColumnOriginKind.Computed);
    }

    private ColumnOrigin Fresh(ColumnOriginKind kind) => new(kind, string.Empty, _next++);
}
