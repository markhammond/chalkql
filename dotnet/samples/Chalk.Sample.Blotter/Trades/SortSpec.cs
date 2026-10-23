using System.Collections.Immutable;

namespace Chalk.Sample.Blotter.Trades;

/// <summary>One column of a sort, and its direction.</summary>
public readonly record struct SortColumn(Column Column, bool Descending);

/// <summary>
/// A tab's order: the columns the user chose, then the tab's key, so that no two rows ever tie. A
/// total order is what lets a page be found by position in the database and placed by comparison
/// in the host and land in the same place either way.
/// </summary>
public sealed class SortSpec : IComparer<IRow>, IEquatable<SortSpec>
{
    private SortSpec(Tab tab, ImmutableArray<SortColumn> chosen)
    {
        Tab = tab;
        Chosen = chosen;
        var keys = chosen.ToBuilder();
        if (!chosen.Any(k => k.Column == tab.Key))
        {
            // Last among ties first: a version 7 UUID starts with its time, so the newest, mostly.
            keys.Add(new SortColumn(tab.Key, Descending: true));
        }

        Keys = keys.ToImmutable();
        Name = string.Join(",", Keys.Select(k => k.Column.Name + (k.Descending ? " desc" : "")));
    }

    public Tab Tab { get; }

    /// <summary>The columns the user chose, in the order they chose them.</summary>
    public ImmutableArray<SortColumn> Chosen { get; }

    /// <summary>The whole order: <see cref="Chosen"/>, then the tab's key when it is not among them.</summary>
    public ImmutableArray<SortColumn> Keys { get; }

    /// <summary>Canonical, for caches and logs: <c>client,order_id desc</c>.</summary>
    public string Name { get; }

    public static SortSpec Of(Tab tab, params SortColumn[] keys) => new(tab, [.. keys]);

    /// <summary>
    /// A header click. A plain click sorts by that column alone, and a second plain click on the
    /// only column sorted by turns it round. A shift-click adds the column to the sort, turns it round
    /// if it is there ascending, and takes it out if it is there descending.
    /// </summary>
    public SortSpec Click(Column column, bool shift)
    {
        var at = Chosen.IndexOf(Chosen.FirstOrDefault(k => k.Column == column));
        if (!shift)
        {
            var descending = Chosen.Length == 1 && at == 0 && !Chosen[0].Descending;
            return new SortSpec(Tab, [new SortColumn(column, descending)]);
        }

        if (at < 0)
        {
            return new SortSpec(Tab, Chosen.Add(new SortColumn(column, Descending: false)));
        }

        if (!Chosen[at].Descending)
        {
            return new SortSpec(Tab, Chosen.SetItem(at, new SortColumn(column, Descending: true)));
        }

        var fewer = Chosen.RemoveAt(at);
        return fewer.IsEmpty ? Tab.DefaultSort : new SortSpec(Tab, fewer);
    }

    /// <summary>Where <paramref name="column"/> stands in the user's sort: its rank from 1, or 0.</summary>
    public int RankOf(Column column)
    {
        for (var i = 0; i < Chosen.Length; i++)
        {
            if (Chosen[i].Column == column)
            {
                return i + 1;
            }
        }

        return 0;
    }

    public bool IsDescending(Column column) => Chosen.Any(k => k.Column == column && k.Descending);

    /// <summary>The <c>ORDER BY</c> list.</summary>
    public string OrderBy() =>
        string.Join(", ", Keys.Select(k => k.Column.Name + (k.Descending ? " DESC" : " ASC")));

    public int Compare(IRow? x, IRow? y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        foreach (var key in Keys)
        {
            var order = key.Column.Compare(x, y);
            if (order != 0)
            {
                return key.Descending ? -order : order;
            }
        }

        return 0;
    }

    public bool Equals(SortSpec? other) => other is not null && other.Tab == Tab && other.Name == Name;

    public override bool Equals(object? obj) => Equals(obj as SortSpec);

    public override int GetHashCode() => HashCode.Combine(Tab.Name, Name);

    public override string ToString() => Name;
}
