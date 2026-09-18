using System.Buffers;
using System.Diagnostics;
using Apache.Arrow;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Memory;

/// <summary>
/// Temporary, stack-only owner of arena rentals that are being attached to one
/// PooledRecordBatch.
///
/// Until RelinquishToBatch() is called this collector owns every adopted rental
/// and will return them on Dispose(). After relinquishment the PooledRecordBatch
/// owns the bookkeeping array and the arena rentals.
/// </summary>
internal ref struct PooledBatchRentalCollector
{
    private readonly ExecutionArena _arena;

    private byte[][]? _rentals;
    private int _count;

    public PooledBatchRentalCollector(
        ExecutionArena arena,
        int capacityHint)
    {
        ArgumentNullException.ThrowIfNull(arena);

        _arena = arena;

        _rentals =
            ArrayPool<byte[]>.Shared.Rent(
                Math.Max(8, capacityHint));

        _count = 0;
    }

    public readonly int Count => _count;

    public readonly byte[][] Rentals =>
        _rentals
        ?? throw new InvalidOperationException(
            "The pooled rentals have already been transferred.");

    /// <summary>
    /// Copies source bytes once into their final arena rental.
    /// </summary>
    public ArrowBuffer CopyFrom(
        ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
            return ArrowBuffer.Empty;

        var bytes =
            _arena.Rent<byte>(
                content.Length);

        try
        {
            content.CopyTo(
                bytes.AsSpan(
                    0,
                    content.Length));

            Add(bytes);

            return new ArrowBuffer(
                bytes.AsMemory(
                    0,
                    content.Length));
        }
        catch
        {
            _arena.Return(bytes);
            throw;
        }
    }

    /// <summary>
    /// Transfers an existing outstanding ArenaBuffer rental into this batch.
    ///
    /// There is no arena accounting change: the rental remains outstanding
    /// until PooledRecordBatch.Dispose() returns it.
    /// </summary>
    public ArrowBuffer Adopt(
        ref ArenaBuffer buffer,
        int length)
    {
        if (length == 0)
            return ArrowBuffer.Empty;

        Debug.Assert(
            buffer.Bytes.Length >= length);

        var bytes =
            buffer.DetachOutstanding();

        try
        {
            Debug.Assert(
                bytes.Length >= length);

            Add(bytes);

            return new ArrowBuffer(
                bytes.AsMemory(
                    0,
                    length));
        }
        catch
        {
            //
            // ArenaBuffer no longer owns the rental.
            //
            _arena.Return(bytes);
            throw;
        }
    }

    /// <summary>
    /// Called only after PooledRecordBatch has successfully taken references
    /// to Rentals and Count.
    /// </summary>
    public void RelinquishToBatch()
    {
        _rentals = null;
        _count = 0;
    }

    /// <summary>
    /// Failure path: returns every adopted payload and the bookkeeping array.
    /// </summary>
    public void Dispose()
    {
        var rentals =
            _rentals;

        if (rentals is null)
            return;

        for (var i = 0;
             i < _count;
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

        _count = 0;
        _rentals = null;

        //
        // This is an array of references. Clear the complete rented array so
        // ArrayPool cannot keep old payload arrays reachable.
        //
        ArrayPool<byte[]>.Shared.Return(
            rentals,
            clearArray: true);
    }

    private void Add(
        byte[] bytes)
    {
        var rentals =
            _rentals
            ?? throw new InvalidOperationException(
                "The pooled rentals have already been transferred.");

        if (_count == rentals.Length)
        {
            var larger =
                ArrayPool<byte[]>.Shared.Rent(
                    checked(
                        rentals.Length * 2));

            rentals
                .AsSpan(0, _count)
                .CopyTo(larger);

            ArrayPool<byte[]>.Shared.Return(
                rentals,
                clearArray: true);

            _rentals =
                rentals =
                    larger;
        }

        rentals[_count++] =
            bytes;
    }
}