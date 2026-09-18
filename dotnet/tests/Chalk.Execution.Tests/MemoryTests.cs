using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Execution.Memory;
using Chalk.Execution.Tests.Harness;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// The two allocators of §6.1: the arena's pooled buffers for the pipeline, with a high-water mark
/// reported through <see cref="ExecutionStats"/>, and GC arrays for output a host may keep.
/// </summary>
public sealed class MemoryTests
{
    [Fact]
    public void The_arena_allocator_rounds_to_a_size_class_and_returns_what_it_rents()
    {
        var stats = new ExecutionStats();
        using var arena = new ExecutionArena();
        arena.BeginExecution(stats);

        var owner = arena.Allocator.Allocate(1000);

        // The buffer is exactly what Arrow asked for; the arena books the size class behind it.
        Assert.Equal(1000, owner.Memory.Length);
        Assert.Equal(1024, arena.OutstandingBytes);
        Assert.Equal(1024, arena.PeakBytes);
        Assert.Equal(1024, stats.PeakPooledBytes);

        owner.Dispose();

        Assert.Equal(0, arena.OutstandingBytes);
        Assert.Equal(1024, arena.PeakBytes);
    }

    [Fact]
    public void The_arena_allocator_hands_out_zeroed_memory()
    {
        using var arena = new ExecutionArena();

        // Dirty an array of the size the next allocation will ask for, then give it back.
        var dirty = arena.Rent<byte>(1024);
        dirty.AsSpan().Fill(0xAB);
        arena.Return(dirty);

        using var owner = arena.Allocator.Allocate(1024);

        Assert.True(owner.Memory.Span.TrimStart((byte)0).IsEmpty);
    }

    [Fact]
    public void Disposing_a_pooled_buffer_twice_does_not_return_it_twice()
    {
        using var arena = new ExecutionArena();
        var owner = arena.Allocator.Allocate(64);

        owner.Dispose();
        owner.Dispose();

        Assert.Equal(0, arena.OutstandingBytes);
        Assert.Equal(256, arena.RetainedBytes);
    }

    [Fact]
    public void The_peak_is_the_high_water_mark_not_the_running_total()
    {
        var stats = new ExecutionStats();
        using var arena = new ExecutionArena();
        arena.BeginExecution(stats);

        for (var i = 0; i < 8; i++)
        {
            arena.Allocator.Allocate(4096).Dispose();
        }

        Assert.Equal(4096, arena.PeakBytes);
        Assert.Equal(4096, stats.PeakPooledBytes);
    }

    [Fact]
    public void Managed_memory_survives_its_owner_being_disposed()
    {
        var owner = ManagedMemoryAllocator.Instance.Allocate(32);
        owner.Memory.Span[0] = 7;

        owner.Dispose();

        Assert.Equal(7, owner.Memory.Span[0]);
    }

    [Fact]
    public async Task An_execution_reports_a_bounded_pooled_high_water_mark()
    {
        var table = TestData.Series(20_000);
        var source = TestData.Source(table);
        var plan = IrBuilder.Plan(IrBuilder.Read(table.Name, table.RowType()));
        var stats = new ExecutionStats();

        var batches = await Runner.RunAsync(plan, source, batchSize: 4096, stats: stats, pooledOutput: true);
        foreach (var batch in batches)
        {
            batch.Dispose();
        }

        Assert.True(stats.PeakPooledBytes > 0);

        // A streaming scan holds a couple of batches at a time, never the whole table.
        Assert.True(
            stats.PeakPooledBytes < 4L * 1024 * 1024,
            $"peak pooled bytes was {stats.PeakPooledBytes} for a streaming scan of 20 000 rows.");
    }

    [Fact]
    public void An_arrow_buffer_built_from_the_arena_round_trips_its_bytes()
    {
        using var arena = new ExecutionArena();
        var builder = new ArrowBuffer.Builder<long>(4);
        builder.Append(1L).Append(2L).Append(3L);

        var array = new Int64Array(builder.Build(arena.Allocator), ArrowBuffer.Empty, 3, 0, 0);
        try
        {
            Assert.Equal([1L, 2L, 3L], array.Values.ToArray());
            Assert.True(arena.OutstandingBytes > 0);
        }
        finally
        {
            array.Dispose();
        }

        Assert.Equal(0, arena.OutstandingBytes);
        Assert.Equal(Int64Type.Default.Name, array.Data.DataType.Name);
    }

    /// <summary>
    /// <see cref="ExecutionArena.CreateBuffer"/> is the arena's conduit from rented staging into
    /// allocator-owned memory (ADR 0012): the bytes arrive, and the batch's dispose gives them back.
    /// </summary>
    [Fact]
    public void A_buffer_created_from_staging_owns_arena_memory()
    {
        using var arena = new ExecutionArena();
        var staging = arena.Rent<long>(3);
        staging[0] = 7;
        staging[1] = 8;
        staging[2] = 9;

        var buffer = arena.CreateBuffer(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(staging.AsSpan(0, 3)));
        arena.Return(staging);

        var array = new Int64Array(buffer, ArrowBuffer.Empty, 3, 0, 0);
        Assert.Equal([7L, 8L, 9L], array.Values.ToArray());
        Assert.True(arena.OutstandingBytes > 0);

        array.Dispose();
        Assert.Equal(0, arena.OutstandingBytes);
    }
}
