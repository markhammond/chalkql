using System.Collections.Immutable;

namespace Chalk.Sample.Blotter.Trades;

/// <summary>One column of a sort, and its direction.</summary>
public readonly record struct SortColumn(Column Column, bool Descending);

/// <summary>
/// The grid's order: the columns the user chose, then the trade id, so that no two rows ever tie.
/// A total order is what lets a page be found by position in the database and placed by
/// comparison in the host and land in the same place either way.
/// </summary>
public sealed class SortSpec : IComparer<TradeVersion>, IEquatable<SortSpec>
{
    private SortSpec(ImmutableArray<SortColumn> chosen)
    {
        Chosen = chosen;
        var keys = chosen.ToBuilder();
        if (!chosen.Any(k => k.Column == Column.TradeId))
        {
            // Newest first among ties: trade ids are handed out in order.
            keys.Add(new SortColumn(Column.TradeId, Descending: true));
        }

        Keys = keys.ToImmutable();
        Name = string.Join(",", Keys.Select(k => k.Column.Name + (k.Descending ? " desc" : "")));
    }

    /// <summary>The newest trades first, which is what a blotter opens on.</summary>
    public static SortSpec Default { get; } = new([new SortColumn(Column.TradeTime, Descending: true)]);

    /// <summary>The columns the user chose, in the order they chose them.</summary>
    public ImmutableArray<SortColumn> Chosen { get; }

    /// <summary>The whole order: <see cref="Chosen"/>, then the trade id when it is not among them.</summary>
    public ImmutableArray<SortColumn> Keys { get; }

    /// <summary>Canonical, for caches and logs: <c>symbol,trade_id desc</c>.</summary>
    public string Name { get; }

    public static SortSpec Of(params SortColumn[] keys) => new([.. keys]);

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
            return new SortSpec([new SortColumn(column, descending)]);
        }

        if (at < 0)
        {
            return new SortSpec(Chosen.Add(new SortColumn(column, Descending: false)));
        }

        if (!Chosen[at].Descending)
        {
            return new SortSpec(Chosen.SetItem(at, new SortColumn(column, Descending: true)));
        }

        var fewer = Chosen.RemoveAt(at);
        return fewer.IsEmpty ? Default : new SortSpec(fewer);
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

    /// <summary>The <c>ORDER BY</c> list, over columns named with <paramref name="prefix"/>.</summary>
    public string OrderBy(string prefix = "") =>
        string.Join(", ", Keys.Select(k => prefix + k.Column.Name + (k.Descending ? " DESC" : " ASC")));

    public int Compare(TradeVersion? x, TradeVersion? y)
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

    public bool Equals(SortSpec? other) => other is not null && other.Name == Name;

    public override bool Equals(object? obj) => Equals(obj as SortSpec);

    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Name;
}
