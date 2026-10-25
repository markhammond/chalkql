using System.Globalization;

namespace Chalk.Sample.Blotter.Postgres;

/// <summary>
///     SQL written for PostgreSQL itself, its values written in: what the host and the tests send PostgreSQL
///     directly, beside ChalkQL, which writes and binds its own.
/// </summary>
public static class PostgresSql
{
    /// <summary>
    ///     A filter's conditions with their values written in, in SQL PostgreSQL takes: for the test
    ///     oracle, and for asking PostgreSQL what a page would scan.
    /// </summary>
    public static string Where(FilterSpec filter)
    {
        if (filter.Columns.IsEmpty) return "TRUE";

        var conditions = new List<string>();
        foreach (var each in filter.Columns)
        {
            var column = each.Column.Name;
            if (!each.Values.IsEmpty)
                conditions.Add(each.Match == TextMatch.AnyOf || each.Column.Kind == ColumnKind.Id
                    ? $"{column} IN ({string.Join(", ", each.Values.Select(v => each.Column.Kind == ColumnKind.Id ? Quote(Guid.TryParse(v, out var id) ? id.ToString() : Guid.Empty.ToString()) : Quote(v)))})"
                    : "(" + string.Join(" OR ", each.Values.Select(v => $"starts_with({column}, {Quote(v)})")) + ")");

            if (each.Min is { } min)
                conditions.Add(string.Create(CultureInfo.InvariantCulture, $"{column} >= {each.Column.Bind(min)}"));

            if (each.Max is { } max)
                conditions.Add(string.Create(CultureInfo.InvariantCulture, $"{column} <= {each.Column.Bind(max)}"));

            if (each.From is { } from)
                conditions.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{column} >= TIMESTAMP '{from:yyyy-MM-dd HH:mm:ss.ffffff}'"));

            if (each.To is { } to)
                conditions.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{column} < TIMESTAMP '{to:yyyy-MM-dd HH:mm:ss.ffffff}'"));
        }

        return string.Join(" AND ", conditions);
    }

    /// <summary>A value written into SQL as PostgreSQL takes it.</summary>
    public static string Literal(object? value) => value switch
    {
        DateTime time => string.Create(CultureInfo.InvariantCulture,
            $"TIMESTAMP '{time:yyyy-MM-dd HH:mm:ss.ffffff}'"),
        Guid id => $"'{id}'::uuid",
        string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable other => other.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"no SQL literal for {value?.GetType().Name ?? "null"}", nameof(value))
    };

    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
}