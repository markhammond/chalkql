namespace Chalk.Tests;

/// <summary>What happened inside a measured window that the reading has to answer for.</summary>
/// <remarks>
/// Shared by every test project with an allocation gate, and linked into each rather than given a
/// home in <c>Chalk.TestKit</c>: it depends on nothing but xunit, and the projects that need it —
/// <c>Chalk.Sources.Poco.Tests</c> among them — deliberately reference one Chalk assembly apiece.
/// </remarks>
internal enum Disturbance
{
    /// <summary>Nothing; the reading is the work's own allocation, to the byte.</summary>
    None,

    /// <summary>
    /// A collection ran inside the window, which can only have charged the thread for memory it
    /// never used — never the other way about. The reading is an upper bound, not a number.
    /// </summary>
    /// <remarks>
    /// Its absence proves nothing: the ephemeral pause that begins a background collection retires
    /// allocation contexts too, and the count it belongs to does not move until that collection
    /// ends, which may be long after the window closed. Observed here as 4 392 phantom bytes across
    /// a window whose three counts were unchanged. So this is a hint worth acting on and never a
    /// certificate — which is why nothing below trusts a window for being free of it.
    /// </remarks>
    Collection,

    /// <summary>
    /// The work resumed on another thread, so the two readings are two threads' counters. Their
    /// difference is not a larger number or a smaller one; it is not a number at all.
    /// </summary>
    ThreadHop,
}

/// <summary>
/// One bracketed measurement of what the calling thread allocated, and whether anything happened
/// inside it that the reading has to answer for.
/// </summary>
internal readonly struct AllocationWindow
{
    private readonly int _thread;
    private readonly int _gen0;
    private readonly int _gen1;
    private readonly int _gen2;
    private readonly long _before;

    private AllocationWindow(int thread, int gen0, int gen1, int gen2, long before)
    {
        _thread = thread;
        _gen0 = gen0;
        _gen1 = gen1;
        _gen2 = gen2;
        _before = before;
    }

    public static AllocationWindow Open() => new(
        Environment.CurrentManagedThreadId,
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        GC.GetAllocatedBytesForCurrentThread());

    /// <summary>
    /// The bytes this window's thread allocated inside it, and what — if anything — is known to
    /// have disturbed the count. True means nothing was caught, which is weaker than nothing
    /// happening: see <see cref="Disturbance.Collection"/>.
    /// </summary>
    public bool TryClose(out long bytes, out Disturbance disturbance)
    {
        // The reading first, so nothing between the work and it is counted.
        bytes = GC.GetAllocatedBytesForCurrentThread() - _before;
        disturbance =
            Environment.CurrentManagedThreadId != _thread ? Disturbance.ThreadHop
            : GC.CollectionCount(0) != _gen0
                || GC.CollectionCount(1) != _gen1
                || GC.CollectionCount(2) != _gen2 ? Disturbance.Collection
            : Disturbance.None;
        return disturbance == Disturbance.None;
    }
}

/// <summary>
/// What a piece of work allocates, measured so that the number is the work's own and nobody else's.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> is the right counter for these gates: the
/// pipelines they measure run from end to end on the calling thread, so a per-thread count is this
/// pipeline's where the process-wide one drifts by megabytes under a parallel test run. It is exact
/// only for an <em>undisturbed</em> window, though, and two different things disturb it.
/// </para>
/// <para>
/// A collection inside the window retires the thread's allocation context and leaves the unconsumed
/// tail of it — up to about 8 KB — charged to the thread as though it had been allocated. A
/// collection the measuring thread triggers <em>itself</em> costs nothing, because its context is
/// exhausted at that moment: which is why these gates read the same number to the byte when they run
/// alone, and drift by thousands of bytes when the rest of the suite is allocating beside them, when
/// the collection is somebody else's and lands wherever it lands. Measured on this runtime at up to
/// 8 080 phantom bytes, which is the size of the flake this was written for and of more than one of
/// these gates' entire budgets. Under the full suite, 56 windows in 60 caught one.
/// </para>
/// <para>
/// A continuation that resumes on another thread is the other, and the two are not alike. The
/// pipelines here never suspend (<c>15-zero-allocation-execution.md</c> §3), so it does not happen
/// today; it is checked rather than assumed, because a source that did suspend would otherwise
/// quietly turn every number these gates assert on into a different number.
/// </para>
/// <para>
/// Neither is avoided by giving the work a thread of its own: a collection suspends every thread in
/// the process, so a dedicated thread catches them exactly as often. What the asymmetry between the
/// two does allow is this. A collection can only ever <em>add</em> to a reading, so the smallest of
/// several readings is the closest to the truth and is reached from above; and every gate here
/// asserts an upper bound, so a reading that is too high can fail a gate but can never pass one that
/// should have failed. The measurement is therefore the smallest of several windows, which needs no
/// window to be known clean — just as well, since <see cref="Disturbance.Collection"/> cannot be
/// ruled out by asking. A window that changed threads is the one thing thrown away outright: that
/// one can be wrong in either direction, and a reading that can be wrong downwards is a gate that
/// can pass when it should not.
/// </para>
/// </remarks>
internal static class AllocationProbe
{
    /// <summary>
    /// Windows a measurement takes. Enough that the smallest is the steady-state cost even when the
    /// machine never offers an undisturbed one.
    /// </summary>
    private const int DefaultSamples = 8;

