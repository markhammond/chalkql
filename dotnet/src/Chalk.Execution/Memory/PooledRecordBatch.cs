using System.Buffers;
using Apache.Arrow;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Memory;

internal sealed class PooledRecordBatch : RecordBatch
{
    private readonly ExecutionArena _arena;
    private readonly int _rentalCount;

    private byte[][]? _rentals;
    private int _disposed;

    public PooledRecordBatch(
        ArrowSchema schema,
        IEnumerable<IArrowArray> arrays,
        int length,
        ExecutionArena arena,
        byte[][] rentals,
        int rentalCount)
        : base(
            schema,
            arrays,
            length)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(rentals);

        _arena = arena;
        _rentals = rentals;
        _rentalCount = rentalCount;
    }

    protected override void Dispose(
        bool disposing)
    {
        if (!disposing)
        {
            base.Dispose(false);
            return;
        }

        if (Interlocked.Exchange(
                ref _disposed,
                1) != 0)
        {
            return;
        }

        try
        {
            //
            // Arrow wrappers must be finished before their borrowed
            // backing arrays are returned to the arena.
            //
            base.Dispose(true);
        }
        finally
        {
            var rentals =
                Interlocked.Exchange(
                    ref _rentals,
                    null);

            if (rentals is not null)
            {

                for (var i = 0;
                     i < _rentalCount;
                     i++)
                {
                    var bytes =
                        rentals[i];

                    rentals[i] = null!;

                    if (bytes is { Length: > 0 })
                    {
                        _arena.Return(bytes);
                    }
                }

                ArrayPool<byte[]>.Shared.Return(
                    rentals,
                    clearArray: true);
            }
        }
    }
}