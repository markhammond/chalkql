using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Chalk.Sample.Blotter.Trades;

/// <summary>How a text column's values are matched.</summary>
public enum TextMatch
{
    /// <summary>The value is one of those given.</summary>
    AnyOf,

    /// <summary>The value starts with one of those given.</summary>
    StartsWith,
}

/// <summary>
/// One column's filter: values to match for text and choices, a range for numbers and times. An
/// empty filter filters nothing.
/// </summary>
public sealed record ColumnFilter
{
    public required Column Column { get; init; }

    /// <summary>For text and choices: the values, or the starts of values, to match.</summary>
    public ImmutableArray<string> Values { get; init; } = [];

    public TextMatch Match { get; init; } = TextMatch.AnyOf;

    /// <summary>For numbers: at least, and at most.</summary>
    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>For times: at or after, and before.</summary>
    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    public bool IsEmpty => Values.IsEmpty && Min is null && Max is null && From is null && To is null;

    /// <summary>
    /// Whether <paramref name="row"/> passes, decided in the host exactly as the database decides the
    /// SQL: values matched byte for byte, a number against its bound as the column binds it.
    /// </summary>
    public bool Matches(IRow row)
    {
        var value = Column.Value(row);
        if (!Values.IsEmpty)
        {
            var passes = Column.Kind == ColumnKind.Id
                ? Values.Any(v => (Guid.TryParse(v, out var id) ? id : Guid.Empty) == (Guid)value)
                : Match == TextMatch.AnyOf
                    ? Values.Contains((string)value, StringComparer.Ordinal)
                    : Values.Any(v => ((string)value).StartsWith(v, StringComparison.Ordinal));
            if (!passes)
            {
                return false;
            }
        }

        return (Min is not { } min || Convert.ToDouble(value, CultureInfo.InvariantCulture) >= Convert.ToDouble(Column.Bind(min), CultureInfo.InvariantCulture))
            && (Max is not { } max || Convert.ToDouble(value, CultureInfo.InvariantCulture) <= Convert.ToDouble(Column.Bind(max), CultureInfo.InvariantCulture))
            && (From is not { } from || (DateTime)value >= from)
            && (To is not { } to || (DateTime)value < to);
    }

    /// <summary>Canonical: values sorted and distinct, times to the microsecond.</summary>
    public ColumnFilter Canonical() => this with
    {
        Values = [.. Values.Where(v => v.Length > 0).Distinct().Order(StringComparer.Ordinal)],
        From = From is { } from ? Cells.Micros(from) : null,
        To = To is { } to ? Cells.Micros(to) : null,
    };

    public string Name
    {
        get
        {
            var name = new StringBuilder(Column.Name);
            if (!Values.IsEmpty)
            {
                name.Append(Match == TextMatch.StartsWith ? " starts " : " in ").AppendJoin('|', Values);
            }

            if (Min is { } min)
            {
                name.Append(" >= ").Append(min.ToString("R", CultureInfo.InvariantCulture));
            }

            if (Max is { } max)
            {
                name.Append(" <= ").Append(max.ToString("R", CultureInfo.InvariantCulture));
            }

            if (From is { } from)
            {
                name.Append(" from ").Append(from.ToString("O", CultureInfo.InvariantCulture));
            }

            if (To is { } to)
            {
                name.Append(" to ").Append(to.ToString("O", CultureInfo.InvariantCulture));
            }

            return name.ToString();
        }
    }

    /// <summary>What a person reads in the filter's pill.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (!Values.IsEmpty)
        {
            var shown = string.Join(", ", Values.Take(3)) + (Values.Length > 3 ? $" +{Values.Length - 3}" : "");
            parts.Add(Match == TextMatch.StartsWith ? $"starts {shown}" : shown);
        }

        if (Min is { } min)
        {
            parts.Add("≥ " + min.ToString("#,0.####", CultureInfo.InvariantCulture));
        }

        if (Max is { } max)
        {
            parts.Add("≤ " + max.ToString("#,0.####", CultureInfo.InvariantCulture));
        }

