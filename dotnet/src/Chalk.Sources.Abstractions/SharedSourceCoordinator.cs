using System.Runtime.CompilerServices;

namespace Chalk.Sources;

internal static class SharedSourceCoordinator
{
    private static readonly ConditionalWeakTable<ISourceRuntime, State> States = new();
    private static long _nextOrder;

    internal static State For(ISourceRuntime source) =>
        States.GetValue(
            source,
            static _ => new State(
                Interlocked.Increment(ref _nextOrder)));

    internal sealed class State
    {
        private long _revision;

        internal State(long order)
        {
            Order = order;
        }

        internal long Order { get; }

        internal SemaphoreSlim Refresh { get; } = new(1, 1);

        internal long Revision =>
            Volatile.Read(ref _revision);

        internal long Changed() =>
            Interlocked.Increment(ref _revision);
    }
}