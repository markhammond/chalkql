using System.Collections.Immutable;

namespace Chalk.Sample.Blotter.Grid;

/// <summary>One column of a sort, and its direction.</summary>
public readonly record struct SortColumn(Column Column, bool Descending);

/// <summary>
///     A tab's order: the columns the user chose, then the tab's key, so that no two rows ever tie. A
///     total order is what lets a page be found by position in the database and placed by comparison
///     in the host and land in the same place either way.
/// </summary>
public sealed class SortSpec : IComparer<IRow>, IEquatable<SortSpec>
{
    private SortSpec(Tab tab, ImmutableArray<SortColumn> chosen)
    {
        if (chosen.FirstOrDefault(k => k.Column.Derived).Column is { } shown)
            throw new ArgumentException(
                $"{shown.Title} is worked out from the row and only shown: nothing is sorted by it", nameof(chosen));

        Tab = tab;
        Chosen = chosen;
        var keys = chosen.ToBuilder();
        if (!chosen.Any(k => k.Column == tab.Key))
            // Last among ties first: a version 7 UUID starts with its time, so the newest, mostly.
            keys.Add(new SortColumn(tab.Key, true));

        Keys = keys.ToImmutable();
        Name = string.Join(",", Keys.Select(k => k.Column.Name + (k.Descending ? " desc" : "")));
    }

    private SortSpec(Tab tab, ImmutableArray<SortColumn> chosen, ImmutableArray<SortColumn> keys)
    {
        Tab = tab;
        Chosen = chosen;
        Keys = keys;
        Name = string.Join(",", Keys.Select(k => k.Column.Name + (k.Descending ? " desc" : "")));
    }

    public Tab Tab { get; }

    /// <summary>The columns the user chose, in the order they chose them.</summary>
    public ImmutableArray<SortColumn> Chosen { get; }

    /// <summary>The whole order: <see cref="Chosen" />, then the tab's key when it is not among them.</summary>
    public ImmutableArray<SortColumn> Keys { get; }

    /// <summary>Canonical, for caches and logs: <c>client,order_id desc</c>.</summary>
    public string Name { get; }

    /// <summary>
    ///     The tab's own order, newest first, which the user has not chosen: no column ranks as sorted, the
    ///     time column shows its arrow muted, and a click on it sorts ascending, as it is descending already.
    /// </summary>
    public bool IsDefault { get; private init; }

    public int Compare(IRow? x, IRow? y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        foreach (var key in Keys)
        {
            var order = key.Column.Compare(x, y);
            if (order != 0) return key.Descending ? -order : order;
        }

        return 0;
    }

    public bool Equals(SortSpec? other)
    {
        return other is not null && other.Tab == Tab && other.Name == Name;
    }

    public static SortSpec Of(Tab tab, params SortColumn[] keys)
    {
        return new SortSpec(tab, [.. keys]);
    }

    /// <summary>The tab's own order: by its time, newest first.</summary>
    public static SortSpec Default(Tab tab)
    {
        return new SortSpec(tab, [new SortColumn(tab.Time, true)]) { IsDefault = true };
    }

    /// <summary>
    ///     A header click, which cycles its column through descending, ascending and out of the sort. A
    ///     plain click sorts by that column alone; a shift-click keeps the other columns, and adds this one
    ///     last. Taking out the last column goes back to the tab's own order, newest first, in which no
    ///     column is chosen.
    /// </summary>
    public SortSpec Click(Column column, bool shift)
    {
        if (IsDefault && column == Tab.Time) return new SortSpec(Tab, [new SortColumn(column, false)]);

        var chosen = IsDefault ? [] : Chosen;
        var at = chosen.IndexOf(chosen.FirstOrDefault(k => k.Column == column));
        if (!shift && (at < 0 || chosen.Length > 1)) return new SortSpec(Tab, [new SortColumn(column, true)]);

        if (at < 0) return new SortSpec(Tab, chosen.Add(new SortColumn(column, true)));

        if (chosen[at].Descending) return new SortSpec(Tab, chosen.SetItem(at, new SortColumn(column, false)));

        var fewer = chosen.RemoveAt(at);
        return fewer.IsEmpty ? Tab.DefaultSort : new SortSpec(Tab, fewer);
    }

    /// <summary>Where <paramref name="column" /> stands in the user's sort: its rank from 1, or 0; 0 in the tab's own order.</summary>
    public int RankOf(Column column)
    {
        for (var i = 0; !IsDefault && i < Chosen.Length; i++)
            if (Chosen[i].Column == column)
                return i + 1;

        return 0;
    }

    public bool IsDescending(Column column)
    {
        return !IsDefault && Chosen.Any(k => k.Column == column && k.Descending);
    }

    /// <summary>Whether <paramref name="column" /> is what the tab's own order sorts by: shown, but muted, as not chosen.</summary>
    public bool ByDefault(Column column)
    {
        return IsDefault && column == Tab.Time;
    }

    /// <summary>Every key turned round: read from a row in this order, the rows before it, nearest first.</summary>
    public SortSpec Reversed()
    {
        return new SortSpec(
            Tab, [.. Chosen.Select(k => k with { Descending = !k.Descending })],
            [.. Keys.Select(k => k with { Descending = !k.Descending })]);
    }

    /// <summary>The <c>ORDER BY</c> list.</summary>
    public string OrderBy()
    {
        return string.Join(", ", Keys.Select(k => k.Column.Name + (k.Descending ? " DESC" : " ASC")));
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as SortSpec);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Tab.Name, Name);
    }

    public override string ToString()
    {
        return Name;
    }
}