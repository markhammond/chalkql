using Akade.IndexedSet;
using Akade.IndexedSet.Concurrency;

namespace Chalk.Sources.Akade;

/// <summary>
/// Obtains the IndexedSet wrapped by ConcurrentIndexedSet through Akade's public Read API without
/// materialising the wrapped set's rows.
/// </summary>
/// <remarks>
/// The returned reference intentionally escapes Akade's reader-lock lifetime. CHALK002 is the public
/// contract for that choice: the host must prevent mutation while Chalk is reading the captured set.
/// This helper is therefore not a concurrency primitive; it is a reflection-free way to obtain the
/// authoritative IndexedSet instance.
/// </remarks>
internal static class ConcurrentIndexedSetAccess
{
    public static IndexedSet<T> CaptureForQuiescentRead<T>(
        ConcurrentIndexedSet<T> set)
    {
        ArgumentNullException.ThrowIfNull(set);

        var capture = new Capture<T>();

        _ = set.Read(
            new CaptureState<T>(capture),
            static (inner, state) =>
            {
                state.Target.Value = inner;

                // Read(...) materialises this returned sequence under the read lock. Returning no
                // rows means the capture itself pays no O(n) copy cost.
                return Array.Empty<T>();
            });

        return capture.Value
            ?? throw new InvalidOperationException(
                $"Akade {set.GetType().Assembly.GetName().Version}: "
                + "ConcurrentIndexedSet.Read did not expose its wrapped IndexedSet.");
    }

    private sealed class Capture<T>
    {
        public IndexedSet<T>? Value;
    }

    private readonly record struct CaptureState<T>(Capture<T> Target);
}
