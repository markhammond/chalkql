using Apache.Arrow;
using Chalk.Client;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A host's own <see cref="ArenaPool"/> (D258, <c>docs/design/08-execution-arena.md</c> §2.1): an
/// execution may run on it instead of on the engine's, which is how one workload's warm memory is
/// kept apart from another's.
/// </summary>
public sealed class ArenaPoolTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    private const string Statement =
        "SELECT symbol, ts, \"close\" FROM bars WHERE volume > 5000 ORDER BY symbol, ts";

    /// <summary>
    /// A bare <c>null</c> would be ambiguous between the list and the dictionary overload, exactly as
    /// it is for the arena ones; the statement takes no parameters either way.
    /// </summary>
    private static readonly IReadOnlyList<object?> NoParameters = [];

    [Fact]
    public async Task An_execution_over_a_host_pool_rents_from_it_and_gives_the_arena_back()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(Statement);
        using var pool = new ArenaPool(new ArenaOptions(), capacity: 2, name: "reports");

        Assert.Equal(0, pool.IdleCount);
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(2 * (64L << 20), pool.WorstCaseBytes);

        var rows = await DrainAsync(engine, prepared, pool);

        Assert.True(rows > 0);

        // The arena came back: idle again, nothing outstanding, and warm.
        Assert.Equal(1, pool.IdleCount);
        Assert.True(
            pool.RetainedBytes > 0,
            "an arena that has served an execution should be holding something warm.");
        Assert.True(
            pool.RetainedBytes <= pool.WorstCaseBytes,
            $"the pool holds {pool.RetainedBytes} bytes against a worst case of {pool.WorstCaseBytes}.");
    }

    /// <summary>The pool's name travels into the execution's counters, and the engine's own has none.</summary>
    [Fact]
    public async Task The_pool_s_name_is_in_the_execution_s_statistics()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(Statement);
        using var pool = new ArenaPool(new ArenaOptions(), capacity: 1, name: "reports");

        Assert.Equal("reports", await PoolNameAsync(engine, prepared, pool));
        Assert.Null(await PoolNameAsync(engine, prepared, pool: null));

        // The engine's own pool is the one an execution without a pool runs on, and it is unnamed.
        Assert.Null(engine.Arenas.Name);
        Assert.Equal(engine.Arenas.Capacity * engine.Arenas.Options.RetainBytes, engine.Arenas.WorstCaseBytes);
    }

    /// <summary>
    /// Capacity is a count of arenas kept warm, not a limit on concurrency: the execution beyond it
    /// gets a transient arena of its own, which is disposed rather than pooled when it finishes.
    /// </summary>
    [Fact]
    public async Task A_second_execution_over_a_pool_of_capacity_one_gets_a_transient_arena()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(Statement);
        using var pool = new ArenaPool(new ArenaOptions(), capacity: 1, name: "one");

        var first = await engine.ExecuteAsync(prepared, NoParameters, pool);
        var second = await engine.ExecuteAsync(prepared, NoParameters, pool);
        try
        {
            var a = DrainAsync(first);
            var b = DrainAsync(second);
            var counts = await Task.WhenAll(a, b);

            Assert.Equal(counts[0], counts[1]);
            Assert.True(counts[0] > 0);
        }
        finally
        {
            await second.DisposeAsync();
            await first.DisposeAsync();
        }

        // Only the pooled one comes back; the transient one was disposed where it was used.
        Assert.Equal(1, pool.IdleCount);
    }

    /// <summary>
    /// The isolation claim: what a host's pool keeps warm is the host's pool's, and the engine's own
    /// is untouched by an execution that ran on the host's.
    /// </summary>
    [Fact]
    public async Task A_host_pool_keeps_its_own_memory_warm_and_leaves_the_engine_s_alone()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(Statement);
        using var pool = new ArenaPool(new ArenaOptions(), capacity: 1, name: "isolated");

        Assert.Equal(0, engine.Arenas.RetainedBytes);

        await DrainAsync(engine, prepared, pool);
        var afterFirst = pool.RetainedBytes;
        await DrainAsync(engine, prepared, pool);

        Assert.True(afterFirst > 0, "the first execution should have left the pool warm.");
        Assert.True(
            pool.RetainedBytes > 0,
            "a second execution of the same statement should leave the pool warm too.");

        // Nothing of this ran on the engine's pool, so the engine's pool holds nothing.
        Assert.Equal(0, engine.Arenas.IdleCount);
        Assert.Equal(0, engine.Arenas.RetainedBytes);
    }

    /// <summary>
    /// Disposing a pool while an execution is running on one of its arenas is deferred, not refused:
    /// the execution keeps its arena to the end and the pool releases it when it comes back, so a
    /// host can never pull memory out from under a running query.
    /// </summary>
    [Fact]
    public async Task A_pool_disposed_mid_execution_releases_the_arena_when_the_execution_ends()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(Statement);
        var pool = new ArenaPool(new ArenaOptions(), capacity: 1, name: "closing");

        await using var execution = await engine.ExecuteAsync(prepared, NoParameters, pool);
        long rows = 0;
        var disposed = false;
        await foreach (var batch in execution.Batches)
        {
            rows += batch.Length;
            batch.Dispose();
            if (!disposed)
            {
                // Mid-flight: the arena serving this execution is not taken away.
                pool.Dispose();
                disposed = true;
            }
        }

        Assert.True(rows > 0);
        Assert.Equal(0, pool.IdleCount);
        Assert.Equal(0, pool.RetainedBytes);
    }

    [Fact]
    public void A_pool_of_zero_capacity_still_keeps_one_arena()
    {
        using var pool = new ArenaPool(new ArenaOptions { RetainBytes = 1024 }, capacity: 0);

        Assert.Equal(1, pool.Capacity);
        Assert.Equal(1024, pool.WorstCaseBytes);
        Assert.Null(pool.Name);
    }

    private async Task<ChalkEngine> CreateEngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = CorpusFixture.Shared.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { BatchSize = 4096 },
        });

    private static async Task<long> DrainAsync(
        ChalkEngine engine, PreparedQuery prepared, ArenaPool? pool)
    {
        await using var execution = pool is null
            ? await engine.ExecuteAsync(prepared)
            : await engine.ExecuteAsync(prepared, NoParameters, pool);
        return await DrainAsync(execution);
    }

    private static async Task<string?> PoolNameAsync(
        ChalkEngine engine, PreparedQuery prepared, ArenaPool? pool)
    {
        await using var execution = pool is null
            ? await engine.ExecuteAsync(prepared)
            : await engine.ExecuteAsync(prepared, NoParameters, pool);
        await DrainAsync(execution);
        return execution.Stats.ArenaPoolName;
    }

    private static async Task<long> DrainAsync(QueryExecution execution)
    {
        long rows = 0;
        await foreach (var batch in execution.Batches)
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return rows;
    }
}
