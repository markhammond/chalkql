using System.Text;
using Chalk.Sample.Blotter.Grid;

namespace Chalk.Sample.Blotter.Live;

/// <summary>Which rows an alert's condition is weighed against.</summary>
public enum ConditionRows
{
    /// <summary>What the streams have just sent: <c>live.arrived_orders</c> or <c>live.arrived_fills</c>.</summary>
    Arrived,

    /// <summary>What the tail holds, at the streams' newest version: <c>live.tail</c> or <c>live.tail_fills</c>.</summary>
    Tail,
}

/// <summary>
/// The statements that weigh alert conditions, run by ChalkQL: the condition as the <c>WHERE</c> of a
/// read of the rows it is weighed against, by key. A path is a join: to the tail, whose versions are
/// the newest the streams have sent, and to the database, the newer of the two standing. The
/// database's side is read for the keys the rows name, <c>@&lt;path&gt;_ids</c>, which ChalkQL hands to
/// the database: joined as a table, it would read every row of it.
/// </summary>
/// <remarks>
/// A row a path finds nothing for, such as a fill whose order has not arrived yet, reads nulls along
/// it, and a comparison with a null passes no row.
/// </remarks>
public static class ConditionStatements
{
    public static string Sql(Condition condition, ConditionRows rows)
    {
        var tab = condition.Tab;
        var table = (tab.Overrides, rows) switch
        {
            (true, ConditionRows.Arrived) => "live.arrived_orders",
            (true, ConditionRows.Tail) => "live.tail",
            (false, ConditionRows.Arrived) => "live.arrived_fills",
            (false, ConditionRows.Tail) => "live.tail_fills",
            _ => throw new ArgumentOutOfRangeException(nameof(rows), rows, null),
        };

        var joins = new StringBuilder();
        foreach (var path in condition.Paths)
        {
            if (!path.Target.Overrides)
            {
                throw new NotSupportedException($"a path to {path.Target.Title} reads the tail of versioned rows, which {path.Target.Title} are not");
            }

            var key = path.Target.Key.Name;
            joins.Append($"\nLEFT JOIN live.tail AS {path.Name}_t ON {path.Name}_t.{key} = a.{path.Via}");
            joins.Append($"\nLEFT JOIN (SELECT {path.Target.Select} FROM {path.Target.Table} WHERE {key} IN @{path.Name}_ids) AS {path.Name}_d");
            joins.Append($" ON {path.Name}_d.{key} = a.{path.Via}");
        }

        var where = condition.Sql(
            column => "a." + column.Name,
            (path, column) => $"(CASE WHEN {path.Name}_d.{path.Target.Key.Name} IS NULL OR {path.Name}_t.version >= {path.Name}_d.version "
                + $"THEN {path.Name}_t.{column.Name} ELSE {path.Name}_d.{column.Name} END)");
        var select = string.Join(", ", tab.Select.Split(',').Select(c => "a." + c.Trim()));
        return $"SELECT {select}\nFROM {table} AS a{joins}\nWHERE a.{tab.Key.Name} IN @ids AND ({where})";
    }
}