    /// <summary>
    /// One measurement of <paramref name="work"/>, which the caller has already warmed, together
    /// with what the run answered. Stops at the first undisturbed window, and falls back to the
    /// smallest reading it saw when the machine never offers one.
    /// </summary>
    public static async Task<(long Bytes, T Result)> OnceAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        // Taken a thousand times over in the arena's leak gate, so it stops at the first window
        // nothing is known to have disturbed and minimises over the rest only when pressed.
        return await SampleAsync(work, DefaultSamples, stopAtClean: true);
    }

    /// <summary>
    /// What <paramref name="work"/> costs in its steady state: warmed, then measured over several
    /// windows, of which the smallest is the answer. The result comes back with it — the one the
    /// smallest window produced — so a caller can still check what the measured run answered.
    /// </summary>
    public static async Task<(long Bytes, T Result)> SteadyStateAsync<T>(
        Func<Task<T>> work, int samples = DefaultSamples)
    {
        ArgumentNullException.ThrowIfNull(work);

        // Warms the JIT, the arena's pools and every per-thread cache underneath — ArrayPool.Shared's
        // buckets among them — none of which the gate is about. One run is enough: the samples that
        // follow are on this same thread, so nothing thread-local goes cold between them.
        await work();
        // Every window is measured: an undisturbed one is not reliably undisturbed, so there is
        // nothing to stop early for, and one more sample only lowers the minimum towards the truth.
        return await SampleAsync(work, samples, stopAtClean: false);
    }

    private static async Task<(long Bytes, T Result)> SampleAsync<T>(
        Func<Task<T>> work, int samples, bool stopAtClean)
    {
        var best = long.MaxValue;
        var result = default(T)!;
        var hops = 0;

        for (var taken = 0; taken < samples; taken++)
        {
            var window = AllocationWindow.Open();
            var answer = await work();
            var undisturbed = window.TryClose(out var bytes, out var disturbance);

            if (disturbance == Disturbance.ThreadHop)
            {
                hops++;
                continue;
            }

            if (bytes < best)
            {
                best = bytes;
                result = answer;
            }

            if (undisturbed && stopAtClean)
            {
                break;
            }
        }

        if (best == long.MaxValue)
        {
            throw new Xunit.Sdk.XunitException(
                $"all {hops} measured windows resumed on another thread, so none of them holds a "
                + "number. These pipelines are not supposed to suspend at all "
                + "(15-zero-allocation-execution.md §3); nothing was measured.");
        }

        return (best, result);
    }

    /// <summary>
    /// The least-allocating of several runs of a measurement the caller shapes itself — one that
    /// marks windows of its own, say, or counts which of them allocated. <paramref name="allocated"/>
    /// says which number of the measurement to minimise on; the run that reported the smallest is
    /// the one least disturbed, and so the one that measured the pipeline rather than the machine.
    /// </summary>
    public static async Task<T> LeastAllocatingAsync<T>(
        Func<Task<T>> measure, Func<T, long> allocated, int samples = DefaultSamples)
    {
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(allocated);

        var best = default(T)!;
        var bestBytes = long.MaxValue;
        var hops = 0;

        for (var taken = 0; taken < samples; taken++)
        {
            var window = AllocationWindow.Open();
            var measured = await measure();
            _ = window.TryClose(out _, out var disturbance);
            if (disturbance == Disturbance.ThreadHop)
            {
                hops++;
                continue;
            }

            var bytes = allocated(measured);
            if (bytes < bestBytes)
            {
                bestBytes = bytes;
                best = measured;
            }
        }

        if (bestBytes == long.MaxValue)
        {
            throw new Xunit.Sdk.XunitException(
                $"all {hops} measured runs resumed on another thread, so none of them holds a "
                + "number. These pipelines are not supposed to suspend at all "
                + "(15-zero-allocation-execution.md §3); nothing was measured.");
        }

        return best;
    }
}
