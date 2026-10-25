using System.Collections.Immutable;
using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;

namespace Chalk.Sample.Blotter.Grid;

/// <summary>
///     A view of a tab as a link: its sort, its filters, the columns hidden, and the row it is held at,
///     unless it follows the live top. Everything is in the link; nothing is kept for it, so it opens the
///     same view anywhere, for as long as the rows are there.
/// </summary>
/// <remarks>
///     <para>
///         The row held at is written as its keyset: its value of each column the view sorts by, the key
///         last. That is where the view stands even if the row has changed, or gone, since.
///     </para>
///     <para>
///         <c>
///             ?tab=fills&amp;sort=-client,qty&amp;hide=region,channel&amp;f.client=CEDAR&amp;f.client=FERN&amp;
///             f.qty.min=44&amp;at.client=…
///         </c>
///         :
///         a tab other than client orders; columns in the sort, descending with a minus; the hidden columns
///         when they are not those hidden at first (<c>hide=</c> for none); a filter's values, each once, or
///         with <c>.starts</c> what they start with, <c>.min</c> and <c>.max</c> for a number,
///         <c>.from</c> and <c>.to</c> for a time; and the row held at.
///     </para>
///     <para>
///         Shrouded (<see cref="LinkShroud" />), the filters' values and the row held at, which are data, go
///         in one opaque <c>s=</c>; the tab, the sort and the hidden columns, which are only how it is laid
///         out, stay as they are. A link may give a filter or the row held at as is, shrouded, or some of
///         each, but not one part both ways.
///     </para>
/// </remarks>
public sealed record ViewLink(Tab Tab, SortSpec Sort, FilterSpec Filter, IReadOnlySet<string> Hidden, IRow? At)
{
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.ffffff";

