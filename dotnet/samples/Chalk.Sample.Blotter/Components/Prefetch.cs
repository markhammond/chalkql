namespace Chalk.Sample.Blotter.Components;

/// <summary>
///     How many rows the grid renders beyond the view, each side, from how long its pages take to be
///     drawn: a view's worth on a fast link, down to a quarter of a view on a slow one, besides the
///     view's worth each side that QuickGrid's observers watch ahead of the reader (blotter.js), which
///     is what has it ask for rows before the reader reaches them. This only sets how often it asks,
///     and how large each page is: at the live top every row rendered is new with nearly every page, so
///     a page costs about as much as the rows it renders, and on a slow link the rows nobody sees cost
///     the most. It starts at a quarter of a view, since the first pages are the heaviest a browser is
///     sent, and grows as pages prove the link fast. A view is the rows the page says it shows, 20
///     until it says.
/// </summary>
/// <remarks>
///     A page's time runs from its leaving the server to the browser acknowledging that it has drawn it:
///     the link and the browser, not the database. The grid steps down once the middle of its last three
///     pages took more than half a second, and up once it took less than 150 ms; between the two it stays.
///     <para>
///         After a step, the first page is not judged, since it carries the step's own change, and three
///         pages at the new size decide the next. A size left for being slow is not tried again for 30 s,
///         and for twice as long each time it is left again, up to five minutes: a link that can just
///         carry the smaller size and not the larger does not swing between them.
///     </para>
/// </remarks>
public sealed class Prefetch(TimeProvider time)
{
    /// <summary>The sizes, in views, largest first.</summary>
    public static readonly IReadOnlyList<double> Steps = [1, 0.5, 0.25];

    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan FirstHoldBack = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LongestHoldBack = TimeSpan.FromMinutes(5);

    private const int Judged = 3;
    private readonly Queue<TimeSpan> _taken = new(Judged);

    // For each size, when it may be tried again, and how long it is held back the next time it is left.
    private readonly DateTimeOffset[] _heldBackUntil = new DateTimeOffset[Steps.Count];
    private readonly TimeSpan[] _holdBack = [.. Steps.Select(_ => FirstHoldBack)];
    private bool _settling;
    private int _step = Steps.Count - 1;

    /// <summary>The rows in view.</summary>
    public int View { get; private set; } = 20;

    /// <summary>Rows rendered beyond the view, each side.</summary>
    public int Rows => (int)Math.Ceiling(Steps[_step] * View);

    /// <summary>The page shows so many rows now; true if <see cref="Rows" /> changed with them.</summary>
    public bool See(int rows)
    {
        if (rows < 1 || rows == View)
        {
            return false;
        }

        View = rows;
        return true;
    }

    /// <summary>
    ///     Counts how long a page took to be drawn; true if <see cref="Rows" /> changed, with
    ///     <paramref name="judged" /> the middle time of the pages that moved it.
    /// </summary>
    public bool Observe(TimeSpan took, out TimeSpan judged)
    {
        judged = default;
        if (_settling)
        {
            _settling = false;
            return false;
        }

        _taken.Enqueue(took);
        if (_taken.Count < Judged)
        {
            return false;
        }

        judged = _taken.Order().ElementAt(Judged / 2);
        _taken.Dequeue();
        var now = time.GetUtcNow();
        var step = _step;
        if (judged > Slow && _step < Steps.Count - 1)
        {
            // Too slow: this size is held back, for longer each time it is left.
            _heldBackUntil[_step] = now + _holdBack[_step];
            _holdBack[_step] = TimeSpan.FromTicks(Math.Min(_holdBack[_step].Ticks * 2, LongestHoldBack.Ticks));
            step = _step + 1;
        }
        else if (judged < Fast && _step > 0 && now >= _heldBackUntil[_step - 1])
        {
            step = _step - 1;
        }

        if (step == _step)
        {
            return false;
        }

        _step = step;
        _taken.Clear();
        _settling = true;
        return true;
    }
}
