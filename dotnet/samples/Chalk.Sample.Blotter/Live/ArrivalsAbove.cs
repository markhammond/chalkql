using System.Collections.Concurrent;
using Chalk.Sample.Blotter.Trades;

namespace Chalk.Sample.Blotter.Live;

/// <summary>
/// Counts what arrives above a held view's top row from the streams, rather than by counting the
/// database: every order version and fill reaches the host through them, so every move across the top
/// row passes this way. Each is weighed against what that row was before: one more above the top row
/// if it now sorts above it, passing the filter, and did not; one fewer the other way.
/// </summary>
/// <remarks>
/// <para>
/// The streams, not the tail: the tail does not take a version the database holds already, which,
/// written promptly, it often does. What a row was is what this last saw of it: the tail's rows when
/// the view is held, then every version the streams send. A row not seen yet was in the database, as
/// it was when the view was held: an order at its latest version sent by then, looked up by key in
/// <c>order_versions</c>; an order placed since, or a fill, is new.
/// </para>
/// <para>
/// The top row is the view's topmost known row at each count, as letting rows in moves it up: a row
/// between it and the last one counted was counted when it crossed, or before it was let in.
/// </para>
/// </remarks>
public sealed class ArrivalsAbove : IDisposable
{
    // Past this many versions waiting, the count is given up rather than hold them all: the reader is gone.
    private const int MostWaiting = 200_000;

    private readonly SortSpec _order;
    private readonly FilterSpec _filter;
    private readonly BlotterHost _host;
    private readonly ConcurrentQueue<IRow> _arrived = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, IRow> _seen = [];
    private readonly DateTime _since;

    /// <summary>
    /// Starts counting at <paramref name="state"/>, the state the view was held at: what is above the top
    /// row then is placed. Notes the streams from before taking the state, so that nothing falls between.
    /// </summary>
    public ArrivalsAbove(BlotterHost host, SortSpec order, Func<ScopeState> state)
    {
        (_host, _order) = (host, order);
        host.Arrived += Note;
        var held = state();
        _filter = held.Filter;
        _since = held.OrdersThrough;
        foreach (var (id, row) in held.Tail)
        {
            _seen[id] = row;
        }
    }

    /// <summary>Whether the count was given up, its reader having stopped taking it.</summary>
    public bool GaveUp { get; private set; }

    /// <summary>How many more rows sort above <paramref name="top"/> than at the last count; fewer, if negative.</summary>
    public async Task<long> TakeAsync(IRow top, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var arrived = new List<IRow>();
            while (_arrived.TryDequeue(out var row))
            {
                arrived.Add(row);
            }

            // Orders not seen since the view was held, nor placed since: as they were then.
            var unknown = arrived.OfType<Order>()
                .Where(o => !_seen.ContainsKey(o.OrderId) && o.OrderTime <= _since)
                .Select(o => o.OrderId).Distinct().ToList();
            var then = unknown.Count == 0 ? [] : await _host.Queries.OrdersAsOfAsync(unknown, _since, ct).ConfigureAwait(false);

            long moved = 0;
            foreach (var row in arrived)
            {
                var was = _seen.TryGetValue(row.Id, out var seen) ? seen : row is Order o ? then.GetValueOrDefault(o.OrderId) : null;
                if (was is not null && was.Version >= row.Version)
                {
                    // Seen already, or older than what was: a version sent again changes nothing.
                    continue;
                }

                moved += Above(row, top) - (was is null ? 0 : Above(was, top));
                _seen[row.Id] = row;
            }

            return moved;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _host.Arrived -= Note;

    private void Note(IReadOnlyList<Order> versions, IReadOnlyList<Fill> fills)
    {
        if (_arrived.Count > MostWaiting)
        {
            GaveUp = true;
            _host.Arrived -= Note;
            return;
        }

        // The tab's own rows: orders, or fills.
        foreach (var row in _order.Tab.Overrides ? (IEnumerable<IRow>)versions : fills)
        {
            _arrived.Enqueue(row);
        }
    }

    private int Above(IRow row, IRow top) =>
        _filter.Columns.All(f => f.Matches(row)) && _order.Compare(row, top) < 0 ? 1 : 0;
}
