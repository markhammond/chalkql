namespace Chalk.Sample.Blotter.Components;

/// <summary>An order opened to show its fills: where it stands among the tab's rows, and its fills.</summary>
/// <param name="Position">The order's position among the rows the grid shows, before any fills.</param>
public sealed record Opened(long Position, Order Order, Fill[] Fills);

/// <summary>A row of the grid: one of the tab's rows by position, or one of an opened order's fills.</summary>
public readonly record struct Slot(long Position, Opened? Under, int Fill)
{
    public bool IsFill => Under is not null;
}

/// <summary>
///     Fills nested under their orders, as rows of their own in the grid's one list. QuickGrid's
///     virtualizing list asks for rows by position and wants every row one height, so a fill is a row
///     like an order, and each opened order moves the rows below it down by its fills.
/// </summary>
/// <remarks>
///     The grid's positions are the tab's positions with the opened orders' fills let in after each.
///     With the opened orders in order of position, a grid position is the tab's position less every fill
///     let in before it; or, when it falls among an order's fills, that order's fill.
/// </remarks>
public static class Nesting
{
    /// <summary>How many rows the grid has: the tab's, and the fills of the opened orders among them.</summary>
    public static long Total(long rows, IReadOnlyList<Opened> opened) => rows + opened.Where(o => o.Position >= 0 && o.Position < rows).Sum(o => (long)o.Fills.Length);

    /// <summary>The grid's rows <paramref name="start" /> to <paramref name="start" /> + <paramref name="count" />, as slots.</summary>
    /// <param name="opened">The opened orders in view, in order of position.</param>
    public static List<Slot> Map(long start, int count, long rows, IReadOnlyList<Opened> opened)
    {
        var slots = new List<Slot>(count);
        var total = Total(rows, opened);
        for (var at = start; at < Math.Min(total, start + count); at++) slots.Add(Locate(at, opened));

        return slots;
    }

    /// <summary>The slot at grid position <paramref name="at" />.</summary>
    public static Slot Locate(long at, IReadOnlyList<Opened> opened)
    {
        var before = 0L;
        foreach (var order in opened)
        {
            if (order.Position < 0) continue;

            var shown = order.Position + before;
            if (at <= shown) break;

            if (at <= shown + order.Fills.Length) return new Slot(order.Position, order, (int)(at - shown - 1));

            before += order.Fills.Length;
        }

        return new Slot(at - before, null, 0);
    }

    /// <summary>The grid position of the tab's row at <paramref name="position" />.</summary>
    public static long GridPosition(long position, IReadOnlyList<Opened> opened) => position + opened.Where(o => o.Position >= 0 && o.Position < position).Sum(o => (long)o.Fills.Length);
}