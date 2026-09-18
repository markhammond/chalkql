using Chalk.Sources;

namespace Chalk.Execution.Aggregation;

/// <summary>
/// The aggregate's per-group state — group key stores and measure accumulators — grows with the
/// number of groups, which is a property of the data and not of the plan. This is the lifecycle that
/// keeps it in the execution's arena rather than in the operator (ADR 0012): claim an arena for the
/// run, grow through it, and hand everything back in the run's <c>finally</c>.
/// </summary>
internal abstract class ArenaScratch
{
    private ExecutionArena? _arena;

    /// <summary>Starts a run: nothing carries over, so a group id from the last one cannot be read.</summary>
    public void Begin(ExecutionArena arena)
    {
        Release();
        _arena = arena;
        BeginCore(arena);
    }

    /// <summary>
    /// Attaches whatever else this state owns to the same arena — a holistic aggregate's value store,
    /// which is an <see cref="ArenaScratch"/> of its own (D57).
    /// </summary>
    protected virtual void BeginCore(ExecutionArena arena)
    {
    }

    /// <summary>Hands every rented array back. Idempotent, and safe before the first <see cref="Begin"/>.</summary>
    public void Release()
    {
        if (_arena is not null)
        {
            ReleaseCore();
            _arena = null;
        }
    }

    /// <summary>Returns this object's rentals; the base class has already checked there are any.</summary>
    protected abstract void ReleaseCore();

    private ExecutionArena Arena => _arena
        ?? throw new InvalidOperationException("this aggregate state is not attached to an execution.");

    /// <summary>
    /// Grows a rental to at least <paramref name="minimum"/>, keeping what is in it and zeroing the
    /// rest — <see cref="Array.Resize{T}"/>'s contract, which every caller here depends on, over a
    /// pool that hands out whatever bytes were there before.
    /// </summary>
    protected T[] Grow<T>(T[] array, int minimum)
        where T : struct
    {
        var grown = Arena.Rent<T>(Math.Max(minimum, Math.Max(16, array.Length * 2)));
        Array.Copy(array, grown, array.Length);
        Array.Clear(grown, array.Length, grown.Length - array.Length);
        Arena.Return(array);
        return grown;
    }

    /// <summary>Returns one rental and forgets it.</summary>
    protected void Give<T>(ref T[] array)
        where T : struct
    {
        Arena.Return(array);
        array = [];
    }
}