    /// <summary>
    ///     The link's query, without the <c>?</c>; empty for the tab as it opens. Given a
    ///     <paramref name="shroud" />, the filters' values and the row held at are shrouded.
    /// </summary>
    public string Query(LinkShroud? shroud = null)
    {
        var pairs = new List<(string Key, string Value)>();
        if (!Tab.Home) pairs.Add(("tab", Tab.Name));

        if (!Sort.IsDefault)
            pairs.Add(("sort", string.Join(",", Sort.Chosen.Select(k => (k.Descending ? "-" : "") + k.Column.Name))));

        if (!Hidden.SetEquals(Tab.HiddenAtFirst))
            pairs.Add(("hide", string.Join(",", Tab.Columns.Where(c => Hidden.Contains(c.Name)).Select(c => c.Name))));

        // What the view holds rather than how it is laid out: shrouded, if the link is.
        var open = pairs.Count;
        foreach (var filter in Filter.Columns)
        {
            var name = "f." + filter.Column.Name;
            pairs.AddRange(
                filter.Values.Select(v => (filter.Match == TextMatch.StartsWith ? name + ".starts" : name, v)));
            Add(name + ".min", filter.Min);
            Add(name + ".max", filter.Max);
            Add(name + ".from", filter.From);
            Add(name + ".to", filter.To);
        }

        if (At is { } at) pairs.AddRange(Sort.Keys.Select(k => ("at." + k.Column.Name, Write(k.Column.Value(at)))));

        if (shroud is not null && pairs.Count > open)
        {
            var held = Join(pairs.GetRange(open, pairs.Count - open));
            pairs.RemoveRange(open, pairs.Count - open);
            pairs.Add(("s", shroud.Shroud(held)));
        }

        return Join(pairs);

        static string Join(IEnumerable<(string Key, string Value)> pairs)
        {
            return string.Join("&",
                pairs.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        }

        void Add(string key, object? value)
        {
            if (value is not null) pairs.Add((key, Write(value)));
        }
    }

    /// <summary>
    ///     The view a link's query describes, of one of <paramref name="tabs" /> (the home tab, if it names
    ///     none), its shrouded part, if it has one, opened by <paramref name="shroud" />.
    /// </summary>
    /// <exception cref="ArgumentException">Something in the link is not understood: said what, and nothing of it is taken.</exception>
    public static ViewLink Parse(string query, IReadOnlyList<Tab> tabs, LinkShroud? shroud = null)
    {
        var pairs = QueryHelpers.ParseQuery(query);
        if (pairs.Remove("s", out var shrouded))
        {
            if (shrouded.Count != 1) throw Refuse($"'s' is given {shrouded.Count} times");

            var held = QueryHelpers.ParseQuery(
                (shroud ?? throw Refuse("its shrouded part cannot be opened here")).Unshroud(shrouded.ToString()));
            foreach (var (key, values) in held)
            {
                if (!key.StartsWith("f.", StringComparison.Ordinal) && !key.StartsWith("at.", StringComparison.Ordinal))
                    throw Refuse($"'{key}' is not a filter or the row held at, which alone are shrouded");

                if (!pairs.TryAdd(key, values)) throw Refuse($"'{key}' is given both as is and shrouded");
            }
        }

        var tab = pairs.TryGetValue("tab", out var named)
            ? tabs.FirstOrDefault(t => t.Name == named.ToString()) ?? throw Refuse($"there is no tab '{named}'")
            : tabs.FirstOrDefault(t => t.Home) ?? tabs[0];

        var sort = pairs.TryGetValue("sort", out var sorted)
            ? SortSpec.Of(tab, [
                .. sorted.ToString().Split(',').Select(s => s.StartsWith('-')
                    ? new SortColumn(Named(tab, s[1..]), true)
                    : new SortColumn(Named(tab, s), false))
            ])
            : tab.DefaultSort;

        var hidden = tab.HiddenAtFirst;
        if (pairs.TryGetValue("hide", out var hide))
        {
            var names = hide.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (names.Select(n => Named(tab, n)).FirstOrDefault(c => tab.Columns.ToList().IndexOf(c) < tab.Frozen) is
                { } frozen) throw Refuse($"{frozen.Title} is always shown");

            hidden = names.ToHashSet(StringComparer.Ordinal);
        }

        var filters = new List<ColumnFilter>();
        foreach (var group in pairs.Keys.Where(k => k.StartsWith("f.", StringComparison.Ordinal))
                     .GroupBy(k => k[2..].Split('.')[0]))
        {
            var column = Named(tab, group.Key);
            if (column.Derived)
                throw Refuse($"{column.Title} is worked out from the row and only shown: nothing is filtered by it");

            var filter = new ColumnFilter { Column = column };
            foreach (var key in group)
            {
                var part = key[(2 + group.Key.Length)..];
                var values = pairs[key].Select(v => v ?? "").ToImmutableArray();
                filter = (part, column.Kind) switch
                {
                    ("", ColumnKind.Id or ColumnKind.Text) => filter with { Values = filter.Values.AddRange(values) },
                    ("", ColumnKind.Choice) when values.FirstOrDefault(v => !column.Choices.Contains(v)) is { } other =>
                        throw Refuse($"{column.Title} is never '{other}'"),
                    ("", ColumnKind.Choice) => filter with { Values = filter.Values.AddRange(values) },
                    (".starts", ColumnKind.Text) => filter with
                    {
                        Values = filter.Values.AddRange(values), Match = TextMatch.StartsWith
                    },
                    (".min", ColumnKind.Integer or ColumnKind.Number) => filter with
                    {
                        Min = (double)Read(column, typeof(double), Single(key, values))
                    },
                    (".max", ColumnKind.Integer or ColumnKind.Number) => filter with
                    {
                        Max = (double)Read(column, typeof(double), Single(key, values))
                    },
                    (".from", ColumnKind.Time) => filter with
                    {
                        From = (DateTime)Read(column, typeof(DateTime), Single(key, values))
                    },
                    (".to", ColumnKind.Time) => filter with
                    {
                        To = (DateTime)Read(column, typeof(DateTime), Single(key, values))
                    },
                    _ => throw Refuse($"{column.Title} has no filter '{key}'")
                };
            }

            if (filter.Match == TextMatch.StartsWith && pairs.ContainsKey("f." + group.Key))
                throw Refuse($"{column.Title} is filtered by values and by what they start with at once");

            filters.Add(filter);
        }

        var filtered = FilterSpec.Of(filters);
        IRow? at = null;
        var given = pairs.Keys.Where(k => k.StartsWith("at.", StringComparison.Ordinal)).ToList();
        if (given.Count > 0)
        {
            var keys = sort.Keys.Select(k => "at." + k.Column.Name).ToList();
            if (!given.ToHashSet().SetEquals(keys))
                throw Refuse(
                    $"the row held at is given by {string.Join(", ", given)}, where this sort needs {string.Join(", ", keys)}");

            // Only the keyset's values: each column reads its own from it, whatever the tab's rows are.
            at = new KeyRow(tab, sort.Keys.ToDictionary(
                k => k.Column.Name,
                k => Read(k.Column, k.Column.Type,
                    Single("at." + k.Column.Name, [.. pairs["at." + k.Column.Name].Select(v => v ?? "")]))));
        }

        if (pairs.Keys.FirstOrDefault(k =>
                k is not ("tab" or "sort" or "hide") && !k.StartsWith("f.", StringComparison.Ordinal) &&
                !k.StartsWith("at.", StringComparison.Ordinal)) is
            { } unknown) throw Refuse($"'{unknown}' is not part of a view");

        return new ViewLink(tab, sort, filtered, hidden, at);
    }

    private static Column Named(Tab tab, string name)
    {
        return tab.Columns.FirstOrDefault(c => c.Name == name) ?? throw Refuse($"{tab.Title} has no column '{name}'");
    }

    private static string Single(string key, ImmutableArray<string> values)
    {
        return values.Length == 1 ? values[0] : throw Refuse($"'{key}' is given {values.Length} times");
    }

    private static ArgumentException Refuse(string why)
    {
        return new ArgumentException($"the link is not a view of the blotter: {why}");
    }

    private static string Write(object value)
    {
        return value switch
        {
            DateTime time => time.ToString(TimeFormat, CultureInfo.InvariantCulture),
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            IFormattable other => other.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
    }

    /// <summary>A value of <paramref name="column" />, as <paramref name="type" />, from the link.</summary>
    private static object Read(Column column, Type type, string text)
    {
        var invariant = CultureInfo.InvariantCulture;
        object? value = type switch
        {
            _ when type == typeof(Guid) => Guid.TryParse(text, out var id) ? id : null,
            _ when type == typeof(int) => int.TryParse(text, NumberStyles.Integer, invariant, out var i) ? i : null,
            _ when type == typeof(long) => long.TryParse(text, NumberStyles.Integer, invariant, out var l) ? l : null,
            _ when type == typeof(double) => double.TryParse(text, NumberStyles.Float, invariant, out var d) ? d : null,
            _ when type == typeof(DateTime) => DateTime.TryParseExact(text, TimeFormat, invariant, DateTimeStyles.None,
                out var t)
                ? Cells.Micros(t)
                : null,
            _ => text
        };
        return value ?? throw Refuse($"'{text}' is not a value of {column.Title}");
    }
}