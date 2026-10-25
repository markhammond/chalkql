namespace Chalk.Sample.Blotter.Components;

/// <summary>
///     How many rows a held grid shows in all: kept as it was while the grid holds its place, so the scroll
///     bar stays put and a page with nothing new in view renders nothing.
/// </summary>
/// <remarks>
///     Held, the database's estimate moves between counts and the tail ebbs and flows with every refresh,
///     so the total worked out afresh moves by a few rows with nearly every page. It is taken again only
///     when it has drifted by more than 1%, when a read learns where the rows end (<see cref="Set" />),
///     or when rows are let in above (<see cref="Shift" />). Opened orders' fills are counted on top of it,
///     so opening or closing one still moves what the grid shows.
/// </remarks>
public sealed class HeldTotal
{
    private long? _held;

    /// <summary>The total to show, given the total worked out afresh: the one held, unless it drifted past 1%.</summary>
    public long Hold(long rows)
    {
        if (_held is { } held && Math.Abs(rows - held) <= held / 100.0)
        {
            return held;
        }

        _held = rows;
        return rows;
    }

    /// <summary>A read learned where the rows end: that is the total now.</summary>
    public void Set(long rows) => _held = rows;

    /// <summary>Rows were let in above: the total grows by as many.</summary>
    public void Shift(long rows) => _held += rows;

    /// <summary>The grid follows the live top again, or holds a new place: the next total is taken afresh.</summary>
    public void Clear() => _held = null;
}