        if (From is { } from)
        {
            parts.Add("from " + from.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        if (To is { } to)
        {
            parts.Add("to " + to.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        return $"{Column.Title}: {string.Join(" ", parts)}";
    }
}

/// <summary>
/// What a tab shows: a filter on any of its columns, all of which must hold. Every value is bound as
/// a parameter, never written into the SQL, so one shape of filter is one prepared statement
/// whatever values it holds.
/// </summary>
public sealed class FilterSpec
{
    private FilterSpec(ImmutableArray<ColumnFilter> columns)
    {
        Columns = columns;
        Name = columns.IsEmpty ? "all" : string.Join("; ", columns.Select(c => c.Name));
    }

    public static FilterSpec None { get; } = new([]);

    /// <summary>The columns filtered, by name.</summary>
    public ImmutableArray<ColumnFilter> Columns { get; }

    /// <summary>Canonical, for sharing a live scope between pages that filter alike.</summary>
    public string Name { get; }

    public static FilterSpec Of(IEnumerable<ColumnFilter> filters) =>
        new([.. filters.Select(f => f.Canonical()).Where(f => !f.IsEmpty).OrderBy(f => f.Column.Name, StringComparer.Ordinal)]);

    public ColumnFilter? For(Column column) => Columns.FirstOrDefault(c => c.Column == column);

    /// <summary>Whether <paramref name="row"/> passes every column's filter, decided in the host.</summary>
    public bool Matches(IRow row) => Columns.All(c => c.Matches(row));

    /// <summary>This filter with <paramref name="filter"/> in place of its column's, or without it when it is empty.</summary>
    public FilterSpec With(ColumnFilter filter) =>
        Of([.. Columns.Where(c => c.Column != filter.Column), filter]);

    public FilterSpec Without(Column column) => Of(Columns.Where(c => c.Column != column));

    /// <summary>
    /// The conditions, joined by <c>AND</c>, over columns named with <paramref name="prefix"/>; <c>TRUE</c>
    /// when there are none. Two prefixes share one set of parameters: an order's live scope tests the
    /// order's version in the tail and its row in the snapshot against the same values.
    /// </summary>
    public string Where(string prefix = "")
    {
        if (Columns.IsEmpty)
        {
            return "TRUE";
        }

        var conditions = new List<string>();
        foreach (var filter in Columns)
        {
            var column = prefix + filter.Column.Name;
            var name = filter.Column.Name;
            if (!filter.Values.IsEmpty)
            {
                conditions.Add(filter.Match == TextMatch.AnyOf || filter.Column.Kind == ColumnKind.Id
                    ? $"{column} IN @{name}_in"
                    : "(" + string.Join(" OR ", filter.Values.Select((_, i) => $"{column} LIKE @{name}_p{i} ESCAPE '\\'")) + ")");
            }

            if (filter.Min is not null)
            {
                conditions.Add($"{column} >= @{name}_min");
            }

            if (filter.Max is not null)
            {
                conditions.Add($"{column} <= @{name}_max");
            }

            if (filter.From is not null)
            {
                conditions.Add($"{column} >= @{name}_from");
            }

            if (filter.To is not null)
            {
                conditions.Add($"{column} < @{name}_to");
            }
        }

        return string.Join(" AND ", conditions);
    }

    /// <summary>The values <see cref="Where"/> names, each of its column's own type.</summary>
    public Dictionary<string, object?> Parameters()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var filter in Columns)
        {
            var name = filter.Column.Name;
            if (!filter.Values.IsEmpty)
            {
                if (filter.Column.Kind == ColumnKind.Id)
                {
                    // An id typed that is not one matches nothing; it is not bound.
                    values[$"{name}_in"] = filter.Values.Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty).ToArray();
                }
                else if (filter.Match == TextMatch.AnyOf)
                {
                    values[$"{name}_in"] = filter.Values.ToArray();
                }
                else
                {
                    for (var i = 0; i < filter.Values.Length; i++)
                    {
                        values[$"{name}_p{i}"] = Escape(filter.Values[i]) + "%";
                    }
                }
            }

            if (filter.Min is { } min)
            {
                values[$"{name}_min"] = filter.Column.Bind(min);
            }

            if (filter.Max is { } max)
            {
                values[$"{name}_max"] = filter.Column.Bind(max);
            }

            if (filter.From is { } from)
            {
                values[$"{name}_from"] = from;
            }

            if (filter.To is { } to)
            {
                values[$"{name}_to"] = to;
            }
        }

        return values;
    }

    /// <summary>
    /// The same conditions with their values written in, in SQL PostgreSQL takes: for the oracle, and
    /// for asking PostgreSQL what it would scan.
    /// </summary>
    public string Literal()
    {
        if (Columns.IsEmpty)
        {
            return "TRUE";
        }

        var conditions = new List<string>();
        foreach (var filter in Columns)
        {
            var column = filter.Column.Name;
            if (!filter.Values.IsEmpty)
            {
                conditions.Add(filter.Match == TextMatch.AnyOf || filter.Column.Kind == ColumnKind.Id
                    ? $"{column} IN ({string.Join(", ", filter.Values.Select(v => filter.Column.Kind == ColumnKind.Id ? Quote(Guid.TryParse(v, out var id) ? id.ToString() : Guid.Empty.ToString()) : Quote(v)))})"
                    : "(" + string.Join(" OR ", filter.Values.Select(v => $"starts_with({column}, {Quote(v)})")) + ")");
            }

            if (filter.Min is { } min)
            {
                conditions.Add(string.Create(CultureInfo.InvariantCulture, $"{column} >= {filter.Column.Bind(min)}"));
            }

            if (filter.Max is { } max)
            {
                conditions.Add(string.Create(CultureInfo.InvariantCulture, $"{column} <= {filter.Column.Bind(max)}"));
            }

            if (filter.From is { } from)
            {
                conditions.Add(string.Create(CultureInfo.InvariantCulture, $"{column} >= TIMESTAMP '{from:yyyy-MM-dd HH:mm:ss.ffffff}'"));
            }

            if (filter.To is { } to)
            {
                conditions.Add(string.Create(CultureInfo.InvariantCulture, $"{column} < TIMESTAMP '{to:yyyy-MM-dd HH:mm:ss.ffffff}'"));
            }
        }

        return string.Join(" AND ", conditions);
    }

    public override string ToString() => Name;

    /// <summary>A typed value matched literally by <c>LIKE … ESCAPE '\'</c>: its wildcards and the escape itself escaped.</summary>
    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
}
