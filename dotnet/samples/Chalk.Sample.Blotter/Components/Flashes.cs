namespace Chalk.Sample.Blotter.Components;

/// <summary>
/// One row as the grid shows it: an order or a fill of the tab, or a fill nested under its order, and
/// which of its cells are flashing.
/// </summary>
public sealed class BlotterRow(IRow row, string?[] flash, string? rowClass, Guid? parent = null)
{
    public IRow Row { get; } = row;

    /// <summary>Per column of the tab, in its order: a flash class, or null.</summary>
    public string?[] Flash { get; } = flash;

    /// <summary>The row's own class: a new order or fill flashes whole.</summary>
    public string? RowClass { get; } = rowClass;

    /// <summary>For a fill nested under its order, the order's id.</summary>
    public Guid? Parent { get; } = parent;

    /// <summary>The grid's key: an order and a fill never share one.</summary>
    public string Key => Parent is null ? $"r{Row.Id}" : $"f{Row.Id}";
}

/// <summary>
/// What one grid has shown, so it can tell what changed: a cell flashes when a newer version of a row
/// it was showing has a different value there, and a row flashes when the feed has just sent it, as a
/// new order or a new fill. A row scrolled into view is not new, and does not flash.
/// </summary>
/// <remarks>
/// A flash is a class with two names for one animation. A cell that changes again while it is still
/// flashing swaps to the other name, which starts the animation over; a cell that does not change keeps
/// its class until the flash is over, so a render in between does not start it again.
/// </remarks>
public sealed class Flashes(IReadOnlyList<Column> columns)
{
    private static readonly TimeSpan CellFlash = TimeSpan.FromMilliseconds(1600);
    private static readonly TimeSpan RowFlash = TimeSpan.FromMilliseconds(2000);

    private readonly Dictionary<Guid, Shown> _shown = [];
    private DateTime _since = DateTime.MaxValue;

    /// <summary>Forgets what was shown: after a new sort or filter, nothing that appears has changed.</summary>
    public void Clear()
    {
        _shown.Clear();
        _since = DateTime.MaxValue;
    }

    /// <summary>
    /// Decorates a page read from a state that covers the feed through <paramref name="ingestedThrough"/>.
    /// A row the feed sent after the previous page's state was read is new.
    /// </summary>
    public BlotterRow[] Decorate(IReadOnlyList<IRow> page, DateTime ingestedThrough, DateTime now, Guid? parent = null)
    {
        var rows = new BlotterRow[page.Count];
        for (var r = 0; r < page.Count; r++)
        {
            rows[r] = Decorate(page[r], now, parent);
        }

        Seen(ingestedThrough, now);
        return rows;
    }

    /// <summary>One row; call <see cref="Seen"/> once the page is done.</summary>
    public BlotterRow Decorate(IRow row, DateTime now, Guid? parent = null)
    {
        if (!_shown.TryGetValue(row.Id, out var shown))
        {
            var opened = row.Version == 1 && row.EventTime > _since;
            shown = new Shown(row, new string?[columns.Count], new DateTime[columns.Count])
            {
                RowClass = opened ? "new-a" : null,
                RowUntil = opened ? now + RowFlash : default,
            };
            _shown[row.Id] = shown;
        }
        else if (shown.Row.Version < row.Version)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                if (columns[c].Show(shown.Row) != columns[c].Show(row))
                {
                    shown.Flash[c] = shown.Flash[c] == "flash-a" && shown.Until[c] > now ? "flash-b" : "flash-a";
                    shown.Until[c] = now + CellFlash;
                }
            }

            shown.Row = row;
        }

        shown.Seen = now;
        var flash = new string?[columns.Count];
        for (var c = 0; c < columns.Count; c++)
        {
            flash[c] = shown.Until[c] > now ? shown.Flash[c] : null;
        }

        return new BlotterRow(row, flash, shown.RowUntil > now ? shown.RowClass : null, parent);
    }

    /// <summary>The page is done: rows the feed sends after <paramref name="ingestedThrough"/> are new to the next.</summary>
    public void Seen(DateTime ingestedThrough, DateTime now)
    {
        _since = ingestedThrough;
        if (_shown.Count > 4096)
        {
            foreach (var stale in _shown.Where(s => now - s.Value.Seen > TimeSpan.FromSeconds(10)).Select(s => s.Key).ToList())
            {
                _shown.Remove(stale);
            }
        }
    }

    private sealed class Shown(IRow row, string?[] flash, DateTime[] until)
    {
        public IRow Row { get; set; } = row;

        public string?[] Flash { get; } = flash;

        public DateTime[] Until { get; } = until;

        public string? RowClass { get; init; }

        public DateTime RowUntil { get; init; }

        public DateTime Seen { get; set; }
    }
}
