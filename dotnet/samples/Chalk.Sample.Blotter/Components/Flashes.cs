namespace Chalk.Sample.Blotter.Components;

/// <summary>One row as the grid shows it: the trade, and which of its cells are flashing.</summary>
public sealed class BlotterRow(TradeVersion trade, string?[] flash, string? rowClass)
{
    public TradeVersion Trade { get; } = trade;

    /// <summary>Per column, in <see cref="Column.All"/>'s order: a flash class, or null.</summary>
    public string?[] Flash { get; } = flash;

    /// <summary>The row's own class: a new trade flashes whole.</summary>
    public string? RowClass { get; } = rowClass;
}

/// <summary>
/// What one page has shown, so it can tell what changed: a cell flashes when a newer version of
/// a trade it was showing has a different value there, and a row flashes when a trade the feed
/// has just opened appears. A trade scrolled into view is not new, and does not flash.
/// </summary>
/// <remarks>
/// A flash is a class with two names for one animation. A cell that changes again while it is still
/// flashing swaps to the other name, which starts the animation over; a cell that does not change
/// keeps its class until the flash is over, so a render in between does not start it again.
/// </remarks>
public sealed class Flashes
{
    private static readonly TimeSpan CellFlash = TimeSpan.FromMilliseconds(1600);
    private static readonly TimeSpan RowFlash = TimeSpan.FromMilliseconds(2000);

    private readonly Dictionary<long, Shown> _shown = [];
    private DateTime _since = DateTime.MaxValue;

    /// <summary>Forgets what was shown: after a new sort or filter, nothing that appears has changed.</summary>
    public void Clear()
    {
        _shown.Clear();
        _since = DateTime.MaxValue;
    }

    /// <summary>
    /// Decorates a page read from a state that covers the feed through <paramref name="ingestedThrough"/>.
    /// A trade opened after the previous page's state was read is new.
    /// </summary>
    public BlotterRow[] Decorate(IReadOnlyList<TradeVersion> page, DateTime ingestedThrough, DateTime now)
    {
        var rows = new BlotterRow[page.Count];
        var columns = Column.All;
        for (var r = 0; r < page.Count; r++)
        {
            var trade = page[r];
            if (!_shown.TryGetValue(trade.TradeId, out var shown))
            {
                var opened = trade.Version == 1 && trade.EventTime > _since;
                shown = new Shown(trade, new string?[columns.Count], new DateTime[columns.Count])
                {
                    RowClass = opened ? "new-a" : null,
                    RowUntil = opened ? now + RowFlash : default,
                };
                _shown[trade.TradeId] = shown;
            }
            else if (shown.Trade.Version < trade.Version)
            {
                for (var c = 0; c < columns.Count; c++)
                {
                    if (columns[c].Show(shown.Trade) != columns[c].Show(trade))
                    {
                        shown.Flash[c] = shown.Flash[c] == "flash-a" && shown.Until[c] > now ? "flash-b" : "flash-a";
                        shown.Until[c] = now + CellFlash;
                    }
                }

                shown.Trade = trade;
            }

            shown.Seen = now;
            var flash = new string?[columns.Count];
            for (var c = 0; c < columns.Count; c++)
            {
                flash[c] = shown.Until[c] > now ? shown.Flash[c] : null;
            }

            rows[r] = new BlotterRow(trade, flash, shown.RowUntil > now ? shown.RowClass : null);
        }

        _since = ingestedThrough;
        if (_shown.Count > 4096)
        {
            foreach (var stale in _shown.Where(s => now - s.Value.Seen > TimeSpan.FromSeconds(10)).Select(s => s.Key).ToList())
            {
                _shown.Remove(stale);
            }
        }

        return rows;
    }

    private sealed class Shown(TradeVersion trade, string?[] flash, DateTime[] until)
    {
        public TradeVersion Trade { get; set; } = trade;

        public string?[] Flash { get; } = flash;

        public DateTime[] Until { get; } = until;

        public string? RowClass { get; init; }

        public DateTime RowUntil { get; init; }

        public DateTime Seen { get; set; }
    }
}
